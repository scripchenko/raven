using MimeKit;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailReplyAllTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 9, 11, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void SenderAndCurrentUserOnly_ProducesSenderOnly()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(to: [Address("User", "user@gmail.test")]));

        AssertAddresses(reply.To, "sender@example.test");
        Assert.Empty(Parse(reply.Cc));
    }

    [Fact]
    public void AdditionalOriginalToRecipients_AreAddedAfterPrimaryTarget()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(to:
            [
                Address("User", "user@gmail.test"),
                Address("Person B", "person-b@example.test"),
                Address("Person C", "person-c@example.test")
            ]));

        AssertAddresses(
            reply.To,
            "sender@example.test",
            "person-b@example.test",
            "person-c@example.test");
    }

    [Fact]
    public void OriginalCcIsPreservedAndOwnAccountIsRemovedFromBothFields()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(
                to:
                [
                    Address("User To", "USER@gmail.test"),
                    Address("Other", "other@example.test")
                ],
                cc:
                [
                    Address("User Cc", "user@GMAIL.test"),
                    Address("Copy", "copy@example.test")
                ]));

        AssertAddresses(reply.To, "sender@example.test", "other@example.test");
        AssertAddresses(reply.Cc, "copy@example.test");
    }

    [Fact]
    public void DuplicatesWithinToAndAcrossCcAreRemovedCaseInsensitively()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(
                to:
                [
                    Address("First", "duplicate@example.test"),
                    Address("Second", "DUPLICATE@example.test")
                ],
                cc:
                [
                    Address("Cross duplicate", "Duplicate@Example.Test"),
                    Address("Unique copy", "copy@example.test"),
                    Address("Repeated copy", "COPY@example.test")
                ]));

        AssertAddresses(reply.To, "sender@example.test", "duplicate@example.test");
        AssertAddresses(reply.Cc, "copy@example.test");
    }

    [Fact]
    public void ReplyToOverridesFromWithoutAddingFromAsAnotherRecipient()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(
                replyTo: "Support <reply@example.test>",
                to: [Address("User", "user@gmail.test")]));

        AssertAddresses(reply.To, "reply@example.test");
        Assert.DoesNotContain(
            Parse(reply.To),
            address => string.Equals(address.Address, "sender@example.test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidReplyToUsesExistingReplyFallbackToFrom()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(
                replyTo: "invalid",
                to: [Address("User", "user@gmail.test")]));

        AssertAddresses(reply.To, "sender@example.test");
    }

    [Fact]
    public void DisplayNamesAndUtf8ArePreservedFromFirstUniqueOccurrence()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(
                fromName: "Отправитель",
                to:
                [
                    Address("User", "user@gmail.test"),
                    Address("Мария Иванова", "maria@example.test"),
                    Address("Ignored duplicate", "MARIA@example.test")
                ],
                cc: [Address("Команда Поддержки", "support@example.test")]));

        MailboxAddress[] to = Parse(reply.To);
        MailboxAddress[] cc = Parse(reply.Cc);
        Assert.Equal("Отправитель", to[0].Name);
        Assert.Equal("Мария Иванова", to[1].Name);
        Assert.Equal("Команда Поддержки", Assert.Single(cc).Name);
    }

    [Fact]
    public void MalformedOptionalRecipientIsSkippedWithoutDestroyingValidRecipients()
    {
        MailComposeTemplate reply = CreateReplyAll(
            Account("user@gmail.test"),
            Source(
                to:
                [
                    Address("Broken", "not-an-email"),
                    Address("Valid", "valid@example.test")
                ],
                cc:
                [
                    Address("Broken Cc", "missing-domain@"),
                    Address("Valid Cc", "valid-cc@example.test")
                ]));

        AssertAddresses(reply.To, "sender@example.test", "valid@example.test");
        AssertAddresses(reply.Cc, "valid-cc@example.test");
    }

    [Fact]
    public void ReplyAllUsesReplySubjectBodyAndCompleteThreadContext()
    {
        MailMessageContent source = Source(
            subject: "RE: Re: Discussion",
            messageId: "current@example.test",
            references: ["older@example.test"]);
        source = source with
        {
            ReplyMetadata = source.ReplyMetadata! with
            {
                ProviderThreadId = "gmail-thread-id"
            }
        };

        MailComposeTemplate reply = CreateReplyAll(Account("user@gmail.test"), source);

        Assert.Equal("Re: Discussion", reply.Subject);
        Assert.Contains("> Original body", reply.TextBody, StringComparison.Ordinal);
        Assert.Equal("current@example.test", reply.ReplyContext!.InReplyTo);
        Assert.Equal(["older@example.test", "current@example.test"], reply.ReplyContext.References);
        Assert.Equal("gmail-thread-id", reply.ReplyContext.ProviderThreadId);
    }

    [Fact]
    public void ReplyAllMimeUsesSameRfcThreadHeadersAsReply()
    {
        MailAccount account = Account("user@gmail.test");
        MailComposeTemplate template = CreateReplyAll(
            account,
            Source(
                messageId: "current@example.test",
                references: ["older@example.test"]));
        MailComposeRequest request = new MailComposeRequestFactory().Create(
            account,
            new MailComposeInput(
                template.To,
                template.Cc,
                template.Bcc,
                template.Subject,
                template.TextBody,
                template.ReplyContext));

        MimeMessage message = new MailMimeMessageFactory(TimeProvider.System).Create(account, request).Message;

        Assert.Equal("current@example.test", message.InReplyTo);
        Assert.Equal(["older@example.test", "current@example.test"], message.References);
    }

    [Fact]
    public void ActiveGmailAccountAloneDefinesOwnIdentity()
    {
        MailMessageContent source = Source(to:
        [
            Address("Gmail A", "a@gmail.test"),
            Address("Gmail B", "b@gmail.test")
        ]);

        MailComposeTemplate fromA = CreateReplyAll(Account("a@gmail.test"), source);
        MailComposeTemplate fromB = CreateReplyAll(Account("b@gmail.test"), source);

        AssertAddresses(fromA.To, "sender@example.test", "b@gmail.test");
        AssertAddresses(fromB.To, "sender@example.test", "a@gmail.test");
    }

    [Fact]
    public void ContentExtractorCapturesStructuredOriginalToAndCc()
    {
        MimeMessage message = new()
        {
            Subject = "Subject",
            Date = ReceivedAt,
            MessageId = "message@example.test",
            Body = new TextPart("plain") { Text = "Body" }
        };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Получатель", "recipient@example.test"));
        message.Cc.Add(new MailboxAddress("Копия", "copy@example.test"));

        MailMessageContent content = new MailContentExtractor(new MailHtmlSanitizer())
            .Extract("gmail:message", message, isUnread: false);

        Assert.Equal(
            new MailMessageAddress("Получатель", "recipient@example.test"),
            Assert.Single(content.ReplyMetadata!.OriginalTo));
        Assert.Equal(
            new MailMessageAddress("Копия", "copy@example.test"),
            Assert.Single(content.ReplyMetadata.OriginalCc));
    }

    [Fact]
    public async Task ReplyAllCommandInitializesEditableComposeAndDoesNotOpenEmptyOwnOnlyDraft()
    {
        MailAccount account = Account("user@gmail.test");
        using MailComposeViewModel compose = Compose();
        compose.ActivateAccount(account);
        MailMessageContent source = Source(
            to: [Address("Second", "second@example.test")],
            cc: [Address("Copy", "copy@example.test")]);

        await compose.ReplyAllCommand.ExecuteAsync(source);

        Assert.True(compose.IsOpen);
        Assert.False(string.IsNullOrWhiteSpace(compose.Draft!.To));
        Assert.False(string.IsNullOrWhiteSpace(compose.Draft.Cc));
        compose.Draft.To = "edited@example.test";
        compose.Draft.Cc = "edited-copy@example.test";
        compose.Draft.Subject = "Edited";
        compose.Draft.TextBody = "Edited body";
        Assert.Equal("edited@example.test", compose.Draft.To);

        await compose.CancelCommand.ExecuteAsync(null);
        MailMessageContent ownOnly = Source(
            fromAddress: "user@gmail.test",
            to: [Address("User", "user@gmail.test")]);
        await compose.ReplyAllCommand.ExecuteAsync(ownOnly);

        Assert.False(compose.IsOpen);
    }

    [Fact]
    public void ReplyAllIsExposedForGmailAndYandexAccountsOnly()
    {
        using MailComposeViewModel compose = Compose();
        MailMessageContent source = Source();
        compose.ActivateAccount(Account("user@gmail.test"));

        Assert.True(compose.IsReplyAllAvailable);
        Assert.True(compose.ReplyAllCommand.CanExecute(source));

        compose.ActivateAccount(Account("user@yandex.test", MailProviderType.Yandex));

        Assert.True(compose.IsReplyAllAvailable);
        Assert.True(compose.ReplyAllCommand.CanExecute(source));

        compose.ActivateAccount(Account("user@example.test", MailProviderType.GenericImap));

        Assert.False(compose.IsReplyAllAvailable);
        Assert.False(compose.ReplyAllCommand.CanExecute(source));
    }

    [Fact]
    public async Task YandexReplyAll_UsesSharedRecipientSemanticsAndYandexSendPipeline()
    {
        MailAccount account = Account("owner@yandex.test", MailProviderType.Yandex);
        RecordingSendProvider sender = new();
        using MailComposeViewModel compose = Compose(sender);
        compose.ActivateAccount(account);
        MailMessageContent source = Source(
            replyTo: "Reply target <reply@example.test>",
            to:
            [
                Address("Owner", "OWNER@yandex.test"),
                Address("Second", "second@example.test"),
                Address("Duplicate", "SECOND@example.test")
            ],
            cc:
            [
                Address("Copy", "copy@example.test"),
                Address("Cross duplicate", "second@example.test"),
                Address("Owner copy", "owner@yandex.test")
            ],
            messageId: "reply-source@example.test",
            references: ["prior@example.test"]);

        await compose.ReplyAllCommand.ExecuteAsync(source);

        Assert.True(compose.IsOpen);
        AssertAddresses(compose.Draft!.To, "reply@example.test", "second@example.test");
        AssertAddresses(compose.Draft.Cc, "copy@example.test");
        Assert.Equal("Re: Subject", compose.Draft.Subject);
        Assert.Equal("reply-source@example.test", compose.Draft.ReplyContext!.InReplyTo);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Same(account, sender.Account);
        Assert.Equal(MailProviderType.Yandex, sender.Account!.Provider);
        Assert.Equal("reply-source@example.test", sender.Request!.ReplyContext!.InReplyTo);
    }

    private static MailComposeTemplate CreateReplyAll(MailAccount account, MailMessageContent source) =>
        new MailComposePreparationService().CreateReplyAll(source, account);

    private static MailMessageContent Source(
        string fromName = "Sender",
        string fromAddress = "sender@example.test",
        string replyTo = "",
        IReadOnlyList<MailMessageAddress>? to = null,
        IReadOnlyList<MailMessageAddress>? cc = null,
        string subject = "Subject",
        string? messageId = "message@example.test",
        IReadOnlyList<string>? references = null) =>
        new(
            "gmail:message",
            subject,
            fromName,
            fromAddress,
            string.Empty,
            ReceivedAt,
            MailMessageBodyKind.PlainText,
            "Original body",
            [],
            true,
            false,
            "Original body",
            new MailReplyMetadata(replyTo, messageId, references ?? [])
            {
                OriginalTo = to ?? [],
                OriginalCc = cc ?? []
            });

    private static MailMessageAddress Address(string name, string address) => new(name, address);

    private static MailAccount Account(string email, MailProviderType provider = MailProviderType.Gmail) => new()
    {
        Id = Guid.NewGuid(),
        Provider = provider,
        EmailAddress = email,
        DisplayName = "Mail user",
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = provider is MailProviderType.Gmail
            ? MailAuthenticationKind.OAuth
            : MailAuthenticationKind.Password,
        IsEnabled = true
    };

    private static MailboxAddress[] Parse(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : InternetAddressList.Parse(value).Mailboxes.ToArray();

    private static void AssertAddresses(string value, params string[] expected) =>
        Assert.Equal(
            expected,
            Parse(value).Select(address => address.Address),
            StringComparer.OrdinalIgnoreCase);

    private static MailComposeViewModel Compose(RecordingSendProvider? sender = null) =>
        new(
            new SendProviderFactory(sender ?? new RecordingSendProvider()),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            new AlwaysConfirmService());

    private sealed class SendProviderFactory(RecordingSendProvider sender) : IMailSendProviderFactory
    {
        public IMailSendProvider Get(MailProviderType providerType) => sender;
    }

    private sealed class RecordingSendProvider : IMailSendProvider
    {
        public MailAccount? Account { get; private set; }
        public MailComposeRequest? Request { get; private set; }

        public bool Supports(MailProviderType providerType) => true;

        public Task<MailSendResult> SendAsync(
            MailAccount account,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            Account = account;
            Request = request;
            return Task.FromResult(MailSendResult.Success());
        }
    }

    private sealed class AlwaysConfirmService : IMailComposeConfirmationService
    {
        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
