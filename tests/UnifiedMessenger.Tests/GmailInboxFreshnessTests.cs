using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailInboxFreshnessTests
{
    [Fact]
    public async Task NewMailForActiveInbox_RefreshesFirstPage()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        provider.Enqueue(account.Id, Page("old", 100));
        provider.Enqueue(account.Id, Page("new", 101));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: true);
        await viewModel.GetCurrentInboxRefreshTask(account.Id);

        Assert.Equal(2, provider.GetPageCallCount(account.Id));
        Assert.Equal("new", viewModel.Messages[0].MessageKey);
        Assert.Equal("1–1 из 101", viewModel.PageRangeText);
        Assert.False(viewModel.IsInboxStale(account.Id));
    }

    [Fact]
    public async Task NewMailForInactiveInbox_MarksOnlyThatAccountStaleWithoutRequest()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        provider.Enqueue(account.Id, Page("old"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: false);

        Assert.True(viewModel.IsInboxStale(account.Id));
        Assert.Equal(1, provider.GetPageCallCount(account.Id));
    }

    [Fact]
    public async Task OpeningStaleAccount_AutomaticallyRefreshesInbox()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        provider.Enqueue(account.Id, Page("old"));
        provider.Enqueue(account.Id, Page("new"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);
        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: false);
        await viewModel.ActivateAsync(null);

        await viewModel.ActivateAsync(account);

        Assert.Equal(2, provider.GetPageCallCount(account.Id));
        Assert.Equal("new", viewModel.Messages[0].MessageKey);
        Assert.False(viewModel.IsInboxStale(account.Id));
    }

    [Fact]
    public async Task FailedAutomaticRefresh_PreservesCachedPageAndStaleState()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        provider.Enqueue(account.Id, Page("cached"));
        provider.EnqueueFailure(
            account.Id,
            new MailReadException(MailReadFailureKind.ConnectionFailed, "Temporary failure"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: true);
        await viewModel.GetCurrentInboxRefreshTask(account.Id);

        Assert.True(viewModel.IsInboxStale(account.Id));
        Assert.Equal("cached", viewModel.Messages[0].MessageKey);
        Assert.Equal("Temporary failure", viewModel.ListErrorMessage);
    }

    [Fact]
    public async Task BurstWhileRefreshRuns_CoalescesToOneFollowUpRefresh()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MailPage<MailMessageSummary>> release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Enqueue(account.Id, Page("old"));
        provider.Enqueue(account.Id, async cancellationToken =>
        {
            started.TrySetResult();
            return await release.Task.WaitAsync(cancellationToken);
        });
        provider.Enqueue(account.Id, Page("latest"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: true);
        Task refresh = viewModel.GetCurrentInboxRefreshTask(account.Id);
        await started.Task;
        for (int index = 0; index < 10; index++)
        {
            viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: true);
        }

        Assert.Equal(2, provider.GetPageCallCount(account.Id));
        release.SetResult(Page("intermediate"));
        await refresh;

        Assert.Equal(3, provider.GetPageCallCount(account.Id));
        Assert.Equal("latest", viewModel.Messages[0].MessageKey);
        Assert.False(viewModel.IsInboxStale(account.Id));
    }

    [Fact]
    public async Task SignalForAccountA_DoesNotChangeAccountB()
    {
        TestReadProvider provider = new();
        MailAccount first = Account(1);
        MailAccount second = Account(2);
        provider.Enqueue(first.Id, Page("first"));
        provider.Enqueue(second.Id, Page("second"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(first);
        await viewModel.ActivateAsync(second);

        viewModel.OnNewMailDetected(first.Id, isAccountActivelyViewed: false);

        Assert.True(viewModel.IsInboxStale(first.Id));
        Assert.False(viewModel.IsInboxStale(second.Id));
        Assert.Equal(1, provider.GetPageCallCount(first.Id));
        Assert.Equal(1, provider.GetPageCallCount(second.Id));
        Assert.Equal("second", viewModel.Messages[0].MessageKey);
    }

    [Fact]
    public async Task FreshnessRequiredForCachedActiveInbox_ForcesFirstPageRefresh()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        provider.Enqueue(account.Id, Page("cached"));
        provider.Enqueue(account.Id, Page("fresh"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.RequireFreshInbox(account.Id);
        await viewModel.GetCurrentInboxRefreshTask(account.Id);

        Assert.Equal(2, provider.GetPageCallCount(account.Id));
        Assert.Equal("fresh", viewModel.Messages[0].MessageKey);
        Assert.False(viewModel.IsInboxStale(account.Id));
    }

    [Fact]
    public async Task AccountSwitchDuringRefresh_DoesNotApplyOldAccountResult()
    {
        TestReadProvider provider = new();
        MailAccount first = Account(1);
        MailAccount second = Account(2);
        TaskCompletionSource firstRefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MailPage<MailMessageSummary>> releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Enqueue(first.Id, Page("first-old"));
        provider.Enqueue(first.Id, async cancellationToken =>
        {
            firstRefreshStarted.TrySetResult();
            return await releaseFirst.Task.WaitAsync(cancellationToken);
        });
        provider.Enqueue(second.Id, Page("second"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(first);

        viewModel.OnNewMailDetected(first.Id, isAccountActivelyViewed: true);
        Task firstRefresh = viewModel.GetCurrentInboxRefreshTask(first.Id);
        await firstRefreshStarted.Task;
        await viewModel.ActivateAsync(second);
        releaseFirst.TrySetResult(Page("first-late"));
        await firstRefresh;

        Assert.Equal(second.Id, viewModel.ActiveAccount?.Id);
        Assert.Equal("second", viewModel.Messages[0].MessageKey);
        Assert.True(viewModel.IsInboxStale(first.Id));
        Assert.False(viewModel.IsInboxStale(second.Id));
    }

    [Fact]
    public async Task ActiveRefresh_PreservesSelectedMessageMissingFromFreshPage()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        provider.Enqueue(account.Id, Page("selected"));
        provider.Enqueue(account.Id, Page("new"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);
        MailMessageSummary selected = Assert.Single(viewModel.Messages);
        viewModel.SelectedMessageSummary = selected;
        await viewModel.CurrentMessageLoadTask;

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: true);
        await viewModel.GetCurrentInboxRefreshTask(account.Id);

        Assert.Equal("selected", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Equal("selected", viewModel.SelectedMessageContent?.MessageKey);
        Assert.Contains(viewModel.Messages, message => message.MessageKey == "new");
        Assert.Contains(viewModel.Messages, message => message.MessageKey == "selected");
    }

    [Fact]
    public async Task AuthenticationFailureDuringAutomaticRefresh_PreservesG1StateAndStaleness()
    {
        TestReadProvider provider = new();
        MailAccount account = Account(1);
        provider.Enqueue(account.Id, Page("cached"));
        provider.EnqueueFailure(
            account.Id,
            new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google."));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: true);
        await viewModel.GetCurrentInboxRefreshTask(account.Id);

        Assert.True(viewModel.IsInboxStale(account.Id));
        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.Equal("cached", viewModel.Messages[0].MessageKey);
    }

    private static MailInboxViewModel CreateViewModel(IMailReadProvider provider) =>
        new(new SingleReadProviderFactory(provider));

    private static MailAccount Account(int number) => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Gmail,
        DisplayName = $"Gmail {number}",
        EmailAddress = $"account{number}@gmail.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        IsEnabled = true
    };

    private static MailPage<MailMessageSummary> Page(string key, long? totalCount = null) =>
        new([Summary(key)], null, totalCount);

    private static MailMessageSummary Summary(string key) =>
        new(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            true);

    private static MailMessageContent Content(string key) =>
        new(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            "recipient@example.test",
            DateTimeOffset.UtcNow,
            MailMessageBodyKind.PlainText,
            "Body",
            [],
            true,
            false);

    private sealed class SingleReadProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class TestReadProvider : IMailReadProvider
    {
        private readonly Dictionary<Guid, Queue<Func<CancellationToken, Task<MailPage<MailMessageSummary>>>>> _responses = [];
        private readonly Dictionary<Guid, int> _pageCalls = [];

        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public void Enqueue(Guid accountId, MailPage<MailMessageSummary> page) =>
            Enqueue(accountId, _ => Task.FromResult(page));

        public void Enqueue(
            Guid accountId,
            Func<CancellationToken, Task<MailPage<MailMessageSummary>>> response)
        {
            if (!_responses.TryGetValue(accountId, out Queue<Func<CancellationToken, Task<MailPage<MailMessageSummary>>>>? queue))
            {
                queue = new Queue<Func<CancellationToken, Task<MailPage<MailMessageSummary>>>>();
                _responses.Add(accountId, queue);
            }

            queue.Enqueue(response);
        }

        public void EnqueueFailure(Guid accountId, MailReadException exception) =>
            Enqueue(accountId, _ => Task.FromException<MailPage<MailMessageSummary>>(exception));

        public int GetPageCallCount(Guid accountId) =>
            _pageCalls.GetValueOrDefault(accountId);

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            _pageCalls[account.Id] = GetPageCallCount(account.Id) + 1;
            if (!_responses.TryGetValue(account.Id, out Queue<Func<CancellationToken, Task<MailPage<MailMessageSummary>>>>? queue)
                || !queue.TryDequeue(out Func<CancellationToken, Task<MailPage<MailMessageSummary>>>? response))
            {
                throw new InvalidOperationException("A deterministic page response is required.");
            }

            return response(cancellationToken);
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Content(messageKey));
    }
}
