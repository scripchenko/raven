using MailKit;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.Tests;

public sealed class YandexSearchPaginationTests
{
    [Fact]
    public void UidCursor_BindsImmutableSnapshotToFolderQueryAndUidValidity()
    {
        string scope = MailKitImapInboxClient.CreateUidCursorScope("INBOX", "проект");
        DateTimeOffset anchor = new(2026, 9, 17, 10, 30, 0, TimeSpan.Zero);
        string cursor = MailKitImapInboxClient.CreateUidCursor(
            scope, 71, 900, anchor.UtcTicks, 750, 318);

        MailKitImapInboxClient.ImapUidPageCursor parsed = Assert.IsType<MailKitImapInboxClient.ImapUidPageCursor>(
            MailKitImapInboxClient.ParseUidCursor(cursor, scope));

        Assert.Equal((uint)71, parsed.UidValidity);
        Assert.Equal((uint)900, parsed.SnapshotMaxUid);
        Assert.Equal(anchor.UtcTicks, parsed.AnchorUtcTicks);
        Assert.Equal((uint)750, parsed.AnchorUid);
        Assert.Equal(318, parsed.TotalCount);
        Assert.DoesNotContain("imap-index", cursor, StringComparison.Ordinal);
        Assert.Throws<MailReadException>(() => MailKitImapInboxClient.ParseUidCursor(
            cursor,
            MailKitImapInboxClient.CreateUidCursorScope("Spam", "проект")));
        Assert.Throws<MailReadException>(() => MailKitImapInboxClient.ParseUidCursor(
            cursor,
            MailKitImapInboxClient.CreateUidCursorScope("INBOX", "другой запрос")));
        MailReadException stale = Assert.Throws<MailReadException>(() =>
            MailKitImapInboxClient.EnsureUidCursorValidity(parsed, 72));
        Assert.Equal(MailReadFailureKind.InvalidConfiguration, stale.FailureKind);
    }

    [Fact]
    public void ManagedImapPages_UseReceivedChronologyWithoutLosingUidSnapshotSafety()
    {
        DateTimeOffset epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ImapChronologyEntry[] snapshot = Enumerable.Range(1, 120)
            .Select(value => new ImapChronologyEntry((uint)value, epoch.AddMinutes(value)))
            .Append(new ImapChronologyEntry(150, epoch.AddMinutes(20)))
            .ToArray();
        ImapChronologyEntry[] first = MailKitImapInboxClient
            .SelectChronologicalPage(snapshot, null, 50)
            .ToArray();
        ImapChronologyEntry anchor = first[^1];
        MailKitImapInboxClient.ImapUidPageCursor cursor = new(
            7, 150, anchor.ReceivedAt.UtcTicks, anchor.UniqueId, snapshot.Length);
        ImapChronologyEntry[] changed = snapshot
            .Where(item => item.UniqueId != 65)
            // A genuinely new delivery is outside the immutable UID snapshot.
            .Append(new ImapChronologyEntry(151, epoch.AddDays(1)))
            .Where(item => item.UniqueId <= cursor.SnapshotMaxUid)
            .ToArray();

        ImapChronologyEntry[] second = MailKitImapInboxClient
            .SelectChronologicalPage(changed, cursor, 50)
            .ToArray();

        Assert.Equal((uint)120, first[0].UniqueId);
        Assert.DoesNotContain(first, item => item.UniqueId == 150);
        Assert.Equal((uint)150, second[^1].UniqueId);
        Assert.Empty(first.Select(item => item.UniqueId).Intersect(second.Select(item => item.UniqueId)));
        Assert.DoesNotContain(second, item => item.UniqueId == 151);
        Assert.DoesNotContain(second, item => item.UniqueId == 65);
    }

    [Fact]
    public void MailRuMovedMessageWithHighUid_RemainsAtItsInternalDateAcrossAllPages()
    {
        DateTimeOffset epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        List<ImapChronologyEntry> entries = Enumerable.Range(1, 120)
            .Select(value => new ImapChronologyEntry((uint)value, epoch.AddMinutes(value)))
            .ToList();
        entries.Add(new ImapChronologyEntry(500, epoch.AddMinutes(20).AddSeconds(1)));

        List<uint> allPages = [];
        MailKitImapInboxClient.ImapUidPageCursor? cursor = null;
        do
        {
            ImapChronologyEntry[] page = MailKitImapInboxClient
                .SelectChronologicalPage(entries, cursor, 50)
                .ToArray();
            allPages.AddRange(page.Select(item => item.UniqueId));
            if (page.Length < 50)
            {
                cursor = null;
                break;
            }

            ImapChronologyEntry last = page[^1];
            cursor = new(9, 500, last.ReceivedAt.UtcTicks, last.UniqueId, entries.Count);
        }
        while (allPages.Count < entries.Count);

        Assert.Equal(entries.Count, allPages.Count);
        Assert.Equal(entries.Count, allPages.Distinct().Count());
        Assert.DoesNotContain((uint)500, allPages.Take(50));
        Assert.True(allPages.IndexOf(500) > allPages.IndexOf(21));
        Assert.True(allPages.IndexOf(500) < allPages.IndexOf(20));
    }

    [Fact]
    public void ManagedImapSearch_UsesSameNewestFirstInternalDateOrder()
    {
        DateTimeOffset epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ImapChronologyEntry[] matches =
        [
            new(900, epoch.AddDays(-10)),
            new(7, epoch),
            new(8, epoch.AddDays(-1))
        ];

        IReadOnlyList<ImapChronologyEntry> page =
            MailKitImapInboxClient.SelectChronologicalPage(matches, null, 50);

        Assert.Equal([(uint)7, 8, 900], page.Select(item => item.UniqueId));
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapProvider_SearchUsesCurrentFolderAndUidSafeServerContract(
        MailProviderType providerType)
    {
        RecordingImapClient client = new()
        {
            Result = new ImapInboxPageData(
                [new ImapSummaryData(42, "Subject", "Sender", "sender@example.test", DateTimeOffset.UtcNow, true, 9)],
                "uid-next",
                64)
        };
        ImapMailReadProvider provider = CreateProvider(client);
        MailAccount account = Account("one@example.test", providerType);
        MailFolder spam = MailFolderCatalog.Create(MailFolderKind.Spam, "Spam");

        MailPage<MailMessageSummary> page = await provider.SearchAsync(
            account,
            spam,
            "invoice",
            "uid-current",
            50);

        Assert.Equal("Spam", client.Folder?.FullName);
        Assert.Equal("invoice", client.Query);
        Assert.Equal("uid-current", client.Cursor);
        Assert.Equal(50, client.PageSize);
        Assert.Equal(64, page.TotalCount);
        Assert.Equal(ImapMailReadProvider.CreateMessageKey(MailFolderKind.Spam, 9, 42), Assert.Single(page.Items).MessageKey);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapFolderPage_UsesUidSafeContractInsteadOfLegacyIndex(
        MailProviderType providerType)
    {
        RecordingImapClient client = new()
        {
            Result = new ImapInboxPageData([], "imap-uid:1:scope:7:90:40:90", 90)
        };
        ImapMailReadProvider provider = CreateProvider(client);
        MailAccount account = Account("one@example.test", providerType);

        MailPage<MailMessageSummary> page = await provider.GetPageAsync(
            account,
            MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"),
            null,
            50);

        Assert.Equal(1, client.UidSafePageCallCount);
        Assert.Equal(0, client.LegacyPageCallCount);
        Assert.StartsWith("imap-uid:", page.ContinuationToken, StringComparison.Ordinal);
        Assert.DoesNotContain("imap-index", page.ContinuationToken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericImapFolderPage_PreservesLegacyContract()
    {
        RecordingImapClient client = new()
        {
            Result = new ImapInboxPageData([], "imap-index:40")
        };
        ImapMailReadProvider provider = CreateProvider(client);
        MailAccount account = Account("one@example.test", MailProviderType.GenericImap);

        MailPage<MailMessageSummary> page = await provider.GetPageAsync(
            account,
            MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"),
            null,
            50);

        Assert.Equal(0, client.UidSafePageCallCount);
        Assert.Equal(1, client.LegacyPageCallCount);
        Assert.Equal("imap-index:40", page.ContinuationToken);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapSearch_ClearAndPaginationRemainFolderScoped(
        MailProviderType providerType)
    {
        MailAccount account = Account("one@example.test", providerType);
        YandexSearchProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page("normal-1", "normal-next", 120));
        provider.SetPage(account.Id, MailFolderKind.Inbox, "normal-next", Page("normal-2", null, 120));
        provider.SearchHandler = (_, folder, query, token, _) => Task.FromResult(
            token is null ? Page("search-1", "search-next", 61) : Page("search-2", null, 61));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.SearchText = "server text";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSearchActive);
        Assert.Equal("search-2", Assert.Single(viewModel.Messages).Subject);
        Assert.Equal(MailFolderKind.Inbox, provider.SearchCalls[1].Folder.Kind);
        Assert.Equal("search-next", provider.SearchCalls[1].Token);
        Assert.Equal("51–51 из 61", viewModel.PageRangeText);

        viewModel.ClearSearchCommand.Execute(null);
        Assert.False(viewModel.IsSearchActive);
        Assert.Equal("normal-1", Assert.Single(viewModel.Messages).Subject);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal("normal-next", provider.PageCalls.Last().Token);
        Assert.Equal("normal-2", Assert.Single(viewModel.Messages).Subject);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapFolderPaginationRetainsExactSnapshotTotalThroughLastPage(
        MailProviderType providerType)
    {
        MailAccount account = Account("one@example.test", providerType);
        YandexSearchProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, PageBatch("first", 50, "page-2", 107));
        provider.SetPage(account.Id, MailFolderKind.Inbox, "page-2", PageBatch("second", 50, "page-3", null));
        provider.SetPage(account.Id, MailFolderKind.Inbox, "page-3", PageBatch("last", 7, null, null));
        using MailInboxViewModel viewModel = ViewModel(provider);

        await viewModel.ActivateAsync(account);
        Assert.Equal("1–50 из 107", viewModel.PageRangeText);

        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal("51–100 из 107", viewModel.PageRangeText);

        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal("101–107 из 107", viewModel.PageRangeText);

        await viewModel.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal("51–100 из 107", viewModel.PageRangeText);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapSearchPaginationRetainsExactTotalAndNewScopeReplacesIt(
        MailProviderType providerType)
    {
        MailAccount account = Account("one@example.test", providerType);
        YandexSearchProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page("normal", null, 1));
        provider.SearchHandler = (_, _, query, token, _) => Task.FromResult((query, token) switch
        {
            ("first", null) => PageBatch("first-search", 50, "first-next", 73),
            ("first", "first-next") => PageBatch("first-last", 23, null, null),
            ("second", null) => PageBatch("second-search", 4, null, 4),
            _ => new MailPage<MailMessageSummary>([], null)
        });
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.SearchText = "first";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal("51–73 из 73", viewModel.PageRangeText);

        viewModel.SearchText = "second";
        await viewModel.SearchCommand.ExecuteAsync(null);
        Assert.Equal("1–4 из 4", viewModel.PageRangeText);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task LatestManagedImapQueryWinsAndCancellationClearsBusyState(
        MailProviderType providerType)
    {
        MailAccount account = Account("one@example.test", providerType);
        TaskCompletionSource<MailPage<MailMessageSummary>> alpha = PendingPage();
        TaskCompletionSource<MailPage<MailMessageSummary>> beta = PendingPage();
        YandexSearchProvider provider = new()
        {
            SearchHandler = (_, _, query, _, _) => query == "alpha" ? alpha.Task : beta.Task
        };
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page("normal", null, 1));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.SearchText = "alpha";
        Task first = viewModel.SearchCommand.ExecuteAsync(null);
        viewModel.SearchText = "beta";
        Task second = viewModel.SearchCommand.ExecuteAsync(null);
        beta.SetResult(Page("beta-result", null, 1));
        await second;
        alpha.SetResult(Page("alpha-result", null, 1));
        await first;

        Assert.Equal("beta", viewModel.ActiveSearchQuery);
        Assert.Equal("beta-result", Assert.Single(viewModel.Messages).Subject);
        Assert.False(viewModel.IsListLoading);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapRefreshRebaselinesSearchAndFolderPagination(
        MailProviderType providerType)
    {
        MailAccount account = Account("one@example.test", providerType);
        YandexSearchProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page("normal-1", "normal-next", 100));
        provider.SetPage(account.Id, MailFolderKind.Inbox, "normal-next", Page("normal-2", null, 100));
        provider.SearchHandler = (_, _, _, token, _) => Task.FromResult(
            token is null ? Page("search-first", "search-next", 80) : Page("search-next", null, 80));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Null(provider.PageCalls.Last().Token);

        viewModel.SearchText = "query";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Null(provider.SearchCalls.Last().Token);
        Assert.Equal("search-first", Assert.Single(viewModel.Messages).Subject);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task FolderAndAccountSwitchResetManagedImapSearchCursorAndState(
        MailProviderType providerType)
    {
        MailAccount first = Account("first@example.test", providerType);
        MailAccount second = Account("second@example.test", providerType);
        YandexSearchProvider provider = new();
        provider.SetPage(first.Id, MailFolderKind.Inbox, null, Page("first-inbox", null, 1));
        provider.SetPage(first.Id, MailFolderKind.Spam, null, Page("first-spam", null, 1));
        provider.SetPage(second.Id, MailFolderKind.Inbox, null, Page("second-inbox", null, 1));
        provider.SearchHandler = (_, _, _, _, _) => Task.FromResult(Page("search", "search-next", 70));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(first);
        viewModel.SearchText = "query";
        await viewModel.SearchCommand.ExecuteAsync(null);

        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Spam);
        await viewModel.CurrentFolderLoadTask;

        Assert.False(viewModel.IsSearchActive);
        Assert.Equal("first-spam", Assert.Single(viewModel.Messages).Subject);
        Assert.Null(provider.PageCalls.Last().Token);

        await viewModel.ActivateAsync(second);
        Assert.False(viewModel.IsSearchActive);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal("second-inbox", Assert.Single(viewModel.Messages).Subject);
        Assert.Null(provider.PageCalls.Last().Token);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ClearingPendingManagedImapSearchCancelsItAndRestoresInteractiveFolder(
        MailProviderType providerType)
    {
        MailAccount account = Account("one@example.test", providerType);
        TaskCompletionSource<MailPage<MailMessageSummary>> pending = PendingPage();
        YandexSearchProvider provider = new()
        {
            SearchHandler = (_, _, _, _, _) => pending.Task
        };
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page("normal", null, 1));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);
        viewModel.SearchText = "slow";
        Task search = viewModel.SearchCommand.ExecuteAsync(null);

        viewModel.ClearSearchCommand.Execute(null);
        pending.SetResult(Page("late", null, 1));
        await search;

        Assert.False(viewModel.IsSearchActive);
        Assert.False(viewModel.IsListLoading);
        Assert.Equal("normal", Assert.Single(viewModel.Messages).Subject);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapMailboxMutationFromSearchReconcilesSearchAndKeepsUiInteractive(
        MailProviderType providerType)
    {
        MailAccount account = Account("one@example.test", providerType);
        YandexSearchProvider provider = new();
        provider.SetPage(account.Id, MailFolderKind.Inbox, null, Page("normal", null, 1));
        int searchCall = 0;
        provider.SearchHandler = (_, _, _, _, _) => Task.FromResult(
            ++searchCall == 1 ? Page("result", null, 1) : new MailPage<MailMessageSummary>([], null, 0));
        RecordingMailboxService mailbox = new();
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.SearchText = "move me";
        await viewModel.SearchCommand.ExecuteAsync(null);
        MailMessageSummary result = Assert.Single(viewModel.Messages);
        viewModel.ToggleMessageSelectionCommand.Execute(result);

        await viewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(MailMailboxAction.Trash, Assert.Single(mailbox.Actions));
        Assert.True(viewModel.IsSearchActive);
        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.IsMailboxChanging);
        Assert.True(viewModel.RefreshCommand.CanExecute(null));
    }

    private static TaskCompletionSource<MailPage<MailMessageSummary>> PendingPage() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static MailInboxViewModel ViewModel(
        YandexSearchProvider provider,
        IMailMailboxManagementService? mailbox = null) =>
        new(new ProviderFactory(provider, mailbox));

    private static MailAccount Account(
        string address,
        MailProviderType providerType = MailProviderType.Yandex) => new()
    {
        Id = Guid.NewGuid(),
        Provider = providerType,
        EmailAddress = address,
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = MailAuthenticationKind.Password,
        IsEnabled = true,
        GenericConnectionSettings = providerType is MailProviderType.GenericImap
            ? new MailConnectionSettings
            {
                Imap = new MailServerSettings
                {
                    Host = "imap.example.test",
                    Port = 993,
                    SecureSocketMode = MailSecureSocketMode.SslOnConnect,
                    Username = address
                },
                Smtp = new MailServerSettings
                {
                    Host = "smtp.example.test",
                    Port = 465,
                    SecureSocketMode = MailSecureSocketMode.SslOnConnect,
                    Username = address
                }
            }
            : null
    };

    private static MailPage<MailMessageSummary> Page(string subject, string? token, long total) =>
        new(
            [new MailMessageSummary(
                ImapMailReadProvider.CreateMessageKey(MailFolderKind.Inbox, 10, (uint)(subject.GetHashCode(StringComparison.Ordinal) & int.MaxValue) + 1),
                subject,
                "Sender",
                "sender@example.test",
                DateTimeOffset.UtcNow,
                "Preview",
                true)],
            token,
            total);

    private static MailPage<MailMessageSummary> PageBatch(
        string prefix,
        int count,
        string? token,
        long? total) =>
        new(
            Enumerable.Range(1, count)
                .Select(index => new MailMessageSummary(
                    ImapMailReadProvider.CreateMessageKey(MailFolderKind.Inbox, 10, (uint)index),
                    $"{prefix}-{index}",
                    "Sender",
                    "sender@example.test",
                    DateTimeOffset.UtcNow,
                    "Preview",
                    true))
                .ToArray(),
            token,
            total);

    private static ImapMailReadProvider CreateProvider(IImapInboxClient client)
    {
        NoOpConnectionValidator validator = new();
        MailProviderFactory providers = new(
            [new GmailApiProvider(), new YandexMailProvider(validator), new MailRuMailProvider(validator), new GenericImapMailProvider(validator)]);
        return new ImapMailReadProvider(
            new CredentialStore(),
            providers,
            client,
            new MailContentExtractor(new MailHtmlSanitizer()));
    }

    private sealed class RecordingImapClient : IImapInboxClient
    {
        public ImapInboxPageData Result { get; set; } = new([], null);
        public ImapFolderDescriptor? Folder { get; private set; }
        public string? Query { get; private set; }
        public string? Cursor { get; private set; }
        public int PageSize { get; private set; }
        public int UidSafePageCallCount { get; private set; }
        public int LegacyPageCallCount { get; private set; }

        public Task<ImapInboxPageData> GetUidSafeFolderPageAsync(
            MailServerSettings server,
            string secret,
            ImapFolderDescriptor folder,
            string? query,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            UidSafePageCallCount++;
            Folder = folder;
            Query = query;
            Cursor = cursor;
            PageSize = pageSize;
            return Task.FromResult(Result);
        }

        public Task<ImapInboxPageData> GetFolderPageAsync(
            MailServerSettings server,
            string secret,
            ImapFolderDescriptor folder,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            LegacyPageCallCount++;
            Folder = folder;
            Cursor = cursor;
            PageSize = pageSize;
            return Task.FromResult(Result);
        }

        public Task<ImapInboxPageData> GetInboxPageAsync(MailServerSettings server, string secret, string? cursor, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<ImapMessageData> GetMessageAsync(MailServerSettings server, string secret, uint uniqueId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class YandexSearchProvider : IMailReadProvider, IMailSearchProvider
    {
        private readonly Dictionary<(Guid AccountId, MailFolderKind Folder, string? Token), MailPage<MailMessageSummary>> _pages = [];
        public Func<Guid, MailFolder, string, string?, CancellationToken, Task<MailPage<MailMessageSummary>>> SearchHandler { get; set; } =
            (_, _, _, _, _) => Task.FromResult(new MailPage<MailMessageSummary>([], null));
        public List<(Guid AccountId, MailFolder Folder, string Query, string? Token)> SearchCalls { get; } = [];
        public List<(Guid AccountId, MailFolder Folder, string? Token)> PageCalls { get; } = [];

        public void SetPage(Guid accountId, MailFolderKind folder, string? token, MailPage<MailMessageSummary> page) =>
            _pages[(accountId, folder, token)] = page;

        public bool Supports(MailProviderType providerType) =>
            providerType is MailProviderType.Yandex or MailProviderType.MailRu;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>
            ([MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"), MailFolderCatalog.Create(MailFolderKind.Spam, "Spam"), MailFolderCatalog.Create(MailFolderKind.Trash, "Trash")]);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(MailAccount account, MailFolder folder, string? continuationToken, int pageSize, CancellationToken cancellationToken = default)
        {
            PageCalls.Add((account.Id, folder, continuationToken));
            return Task.FromResult(_pages.GetValueOrDefault((account.Id, folder.Kind, continuationToken))
                ?? new MailPage<MailMessageSummary>([], null, 0));
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(MailAccount account, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"), continuationToken, pageSize, cancellationToken);

        public Task<MailPage<MailMessageSummary>> SearchAsync(MailAccount account, string query, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            SearchAsync(account, MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"), query, continuationToken, pageSize, cancellationToken);

        public Task<MailPage<MailMessageSummary>> SearchAsync(MailAccount account, MailFolder folder, string query, string? continuationToken, int pageSize, CancellationToken cancellationToken = default)
        {
            SearchCalls.Add((account.Id, folder, query, continuationToken));
            return SearchHandler(account.Id, folder, query, continuationToken, cancellationToken);
        }

        public Task<MailMessageContent> GetMessageAsync(MailAccount account, string messageKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ProviderFactory(
        YandexSearchProvider provider,
        IMailMailboxManagementService? mailbox) : IMailReadProviderFactory
    {
        public IMailMailboxManagementService? MailboxManagementService => mailbox;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class RecordingMailboxService : IMailMailboxManagementService
    {
        public List<MailMailboxAction> Actions { get; } = [];
        public bool Supports(MailProviderType provider) =>
            MailProviderFeaturePolicies.Get(provider).IsManagedImap;
        public bool CanApply(MailFolderKind source, MailMailboxAction action) => action is MailMailboxAction.Trash;

        public Task<MailMailboxMutationResult> ApplyAsync(
            MailAccount account,
            MailFolder source,
            IReadOnlyCollection<string> messageKeys,
            MailMailboxAction action,
            CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.FromResult(new MailMailboxMutationResult(
                messageKeys.ToArray(),
                [],
                MailFolderKind.Trash));
        }
    }

    private sealed class CredentialStore : IMailCredentialStore
    {
        public Task SaveAsync(string credentialKey, MailCredential credential, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<MailCredential?>(MailCredential.CreatePassword("password"));
        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpConnectionValidator : IMailConnectionValidator
    {
        public Task<MailConnectionValidationResult> ValidateAsync(MailConnectionSettings settings, string emailAddress, string secret, CancellationToken cancellationToken = default) =>
            Task.FromResult(MailConnectionValidationResult.Success(new MailIdentity(emailAddress, null)));
    }
}
