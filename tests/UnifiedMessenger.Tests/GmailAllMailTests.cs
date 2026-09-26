using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailAllMailTests
{
    [Fact]
    public void FolderCatalog_DefinesAllMailAsReadableSpecialView()
    {
        MailFolder folder = MailFolderCatalog.Create(MailFolderKind.AllMail, GmailSystemFolders.AllMailView);

        Assert.Equal(MailFolderCatalog.AllMailKey, folder.Key);
        Assert.Equal("All Mail", folder.DisplayName);
        Assert.True(folder.SupportsReadState);
        Assert.Equal(GmailSystemFolders.AllMailView, folder.ProviderLocator);
    }

    [Fact]
    public void GmailFolderMap_PlacesAllMailBetweenDraftsAndSpam()
    {
        IReadOnlyList<MailFolder> folders = GmailSystemFolders.Map(
            GmailSystemFolders.LabelIds.ToHashSet(StringComparer.Ordinal));

        Assert.Equal(
            [MailFolderKind.Inbox, MailFolderKind.Starred, MailFolderKind.Sent, MailFolderKind.Drafts,
                MailFolderKind.AllMail, MailFolderKind.Spam, MailFolderKind.Trash],
            folders.Select(folder => folder.Kind));
    }

    [Fact]
    public void GmailFolderMap_AllMailDoesNotDependOnSyntheticSystemLabel()
    {
        MailFolder folder = Assert.Single(GmailSystemFolders.Map(new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal(MailFolderKind.AllMail, folder.Kind);
        Assert.DoesNotContain(GmailSystemFolders.AllMailView, GmailSystemFolders.LabelIds);
    }

    [Fact]
    public async Task GmailProvider_RoutesAllMailToDedicatedApiPath()
    {
        RecordingApi api = new();
        GmailMailReadProvider provider = Provider(api);
        MailAccount account = Account();

        await provider.GetPageAsync(account, AllMail(), "all-next", 50);

        AllMailCall call = Assert.Single(api.AllMailCalls);
        Assert.Equal(account.Id, call.AccountId);
        Assert.Equal("all-next", call.PageToken);
        Assert.Equal(50, call.PageSize);
        Assert.Empty(api.FolderCalls);
    }

    [Fact]
    public async Task GmailProvider_AllMailNeverPublishesApproximateOrMailboxWideTotal()
    {
        RecordingApi api = new()
        {
            AllMailResult = new GmailApiInboxPage(
                [ApiSummary("message", [GmailSystemFolders.Inbox])],
                "next",
                LabelMessagesTotal: 999,
                ResultSizeEstimate: 888)
        };

        MailPage<MailMessageSummary> page = await Provider(api).GetPageAsync(Account(), AllMail(), null, 50);

        Assert.Null(page.TotalCount);
        Assert.Equal("next", page.ContinuationToken);
    }

    [Fact]
    public async Task GmailProvider_RejectsAllMailKindWithNonSpecialLocator()
    {
        GmailMailReadProvider provider = Provider(new RecordingApi());
        MailFolder forged = MailFolderCatalog.Create(MailFolderKind.AllMail, GmailSystemFolders.Inbox);

        MailReadException exception = await Assert.ThrowsAsync<MailReadException>(
            () => provider.GetPageAsync(Account(), forged, null, 50));

        Assert.Equal(MailReadFailureKind.FolderUnavailable, exception.FailureKind);
    }

    [Fact]
    public void GmailAllMailRequest_HasNoLabelFilter()
    {
        using GmailService service = TestGmailService();
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");

        GmailApiReadClient.ConfigureAllMailRequest(request, "next", 50);

        Assert.Null(request.LabelIds);
    }

    [Fact]
    public void GmailAllMailRequest_ExcludesSpamAndTrashServerSide()
    {
        using GmailService service = TestGmailService();
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");

        GmailApiReadClient.ConfigureAllMailRequest(request, null, 50);

        Assert.False(request.IncludeSpamTrash);
    }

    [Fact]
    public void GmailMessageMetadata_OnlyTreatsMissingListedMessageAsStale()
    {
        GoogleApiException missing = new("Gmail", "synthetic") { HttpStatusCode = HttpStatusCode.NotFound };
        GoogleApiException throttled = new("Gmail", "synthetic") { HttpStatusCode = HttpStatusCode.TooManyRequests };

        Assert.True(GmailApiReadClient.IsStaleListedItem(missing));
        Assert.False(GmailApiReadClient.IsStaleListedItem(throttled));
        Assert.False(GmailApiReadClient.IsStaleListedItem(new HttpRequestException("synthetic")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    public void GmailAllMailRequest_PreservesRequestedPageSize(int pageSize)
    {
        using GmailService service = TestGmailService();
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");

        GmailApiReadClient.ConfigureAllMailRequest(request, null, pageSize);

        Assert.Equal(pageSize, request.MaxResults);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("next-page", "next-page")]
    public void GmailAllMailRequest_NormalizesAndPreservesPageToken(string? input, string? expected)
    {
        using GmailService service = TestGmailService();
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");

        GmailApiReadClient.ConfigureAllMailRequest(request, input, 50);

        Assert.Equal(expected, request.PageToken);
    }

    [Fact]
    public void GmailAllMailDraftMapping_AttachesStableDraftIdentity()
    {
        GmailApiSummaryData draft = ApiSummary("message-draft", [GmailSystemFolders.Draft]);

        GmailApiSummaryData mapped = Assert.Single(GmailApiReadClient.ApplyDraftIdentities(
            [draft],
            new Dictionary<string, string> { ["message-draft"] = "draft-stable" }));

        Assert.Equal("draft-stable", mapped.DraftId);
    }

    [Fact]
    public void GmailAllMailDraftMapping_OmitsUnmatchedDraftRatherThanOpeningItAsMessage()
    {
        GmailApiSummaryData draft = ApiSummary("raced-draft", [GmailSystemFolders.Draft]);

        IReadOnlyList<GmailApiSummaryData> mapped = GmailApiReadClient.ApplyDraftIdentities(
            [draft],
            new Dictionary<string, string>());

        Assert.Empty(mapped);
    }

    [Fact]
    public void GmailAllMailDraftMapping_PreservesServerOrder()
    {
        GmailApiSummaryData first = ApiSummary("first", [GmailSystemFolders.Inbox]);
        GmailApiSummaryData draft = ApiSummary("draft-message", [GmailSystemFolders.Draft]);
        GmailApiSummaryData last = ApiSummary("last", [GmailSystemFolders.Sent]);

        IReadOnlyList<GmailApiSummaryData> mapped = GmailApiReadClient.ApplyDraftIdentities(
            [first, draft, last],
            new Dictionary<string, string> { [draft.Id] = "draft-id" });

        Assert.Equal(["first", "draft-message", "last"], mapped.Select(item => item.Id));
    }

    [Theory]
    [InlineData("INBOX")]
    [InlineData("SENT")]
    [InlineData("STARRED")]
    [InlineData("")]
    public void GmailAllMailDraftMapping_PreservesOrdinaryMailboxMessages(string label)
    {
        IReadOnlyList<string> labels = string.IsNullOrEmpty(label) ? [] : [label];
        GmailApiSummaryData summary = ApiSummary("ordinary", labels);

        GmailApiSummaryData mapped = Assert.Single(GmailApiReadClient.ApplyDraftIdentities(
            [summary],
            new Dictionary<string, string>()));

        Assert.Equal("ordinary", mapped.Id);
        Assert.Null(mapped.DraftId);
    }

    [Fact]
    public void GmailAllMailMapping_AcceptsRestoredMessageWithoutInboxLabel()
    {
        GmailApiSummaryData restored = ApiSummary("restored", ["CATEGORY_UPDATES", "IMPORTANT", GmailSystemFolders.Unread]);

        GmailApiSummaryData mapped = Assert.Single(GmailApiReadClient.ApplyDraftIdentities(
            [restored],
            new Dictionary<string, string>()));
        MailMessageSummary summary = GmailMailReadProvider.MapSummary(mapped);

        Assert.DoesNotContain(GmailSystemFolders.Inbox, summary.ProviderLabelIds);
        Assert.Contains("CATEGORY_UPDATES", summary.ProviderLabelIds);
        Assert.Contains("IMPORTANT", summary.ProviderLabelIds);
        Assert.True(summary.IsUnread);
    }

    [Fact]
    public async Task AllMailPagination_UsesFiftyAndDedicatedContinuationToken()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.AllMail, null, Page(Summaries("all", 50), "all-next"));
        provider.SetPage(account.Id, MailFolderKind.AllMail, "all-next", Page([Summary("all-51")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SelectAllMailAsync(viewModel, account);

        await viewModel.NextPageCommand.ExecuteAsync(null);

        PageCall call = provider.PageCalls.Last();
        Assert.Equal(MailFolderKind.AllMail, call.FolderKind);
        Assert.Equal("all-next", call.PageToken);
        Assert.Equal(50, call.PageSize);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task AllMailPagination_PreviousReturnsToFirstPage()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = PagedAllMail(account);
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SelectAllMailAsync(viewModel, account);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        await viewModel.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal("gmail:all-1", viewModel.Messages[0].MessageKey);
        Assert.False(viewModel.CanNavigateToPreviousPage);
    }

    [Fact]
    public async Task AllMailPagination_IsIndependentFromInboxFolderState()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = PagedAllMail(account);
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page([Summary("inbox")], null, 1));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SelectAllMailAsync(viewModel, account);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);

        Assert.Equal("gmail:all-51", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task SearchFromAllMail_ClearRestoresAllMailPageAndTokenHistory()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = PagedAllMail(account);
        provider.SetSearchPage(account.Id, null, Page([Summary("search-1")], "search-next"));
        provider.SetSearchPage(account.Id, "search-next", Page([Summary("search-51")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SelectAllMailAsync(viewModel, account);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        viewModel.SearchText = "from:example.test";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        viewModel.ClearSearchCommand.Execute(null);

        Assert.Equal(MailFolderKind.AllMail, viewModel.SelectedFolder?.Kind);
        Assert.Equal("gmail:all-51", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task AllMailDetailBack_PreservesCurrentPage()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = PagedAllMail(account);
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SelectAllMailAsync(viewModel, account);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        MailMessageSummary message = Assert.Single(viewModel.Messages);

        viewModel.OpenMessageCommand.Execute(message);
        await viewModel.CurrentMessageLoadTask;
        viewModel.BackToMessageListCommand.Execute(null);

        Assert.True(viewModel.IsMessageListVisible);
        Assert.Equal("gmail:all-51", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task ArchiveFromAllMail_RemovesInboxLabelButRetainsRow()
    {
        (MailInboxViewModel viewModel, RecordingMailbox mailbox) = await MailboxViewModelAsync(
            Summary("inbox-member", labels: [GmailSystemFolders.Inbox]));
        using (viewModel)
        {
            MailMessageSummary message = Assert.Single(viewModel.Messages);
            viewModel.ToggleMessageSelectionCommand.Execute(message);

            await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);

            Assert.Equal(["gmail:inbox-member"], mailbox.ArchivedKeys);
            MailMessageSummary retained = Assert.Single(viewModel.Messages);
            Assert.DoesNotContain(GmailSystemFolders.Inbox, retained.ProviderLabelIds);
        }
    }

    [Fact]
    public async Task ArchiveFromInbox_RemovesInboxRowButKeepsCachedAllMailRow()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page([
            Summary("shared", labels: [GmailSystemFolders.Inbox])
        ], null, 1));
        provider.SetPage(account.Id, MailFolderKind.AllMail, null, Page([
            Summary("shared", labels: [GmailSystemFolders.Inbox])
        ], null));
        RecordingMailbox mailbox = new();
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await SelectAllMailAsync(viewModel, account);
        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);
        MailMessageSummary inboxMessage = Assert.Single(viewModel.Messages);
        viewModel.ToggleMessageSelectionCommand.Execute(inboxMessage);

        await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Messages);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        MailMessageSummary allMailMessage = Assert.Single(viewModel.Messages);
        Assert.DoesNotContain(GmailSystemFolders.Inbox, allMailMessage.ProviderLabelIds);
    }

    [Fact]
    public async Task ArchiveFromAllMail_IsDisabledForAlreadyArchivedOrSentMessage()
    {
        (MailInboxViewModel viewModel, RecordingMailbox mailbox) = await MailboxViewModelAsync(
            Summary("sent", labels: [GmailSystemFolders.Sent]));
        using (viewModel)
        {
            MailMessageSummary message = Assert.Single(viewModel.Messages);
            viewModel.ToggleMessageSelectionCommand.Execute(message);

            Assert.False(viewModel.ArchiveSelectedCommand.CanExecute(null));
            await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);
            Assert.Empty(mailbox.ArchivedKeys);
        }
    }

    [Fact]
    public async Task MixedAllMailArchive_SendsOnlyInboxMembers()
    {
        (MailInboxViewModel viewModel, RecordingMailbox mailbox) = await MailboxViewModelAsync(
            Summary("inbox", labels: [GmailSystemFolders.Inbox]),
            Summary("archived", labels: []));
        using (viewModel)
        {
            foreach (MailMessageSummary message in viewModel.Messages)
            {
                viewModel.ToggleMessageSelectionCommand.Execute(message);
            }

            await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);

            Assert.Equal(["gmail:inbox"], mailbox.ArchivedKeys);
            Assert.Equal(2, viewModel.Messages.Count);
        }
    }

    [Fact]
    public async Task TrashFromAllMail_RemovesSuccessfulRow()
    {
        (MailInboxViewModel viewModel, RecordingMailbox mailbox) = await MailboxViewModelAsync(Summary("trash-me"));
        using (viewModel)
        {
            MailMessageSummary message = Assert.Single(viewModel.Messages);
            viewModel.ToggleMessageSelectionCommand.Execute(message);

            await viewModel.DeleteSelectedCommand.ExecuteAsync(null);

            Assert.Equal(["gmail:trash-me"], mailbox.TrashedKeys);
            Assert.Empty(viewModel.Messages);
        }
    }

    [Fact]
    public async Task StarFromAllMail_RetainsRowAndUpdatesState()
    {
        (MailInboxViewModel viewModel, _) = await MailboxViewModelAsync(Summary("star-me", isStarred: false));
        using (viewModel)
        {
            MailMessageSummary message = Assert.Single(viewModel.Messages);

            await viewModel.ToggleStarCommand.ExecuteAsync(message);

            Assert.True(Assert.Single(viewModel.Messages).IsStarred);
        }
    }

    [Fact]
    public async Task ReadFromAllMail_RetainsRowAndUpdatesInboxBadgeOnlyForInboxMember()
    {
        MailMessageSummary inbox = Summary("inbox-unread", labels: [GmailSystemFolders.Inbox], isUnread: true);
        (MailInboxViewModel viewModel, _) = await MailboxViewModelAsync(inbox);
        using (viewModel)
        {
            viewModel.ActiveAccount!.InboxUnreadCount = 3;
            MailMessageSummary message = Assert.Single(viewModel.Messages);
            viewModel.ToggleMessageSelectionCommand.Execute(message);

            await viewModel.MarkSelectedReadCommand.ExecuteAsync(null);

            Assert.False(Assert.Single(viewModel.Messages).IsUnread);
            Assert.Equal(2, viewModel.ActiveAccount.InboxUnreadCount);
        }
    }

    [Fact]
    public async Task UserLabelFromAllMail_RetainsRow()
    {
        (MailInboxViewModel viewModel, RecordingMailbox mailbox) = await MailboxViewModelAsync(Summary("label-me"));
        using (viewModel)
        {
            MailMessageSummary message = Assert.Single(viewModel.Messages);
            viewModel.ToggleMessageSelectionCommand.Execute(message);
            await viewModel.OpenLabelsForSelectionCommand.ExecuteAsync(null);

            await viewModel.ToggleUserLabelCommand.ExecuteAsync(Assert.Single(viewModel.UserLabels));

            Assert.Equal(["gmail:label-me"], mailbox.LabelKeys);
            Assert.Single(viewModel.Messages);
        }
    }

    [Fact]
    public async Task AllMailDraft_DisablesSelectionAndMailboxActions()
    {
        MailMessageSummary draft = Summary("draft-message", labels: [GmailSystemFolders.Draft]) with
        {
            ProviderDraftId = "stable-draft"
        };
        (MailInboxViewModel viewModel, _) = await MailboxViewModelAsync(draft);
        using (viewModel)
        {
            MailMessageSummary message = Assert.Single(viewModel.Messages);

            Assert.False(viewModel.ToggleMessageSelectionCommand.CanExecute(message));
            Assert.False(viewModel.ToggleStarCommand.CanExecute(message));
            viewModel.ToggleMessageSelectionCommand.Execute(message);
            Assert.False(message.IsSelected);
        }
    }

    [Fact]
    public async Task AllMailDraft_OpenUsesDraftPathAndNeverLoadsOrdinaryMessageBody()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.AllMail, null, Page([
            Summary("draft-message", labels: [GmailSystemFolders.Draft]) with { ProviderDraftId = "draft-id" }
        ], null));
        using MailInboxViewModel viewModel = ViewModel(provider, new RecordingMailbox());
        await SelectAllMailAsync(viewModel, account);

        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;

        Assert.Equal(0, provider.MessageBodyLoads);
        Assert.False(viewModel.IsMessageDetailVisible);
    }

    [Fact]
    public async Task AllMailState_IsIsolatedBetweenGmailAccounts()
    {
        MailAccount first = Account();
        MailAccount second = Account();
        AllMailReadProvider provider = PagedAllMail(first);
        provider.SetPage(second.Id, MailFolderKind.Inbox, null, Page([], null, 0));
        provider.SetPage(second.Id, MailFolderKind.AllMail, null, Page([Summary("second-all")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SelectAllMailAsync(viewModel, first);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        await SelectAllMailAsync(viewModel, second);
        Assert.Equal("gmail:second-all", Assert.Single(viewModel.Messages).MessageKey);
        await SelectAllMailAsync(viewModel, first);

        Assert.Equal("gmail:all-51", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task ExistingNewMailSignal_MarksAllMailStaleWithoutReloadingItInBackground()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page([], null, 0));
        provider.SetPage(account.Id, MailFolderKind.AllMail, null, Page([Summary("existing")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SelectAllMailAsync(viewModel, account);
        int callsBeforeSignal = provider.PageCalls.Count;

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: false);

        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));
        Assert.Equal(callsBeforeSignal, provider.PageCalls.Count);
    }

    [Fact]
    public async Task AllMailAuthorizationFailure_UsesExistingGmailReauthenticationState()
    {
        MailAccount account = Account();
        AllMailReadProvider provider = new()
        {
            AllMailException = new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google.")
        };
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page([], null, 0));
        using MailInboxViewModel viewModel = ViewModel(provider);

        await SelectAllMailAsync(viewModel, account);

        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.Equal(MailReadFailureKind.ReauthorizationRequired, viewModel.FailureKind);
    }

    [Fact]
    public void AllMailNavigation_HasDedicatedIconAndNoFakeAllMailLabel()
    {
        string xaml = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));

        Assert.Contains("MailFolderAllMailGeometry", xaml, StringComparison.Ordinal);
        Assert.Contains("MailFolderKind.AllMail", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ALL_MAIL", GmailSystemFolders.AllMailView, StringComparison.Ordinal);
    }

    private static GmailService TestGmailService() =>
        new(new BaseClientService.Initializer { ApplicationName = "Lantern tests" });

    private static GmailMailReadProvider Provider(RecordingApi api) =>
        new(new CredentialStore(), api, new MailContentExtractor(new MailHtmlSanitizer()));

    private static MailFolder AllMail() =>
        MailFolderCatalog.Create(MailFolderKind.AllMail, GmailSystemFolders.AllMailView);

    private static GmailApiSummaryData ApiSummary(string id, IReadOnlyList<string> labels) =>
        new(id, "Subject", "Sender <sender@example.test>", 1, "Preview", labels);

    private static MailAccount Account() => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Gmail,
        EmailAddress = $"{Guid.NewGuid():N}@example.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        IsEnabled = true
    };

    private static MailMessageSummary Summary(
        string id,
        IReadOnlyCollection<string>? labels = null,
        bool isUnread = false,
        bool isStarred = false)
    {
        HashSet<string> providerLabels = labels?.ToHashSet(StringComparer.Ordinal) ?? [];
        if (isUnread)
        {
            providerLabels.Add(GmailSystemFolders.Unread);
        }
        if (isStarred)
        {
            providerLabels.Add(GmailSystemFolders.Starred);
        }
        return new MailMessageSummary(
            $"gmail:{id}",
            $"Subject {id}",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            isUnread)
        {
            IsStarred = isStarred,
            ProviderLabelIds = providerLabels
        };
    }

    private static IReadOnlyList<MailMessageSummary> Summaries(string prefix, int count) =>
        Enumerable.Range(1, count).Select(index => Summary($"{prefix}-{index}")).ToArray();

    private static MailPage<MailMessageSummary> Page(
        IReadOnlyList<MailMessageSummary> items,
        string? continuationToken,
        long? total = null) =>
        new(items, continuationToken, total);

    private static AllMailReadProvider PagedAllMail(MailAccount account)
    {
        AllMailReadProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page([], null, 0));
        provider.SetPage(account.Id, MailFolderKind.AllMail, null, Page(Summaries("all", 50), "all-next"));
        provider.SetPage(account.Id, MailFolderKind.AllMail, "all-next", Page([Summary("all-51")], null));
        return provider;
    }

    private static MailInboxViewModel ViewModel(
        AllMailReadProvider provider,
        RecordingMailbox? mailbox = null) =>
        new(new ProviderFactory(provider, mailbox));

    private static async Task SelectAllMailAsync(MailInboxViewModel viewModel, MailAccount account)
    {
        if (viewModel.ActiveAccount?.Id != account.Id)
        {
            await viewModel.ActivateAsync(account);
        }
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
    }

    private static async Task SelectFolderAsync(MailInboxViewModel viewModel, MailFolderKind kind)
    {
        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind == kind);
        await viewModel.CurrentFolderLoadTask;
    }

    private static async Task<(MailInboxViewModel ViewModel, RecordingMailbox Mailbox)> MailboxViewModelAsync(
        params MailMessageSummary[] messages)
    {
        MailAccount account = Account();
        AllMailReadProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page([], null, 0));
        provider.SetPage(account.Id, MailFolderKind.AllMail, null, Page(messages, null));
        RecordingMailbox mailbox = new();
        MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await SelectAllMailAsync(viewModel, account);
        return (viewModel, mailbox);
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativePath));
    }

    private sealed class CredentialStore : IMailCredentialStore
    {
        private static readonly MailCredential Credential = MailCredential.CreateGmailOAuth(
            "refresh",
            "client",
            "secret",
            GmailOAuthConstants.ModifyScope);

        public Task SaveAsync(string credentialKey, MailCredential credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<MailCredential?>(Credential);

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingApi : IGmailApiReadClient
    {
        public List<AllMailCall> AllMailCalls { get; } = [];
        public List<string> FolderCalls { get; } = [];
        public GmailApiInboxPage AllMailResult { get; init; } = new([], null);

        public Task<GmailApiInboxPage> GetAllMailPageAsync(
            MailCredential credential,
            Guid accountId,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            AllMailCalls.Add(new AllMailCall(accountId, pageToken, pageSize));
            return Task.FromResult(AllMailResult);
        }

        public Task<GmailApiInboxPage> GetFolderPageAsync(
            MailCredential credential,
            Guid accountId,
            string labelId,
            bool includeSpamTrash,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            FolderCalls.Add(labelId);
            return Task.FromResult(new GmailApiInboxPage([], null));
        }

        public Task<GmailApiInboxPage> GetInboxPageAsync(
            MailCredential credential,
            Guid accountId,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiInboxPage([], null));

        public Task<GmailApiRawMessage> GetRawMessageAsync(
            MailCredential credential,
            Guid accountId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiRawMessage([], false));
    }

    private sealed class AllMailReadProvider : IMailReadProvider, IMailSearchProvider, IMailMessageStateProvider
    {
        private readonly Dictionary<(Guid AccountId, MailFolderKind Kind, string Token), MailPage<MailMessageSummary>> _pages = [];
        private readonly Dictionary<(Guid AccountId, string Token), MailPage<MailMessageSummary>> _searchPages = [];

        public List<PageCall> PageCalls { get; } = [];
        public int MessageBodyLoads { get; private set; }
        public Exception? AllMailException { get; init; }

        public void SetPage(Guid accountId, MailFolderKind kind, string? token, MailPage<MailMessageSummary> page) =>
            _pages[(accountId, kind, token ?? string.Empty)] = page;

        public void SetSearchPage(Guid accountId, string? token, MailPage<MailMessageSummary> page) =>
            _searchPages[(accountId, token ?? string.Empty)] = page;

        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([
                MailFolderCatalog.Inbox(),
                AllMail(),
                MailFolderCatalog.Create(MailFolderKind.Sent, GmailSystemFolders.Sent)
            ]);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageCalls.Add(new PageCall(account.Id, folder.Kind, continuationToken, pageSize));
            if (folder.Kind is MailFolderKind.AllMail && AllMailException is Exception exception)
            {
                return Task.FromException<MailPage<MailMessageSummary>>(exception);
            }
            return Task.FromResult(_pages.GetValueOrDefault(
                (account.Id, folder.Kind, continuationToken ?? string.Empty),
                Page([], null)));
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Inbox(), continuationToken, pageSize, cancellationToken);

        public Task<MailPage<MailMessageSummary>> SearchAsync(
            MailAccount account,
            string query,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_searchPages.GetValueOrDefault(
                (account.Id, continuationToken ?? string.Empty),
                Page([], null)));

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            MessageBodyLoads++;
            return Task.FromResult(new MailMessageContent(
                messageKey,
                "Subject",
                "Sender",
                "sender@example.test",
                account.EmailAddress,
                DateTimeOffset.UtcNow,
                MailMessageBodyKind.PlainText,
                "Body",
                [],
                true,
                false));
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            GetMessageAsync(account, MailFolderCatalog.Inbox(), messageKey, cancellationToken);

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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ProviderFactory(AllMailReadProvider provider, RecordingMailbox? mailbox) : IMailReadProviderFactory
    {
        public IGmailMailboxManagementService? GmailMailboxManagementService => mailbox;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class RecordingMailbox : IGmailMailboxManagementService
    {
        public IReadOnlyList<string> ArchivedKeys { get; private set; } = [];
        public IReadOnlyList<string> TrashedKeys { get; private set; } = [];
        public IReadOnlyList<string> LabelKeys { get; private set; } = [];

        public Task<GmailUserLabelResult> GetUserLabelsAsync(
            MailAccount account,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailUserLabelResult([new GmailUserLabel("Label_Test", "Test")]));

        public Task<GmailMailboxMutationResult> SetStarredAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            bool isStarred,
            CancellationToken cancellationToken = default) =>
            Success(messageKeys);

        public Task<GmailMailboxMutationResult> SetReadStateAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            bool isRead,
            CancellationToken cancellationToken = default) =>
            Success(messageKeys);

        public Task<GmailMailboxMutationResult> ArchiveAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            CancellationToken cancellationToken = default)
        {
            ArchivedKeys = messageKeys.ToArray();
            return Success(messageKeys);
        }

        public Task<GmailMailboxMutationResult> MoveToTrashAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            CancellationToken cancellationToken = default)
        {
            TrashedKeys = messageKeys.ToArray();
            return Success(messageKeys);
        }

        public Task<GmailMailboxMutationResult> RestoreFromTrashAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            CancellationToken cancellationToken = default) =>
            Success(messageKeys);

        public Task<GmailMailboxMutationResult> MarkNotSpamAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            CancellationToken cancellationToken = default) =>
            Success(messageKeys);

        public Task<GmailMailboxMutationResult> ReportSpamAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            CancellationToken cancellationToken = default) =>
            Success(messageKeys);

        public Task<GmailMailboxMutationResult> SetUserLabelAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            string labelId,
            bool isApplied,
            CancellationToken cancellationToken = default)
        {
            LabelKeys = messageKeys.ToArray();
            return Success(messageKeys);
        }

        public void RemoveAccount(Guid accountId)
        {
        }

        private static Task<GmailMailboxMutationResult> Success(IReadOnlyCollection<string> keys) =>
            Task.FromResult(new GmailMailboxMutationResult(keys.ToArray(), []));
    }

    private sealed record AllMailCall(Guid AccountId, string? PageToken, int PageSize);
    private sealed record PageCall(Guid AccountId, MailFolderKind FolderKind, string? PageToken, int PageSize);
}
