using System.Reflection;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;
using MailKit;
using MimeKit;

namespace UnifiedMessenger.Tests;

public sealed class MailBackgroundPollingMonitorTests
{
    [Fact]
    public async Task FirstSuccessfulSnapshot_CreatesBaselineWithoutDetection()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(8, "1", "2"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();

        Assert.Empty(events);
        Assert.Equal(8, account.InboxUnreadCount);
    }

    [Fact]
    public async Task SecondSnapshotWithOneNewMessage_RaisesOneDetection()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(2, "1", "2"));
        provider.Enqueue(account.Id, Snapshot(3, "3", "1", "2"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        MailNewMessageDetectedEventArgs detected = Assert.Single(events);
        Assert.Equal(account.Id, detected.MailAccountId);
        Assert.Equal(1, detected.NewMessageCount);
    }

    [Fact]
    public async Task RepeatedSnapshot_DoesNotDuplicateDetection()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "1"));
        provider.Enqueue(account.Id, Snapshot(2, "2", "1"));
        provider.Enqueue(account.Id, Snapshot(2, "2", "1"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Single(events);
    }

    [Fact]
    public async Task MultipleNewMessagesBetweenPolls_ReportExactCount()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "1"));
        provider.Enqueue(account.Id, Snapshot(4, "4", "3", "2", "1"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Equal(3, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task UnreadCountUpdatesIndependentlyFromDetection()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(9, "1"));
        provider.Enqueue(account.Id, Snapshot(4, "2", "1"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        Assert.Equal(9, account.InboxUnreadCount);
        await monitor.PollOnceAsync();

        Assert.Equal(4, account.InboxUnreadCount);
        Assert.Single(events);
    }

    [Fact]
    public async Task ReadStateCountChangeWithoutNewIdentity_DoesNotFalseDetect()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(5, "1", "2"));
        provider.Enqueue(account.Id, Snapshot(4, "1", "2"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Empty(events);
        Assert.Equal(4, account.InboxUnreadCount);
    }

    [Fact]
    public async Task OneAccountFailure_DoesNotStopOtherAccounts()
    {
        MailAccount failing = Account();
        MailAccount healthy = Account(MailProviderType.Yandex);
        SnapshotProvider provider = new();
        provider.EnqueueFailure(failing.Id);
        provider.Enqueue(healthy.Id, Snapshot(7, "1"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([failing, healthy], provider);

        await monitor.PollOnceAsync();

        Assert.Null(failing.InboxUnreadCount);
        Assert.Equal(7, healthy.InboxUnreadCount);
        Assert.Equal(1, provider.PollCount(failing.Id));
        Assert.Equal(1, provider.PollCount(healthy.Id));
    }

    [Fact]
    public async Task DisabledAccount_IsNotPolled()
    {
        MailAccount disabled = Account();
        disabled.IsEnabled = false;
        SnapshotProvider provider = new();
        using MailBackgroundPollingMonitor monitor = CreateMonitor([disabled], provider);

        await monitor.PollOnceAsync();

        Assert.Equal(0, provider.PollCount(disabled.Id));
        Assert.Null(disabled.InboxUnreadCount);
    }

    [Fact]
    public async Task NewMonitorAfterRestart_BaselinesExistingMessagesWithoutDetection()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "1"));
        using (MailBackgroundPollingMonitor first = CreateMonitor([account], provider))
        {
            await first.PollOnceAsync();
        }

        provider.Enqueue(account.Id, Snapshot(2, "2", "1"));
        using MailBackgroundPollingMonitor restarted = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(restarted);

        await restarted.PollOnceAsync();

        Assert.Empty(events);
        Assert.Equal(2, account.InboxUnreadCount);
    }

    [Fact]
    public async Task IdentityScopeChange_RebaselinesWithoutFalseDetection()
    {
        MailAccount account = Account(MailProviderType.GenericImap);
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(2, "1", "2"));
        provider.Enqueue(account.Id, Snapshot(3, "100", "101", "102") with { IdentityScope = "scope-2" });
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Empty(events);
        Assert.Equal(3, account.InboxUnreadCount);
    }

    [Fact]
    public void PublicDetectionEvent_ContainsOnlyApprovedPreviewMetadataAndNoMessageIdentity()
    {
        PropertyInfo[] properties = typeof(MailNewMessageDetectedEventArgs).GetProperties();

        Assert.Equal(
            [
                nameof(MailNewMessageDetectedEventArgs.MailAccountId),
                nameof(MailNewMessageDetectedEventArgs.NewMessageCount),
                nameof(MailNewMessageDetectedEventArgs.Preview)
            ],
            properties.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(string));

        Assert.Equal(
            [
                nameof(MailNotificationPreview.SenderAddress),
                nameof(MailNotificationPreview.SenderDisplayName),
                nameof(MailNotificationPreview.Snippet),
                nameof(MailNotificationPreview.Subject)
            ],
            typeof(MailNotificationPreview)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Foundation_HasSixtySecondCadenceAndNoPresentationDependencies()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), MailBackgroundPollingMonitor.PollingInterval);
        ConstructorInfo constructor = Assert.Single(typeof(MailBackgroundPollingMonitor).GetConstructors());
        Type[] parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.DoesNotContain(parameterTypes, type => type.Name.Contains("Popup", StringComparison.Ordinal));
        Assert.DoesNotContain(parameterTypes, type => type.Name.Contains("Sound", StringComparison.Ordinal));
        Assert.DoesNotContain(parameterTypes, type => type.Name.Contains("Taskbar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopCancelsThePollingLoopWithoutWaitingForNextInterval()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "1"));
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);

        monitor.Start();
        await provider.FirstPollCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, provider.PollCount(account.Id));
    }

    [Fact]
    public async Task GmailHistoryProvider_MapsBaselineAndDeltaWithoutSnapshotFallback()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        GmailHistoryClient client = new(
            new GmailApiHistoryBaseline(6, 120),
            new GmailApiHistoryDelta(7, 124, ["gmail-1"])
            {
                NotificationPreview = new GmailApiSummaryData(
                    "gmail-1",
                    "Subject",
                    "Sender <sender@example.test>",
                    1,
                    "Snippet",
                    [])
            });
        GmailMailReadProvider provider = new(
            new TestCredentialStore(MailCredential.CreateGmailOAuth("refresh", "client", "secret")),
            client,
            new MailContentExtractor(new MailHtmlSanitizer()));

        IMailNewMessageHistoryProvider historyProvider = Assert.IsAssignableFrom<IMailNewMessageHistoryProvider>(provider);
        Assert.False((object)provider is IMailInboxTechnicalSnapshotProvider);
        MailHistoryPollResult baseline = await historyProvider.PollHistoryAsync(account, null);
        MailHistoryPollResult delta = await historyProvider.PollHistoryAsync(account, baseline.HistoryCursor);

        Assert.Equal(6, baseline.UnreadCount);
        Assert.Equal(120UL, baseline.HistoryCursor);
        Assert.Empty(baseline.NewMessageIdentities);
        Assert.Equal(7, delta.UnreadCount);
        Assert.Equal(124UL, delta.HistoryCursor);
        Assert.Equal(["gmail-1"], delta.NewMessageIdentities);
        Assert.Equal("Sender", delta.NotificationPreview?.SenderDisplayName);
        Assert.Equal("sender@example.test", delta.NotificationPreview?.SenderAddress);
        Assert.Equal("Subject", delta.NotificationPreview?.Subject);
        Assert.Equal("Snippet", delta.NotificationPreview?.Snippet);
        Assert.Equal(account.Id, client.AccountId);
    }

    [Fact]
    public async Task ImapSnapshot_ScopesUniqueIdsByUidValidity()
    {
        MailAccount account = Account(MailProviderType.Yandex);
        ImapSnapshotClient client = new(new ImapInboxTechnicalSnapshot(4, 812, [19, 20, 20, 0])
        {
            NotificationPreviews = new Dictionary<uint, MailNotificationPreview>
            {
                [20] = new("Sender", "sender@yandex.test", "Subject", string.Empty)
            }
        });
        ImapMailReadProvider provider = new(
            new TestCredentialStore(MailCredential.CreatePassword("password")),
            CreateMailProviderFactory(),
            client,
            new MailContentExtractor(new MailHtmlSanitizer()));

        MailInboxTechnicalSnapshot snapshot = await ((IMailInboxTechnicalSnapshotProvider)provider)
            .GetInboxTechnicalSnapshotAsync(account);

        Assert.Equal(4, snapshot.UnreadCount);
        Assert.Equal("imap-inbox:812", snapshot.IdentityScope);
        Assert.Equal(["19", "20"], snapshot.MessageIdentities);
        Assert.Equal("Subject", snapshot.NotificationPreviews["20"].Subject);
        Assert.Equal("imap.yandex.com", client.Server?.Host);
    }

    [Fact]
    public async Task YandexRemovingNewestMail_DoesNotDetectOlderUidsExposedBySlidingWindow()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(2, "100", "101"));
        provider.Enqueue(account.Id, Snapshot(1, "99", "100"));
        provider.Enqueue(account.Id, Snapshot(2, "102", "100"));
        using var monitor = CreateMonitor([account], provider);
        var events = Subscribe(monitor);
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Assert.Empty(events);
        await monitor.PollOnceAsync();
        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task YandexNewMail_UsesEnvelopePreviewWhenPartialBodyIsUnavailable()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(2, "100", "101") with
        {
            NotificationPreviews = new Dictionary<string, MailNotificationPreview>
            {
                ["101"] = new("Иван Петров", "ivan@yandex.test", "Новая тема", string.Empty)
            }
        });
        using var monitor = CreateMonitor([account], provider);
        var events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        MailNewMessageDetectedEventArgs detected = Assert.Single(events);
        Assert.Equal("Иван Петров", detected.Preview?.SenderDisplayName);
        Assert.Equal("Новая тема", detected.Preview?.Subject);
        Assert.Equal(string.Empty, detected.Preview?.Snippet);
        Assert.Equal(["101"], provider.PreviewRequests);
    }

    [Fact]
    public async Task YandexNewMail_EnrichesOnlyConfirmedNewMessageWithBoundedPreview()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(2, "100", "101") with
        {
            NotificationPreviews = new Dictionary<string, MailNotificationPreview>
            {
                ["101"] = new("Иван Петров", "ivan@yandex.test", "Новая тема", string.Empty)
            }
        });
        provider.EnrichedPreviews["101"] = new("Иван Петров", "ivan@yandex.test", "Новая тема", "Короткий текст письма");
        using var monitor = CreateMonitor([account], provider);
        var events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        MailNewMessageDetectedEventArgs detected = Assert.Single(events);
        Assert.Equal("Короткий текст письма", detected.Preview?.Snippet);
        Assert.Equal(["101"], provider.PreviewRequests);
    }

    [Fact]
    public async Task YandexPreviewFailure_StillRaisesEnvelopeOnlyNotification()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new() { ThrowOnPreviewRequest = true };
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(2, "100", "101") with
        {
            NotificationPreviews = new Dictionary<string, MailNotificationPreview>
            {
                ["101"] = new("Иван Петров", "ivan@yandex.test", "Новая тема", string.Empty)
            }
        });
        using var monitor = CreateMonitor([account], provider);
        var events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        MailNewMessageDetectedEventArgs detected = Assert.Single(events);
        Assert.Equal("Иван Петров", detected.Preview?.SenderDisplayName);
        Assert.Equal(string.Empty, detected.Preview?.Snippet);
        Assert.Equal(["101"], provider.PreviewRequests);
    }

    [Fact]
    public async Task YandexMultipleNewMessages_DoesNotFetchNotificationPreviewBodies()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(3, "100", "101", "102"));
        using var monitor = CreateMonitor([account], provider);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        Assert.Empty(provider.PreviewRequests);
    }

    [Fact]
    public void ImapNotificationPreviewText_PrefersSafeTextAndRemovesHtmlMarkup()
    {
        BodyPartText htmlPart = new(ContentType.Parse("text/html; charset=utf-8"), "1")
        {
            ContentTransferEncoding = "quoted-printable"
        };

        string preview = MailKitImapInboxClient.ExtractNotificationPreviewText(
            htmlPart,
            System.Text.Encoding.UTF8.GetBytes("<p>Здравствуйте, <b>мир</b>.</p><img src=3D\"https://example.test/image.png\">") );

        Assert.Equal("Здравствуйте, мир.", preview);
        Assert.DoesNotContain("<", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("image.png", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ImapNotificationPreviewText_DecodesAndBoundsText()
    {
        BodyPartText plainPart = new(ContentType.Parse("text/plain; charset=utf-8"), "1")
        {
            ContentTransferEncoding = "base64"
        };
        string source = new('т', 400);

        string preview = MailKitImapInboxClient.ExtractNotificationPreviewText(
            plainPart,
            System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source))));

        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.True(preview.Length <= 281);
    }

    [Fact]
    public void ImapNotificationPreviewText_DecodesUtf8Base64RussianText()
    {
        string preview = DecodePreview(
            "text/plain; charset=utf-8",
            "base64",
            System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Здравствуйте, мир!"))));

        Assert.Equal("Здравствуйте, мир!", preview);
        Assert.DoesNotContain('\ufffd', preview);
    }

    [Fact]
    public void ImapNotificationPreviewText_DecodesUtf8QuotedPrintableRussianText()
    {
        string preview = DecodePreview(
            "text/plain; charset=utf-8",
            "quoted-printable",
            System.Text.Encoding.ASCII.GetBytes(ToQuotedPrintable(System.Text.Encoding.UTF8.GetBytes("Здравствуйте, мир!"))));

        Assert.Equal("Здравствуйте, мир!", preview);
        Assert.DoesNotContain('\ufffd', preview);
    }

    [Fact]
    public void ImapNotificationPreviewText_HonorsWindows1251Charset()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        string preview = DecodePreview(
            "text/plain; charset=windows-1251",
            "8bit",
            System.Text.Encoding.GetEncoding("windows-1251").GetBytes("Здравствуйте, мир!"));

        Assert.Equal("Здравствуйте, мир!", preview);
        Assert.DoesNotContain('\ufffd', preview);
    }

    [Fact]
    public void ImapNotificationPreviewText_PreservesAsciiSevenBitText()
    {
        string preview = DecodePreview(
            "text/plain; charset=us-ascii",
            "7bit",
            System.Text.Encoding.ASCII.GetBytes("Plain ASCII message"));

        Assert.Equal("Plain ASCII message", preview);
    }

    [Fact]
    public void ImapNotificationPreviewText_DecodesUtf8HtmlAfterTransferDecoding()
    {
        string html = "<p>Здравствуйте, <b>мир</b>!</p><img src=\"https://example.test/pixel.png\">";
        string preview = DecodePreview(
            "text/html; charset=utf-8",
            "base64",
            System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html))));

        Assert.Equal("Здравствуйте, мир!", preview);
        Assert.DoesNotContain("<", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ImapNotificationPreviewText_IgnoresPartialBase64Quantum()
    {
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Здравствуйте"));
        string preview = DecodePreview(
            "text/plain; charset=utf-8",
            "base64",
            System.Text.Encoding.ASCII.GetBytes(encoded[..^1]));

        Assert.NotEmpty(preview);
        Assert.DoesNotContain('\ufffd', preview);
    }

    [Fact]
    public void ImapNotificationPreviewText_IgnoresPartialQuotedPrintableEscapeAndUtf8Tail()
    {
        string preview = DecodePreview(
            "text/plain; charset=utf-8",
            "quoted-printable",
            System.Text.Encoding.ASCII.GetBytes("=D0=9F=D1=80=D0=B8=D0=B2=D0=B5=D1="));

        Assert.Equal("Приве", preview);
        Assert.DoesNotContain('\ufffd', preview);
    }

    [Fact]
    public void ImapNotificationPreviewText_TrimsIncompleteUtf8Character()
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes("Привет");
        string preview = DecodePreview("text/plain; charset=utf-8", "8bit", body[..^1]);

        Assert.Equal("Приве", preview);
        Assert.DoesNotContain('\ufffd', preview);
    }

    [Fact]
    public void ImapNotificationPreviewText_UsesSafeFallbackForMissingOrInvalidCharset()
    {
        string missingCharset = DecodePreview(
            "text/plain",
            "8bit",
            System.Text.Encoding.UTF8.GetBytes("Здравствуйте"));
        string invalidCharset = DecodePreview(
            "text/plain; charset=x-invalid-charset",
            "8bit",
            System.Text.Encoding.UTF8.GetBytes("Здравствуйте"));

        Assert.Equal("Здравствуйте", missingCharset);
        Assert.Equal(string.Empty, invalidCharset);
    }

    [Fact]
    public void ImapNotificationPreviewText_RejectsMalformedTransferPayload()
    {
        string preview = DecodePreview(
            "text/plain; charset=utf-8",
            "base64",
            System.Text.Encoding.ASCII.GetBytes("not-base64!"));

        Assert.Equal(string.Empty, preview);
    }

    [Fact]
    public void RootSinglePartRuntimePath_UsesTextSectionAndDecodesBodyOnlyPayload()
    {
        BodyPartText root = TextPart("text/plain; charset=UTF-8", string.Empty);
        root.ContentTransferEncoding = "base64";
        string expected = "Это проверка текста всплывающего уведомления Raven.";
        byte[] bodyOnly = System.Text.Encoding.ASCII.GetBytes(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(expected)));
        byte[] formerWholeMessagePayload = System.Text.Encoding.ASCII.GetBytes(
            "Received: from postback.a.mail.yandex.net\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"
            + System.Text.Encoding.ASCII.GetString(bodyOnly));

        Assert.Equal("TEXT", MailKitImapInboxClient.ResolveNotificationPreviewSection(root));
        Assert.Equal(string.Empty, MailKitImapInboxClient.ExtractNotificationPreviewText(root, formerWholeMessagePayload));
        Assert.Equal(expected, MailKitImapInboxClient.ExtractNotificationPreviewText(root, bodyOnly));
    }

    [Fact]
    public void ProductionPreviewFetch_UsesResolvedStringSectionInsteadOfBodyPartOverload()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Mail", "ImapMailReadProvider.cs"));
        int start = source.IndexOf(
            "public async Task<MailNotificationPreview?> GetInboxNotificationPreviewAsync",
            StringComparison.Ordinal);
        int end = source.IndexOf(
            "public Task<ImapInboxPageData> GetInboxPageAsync",
            start,
            StringComparison.Ordinal);
        string productionPath = source[start..end];

        Assert.Contains("ResolveNotificationPreviewSection(textPart)", productionPath, StringComparison.Ordinal);
        Assert.Contains("uid,\n                section,", productionPath.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("uid,\n                textPart,", productionPath.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void ImapNotificationPreviewPartSelection_PrefersPlainTextFromAlternative()
    {
        BodyPartText plain = TextPart("text/plain; charset=utf-8", "1.1");
        BodyPartText html = TextPart("text/html; charset=utf-8", "1.2");
        BodyPartMultipart alternative = Multipart("multipart/alternative", "1", plain, html);

        Assert.Same(plain, MailKitImapInboxClient.FindNotificationPreviewTextPart(alternative));
    }

    [Fact]
    public void ImapNotificationPreviewPartSelection_ExcludesAttachmentsAndAttachedMessages()
    {
        BodyPartText body = TextPart("text/plain; charset=utf-8", "1.1");
        BodyPartText attachedText = TextPart("text/plain; charset=utf-8", "1.2");
        attachedText.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment);
        BodyPartMessage attachedMessage = new(ContentType.Parse("message/rfc822"), "1.3")
        {
            Body = TextPart("text/plain; charset=utf-8", "1.3.1")
        };
        BodyPartMultipart mixed = Multipart("multipart/mixed", "1", body, attachedText, attachedMessage);

        Assert.Same(body, MailKitImapInboxClient.FindNotificationPreviewTextPart(mixed));
        Assert.Null(MailKitImapInboxClient.FindNotificationPreviewTextPart(attachedMessage));
    }

    [Fact]
    public void ImapNotificationPreviewPartSelection_RejectsTechnicalReport()
    {
        BodyPartText deliveryStatus = TextPart("text/plain; charset=utf-8", "1.1");
        BodyPartMultipart report = Multipart("multipart/report; report-type=delivery-status", "1", deliveryStatus);

        Assert.Null(MailKitImapInboxClient.FindNotificationPreviewTextPart(report));
    }

    [Fact]
    public void ImapNotificationPreviewText_RemovesLeadingTransportHeaderBlock()
    {
        BodyPartText plainPart = TextPart("text/plain; charset=utf-8", "1");

        string preview = MailKitImapInboxClient.ExtractNotificationPreviewText(
            plainPart,
            System.Text.Encoding.UTF8.GetBytes(
                "Received: from postback.a.mail.yandex.net\r\nAuthentication-Results: example\r\n\r\nЗдравствуйте, это текст письма."));

        Assert.Equal("Здравствуйте, это текст письма.", preview);
        Assert.DoesNotContain("Received:", preview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImapNotificationPreviewText_RejectsIncompleteTransportHeaderPayload()
    {
        BodyPartText plainPart = TextPart("text/plain; charset=utf-8", "1");

        string preview = MailKitImapInboxClient.ExtractNotificationPreviewText(
            plainPart,
            System.Text.Encoding.UTF8.GetBytes("Received: from postback.a.mail.yandex.net\r\nDKIM-Signature: example"));

        Assert.Equal(string.Empty, preview);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapLocalInboxMoveSuppressesOnlyMappedUid_NotConcurrentNewMailOrOtherAccount(
        MailProviderType providerType)
    {
        MailAccount first = Account(providerType);
        MailAccount second = Account(providerType);
        SnapshotProvider provider = new();
        provider.Enqueue(first.Id, Snapshot(1, "100"));
        provider.Enqueue(second.Id, Snapshot(1, "100"));
        provider.Enqueue(first.Id, Snapshot(3, "100", "101", "102"));
        provider.Enqueue(second.Id, Snapshot(2, "100", "101"));
        ImapMailboxChangeTracker tracker = new();
        using var monitor = CreateMonitor([first, second], provider, tracker);
        var events = Subscribe(monitor);
        await monitor.PollOnceAsync();
        using (await tracker.EnterAsync(first.Id, CancellationToken.None))
        {
            tracker.RecordInboxMove(first.Id, 812, [101]);
        }
        await monitor.PollOnceAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, item => Assert.Equal(1, item.NewMessageCount));
        Assert.Contains(events, item => item.MailAccountId == first.Id);
        Assert.Contains(events, item => item.MailAccountId == second.Id);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapPollingWaitsForMutationUidMapping(MailProviderType providerType)
    {
        MailAccount account = Account(providerType);
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(2, "100", "101"));
        ImapMailboxChangeTracker tracker = new();
        using var monitor = CreateMonitor([account], provider, tracker);
        var events = Subscribe(monitor);
        await monitor.PollOnceAsync();
        IDisposable lease = await tracker.EnterAsync(account.Id, CancellationToken.None);
        Task poll = monitor.PollOnceAsync();
        Assert.False(poll.IsCompleted);
        Assert.Equal(1, provider.PollCount(account.Id));
        tracker.RecordInboxMove(account.Id, 812, [101]);
        lease.Dispose();
        await poll;
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ManagedImapAmbiguousMoveRebaselinesOnce_ThenDetectsNewMailNormally(
        MailProviderType providerType)
    {
        MailAccount account = Account(providerType);
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(2, "100", "101"));
        provider.Enqueue(account.Id, Snapshot(3, "100", "101", "102"));
        ImapMailboxChangeTracker tracker = new();
        using var monitor = CreateMonitor([account], provider, tracker);
        var events = Subscribe(monitor);
        await monitor.PollOnceAsync();
        tracker.RequireBaseline(account.Id);
        await monitor.PollOnceAsync();
        Assert.Empty(events);
        await monitor.PollOnceAsync();
        Assert.Equal(1, Assert.Single(events).NewMessageCount);
    }

    [Fact]
    public async Task MailRuManagedPollingDoesNotEnableYandexPreviewEnrichment()
    {
        MailAccount account = Account(MailProviderType.MailRu);
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(2, "100", "101") with
        {
            NotificationPreviews = new Dictionary<string, MailNotificationPreview>
            {
                ["101"] = new("Sender", "sender@example.test", "Subject", "Preview")
            }
        });
        using MailBackgroundPollingMonitor monitor = CreateMonitor([account], provider);
        List<MailNewMessageDetectedEventArgs> events = Subscribe(monitor);

        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();

        MailNewMessageDetectedEventArgs detected = Assert.Single(events);
        Assert.Equal(1, detected.NewMessageCount);
        Assert.Null(detected.Preview);
        Assert.Empty(provider.PreviewRequests);
    }

    [Fact]
    public async Task YandexDisableReenableAndUidValidityChange_BaselineWithoutFlood()
    {
        MailAccount account = Account();
        SnapshotProvider provider = new();
        provider.Enqueue(account.Id, Snapshot(1, "100"));
        provider.Enqueue(account.Id, Snapshot(2, "100", "101"));
        provider.Enqueue(account.Id, Snapshot(3, "500", "501") with { IdentityScope = "imap-inbox:999" });
        using var monitor = CreateMonitor([account], provider);
        var events = Subscribe(monitor);
        await monitor.PollOnceAsync();
        account.IsEnabled = false;
        await monitor.PollOnceAsync();
        account.IsEnabled = true;
        await monitor.PollOnceAsync();
        await monitor.PollOnceAsync();
        Assert.Empty(events);
    }

    private static MailBackgroundPollingMonitor CreateMonitor(
        IReadOnlyList<MailAccount> accounts,
        SnapshotProvider provider,
        ImapMailboxChangeTracker? changes = null)
    {
        TestSettingsStore settings = new(new AppSettings { MailAccounts = accounts.ToList() });
        return new MailBackgroundPollingMonitor(
            settings,
            new TestProviderFactory(provider),
            new ImmediateDispatcher(),
            TimeProvider.System,
            changes);
    }

    private static List<MailNewMessageDetectedEventArgs> Subscribe(MailBackgroundPollingMonitor monitor)
    {
        List<MailNewMessageDetectedEventArgs> events = [];
        monitor.MailNewMessageDetected += (_, eventArgs) => events.Add(eventArgs);
        return events;
    }

    private static MailInboxTechnicalSnapshot Snapshot(int unreadCount, params string[] identities) =>
        new(unreadCount, "imap-inbox:812", identities);

    private static BodyPartText TextPart(string contentType, string partSpecifier) =>
        new(ContentType.Parse(contentType), partSpecifier)
        {
            ContentTransferEncoding = "8bit"
        };

    private static string DecodePreview(string contentType, string transferEncoding, byte[] body)
    {
        BodyPartText part = TextPart(contentType, "1");
        part.ContentTransferEncoding = transferEncoding;
        return MailKitImapInboxClient.ExtractNotificationPreviewText(part, body);
    }

    private static string ToQuotedPrintable(byte[] bytes) =>
        string.Concat(bytes.Select(value => $"={value:X2}"));

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

        throw new FileNotFoundException(Path.Combine(relativePath));
    }

    private static BodyPartMultipart Multipart(string contentType, string partSpecifier, params BodyPart[] parts)
    {
        BodyPartCollection collection = new();
        foreach (BodyPart part in parts)
        {
            collection.Add(part);
        }

        return new BodyPartMultipart(ContentType.Parse(contentType), partSpecifier, collection);
    }

    private static MailAccount Account(MailProviderType provider = MailProviderType.Yandex) =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            EmailAddress = "mail@example.test",
            CredentialKey = "credential",
            IsEnabled = true
        };

    private static IMailProviderFactory CreateMailProviderFactory()
    {
        NoOpConnectionValidator validator = new();
        return new MailProviderFactory(
        [
            new GmailApiProvider(),
            new YandexMailProvider(validator),
            new MailRuMailProvider(validator),
            new GenericImapMailProvider(validator)
        ]);
    }

    private sealed class SnapshotProvider : IMailReadProvider, IMailInboxTechnicalSnapshotProvider, IMailInboxNotificationPreviewProvider
    {
        private readonly Dictionary<Guid, Queue<Func<MailInboxTechnicalSnapshot>>> _results = [];
        private readonly Dictionary<Guid, int> _pollCounts = [];

        public TaskCompletionSource FirstPollCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, MailNotificationPreview> EnrichedPreviews { get; } = new(StringComparer.Ordinal);
        public List<string> PreviewRequests { get; } = [];
        public bool ThrowOnPreviewRequest { get; init; }

        public bool Supports(MailProviderType providerType) => true;

        public void Enqueue(Guid accountId, MailInboxTechnicalSnapshot snapshot) =>
            Queue(accountId).Enqueue(() => snapshot);

        public void EnqueueFailure(Guid accountId) =>
            Queue(accountId).Enqueue(() => throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                "Background poll failed."));

        public int PollCount(Guid accountId) => _pollCounts.GetValueOrDefault(accountId);

        public Task<MailInboxTechnicalSnapshot> GetInboxTechnicalSnapshotAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pollCounts[account.Id] = PollCount(account.Id) + 1;
            try
            {
                return Task.FromResult(_results[account.Id].Dequeue().Invoke());
            }
            finally
            {
                FirstPollCompleted.TrySetResult();
            }
        }

        public Task<MailNotificationPreview?> GetInboxNotificationPreviewAsync(
            MailAccount account,
            string identityScope,
            string messageIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreviewRequests.Add(messageIdentity);
            if (ThrowOnPreviewRequest)
            {
                throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Preview fetch failed.");
            }

            EnrichedPreviews.TryGetValue(messageIdentity, out MailNotificationPreview? preview);
            return Task.FromResult(preview);
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

        private Queue<Func<MailInboxTechnicalSnapshot>> Queue(Guid accountId)
        {
            if (!_results.TryGetValue(accountId, out Queue<Func<MailInboxTechnicalSnapshot>>? queue))
            {
                queue = new Queue<Func<MailInboxTechnicalSnapshot>>();
                _results.Add(accountId, queue);
            }

            return queue;
        }
    }

    private sealed class TestProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class GmailHistoryClient(
        GmailApiHistoryBaseline baseline,
        GmailApiHistoryDelta delta) : IGmailApiReadClient
    {
        public Guid? AccountId { get; private set; }

        public Task<GmailApiHistoryBaseline> GetHistoryBaselineAsync(
            MailCredential credential,
            Guid accountId,
            CancellationToken cancellationToken = default)
        {
            AccountId = accountId;
            return Task.FromResult(baseline);
        }

        public Task<GmailApiHistoryDelta> GetHistoryDeltaAsync(
            MailCredential credential,
            Guid accountId,
            ulong historyId,
            CancellationToken cancellationToken = default)
        {
            AccountId = accountId;
            return Task.FromResult(delta);
        }

        public Task<GmailApiInboxPage> GetInboxPageAsync(
            MailCredential credential,
            Guid accountId,
            string? pageToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromException<GmailApiInboxPage>(new NotSupportedException());

        public Task<GmailApiRawMessage> GetRawMessageAsync(
            MailCredential credential,
            Guid accountId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<GmailApiRawMessage>(new NotSupportedException());
    }

    private sealed class ImapSnapshotClient(ImapInboxTechnicalSnapshot snapshot) : IImapInboxClient
    {
        public MailServerSettings? Server { get; private set; }

        public Task<ImapInboxTechnicalSnapshot> GetInboxTechnicalSnapshotAsync(
            MailServerSettings server,
            string secret,
            CancellationToken cancellationToken = default)
        {
            Server = server;
            return Task.FromResult(snapshot);
        }

        public Task<ImapInboxPageData> GetInboxPageAsync(
            MailServerSettings server,
            string secret,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapInboxPageData>(new NotSupportedException());

        public Task<ImapMessageData> GetMessageAsync(
            MailServerSettings server,
            string secret,
            uint uniqueId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ImapMessageData>(new NotSupportedException());
    }

    private sealed class TestCredentialStore(MailCredential credential) : IMailCredentialStore
    {
        public Task SaveAsync(
            string credentialKey,
            MailCredential value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MailCredential?> LoadAsync(
            string credentialKey,
            CancellationToken cancellationToken = default) => Task.FromResult<MailCredential?>(credential);

        public Task DeleteAsync(
            string credentialKey,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
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
