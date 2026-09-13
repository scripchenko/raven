using System.Net;
using Google;
using Google.Apis.Requests;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.Tests;

public sealed class GmailFolderReliabilityTests
{
    [Theory]
    [InlineData("Inbox")]
    [InlineData("Starred")]
    [InlineData("Sent")]
    [InlineData("Spam")]
    [InlineData("Trash")]
    [InlineData("AllMail")]
    public async Task MessageBackedFolder_OneValidAndOneStaleGet404KeepsValidItem(string folder)
    {
        GmailApiSummaryData valid = Summary($"valid-{folder}", [folder.ToUpperInvariant()]);
        (int Index, GmailApiSummaryData Summary)?[] hydrated = await Task.WhenAll(
            GmailApiReadClient.LoadAvailableListedItemAsync(() => Task.FromResult((0, valid))),
            GmailApiReadClient.LoadAvailableListedItemAsync(() =>
                Task.FromException<(int, GmailApiSummaryData)>(ApiError(HttpStatusCode.NotFound))));

        GmailApiSummaryData remaining = Assert.Single(GmailApiReadClient.SelectAvailableSummaries(hydrated));

        Assert.Same(valid, remaining);
    }

    [Fact]
    public async Task Search_OneValidAndOneStaleGet404KeepsValidResult()
    {
        GmailApiSummaryData valid = Summary("search-valid", [GmailSystemFolders.Inbox]);
        (int Index, GmailApiSummaryData Summary)?[] hydrated = await Task.WhenAll(
            GmailApiReadClient.LoadAvailableListedItemAsync(() => Task.FromResult((0, valid))),
            GmailApiReadClient.LoadAvailableListedItemAsync(() =>
                Task.FromException<(int, GmailApiSummaryData)>(ApiError(HttpStatusCode.NotFound))));

        Assert.Equal("search-valid", Assert.Single(GmailApiReadClient.SelectAvailableSummaries(hydrated)).Id);
    }

    [Fact]
    public async Task Drafts_DeletedDraftGet404KeepsRemainingDraft()
    {
        GmailApiSummaryData valid = Summary("draft-valid", [GmailSystemFolders.Draft]);
        (int Index, GmailApiSummaryData Summary)?[] hydrated = await Task.WhenAll(
            GmailApiReadClient.LoadAvailableListedItemAsync(() => Task.FromResult((0, valid))),
            GmailApiReadClient.LoadAvailableListedItemAsync(() =>
                Task.FromException<(int, GmailApiSummaryData)>(ApiError(HttpStatusCode.NotFound))));

        GmailApiSummaryData remaining = Assert.Single(GmailApiReadClient.SelectAvailableSummaries(hydrated));

        Assert.True(GmailApiReadClient.IsCurrentDraftSummary(remaining));
    }

    [Fact]
    public void DraftSentBetweenListAndHydration_IsNotCurrentDraft()
    {
        GmailApiSummaryData sent = Summary("sent", [GmailSystemFolders.Sent]);

        Assert.False(GmailApiReadClient.IsCurrentDraftSummary(sent));
    }

    [Fact]
    public async Task RateLimit403_RetriesWithBoundedExponentialBackoff()
    {
        int attempts = 0;
        List<TimeSpan> delays = [];

        int result = await GmailApiReadClient.ExecuteWithRateLimitRetryAsync(
            () => ++attempts < 3
                ? Task.FromException<int>(ApiError(HttpStatusCode.Forbidden, "rateLimitExceeded"))
                : Task.FromResult(42),
            CancellationToken.None,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Unauthorized401_IsNotRetriedOrSwallowed()
    {
        await AssertNotRetriedAsync(ApiError(HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Permission403_IsNotRetriedOrSwallowed()
    {
        await AssertNotRetriedAsync(ApiError(HttpStatusCode.Forbidden, "forbidden"));
    }

    [Fact]
    public async Task RateLimit429_IsRetriedButNotSwallowedAfterBound()
    {
        int attempts = 0;

        await Assert.ThrowsAsync<GoogleApiException>(() => GmailApiReadClient.ExecuteWithRateLimitRetryAsync(
            () =>
            {
                attempts++;
                return Task.FromException<int>(ApiError(HttpStatusCode.TooManyRequests));
            },
            CancellationToken.None,
            (_, _) => Task.CompletedTask));

        Assert.Equal(GmailApiReadClient.MaximumRateLimitRetries + 1, attempts);
    }

    [Fact]
    public async Task NetworkFailure_IsNotRetriedOrSwallowed()
    {
        await AssertNotRetriedAsync(new HttpRequestException("synthetic"));
    }

    [Fact]
    public async Task Server5xx_IsNotRetriedOrSwallowed()
    {
        await AssertNotRetriedAsync(ApiError(HttpStatusCode.ServiceUnavailable, "backendError"));
    }

    [Fact]
    public void NonNetworkListFailure_UsesNeutralMessage()
    {
        MailReadException mapped = GmailApiReadClient.MapListException(new InvalidOperationException("synthetic"));

        Assert.Equal("Не удалось загрузить почту. Попробуйте ещё раз.", mapped.UserMessage);
        Assert.DoesNotContain("подключение", mapped.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActualNetworkListFailure_UsesNetworkMessage()
    {
        MailReadException mapped = GmailApiReadClient.MapListException(new HttpRequestException("synthetic"));

        Assert.Contains("подключение к сети", mapped.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RateLimitFailure_UsesSpecificNeutralMessage()
    {
        MailReadException mapped = GmailApiReadClient.MapListException(
            ApiError(HttpStatusCode.Forbidden, "rateLimitExceeded"));

        Assert.Contains("ограничил частоту запросов", mapped.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("подключение", mapped.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertNotRetriedAsync(Exception expected)
    {
        int attempts = 0;

        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            GmailApiReadClient.ExecuteWithRateLimitRetryAsync<int>(
                () =>
                {
                    attempts++;
                    return Task.FromException<int>(expected);
                },
                CancellationToken.None,
                (_, _) => Task.CompletedTask));

        Assert.Same(expected, actual);
        Assert.Equal(1, attempts);
    }

    private static GmailApiSummaryData Summary(string id, IReadOnlyList<string> labels) =>
        new(id, "synthetic", "sender@example.test", 1, "synthetic", labels);

    private static GoogleApiException ApiError(HttpStatusCode status, string? reason = null) =>
        new("Gmail", "synthetic")
        {
            HttpStatusCode = status,
            Error = reason is null
                ? null
                : new RequestError { Errors = [new SingleError { Reason = reason }] }
        };
}
