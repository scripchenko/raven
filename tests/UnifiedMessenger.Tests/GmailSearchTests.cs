using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailSearchTests
{
    [Fact]
    public async Task GmailProvider_ForwardsQueryAndPaginationToServerSideSearch()
    {
        RecordingGmailApiClient api = new();
        api.SearchResult = new GmailApiInboxPage(
            [new GmailApiSummaryData("outside", "Outside", "Sender <sender@example.test>", 1, "Preview", [GmailSystemFolders.Inbox])],
            "next-search",
            LabelMessagesTotal: 999,
            ResultSizeEstimate: 125);
        GmailMailReadProvider provider = new(new CredentialStore(), api, new MailContentExtractor(new MailHtmlSanitizer()));
        MailAccount account = Account("gmail-a@example.test");

        MailPage<MailMessageSummary> page = await provider.SearchAsync(
            account,
            "from:sender@example.test has:attachment",
            "search-token",
            25);

        SearchCall call = Assert.Single(api.SearchCalls);
        Assert.Equal(account.Id, call.AccountId);
        Assert.Equal("from:sender@example.test has:attachment", call.Query);
        Assert.Equal("search-token", call.PageToken);
        Assert.Equal(25, call.PageSize);
        Assert.Equal("gmail:outside", Assert.Single(page.Items).MessageKey);
        Assert.Equal("next-search", page.ContinuationToken);
        Assert.Null(page.TotalCount);
    }

    [Theory]
    [InlineData(MailFolderKind.Inbox, GmailSystemFolders.Inbox)]
    [InlineData(MailFolderKind.Starred, GmailSystemFolders.Starred)]
    [InlineData(MailFolderKind.Trash, GmailSystemFolders.Trash)]
    public async Task GmailProvider_NormalFolderUsesExactLabelMessageCountAndIgnoresEstimate(
        MailFolderKind kind,
        string labelId)
    {
        RecordingGmailApiClient api = new()
        {
            FolderResult = new GmailApiInboxPage(
                [new GmailApiSummaryData("message", "Subject", "Sender", 1, "Preview", [labelId])],
                null,
                LabelMessagesTotal: 5127,
                ResultSizeEstimate: 201)
        };
        GmailMailReadProvider provider = new(new CredentialStore(), api, new MailContentExtractor(new MailHtmlSanitizer()));
        MailAccount account = Account("gmail-folder@example.test");

        MailPage<MailMessageSummary> page = await provider.GetPageAsync(
            account,
            MailFolderCatalog.Create(kind, labelId),
            null,
            50);

        Assert.Equal(5127, page.TotalCount);
        Assert.NotEqual(api.FolderResult.ResultSizeEstimate, page.TotalCount);
        Assert.Equal((account.Id, labelId), Assert.Single(api.FolderCalls));
    }

    [Fact]
    public async Task GmailProvider_NormalFolderDoesNotFallBackToResultSizeEstimate()
    {
        RecordingGmailApiClient api = new()
        {
            FolderResult = new GmailApiInboxPage(
                [new GmailApiSummaryData("message", "Subject", "Sender", 1, "Preview", [GmailSystemFolders.Inbox])],
                null,
                LabelMessagesTotal: null,
                ResultSizeEstimate: 201)
        };
        GmailMailReadProvider provider = new(new CredentialStore(), api, new MailContentExtractor(new MailHtmlSanitizer()));

        MailPage<MailMessageSummary> page = await provider.GetInboxPageAsync(
            Account("gmail-fallback@example.test"),
            null,
            50);

        Assert.Null(page.TotalCount);
    }

    [Theory]
    [InlineData(GmailSystemFolders.Inbox)]
    [InlineData(GmailSystemFolders.Starred)]
    [InlineData(GmailSystemFolders.Trash)]
    [InlineData("Label_Projects")]
    public void GmailLabelCount_IsMessageOrientedForSystemAndUserLabels(string labelId)
    {
        Google.Apis.Gmail.v1.Data.Label label = new()
        {
            Id = labelId,
            MessagesTotal = 5127,
            ThreadsTotal = 4384
        };

        Assert.Equal(5127, GmailApiReadClient.GetMessageOrientedLabelTotal(label));
        Assert.NotEqual(label.ThreadsTotal, GmailApiReadClient.GetMessageOrientedLabelTotal(label));
    }

    [Fact]
    public void GmailApiRequest_UsesQAndKeepsSearchContinuationToken()
    {
        using GmailService service = new(new BaseClientService.Initializer { ApplicationName = "Lantern tests" });
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");

        GmailApiReadClient.ConfigureSearchRequest(request, "is:unread", "search-next", 30);

        Assert.Equal("is:unread", request.Q);
        Assert.Equal("search-next", request.PageToken);
        Assert.Equal(30, request.MaxResults);
        Assert.True(request.IncludeSpamTrash);
        Assert.Null(request.LabelIds);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, MailReadFailureKind.InvalidSearchQuery, "Проверьте запрос")]
    [InlineData(HttpStatusCode.TooManyRequests, MailReadFailureKind.ConnectionFailed, "Попробуйте ещё раз позже")]
    public void GmailApiSearchFailures_AreMappedToFriendlyMessages(
        HttpStatusCode statusCode,
        MailReadFailureKind expectedFailureKind,
        string expectedMessagePart)
    {
        GoogleApiException exception = new("Gmail", "synthetic API failure")
        {
            HttpStatusCode = statusCode
        };

        MailReadException mapped = GmailApiReadClient.MapSearchException(exception);

        Assert.Equal(expectedFailureKind, mapped.FailureKind);
        Assert.Contains(expectedMessagePart, mapped.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic API failure", mapped.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_IsServerSideAndClearRestoresUnfilteredCachedFolder()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:inbox")], "normal-next", 80));
        provider.SearchHandler = (_, query, token, _) => Task.FromResult(
            token is null
                ? Page([Summary("gmail:outside")], "search-next", 80)
                : Page([Summary("gmail:second")], null, 80));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.SearchText = "subject:outside";
        Assert.Empty(provider.SearchCalls);
        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSearchActive);
        Assert.Equal("subject:outside", viewModel.ActiveSearchQuery);
        Assert.Equal("gmail:outside", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("search-next", viewModel.ContinuationToken);
        Assert.Equal("subject:outside", Assert.Single(provider.SearchCalls).Query);

        await viewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Equal("gmail:second", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("subject:outside", provider.SearchCalls[1].Query);
        Assert.Equal("search-next", provider.SearchCalls[1].PageToken);
        Assert.Equal("51–51", viewModel.PageRangeText);

        await viewModel.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal("gmail:outside", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Null(provider.SearchCalls[2].PageToken);
        Assert.False(viewModel.CanNavigateToPreviousPage);

        viewModel.ClearSearchCommand.Execute(null);

        Assert.False(viewModel.IsSearchActive);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal("gmail:inbox", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("normal-next", viewModel.ContinuationToken);
        Assert.Single(provider.NormalCalls);
    }

    [Fact]
    public async Task NormalAndSearchPageTokensStayIsolatedWithFiftyMessageRequests()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:normal-1")], "normal-next", 120));
        provider.SetNormalPage(account.Id, "normal-next", Page([Summary("gmail:normal-2")], null, 120));
        provider.SearchHandler = (_, _, token, _) => Task.FromResult(
            token is null
                ? Page([Summary("gmail:search-1")], "search-next", 70)
                : Page([Summary("gmail:search-2")], null, 70));
        using MailInboxViewModel viewModel = ViewModel(provider);

        await viewModel.ActivateAsync(account);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Equal("gmail:normal-2", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal([null, "normal-next"], provider.NormalCalls.Select(call => call.Token));
        Assert.All(provider.NormalCalls, call => Assert.Equal(50, call.PageSize));
        Assert.Equal("51–51 из 120", viewModel.PageRangeText);

        viewModel.SearchText = "has:attachment";
        await viewModel.SearchCommand.ExecuteAsync(null);
        await viewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Equal("gmail:search-2", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal([null, "search-next"], provider.SearchCalls.Select(call => call.PageToken));
        Assert.All(provider.SearchCalls, call => Assert.Equal(50, call.PageSize));
        Assert.Equal("51–51", viewModel.PageRangeText);
        Assert.DoesNotContain("из", viewModel.PageRangeText, StringComparison.Ordinal);

        viewModel.ClearSearchCommand.Execute(null);

        Assert.Equal("gmail:normal-2", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("51–51 из 120", viewModel.PageRangeText);
        Assert.True(viewModel.CanNavigateToPreviousPage);
        await viewModel.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal("gmail:normal-1", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Null(provider.NormalCalls[^1].Token);
    }

    [Fact]
    public async Task PageRangeUsesExactFolderTotalButSearchAndUnavailableCountStayRangeOnly()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page(Summaries("page-1", 50), "page-2", 5127));
        provider.SetNormalPage(account.Id, "page-2", Page(Summaries("page-2", 50), "page-3", 5127));
        provider.SetNormalPage(account.Id, "page-3", Page(Summaries("page-3", 50), null, 5127));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(Page(Summaries("search", 50), null, 201));
        using MailInboxViewModel viewModel = ViewModel(provider);

        await viewModel.ActivateAsync(account);

        Assert.Equal("1–50 из 5127", viewModel.PageRangeText);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal("101–150 из 5127", viewModel.PageRangeText);

        viewModel.SearchText = "has:attachment";
        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.Equal("1–50", viewModel.PageRangeText);
        Assert.DoesNotContain("из", viewModel.PageRangeText, StringComparison.Ordinal);

        viewModel.ClearSearchCommand.Execute(null);

        Assert.Equal("101–150 из 5127", viewModel.PageRangeText);

        MailAccount accountWithoutTotal = Account("gmail-no-total@example.test");
        provider.SetNormalPage(accountWithoutTotal.Id, null, Page(Summaries("no-total", 50), null));

        await viewModel.ActivateAsync(accountWithoutTotal);

        Assert.Equal("1–50", viewModel.PageRangeText);
        Assert.DoesNotContain("из", viewModel.PageRangeText, StringComparison.Ordinal);

        await viewModel.ActivateAsync(account);

        Assert.Equal("101–150 из 5127", viewModel.PageRangeText);
    }

    [Fact]
    public async Task ArchiveUpdatesExactNormalFolderTotalWithoutUsingSearchEstimate()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(
            account.Id,
            null,
            Page([Summary("gmail:first"), Summary("gmail:second")], null, 10));
        RecordingMailboxService mailbox = new();
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.ToggleMessageSelectionCommand.Execute(viewModel.Messages[0]);

        await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);

        Assert.Equal("1–1 из 9", viewModel.PageRangeText);
        Assert.Equal(MailboxOperation.Archive, Assert.Single(mailbox.Operations));
    }

    [Fact]
    public async Task SearchArchiveInvalidatesCachedInboxTotalWhenLocalDeltaIsUnknown()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:cached")], null, 10));
        Queue<MailPage<MailMessageSummary>> searchPages = new(
        [
            Page([Summary("gmail:outside-cache")], null, 201),
            Page([], null, 200)
        ]);
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(searchPages.Dequeue());
        RecordingMailboxService mailbox = new();
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await SearchAsync(viewModel, account, "in:inbox subject:outside");
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);
        viewModel.ClearSearchCommand.Execute(null);

        Assert.Equal("1–1", viewModel.PageRangeText);
        Assert.DoesNotContain("из", viewModel.PageRangeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySubmittedQueryLeavesSearchAndReturnsToFolder()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:inbox")], null));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(Page([Summary("gmail:result")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);
        viewModel.SearchText = "alpha";
        await viewModel.SearchCommand.ExecuteAsync(null);

        viewModel.SearchText = "   ";
        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsSearchActive);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal("gmail:inbox", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Single(provider.SearchCalls);
    }

    [Fact]
    public async Task LaterQueryWinsWhenEarlierServerResponseCompletesLate()
    {
        MailAccount account = Account("gmail@example.test");
        TaskCompletionSource<MailPage<MailMessageSummary>> alpha = PendingPage();
        TaskCompletionSource<MailPage<MailMessageSummary>> beta = PendingPage();
        SearchReadProvider provider = new()
        {
            SearchHandler = (_, query, _, _) => query == "alpha" ? alpha.Task : beta.Task
        };
        provider.SetNormalPage(account.Id, null, Page([], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);

        viewModel.SearchText = "alpha";
        Task alphaSearch = viewModel.SearchCommand.ExecuteAsync(null);
        viewModel.SearchText = "beta";
        Task betaSearch = viewModel.SearchCommand.ExecuteAsync(null);
        beta.SetResult(Page([Summary("gmail:beta")], null));
        await betaSearch;
        alpha.SetResult(Page([Summary("gmail:alpha")], null));
        await alphaSearch;

        Assert.Equal("beta", viewModel.ActiveSearchQuery);
        Assert.Equal("gmail:beta", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal(["alpha", "beta"], provider.SearchCalls.Select(call => call.Query));
    }

    [Fact]
    public async Task AccountSwitchInvalidatesPendingSearchAndClearsAccountSpecificQuery()
    {
        MailAccount first = Account("first@gmail.test");
        MailAccount second = Account("second@gmail.test");
        TaskCompletionSource<MailPage<MailMessageSummary>> pending = PendingPage();
        SearchReadProvider provider = new()
        {
            SearchHandler = (_, _, _, _) => pending.Task
        };
        provider.SetNormalPage(first.Id, null, Page([Summary("gmail:first-normal")], null));
        provider.SetNormalPage(second.Id, null, Page([Summary("gmail:second-normal")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(first);
        viewModel.SearchText = "private first query";
        Task firstSearch = viewModel.SearchCommand.ExecuteAsync(null);

        await viewModel.ActivateAsync(second);
        pending.SetResult(Page([Summary("gmail:first-result")], null));
        await firstSearch;

        Assert.Equal(second.Id, viewModel.ActiveAccount?.Id);
        Assert.False(viewModel.IsSearchActive);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal("gmail:second-normal", Assert.Single(viewModel.Messages).MessageKey);
        Assert.DoesNotContain(viewModel.Messages, message => message.MessageKey == "gmail:first-result");
    }

    [Fact]
    public async Task ClearInvalidatesPendingSearchAndImmediatelyRestoresLoadingState()
    {
        MailAccount account = Account("gmail@example.test");
        TaskCompletionSource<MailPage<MailMessageSummary>> pending = PendingPage();
        SearchReadProvider provider = new()
        {
            SearchHandler = (_, _, _, _) => pending.Task
        };
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:normal")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);
        viewModel.SearchText = "alpha";
        Task search = viewModel.SearchCommand.ExecuteAsync(null);

        viewModel.ClearSearchCommand.Execute(null);

        Assert.False(viewModel.IsSearchActive);
        Assert.False(viewModel.IsListLoading);
        Assert.Equal("gmail:normal", Assert.Single(viewModel.Messages).MessageKey);
        pending.SetResult(Page([Summary("gmail:late")], null));
        await search;
        Assert.Equal("gmail:normal", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task FolderSwitchInvalidatesPendingSearchAndUsesFolderState()
    {
        MailAccount account = Account("gmail@example.test");
        TaskCompletionSource<MailPage<MailMessageSummary>> pending = PendingPage();
        SearchReadProvider provider = new()
        {
            SearchHandler = (_, _, _, _) => pending.Task
        };
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:folder")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);
        viewModel.SearchText = "alpha";
        Task search = viewModel.SearchCommand.ExecuteAsync(null);

        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Starred);
        await viewModel.CurrentFolderLoadTask;
        pending.SetResult(Page([Summary("gmail:late")], null));
        await search;

        Assert.False(viewModel.IsSearchActive);
        Assert.Equal(MailFolderKind.Starred, viewModel.SelectedFolder?.Kind);
        Assert.Equal("gmail:folder", Assert.Single(viewModel.Messages).MessageKey);
        Assert.DoesNotContain(viewModel.Messages, message => message.MessageKey == "gmail:late");
    }

    [Fact]
    public async Task MessageDetailAndBackKeepSearchResultsAndQuery()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([], null));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(Page([Summary("gmail:result")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await viewModel.ActivateAsync(account);
        viewModel.SearchText = "subject:detail";
        await viewModel.SearchCommand.ExecuteAsync(null);

        MailMessageSummary result = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(result);
        await viewModel.CurrentMessageLoadTask;

        Assert.True(viewModel.IsMessageDetailVisible);
        Assert.True(viewModel.IsSearchActive);
        Assert.Equal("subject:detail", viewModel.ActiveSearchQuery);

        viewModel.BackToMessageListCommand.Execute(null);

        Assert.True(viewModel.IsMessageListVisible);
        Assert.True(viewModel.IsSearchActive);
        Assert.Equal("subject:detail", viewModel.ActiveSearchQuery);
        Assert.Equal("gmail:result", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task SearchSelectionStarAndReadActionsRefreshServerMembership()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        Queue<MailPage<MailMessageSummary>> results = new(
        [
            Page([Summary("gmail:result", isUnread: true)], null),
            Page([Summary("gmail:result", isUnread: true, isStarred: true)], null),
            Page([], null)
        ]);
        provider.SetNormalPage(account.Id, null, Page([], null));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(results.Dequeue());
        RecordingMailboxService mailbox = new();
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await SearchAsync(viewModel, account, "is:unread");

        MailMessageSummary result = Assert.Single(viewModel.Messages);
        await viewModel.ToggleStarCommand.ExecuteAsync(result);
        Assert.True(Assert.Single(viewModel.Messages).IsStarred);

        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.MarkSelectedReadCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasMessages);
        Assert.Equal([MailboxOperation.Star, MailboxOperation.Read], mailbox.Operations);
        Assert.Equal(3, provider.SearchCalls.Count);
    }

    [Fact]
    public async Task DetailReadMutationRefreshesSearchMembership()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        Queue<MailPage<MailMessageSummary>> results = new(
        [
            Page([Summary("gmail:result", isUnread: true)], null),
            Page([], null)
        ]);
        provider.SetNormalPage(account.Id, null, Page([], null));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(results.Dequeue());
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SearchAsync(viewModel, account, "is:unread");
        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSearchActive);
        Assert.False(viewModel.HasMessages);
        Assert.Equal(2, provider.SearchCalls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchArchiveAndTrashActionsUseG4PipelineAndRerunQuery(bool archive)
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        Queue<MailPage<MailMessageSummary>> results = new(
        [
            Page([Summary("gmail:result")], null),
            Page([], null)
        ]);
        provider.SetNormalPage(account.Id, null, Page([], null));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(results.Dequeue());
        RecordingMailboxService mailbox = new();
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await SearchAsync(viewModel, account, archive ? "in:inbox" : "subject:remove");
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        if (archive)
        {
            await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);
        }
        else
        {
            await viewModel.DeleteSelectedCommand.ExecuteAsync(null);
        }

        Assert.False(viewModel.HasMessages);
        Assert.Equal(archive ? MailboxOperation.Archive : MailboxOperation.Trash, Assert.Single(mailbox.Operations));
        Assert.Equal(2, provider.SearchCalls.Count);
    }

    [Fact]
    public async Task SearchLabelActionUsesG4PipelineAndRerunsQuery()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        Queue<MailPage<MailMessageSummary>> results = new(
        [
            Page([Summary("gmail:result", labels: [GmailSystemFolders.Inbox, "label-project"])], null),
            Page([], null)
        ]);
        provider.SetNormalPage(account.Id, null, Page([], null));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(results.Dequeue());
        RecordingMailboxService mailbox = new()
        {
            Labels = [new GmailUserLabel("label-project", "Проект")]
        };
        using MailInboxViewModel viewModel = ViewModel(provider, mailbox);
        await SearchAsync(viewModel, account, "label:Проект");
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.OpenLabelsForSelectionCommand.ExecuteAsync(null);

        GmailUserLabelOption label = Assert.Single(viewModel.UserLabels);
        Assert.True(label.IsApplied);
        await viewModel.ToggleUserLabelCommand.ExecuteAsync(label);

        Assert.False(viewModel.HasMessages);
        Assert.Equal(MailboxOperation.Label, Assert.Single(mailbox.Operations));
    }

    [Fact]
    public async Task SearchAuthorizationFailureOffersGoogleLoginAndSuccessfulReauthRetriesQuery()
    {
        MailAccount account = Account("gmail@example.test");
        int calls = 0;
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([], null));
        provider.SearchHandler = (_, _, _, _) => ++calls == 1
            ? Task.FromException<MailPage<MailMessageSummary>>(new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google."))
            : Task.FromResult(Page([Summary("gmail:after-login")], null));
        RecordingReauthenticationService reauthentication = new();
        using MailInboxViewModel viewModel = ViewModel(provider, reauthentication: reauthentication);
        await SearchAsync(viewModel, account, "after:2026/09/01");

        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.True(viewModel.ReauthenticateGmailCommand.CanExecute(null));
        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.Equal(1, reauthentication.Calls);
        Assert.False(viewModel.RequiresGmailReauthentication);
        Assert.Equal("after:2026/09/01", viewModel.ActiveSearchQuery);
        Assert.Equal("gmail:after-login", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal(2, provider.SearchCalls.Count);
    }

    [Fact]
    public async Task InvalidQueryAndNoResultsHaveFriendlySearchStates()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([], null));
        provider.SearchHandler = (_, query, _, _) => query == "bad-query"
            ? Task.FromException<MailPage<MailMessageSummary>>(new MailReadException(
                MailReadFailureKind.InvalidSearchQuery,
                "Не удалось выполнить поиск. Проверьте запрос."))
            : Task.FromResult(Page([], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SearchAsync(viewModel, account, "bad-query");

        Assert.Equal("Не удалось выполнить поиск", viewModel.ErrorTitle);
        Assert.Equal("Не удалось выполнить поиск. Проверьте запрос.", viewModel.ListErrorDescription);
        Assert.DoesNotContain("exception", viewModel.ListErrorDescription!, StringComparison.OrdinalIgnoreCase);

        viewModel.SearchText = "nothing";
        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsEmpty);
        Assert.Equal("По вашему запросу ничего не найдено.", viewModel.EmptyListMessage);
    }

    [Fact]
    public async Task InboxFreshnessWaitsDuringSearchAndRefreshesAfterClear()
    {
        MailAccount account = Account("gmail@example.test");
        SearchReadProvider provider = new();
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:old")], null));
        provider.SearchHandler = (_, _, _, _) => Task.FromResult(Page([Summary("gmail:search")], null));
        using MailInboxViewModel viewModel = ViewModel(provider);
        await SearchAsync(viewModel, account, "is:unread");
        provider.SetNormalPage(account.Id, null, Page([Summary("gmail:fresh")], null));

        viewModel.OnNewMailDetected(account.Id, isAccountActivelyViewed: true);

        Assert.True(viewModel.IsInboxStale(account.Id));
        Assert.True(viewModel.IsSearchActive);
        Assert.Equal("gmail:search", Assert.Single(viewModel.Messages).MessageKey);

        viewModel.ClearSearchCommand.Execute(null);
        await viewModel.CurrentFolderLoadTask;

        Assert.False(viewModel.IsInboxStale(account.Id));
        Assert.Equal("gmail:fresh", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public void MailSearchUiHasCompactSubmitClearAndInlinePagination()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));

        Assert.Contains("Text=\"Поиск в почте\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("Command=\"{Binding SearchCommand}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("MaxWidth=\"560\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ClearSearchCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Enter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Escape\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding PreviousPageCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding NextPageCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding PageRangeText}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("<TranslateTransform Y=\"-3\" />", StringSplitOptions.None).Length - 1);
        int refreshIndex = xaml.IndexOf("Command=\"{Binding RefreshCommand}\"", StringComparison.Ordinal);
        int paginationIndex = xaml.IndexOf("x:Name=\"MailPaginationPanel\"", StringComparison.Ordinal);
        int selectionActionsIndex = xaml.IndexOf("Command=\"{Binding ArchiveSelectedCommand}\"", StringComparison.Ordinal);
        Assert.True(refreshIndex >= 0 && refreshIndex < paginationIndex);
        Assert.True(paginationIndex < selectionActionsIndex);
        string paginationDeclaration = xaml[paginationIndex..xaml.IndexOf('>', paginationIndex)];
        Assert.DoesNotContain("HorizontalAlignment=\"Right\"", paginationDeclaration, StringComparison.Ordinal);
        Assert.DoesNotContain("Загрузить ещё", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadMoreCommand", xaml, StringComparison.Ordinal);
        Assert.Equal(50, MailInboxViewModel.PageSize);
    }

    private static async Task SearchAsync(
        MailInboxViewModel viewModel,
        MailAccount account,
        string query)
    {
        await viewModel.ActivateAsync(account);
        viewModel.SearchText = query;
        await viewModel.SearchCommand.ExecuteAsync(null);
    }

    private static MailInboxViewModel ViewModel(
        SearchReadProvider provider,
        RecordingMailboxService? mailbox = null,
        RecordingReauthenticationService? reauthentication = null) =>
        new(new ProviderFactory(provider, mailbox, reauthentication));

    private static MailAccount Account(string address) => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Gmail,
        EmailAddress = address,
        CredentialKey = Guid.NewGuid().ToString("N"),
        IsEnabled = true
    };

    private static MailMessageSummary Summary(
        string key,
        bool isUnread = true,
        bool isStarred = false,
        IReadOnlyCollection<string>? labels = null)
    {
        HashSet<string> providerLabels = labels?.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>([GmailSystemFolders.Inbox], StringComparer.Ordinal);
        if (isUnread)
        {
            providerLabels.Add(GmailSystemFolders.Unread);
        }
        if (isStarred)
        {
            providerLabels.Add(GmailSystemFolders.Starred);
        }

        return new MailMessageSummary(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            isUnread)
        {
            IsStarred = isStarred,
            ProviderLabelIds = providerLabels,
            AttachmentSummary = new MailMessageAttachmentSummary(
                1,
                [new MailAttachmentPreviewItem("document.pdf", "application/pdf", 42)])
        };
    }

    private static IReadOnlyList<MailMessageSummary> Summaries(string prefix, int count) =>
        Enumerable.Range(1, count)
            .Select(index => Summary($"gmail:{prefix}-{index}"))
            .ToArray();

    private static MailPage<MailMessageSummary> Page(
        IReadOnlyList<MailMessageSummary> items,
        string? token,
        long? totalCount = null) => new(items, token, totalCount);

    private static TaskCompletionSource<MailPage<MailMessageSummary>> PendingPage() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class RecordingGmailApiClient : IGmailApiReadClient
    {
        public List<SearchCall> SearchCalls { get; } = [];
        public List<(Guid AccountId, string LabelId)> FolderCalls { get; } = [];
        public GmailApiInboxPage FolderResult { get; set; } = new([], null);
        public GmailApiInboxPage SearchResult { get; set; } = new([], null);

        public Task<GmailApiInboxPage> GetInboxPageAsync(MailCredential credential, Guid accountId, string? pageToken, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(FolderResult);

        public Task<GmailApiInboxPage> GetFolderPageAsync(
            MailCredential credential,
            Guid accountId,
            string labelId,
            bool includeSpamTrash,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            FolderCalls.Add((accountId, labelId));
            return Task.FromResult(FolderResult);
        }

        public Task<GmailApiInboxPage> SearchPageAsync(MailCredential credential, Guid accountId, string query, string? pageToken, int pageSize, CancellationToken cancellationToken = default)
        {
            SearchCalls.Add(new SearchCall(accountId, query, pageToken, pageSize));
            return Task.FromResult(SearchResult);
        }

        public Task<GmailApiRawMessage> GetRawMessageAsync(MailCredential credential, Guid accountId, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiRawMessage([], false));
    }

    private sealed class SearchReadProvider : IMailReadProvider, IMailSearchProvider, IMailMessageStateProvider
    {
        private readonly Dictionary<(Guid AccountId, string? Token), MailPage<MailMessageSummary>> _normalPages = [];

        public Func<Guid, string, string?, CancellationToken, Task<MailPage<MailMessageSummary>>> SearchHandler { get; set; } =
            (_, _, _, _) => Task.FromResult(Page([], null));
        public List<SearchCall> SearchCalls { get; } = [];
        public List<(Guid AccountId, string? Token, int PageSize)> NormalCalls { get; } = [];

        public void SetNormalPage(Guid accountId, string? token, MailPage<MailMessageSummary> page) =>
            _normalPages[(accountId, token)] = page;

        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>
            ([
                MailFolderCatalog.Create(MailFolderKind.Inbox, GmailSystemFolders.Inbox),
                MailFolderCatalog.Create(MailFolderKind.Starred, GmailSystemFolders.Starred),
                MailFolderCatalog.Create(MailFolderKind.Trash, GmailSystemFolders.Trash)
            ]);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(MailAccount account, MailFolder folder, string? continuationToken, int pageSize, CancellationToken cancellationToken = default)
        {
            NormalCalls.Add((account.Id, continuationToken, pageSize));
            return Task.FromResult(_normalPages.GetValueOrDefault((account.Id, continuationToken)) ?? Page([], null));
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(MailAccount account, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Inbox(), continuationToken, pageSize, cancellationToken);

        public Task<MailPage<MailMessageSummary>> SearchAsync(MailAccount account, string query, string? continuationToken, int pageSize, CancellationToken cancellationToken = default)
        {
            SearchCalls.Add(new SearchCall(account.Id, query, continuationToken, pageSize));
            return SearchHandler(account.Id, query, continuationToken, cancellationToken);
        }

        public Task<MailMessageContent> GetMessageAsync(MailAccount account, MailFolder folder, string messageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailMessageContent(
                messageKey,
                $"Subject {messageKey}",
                "Sender",
                "sender@example.test",
                account.EmailAddress,
                DateTimeOffset.UtcNow,
                MailMessageBodyKind.PlainText,
                "Body",
                [],
                true,
                false));

        public Task<MailMessageContent> GetMessageAsync(MailAccount account, string messageKey, CancellationToken cancellationToken = default) =>
            GetMessageAsync(account, MailFolderCatalog.Inbox(), messageKey, cancellationToken);

        public Task<MailReadStateCapability> GetReadStateCapabilityAsync(MailAccount account, MailFolder folder, CancellationToken cancellationToken = default) =>
            Task.FromResult(MailReadStateCapability.Available);

        public Task SetReadStateAsync(MailAccount account, MailFolder folder, string messageKey, bool isRead, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ProviderFactory(
        SearchReadProvider provider,
        RecordingMailboxService? mailbox,
        RecordingReauthenticationService? reauthentication) : IMailReadProviderFactory
    {
        public IGmailMailboxManagementService? GmailMailboxManagementService => mailbox;
        public IGmailReauthenticationService? GmailReauthenticationService => reauthentication;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private enum MailboxOperation
    {
        Star,
        Read,
        Archive,
        Trash,
        Label
    }

    private sealed class RecordingMailboxService : IGmailMailboxManagementService
    {
        public List<MailboxOperation> Operations { get; } = [];
        public IReadOnlyList<GmailUserLabel> Labels { get; init; } = [];

        public Task<GmailUserLabelResult> GetUserLabelsAsync(MailAccount account, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailUserLabelResult(Labels));

        public Task<GmailMailboxMutationResult> SetStarredAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, bool isStarred, CancellationToken cancellationToken = default) =>
            Result(MailboxOperation.Star, messageKeys);

        public Task<GmailMailboxMutationResult> SetReadStateAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, bool isRead, CancellationToken cancellationToken = default) =>
            Result(MailboxOperation.Read, messageKeys);

        public Task<GmailMailboxMutationResult> ArchiveAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(MailboxOperation.Archive, messageKeys);

        public Task<GmailMailboxMutationResult> MoveToTrashAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(MailboxOperation.Trash, messageKeys);

        public Task<GmailMailboxMutationResult> SetUserLabelAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, string labelId, bool isApplied, CancellationToken cancellationToken = default) =>
            Result(MailboxOperation.Label, messageKeys);

        public void RemoveAccount(Guid accountId)
        {
        }

        private Task<GmailMailboxMutationResult> Result(MailboxOperation operation, IReadOnlyCollection<string> keys)
        {
            Operations.Add(operation);
            return Task.FromResult(new GmailMailboxMutationResult(keys.ToArray(), []));
        }
    }

    private sealed class RecordingReauthenticationService : IGmailReauthenticationService
    {
        public int Calls { get; private set; }

        public Task<GmailReauthenticationResult> ReauthenticateAsync(MailAccount account, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(GmailReauthenticationResult.Success());
        }
    }

    private sealed record SearchCall(Guid AccountId, string Query, string? PageToken, int PageSize);
}
