using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.Tests;

public sealed class MailReadDwellTests
{
    [Fact]
    public async Task UnreadMessage_MutatesExactlyOnceOnlyAfterCentralizedDelayCompletes()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: true));
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);

        viewModel.SetDetailHostActive(true);
        await ActivateAndOpenAsync(viewModel, Account(), "message");

        Assert.Equal(MailInboxViewModel.MailReadDwellDelay, Assert.Single(scheduler.Requests).Delay);
        Assert.Empty(provider.Mutations);

        Task dwell = viewModel.CurrentReadDwellTask;
        scheduler.Complete(0);
        await dwell;

        MailMutation mutation = Assert.Single(provider.Mutations);
        Assert.True(mutation.IsRead);
        Assert.False(viewModel.SelectedMessageSummary!.IsUnread);
        Assert.False(viewModel.SelectedMessageContent!.IsUnread);
    }

    [Fact]
    public async Task AutomaticRead_PreservesPrintReadinessBodyAndMessageIdentity()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: true));
        provider.BodyKind = MailMessageBodyKind.SanitizedHtml;
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);

        viewModel.SetDetailHostActive(true);
        await ActivateAndOpenAsync(viewModel, Account(), "message");
        viewModel.SetPrintAvailable(isAvailable: true);
        Assert.True(viewModel.CanPrintMessage);

        Task dwell = viewModel.CurrentReadDwellTask;
        scheduler.Complete(0);
        await dwell;

        Assert.True(viewModel.CanPrintMessage);
        Assert.Equal("message", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Equal("message", viewModel.SelectedMessageContent?.MessageKey);
        Assert.Equal(1, provider.BodyFetchCount);
    }

    [Fact]
    public async Task BackBeforeDwell_CancelsWithoutMutation()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: true));
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);
        await ActivateAndOpenAsync(viewModel, Account(), "message");
        viewModel.SetDetailHostActive(true);
        Task dwell = viewModel.CurrentReadDwellTask;

        viewModel.BackToMessageListCommand.Execute(null);
        await dwell;

        Assert.Empty(provider.Mutations);
        Assert.True(scheduler.Requests[0].IsCanceled);
    }

    [Fact]
    public async Task SelectingMessageB_CancelsMessageAAndStartsIndependentDwell()
    {
        DwellProvider provider = ProviderWithInbox(
            Summary("a", unread: true),
            Summary("b", unread: true));
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);
        await ActivateAndOpenAsync(viewModel, Account(), "a");
        viewModel.SetDetailHostActive(true);
        Task firstDwell = viewModel.CurrentReadDwellTask;

        viewModel.OpenMessageCommand.Execute(viewModel.Messages.Single(message => message.MessageKey == "b"));
        await viewModel.CurrentMessageLoadTask;
        Task secondDwell = viewModel.CurrentReadDwellTask;
        await firstDwell;

        Assert.Equal(2, scheduler.Requests.Count);
        Assert.True(scheduler.Requests[0].IsCanceled);
        scheduler.Complete(1);
        await secondDwell;

        MailMutation mutation = Assert.Single(provider.Mutations);
        Assert.Equal("b", mutation.MessageKey);
    }

    [Theory]
    [InlineData(DwellCancellationKind.Folder)]
    [InlineData(DwellCancellationKind.Account)]
    [InlineData(DwellCancellationKind.WebService)]
    [InlineData(DwellCancellationKind.Compose)]
    [InlineData(DwellCancellationKind.WindowInactive)]
    public async Task LeavingActiveDetail_CancelsPendingDwell(DwellCancellationKind cancellationKind)
    {
        MailFolder inbox = MailFolderCatalog.Inbox();
        MailFolder sent = MailFolderCatalog.Create(MailFolderKind.Sent, "SENT");
        DwellProvider provider = new([inbox, sent]);
        provider.SetPage(inbox, [Summary("message", unread: true)]);
        provider.SetPage(sent, [Summary("sent", unread: true)]);
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);
        MailAccount account = Account();
        await ActivateAndOpenAsync(viewModel, account, "message");
        viewModel.SetDetailHostActive(true);
        Task dwell = viewModel.CurrentReadDwellTask;

        switch (cancellationKind)
        {
            case DwellCancellationKind.Folder:
                viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Sent);
                break;
            case DwellCancellationKind.Account:
                await viewModel.ActivateAsync(Account());
                break;
            case DwellCancellationKind.WebService:
                await viewModel.ActivateAsync(null);
                break;
            case DwellCancellationKind.Compose:
                viewModel.Compose.NewMessageCommand.Execute(null);
                break;
            case DwellCancellationKind.WindowInactive:
                viewModel.SetDetailHostActive(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cancellationKind));
        }

        await dwell;
        Assert.Empty(provider.Mutations);
        Assert.True(scheduler.Requests[0].IsCanceled);
    }

    [Fact]
    public async Task AlreadyReadMessage_DoesNotStartTimerOrMutation()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: false));
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);

        await ActivateAndOpenAsync(viewModel, Account(), "message");
        viewModel.SetDetailHostActive(true);

        Assert.Empty(scheduler.Requests);
        Assert.Empty(provider.Mutations);
        Assert.Equal("Mark message as unread", viewModel.ReadStateActionText);
    }

    [Fact]
    public async Task Dispose_CancelsPendingDwell()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: true));
        ManualDwellScheduler scheduler = new();
        MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);
        await ActivateAndOpenAsync(viewModel, Account(), "message");
        viewModel.SetDetailHostActive(true);
        Task dwell = viewModel.CurrentReadDwellTask;

        viewModel.Dispose();
        await dwell;

        Assert.Empty(provider.Mutations);
        Assert.True(scheduler.Requests[0].IsCanceled);
    }

    [Fact]
    public async Task ManualUnreadAfterAutomaticRead_SuppressesSameDetailUntilReopened()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: true));
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);
        await ActivateAndOpenAsync(viewModel, Account(), "message");
        viewModel.SetDetailHostActive(true);

        Task automaticRead = viewModel.CurrentReadDwellTask;
        scheduler.Complete(0);
        await automaticRead;
        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.Equal([true, false], provider.Mutations.Select(mutation => mutation.IsRead));
        Assert.True(viewModel.SelectedMessageSummary!.IsUnread);
        Assert.Single(scheduler.Requests);

        viewModel.SetDetailHostActive(false);
        viewModel.SetDetailHostActive(true);
        Assert.Single(scheduler.Requests);

        viewModel.BackToMessageListCommand.Execute(null);
        viewModel.OpenMessageCommand.Execute(viewModel.SelectedMessageSummary);
        Assert.Equal(2, scheduler.Requests.Count);
        Task reopenedDwell = viewModel.CurrentReadDwellTask;
        scheduler.Complete(1);
        await reopenedDwell;

        Assert.Equal([true, false, true], provider.Mutations.Select(mutation => mutation.IsRead));
    }

    [Fact]
    public async Task ManualReadBeforeDelay_CancelsTimerAndDoesNotDuplicateMutation()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: true));
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);
        await ActivateAndOpenAsync(viewModel, Account(), "message");
        viewModel.SetDetailHostActive(true);
        Task dwell = viewModel.CurrentReadDwellTask;

        await viewModel.SetReadStateCommand.ExecuteAsync(null);
        await dwell;

        MailMutation mutation = Assert.Single(provider.Mutations);
        Assert.True(mutation.IsRead);
        Assert.True(scheduler.Requests[0].IsCanceled);
        Assert.False(viewModel.SelectedMessageSummary!.IsUnread);
    }

    [Fact]
    public async Task FailedAutomaticMutation_LeavesUnreadAndDoesNotRetryInSameDetail()
    {
        DwellProvider provider = ProviderWithInbox(Summary("message", unread: true));
        provider.MutationFailure = true;
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);
        await ActivateAndOpenAsync(viewModel, Account(), "message");
        viewModel.SetDetailHostActive(true);

        Task dwell = viewModel.CurrentReadDwellTask;
        scheduler.Complete(0);
        await dwell;

        Assert.Single(provider.Mutations);
        Assert.True(viewModel.SelectedMessageSummary!.IsUnread);
        Assert.True(viewModel.HasReadStateError);
        Assert.Single(scheduler.Requests);
    }

    [Fact]
    public async Task DraftFolder_HasNoReadActionTimerOrMutation()
    {
        MailFolder drafts = MailFolderCatalog.Create(MailFolderKind.Drafts, "DRAFT");
        DwellProvider provider = new([drafts]);
        provider.SetPage(drafts, [Summary("draft", unread: true)]);
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(provider, scheduler);

        await ActivateAndOpenAsync(viewModel, Account(), "draft");
        viewModel.SetDetailHostActive(true);

        Assert.False(viewModel.ShowReadStateAction);
        Assert.Empty(scheduler.Requests);
        Assert.Empty(provider.Mutations);
    }

    private static async Task ActivateAndOpenAsync(
        MailInboxViewModel viewModel,
        MailAccount account,
        string messageKey)
    {
        await viewModel.ActivateAsync(account);
        viewModel.OpenMessageCommand.Execute(
            viewModel.Messages.Single(message => message.MessageKey == messageKey));
        await viewModel.CurrentMessageLoadTask;
    }

    private static MailInboxViewModel CreateViewModel(
        DwellProvider provider,
        ManualDwellScheduler scheduler) =>
        new(
            new DwellProviderFactory(provider),
            composeViewModel: null,
            attachmentSaveService: null,
            messageSourceCache: null,
            readDwellScheduler: scheduler);

    private static DwellProvider ProviderWithInbox(params MailMessageSummary[] summaries)
    {
        MailFolder inbox = MailFolderCatalog.Inbox();
        DwellProvider provider = new([inbox]);
        provider.SetPage(inbox, summaries);
        return provider;
    }

    private static MailAccount Account() => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Yandex,
        EmailAddress = "account@example.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = MailAuthenticationKind.Password,
        IsEnabled = true
    };

    private static MailMessageSummary Summary(string key, bool unread) =>
        new(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            unread);

    public enum DwellCancellationKind
    {
        Folder,
        Account,
        WebService,
        Compose,
        WindowInactive
    }

    private sealed class ManualDwellScheduler : IMailReadDwellScheduler
    {
        public List<DelayRequest> Requests { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DelayRequest request = new(delay, source);
            cancellationToken.Register(() =>
            {
                request.IsCanceled = true;
                source.TrySetCanceled(cancellationToken);
            });
            Requests.Add(request);
            return source.Task;
        }

        public void Complete(int index) => Requests[index].Completion.TrySetResult(true);
    }

    private sealed class DelayRequest(TimeSpan delay, TaskCompletionSource<bool> completion)
    {
        public TimeSpan Delay { get; } = delay;
        public TaskCompletionSource<bool> Completion { get; } = completion;
        public bool IsCanceled { get; set; }
    }

    private sealed class DwellProviderFactory(DwellProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class DwellProvider(IReadOnlyList<MailFolder> folders) : IMailReadProvider, IMailMessageStateProvider
    {
        private readonly Dictionary<string, MailPage<MailMessageSummary>> _pages = [];

        public bool MutationFailure { get; set; }
        public MailMessageBodyKind BodyKind { get; set; } = MailMessageBodyKind.PlainText;
        public int BodyFetchCount { get; private set; }
        public List<MailMutation> Mutations { get; } = [];

        public void SetPage(MailFolder folder, IReadOnlyList<MailMessageSummary> summaries) =>
            _pages[folder.Key] = new MailPage<MailMessageSummary>(summaries, null);

        public bool Supports(MailProviderType providerType) => true;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(folders);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_pages.GetValueOrDefault(folder.Key) ?? new MailPage<MailMessageSummary>([], null));

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            BodyFetchCount++;
            bool unread = _pages[folder.Key].Items.Single(message => message.MessageKey == messageKey).IsUnread;
            return Task.FromResult(new MailMessageContent(
                messageKey,
                $"Subject {messageKey}",
                "Sender",
                "sender@example.test",
                account.EmailAddress,
                DateTimeOffset.UtcNow,
                BodyKind,
                BodyKind is MailMessageBodyKind.SanitizedHtml ? "<p>Body</p>" : "Body",
                [],
                unread,
                false));
        }

        public Task<MailReadStateCapability> GetReadStateCapabilityAsync(
            MailAccount account,
            MailFolder folder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MailReadStateCapability.Available);

        public Task SetReadStateAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            bool isRead,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add(new MailMutation(account.Id, folder.Key, messageKey, isRead));
            return MutationFailure
                ? Task.FromException(new MailReadException(
                    MailReadFailureKind.MutationFailed,
                    "Read state could not be changed."))
                : Task.CompletedTask;
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Inbox(), continuationToken, pageSize, cancellationToken);

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            GetMessageAsync(account, MailFolderCatalog.Inbox(), messageKey, cancellationToken);
    }

    private sealed record MailMutation(Guid AccountId, string FolderKey, string MessageKey, bool IsRead);
}
