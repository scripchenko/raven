using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailUserLabelNavigationTests
{
    [Fact]
    public void LabelCatalog_ExcludesSystemLabelsFromUserLabels()
    {
        GmailApiLabelCatalog catalog = GmailApiReadClient.MapLabelCatalog(
        [
            new Label { Id = GmailSystemFolders.Inbox, Name = "INBOX", Type = "system" },
            new Label { Id = "Label_Work", Name = "Работа", Type = "user" }
        ]);

        Assert.Equal([GmailSystemFolders.Inbox], catalog.SystemLabelIds);
        Assert.Equal("Label_Work", Assert.Single(catalog.UserLabels).Id);
    }

    [Fact]
    public void LabelCatalog_KeepsOpaqueIdSeparateFromDisplayName()
    {
        GmailApiUserLabel label = Assert.Single(GmailApiReadClient.MapLabelCatalog(
            [new Label { Id = "opaque/id:42", Name = "Клиенты", Type = "user" }]).UserLabels);

        Assert.Equal("opaque/id:42", label.Id);
        Assert.Equal("Клиенты", label.Name);
    }

    [Fact]
    public void LabelCatalog_UsesStableNaturalCaseInsensitiveOrder()
    {
        GmailApiLabelCatalog catalog = GmailApiReadClient.MapLabelCatalog(
        [
            new Label { Id = "3", Name = "Проект 10", Type = "user" },
            new Label { Id = "1", Name = "проект 2", Type = "user" },
            new Label { Id = "2", Name = "Проект 1", Type = "user" }
        ]);

        Assert.Equal(["Проект 1", "проект 2", "Проект 10"], catalog.UserLabels.Select(label => label.Name));
    }

    [Fact]
    public void LabelCatalog_DeduplicatesRepeatedOpaqueIds()
    {
        GmailApiLabelCatalog catalog = GmailApiReadClient.MapLabelCatalog(
        [
            new Label { Id = "same", Name = "Первый", Type = "user" },
            new Label { Id = "same", Name = "Второй", Type = "user" }
        ]);

        Assert.Single(catalog.UserLabels);
    }

    [Fact]
    public void FolderCatalog_ShowsOnlyFirstUserLabelSectionHeader()
    {
        IReadOnlyList<MailFolder> folders = GmailSystemFolders.Map(Catalog(
            new GmailApiUserLabel("b", "Бета"),
            new GmailApiUserLabel("a", "Альфа")));

        MailFolder[] labels = folders.Where(folder => folder.IsUserLabel).ToArray();
        Assert.True(labels[0].ShowsUserLabelSectionHeader);
        Assert.False(labels[1].ShowsUserLabelSectionHeader);
    }

    [Fact]
    public void FolderCatalog_PreservesSlashNameAsSafeFullName()
    {
        MailFolder folder = GmailSystemFolders.Map(Catalog(
            new GmailApiUserLabel("company-a", "Клиенты/Компания A")))
            .Single(item => item.IsUserLabel);

        Assert.Equal("Клиенты/Компания A", folder.DisplayName);
    }

    [Fact]
    public void FolderCatalog_DistinguishesSameLeafInDifferentPaths()
    {
        MailFolder[] folders = GmailSystemFolders.Map(Catalog(
                new GmailApiUserLabel("a", "Клиенты/Общие"),
                new GmailApiUserLabel("b", "Работа/Общие")))
            .Where(folder => folder.IsUserLabel)
            .ToArray();

        Assert.Equal(2, folders.Select(folder => folder.DisplayName).Distinct().Count());
        Assert.Equal(2, folders.Select(folder => folder.Key).Distinct().Count());
    }

    [Fact]
    public void FolderCatalog_SameDisplayNamesStillHaveDistinctIdentity()
    {
        MailFolder[] folders = GmailSystemFolders.Map(Catalog(
                new GmailApiUserLabel("first", "Общие"),
                new GmailApiUserLabel("second", "Общие")))
            .Where(folder => folder.IsUserLabel)
            .ToArray();

        Assert.NotEqual(folders[0].Key, folders[1].Key);
        Assert.Equal(["first", "second"], folders.Select(folder => folder.ProviderLocator).Order());
    }

    [Fact]
    public void FolderRequest_UsesExactLabelIdAndPageSize()
    {
        using GmailService service = TestGmailService();
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");

        GmailApiReadClient.ConfigureFolderRequest(request, "opaque-label", false, "next-token", 50);

        Assert.Equal(["opaque-label"], request.LabelIds);
        Assert.Equal(50, request.MaxResults);
        Assert.Equal("next-token", request.PageToken);
    }

    [Fact]
    public void UserLabelRequest_ExcludesSpamAndTrash()
    {
        using GmailService service = TestGmailService();
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");

        GmailApiReadClient.ConfigureFolderRequest(request, "Label_Work", false, null, 50);

        Assert.False(request.IncludeSpamTrash);
    }

    [Fact]
    public void UserLabelTotal_DoesNotTriggerPerLabelGet()
    {
        Assert.False(GmailApiReadClient.SupportsExactLabelTotal("Label_Work"));
        Assert.True(GmailApiReadClient.SupportsExactLabelTotal(GmailSystemFolders.Inbox));
    }

    [Fact]
    public void DeletedUserLabelFailure_IsClassifiedWithoutChangingSystemFolderFailures()
    {
        Google.GoogleApiException missing = new("gmail", "missing")
        {
            HttpStatusCode = System.Net.HttpStatusCode.NotFound
        };

        Assert.True(GmailApiReadClient.IsUnavailableUserLabelFailure(missing, "Label_Removed"));
        Assert.False(GmailApiReadClient.IsUnavailableUserLabelFailure(missing, GmailSystemFolders.Inbox));
    }

    [Fact]
    public async Task SelectingUserLabel_LoadsExactProviderLocator()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "work");

        Assert.Equal("work", context.Provider.Calls.Last().Locator);
    }

    [Fact]
    public async Task UserLabelPageSize_IsFifty()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "work");

        Assert.Equal(50, context.Provider.Calls.Last().PageSize);
    }

    [Fact]
    public async Task EmptyUserLabel_UsesLabelSpecificEmptyState()
    {
        TestContext context = await CreateAsync(("empty", "Пусто"));
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "empty");

        Assert.True(viewModel.IsEmpty);
        Assert.Equal("В этом ярлыке нет писем.", viewModel.EmptyListMessage);
    }

    [Fact]
    public async Task UserLabelTotal_IsRangeOnly()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        context.Provider.SetPage(context.Account.Id, "work", null, Page([Summary("one", "work")], null));
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "work");

        Assert.Equal("1–1", viewModel.PageRangeText);
    }

    [Fact]
    public async Task Pagination_IsIndependentPerUserLabel()
    {
        TestContext context = await CreateAsync(("a", "A"), ("b", "B"));
        context.Provider.SetPage(context.Account.Id, "a", null, Page(Summaries("a", "a", 50), "a-next"));
        context.Provider.SetPage(context.Account.Id, "a", "a-next", Page([Summary("a-51", "a")], null));
        context.Provider.SetPage(context.Account.Id, "b", null, Page([Summary("b-1", "b")], null));
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "a");
        await viewModel.NextPageCommand.ExecuteAsync(null);
        await SelectLabelAsync(viewModel, "b");
        await SelectLabelAsync(viewModel, "a");

        Assert.Equal("gmail:a-51", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task ArchivedMessage_RemainsVisibleWhenServerReturnsUserLabel()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        context.Provider.SetPage(context.Account.Id, "work", null, Page([Summary("archived", "work")], null));
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "work");

        Assert.Single(viewModel.Messages);
        Assert.DoesNotContain(GmailSystemFolders.Inbox, viewModel.Messages[0].ProviderLabelIds);
    }

    [Fact]
    public async Task SentMessage_IsVisibleWhenServerReturnsUserLabel()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        context.Provider.SetPage(context.Account.Id, "work", null, Page([
            Summary("sent", "work", GmailSystemFolders.Sent)
        ], null));
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "work");

        Assert.Contains(GmailSystemFolders.Sent, Assert.Single(viewModel.Messages).ProviderLabelIds);
    }

    [Fact]
    public async Task RemoveCurrentLabel_RemovesRowImmediately()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;
        SelectAll(viewModel);

        await viewModel.OpenLabelsForSelectionCommand.ExecuteAsync(null);
        await viewModel.ToggleUserLabelCommand.ExecuteAsync(
            viewModel.UserLabels.Single(label => label.Id == "work"));

        Assert.Empty(viewModel.Messages);
        Assert.Equal(("work", false), context.Mailbox.LabelMutations.Last());
    }

    [Fact]
    public async Task RemoveCurrentLabel_MultiSelectRemovesEverySuccessfulRow()
    {
        TestContext context = await LoadedLabelAsync(
            "work",
            Summary("one", "work"),
            Summary("two", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;
        SelectAll(viewModel);

        await viewModel.OpenLabelsForSelectionCommand.ExecuteAsync(null);
        await viewModel.ToggleUserLabelCommand.ExecuteAsync(
            viewModel.UserLabels.Single(label => label.Id == "work"));

        Assert.Empty(viewModel.Messages);
        Assert.Equal(2, context.Mailbox.LastMessageKeys.Count);
    }

    [Fact]
    public async Task AddOtherLabel_RetainsCurrentRowAndMarksTargetStale()
    {
        TestContext context = await CreateAsync(("a", "A"), ("b", "B"));
        context.Provider.SetPage(context.Account.Id, "a", null, Page([Summary("one", "a")], null));
        context.Provider.SetPage(context.Account.Id, "b", null, Page([], null));
        using MailInboxViewModel viewModel = context.ViewModel;
        await SelectLabelAsync(viewModel, "b");
        await SelectLabelAsync(viewModel, "a");
        SelectAll(viewModel);

        await viewModel.OpenLabelsForSelectionCommand.ExecuteAsync(null);
        await viewModel.ToggleUserLabelCommand.ExecuteAsync(
            viewModel.UserLabels.Single(label => label.Id == "b"));

        Assert.Single(viewModel.Messages);
        Assert.True(viewModel.IsUserLabelFolderStateStale(context.Account.Id, "b"));
    }

    [Fact]
    public async Task DeleteFromUserLabel_RemovesRowWithoutRemovingUserLabel()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;
        SelectAll(viewModel);

        await viewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Messages);
        Assert.Empty(context.Mailbox.LabelMutations);
        Assert.Equal("Trash", context.Mailbox.LastOperation);
    }

    [Fact]
    public async Task ArchiveFromUserLabel_RetainsRowAndUserLabel()
    {
        TestContext context = await LoadedLabelAsync(
            "work",
            Summary("one", "work", GmailSystemFolders.Inbox));
        using MailInboxViewModel viewModel = context.ViewModel;
        SelectAll(viewModel);

        await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);

        MailMessageSummary retained = Assert.Single(viewModel.Messages);
        Assert.Contains("work", retained.ProviderLabelIds);
        Assert.DoesNotContain(GmailSystemFolders.Inbox, retained.ProviderLabelIds);
    }

    [Fact]
    public async Task StarInUserLabel_RetainsRowAndUpdatesState()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;

        await viewModel.ToggleStarCommand.ExecuteAsync(Assert.Single(viewModel.Messages));

        Assert.True(Assert.Single(viewModel.Messages).IsStarred);
    }

    [Fact]
    public async Task ReadInUserLabel_RetainsRowAndUpdatesState()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work", isUnread: true));
        using MailInboxViewModel viewModel = context.ViewModel;
        SelectAll(viewModel);

        await viewModel.MarkSelectedReadCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(viewModel.Messages).IsUnread);
    }

    [Fact]
    public async Task G10FolderSpecificActions_AreHiddenInUserLabel()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;

        Assert.False(viewModel.ShowReportSpamAction);
        Assert.False(viewModel.ShowNotSpamAction);
        Assert.False(viewModel.ShowRestoreAction);
    }

    [Fact]
    public async Task DetailBack_ReturnsToSameUserLabelAndPage()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        context.Provider.SetPage(context.Account.Id, "work", null, Page(Summaries("item", "work", 50), "next"));
        context.Provider.SetPage(context.Account.Id, "work", "next", Page([Summary("last", "work")], null));
        using MailInboxViewModel viewModel = context.ViewModel;
        await SelectLabelAsync(viewModel, "work");
        await viewModel.NextPageCommand.ExecuteAsync(null);

        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;
        viewModel.BackToMessageListCommand.Execute(null);

        Assert.Equal("work", viewModel.SelectedFolder?.ProviderLocator);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task SearchClear_ReturnsToSameUserLabelAndPage()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        context.Provider.SetPage(context.Account.Id, "work", null, Page(Summaries("item", "work", 50), "next"));
        context.Provider.SetPage(context.Account.Id, "work", "next", Page([Summary("last", "work")], null));
        context.Provider.SearchPage = Page([Summary("search")], null);
        using MailInboxViewModel viewModel = context.ViewModel;
        await SelectLabelAsync(viewModel, "work");
        await viewModel.NextPageCommand.ExecuteAsync(null);
        viewModel.SearchText = "from:test@example.test";
        await viewModel.SearchCommand.ExecuteAsync(null);

        viewModel.ClearSearchCommand.Execute(null);

        Assert.Equal("work", viewModel.SelectedFolder?.ProviderLocator);
        Assert.Equal("gmail:last", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("51–51", viewModel.PageRangeText);
    }

    [Fact]
    public async Task AccountSwitch_KeepsLabelCatalogsAndFolderStatesIsolated()
    {
        MailAccount first = Account(1);
        MailAccount second = Account(2);
        LabelReadProvider provider = new();
        provider.SetLabels(first.Id, ("same", "Первый аккаунт"));
        provider.SetLabels(second.Id, ("same", "Второй аккаунт"));
        provider.SetPage(first.Id, "same", null, Page([Summary("first", "same")], null));
        provider.SetPage(second.Id, "same", null, Page([Summary("second", "same")], null));
        LabelMailboxService mailbox = new();
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);

        await viewModel.ActivateAsync(first);
        await SelectLabelAsync(viewModel, "same");
        await viewModel.ActivateAsync(second);
        await SelectLabelAsync(viewModel, "same");

        Assert.Equal("Второй аккаунт", viewModel.SelectedFolder?.DisplayName);
        Assert.Equal("gmail:second", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal(2, mailbox.CatalogsByAccount.Count);
    }

    [Fact]
    public async Task NewMailSignal_MarksUserLabelsStaleWithoutBackgroundLoad()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;
        int callsBefore = context.Provider.Calls.Count;

        viewModel.OnNewMailDetected(context.Account.Id, isAccountActivelyViewed: false);

        Assert.True(viewModel.IsUserLabelFolderStateStale(context.Account.Id, "work"));
        Assert.Equal(callsBefore, context.Provider.Calls.Count);
    }

    [Fact]
    public async Task ManualRefresh_RefreshesLabelCatalogAndFallsBackFromDeletedLabel()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;
        context.Provider.SetLabels(context.Account.Id);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(MailFolderKind.Inbox, viewModel.SelectedFolder?.Kind);
        Assert.DoesNotContain(viewModel.Folders, folder => folder.IsUserLabel);
        Assert.Contains("больше недоступен", viewModel.MailboxActionErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeletedLabelApiFailure_RefreshesCatalogAndFallsBackSafely()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        context.Provider.FailuresByLocator["work"] = new MailReadException(
            MailReadFailureKind.FolderUnavailable,
            "Этот ярлык Gmail больше недоступен.");
        context.Provider.SetLabels(context.Account.Id);
        using MailInboxViewModel viewModel = context.ViewModel;

        await SelectLabelAsync(viewModel, "work");

        Assert.Equal(MailFolderKind.Inbox, viewModel.SelectedFolder?.Kind);
        Assert.DoesNotContain(viewModel.Folders, folder => folder.IsUserLabel);
        Assert.Contains("больше недоступен", viewModel.MailboxActionErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteRename_UpdatesDisplayNameWithoutChangingLabelIdentity()
    {
        TestContext context = await LoadedLabelAsync("work", Summary("one", "work"));
        using MailInboxViewModel viewModel = context.ViewModel;
        string originalKey = viewModel.SelectedFolder!.Key;
        context.Provider.SetLabels(context.Account.Id, ("work", "Новая работа"));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(originalKey, viewModel.SelectedFolder?.Key);
        Assert.Equal("Новая работа", viewModel.SelectedFolder?.DisplayName);
    }

    [Fact]
    public async Task CatalogRefresh_DoesNotLoadEveryLabelPage()
    {
        TestContext context = await CreateAsync(("a", "A"), ("b", "B"), ("c", "C"));
        using MailInboxViewModel viewModel = context.ViewModel;
        int pageCallsBefore = context.Provider.Calls.Count;
        int catalogCallsBefore = context.Provider.FolderCatalogLoads;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(catalogCallsBefore + 1, context.Provider.FolderCatalogLoads);
        Assert.Equal(pageCallsBefore + 1, context.Provider.Calls.Count);
        Assert.DoesNotContain(context.Provider.Calls, call => call.Locator is "a" or "b" or "c");
    }

    [Fact]
    public async Task LabelCatalogFromInitialFolderLoad_PrimesMailboxActionCache()
    {
        TestContext context = await CreateAsync(("work", "Работа"));
        using MailInboxViewModel viewModel = context.ViewModel;

        Assert.Equal("Работа", Assert.Single(context.Mailbox.CatalogsByAccount[context.Account.Id]).DisplayName);
        Assert.Equal(0, context.Mailbox.ForcedCatalogLoads);
    }

    [Fact]
    public void DraftIdentityMapping_IsReusableForLabeledDraftMessages()
    {
        GmailApiSummaryData draft = new("message", "Draft", "", 1, "", ["work", GmailSystemFolders.Draft]);

        GmailApiSummaryData mapped = Assert.Single(GmailApiReadClient.ApplyDraftIdentities(
            [draft],
            new Dictionary<string, string> { ["message"] = "draft-id" }));

        Assert.Equal("draft-id", mapped.DraftId);
        Assert.Contains("work", mapped.LabelIds);
    }

    [Fact]
    public void StaleListedMessage404_RemainsSkippableForLabelPages()
    {
        Google.GoogleApiException missing = new("gmail", "missing")
        {
            HttpStatusCode = System.Net.HttpStatusCode.NotFound
        };

        Assert.True(GmailApiReadClient.IsStaleListedItem(missing));
    }

    [Fact]
    public void ExistingRateLimitRetryContract_RemainsBounded()
    {
        Assert.Equal(2, GmailApiReadClient.MaximumRateLimitRetries);
    }

    [Fact]
    public void SidebarXaml_ContainsLabelSectionIconTooltipAndEllipsis()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));

        Assert.Contains("Text=\"Ярлыки\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MailFolderKind.UserLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("MailFolderLabelGeometry", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding DisplayName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
    }

    private static GmailApiLabelCatalog Catalog(params GmailApiUserLabel[] labels) =>
        new(new HashSet<string>(GmailSystemFolders.LabelIds, StringComparer.Ordinal), labels);

    private static GmailService TestGmailService() =>
        new(new BaseClientService.Initializer { ApplicationName = "Lantern tests" });

    private static MailAccount Account(int number = 1) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
        Provider = MailProviderType.Gmail,
        EmailAddress = $"account{number}@gmail.test",
        CredentialKey = $"credential-{number}",
        IsEnabled = true
    };

    private static MailMessageSummary Summary(
        string id,
        string? userLabel = null,
        string? systemLabel = null,
        bool isUnread = false)
    {
        HashSet<string> labels = [];
        if (userLabel is not null)
        {
            labels.Add(userLabel);
        }
        if (systemLabel is not null)
        {
            labels.Add(systemLabel);
        }
        if (isUnread)
        {
            labels.Add(GmailSystemFolders.Unread);
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
            ProviderLabelIds = labels
        };
    }

    private static IReadOnlyList<MailMessageSummary> Summaries(string prefix, string label, int count) =>
        Enumerable.Range(1, count).Select(index => Summary($"{prefix}-{index}", label)).ToArray();

    private static MailPage<MailMessageSummary> Page(
        IReadOnlyList<MailMessageSummary> messages,
        string? nextToken) =>
        new(messages, nextToken);

    private static MailInboxViewModel ViewModel(LabelReadProvider provider, LabelMailboxService mailbox) =>
        new(new ProviderFactory(provider, mailbox));

    private static async Task<TestContext> CreateAsync(params (string Id, string Name)[] labels)
    {
        MailAccount account = Account();
        LabelReadProvider provider = new();
        provider.SetLabels(account.Id, labels);
        provider.SetPage(account.Id, GmailSystemFolders.Inbox, null, Page([], null));
        LabelMailboxService mailbox = new();
        MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        return new TestContext(account, provider, mailbox, viewModel);
    }

    private static async Task<TestContext> LoadedLabelAsync(
        string labelId,
        params MailMessageSummary[] messages)
    {
        TestContext context = await CreateAsync((labelId, "Работа"));
        context.Provider.SetPage(context.Account.Id, labelId, null, Page(messages, null));
        await SelectLabelAsync(context.ViewModel, labelId);
        return context;
    }

    private static async Task SelectLabelAsync(MailInboxViewModel viewModel, string labelId)
    {
        viewModel.SelectedFolder = viewModel.Folders.Single(folder =>
            folder.IsUserLabel && string.Equals(folder.ProviderLocator, labelId, StringComparison.Ordinal));
        await viewModel.CurrentFolderLoadTask;
    }

    private static void SelectAll(MailInboxViewModel viewModel) =>
        viewModel.SelectAllLoadedCommand.Execute(null);

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

    private sealed record TestContext(
        MailAccount Account,
        LabelReadProvider Provider,
        LabelMailboxService Mailbox,
        MailInboxViewModel ViewModel);

    private sealed record PageCall(Guid AccountId, string Locator, string? Token, int PageSize);

    private sealed class LabelReadProvider : IMailReadProvider, IMailSearchProvider, IMailMessageStateProvider
    {
        private readonly Dictionary<Guid, IReadOnlyList<MailFolder>> _folders = [];
        private readonly Dictionary<(Guid AccountId, string Locator, string Token), MailPage<MailMessageSummary>> _pages = [];

        public List<PageCall> Calls { get; } = [];
        public Dictionary<string, Exception> FailuresByLocator { get; } = new(StringComparer.Ordinal);
        public MailPage<MailMessageSummary> SearchPage { get; set; } = new([], null);
        public int FolderCatalogLoads { get; private set; }

        public void SetLabels(Guid accountId, params (string Id, string Name)[] labels)
        {
            List<MailFolder> folders = GmailSystemFolders.Map(new HashSet<string>(
                GmailSystemFolders.LabelIds,
                StringComparer.Ordinal)).ToList();
            (string Id, string Name)[] ordered = labels
                .OrderBy(label => label.Name, GmailUserLabelNameComparer.Instance)
                .ThenBy(label => label.Id, StringComparer.Ordinal)
                .ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                folders.Add(MailFolderCatalog.CreateUserLabel(
                    ordered[index].Id,
                    ordered[index].Name,
                    index == 0));
            }
            _folders[accountId] = folders;
        }

        public void SetPage(
            Guid accountId,
            string locator,
            string? token,
            MailPage<MailMessageSummary> page) =>
            _pages[(accountId, locator, token ?? string.Empty)] = page;

        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            FolderCatalogLoads++;
            return Task.FromResult(_folders[account.Id]);
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Inbox(), continuationToken, pageSize, cancellationToken);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new PageCall(account.Id, folder.ProviderLocator, continuationToken, pageSize));
            if (FailuresByLocator.TryGetValue(folder.ProviderLocator, out Exception? failure))
            {
                return Task.FromException<MailPage<MailMessageSummary>>(failure);
            }

            return Task.FromResult(
                _pages.GetValueOrDefault((account.Id, folder.ProviderLocator, continuationToken ?? string.Empty))
                ?? new MailPage<MailMessageSummary>([], null));
        }

        public Task<MailPage<MailMessageSummary>> SearchAsync(
            MailAccount account,
            string query,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SearchPage);

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            GetMessageAsync(account, MailFolderCatalog.Inbox(), messageKey, cancellationToken);

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailMessageContent(
                messageKey,
                "Subject",
                "Sender",
                "sender@example.test",
                account.EmailAddress,
                DateTimeOffset.UtcNow,
                MailMessageBodyKind.PlainText,
                "Body",
                [],
                false,
                false));

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

    private sealed class LabelMailboxService : IGmailMailboxManagementService
    {
        public Dictionary<Guid, IReadOnlyList<GmailUserLabel>> CatalogsByAccount { get; } = [];
        public Dictionary<Guid, IReadOnlyList<GmailUserLabel>> RemoteLabelsByAccount { get; } = [];
        public List<(string LabelId, bool IsApplied)> LabelMutations { get; } = [];
        public IReadOnlyList<string> LastMessageKeys { get; private set; } = [];
        public string? LastOperation { get; private set; }
        public int ForcedCatalogLoads { get; private set; }

        public void UseUserLabelCatalog(Guid accountId, IReadOnlyList<GmailUserLabel> labels) =>
            CatalogsByAccount[accountId] = labels.ToArray();

        public Task<GmailUserLabelResult> GetUserLabelsAsync(
            MailAccount account,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (forceRefresh)
            {
                ForcedCatalogLoads++;
            }

            IReadOnlyList<GmailUserLabel> labels = RemoteLabelsByAccount.GetValueOrDefault(account.Id)
                ?? CatalogsByAccount.GetValueOrDefault(account.Id)
                ?? [];
            if (forceRefresh)
            {
                CatalogsByAccount[account.Id] = labels;
            }
            return Task.FromResult(new GmailUserLabelResult(labels));
        }

        public Task<GmailMailboxMutationResult> SetStarredAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, bool isStarred, CancellationToken cancellationToken = default) =>
            Result(messageKeys, "Star");

        public Task<GmailMailboxMutationResult> SetReadStateAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, bool isRead, CancellationToken cancellationToken = default) =>
            Result(messageKeys, "Read");

        public Task<GmailMailboxMutationResult> ArchiveAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(messageKeys, "Archive");

        public Task<GmailMailboxMutationResult> MoveToTrashAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(messageKeys, "Trash");

        public Task<GmailMailboxMutationResult> RestoreFromTrashAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(messageKeys, "Restore");

        public Task<GmailMailboxMutationResult> MarkNotSpamAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(messageKeys, "NotSpam");

        public Task<GmailMailboxMutationResult> ReportSpamAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(messageKeys, "ReportSpam");

        public Task<GmailMailboxMutationResult> SetUserLabelAsync(
            MailAccount account,
            IReadOnlyCollection<string> messageKeys,
            string labelId,
            bool isApplied,
            CancellationToken cancellationToken = default)
        {
            LabelMutations.Add((labelId, isApplied));
            return Result(messageKeys, "Label");
        }

        public void RemoveAccount(Guid accountId)
        {
            CatalogsByAccount.Remove(accountId);
            RemoteLabelsByAccount.Remove(accountId);
        }

        private Task<GmailMailboxMutationResult> Result(
            IReadOnlyCollection<string> messageKeys,
            string operation)
        {
            LastMessageKeys = messageKeys.ToArray();
            LastOperation = operation;
            return Task.FromResult(new GmailMailboxMutationResult(messageKeys.ToArray(), []));
        }
    }

    private sealed class ProviderFactory(
        IMailReadProvider provider,
        IGmailMailboxManagementService mailbox) : IMailReadProviderFactory
    {
        public IGmailMailboxManagementService? GmailMailboxManagementService => mailbox;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }
}
