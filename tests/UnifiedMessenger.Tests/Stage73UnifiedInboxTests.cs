using System.Net;
using System.Net.Http;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HtmlAgilityPack;
using MailKit;
using Microsoft.Web.WebView2.Core;
using MimeKit;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.Tests;

public sealed class Stage73UnifiedInboxTests
{
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xD9];

    [Fact]
    public void GmailSummary_MapsToProviderNeutralModel()
    {
        GmailApiSummaryData source = new(
            "gmail-id",
            "Subject",
            "John Smith <john@example.test>",
            1_700_000_000_000,
            "Short preview",
            ["INBOX", "UNREAD"]);

        MailMessageSummary summary = GmailMailReadProvider.MapSummary(source);

        Assert.Equal("gmail:gmail-id", summary.MessageKey);
        Assert.Equal("John Smith", summary.FromDisplayName);
        Assert.Equal("john@example.test", summary.FromAddress);
        Assert.Equal("Subject", summary.Subject);
        Assert.Equal("Short preview", summary.Preview);
        Assert.True(summary.IsUnread);
    }

    [Fact]
    public void ImapSummary_MapsToTheSameProviderNeutralModel()
    {
        ImapSummaryData source = new(
            42,
            "IMAP subject",
            "IMAP Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            true);

        MailMessageSummary summary = ImapMailReadProvider.MapSummary(source);

        Assert.Equal("imap:42", summary.MessageKey);
        Assert.Equal("IMAP Sender", summary.FromDisplayName);
        Assert.True(summary.IsUnread);
        Assert.IsType<MailMessageSummary>(summary);
    }

    [Fact]
    public void GmailAndImap_ImplementTheSameUiFacingContract()
    {
        Assert.Contains(typeof(IMailReadProvider), typeof(GmailMailReadProvider).GetInterfaces());
        Assert.Contains(typeof(IMailReadProvider), typeof(ImapMailReadProvider).GetInterfaces());
        Assert.DoesNotContain(
            typeof(MailInboxViewModel).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("Gmail", StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("Imap", StringComparison.Ordinal));
    }

    [Fact]
    public void MailMessageContent_IsProviderNeutralAndContainsNoProtocolObject()
    {
        Type[] propertyTypes = typeof(MailMessageContent).GetProperties().Select(property => property.PropertyType).ToArray();

        Assert.DoesNotContain(propertyTypes, type => type.Namespace?.StartsWith("Google", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(propertyTypes, type => type.Namespace?.StartsWith("MailKit", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(propertyTypes, type => type.Namespace?.StartsWith("MimeKit", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task GmailProvider_PreservesPaginationToken()
    {
        FakeGmailApiReadClient api = new()
        {
            Page = new GmailApiInboxPage([], "next-gmail-page")
        };
        GmailMailReadProvider provider = CreateGmailProvider(api);

        MailPage<MailMessageSummary> result = await provider.GetInboxPageAsync(
            CreateAccount(MailProviderType.Gmail),
            "current-page",
            30);

        Assert.Equal("current-page", api.ReceivedPageToken);
        Assert.Equal("next-gmail-page", result.ContinuationToken);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task ImapProvider_PreservesOlderPageCursor()
    {
        FakeImapInboxClient imap = new()
        {
            Page = new ImapInboxPageData([], "imap-index:17")
        };
        ImapMailReadProvider provider = CreateImapProvider(imap);

        MailPage<MailMessageSummary> result = await provider.GetInboxPageAsync(
            CreateAccount(MailProviderType.Yandex),
            "imap-index:47",
            30);

        Assert.Equal("imap-index:47", imap.ReceivedCursor);
        Assert.Equal("imap-index:17", result.ContinuationToken);
        Assert.Equal(17, MailKitImapInboxClient.ParseCursor(result.ContinuationToken));
    }

    [Fact]
    public async Task LoadMore_AppendsWithoutDuplicatingMessageKeys()
    {
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([Summary("one")], "next"));
        provider.EnqueuePage(Page([Summary("one"), Summary("two")], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);

        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["one", "two"], viewModel.Messages.Select(message => message.MessageKey));
    }

    [Fact]
    public void GmailReadPath_RemainsReadonlyAndBounded()
    {
        Assert.Equal(GmailOAuthConstants.ReadOnlyScope, "https://www.googleapis.com/auth/gmail.readonly");
        Assert.Equal("INBOX", GmailApiReadClient.InboxLabel);
        Assert.InRange(GmailApiReadClient.MaximumMetadataConcurrency, 4, 6);
        Assert.DoesNotContain(
            typeof(IGmailApiReadClient).GetMethods(),
            method => method.Name.Contains("Modify", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Send", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImapInbox_IsOpenedReadOnly()
    {
        Assert.Equal(FolderAccess.ReadOnly, MailKitImapInboxClient.InboxAccess);
    }

    [Fact]
    public async Task SelectingMessage_DoesNotChangeServerUnreadStateLocally()
    {
        QueueReadProvider provider = new();
        MailMessageSummary unread = Summary("unread", isUnread: true);
        provider.EnqueuePage(Page([unread], null));
        provider.Message = Content("unread", isUnread: true);
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));

        viewModel.SelectedMessageSummary = unread;
        await viewModel.CurrentMessageLoadTask;

        Assert.True(Assert.Single(viewModel.Messages).IsUnread);
        Assert.True(viewModel.SelectedMessageContent!.IsUnread);
        Assert.Equal(0, provider.MutationCount);
    }

    [Fact]
    public async Task Refresh_UsesReadContractAndDoesNotMutateFlags()
    {
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([Summary("first", true)], null));
        provider.EnqueuePage(Page([Summary("fresh", true)], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Yandex));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("fresh", Assert.Single(viewModel.Messages).MessageKey);
        Assert.All(provider.ReceivedPageTokens, Assert.Null);
        Assert.Equal(0, provider.MutationCount);
    }

    [Fact]
    public async Task Refresh_PreservesSelectedMessageKeyAndRestoresSelection()
    {
        MailMessageSummary original = Summary("selected");
        MailMessageSummary refreshed = original with { Subject = "Updated subject" };
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([original], null));
        provider.EnqueuePage(Page([refreshed], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        viewModel.SelectedMessageSummary = original;
        await viewModel.CurrentMessageLoadTask;
        MailMessageContent visibleContent = Assert.IsType<MailMessageContent>(viewModel.SelectedMessageContent);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        MailMessageSummary restored = Assert.IsType<MailMessageSummary>(viewModel.SelectedMessageSummary);
        Assert.Equal("selected", restored.MessageKey);
        Assert.Same(Assert.Single(viewModel.Messages), restored);
        Assert.Equal("Updated subject", restored.Subject);
        Assert.Same(visibleContent, viewModel.SelectedMessageContent);
        Assert.Equal(["selected"], provider.ReceivedMessageKeys);
    }

    [Theory]
    [InlineData(MailMessageBodyKind.PlainText)]
    [InlineData(MailMessageBodyKind.SanitizedHtml)]
    public async Task Refresh_DoesNotClearVisibleMessageContentWhileLoading(
        MailMessageBodyKind bodyKind)
    {
        MailMessageSummary selected = Summary("selected");
        TaskCompletionSource<MailPage<MailMessageSummary>> pendingRefresh = NewPendingPage();
        int pageCall = 0;
        QueueReadProvider provider = new()
        {
            PageHandler = (_, _, _, _) => ++pageCall == 1
                ? Task.FromResult(Page([selected], null))
                : pendingRefresh.Task,
            Message = Content(
                "selected",
                bodyKind: bodyKind,
                body: bodyKind == MailMessageBodyKind.SanitizedHtml ? "<p>Body</p>" : "Body")
        };
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        viewModel.SelectedMessageSummary = selected;
        await viewModel.CurrentMessageLoadTask;
        MailMessageContent visibleContent = Assert.IsType<MailMessageContent>(viewModel.SelectedMessageContent);

        Task refresh = viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsListLoading);
        Assert.Equal("selected", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Same(visibleContent, viewModel.SelectedMessageContent);

        pendingRefresh.SetResult(Page([selected with { Subject = "Refreshed" }], null));
        await refresh;

        Assert.Same(visibleContent, viewModel.SelectedMessageContent);
        Assert.Equal("selected", viewModel.SelectedMessageSummary?.MessageKey);
    }

    [Fact]
    public async Task Refresh_DeduplicatesRefreshedSummaries()
    {
        MailMessageSummary summary = Summary("same");
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([summary], null));
        provider.EnqueuePage(Page([summary, summary, summary with { Subject = "Duplicate" }], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("same", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task RefreshAfterLoadMore_PreservesSelectedOlderMessage()
    {
        MailMessageSummary firstPageMessage = Summary("first");
        MailMessageSummary olderSelectedMessage = Summary("older");
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([firstPageMessage], "next"));
        provider.EnqueuePage(Page([olderSelectedMessage], null));
        provider.EnqueuePage(Page([firstPageMessage with { Subject = "Refreshed" }], "next"));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        viewModel.SelectedMessageSummary = olderSelectedMessage;
        await viewModel.CurrentMessageLoadTask;
        MailMessageContent visibleContent = Assert.IsType<MailMessageContent>(viewModel.SelectedMessageContent);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("older", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.Same(visibleContent, viewModel.SelectedMessageContent);
        Assert.Equal(2, viewModel.Messages.Select(message => message.MessageKey).Distinct().Count());
    }

    [Fact]
    public async Task StaleRefresh_CannotRestoreMailAfterWebNavigation()
    {
        MailMessageSummary selected = Summary("selected");
        TaskCompletionSource<MailPage<MailMessageSummary>> pendingRefresh = NewPendingPage();
        int pageCall = 0;
        QueueReadProvider provider = new()
        {
            PageHandler = (_, _, _, _) => ++pageCall == 1
                ? Task.FromResult(Page([selected], null))
                : pendingRefresh.Task
        };
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        viewModel.SelectedMessageSummary = selected;
        await viewModel.CurrentMessageLoadTask;

        Task refresh = viewModel.RefreshCommand.ExecuteAsync(null);
        await viewModel.ActivateAsync(null);
        pendingRefresh.SetResult(Page([selected with { Subject = "Stale" }], null));
        await refresh;

        Assert.Null(viewModel.ActiveAccount);
        Assert.Null(viewModel.SelectedMessageSummary);
        Assert.Null(viewModel.SelectedMessageContent);
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public async Task SelectingMailAccount_LoadsInboxAndExposesLoadingState()
    {
        TaskCompletionSource<MailPage<MailMessageSummary>> pending = NewPendingPage();
        QueueReadProvider provider = new() { PageHandler = (_, _, _, _) => pending.Task };
        using MailInboxViewModel viewModel = CreateViewModel(provider);

        Task activation = viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));

        Assert.True(viewModel.IsInitialLoading);
        pending.SetResult(Page([Summary("loaded")], null));
        await activation;
        Assert.False(viewModel.IsListLoading);
        Assert.Equal("loaded", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task SelectingWebAccount_CancelsAndIgnoresStaleMailResponse()
    {
        TaskCompletionSource<MailPage<MailMessageSummary>> pending = NewPendingPage();
        QueueReadProvider provider = new() { PageHandler = (_, _, _, _) => pending.Task };
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        Task activation = viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));

        await viewModel.ActivateAsync(null);
        pending.SetResult(Page([Summary("stale")], null));
        await activation;

        Assert.Null(viewModel.ActiveAccount);
        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.IsActive);
    }

    [Fact]
    public async Task FastMailAccountSwitch_DoesNotCrossContaminateState()
    {
        MailAccount first = CreateAccount(MailProviderType.Gmail);
        MailAccount second = CreateAccount(MailProviderType.Yandex);
        TaskCompletionSource<MailPage<MailMessageSummary>> firstPending = NewPendingPage();
        QueueReadProvider provider = new()
        {
            PageHandler = (account, _, _, _) => account.Id == first.Id
                ? firstPending.Task
                : Task.FromResult(Page([Summary("second")], null))
        };
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        Task firstActivation = viewModel.ActivateAsync(first);

        await viewModel.ActivateAsync(second);
        firstPending.SetResult(Page([Summary("first-stale")], null));
        await firstActivation;

        Assert.Equal(second.Id, viewModel.ActiveAccount!.Id);
        Assert.Equal("second", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task EmptyInbox_ProducesEmptyState()
    {
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);

        await viewModel.ActivateAsync(CreateAccount(MailProviderType.MailRu));

        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.HasListError);
    }

    [Fact]
    public async Task ErrorState_IsSanitizedAndRetryLoadsInbox()
    {
        QueueReadProvider provider = new();
        provider.EnqueueFailure(new MailReadException(
            MailReadFailureKind.AuthenticationFailed,
            "Не удалось войти в почту. Проверьте пароль приложения."));
        provider.EnqueuePage(Page([Summary("after-retry")], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);

        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Yandex));
        Assert.True(viewModel.HasBlockingListError);
        Assert.Equal("Не удалось войти в почту", viewModel.ErrorTitle);

        await viewModel.RetryCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasListError);
        Assert.Equal("after-retry", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task SelectingSummary_LoadsOnlyThatMessageContent()
    {
        QueueReadProvider provider = new();
        MailMessageSummary summary = Summary("selected");
        provider.EnqueuePage(Page([summary], null));
        provider.Message = Content("selected");
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.GenericImap));

        viewModel.SelectedMessageSummary = summary;
        await viewModel.CurrentMessageLoadTask;

        Assert.Equal("selected", viewModel.SelectedMessageContent!.MessageKey);
        Assert.Equal(["selected"], provider.ReceivedMessageKeys);
    }

    [Fact]
    public async Task Refresh_ReplacesFirstPageAndClearsPagination()
    {
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([Summary("old")], "older"));
        provider.EnqueuePage(Page([Summary("new")], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("new", Assert.Single(viewModel.Messages).MessageKey);
        Assert.False(viewModel.HasMore);
    }

    [Fact]
    public async Task PerAccountSessionCache_RemainsIndependent()
    {
        MailAccount gmail = CreateAccount(MailProviderType.Gmail);
        MailAccount yandex = CreateAccount(MailProviderType.Yandex);
        QueueReadProvider provider = new()
        {
            PageHandler = (account, _, _, _) => Task.FromResult(
                Page([Summary(account.Id == gmail.Id ? "gmail" : "yandex")], null))
        };
        using MailInboxViewModel viewModel = CreateViewModel(provider);

        await viewModel.ActivateAsync(gmail);
        await viewModel.ActivateAsync(yandex);
        await viewModel.ActivateAsync(gmail);

        Assert.Equal("gmail", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal(2, provider.PageCallCount);
    }

    [Fact]
    public void GmailSelection_HasNoWebView2ControllerDependency()
    {
        Type[] dependencies = typeof(MailInboxViewModel)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(dependencies, type => type.Name.Contains("WebView", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(MailInboxView).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType.Name.Contains("WebView", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoteImagesBanner_ButtonKeepsCompactLabelAndAdaptiveWidth()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src",
            "UnifiedMessenger.App",
            "Views",
            "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement button = Assert.Single(
            view.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "ShowRemoteImages_Click");

        Assert.Equal("{Binding RemoteImagesButtonText}", (string?)button.Attribute("Content"));
        Assert.Null(button.Attribute("Width"));
        Assert.Null(button.Attribute("MaxWidth"));
        Assert.Equal("18,8", (string?)button.Attribute("Padding"));
        Assert.Equal("Center", (string?)button.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Center", (string?)button.Attribute("VerticalContentAlignment"));

        XElement actionPanel = Assert.IsType<XElement>(button.Parent);
        Assert.Equal(presentation + "WrapPanel", actionPanel.Name);
        XElement bannerPanel = Assert.IsType<XElement>(actionPanel.Parent);
        Assert.Contains(
            bannerPanel.Elements(presentation + "TextBlock"),
            text => (string?)text.Attribute("TextWrapping") == "Wrap");

        using MailInboxViewModel viewModel = CreateViewModel(new QueueReadProvider());
        Assert.Equal("Показать", viewModel.RemoteImagesButtonText);
    }

    [Fact]
    public void MimeExtractor_PrefersRealHtmlMimePart()
    {
        MimeMessage message = CreateMessage(
            new MultipartAlternative
            {
                new TextPart("plain") { Text = "Plain body" },
                new TextPart("html") { Text = "<p>HTML body</p>" }
            });

        MailMessageContent content = CreateExtractor().Extract("key", message, true);

        Assert.Equal(MailMessageBodyKind.SanitizedHtml, content.BodyKind);
        Assert.Contains("<p>HTML body</p>", content.SanitizedHtmlContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain body", content.SanitizedHtmlContent, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlMail_KeepsSafeVisualStructure()
    {
        MimeMessage message = CreateMessage(
            new TextPart("html")
            {
                Text = "<h1>Hello</h1><p style='color:#123456;text-align:center'>World <strong>Again</strong></p>" +
                    "<table><tbody><tr><td>Cell</td></tr></tbody></table><ul><li>Item</li></ul>"
            });

        MailMessageContent content = CreateExtractor().Extract("key", message, false);

        Assert.Equal(MailMessageBodyKind.SanitizedHtml, content.BodyKind);
        Assert.Contains("<h1>Hello</h1>", content.SanitizedHtmlContent, StringComparison.Ordinal);
        Assert.Contains("<strong>Again</strong>", content.SanitizedHtmlContent, StringComparison.Ordinal);
        Assert.Contains("<table>", content.SanitizedHtmlContent, StringComparison.Ordinal);
        Assert.Contains("<li>Item</li>", content.SanitizedHtmlContent, StringComparison.Ordinal);
        Assert.Contains("text-align: center", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HtmlAnchor_WithDescriptiveText_DoesNotAppendRawHref()
    {
        const string href = "https://accounts.google.com/long/settings/path";
        MimeMessage message = CreateMessage(
            new TextPart("html")
            {
                Text = $"<p>Перед ссылкой <a href='{href}'><span>Открыть настройки</span><span>&lt;{href}&gt;</span></a> после ссылки.</p>"
            });

        string body = CreateExtractor().Extract("key", message, false).SanitizedHtmlContent;
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(body);
        HtmlNode anchor = Assert.Single(document.DocumentNode.SelectNodes("//a"));

        Assert.Equal("Открыть настройки", anchor.InnerText.Trim());
        Assert.Equal(href, anchor.GetAttributeValue("href", string.Empty));
        Assert.DoesNotContain($"&lt;{href}&gt;", anchor.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlAnchor_WhoseTextEqualsUrl_EmitsUrlOnlyOnce()
    {
        const string href = "https://example.test/settings";
        MimeMessage message = CreateMessage(
            new TextPart("html") { Text = $"<p><a href='{href}'>{href}</a></p>" });

        string body = CreateExtractor().Extract("key", message, false).SanitizedHtmlContent;
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(body);
        string visible = HtmlEntity.DeEntitize(document.DocumentNode.InnerText).Trim();

        Assert.Equal(href, visible);
        Assert.Equal(1, CountOccurrences(visible, href));
    }

    [Fact]
    public void HtmlAnchor_WithoutMeaningfulText_DoesNotCreateExecutableFallback()
    {
        const string href = "https://example.test/fallback";
        MimeMessage message = CreateMessage(
            new TextPart("html") { Text = $"<p><a href='{href}'>&nbsp;&#8203;</a></p>" });

        string body = CreateExtractor().Extract("key", message, false).SanitizedHtmlContent;

        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(body);
        Assert.Equal(href, HtmlEntity.DeEntitize(document.DocumentNode.InnerText).Trim());
        Assert.DoesNotContain("javascript:", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HtmlMultipleLinks_AreSeparatedWithoutDuplicatedHrefs()
    {
        MimeMessage message = CreateMessage(
            new TextPart("html")
            {
                Text = "<p><a href='https://one.invalid/path'>Первая ссылка</a><a href='https://two.invalid/path'>Вторая ссылка</a></p>"
            });

        string body = CreateExtractor().Extract("key", message, false).SanitizedHtmlContent;
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(body);

        Assert.Equal(2, document.DocumentNode.SelectNodes("//a").Count);
        Assert.Contains("Первая ссылка", document.DocumentNode.InnerText, StringComparison.Ordinal);
        Assert.Contains("Вторая ссылка", document.DocumentNode.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlParagraphsAndLists_RemainStructured()
    {
        MimeMessage message = CreateMessage(
            new TextPart("html")
            {
                Text = "<p>Первый абзац.</p><p>Второй абзац.</p><ul><li>Первый пункт</li><li>Второй пункт</li></ul>"
            });

        string body = CreateExtractor().Extract("key", message, false).SanitizedHtmlContent;

        Assert.Equal(2, CountOccurrences(body, "<p>"));
        Assert.Equal(2, CountOccurrences(body, "<li>"));
    }

    [Fact]
    public void PlainTextMessage_PresentationIsUnaffectedByHtmlLinkRules()
    {
        const string plain = "Открыть настройки <https://example.test/path>";
        MimeMessage message = CreateMessage(new TextPart("plain") { Text = plain });

        MailMessageContent content = CreateExtractor().Extract("key", message, false);

        Assert.Equal(MailMessageBodyKind.PlainText, content.BodyKind);
        Assert.Equal(plain, content.PlainTextContent);
    }

    [Fact]
    public void HtmlSecurity_RemovesActiveContentHandlersAndUnsafeSchemes()
    {
        MimeMessage message = CreateMessage(
            new TextPart("html")
            {
                Text = "<p onclick='secretHandler()'>Safe text</p><script>secretScript()</script>" +
                    "<iframe src='https://frame.invalid/'>frame</iframe><form action='https://post.invalid/'>" +
                    "<input value='secret'><button>Submit</button></form><a href='javascript:alert(1)'>Unsafe</a>"
            });

        string html = CreateExtractor().Extract("key", message, false).SanitizedHtmlContent;

        Assert.Contains("Safe text", html, StringComparison.Ordinal);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("input", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("button", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HtmlSecurity_RemoteImageCssFontAndTrackingPixelCannotFetch()
    {
        const string remote = "https://sender-controlled.invalid/resource";
        MimeMessage message = CreateMessage(
            new TextPart("html")
            {
                Text = $"<link rel='stylesheet' href='{remote}.css'><style>@font-face {{ src:url('{remote}.woff2'); }} .x {{ background-image:url('{remote}.png'); }}</style>" +
                    $"<p class='x' style=\"background-image:url('{remote}.png');color:#123456\">Visible</p>" +
                    $"<img src='{remote}.png' width='1' height='1' alt='Remote image'>"
            });

        MailMessageContent content = CreateExtractor().Extract("key", message, false);
        string html = content.SanitizedHtmlContent;

        Assert.DoesNotContain(remote, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MailHtmlSanitizer.RemoteImageIdAttribute, html, StringComparison.Ordinal);
        Assert.Contains("Remote image", html, StringComparison.Ordinal);
        Assert.Contains("color", html, StringComparison.OrdinalIgnoreCase);
        MailRemoteImageReference image = Assert.Single(content.RemoteImages);
        Assert.Equal(new Uri($"{remote}.png"), image.SourceUri);
        Assert.False(new MailRendererNavigationPolicy().IsAllowedInRendererResource(
            new Uri(remote),
            CoreWebView2WebResourceContext.Image));
    }

    [Fact]
    public void InlineCidPngAndJpeg_AreRenderedFromMimeMemoryOnly()
    {
        MultipartRelated related = new();
        related.Add(new TextPart("html")
        {
            Text = "<p>Inline</p><img src='cid:png-image'><img src='cid:jpeg-image'>"
        });
        related.Add(CreateInlineImage("png", "png-image", PngBytes));
        related.Add(CreateInlineImage("jpeg", "jpeg-image", JpegBytes));
        MailMessageContent content = CreateExtractor().Extract("cid", CreateMessage(related), false);

        Assert.Contains("data:image/png;base64,", content.SanitizedHtmlContent, StringComparison.Ordinal);
        Assert.Contains("data:image/jpeg;base64,", content.SanitizedHtmlContent, StringComparison.Ordinal);
        Assert.Empty(content.RemoteImages);
    }

    [Fact]
    public async Task RemoteImage_DefaultExtractionPerformsNoNetworkRequest()
    {
        RecordingImageHttpClient client = new(CreateImageResponse(PngBytes, "image/png"));
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<p>Text</p><img src='https://images.example.test/pixel.png'>"
        });

        MailMessageContent content = CreateExtractor().Extract("remote", message, false);

        Assert.True(content.HasRemoteImages);
        Assert.Empty(client.Requests);
        Assert.DoesNotContain("https://images.example.test", new MailHtmlDocumentBuilder().Build(content), StringComparison.Ordinal);

        await loader.LoadAsync(content.RemoteImages);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task RemoteImage_ExplicitOptInUsesHeaderlessApplicationRequestAndDataOnlyDocument()
    {
        RecordingImageHttpClient client = new(CreateImageResponse(PngBytes, "image/png"));
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<style>@font-face{src:url('https://fonts.example.test/font.woff2')}</style>" +
                "<script src='https://scripts.example.test/app.js'>unsafe()</script>" +
                "<p>Visible</p><img src='https://images.example.test/photo.png'>"
        });
        MailMessageContent content = CreateExtractor().Extract("remote", message, false);

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(content.RemoteImages);
        string document = new MailHtmlDocumentBuilder().Build(content, loaded);

        HttpRequestMessage request = Assert.Single(client.Requests);
        Assert.Null(request.Headers.Authorization);
        Assert.Null(request.Headers.Referrer);
        Assert.Empty(request.Headers);
        Assert.Contains("data:image/png;base64,", document, StringComparison.Ordinal);
        Assert.DoesNotContain("images.example.test", document, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.example.test", document, StringComparison.Ordinal);
        Assert.DoesNotContain("scripts.example.test", document, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("font-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkedRemoteImage_PreservesVisualChildWithoutVisibleHrefAndUsesControlledLoader()
    {
        const string href = "https://example.com/";
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = $"<a href='{href}'><img src='https://cdn.example.com/logo.png' alt=''></a>"
        });
        RecordingImageHttpClient client = new(CreateImageResponse(PngBytes, "image/png"));
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());

        MailMessageContent content = CreateExtractor().Extract("linked-image", message, false);
        HtmlAgilityPack.HtmlDocument sanitized = new();
        sanitized.LoadHtml(content.SanitizedHtmlContent);
        HtmlNode anchor = Assert.IsType<HtmlNode>(sanitized.DocumentNode.SelectSingleNode("//a"));
        Assert.NotNull(anchor.SelectSingleNode(".//img"));
        Assert.DoesNotContain(href, HtmlEntity.DeEntitize(anchor.InnerText), StringComparison.Ordinal);
        Assert.Empty(client.Requests);

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(content.RemoteImages);
        string document = new MailHtmlDocumentBuilder().Build(content, loaded);
        Assert.Contains("<a", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,", document, StringComparison.Ordinal);

        RecordingExternalBrowser browser = new();
        MailRendererNavigationDisposition disposition = new MailRendererNavigationCoordinator(
            new MailRendererNavigationPolicy(),
            browser).RouteTopLevel(new Uri(href), isUserInitiated: true);
        Assert.Equal(MailRendererNavigationDisposition.ExternalOpened, disposition);
        Assert.Equal(new Uri(href), Assert.Single(browser.Opened));
    }

    [Fact]
    public void StyledCta_PreservesMeaningfulTextAndSafePresentationStyles()
    {
        const string href = "https://example.com/pay";
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = $"<a href='{href}' style='background:#1769aa;color:#fff;padding:12px 20px;" +
                "border:1px solid #1769aa;border-radius:8px;display:inline-block;text-align:center;" +
                "font-family:Arial,sans-serif;font-weight:bold'>Пополнить баланс</a>"
        });

        string html = CreateExtractor().Extract("cta", message, false).SanitizedHtmlContent;
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(html);
        HtmlNode anchor = Assert.IsType<HtmlNode>(document.DocumentNode.SelectSingleNode("//a"));
        string style = anchor.GetAttributeValue("style", string.Empty);

        Assert.Equal("Пополнить баланс", HtmlEntity.DeEntitize(anchor.InnerText).Trim());
        Assert.DoesNotContain(href, HtmlEntity.DeEntitize(anchor.InnerText), StringComparison.Ordinal);
        Assert.Contains("background-color", style, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color", style, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("padding", style, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("border-radius", style, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display", style, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddedStaticPresentationClass_PreservesGreenBackgroundAndTextColors()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<style>.status-panel{background-color:#198754;color:#ffffff;border-color:#146c43}" +
                ".status-panel{background-image:url('https://tracking.invalid/pixel.png')}</style>" +
                "<div class='status-panel'>Success</div>"
        });

        string html = CreateExtractor().Extract("static-green", message, false).SanitizedHtmlContent;
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(html);
        HtmlNode panel = Assert.IsType<HtmlNode>(document.DocumentNode.SelectSingleNode("//div"));
        string style = panel.GetAttributeValue("style", string.Empty);

        Assert.Contains("background-color", style, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            style.Contains("#198754", StringComparison.OrdinalIgnoreCase)
            || style.Contains("25, 135, 84", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("color", style, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("border-color", style, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracking.invalid", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YandexIdStylePattern_CompoundDescendantBackgroundImportantOverridesGrayInlineFallback()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<style>" +
                ".mail-shell > table.notice td.status.success{" +
                "background:#c5efb7 !important;color:#153b20;border-color:#86c995}" +
                "</style>" +
                "<div class='mail-shell'><table class='notice'><tr>" +
                "<td class='status success' style='background-color:#d9d9d9'>Success</td>" +
                "</tr></table></div>"
        });

        string html = CreateExtractor().Extract("selector-cascade-green", message, false).SanitizedHtmlContent;
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(html);
        HtmlNode cell = Assert.IsType<HtmlNode>(document.DocumentNode.SelectSingleNode("//td"));
        string style = cell.GetAttributeValue("style", string.Empty);

        Assert.Contains("background-color", style, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            style.Contains("#c5efb7", StringComparison.OrdinalIgnoreCase)
            || style.Contains("197, 239, 183", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("important", style, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaticGreenPanel_RemainsGreenWhileRemoteImageIsStillBlocked()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<style>.green-panel{background-color:#198754;color:#ffffff}</style>" +
                "<div class='green-panel'>Success</div>" +
                "<img src='https://images.example.test/remote.png'>"
        });

        MailMessageContent content = CreateExtractor().Extract("green-with-remote", message, false);
        string document = new MailHtmlDocumentBuilder().Build(content);

        Assert.Single(content.RemoteImages);
        Assert.Contains("background-color", document, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            document.Contains("#198754", StringComparison.OrdinalIgnoreCase)
            || document.Contains("25, 135, 84", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("images.example.test", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyBgColor_IsConvertedToSafeInlineBackgroundColor()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<table><tr><td bgcolor='#198754'>Success</td></tr></table>"
        });

        string html = CreateExtractor().Extract("legacy-green", message, false).SanitizedHtmlContent;

        Assert.Contains("background-color", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            html.Contains("#198754", StringComparison.OrdinalIgnoreCase)
            || html.Contains("25, 135, 84", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("bgcolor", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddedPresentationRules_DoNotAllowBackgroundUrlsOrComplexSelectorEscapes()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<style>.safe .nested{background-color:#198754}" +
                ".unsafe{background:url('https://tracking.invalid/pixel.png') #198754}" +
                "@import url('https://tracking.invalid/mail.css')</style>" +
                "<div class='safe'><span class='nested'>No complex selector inlining</span></div>" +
                "<div class='unsafe'>Static color only</div>"
        });

        string html = CreateExtractor().Extract("css-network-block", message, false).SanitizedHtmlContent;

        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracking.invalid", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Static color only", html, StringComparison.Ordinal);
        Assert.Contains("background-color", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            html.Contains("#198754", StringComparison.OrdinalIgnoreCase)
            || html.Contains("25, 135, 84", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MailRuRemoteBackgroundShorthand_PreservesImportantBlueFallbackAndWhiteForeground()
    {
        const string remoteBackground = "https://tracking.invalid/decorative-background.png";
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<style>" +
                ".mail-shell > table.notice td.primary{" +
                $"background:#087eff url('{remoteBackground}') center/cover no-repeat !important;" +
                "color:#ffffff !important}" +
                "</style>" +
                "<div class='mail-shell'><table class='notice'><tr>" +
                "<td class='primary' style='background-color:#d9d9d9'>Readable</td>" +
                "</tr></table></div>"
        });

        MailMessageContent content = CreateExtractor().Extract("mailru-blue-fallback", message, false);
        HtmlAgilityPack.HtmlDocument sanitized = new();
        sanitized.LoadHtml(content.SanitizedHtmlContent);
        HtmlNode cell = Assert.IsType<HtmlNode>(sanitized.DocumentNode.SelectSingleNode("//td"));
        string style = cell.GetAttributeValue("style", string.Empty);
        string document = new MailHtmlDocumentBuilder().Build(content);

        Assert.Contains("background-color", style, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            style.Contains("#087eff", StringComparison.OrdinalIgnoreCase)
            || style.Contains("8, 126, 255", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("color", style, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            style.Contains("#ffffff", StringComparison.OrdinalIgnoreCase)
            || style.Contains("255, 255, 255", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("important", style, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#d9d9d9", style, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(remoteBackground, content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(remoteBackground, document, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(content.RemoteImages);
    }

    [Fact]
    public void StaticGradientFallback_PreservesOnlySolidColorAndNeverTheGradientLayer()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<div style='background:linear-gradient(#004a99,#087eff) #087eff;color:#fff'>Readable</div>"
        });

        string html = CreateExtractor().Extract("gradient-solid-fallback", message, false).SanitizedHtmlContent;

        Assert.Contains("background-color", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            html.Contains("#087eff", StringComparison.OrdinalIgnoreCase)
            || html.Contains("8, 126, 255", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("gradient", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleLinkedSocialIcons_DoNotBecomeConcatenatedRawUrls()
    {
        const string firstHref = "https://example.com/social-one";
        const string secondHref = "https://example.com/social-two";
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = $"<a href='{firstHref}'><img src='https://cdn.example.com/one.png'></a>" +
                $"<a href='{secondHref}'><img src='https://cdn.example.com/two.png'></a>"
        });
        RecordingImageHttpClient client = new(
            () => CreateImageResponse(PngBytes, "image/png"));
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());

        MailMessageContent content = CreateExtractor().Extract("social", message, false);
        HtmlAgilityPack.HtmlDocument sanitized = new();
        sanitized.LoadHtml(content.SanitizedHtmlContent);
        Assert.Equal(2, sanitized.DocumentNode.SelectNodes("//a")?.Count);
        Assert.Equal(2, sanitized.DocumentNode.SelectNodes("//a/img")?.Count);
        Assert.DoesNotContain(firstHref, HtmlEntity.DeEntitize(sanitized.DocumentNode.InnerText), StringComparison.Ordinal);
        Assert.DoesNotContain(secondHref, HtmlEntity.DeEntitize(sanitized.DocumentNode.InnerText), StringComparison.Ordinal);
        Assert.Empty(client.Requests);

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(content.RemoteImages);
        string document = new MailHtmlDocumentBuilder().Build(content, loaded);
        Assert.Equal(2, CountOccurrences(document, "data:image/png;base64,"));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task SrcsetAndPicture_AreReducedToOpaqueImagesAndLoadedOnlyAfterOptIn()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<picture><source srcset='https://cdn.example.com/hero.webp 2x'>" +
                "<img src='https://cdn.example.com/fallback.png'></picture>" +
                "<img srcset='https://cdn.example.com/icon.png 1x' alt='Icon'>"
        });
        RecordingImageHttpClient client = new(
            () => CreateImageResponse(PngBytes, "image/png"));
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());

        MailMessageContent content = CreateExtractor().Extract("responsive", message, false);

        Assert.Equal(2, content.RemoteImages.Count);
        Assert.DoesNotContain("srcset", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<picture", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<source", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn.example.com", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.Requests);

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(content.RemoteImages);
        string document = new MailHtmlDocumentBuilder().Build(content, loaded);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(2, CountOccurrences(document, "data:image/png;base64,"));
    }

    [Fact]
    public void BackgroundShorthandWithRemoteUrl_IsRemovedByCssParsing()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<a href='https://example.com' style=\"background:url('https://cdn.example.com/track.png') #fff;" +
                "color:#111;padding:8px\">Safe text</a>"
        });

        string html = CreateExtractor().Extract("unsafe-background", message, false).SanitizedHtmlContent;

        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn.example.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Safe text", html, StringComparison.Ordinal);
        Assert.Contains("padding", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background-color", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteImage_NonImageContentTypeIsRejected()
    {
        RecordingImageHttpClient client = new(CreateImageResponse(PngBytes, "text/html"));
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(
            [new MailRemoteImageReference("image", new Uri("https://images.example.test/not-image"))]);

        Assert.Empty(loaded);
    }

    [Theory]
    [InlineData("binary/octet-stream")]
    [InlineData("application/octet-stream")]
    public async Task RemoteImage_GenericBinaryMimeWithValidPngSignatureIsRendered(string responseContentType)
    {
        Uri source = new("http://assets.example.test/logo.png");
        Uri redirected = new("https://assets.example.test/logo.png");
        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.MovedPermanently)
            {
                Headers = { Location = redirected }
            },
            CreateImageResponse(PngBytes, responseContentType)
        ]);
        RecordingImageHttpClient client = new(() => responses.Dequeue());
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = $"<img src='{source}'>"
        });
        MailMessageContent content = CreateExtractor().Extract("generic-binary-png", message, false);

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(content.RemoteImages);
        string document = new MailHtmlDocumentBuilder().Build(content, loaded);

        MailImageContent image = Assert.Single(loaded).Value;
        Assert.Equal("image/png", image.ContentType);
        Assert.Equal(PngBytes, image.Bytes.ToArray());
        Assert.Equal([source, redirected], client.Requests.Select(request => request.RequestUri));
        Assert.Contains("data:image/png;base64,", document, StringComparison.Ordinal);
        Assert.DoesNotContain("assets.example.test", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteImage_GenericBinaryMimeWithoutSupportedSignatureIsRejected()
    {
        RecordingImageHttpClient client = new(CreateImageResponse("not-an-image"u8.ToArray(), "binary/octet-stream"));
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(
            [new MailRemoteImageReference("image", new Uri("https://images.example.test/not-image.bin"))]);

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task RemoteImage_OversizedResponseIsRejected()
    {
        HttpResponseMessage response = CreateImageResponse(PngBytes, "image/png");
        response.Content.Headers.ContentLength = RemoteMailImageLoader.MaximumImageBytes + 1L;
        RecordingImageHttpClient client = new(response);
        RemoteMailImageLoader loader = new(client, new AllowAllImageUriValidator());

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(
            [new MailRemoteImageReference("image", new Uri("https://images.example.test/large.png"))]);

        Assert.Empty(loaded);
    }

    [Theory]
    [InlineData("file:///C:/secret.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("ftp://images.example.test/photo.png")]
    [InlineData("http://localhost/photo.png")]
    public async Task RemoteImage_UnsupportedOrLocalUriNeverReachesHttpClient(string source)
    {
        RecordingImageHttpClient client = new(CreateImageResponse(PngBytes, "image/png"));
        RemoteMailImageLoader loader = new(client, new RejectAllImageUriValidator());

        IReadOnlyDictionary<string, MailImageContent> loaded = await loader.LoadAsync(
            [new MailRemoteImageReference("image", new Uri(source))]);

        Assert.Empty(loaded);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task RemoteImageConsent_ResetsWhenSelectedMessageChanges()
    {
        MailMessageSummary first = Summary("first");
        MailMessageSummary second = Summary("second");
        QueueReadProvider provider = new()
        {
            MessageHandler = (_, key, _) => Task.FromResult(Content(
                key,
                bodyKind: MailMessageBodyKind.SanitizedHtml,
                body: "<img um-remote-image-id='remote-1'>",
                remoteImages:
                [
                    new MailRemoteImageReference("remote-1", new Uri($"https://images.example.test/{key}.png"))
                ]))
        };
        provider.EnqueuePage(Page([first, second], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        viewModel.SelectedMessageSummary = first;
        await viewModel.CurrentMessageLoadTask;
        Assert.True(viewModel.ShowRemoteImagesBanner);
        Assert.True(viewModel.CanShowRemoteImages);
        Assert.Equal("Показать", viewModel.RemoteImagesButtonText);

        viewModel.MarkRemoteImagesShown();
        Assert.False(viewModel.ShowRemoteImagesBanner);
        Assert.False(viewModel.CanShowRemoteImages);
        viewModel.SelectedMessageSummary = second;
        await viewModel.CurrentMessageLoadTask;

        Assert.False(viewModel.AreRemoteImagesShown);
        Assert.True(viewModel.ShowRemoteImagesBanner);
    }

    [Fact]
    public async Task RemoteImageConsent_SurvivesMailToWebAndSameMailMessage()
    {
        MailAccount account = CreateAccount(MailProviderType.Gmail);
        MailMessageSummary message = Summary("same-message");
        QueueReadProvider provider = CreateRemoteImageProvider(message);
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(account);
        viewModel.SelectedMessageSummary = message;
        await viewModel.CurrentMessageLoadTask;
        viewModel.MarkRemoteImagesShown();

        await viewModel.ActivateAsync(null);
        await viewModel.ActivateAsync(account);

        Assert.Equal("same-message", viewModel.SelectedMessageContent?.MessageKey);
        Assert.True(viewModel.AreRemoteImagesShown);
        Assert.False(viewModel.ShowRemoteImagesBanner);
    }

    [Fact]
    public async Task RemoteImageConsent_SurvivesGmailToYandexAndBack()
    {
        MailAccount gmail = CreateAccount(MailProviderType.Gmail);
        MailAccount yandex = CreateAccount(MailProviderType.Yandex);
        MailMessageSummary message = Summary("shared-looking-key");
        QueueReadProvider provider = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Page([message], null)),
            MessageHandler = (_, key, _) => Task.FromResult(RemoteImageContent(key))
        };
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(gmail);
        viewModel.SelectedMessageSummary = message;
        await viewModel.CurrentMessageLoadTask;
        viewModel.MarkRemoteImagesShown();

        await viewModel.ActivateAsync(yandex);
        viewModel.SelectedMessageSummary = message;
        await viewModel.CurrentMessageLoadTask;
        Assert.False(viewModel.AreRemoteImagesShown);

        await viewModel.ActivateAsync(gmail);

        Assert.True(viewModel.AreRemoteImagesShown);
        Assert.False(viewModel.ShowRemoteImagesBanner);
    }

    [Fact]
    public async Task RemoteImageConsent_DifferentAccountWithSameMessageKeyRemainsBlocked()
    {
        MailAccount firstAccount = CreateAccount(MailProviderType.Gmail);
        MailAccount secondAccount = CreateAccount(MailProviderType.Gmail);
        MailMessageSummary sameLookingMessage = Summary("same-key");
        QueueReadProvider provider = new()
        {
            PageHandler = (_, _, _, _) => Task.FromResult(Page([sameLookingMessage], null)),
            MessageHandler = (_, key, _) => Task.FromResult(RemoteImageContent(key))
        };
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(firstAccount);
        viewModel.SelectedMessageSummary = sameLookingMessage;
        await viewModel.CurrentMessageLoadTask;
        viewModel.MarkRemoteImagesShown();

        await viewModel.ActivateAsync(secondAccount);
        viewModel.SelectedMessageSummary = sameLookingMessage;
        await viewModel.CurrentMessageLoadTask;

        Assert.False(viewModel.AreRemoteImagesShown);
        Assert.True(viewModel.ShowRemoteImagesBanner);
    }

    [Fact]
    public async Task RemoteImageConsent_RefreshPreservesSameStableMessage()
    {
        MailMessageSummary message = Summary("same-message");
        QueueReadProvider provider = CreateRemoteImageProvider(message);
        provider.EnqueuePage(Page([message with { Subject = "Refreshed" }], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        viewModel.SelectedMessageSummary = message;
        await viewModel.CurrentMessageLoadTask;
        viewModel.MarkRemoteImagesShown();

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("same-message", viewModel.SelectedMessageSummary?.MessageKey);
        Assert.True(viewModel.AreRemoteImagesShown);
        Assert.False(viewModel.ShowRemoteImagesBanner);
    }

    [Fact]
    public async Task RemoteImageConsent_NewApplicationSessionStartsBlocked()
    {
        MailAccount account = CreateAccount(MailProviderType.Gmail);
        MailMessageSummary message = Summary("same-message");
        QueueReadProvider firstProvider = CreateRemoteImageProvider(message);
        using (MailInboxViewModel firstSession = CreateViewModel(firstProvider))
        {
            await firstSession.ActivateAsync(account);
            firstSession.SelectedMessageSummary = message;
            await firstSession.CurrentMessageLoadTask;
            firstSession.MarkRemoteImagesShown();
            Assert.True(firstSession.AreRemoteImagesShown);
        }

        QueueReadProvider restartedProvider = CreateRemoteImageProvider(message);
        using MailInboxViewModel restartedSession = CreateViewModel(restartedProvider);
        await restartedSession.ActivateAsync(account);
        restartedSession.SelectedMessageSummary = message;
        await restartedSession.CurrentMessageLoadTask;

        Assert.False(restartedSession.AreRemoteImagesShown);
        Assert.True(restartedSession.ShowRemoteImagesBanner);
    }

    [Fact]
    public void RemoteImageConsent_IsAbsentFromPersistedSettingsSchema()
    {
        string settingsJson = JsonSerializer.Serialize(new AppSettings());

        Assert.DoesNotContain("RemoteImage", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(AppSettings).GetProperties(),
            property => property.Name.Contains("Consent", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("RemoteImage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(MailInboxViewModel).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
                || parameter.ParameterType == typeof(Stream));
    }

    [Fact]
    public void RemoteImagePipeline_HasNoPersistentStoreOrWebViewNetworkDependency()
    {
        Type[] dependencies = typeof(RemoteMailImageLoader)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal([typeof(IRemoteMailImageHttpClient), typeof(IRemoteMailImageUriValidator)], dependencies);
        Assert.DoesNotContain(dependencies, type => type.Name.Contains("Settings", StringComparison.Ordinal)
            || type.Name.Contains("Store", StringComparison.Ordinal)
            || type.Name.Contains("WebView", StringComparison.Ordinal));
        Assert.False(RemoteMailImageHttpClient.CookiesEnabled);
        Assert.False(RemoteMailImageHttpClient.DefaultCredentialsEnabled);
        Assert.False(RemoteMailImageHttpClient.AutomaticRedirectsEnabled);
        Assert.DoesNotContain(
            typeof(AppSettings).GetProperties(),
            property => property.PropertyType == typeof(MailMessageContent)
                || property.PropertyType == typeof(MailImageContent)
                || property.Name.Contains("RemoteImage", StringComparison.Ordinal));
    }

    [Fact]
    public void ResponsiveMailDocument_PreservesFixedWidthEmailGeometry()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<table style='width:700px;min-width:700px;max-width:700px'><tr><td>Receipt</td></tr></table>"
        });
        MailMessageContent content = CreateExtractor().Extract("fixed-700", message, false);

        string document = new MailHtmlDocumentBuilder().Build(content);

        Assert.Contains("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">", document, StringComparison.Ordinal);
        Assert.Contains("width: 700px", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("min-width: 700px", content.SanitizedHtmlContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".um-mail-content table {", document, StringComparison.Ordinal);
        Assert.DoesNotContain("min-width: 0 !important", document, StringComparison.Ordinal);
        Assert.Contains(".um-mail-viewport { width: 100%; max-width: 100%;", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveMailDocument_ConstrainsOversizedImagesAndPreservesAspectRatio()
    {
        string document = new MailHtmlDocumentBuilder().Build(Content(
            "oversized-image",
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<img src='data:image/png;base64,iVBORw0KGgo=' width='1600' height='900'>"));
        HtmlAgilityPack.HtmlDocument parsed = new();
        parsed.LoadHtml(document);
        HtmlNode image = Assert.IsType<HtmlNode>(parsed.DocumentNode.SelectSingleNode("//img"));

        Assert.Contains(".um-mail-content img { max-width: 100% !important; height: auto !important; }", document, StringComparison.Ordinal);
        Assert.Equal("1600", image.GetAttributeValue("width", string.Empty));
    }

    [Fact]
    public void ResponsiveMailDocument_DoesNotRewriteFixedTableOrCellGeometry()
    {
        string document = new MailHtmlDocumentBuilder().Build(Content(
            "fixed-table",
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<table width='700'><tr><th>Description</th><td>LongValueThatCanWrapSafely</td></tr></table>"));

        Assert.Contains("<table width='700'>", document, StringComparison.Ordinal);
        Assert.DoesNotContain(".um-mail-content td, .um-mail-content th", document, StringComparison.Ordinal);
        Assert.DoesNotContain(".um-mail-content * { box-sizing", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveMailDocument_KeepsCenteredSixHundredPixelEmailCentered()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<table align='center' style='width:600px;max-width:600px'><tr><td>Centered</td></tr></table>"
        });
        MailMessageContent content = CreateExtractor().Extract("centered-600", message, false);

        string document = new MailHtmlDocumentBuilder().Build(content);

        Assert.Contains("align=\"center\"", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("width: 600px", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".um-mail-content table", document, StringComparison.Ordinal);
        Assert.Contains("class=\"um-mail-viewport\"", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveMailDocument_DoesNotStretchNarrowCenteredEmail()
    {
        string document = new MailHtmlDocumentBuilder().Build(Content(
            "narrow",
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<table align='center' width='320'><tr><td>Narrow</td></tr></table>"));

        Assert.Contains("width='320'", document, StringComparison.Ordinal);
        Assert.Contains("align='center'", document, StringComparison.Ordinal);
        Assert.DoesNotContain(".um-mail-content table", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveMailDocument_ProvidesReachableHorizontalOverflowForOversizedEmail()
    {
        string document = new MailHtmlDocumentBuilder().Build(Content(
            "overflow",
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<table width='1400' style='width:1400px;min-width:1400px'><tr><td>Wide receipt</td></tr></table>"));

        Assert.Contains("width='1400'", document, StringComparison.Ordinal);
        Assert.Contains("min-width:1400px", document, StringComparison.Ordinal);
        Assert.Contains("class=\"um-mail-viewport\"", document, StringComparison.Ordinal);
        Assert.Contains("class=\"um-mail-content\"", document, StringComparison.Ordinal);
        Assert.Contains(".um-mail-viewport { width: 100%; max-width: 100%; box-sizing: border-box; overflow-x: auto; overflow-y: visible; }", document, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-x: hidden", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".um-mail-content table", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveMailDocument_PreservesBegetStyleImagesCtaAndLayout()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<table align='center' style='width:600px;max-width:600px'><tr><td>" +
                "<a href='https://example.test/home'><img src='https://images.example.test/logo.png'></a>" +
                "<a href='https://example.test/social'><img src='https://images.example.test/social.png'></a>" +
                "<a href='https://example.test/action' style='display:inline-block;padding:12px 20px;background:#1769aa;color:#fff'>Action</a>" +
                "</td></tr></table>"
        });
        MailMessageContent content = CreateExtractor().Extract("provider-a", message, false);

        string document = new MailHtmlDocumentBuilder().Build(content);
        HtmlAgilityPack.HtmlDocument parsed = new();
        parsed.LoadHtml(document);

        Assert.Equal(2, content.RemoteImages.Count);
        Assert.Equal(2, parsed.DocumentNode.SelectNodes($"//img[@{MailHtmlSanitizer.RemoteImageIdAttribute}]")?.Count);
        Assert.Contains("Action", document, StringComparison.Ordinal);
        Assert.Contains("align=\"center\"", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("width: 600px", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".um-mail-content table", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveMailDocument_PreservesBinanceStyleCenteredColumnHeaderAndList()
    {
        MimeMessage message = CreateMessage(new TextPart("html")
        {
            Text = "<table align='center' style='width:100%;max-width:600px'><tr><td>" +
                "<h1>Header</h1><ul><li>First</li><li>Second</li></ul></td></tr></table>"
        });
        MailMessageContent content = CreateExtractor().Extract("provider-b", message, false);

        string document = new MailHtmlDocumentBuilder().Build(content);

        Assert.Contains("max-width: 600px", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1>Header</h1>", document, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(document, "<li>"));
        Assert.DoesNotContain(".um-mail-content table", document, StringComparison.Ordinal);
    }

    [Fact]
    public void MailDocument_HasStrictCspAndNoRemoteSource()
    {
        MailMessageContent content = Content(
            "html",
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<p>Safe</p>");

        string document = new MailHtmlDocumentBuilder().Build(content);

        Assert.Contains("Content-Security-Policy", document, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", document, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrintDocument_UsesEscapedMetadataHeaderHiddenOnScreenAndVisibleOnlyForPrint()
    {
        DateTimeOffset receivedAt = new(2026, 8, 25, 9, 42, 17, TimeSpan.Zero);
        MailMessageContent content = new(
            "print-safe",
            "<Subject & details>",
            "<Sender & name>",
            "sender@example.test",
            "Recipient <recipient@example.test>",
            receivedAt,
            MailMessageBodyKind.SanitizedHtml,
            "<p>Safe body</p>",
            [],
            false,
            true)
        {
            Attachments =
            [
                new MailAttachmentInfo(
                    "attachment-1",
                    "invoice <draft> & copy.pdf",
                    "application/pdf",
                    1024,
                    false,
                    true)
            ]
        };

        string document = new MailHtmlDocumentBuilder().Build(content);
        string expectedDate = WebUtility.HtmlEncode(
            receivedAt.ToLocalTime().ToString("ddd, d MMM, HH:mm", CultureInfo.CurrentCulture));

        Assert.Contains("<section class=\"um-print-header\"", document, StringComparison.Ordinal);
        Assert.Contains("&lt;Subject &amp; details&gt;", document, StringComparison.Ordinal);
        Assert.Contains("&lt;Sender &amp; name&gt;", document, StringComparison.Ordinal);
        Assert.Contains("Recipient &lt;recipient@example.test&gt;", document, StringComparison.Ordinal);
        Assert.Contains(expectedDate, document, StringComparison.Ordinal);
        Assert.Contains("invoice &lt;draft&gt; &amp; copy.pdf", document, StringComparison.Ordinal);
        Assert.Contains(".um-print-header { display: none; }", document, StringComparison.Ordinal);
        Assert.Contains("@media print", document, StringComparison.Ordinal);
        Assert.Contains(".um-print-header { display: block;", document, StringComparison.Ordinal);
        Assert.DoesNotContain("<Subject & details>", document, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice <draft> & copy.pdf", document, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintDocument_UsesAttachmentMetadataOnlyAndDoesNotGrantRemoteImageConsent()
    {
        MailMessageContent content = Content(
            "print-blocked-image",
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: $"<img {MailHtmlSanitizer.RemoteImageIdAttribute}='remote-1'>",
            remoteImages:
            [
                new MailRemoteImageReference(
                    "remote-1",
                    new Uri("https://images.example.test/private.png"))
            ]) with
        {
            Attachments =
            [
                new MailAttachmentInfo(
                    "attachment-1",
                    "metadata-only.pdf",
                    "application/pdf",
                    2048,
                    false,
                    true)
            ]
        };

        string document = new MailHtmlDocumentBuilder().Build(content);
        HtmlAgilityPack.HtmlDocument parsed = new();
        parsed.LoadHtml(document);
        HtmlNode image = Assert.IsType<HtmlNode>(parsed.DocumentNode.SelectSingleNode("//img"));

        Assert.Null(image.Attributes["src"]);
        Assert.Equal("remote-1", image.GetAttributeValue(MailHtmlSanitizer.RemoteImageIdAttribute, string.Empty));
        Assert.DoesNotContain("images.example.test", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata-only.pdf", document, StringComparison.Ordinal);
        Assert.Empty(typeof(MailHtmlDocumentBuilder).GetConstructors().Single().GetParameters());
        Assert.DoesNotContain(
            typeof(MailAttachmentInfo).GetProperties(),
            property => property.PropertyType == typeof(byte[])
                || property.PropertyType == typeof(Stream)
                || property.PropertyType == typeof(ReadOnlyMemory<byte>));
    }

    [Fact]
    public async Task PrintAction_IsAvailableOnlyForLoadedHtmlDetailWithReadyRenderer()
    {
        MailMessageSummary summary = Summary("printable");
        QueueReadProvider provider = new()
        {
            Message = Content(
                "printable",
                bodyKind: MailMessageBodyKind.SanitizedHtml,
                body: "<p>Printable</p>")
        };
        provider.EnqueuePage(Page([summary], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);

        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));
        Assert.False(viewModel.ShowPrintAction);
        Assert.False(viewModel.CanPrintMessage);

        viewModel.OpenMessageCommand.Execute(summary);
        await viewModel.CurrentMessageLoadTask;
        Assert.True(viewModel.ShowPrintAction);
        Assert.False(viewModel.CanPrintMessage);

        viewModel.SetPrintAvailable(isAvailable: true);
        Assert.True(viewModel.CanPrintMessage);

        viewModel.BackToMessageListCommand.Execute(null);
        Assert.False(viewModel.ShowPrintAction);
        Assert.False(viewModel.CanPrintMessage);

        viewModel.Compose.NewMessageCommand.Execute(null);
        Assert.False(viewModel.ShowPrintAction);
        Assert.False(viewModel.CanPrintMessage);
    }

    [Fact]
    public void RendererResourcePolicy_AllowsOnlySupportedMemoryImagesAndInternalDocument()
    {
        MailRendererNavigationPolicy policy = new();

        Assert.True(policy.IsAllowedInRendererResource(
            new Uri("data:image/png;base64,iVBORw0KGgo="),
            CoreWebView2WebResourceContext.Image));
        Assert.False(policy.IsAllowedInRendererResource(
            new Uri("data:image/svg+xml;base64,PHN2Zz4="),
            CoreWebView2WebResourceContext.Image));
        Assert.False(policy.IsAllowedInRendererResource(
            new Uri("data:text/html;base64,PGgxPkJhZDwvaDE+"),
            CoreWebView2WebResourceContext.Image));
        Assert.False(policy.IsAllowedInRendererResource(
            new Uri("https://images.example.test/photo.png"),
            CoreWebView2WebResourceContext.Image));
        Assert.True(policy.IsAllowedInRendererResource(
            MailRendererNavigationPolicy.InternalDocumentUri,
            CoreWebView2WebResourceContext.Document));
    }

    [Fact]
    public void MailRenderer_DisablesJavaScriptWebMessagesAndHostObjects()
    {
        Assert.False(MailMessageHtmlRenderer.JavaScriptEnabled);
        Assert.False(MailMessageHtmlRenderer.WebMessagingEnabled);
        Assert.False(MailMessageHtmlRenderer.HostObjectsEnabled);
        Assert.True(MailMessageHtmlRenderer.UsesInPrivateProfile);
        Assert.Equal(1, MailMessageHtmlRenderer.MaximumControllerCount);
        Assert.Equal(CoreWebView2PrintDialogKind.System, MailMessageHtmlRenderer.PrintDialogKind);
        Assert.NotNull(typeof(IMailMessageHtmlRenderer).GetMethod(nameof(IMailMessageHtmlRenderer.TryShowPrintPreview)));
        Assert.NotNull(typeof(CoreWebView2).GetMethod(
            nameof(CoreWebView2.ShowPrintUI),
            [typeof(CoreWebView2PrintDialogKind)]));
    }

    [Theory]
    [InlineData("https://example.test/path")]
    [InlineData("http://example.test/path")]
    public void MailLink_IsCancelledInRendererAndHandedOnlyToSystemBrowser(string target)
    {
        RecordingExternalBrowser browser = new();
        MailRendererNavigationCoordinator coordinator = new(
            new MailRendererNavigationPolicy(),
            browser);

        MailRendererNavigationDisposition disposition = coordinator.RouteTopLevel(
            new Uri(target),
            isUserInitiated: true);

        Assert.Equal(MailRendererNavigationDisposition.ExternalOpened, disposition);
        Assert.Equal(target, Assert.Single(browser.Opened).AbsoluteUri);
        Assert.NotEqual(MailRendererNavigationDisposition.InternalDocument, disposition);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/sensitive.txt")]
    [InlineData("mailto:user@example.test")]
    [InlineData("custom-scheme:value")]
    public void UnsupportedMailLink_IsBlockedWithoutSystemBrowser(string target)
    {
        RecordingExternalBrowser browser = new();
        MailRendererNavigationCoordinator coordinator = new(
            new MailRendererNavigationPolicy(),
            browser);

        MailRendererNavigationDisposition disposition = coordinator.RouteTopLevel(
            new Uri(target),
            isUserInitiated: true);

        Assert.Equal(MailRendererNavigationDisposition.Blocked, disposition);
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public void AutomaticRemoteNavigation_IsBlockedWithoutSystemBrowser()
    {
        RecordingExternalBrowser browser = new();
        MailRendererNavigationCoordinator coordinator = new(
            new MailRendererNavigationPolicy(),
            browser);

        MailRendererNavigationDisposition disposition = coordinator.RouteTopLevel(
            new Uri("https://tracking.invalid/redirect"),
            isUserInitiated: false);

        Assert.Equal(MailRendererNavigationDisposition.Blocked, disposition);
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public void MailRenderer_HasAtMostOneDirectControllerAndMailAccountOwnsNone()
    {
        Assert.Equal(
            1,
            typeof(MailMessageHtmlRenderer)
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Count(field => field.FieldType == typeof(CoreWebView2Controller)));
        Assert.DoesNotContain(
            typeof(MailAccount).GetProperties(),
            property => property.PropertyType.Name.Contains("WebView", StringComparison.Ordinal)
                || property.PropertyType.Name.Contains("Controller", StringComparison.Ordinal));
    }

    [Fact]
    public void MailToTelegram_HidesRendererAndStartupPrimeDoesNotShowIt()
    {
        Assert.False(MailRendererVisibilityPolicy.ShouldShow(
            windowVisible: true,
            windowMinimized: false,
            settingsOpen: false,
            hasSelectedWebService: true,
            hasActiveMailAccount: false,
            bodyKind: MailMessageBodyKind.SanitizedHtml));
        Assert.False(MailRendererVisibilityPolicy.ShouldShow(
            windowVisible: false,
            windowMinimized: false,
            settingsOpen: false,
            hasSelectedWebService: false,
            hasActiveMailAccount: true,
            bodyKind: MailMessageBodyKind.SanitizedHtml));
    }

    [Fact]
    public void HiddenMailRenderer_IgnoresWindowMovementNotifications()
    {
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = false };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        coordinator.NotifyParentWindowPositionChanged();
        coordinator.NotifyParentWindowPositionChanged();

        Assert.Equal(0, renderer.ParentPositionChangeCalls);
    }

    [Fact]
    public void VisibleMailRenderer_ReceivesOneUpdatePerWindowMovementNotification()
    {
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = true };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        coordinator.NotifyParentWindowPositionChanged();

        Assert.Equal(1, renderer.ParentPositionChangeCalls);
    }

    [Fact]
    public void HiddenMailRenderer_DoesNotReadOrUpdateBounds()
    {
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = false };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);
        int boundsReads = 0;

        coordinator.UpdateSurface(
            shouldShow: false,
            () =>
            {
                boundsReads++;
                return new System.Drawing.Rectangle(10, 20, 800, 600);
            });

        Assert.Equal(0, boundsReads);
        Assert.Equal(0, renderer.LayoutCalls);
        Assert.Equal(0, renderer.HideCalls);
    }

    [Fact]
    public void MailToWeb_DisablesFurtherMailMovementUpdates()
    {
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = true };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        coordinator.Deactivate(clearContent: true);
        coordinator.NotifyParentWindowPositionChanged();

        Assert.False(renderer.IsVisible);
        Assert.Equal(1, renderer.ReleaseCalls);
        Assert.Equal(0, renderer.ParentPositionChangeCalls);
    }

    [Fact]
    public void EnteringCompose_ReleasesNativeMailRendererInputSurfaceOnce()
    {
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = true };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        coordinator.SetComposeActive(isActive: true);
        coordinator.SetComposeActive(isActive: true);

        Assert.False(renderer.IsInitialized);
        Assert.False(renderer.IsVisible);
        Assert.Equal(1, renderer.ReleaseCalls);
    }

    [Fact]
    public void EnteringCompose_InvalidatesRendererEvenWhileControllerIsNotYetInitialized()
    {
        RecordingMailRenderer renderer = new() { IsInitialized = false, IsVisible = false };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        coordinator.SetComposeActive(isActive: true);

        Assert.Equal(1, renderer.ReleaseCalls);
    }

    [Fact]
    public void LeavingCompose_AllowsMailRendererToBeRestoredLazily()
    {
        System.Drawing.Rectangle bounds = new(14, 28, 960, 640);
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = true };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);
        coordinator.SetComposeActive(isActive: true);
        renderer.IsInitialized = true;

        coordinator.UpdateSurface(shouldShow: true, () => bounds);
        Assert.Equal(0, renderer.LayoutCalls);

        coordinator.SetComposeActive(isActive: false);
        coordinator.UpdateSurface(shouldShow: true, () => bounds);

        Assert.True(renderer.IsVisible);
        Assert.Equal(1, renderer.LayoutCalls);
        Assert.Equal(bounds, renderer.LastBounds);
    }

    [Fact]
    public void WebToMail_RestoresCurrentBoundsAndVisibility()
    {
        System.Drawing.Rectangle currentBounds = new(14, 28, 960, 640);
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = false };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        coordinator.UpdateSurface(shouldShow: true, () => currentBounds);

        Assert.True(renderer.IsVisible);
        Assert.Equal(1, renderer.LayoutCalls);
        Assert.Equal(currentBounds, renderer.LastBounds);
    }

    [Fact]
    public void UnchangedVisibleMailBounds_DoNotCreateAnUpdateLoop()
    {
        System.Drawing.Rectangle currentBounds = new(14, 28, 960, 640);
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = false };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        coordinator.UpdateSurface(shouldShow: true, () => currentBounds);
        coordinator.UpdateSurface(shouldShow: true, () => currentBounds);

        Assert.Equal(1, renderer.LayoutCalls);
    }

    [Fact]
    public void InteractiveMove_ReleasesVisibleRendererOnlyOnce()
    {
        RecordingMailRenderer renderer = new() { IsInitialized = true, IsVisible = true };
        MailRendererWindowLifecycleCoordinator coordinator = new(renderer);

        bool shouldRestore = coordinator.ReleaseForInteractiveMove();
        bool duplicateRestore = coordinator.ReleaseForInteractiveMove();

        Assert.True(shouldRestore);
        Assert.False(duplicateRestore);
        Assert.Equal(1, renderer.ReleaseCalls);
        Assert.False(renderer.IsVisible);
    }

    [Fact]
    public async Task StaleMailContent_CannotReplaceNewerSelection()
    {
        MailMessageSummary first = Summary("first");
        MailMessageSummary second = Summary("second");
        TaskCompletionSource<MailMessageContent> pendingFirst = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        QueueReadProvider provider = new()
        {
            MessageHandler = (_, key, _) => key == "first"
                ? pendingFirst.Task
                : Task.FromResult(Content("second", bodyKind: MailMessageBodyKind.SanitizedHtml, body: "<p>Second</p>"))
        };
        provider.EnqueuePage(Page([first, second], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        await viewModel.ActivateAsync(CreateAccount(MailProviderType.Gmail));

        viewModel.SelectedMessageSummary = first;
        Task firstLoad = viewModel.CurrentMessageLoadTask;
        viewModel.SelectedMessageSummary = second;
        await viewModel.CurrentMessageLoadTask;
        pendingFirst.SetResult(Content("first", bodyKind: MailMessageBodyKind.SanitizedHtml, body: "<p>Stale</p>"));
        await firstLoad;

        Assert.Equal("second", viewModel.SelectedMessageContent!.MessageKey);
        Assert.DoesNotContain("Stale", viewModel.SelectedMessageContent.BodyContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GmailRawMime_UsesSharedMimePipelineWithoutLeakingRawHeaders()
    {
        MimeMessage message = CreateMessage(new TextPart("plain") { Text = "Visible body" });
        using MemoryStream stream = new();
        await message.WriteToAsync(stream);
        FakeGmailApiReadClient api = new()
        {
            RawMessage = new GmailApiRawMessage(stream.ToArray(), true)
        };

        MailMessageContent content = await CreateGmailProvider(api).GetMessageAsync(
            CreateAccount(MailProviderType.Gmail),
            "gmail:raw-id");

        Assert.Equal("Visible body", content.PlainTextContent);
        Assert.DoesNotContain("MIME-Version:", content.PlainTextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Type:", content.PlainTextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InboxProductionTypes_HaveNoLoggerOrPersistentMailCacheDependency()
    {
        Type[] types =
        [
            typeof(MailInboxViewModel),
            typeof(GmailMailReadProvider),
            typeof(ImapMailReadProvider),
            typeof(MailContentExtractor)
        ];

        foreach (Type type in types)
        {
            Assert.DoesNotContain(
                type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType.Name.Contains("Logger", StringComparison.Ordinal)
                    || parameter.ParameterType.Name.Contains("SettingsStore", StringComparison.Ordinal)
                    || parameter.ParameterType.Name.Contains("CacheStore", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    [InlineData(MailProviderType.GenericImap)]
    public void ExistingPasswordMailProviders_StillResolveForInbox(MailProviderType providerType)
    {
        ImapMailReadProvider imap = CreateImapProvider(new FakeImapInboxClient());
        MailReadProviderFactory factory = new(
            new IMailReadProvider[]
            {
                CreateGmailProvider(new FakeGmailApiReadClient()),
                imap
            });

        Assert.Same(imap, factory.Get(providerType));
    }

    [Fact]
    public async Task WebMailWebStateTransition_ClearsOnlyVisibleMailWorkspace()
    {
        QueueReadProvider provider = new();
        provider.EnqueuePage(Page([Summary("mail")], null));
        using MailInboxViewModel viewModel = CreateViewModel(provider);
        MailAccount mail = CreateAccount(MailProviderType.Gmail);

        await viewModel.ActivateAsync(null);
        await viewModel.ActivateAsync(mail);
        Assert.True(viewModel.IsActive);
        await viewModel.ActivateAsync(null);

        Assert.False(viewModel.IsActive);
        Assert.Empty(viewModel.Messages);
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

        throw new FileNotFoundException(
            $"Repository file was not found: {Path.Combine(relativePath)}");
    }

    private static GmailMailReadProvider CreateGmailProvider(FakeGmailApiReadClient api) =>
        new(
            new FakeCredentialStore(MailCredential.CreateGmailOAuth("refresh", "client", "secret")),
            api,
            CreateExtractor());

    private static ImapMailReadProvider CreateImapProvider(FakeImapInboxClient imap)
    {
        NoOpConnectionValidator validator = new();
        MailProviderFactory providers = new(
            new IMailProvider[]
            {
                new GmailApiProvider(),
                new YandexMailProvider(validator),
                new MailRuMailProvider(validator),
                new GenericImapMailProvider(validator)
            });
        return new ImapMailReadProvider(
            new FakeCredentialStore(MailCredential.CreatePassword("password")),
            providers,
            imap,
            CreateExtractor());
    }

    private static MailContentExtractor CreateExtractor() =>
        new(new MailHtmlSanitizer());

    private static QueueReadProvider CreateRemoteImageProvider(MailMessageSummary message)
    {
        QueueReadProvider provider = new()
        {
            MessageHandler = (_, key, _) => Task.FromResult(RemoteImageContent(key))
        };
        provider.EnqueuePage(Page([message], null));
        return provider;
    }

    private static MailMessageContent RemoteImageContent(string key) =>
        Content(
            key,
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<img data-um-remote-image-id='remote-1'>",
            remoteImages:
            [
                new MailRemoteImageReference(
                    "remote-1",
                    new Uri($"https://images.example.test/{key}.png"))
            ]);

    private static MailInboxViewModel CreateViewModel(QueueReadProvider provider) =>
        new(new SingleReadProviderFactory(provider));

    private static MailAccount CreateAccount(MailProviderType provider) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            EmailAddress = "account@example.test",
            CredentialKey = Guid.NewGuid().ToString("N"),
            AuthenticationKind = provider == MailProviderType.Gmail
                ? MailAuthenticationKind.OAuth
                : MailAuthenticationKind.Password,
            IsEnabled = true,
            GenericConnectionSettings = provider == MailProviderType.GenericImap
                ? new MailConnectionSettings
                {
                    Imap = new MailServerSettings
                    {
                        Host = "imap.example.test",
                        Port = 993,
                        Username = "account@example.test"
                    },
                    Smtp = new MailServerSettings
                    {
                        Host = "smtp.example.test",
                        Port = 465,
                        Username = "account@example.test"
                    }
                }
                : null
        };

    private static MailMessageSummary Summary(string key, bool isUnread = false) =>
        new(
            key,
            "Subject " + key,
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            isUnread);

    private static MailMessageContent Content(
        string key,
        bool isUnread = false,
        MailMessageBodyKind bodyKind = MailMessageBodyKind.PlainText,
        string body = "Body",
        IReadOnlyList<MailRemoteImageReference>? remoteImages = null) =>
        new(
            key,
            "Subject",
            "Sender",
            "sender@example.test",
            "Recipient <recipient@example.test>",
            DateTimeOffset.UtcNow,
            bodyKind,
            body,
            remoteImages ?? [],
            isUnread,
            false);

    private static MailPage<MailMessageSummary> Page(
        IReadOnlyList<MailMessageSummary> items,
        string? continuationToken) =>
        new(items, continuationToken);

    private static TaskCompletionSource<MailPage<MailMessageSummary>> NewPendingPage() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }

    private static MimeMessage CreateMessage(MimeEntity body)
    {
        MimeMessage message = new()
        {
            Subject = "Safe subject",
            Date = DateTimeOffset.UtcNow,
            Body = body
        };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.test"));
        return message;
    }

    private static MimePart CreateInlineImage(string subtype, string contentId, byte[] bytes) =>
        new("image", subtype)
        {
            ContentId = contentId,
            Content = new MimeContent(new MemoryStream(bytes, writable: false), ContentEncoding.Default)
        };

    private static HttpResponseMessage CreateImageResponse(byte[] bytes, string contentType)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class SingleReadProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class QueueReadProvider : IMailReadProvider
    {
        private readonly Queue<object> _pages = new();

        public Func<MailAccount, string?, int, CancellationToken, Task<MailPage<MailMessageSummary>>>? PageHandler { get; init; }
        public Func<MailAccount, string, CancellationToken, Task<MailMessageContent>>? MessageHandler { get; init; }
        public MailMessageContent Message { get; set; } = Content("default");
        public List<string?> ReceivedPageTokens { get; } = [];
        public List<string> ReceivedMessageKeys { get; } = [];
        public int PageCallCount { get; private set; }
        public int MutationCount => 0;

        public bool Supports(MailProviderType providerType) => true;

        public void EnqueuePage(MailPage<MailMessageSummary> page) => _pages.Enqueue(page);
        public void EnqueueFailure(MailReadException exception) => _pages.Enqueue(exception);

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            PageCallCount++;
            ReceivedPageTokens.Add(continuationToken);
            if (PageHandler is not null)
            {
                return PageHandler(account, continuationToken, pageSize, cancellationToken);
            }

            object next = _pages.Dequeue();
            return next is Exception exception
                ? Task.FromException<MailPage<MailMessageSummary>>(exception)
                : Task.FromResult((MailPage<MailMessageSummary>)next);
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            ReceivedMessageKeys.Add(messageKey);
            if (MessageHandler is not null)
            {
                return MessageHandler(account, messageKey, cancellationToken);
            }

            return Task.FromResult(Message with { MessageKey = messageKey });
        }
    }

    private sealed class RecordingExternalBrowser : IExternalBrowserService
    {
        public List<Uri> Opened { get; } = [];

        public bool TryOpen(Uri uri)
        {
            Opened.Add(uri);
            return true;
        }
    }

    private sealed class RecordingMailRenderer : IMailMessageHtmlRenderer
    {
        public bool IsInitialized { get; set; }
        public bool IsVisible { get; set; }
        public bool CanPrint { get; set; }
        public int ControllerCount => IsInitialized ? 1 : 0;
        public event EventHandler? PrintAvailabilityChanged;
        public int LayoutCalls { get; private set; }
        public int ParentPositionChangeCalls { get; private set; }
        public int HideCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public System.Drawing.Rectangle LastBounds { get; private set; }

        public Task ShowAsync(
            IntPtr parentWindow,
            System.Drawing.Rectangle bounds,
            MailMessageContent content,
            IReadOnlyDictionary<string, MailImageContent>? remoteImages,
            bool isVisible,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void UpdateLayout(System.Drawing.Rectangle bounds, bool isVisible)
        {
            LayoutCalls++;
            LastBounds = bounds;
            IsVisible = isVisible;
        }

        public void NotifyParentWindowPositionChanged() => ParentPositionChangeCalls++;

        public bool TryShowPrintPreview() => CanPrint;

        public void Hide(bool clearContent)
        {
            HideCalls++;
            IsVisible = false;
        }

        public void ReleaseController(bool clearContent)
        {
            ReleaseCalls++;
            IsInitialized = false;
            IsVisible = false;
        }

        public void BeginShutdown()
        {
            IsInitialized = false;
            IsVisible = false;
        }

        public void Dispose() => BeginShutdown();

        public void RaisePrintAvailabilityChanged() =>
            PrintAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RecordingImageHttpClient : IRemoteMailImageHttpClient
    {
        private readonly Func<HttpResponseMessage> _responseFactory;

        public RecordingImageHttpClient(HttpResponseMessage response)
            : this(() => response)
        {
        }

        public RecordingImageHttpClient(Func<HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpRequestMessage recorded = new(request.Method, request.RequestUri);
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                recorded.Headers.TryAddWithoutValidation(name, values);
            }

            Requests.Add(recorded);
            return Task.FromResult(_responseFactory());
        }
    }

    private sealed class AllowAllImageUriValidator : IRemoteMailImageUriValidator
    {
        public Task<bool> IsAllowedAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class RejectAllImageUriValidator : IRemoteMailImageUriValidator
    {
        public Task<bool> IsAllowedAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeGmailApiReadClient : IGmailApiReadClient
    {
        public GmailApiInboxPage Page { get; set; } = new([], null);
        public GmailApiRawMessage RawMessage { get; set; } = new([], false);
        public string? ReceivedPageToken { get; private set; }

        public Task<GmailApiInboxPage> GetInboxPageAsync(
            MailCredential credential,
            Guid accountId,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ReceivedPageToken = pageToken;
            return Task.FromResult(Page);
        }

        public Task<GmailApiRawMessage> GetRawMessageAsync(
            MailCredential credential,
            Guid accountId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RawMessage);
    }

    private sealed class FakeImapInboxClient : IImapInboxClient
    {
        public ImapInboxPageData Page { get; set; } = new([], null);
        public string? ReceivedCursor { get; private set; }

        public Task<ImapInboxPageData> GetInboxPageAsync(
            MailServerSettings server,
            string secret,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ReceivedCursor = cursor;
            return Task.FromResult(Page);
        }

        public Task<ImapMessageData> GetMessageAsync(
            MailServerSettings server,
            string secret,
            uint uniqueId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImapMessageData(CreateMessage(new TextPart("plain") { Text = "Body" }), true));
    }

    private sealed class FakeCredentialStore(MailCredential credential) : IMailCredentialStore
    {
        public Task SaveAsync(
            string credentialKey,
            MailCredential value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MailCredential?> LoadAsync(
            string credentialKey,
            CancellationToken cancellationToken = default) => Task.FromResult<MailCredential?>(credential);

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
}
