using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;
using GmailHistory = Google.Apis.Gmail.v1.Data.History;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace UnifiedMessenger.Tests;

public sealed class GmailHistoryPollingTests
{
    [Fact]
    public async Task FirstPoll_BaselinesWithoutNotification()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(9, 100));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();

        Assert.Empty(events);
        Assert.Equal(9, account.InboxUnreadCount);
        Assert.Equal([null], provider.Cursors(account.Id));
    }

    [Fact]
    public async Task NewInboxMessage_IsDetectedExactlyOnce()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(1, 100));
        provider.Enqueue(account.Id, Result(2, 104, "m1"));
        provider.Enqueue(account.Id, Result(2, 104));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(1, Assert.Single(events).NewMessageCount);
        Assert.Equal([null, 100UL, 104UL], provider.Cursors(account.Id));
    }

    [Fact]
    public async Task DuplicateHistoryRecords_AreDeduplicatedByMessageId()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(0, 10));
        provider.Enqueue(account.Id, Result(1, 11, "same", "same", "same"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Theory]
    [InlineData("archive backfill")]
    [InlineData("trash backfill")]
    [InlineData("star")]
    [InlineData("unstar")]
    [InlineData("add label")]
    [InlineData("remove label")]
    [InlineData("mark read")]
    [InlineData("mark unread")]
    [InlineData("restore old message to inbox")]
    [InlineData("draft autosave")]
    [InlineData("sent message")]
    public async Task NonMessageAddedMailboxMutation_DoesNotDetectNewMail(string scenario)
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(4, 20));
        provider.Enqueue(account.Id, Result(4, 21));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Empty(events);
        Assert.False(string.IsNullOrWhiteSpace(scenario));
    }

    [Fact]
    public async Task BurstOverOneHundred_IsNotTruncatedAndRemainsOneAccountEvent()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(0, 30));
        provider.Enqueue(account.Id, Result(150, 200, Enumerable.Range(1, 150).Select(index => $"m{index}").ToArray()));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(150, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task ExactUnreadCount_UpdatesEvenWithoutNewMessageCandidate()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(8, 40));
        provider.Enqueue(account.Id, Result(3, 41));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(3, account.InboxUnreadCount);
        Assert.Empty(events);
    }

    [Fact]
    public async Task TransientFailure_RetainsCursorForRetry()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(0, 50));
        provider.EnqueueFailure(account.Id);
        provider.Enqueue(account.Id, Result(1, 51, "after-retry"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([null, 50UL, 50UL], provider.Cursors(account.Id));
        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task AuthorizationFailure_RetainsCursorUntilReauthenticationSucceeds()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(0, 55));
        provider.EnqueueFailure(account.Id, MailReadFailureKind.ReauthorizationRequired);
        provider.Enqueue(account.Id, Result(1, 56, "after-reauth"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([null, 55UL, 55UL], provider.Cursors(account.Id));
        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task StaleCursor_RebaselinesWithoutFlood_ThenDetectsNewMail()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(2, 60));
        provider.Enqueue(account.Id, Result(12, 90, "ignored-existing") with { IsRebaseline = true });
        provider.Enqueue(account.Id, Result(13, 91, "new-after-rebaseline"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Assert.Empty(events);
        Assert.Equal(12, account.InboxUnreadCount);
        await monitor.PollOnceAsync();

        Assert.Equal([null, 60UL, 90UL], provider.Cursors(account.Id));
        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task RegressiveCursor_IsRejectedAndPreviousCursorIsRetried()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(0, 100));
        provider.Enqueue(account.Id, Result(1, 99, "must-not-commit"));
        provider.Enqueue(account.Id, Result(1, 101, "valid"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([null, 100UL, 100UL], provider.Cursors(account.Id));
        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task CandidateWithoutCursorAdvancement_IsRejectedAndRetried()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(0, 110));
        provider.Enqueue(account.Id, Result(1, 110, "must-not-repeat"));
        provider.Enqueue(account.Id, Result(1, 111, "valid"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([null, 110UL, 110UL], provider.Cursors(account.Id));
        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task AccountsHaveIndependentHistoryCursors()
    {
        MailAccount accountA = Account();
        MailAccount accountB = Account();
        HistoryProvider provider = new();
        provider.Enqueue(accountA.Id, Result(1, 10));
        provider.Enqueue(accountB.Id, Result(2, 70));
        provider.Enqueue(accountA.Id, Result(2, 11, "a-new"));
        provider.Enqueue(accountB.Id, Result(3, 75, "b-new"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([accountA, accountB], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal([null, 10UL], provider.Cursors(accountA.Id));
        Assert.Equal([null, 70UL], provider.Cursors(accountB.Id));
        Assert.Collection(
            events.OrderBy(item => item.MailAccountId),
            first => Assert.Equal(1, first.NewMessageCount),
            second => Assert.Equal(1, second.NewMessageCount));
    }

    [Fact]
    public async Task DisableThenReenable_DropsCursorAndCreatesFreshBaseline()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(1, 10));
        provider.Enqueue(account.Id, Result(8, 80, "mail-while-disabled"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        account.IsEnabled = false;
        await monitor.PollOnceAsync();
        account.IsEnabled = true;
        await monitor.PollOnceAsync();

        Assert.Equal([null, null], provider.Cursors(account.Id));
        Assert.Empty(events);
        Assert.Equal(8, account.InboxUnreadCount);
    }

    [Fact]
    public async Task DisableDuringInFlightPoll_DiscardsLateCursorAndNotification()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.EnqueueAsync(account.Id, async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return Result(7, 70, "late-message");
        });
        provider.Enqueue(account.Id, Result(7, 80));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        Task inFlightPoll = monitor.PollOnceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        account.IsEnabled = false;
        release.TrySetResult();
        await inFlightPoll;
        account.IsEnabled = true;
        await monitor.PollOnceAsync();

        Assert.Equal([null, null], provider.Cursors(account.Id));
        Assert.Empty(events);
    }

    [Fact]
    public async Task NewMonitorAfterRestart_CreatesFreshRuntimeBaseline()
    {
        MailAccount account = Account();
        HistoryProvider provider = new();
        provider.Enqueue(account.Id, Result(1, 10));
        using (MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider))
        {
            await monitor.PollOnceAsync();
        }

        provider.Enqueue(account.Id, Result(5, 90, "mail-while-stopped"));
        using MailBackgroundPollingMonitor restarted = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(restarted);
        await restarted.PollOnceAsync();

        Assert.Equal([null, null], provider.Cursors(account.Id));
        Assert.Empty(events);
    }

    [Fact]
    public async Task HistoryPagination_ProcessesEveryPageAndDeduplicatesCandidates()
    {
        List<string?> requestedTokens = [];
        GmailHistory[] firstPageHistory = Enumerable.Range(1, 75)
            .Select(index => Added($"m{index}"))
            .Append(Added("m75"))
            .ToArray();
        GmailHistory[] secondPageHistory = Enumerable.Range(76, 75)
            .Select(index => Added($"m{index}"))
            .Prepend(Added("m75"))
            .ToArray();
        ListHistoryResponse first = Page(
            149,
            "page-2",
            firstPageHistory);
        ListHistoryResponse second = Page(
            150,
            null,
            secondPageHistory);

        (ulong cursor, IReadOnlyCollection<string> messageIds) = await GmailApiReadClient.ReadHistoryPagesAsync(
            100,
            (pageToken, _) =>
            {
                requestedTokens.Add(pageToken);
                return Task.FromResult(pageToken is null ? first : second);
            });

        Assert.Equal([null, "page-2"], requestedTokens);
        Assert.Equal(150UL, cursor);
        Assert.Equal(150, messageIds.Count);
        Assert.Contains("m1", messageIds);
        Assert.Contains("m150", messageIds);
    }

    [Fact]
    public async Task HistoryPagination_PageTwoFailureDoesNotReturnPartialResult()
    {
        int calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => GmailApiReadClient.ReadHistoryPagesAsync(
            100,
            (_, _) => ++calls == 1
                ? Task.FromResult(Page(120, "page-2", Added("m1")))
                : Task.FromException<ListHistoryResponse>(new HttpRequestException("synthetic"))));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task HistoryPayload_IgnoresGenericMessagesAndAllMutationCollections()
    {
        GmailHistory mutation = new()
        {
            Messages = [new GmailMessage { Id = "generic" }],
            MessagesDeleted = [new HistoryMessageDeleted { Message = new GmailMessage { Id = "deleted" } }],
            LabelsAdded = [new HistoryLabelAdded { Message = new GmailMessage { Id = "label-added" } }],
            LabelsRemoved = [new HistoryLabelRemoved { Message = new GmailMessage { Id = "label-removed" } }]
        };

        (_, IReadOnlyCollection<string> messageIds) = await GmailApiReadClient.ReadHistoryPagesAsync(
            10,
            (_, _) => Task.FromResult(Page(11, null, mutation)));

        Assert.Empty(messageIds);
    }

    [Fact]
    public void HistoryRequest_UsesOnlyMessageAddedForInboxWithMaximumPageSize()
    {
        using GmailService service = new();
        UsersResource.HistoryResource.ListRequest request = service.Users.History.List("me");

        GmailApiReadClient.ConfigureHistoryRequest(request, 123, "next");

        Assert.Equal(123UL, request.StartHistoryId);
        Assert.Equal(UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded, request.HistoryTypes);
        Assert.Equal(GmailSystemFolders.Inbox, request.LabelId);
        Assert.Equal(500, request.MaxResults);
        Assert.Equal("next", request.PageToken);
    }

    [Fact]
    public void StaleCursor_IsOnlyClassifiedFromGmailNotFoundStatus()
    {
        GoogleApiException stale = new("Gmail", "synthetic") { HttpStatusCode = HttpStatusCode.NotFound };
        GoogleApiException transient = new("Gmail", "synthetic") { HttpStatusCode = HttpStatusCode.InternalServerError };

        Assert.True(GmailApiReadClient.IsStaleHistoryCursor(stale));
        Assert.False(GmailApiReadClient.IsStaleHistoryCursor(transient));
    }

    private static GmailHistory Added(string id) =>
        new()
        {
            MessagesAdded = [new HistoryMessageAdded { Message = new GmailMessage { Id = id } }]
        };

    private static ListHistoryResponse Page(ulong historyId, string? nextPageToken, params GmailHistory[] history) =>
        new()
        {
            HistoryId = historyId,
            NextPageToken = nextPageToken,
            History = history
        };

    private static MailHistoryPollResult Result(int unreadCount, ulong cursor, params string[] messageIds) =>
        new(unreadCount, cursor, messageIds);

    private static MailAccount Account() =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = MailProviderType.Gmail,
            EmailAddress = "mail@example.test",
            CredentialKey = "credential",
            IsEnabled = true
        };

    private static MailBackgroundPollingMonitor CreateMonitor(
        IReadOnlyList<MailAccount> accounts,
        HistoryProvider provider) =>
        new(
            new TestSettingsStore(new AppSettings { MailAccounts = accounts.ToList() }),
            new TestProviderFactory(provider),
            new ImmediateDispatcher(),
            TimeProvider.System);

    private static List<MailNewMessageDetectedEventArgs> Subscribe(MailBackgroundPollingMonitor monitor)
    {
        List<MailNewMessageDetectedEventArgs> events = [];
        monitor.MailNewMessageDetected += (_, eventArgs) => events.Add(eventArgs);
        return events;
    }

    private sealed class HistoryProvider : IMailReadProvider, IMailNewMessageHistoryProvider
    {
        private readonly Dictionary<Guid, Queue<Func<Task<MailHistoryPollResult>>>> _results = [];
        private readonly Dictionary<Guid, List<ulong?>> _cursors = [];

        public bool Supports(MailProviderType providerType) => providerType == MailProviderType.Gmail;

        public void Enqueue(Guid accountId, MailHistoryPollResult result) =>
            Queue(accountId).Enqueue(() => Task.FromResult(result));

        public void EnqueueAsync(Guid accountId, Func<Task<MailHistoryPollResult>> resultFactory) =>
            Queue(accountId).Enqueue(resultFactory);

        public void EnqueueFailure(
            Guid accountId,
            MailReadFailureKind failureKind = MailReadFailureKind.ConnectionFailed) =>
            Queue(accountId).Enqueue(() => Task.FromException<MailHistoryPollResult>(new MailReadException(
                failureKind,
                "Synthetic history failure.")));

        public IReadOnlyList<ulong?> Cursors(Guid accountId) =>
            _cursors.GetValueOrDefault(accountId) ?? [];

        public Task<MailHistoryPollResult> PollHistoryAsync(
            MailAccount account,
            ulong? historyCursor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_cursors.TryGetValue(account.Id, out List<ulong?>? cursors))
            {
                cursors = [];
                _cursors.Add(account.Id, cursors);
            }

            cursors.Add(historyCursor);
            return Queue(account.Id).Dequeue().Invoke();
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromException<MailPage<MailMessageSummary>>(new NotSupportedException());

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromException<MailMessageContent>(new NotSupportedException());

        private Queue<Func<Task<MailHistoryPollResult>>> Queue(Guid accountId)
        {
            if (!_results.TryGetValue(accountId, out Queue<Func<Task<MailHistoryPollResult>>>? queue))
            {
                queue = new Queue<Func<Task<MailHistoryPollResult>>>();
                _results.Add(accountId, queue);
            }

            return queue;
        }
    }

    private sealed class TestProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class TestSettingsStore(AppSettings current) : IApplicationSettingsStore
    {
        public AppSettings Current { get; private set; } = current;
        public bool IsInitialized => true;
        public void Initialize(AppSettings settings) => Current = settings;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }
}
