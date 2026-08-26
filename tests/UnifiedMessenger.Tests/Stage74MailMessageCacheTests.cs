using System.Text.Json;
using System.Collections.Specialized;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.Tests;

public sealed class Stage74MailMessageCacheTests
{
    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.Yandex)]
    public async Task MessageA_ToMessageB_ToMessageA_FetchesEachBodyOnlyOnce(
        MailProviderType providerType)
    {
        CountingReadProvider provider = ProviderWithInbox("a", "b");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(providerType, 1);

        await viewModel.ActivateAsync(account);
        await OpenAsync(viewModel, "a");
        await OpenAsync(viewModel, "b");
        await OpenAsync(viewModel, "a");

        Assert.Equal(1, provider.FetchCount(account.Id, "a"));
        Assert.Equal(1, provider.FetchCount(account.Id, "b"));
        Assert.Equal("a", viewModel.SelectedMessageContent?.MessageKey);
    }

    [Fact]
    public async Task FolderSwitch_RestoresCachedBodyWithoutProviderFetch()
    {
        MailFolder inbox = MailFolderCatalog.Inbox();
        MailFolder sent = MailFolderCatalog.Create(MailFolderKind.Sent, "SENT");
        CountingReadProvider provider = new([inbox, sent]);
        provider.SetPage(inbox, [Summary("inbox-a")]);
        provider.SetPage(sent, [Summary("sent-b")]);
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Gmail, 1);

        await viewModel.ActivateAsync(account);
        await OpenAsync(viewModel, "inbox-a");
        await SelectFolderAsync(viewModel, MailFolderKind.Sent);
        await OpenAsync(viewModel, "sent-b");
        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);

        Assert.Equal("inbox-a", viewModel.SelectedMessageContent?.MessageKey);
        Assert.Equal(1, provider.FetchCount(account.Id, "inbox-a"));
    }

    [Fact]
    public async Task AccountSwitch_RestoresEachAccountsBodyIndependently()
    {
        CountingReadProvider provider = ProviderWithInbox("shared");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount gmail = Account(MailProviderType.Gmail, 1);
        MailAccount yandex = Account(MailProviderType.Yandex, 2);

        await viewModel.ActivateAsync(gmail);
        await OpenAsync(viewModel, "shared");
        string gmailBody = viewModel.SelectedMessageContent!.BodyContent;
        await viewModel.ActivateAsync(yandex);
        await OpenAsync(viewModel, "shared");
        string yandexBody = viewModel.SelectedMessageContent!.BodyContent;
        await viewModel.ActivateAsync(gmail);

        Assert.NotEqual(gmailBody, yandexBody);
        Assert.Equal(gmailBody, viewModel.SelectedMessageContent?.BodyContent);
        Assert.Equal(1, provider.FetchCount(gmail.Id, "shared"));
        Assert.Equal(1, provider.FetchCount(yandex.Id, "shared"));
    }

    [Fact]
    public async Task Refresh_PreservesVisibleCachedBodyAndDoesNotRefetchIt()
    {
        CountingReadProvider provider = ProviderWithInbox("a", "b");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Gmail, 1);
        await viewModel.ActivateAsync(account);
        await OpenAsync(viewModel, "a");
        MailMessageContent content = viewModel.SelectedMessageContent!;

        provider.SetPage(MailFolderCatalog.Inbox(), [Summary("a"), Summary("a"), Summary("b")]);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Same(content, viewModel.SelectedMessageContent);
        Assert.Equal("a", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal(1, provider.FetchCount(account.Id, "a"));
    }

    [Fact]
    public async Task ReadStateChanges_UpdateCachedBodyWithoutRefetch()
    {
        CountingReadProvider provider = ProviderWithInbox("a");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Gmail, 1);
        await viewModel.ActivateAsync(account);
        await OpenAsync(viewModel, "a");

        await viewModel.SetReadStateCommand.ExecuteAsync(null);
        Assert.False(viewModel.SelectedMessageContent!.IsUnread);
        await viewModel.SetReadStateCommand.ExecuteAsync(null);
        Assert.True(viewModel.SelectedMessageContent!.IsUnread);
        await viewModel.ActivateAsync(null);
        await viewModel.ActivateAsync(account);

        Assert.True(viewModel.SelectedMessageContent!.IsUnread);
        Assert.Equal(1, provider.FetchCount(account.Id, "a"));
        Assert.Equal(2, provider.MutationCount);
    }

    [Theory]
    [InlineData(MailProviderType.Gmail, true)]
    [InlineData(MailProviderType.Gmail, false)]
    [InlineData(MailProviderType.Yandex, true)]
    [InlineData(MailProviderType.Yandex, false)]
    public async Task ReadStateMutation_PreservesSelectionBodyAndFetchCount(
        MailProviderType providerType,
        bool initiallyUnread)
    {
        CountingReadProvider provider = new([MailFolderCatalog.Inbox()]);
        provider.SetPage(MailFolderCatalog.Inbox(), [Summary("a", initiallyUnread)]);
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(providerType, 1);
        await viewModel.ActivateAsync(account);
        await OpenAsync(viewModel, "a");
        SimulateWpfSelectionResetOnCollectionReplacement(viewModel);

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.Equal("a", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Equal("a", viewModel.SelectedMessageContent?.MessageKey);
        Assert.Equal(!initiallyUnread, viewModel.SelectedMessageSummary?.IsUnread);
        Assert.Equal(!initiallyUnread, viewModel.SelectedMessageContent?.IsUnread);
        Assert.Equal(1, provider.FetchCount(account.Id, "a"));
    }

    [Theory]
    [InlineData(MailMessageBodyKind.PlainText)]
    [InlineData(MailMessageBodyKind.SanitizedHtml)]
    public async Task ReadStateMutation_KeepsViewerContentActive(
        MailMessageBodyKind bodyKind)
    {
        CountingReadProvider provider = ProviderWithInbox("a");
        provider.ReturnBodyKind = bodyKind;
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(Account(MailProviderType.Gmail, 1));
        await OpenAsync(viewModel, "a");
        SimulateWpfSelectionResetOnCollectionReplacement(viewModel);
        List<bool> rendererRefreshDecisions = [];
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(MailInboxViewModel.SelectedMessageContent))
            {
                rendererRefreshDecisions.Add(
                    MainWindow.ShouldRefreshMailRendererContent(viewModel, eventArgs.PropertyName));
            }
        };

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.Equal("a", viewModel.SelectedMessageContent?.MessageKey);
        Assert.NotNull(viewModel.SelectedMessageContent?.BodyContent);
        Assert.Single(rendererRefreshDecisions);
        Assert.False(rendererRefreshDecisions[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualReadStateMutation_PreservesPrintBodySelectionAndRendererReadiness(
        bool initiallyUnread)
    {
        CountingReadProvider provider = new([MailFolderCatalog.Inbox()])
        {
            ReturnBodyKind = MailMessageBodyKind.SanitizedHtml
        };
        provider.SetPage(MailFolderCatalog.Inbox(), [Summary("printable", initiallyUnread)]);
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Gmail, 1);
        await viewModel.ActivateAsync(account);
        await OpenAsync(viewModel, "printable");
        viewModel.SetPrintAvailable(isAvailable: true);
        int rendererRefreshRequests = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (MainWindow.ShouldRefreshMailRendererContent(viewModel, eventArgs.PropertyName))
            {
                rendererRefreshRequests++;
            }
        };

        Assert.True(viewModel.CanPrintMessage);
        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanPrintMessage);
        Assert.Equal("printable", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Equal("printable", viewModel.SelectedMessageContent?.MessageKey);
        Assert.Equal(1, provider.FetchCount(account.Id, "printable"));
        Assert.Equal(0, rendererRefreshRequests);

        viewModel.BackToMessageListCommand.Execute(null);
        Assert.False(viewModel.CanPrintMessage);

        viewModel.OpenMessageCommand.Execute(viewModel.SelectedMessageSummary);
        Assert.True(viewModel.CanPrintMessage);
        Assert.Equal(1, provider.FetchCount(account.Id, "printable"));
    }

    [Fact]
    public async Task ReadStateMutation_PreservesRemoteImageConsent()
    {
        CountingReadProvider provider = ProviderWithRemoteInbox("a");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(Account(MailProviderType.Gmail, 1));
        await OpenAsync(viewModel, "a");
        viewModel.MarkRemoteImagesShown();
        SimulateWpfSelectionResetOnCollectionReplacement(viewModel);

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.True(viewModel.AreRemoteImagesShown);
        Assert.False(viewModel.ShowRemoteImagesBanner);
        Assert.Equal("a", viewModel.SelectedMessageContent?.MessageKey);
    }

    [Fact]
    public async Task FailedReadStateMutation_KeepsPreviousStateAndVisibleBody()
    {
        CountingReadProvider provider = ProviderWithInbox("a");
        provider.MutationFailure = true;
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Yandex, 1);
        await viewModel.ActivateAsync(account);
        await OpenAsync(viewModel, "a");
        MailMessageContent visibleContent = viewModel.SelectedMessageContent!;

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.True(viewModel.SelectedMessageSummary!.IsUnread);
        Assert.Same(visibleContent, viewModel.SelectedMessageContent);
        Assert.True(viewModel.HasReadStateError);
        Assert.Equal(1, provider.FetchCount(account.Id, "a"));
    }

    [Fact]
    public async Task StaleCompletedBody_IsCachedButCannotReplaceCurrentSelection()
    {
        CountingReadProvider provider = ProviderWithInbox("a", "b");
        TaskCompletionSource<MailMessageContent> delayed = provider.Delay("a");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Yandex, 1);
        await viewModel.ActivateAsync(account);

        viewModel.SelectedMessageSummary = Find(viewModel, "a");
        Task staleLoad = viewModel.CurrentMessageLoadTask;
        await OpenAsync(viewModel, "b");
        delayed.SetResult(Content(account.Id, "a"));
        await staleLoad;

        Assert.Equal("b", viewModel.SelectedMessageContent?.MessageKey);
        await OpenAsync(viewModel, "a");
        Assert.Equal("a", viewModel.SelectedMessageContent?.MessageKey);
        Assert.Equal(1, provider.FetchCount(account.Id, "a"));
    }

    [Fact]
    public async Task MessageBodyLru_IsBoundedAndEvictsLeastRecentlyUsedEntry()
    {
        string[] keys = Enumerable.Range(0, MailInboxViewModel.MessageBodyCacheCapacity + 1)
            .Select(index => $"message-{index}")
            .ToArray();
        CountingReadProvider provider = ProviderWithInbox(keys);
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Gmail, 1);
        await viewModel.ActivateAsync(account);

        foreach (string key in keys.Take(MailInboxViewModel.MessageBodyCacheCapacity))
        {
            await OpenAsync(viewModel, key);
        }

        await OpenAsync(viewModel, keys[0]);
        await OpenAsync(viewModel, keys[^1]);

        Assert.Equal(MailInboxViewModel.MessageBodyCacheCapacity, viewModel.CachedMessageBodyCount);
        await OpenAsync(viewModel, keys[0]);
        Assert.Equal(1, provider.FetchCount(account.Id, keys[0]));
        await OpenAsync(viewModel, keys[1]);
        Assert.Equal(2, provider.FetchCount(account.Id, keys[1]));
        Assert.Equal(MailInboxViewModel.MessageBodyCacheCapacity, viewModel.CachedMessageBodyCount);
    }

    [Fact]
    public async Task DeleteAccount_RemovesOnlyThatAccountsCachedBodiesAndConsents()
    {
        CountingReadProvider provider = ProviderWithRemoteInbox("shared");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount gmail = Account(MailProviderType.Gmail, 1);
        MailAccount yandex = Account(MailProviderType.Yandex, 2);

        await viewModel.ActivateAsync(gmail);
        await OpenAsync(viewModel, "shared");
        viewModel.MarkRemoteImagesShown();
        await viewModel.ActivateAsync(yandex);
        await OpenAsync(viewModel, "shared");
        viewModel.MarkRemoteImagesShown();

        viewModel.RemoveAccount(gmail.Id);

        Assert.Equal(1, viewModel.CachedMessageBodyCount);
        Assert.Equal(1, viewModel.CachedRemoteImageConsentCount);
        await viewModel.ActivateAsync(yandex);
        Assert.True(viewModel.AreRemoteImagesShown);
        Assert.Equal(1, provider.FetchCount(yandex.Id, "shared"));
        await viewModel.ActivateAsync(gmail);
        await OpenAsync(viewModel, "shared");
        Assert.False(viewModel.AreRemoteImagesShown);
        Assert.Equal(2, provider.FetchCount(gmail.Id, "shared"));
    }

    [Fact]
    public async Task DeleteAccount_PreventsAStaleInFlightBodyFromRepopulatingItsCache()
    {
        CountingReadProvider provider = ProviderWithInbox("a");
        TaskCompletionSource<MailMessageContent> delayed = provider.Delay("a");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount account = Account(MailProviderType.Gmail, 1);
        await viewModel.ActivateAsync(account);
        viewModel.SelectedMessageSummary = Find(viewModel, "a");
        Task staleLoad = viewModel.CurrentMessageLoadTask;

        viewModel.RemoveAccount(account.Id);
        delayed.SetResult(Content(account.Id, "a"));
        await staleLoad;

        Assert.Equal(0, viewModel.CachedMessageBodyCount);
    }

    [Fact]
    public async Task Dispose_ClearsAllSessionOnlyMailCaches()
    {
        CountingReadProvider provider = ProviderWithRemoteInbox("a");
        MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(Account(MailProviderType.Gmail, 1));
        await OpenAsync(viewModel, "a");
        viewModel.MarkRemoteImagesShown();

        viewModel.Dispose();

        Assert.Equal(0, viewModel.CachedMessageBodyCount);
        Assert.Equal(0, viewModel.CachedRemoteImageConsentCount);
    }

    [Fact]
    public async Task RemoteImageConsent_IsMessageSpecificAndSurvivesCachedReopen()
    {
        CountingReadProvider provider = ProviderWithRemoteInbox("a", "b");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(Account(MailProviderType.Gmail, 1));
        await OpenAsync(viewModel, "a");
        viewModel.MarkRemoteImagesShown();

        await OpenAsync(viewModel, "b");
        Assert.False(viewModel.AreRemoteImagesShown);
        await OpenAsync(viewModel, "a");
        Assert.True(viewModel.AreRemoteImagesShown);
    }

    [Fact]
    public void RemoteImageByteCache_UsesTheSameBoundedLruPolicy()
    {
        BoundedLruCache<int, IReadOnlyDictionary<string, MailImageContent>> cache =
            new(MainWindow.RemoteImageSessionCacheCapacity);
        IReadOnlyDictionary<string, MailImageContent> image =
            new Dictionary<string, MailImageContent>
            {
                ["image"] = new("image/png", new byte[] { 1, 2, 3 })
            };

        for (int index = 0; index <= MainWindow.RemoteImageSessionCacheCapacity; index++)
        {
            cache.Set(index, image);
        }

        Assert.Equal(20, MainWindow.RemoteImageSessionCacheCapacity);
        Assert.Equal(MainWindow.RemoteImageSessionCacheCapacity, cache.Count);
        Assert.False(cache.TryGet(0, out _));
        Assert.True(cache.TryGet(MainWindow.RemoteImageSessionCacheCapacity, out _));
    }

    [Fact]
    public void MessageBodiesAndRemoteImageConsent_AreNotPartOfPersistedSettings()
    {
        string settings = JsonSerializer.Serialize(AppSettings.CreateDefault());
        string[] persistedPropertyNames = typeof(AppSettings).GetProperties()
            .Concat(typeof(MailAccount).GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("MessageBody", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RemoteImage", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            persistedPropertyNames,
            name => name.Contains("MessageContent", StringComparison.OrdinalIgnoreCase)
                || name.Contains("RemoteImage", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Consent", StringComparison.OrdinalIgnoreCase));
    }

    private static MailInboxViewModel CreateViewModel(CountingReadProvider provider) =>
        new(new ProviderFactory(provider));

    private static CountingReadProvider ProviderWithInbox(params string[] keys)
    {
        CountingReadProvider provider = new([MailFolderCatalog.Inbox()]);
        provider.SetPage(MailFolderCatalog.Inbox(), keys.Select(key => Summary(key)).ToArray());
        return provider;
    }

    private static CountingReadProvider ProviderWithRemoteInbox(params string[] keys)
    {
        CountingReadProvider provider = ProviderWithInbox(keys);
        provider.ReturnRemoteImages = true;
        return provider;
    }

    private static async Task OpenAsync(MailInboxViewModel viewModel, string messageKey)
    {
        viewModel.SelectedMessageSummary = Find(viewModel, messageKey);
        await viewModel.CurrentMessageLoadTask;
    }

    private static async Task SelectFolderAsync(MailInboxViewModel viewModel, MailFolderKind kind)
    {
        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind == kind);
        await viewModel.CurrentFolderLoadTask;
    }

    private static void SimulateWpfSelectionResetOnCollectionReplacement(MailInboxViewModel viewModel)
    {
        viewModel.Messages.CollectionChanged += (_, eventArgs) =>
        {
            if (eventArgs.Action is NotifyCollectionChangedAction.Replace)
            {
                viewModel.SelectedMessageSummary = null;
            }
        };
    }

    private static MailMessageSummary Find(MailInboxViewModel viewModel, string messageKey) =>
        viewModel.Messages.Single(message => message.MessageKey == messageKey);

    private static MailAccount Account(MailProviderType provider, int suffix) => new()
    {
        Id = Guid.Parse($"{suffix:D8}-1111-1111-1111-111111111111"),
        Provider = provider,
        EmailAddress = $"account-{suffix}@example.test",
        CredentialKey = $"credential-{suffix}",
        AuthenticationKind = provider is MailProviderType.Gmail
            ? MailAuthenticationKind.OAuth
            : MailAuthenticationKind.Password,
        IsEnabled = true
    };

    private static MailMessageSummary Summary(string key, bool isUnread = true) =>
        new(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            isUnread);

    private static MailMessageContent Content(
        Guid accountId,
        string key,
        bool remoteImages = false,
        bool isUnread = true,
        MailMessageBodyKind? bodyKind = null) =>
        new(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            "recipient@example.test",
            DateTimeOffset.UtcNow,
            bodyKind ?? (remoteImages ? MailMessageBodyKind.SanitizedHtml : MailMessageBodyKind.PlainText),
            bodyKind is MailMessageBodyKind.SanitizedHtml || remoteImages
                ? $"<p>Body {accountId:D} {key}</p>"
                : $"Body {accountId:D} {key}",
            remoteImages
                ? [new MailRemoteImageReference("image", new Uri("https://images.example.test/image.png"))]
                : [],
            isUnread,
            false);

    private sealed class ProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class CountingReadProvider(
        IReadOnlyList<MailFolder> folders) : IMailReadProvider, IMailMessageStateProvider
    {
        private readonly Dictionary<string, MailPage<MailMessageSummary>> _pages = [];
        private readonly Dictionary<(Guid AccountId, string MessageKey), int> _fetchCounts = [];
        private readonly Dictionary<string, TaskCompletionSource<MailMessageContent>> _delayed = [];

        public bool ReturnRemoteImages { get; set; }
        public MailMessageBodyKind ReturnBodyKind { get; set; } = MailMessageBodyKind.PlainText;
        public bool MutationFailure { get; set; }
        public int MutationCount { get; private set; }

        public bool Supports(MailProviderType providerType) => true;

        public void SetPage(MailFolder folder, IReadOnlyList<MailMessageSummary> messages) =>
            _pages[folder.Key] = new MailPage<MailMessageSummary>(messages, null);

        public TaskCompletionSource<MailMessageContent> Delay(string messageKey)
        {
            TaskCompletionSource<MailMessageContent> source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            _delayed[messageKey] = source;
            return source;
        }

        public int FetchCount(Guid accountId, string messageKey) =>
            _fetchCounts.GetValueOrDefault((accountId, messageKey));

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) => Task.FromResult(folders);

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
            (Guid, string) key = (account.Id, messageKey);
            _fetchCounts[key] = _fetchCounts.GetValueOrDefault(key) + 1;
            bool isUnread = _pages.Values
                .SelectMany(page => page.Items)
                .FirstOrDefault(message => message.MessageKey == messageKey)
                ?.IsUnread ?? true;
            return _delayed.TryGetValue(messageKey, out TaskCompletionSource<MailMessageContent>? delayed)
                ? delayed.Task
                : Task.FromResult(Content(
                    account.Id,
                    messageKey,
                    ReturnRemoteImages,
                    isUnread,
                    ReturnBodyKind));
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
            MutationCount++;
            return MutationFailure
                ? Task.FromException(new MailReadException(
                    MailReadFailureKind.MutationFailed,
                    "Mutation failed."))
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
}
