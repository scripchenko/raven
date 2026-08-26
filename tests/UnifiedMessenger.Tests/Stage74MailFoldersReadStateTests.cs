using Google.Apis.Gmail.v1.Data;
using MailKit;
using MimeKit;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.Tests;

public sealed class Stage74MailFoldersReadStateTests
{
    [Theory]
    [InlineData("INBOX", MailFolderKind.Inbox, "Входящие")]
    [InlineData("SENT", MailFolderKind.Sent, "Отправленные")]
    [InlineData("DRAFT", MailFolderKind.Drafts, "Черновики")]
    [InlineData("SPAM", MailFolderKind.Spam, "Спам")]
    [InlineData("TRASH", MailFolderKind.Trash, "Корзина")]
    public void GmailSystemLabels_MapToCommonFolders(string label, MailFolderKind kind, string displayName)
    {
        IReadOnlyList<MailFolder> folders = GmailSystemFolders.Map(
            new HashSet<string>([label], StringComparer.Ordinal));

        MailFolder folder = Assert.Single(folders);
        Assert.Equal(kind, folder.Kind);
        Assert.Equal(displayName, folder.DisplayName);
        Assert.Equal(label, folder.ProviderLocator);
    }

    [Fact]
    public void GmailUnavailableSystemFolder_IsHiddenGracefully()
    {
        IReadOnlyList<MailFolder> folders = GmailSystemFolders.Map(
            new HashSet<string>([GmailSystemFolders.Inbox, GmailSystemFolders.Sent], StringComparer.Ordinal));

        Assert.Equal([MailFolderKind.Inbox, MailFolderKind.Sent], folders.Select(folder => folder.Kind));
        Assert.DoesNotContain(folders, folder => folder.Kind is MailFolderKind.Trash);
    }

    [Fact]
    public async Task GmailFolderListing_UsesOfficialSystemLabelsOnly()
    {
        FakeGmailClient client = new()
        {
            Labels = new HashSet<string>(["INBOX", "SENT", "DRAFT", "SPAM", "TRASH", "CATEGORY_SOCIAL"])
        };
        GmailMailReadProvider provider = CreateGmailProvider(client, modifyScope: false);

        IReadOnlyList<MailFolder> folders = await provider.GetFoldersAsync(GmailAccount());

        Assert.Equal(5, folders.Count);
        Assert.DoesNotContain(folders, folder => folder.ProviderLocator == "CATEGORY_SOCIAL");
    }

    [Theory]
    [InlineData(MailFolderKind.Inbox, "INBOX", false)]
    [InlineData(MailFolderKind.Sent, "SENT", false)]
    [InlineData(MailFolderKind.Drafts, "DRAFT", false)]
    [InlineData(MailFolderKind.Spam, "SPAM", true)]
    [InlineData(MailFolderKind.Trash, "TRASH", true)]
    public async Task GmailPage_FiltersBySelectedSystemLabel(
        MailFolderKind kind,
        string expectedLabel,
        bool expectedIncludeSpamTrash)
    {
        FakeGmailClient client = new();
        GmailMailReadProvider provider = CreateGmailProvider(client, modifyScope: false);
        MailFolder folder = MailFolderCatalog.Create(kind, expectedLabel);

        await provider.GetPageAsync(GmailAccount(), folder, "cursor", 20);

        Assert.Equal(expectedLabel, client.LastLabel);
        Assert.Equal(expectedIncludeSpamTrash, client.LastIncludeSpamTrash);
        Assert.Equal("cursor", client.LastPageToken);
    }

    [Fact]
    public void GmailMarkRead_RemovesUnreadLabel()
    {
        ModifyMessageRequest request = GmailApiReadClient.CreateReadStateRequest(isRead: true);

        Assert.Contains(GmailSystemFolders.Unread, request.RemoveLabelIds);
        Assert.Null(request.AddLabelIds);
    }

    [Fact]
    public void GmailMarkUnread_AddsUnreadLabel()
    {
        ModifyMessageRequest request = GmailApiReadClient.CreateReadStateRequest(isRead: false);

        Assert.Contains(GmailSystemFolders.Unread, request.AddLabelIds);
        Assert.Null(request.RemoveLabelIds);
    }

    [Fact]
    public async Task GmailReadonlyCredential_ReportsMissingMutationCapability()
    {
        GmailMailReadProvider provider = CreateGmailProvider(new FakeGmailClient(), modifyScope: false);

        MailReadStateCapability capability = await provider.GetReadStateCapabilityAsync(
            GmailAccount(),
            MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"));

        Assert.False(capability.CanSetReadState);
        Assert.True(capability.RequiresAuthorization);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GmailMutation_InvokesModifyOnlyForExplicitCommand(bool isRead)
    {
        FakeGmailClient client = new();
        GmailMailReadProvider provider = CreateGmailProvider(client, modifyScope: true);

        await provider.SetReadStateAsync(
            GmailAccount(),
            MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"),
            "gmail:message",
            isRead);

        Assert.Equal(1, client.MutationCount);
        Assert.Equal(isRead, client.LastIsRead);
        Assert.Equal("message", client.LastMessageId);
    }

    [Fact]
    public void GmailClient_HasNoSendEndpoint()
    {
        Assert.DoesNotContain(
            typeof(GmailApiReadClient).GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic),
            method => method.Name.Contains("Send", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImapFolders_MapInboxAndOnlyAdvertisedSpecialFolders()
    {
        FakeImapClient client = new()
        {
            Folders =
            [
                new(MailFolderKind.Inbox, "INBOX"),
                new(MailFolderKind.Sent, "server-sent"),
                new(MailFolderKind.Drafts, "server-drafts"),
                new(MailFolderKind.Spam, "server-junk"),
                new(MailFolderKind.Trash, "server-trash")
            ]
        };
        ImapMailReadProvider provider = CreateImapProvider(client);

        IReadOnlyList<MailFolder> folders = await provider.GetFoldersAsync(ImapAccount());

        Assert.Equal(
            [MailFolderKind.Inbox, MailFolderKind.Sent, MailFolderKind.Drafts, MailFolderKind.Spam, MailFolderKind.Trash],
            folders.Select(folder => folder.Kind));
        Assert.Equal("server-junk", folders.Single(folder => folder.Kind is MailFolderKind.Spam).ProviderLocator);
    }

    [Fact]
    public async Task ImapMissingSpecialFolder_DoesNotCrashOrGuessName()
    {
        FakeImapClient client = new()
        {
            Folders = [new(MailFolderKind.Inbox, "INBOX")]
        };

        IReadOnlyList<MailFolder> folders = await CreateImapProvider(client).GetFoldersAsync(ImapAccount());

        Assert.Single(folders);
        Assert.Equal(MailFolderKind.Inbox, folders[0].Kind);
    }

    [Fact]
    public void ImapMessageIdentity_IsFolderScoped()
    {
        string inbox = ImapMailReadProvider.CreateMessageKey(MailFolderKind.Inbox, 9, 42);
        string sent = ImapMailReadProvider.CreateMessageKey(MailFolderKind.Sent, 9, 42);

        Assert.NotEqual(inbox, sent);
    }

    [Fact]
    public void ImapMessageIdentity_IsUidValidityScoped()
    {
        string before = ImapMailReadProvider.CreateMessageKey(MailFolderKind.Inbox, 9, 42);
        string after = ImapMailReadProvider.CreateMessageKey(MailFolderKind.Inbox, 10, 42);

        Assert.NotEqual(before, after);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ImapSeenMutation_UsesSeenAndCorrectDirection(bool isRead, bool expectedAdd)
    {
        (MessageFlags flags, bool add) = MailKitImapInboxClient.CreateSeenMutation(isRead);

        Assert.Equal(MessageFlags.Seen, flags);
        Assert.Equal(expectedAdd, add);
        Assert.Equal(FolderAccess.ReadWrite, MailKitImapInboxClient.MutationAccess);
        Assert.Equal(FolderAccess.ReadOnly, MailKitImapInboxClient.InboxAccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImapExplicitMutation_UsesFolderScopedUidAndDisconnectableClient(bool isRead)
    {
        FakeImapClient client = new();
        ImapMailReadProvider provider = CreateImapProvider(client);
        MailFolder sent = MailFolderCatalog.Create(MailFolderKind.Sent, "opaque-sent");

        await provider.SetReadStateAsync(
            ImapAccount(),
            sent,
            ImapMailReadProvider.CreateMessageKey(MailFolderKind.Sent, 9, 77),
            isRead);

        Assert.Equal((uint)77, client.LastUid);
        Assert.Equal((uint)9, client.LastUidValidity);
        Assert.Equal("opaque-sent", client.LastFolder?.FullName);
        Assert.Equal(isRead, client.LastIsRead);
        Assert.Equal(1, client.MutationCount);
    }

    [Fact]
    public async Task MessageOpenWithoutActiveDetailHost_DoesNotMutateServerState()
    {
        StatefulFolderProvider provider = new();
        provider.SetPage(MailFolderCatalog.Inbox(), [Summary("one", unread: true)]);
        MailInboxViewModel viewModel = new(new FolderProviderFactory(provider));

        await viewModel.ActivateAsync(GmailAccount());
        viewModel.SelectedMessageSummary = viewModel.Messages.Single();
        await viewModel.CurrentMessageLoadTask;

        Assert.Equal(0, provider.MutationCount);
        Assert.True(viewModel.SelectedMessageSummary.IsUnread);
    }

    [Fact]
    public async Task ServerConfirmedMutation_UpdatesSummaryOnlyAfterSuccess()
    {
        StatefulFolderProvider provider = new();
        provider.SetPage(MailFolderCatalog.Inbox(), [Summary("one", unread: true)]);
        MailInboxViewModel viewModel = new(new FolderProviderFactory(provider));
        await viewModel.ActivateAsync(GmailAccount());
        viewModel.SelectedMessageSummary = viewModel.Messages.Single();
        await viewModel.CurrentMessageLoadTask;

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.False(viewModel.SelectedMessageSummary!.IsUnread);
        Assert.False(viewModel.SelectedMessageContent!.IsUnread);
        Assert.Equal(1, provider.MutationCount);
    }

    [Fact]
    public async Task FailedMutation_KeepsPreviousLocalState()
    {
        StatefulFolderProvider provider = new() { MutationFailure = true };
        provider.SetPage(MailFolderCatalog.Inbox(), [Summary("one", unread: true)]);
        MailInboxViewModel viewModel = new(new FolderProviderFactory(provider));
        await viewModel.ActivateAsync(GmailAccount());
        viewModel.SelectedMessageSummary = viewModel.Messages.Single();
        await viewModel.CurrentMessageLoadTask;

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.True(viewModel.SelectedMessageSummary!.IsUnread);
        Assert.True(viewModel.SelectedMessageContent!.IsUnread);
        Assert.True(viewModel.HasReadStateError);
    }

    [Fact]
    public async Task FolderSelectionAndPagination_AreIsolatedPerAccountAndFolder()
    {
        StatefulFolderProvider provider = new();
        MailFolder inbox = MailFolderCatalog.Inbox();
        MailFolder sent = MailFolderCatalog.Create(MailFolderKind.Sent, "SENT");
        provider.Folders = [inbox, sent];
        provider.SetPage(inbox, [Summary("inbox", true)], "inbox-next");
        provider.SetPage(sent, [Summary("sent", false)], "sent-next");
        MailInboxViewModel viewModel = new(new FolderProviderFactory(provider));

        await viewModel.ActivateAsync(GmailAccount());
        viewModel.SelectedMessageSummary = viewModel.Messages.Single();
        await viewModel.CurrentMessageLoadTask;
        viewModel.SelectedFolder = sent;
        await viewModel.CurrentFolderLoadTask;
        viewModel.SelectedMessageSummary = viewModel.Messages.Single();
        await viewModel.CurrentMessageLoadTask;
        viewModel.SelectedFolder = inbox;
        await viewModel.CurrentFolderLoadTask;

        Assert.Equal("inbox", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Equal("inbox-next", viewModel.ContinuationToken);
        Assert.Equal("inbox", viewModel.Messages.Single().MessageKey);
    }

    [Fact]
    public async Task StaleFolderResponse_CannotReplaceActiveFolder()
    {
        StatefulFolderProvider provider = new();
        MailFolder inbox = MailFolderCatalog.Inbox();
        MailFolder sent = MailFolderCatalog.Create(MailFolderKind.Sent, "SENT");
        provider.Folders = [inbox, sent];
        provider.SetPage(inbox, [Summary("inbox", true)]);
        TaskCompletionSource<MailPage<MailMessageSummary>> delayed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DelayedPages[sent.Key] = delayed;
        MailInboxViewModel viewModel = new(new FolderProviderFactory(provider));
        await viewModel.ActivateAsync(GmailAccount());

        viewModel.SelectedFolder = sent;
        Task sentLoad = viewModel.CurrentFolderLoadTask;
        viewModel.SelectedFolder = inbox;
        await viewModel.CurrentFolderLoadTask;
        delayed.SetResult(new MailPage<MailMessageSummary>([Summary("stale", false)], null));
        await sentLoad;

        Assert.Equal(inbox.Key, viewModel.SelectedFolder?.Key);
        Assert.Equal("inbox", viewModel.Messages.Single().MessageKey);
    }

    [Fact]
    public async Task GmailScopeUpgrade_RequestsModifyScopeAndReplacesMatchingCredential()
    {
        RecordingCredentialStore store = new(ReadOnlyCredential());
        RecordingOAuthService oauth = new("same@gmail.test", success: true);
        GmailScopeUpgradeService service = new(store, oauth);

        GmailScopeUpgradeResult result = await service.UpgradeAsync(GmailAccount());

        Assert.True(result.IsSuccess);
        Assert.Equal(GmailOAuthConstants.ModifyScope, oauth.RequestedScope);
        Assert.Equal(1, store.SaveCount);
        Assert.True(store.Value.HasGmailModifyScope);
    }

    [Fact]
    public void GoogleOAuthProtocol_ModifyUpgradeRequestsOnlyGmailModify()
    {
        GoogleOAuthProtocolClient client = new();

        GoogleOAuthAuthorizationRequest request = client.CreateAuthorizationRequest(
            new GoogleOAuthClientConfiguration("client", "secret"),
            new Uri("http://127.0.0.1:49152/oauth2/callback/"),
            "state",
            GmailOAuthConstants.ModifyScope);
        string query = Uri.UnescapeDataString(request.AuthorizationUri.Query);

        Assert.Contains(GmailOAuthConstants.ModifyScope, query, StringComparison.Ordinal);
        Assert.DoesNotContain("gmail.send", query, StringComparison.Ordinal);
        Assert.DoesNotContain("gmail.compose", query, StringComparison.Ordinal);
        Assert.DoesNotContain("https://mail.google.com/", query, StringComparison.Ordinal);
        Assert.Equal(GmailOAuthConstants.ModifyScope, request.RequestedScope);
    }

    [Theory]
    [InlineData(false, "same@gmail.test")]
    [InlineData(true, "other@gmail.test")]
    public async Task GmailScopeUpgrade_FailureOrWrongIdentityPreservesOldCredential(
        bool authorizationSuccess,
        string profileEmail)
    {
        MailCredential oldCredential = ReadOnlyCredential();
        RecordingCredentialStore store = new(oldCredential);
        RecordingOAuthService oauth = new(profileEmail, authorizationSuccess);
        GmailScopeUpgradeService service = new(store, oauth);

        GmailScopeUpgradeResult result = await service.UpgradeAsync(GmailAccount());

        Assert.False(result.IsSuccess);
        Assert.Same(oldCredential, store.Value);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task GmailScopeUpgrade_ProfileFailurePreservesOldCredential()
    {
        MailCredential oldCredential = ReadOnlyCredential();
        RecordingCredentialStore store = new(oldCredential);
        RecordingOAuthService oauth = new("same@gmail.test", success: true) { ProfileFailure = true };

        GmailScopeUpgradeResult result = await new GmailScopeUpgradeService(store, oauth)
            .UpgradeAsync(GmailAccount());

        Assert.False(result.IsSuccess);
        Assert.Same(oldCredential, store.Value);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void GmailScopeMetadata_IsNotPartOfSettingsSchema()
    {
        Assert.Equal(4, AppSettings.CurrentSchemaVersion);
        Assert.DoesNotContain(
            typeof(MailAccount).GetProperties(),
            property => property.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Drafts_DoNotExposeReadStateMutation()
    {
        MailFolder drafts = MailFolderCatalog.Create(MailFolderKind.Drafts, "DRAFT");

        Assert.False(drafts.SupportsReadState);
    }

    private static GmailMailReadProvider CreateGmailProvider(FakeGmailClient client, bool modifyScope) =>
        new(
            new RecordingCredentialStore(modifyScope ? ModifyCredential() : ReadOnlyCredential()),
            client,
            new FakeContentExtractor());

    private static ImapMailReadProvider CreateImapProvider(FakeImapClient client) =>
        new(
            new RecordingCredentialStore(MailCredential.CreatePassword("app-password")),
            new TestMailProviderFactory(new YandexMailProvider(new NoOpConnectionValidator())),
            client,
            new FakeContentExtractor());

    private static MailAccount GmailAccount() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Provider = MailProviderType.Gmail,
        EmailAddress = "same@gmail.test",
        CredentialKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        AuthenticationKind = MailAuthenticationKind.OAuth,
        IsEnabled = true
    };

    private static MailAccount ImapAccount() => new()
    {
        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Provider = MailProviderType.Yandex,
        EmailAddress = "mail@yandex.test",
        CredentialKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        AuthenticationKind = MailAuthenticationKind.Password,
        IsEnabled = true
    };

    private static MailCredential ReadOnlyCredential() =>
        MailCredential.CreateGmailOAuth("old-refresh", "client", "secret");

    private static MailCredential ModifyCredential() =>
        MailCredential.CreateGmailOAuth(
            "new-refresh",
            "client",
            "secret",
            GmailOAuthConstants.ModifyScope);

    private static MailMessageSummary Summary(string key, bool unread) =>
        new(key, "Subject", "Sender", "sender@example.test", DateTimeOffset.UtcNow, "Preview", unread);

    private static MailMessageContent Content(string key, bool unread) =>
        new(
            key,
            "Subject",
            "Sender",
            "sender@example.test",
            "receiver@example.test",
            DateTimeOffset.UtcNow,
            MailMessageBodyKind.PlainText,
            "Body",
            [],
            unread,
            false);

    private sealed class FakeGmailClient : IGmailApiReadClient
    {
        public IReadOnlySet<string> Labels { get; set; } = new HashSet<string>(GmailSystemFolders.LabelIds);
        public string? LastLabel { get; private set; }
        public bool LastIncludeSpamTrash { get; private set; }
        public string? LastPageToken { get; private set; }
        public string? LastMessageId { get; private set; }
        public bool LastIsRead { get; private set; }
        public int MutationCount { get; private set; }

        public Task<IReadOnlySet<string>> GetSystemLabelIdsAsync(
            MailCredential credential,
            Guid accountId,
            CancellationToken cancellationToken = default) => Task.FromResult(Labels);

        public Task<GmailApiInboxPage> GetFolderPageAsync(
            MailCredential credential,
            Guid accountId,
            string labelId,
            bool includeSpamTrash,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            LastLabel = labelId;
            LastIncludeSpamTrash = includeSpamTrash;
            LastPageToken = pageToken;
            return Task.FromResult(new GmailApiInboxPage([], null));
        }

        public Task SetReadStateAsync(
            MailCredential credential,
            Guid accountId,
            string messageId,
            bool isRead,
            CancellationToken cancellationToken = default)
        {
            MutationCount++;
            LastMessageId = messageId;
            LastIsRead = isRead;
            return Task.CompletedTask;
        }

        public Task<GmailApiInboxPage> GetInboxPageAsync(
            MailCredential credential,
            Guid accountId,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            GetFolderPageAsync(credential, accountId, "INBOX", false, pageToken, pageSize, cancellationToken);

        public Task<GmailApiRawMessage> GetRawMessageAsync(
            MailCredential credential,
            Guid accountId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiRawMessage([], false));
    }

    private sealed class FakeImapClient : IImapInboxClient
    {
        public IReadOnlyList<ImapFolderDescriptor> Folders { get; set; } = [new(MailFolderKind.Inbox, "INBOX")];
        public ImapFolderDescriptor? LastFolder { get; private set; }
        public uint LastUid { get; private set; }
        public uint LastUidValidity { get; private set; }
        public bool LastIsRead { get; private set; }
        public int MutationCount { get; private set; }

        public Task<IReadOnlyList<ImapFolderDescriptor>> GetFoldersAsync(
            MailServerSettings server,
            string secret,
            CancellationToken cancellationToken = default) => Task.FromResult(Folders);

        public Task SetReadStateAsync(
            MailServerSettings server,
            string secret,
            ImapFolderDescriptor folder,
            uint uniqueId,
            uint expectedUidValidity,
            bool isRead,
            CancellationToken cancellationToken = default)
        {
            MutationCount++;
            LastFolder = folder;
            LastUid = uniqueId;
            LastUidValidity = expectedUidValidity;
            LastIsRead = isRead;
            return Task.CompletedTask;
        }

        public Task<ImapInboxPageData> GetInboxPageAsync(
            MailServerSettings server,
            string secret,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapInboxPageData([], null));

        public Task<ImapMessageData> GetMessageAsync(
            MailServerSettings server,
            string secret,
            uint uniqueId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMessageData(new MimeMessage(), true));
    }

    private sealed class StatefulFolderProvider : IMailReadProvider, IMailMessageStateProvider
    {
        private readonly Dictionary<string, MailPage<MailMessageSummary>> _pages = [];
        public IReadOnlyList<MailFolder> Folders { get; set; } = [MailFolderCatalog.Inbox()];
        public Dictionary<string, TaskCompletionSource<MailPage<MailMessageSummary>>> DelayedPages { get; } = [];
        public bool MutationFailure { get; set; }
        public int MutationCount { get; private set; }

        public bool Supports(MailProviderType providerType) => true;

        public void SetPage(MailFolder folder, IReadOnlyList<MailMessageSummary> items, string? cursor = null) =>
            _pages[folder.Key] = new MailPage<MailMessageSummary>(items, cursor);

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) => Task.FromResult(Folders);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (DelayedPages.TryGetValue(folder.Key, out TaskCompletionSource<MailPage<MailMessageSummary>>? delayed))
            {
                return delayed.Task;
            }

            return Task.FromResult(_pages.GetValueOrDefault(folder.Key) ?? new MailPage<MailMessageSummary>([], null));
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            bool unread = _pages[folder.Key].Items.Single(item => item.MessageKey == messageKey).IsUnread;
            return Task.FromResult(Content(messageKey, unread));
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
                ? Task.FromException(new MailReadException(MailReadFailureKind.MutationFailed, "Mutation failed."))
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

    private sealed class FolderProviderFactory(
        IMailReadProvider provider,
        IGmailScopeUpgradeService? upgradeService = null) : IMailReadProviderFactory
    {
        public IGmailScopeUpgradeService? GmailScopeUpgradeService { get; } = upgradeService;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class RecordingCredentialStore(MailCredential credential) : IMailCredentialStore
    {
        public MailCredential Value { get; private set; } = credential;
        public int SaveCount { get; private set; }

        public Task SaveAsync(string credentialKey, MailCredential value, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Value = value;
            return Task.CompletedTask;
        }

        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<MailCredential?>(Value);

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOAuthService(string profileEmail, bool success) : IGmailOAuthService
    {
        public string? RequestedScope { get; private set; }
        public bool ProfileFailure { get; set; }

        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(CancellationToken cancellationToken = default) =>
            AuthorizeAsync(GmailOAuthConstants.ReadOnlyScope, cancellationToken);

        public Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
            string scope,
            CancellationToken cancellationToken = default)
        {
            RequestedScope = scope;
            if (!success)
            {
                return Task.FromResult(GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthDenied,
                    "Доступ не предоставлен."));
            }

            return Task.FromResult(GmailOAuthAuthorizationResult.Success(
                new GmailOAuthSession("access", ModifyCredential())));
        }

        public Task<GmailProfileResult> GetProfileAsync(
            GmailOAuthSession session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ProfileFailure
                ? GmailProfileResult.Failure("Profile failed.")
                : GmailProfileResult.Success(new GmailUserProfile(profileEmail, null)));
    }

    private sealed class FakeContentExtractor : IMailContentExtractor
    {
        public MailMessageContent Extract(string messageKey, MimeMessage message, bool isUnread) => Content(messageKey, isUnread);
    }

    private sealed class NoOpConnectionValidator : IMailConnectionValidator
    {
        public Task<MailConnectionValidationResult> ValidateAsync(
            MailConnectionSettings settings,
            string emailAddress,
            string secret,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MailConnectionValidationResult.Success(new MailIdentity(emailAddress, null)));
    }

    private sealed class TestMailProviderFactory(IMailProvider provider) : IMailProviderFactory
    {
        public IReadOnlyList<MailProviderDescriptor> Providers => [provider.Descriptor];
        public IMailProvider Get(MailProviderType providerType) => provider;
    }
}
