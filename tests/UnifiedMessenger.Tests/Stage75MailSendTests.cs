using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MimeKit;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class Stage75MailSendTests
{
    [Fact]
    public void ComposeRequest_IsProviderNeutral()
    {
        Type[] propertyTypes = typeof(MailComposeRequest).GetProperties()
            .Select(property => property.PropertyType)
            .ToArray();

        Assert.DoesNotContain(propertyTypes, type => type.Namespace?.StartsWith("Google", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(propertyTypes, type => type.Namespace?.StartsWith("MailKit", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(propertyTypes, type => type.Namespace?.StartsWith("MimeKit", StringComparison.Ordinal) == true);
        Assert.Null(typeof(MailReplyMetadata).GetProperty("ProviderThreadId"));
        Assert.Null(typeof(MailReplyContext).GetProperty("ProviderThreadId"));
    }

    [Theory]
    [InlineData("person@example.test", 1)]
    [InlineData("Person <person@example.test>", 1)]
    [InlineData("first@example.test, Second <second@example.test>", 2)]
    public void AddressParser_AcceptsStandardMailboxForms(string recipients, int expectedCount)
    {
        MailComposeRequest request = CreateRequestFactory().Create(
            Account(MailProviderType.Gmail),
            new MailComposeInput(recipients, string.Empty, string.Empty, string.Empty, "Body"));

        Assert.Equal(expectedCount, request.To.Count);
        Assert.Equal(expectedCount, request.RecipientCount);
    }

    [Fact]
    public void AddressParser_SupportsCcAndBcc()
    {
        MailComposeRequest request = CreateRequestFactory().Create(
            Account(MailProviderType.Yandex),
            new MailComposeInput(
                "to@example.test",
                "cc@example.test",
                "bcc@example.test",
                string.Empty,
                string.Empty));

        Assert.Single(request.To);
        Assert.Single(request.Cc);
        Assert.Single(request.Bcc);
        Assert.Equal(3, request.RecipientCount);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("valid@example.test, broken")]
    [InlineData("victim@example.test\r\nBcc: injected@example.test")]
    public void InvalidOrInjectedAddress_IsRejected(string value)
    {
        Assert.Throws<MailComposeValidationException>(() => CreateRequestFactory().Create(
            Account(MailProviderType.Gmail),
            new MailComposeInput(value, string.Empty, string.Empty, "Subject", "Body")));
    }

    [Fact]
    public void ZeroRecipients_IsRejected()
    {
        Assert.Throws<MailComposeValidationException>(() => CreateRequestFactory().Create(
            Account(MailProviderType.Gmail),
            new MailComposeInput(string.Empty, string.Empty, string.Empty, "Subject", "Body")));
    }

    [Fact]
    public void EmptySubject_IsAllowed()
    {
        MailComposeRequest request = CreateRequestFactory().Create(
            Account(MailProviderType.Gmail),
            new MailComposeInput("to@example.test", string.Empty, string.Empty, string.Empty, "Body"));

        Assert.Equal(string.Empty, request.Subject);
    }

    [Fact]
    public void SubjectHeaderInjection_IsRejected()
    {
        Assert.Throws<MailComposeValidationException>(() => CreateRequestFactory().Create(
            Account(MailProviderType.Gmail),
            new MailComposeInput(
                "to@example.test",
                string.Empty,
                string.Empty,
                "Subject\r\nBcc: injected@example.test",
                "Body")));
    }

    [Fact]
    public void MimeFactory_GeneratesUtf8PlainTextAndRequiredHeaders()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        MailComposeRequest request = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "Recipient <to@example.test>",
                "cc@example.test",
                string.Empty,
                "Тема",
                "Привет, мир"));

        MailMimeSubmission submission = CreateMimeFactory().Create(account, request);
        TextPart body = Assert.IsType<TextPart>(submission.Message.Body);

        Assert.Equal("plain", body.ContentType.MediaSubtype);
        Assert.Equal("utf-8", body.ContentType.Charset, ignoreCase: true);
        Assert.Equal("Привет, мир", body.Text);
        Assert.Equal("Тема", submission.Message.Subject);
        Assert.False(string.IsNullOrWhiteSpace(submission.Message.MessageId));
        Assert.Equal(FixedNow, submission.Message.Date);
        Assert.Equal("sender@example.test", submission.Message.From.Mailboxes.Single().Address);
    }

    [Fact]
    public void MimeFactory_BccIsInEnvelopeAndMimeForGmailSemantics()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        MailComposeRequest request = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "to@example.test",
                "cc@example.test",
                "hidden@example.test",
                "Subject",
                "Body"));

        MailMimeSubmission submission = CreateMimeFactory().Create(account, request);

        Assert.Equal(3, submission.EnvelopeRecipients.Count);
        Assert.Contains(submission.EnvelopeRecipients, address => address.Address == "hidden@example.test");
        Assert.Equal("hidden@example.test", submission.Message.Bcc.Mailboxes.Single().Address);
    }

    [Fact]
    public async Task GmailSend_UsesRawMimeAndThreadContext()
    {
        RecordingGmailApiClient api = new();
        MailAccount account = Account(MailProviderType.Gmail);
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            api,
            CreateMimeFactory());
        MailComposeRequest request = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "to@example.test",
                string.Empty,
                string.Empty,
                "Re: Subject",
                "Reply",
                new MailReplyContext("original@example.test", ["older@example.test", "original@example.test"])
                {
                    ProviderThreadId = "thread-1"
                }));

        MailSendResult result = await provider.SendAsync(account, request);
        using MemoryStream stream = new(api.RawMime!, writable: false);
        MimeMessage raw = await MimeMessage.LoadAsync(stream);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, api.SendCount);
        Assert.Equal("thread-1", api.ThreadId);
        Assert.Equal("original@example.test", raw.InReplyTo);
        Assert.Contains("original@example.test", raw.References);
        Assert.Equal("Reply", raw.TextBody);
    }

    [Fact]
    public async Task GmailForward_DoesNotUseSourceThreadOrReplyHeaders()
    {
        RecordingGmailApiClient api = new();
        MailAccount account = Account(MailProviderType.Gmail);
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            api,
            CreateMimeFactory());
        MailComposeTemplate forward = new MailComposePreparationService().CreateForward(Content(
            replyMetadata: new MailReplyMetadata(
                "reply@example.test",
                "source-message@example.test",
                ["older@example.test"])
            {
                ProviderThreadId = "source-thread"
            }));
        MailComposeRequest request = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "recipient@example.test",
                forward.Cc,
                forward.Bcc,
                forward.Subject,
                forward.TextBody,
                forward.ReplyContext));

        MailSendResult result = await provider.SendAsync(account, request);
        using MemoryStream stream = new(api.RawMime!, writable: false);
        MimeMessage raw = await MimeMessage.LoadAsync(stream);

        Assert.True(result.IsSuccess);
        Assert.Null(api.ThreadId);
        Assert.Null(raw.InReplyTo);
        Assert.Empty(raw.References);
        Assert.NotEqual("source-message@example.test", raw.MessageId);
    }

    [Fact]
    public void GmailRawEncoding_IsBase64UrlWithoutPadding()
    {
        string encoded = GmailApiSendClient.EncodeBase64Url([0xfb, 0xff, 0xef]);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.Equal([0xfb, 0xff, 0xef], GmailApiReadClient.DecodeBase64Url(encoded));
    }

    [Fact]
    public async Task GmailModifyCredential_IsAcceptedWithoutAdditionalScope()
    {
        RecordingGmailApiClient api = new();
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            api,
            CreateMimeFactory());
        MailAccount account = Account(MailProviderType.Gmail);

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, api.SendCount);
        Assert.Equal(GmailOAuthConstants.ModifyScope, ModifyCredential().OAuthScope);
        Assert.DoesNotContain("gmail.send", ModifyCredential().OAuthScope!, StringComparison.Ordinal);
        Assert.DoesNotContain("gmail.compose", ModifyCredential().OAuthScope!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GmailReadonlyCredential_ReportsCapabilityAndDoesNotSend()
    {
        RecordingGmailApiClient api = new();
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ReadOnlyCredential()),
            api,
            CreateMimeFactory());
        MailAccount account = Account(MailProviderType.Gmail);

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.False(result.IsSuccess);
        Assert.Equal(MailSendFailureKind.CapabilityUnavailable, result.FailureKind);
        Assert.Equal(0, api.SendCount);
    }

    [Fact]
    public void GmailSendProvider_HasNoSmtpOrDeleteDependency()
    {
        Type[] dependencies = typeof(GmailMailSendProvider).GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(dependencies, type => type.Name.Contains("Smtp", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, type => type.Name.Contains("Imap", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(GmailApiSendClient).GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance),
            method => method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Trash", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(MailProviderType.Yandex, "smtp.yandex.com", 465)]
    [InlineData(MailProviderType.MailRu, "smtp.mail.ru", 465)]
    public async Task PresetProviders_ResolveExpectedSmtp(
        MailProviderType providerType,
        string expectedHost,
        int expectedPort)
    {
        RecordingSmtpSubmissionClient smtp = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp);
        MailAccount account = Account(providerType);

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedHost, smtp.Server!.Host);
        Assert.Equal(expectedPort, smtp.Server.Port);
        Assert.Equal(account.EmailAddress, smtp.Server.Username);
        Assert.Equal(MailSecureSocketMode.SslOnConnect, smtp.Server.SecureSocketMode);
    }

    [Fact]
    public async Task GenericProvider_UsesConfiguredSmtp()
    {
        RecordingSmtpSubmissionClient smtp = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp);
        MailAccount account = Account(MailProviderType.GenericImap);
        account.GenericConnectionSettings = new MailConnectionSettings
        {
            Imap = new MailServerSettings { Host = "imap.example.test", Port = 993, Username = account.EmailAddress },
            Smtp = new MailServerSettings
            {
                Host = "smtp.example.test",
                Port = 587,
                SecureSocketMode = MailSecureSocketMode.StartTls,
                Username = "smtp-user@example.test"
            }
        };

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.True(result.IsSuccess);
        Assert.Equal("smtp.example.test", smtp.Server!.Host);
        Assert.Equal(587, smtp.Server.Port);
        Assert.Equal(MailSecureSocketMode.StartTls, smtp.Server.SecureSocketMode);
    }

    [Fact]
    public async Task SmtpProvider_UsesDpapiStoreCredentialWithoutPersistence()
    {
        FixedCredentialStore store = new(MailCredential.CreatePassword("app-password"));
        RecordingSmtpSubmissionClient smtp = new();
        RecordingImapSentCopyClient sentCopy = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp, store, sentCopy);
        MailAccount account = Account(MailProviderType.Yandex);

        await provider.SendAsync(account, Request(account));

        Assert.Equal(1, store.LoadCount);
        Assert.Equal(account.CredentialKey, store.LastLoadedKey);
        Assert.Equal("app-password", smtp.Secret);
        Assert.Equal("app-password", sentCopy.Secret);
        Assert.Equal(1, sentCopy.AppendCount);
        Assert.DoesNotContain(
            typeof(MailAccount).GetProperties(),
            property => property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SmtpBcc_IsEnvelopeOnlyAtSubmissionBoundary()
    {
        RecordingSmtpSubmissionClient smtp = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp);
        MailAccount account = Account(MailProviderType.Yandex);
        MailComposeRequest request = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "to@example.test",
                "cc@example.test",
                "hidden@example.test",
                "Subject",
                "Body"));

        MailSendResult result = await provider.SendAsync(account, request);

        Assert.True(result.IsSuccess);
        Assert.Empty(smtp.Message!.Bcc);
        Assert.DoesNotContain("Bcc:", Serialize(smtp.Message), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(smtp.EnvelopeRecipients!, address => address.Address == "hidden@example.test");
    }

    [Theory]
    [InlineData(MailProviderType.Yandex, "imap.yandex.com")]
    [InlineData(MailProviderType.MailRu, "imap.mail.ru")]
    public async Task SmtpSuccess_AppendsExactlySameMimeOnceToProviderSentFolder(
        MailProviderType providerType,
        string expectedImapHost)
    {
        RecordingSmtpSubmissionClient smtp = new();
        RecordingImapSentCopyClient sentCopy = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp, sentCopy: sentCopy);
        MailAccount account = Account(providerType);
        MailComposeRequest request = Request(account);

        MailSendResult result = await provider.SendAsync(account, request);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsMessageSent);
        Assert.True(result.SentCopySaved);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(1, sentCopy.AppendCount);
        Assert.Equal(expectedImapHost, sentCopy.Server!.Host);
        Assert.Same(smtp.Message, sentCopy.Message);
        Assert.False(string.IsNullOrWhiteSpace(sentCopy.Message!.MessageId));
        Assert.Equal(FixedNow, sentCopy.Message.Date);
        Assert.Equal("Subject", sentCopy.Message.Subject);
        Assert.Equal("Body", sentCopy.Message.TextBody);
        Assert.Equal("to@example.test", sentCopy.Message.To.Mailboxes.Single().Address);
        Assert.Equal(MailKit.MessageFlags.Seen, sentCopy.Flags);
    }

    [Fact]
    public async Task GenericSmtpSuccess_UsesConfiguredImapForSingleSentAppend()
    {
        RecordingSmtpSubmissionClient smtp = new();
        RecordingImapSentCopyClient sentCopy = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp, sentCopy: sentCopy);
        MailAccount account = Account(MailProviderType.GenericImap);
        account.GenericConnectionSettings = new MailConnectionSettings
        {
            Imap = new MailServerSettings
            {
                Host = "imap.example.test",
                Port = 993,
                SecureSocketMode = MailSecureSocketMode.SslOnConnect,
                Username = account.EmailAddress
            },
            Smtp = new MailServerSettings
            {
                Host = "smtp.example.test",
                Port = 465,
                SecureSocketMode = MailSecureSocketMode.SslOnConnect,
                Username = account.EmailAddress
            }
        };

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(1, sentCopy.AppendCount);
        Assert.Equal("imap.example.test", sentCopy.Server!.Host);
        Assert.Same(smtp.Message, sentCopy.Message);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex, MailSendFailureKind.MessageRejected)]
    [InlineData(MailProviderType.Yandex, MailSendFailureKind.Ambiguous)]
    [InlineData(MailProviderType.MailRu, MailSendFailureKind.MessageRejected)]
    [InlineData(MailProviderType.MailRu, MailSendFailureKind.Ambiguous)]
    public async Task SmtpFailure_NeverAttemptsSentAppend(
        MailProviderType providerType,
        MailSendFailureKind failureKind)
    {
        RecordingSmtpSubmissionClient smtp = new()
        {
            Failure = new MailSubmissionException(failureKind, "SMTP failed.")
        };
        RecordingImapSentCopyClient sentCopy = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp, sentCopy: sentCopy);
        MailAccount account = Account(providerType);

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.False(result.IsMessageSent);
        Assert.Equal(failureKind, result.FailureKind);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(0, sentCopy.AppendCount);
    }

    [Theory]
    [InlineData(MailSentCopyFailureKind.FolderUnavailable)]
    [InlineData(MailSentCopyFailureKind.AuthenticationFailed)]
    [InlineData(MailSentCopyFailureKind.Ambiguous)]
    public async Task SmtpSuccessAndSentAppendFailure_IsPartialSuccessWithoutRetry(
        MailSentCopyFailureKind copyFailure)
    {
        RecordingSmtpSubmissionClient smtp = new();
        RecordingImapSentCopyClient sentCopy = new()
        {
            Result = ImapSentCopyResult.Failed(copyFailure)
        };
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp, sentCopy: sentCopy);
        MailAccount account = Account(MailProviderType.Yandex);

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsMessageSent);
        Assert.True(result.IsPartialSuccess);
        Assert.False(result.SentCopySaved);
        Assert.Equal(MailSendFailureKind.None, result.FailureKind);
        Assert.Equal(copyFailure, result.SentCopyFailureKind);
        Assert.Contains("Письмо отправлено", result.UserMessage, StringComparison.Ordinal);
        Assert.Contains("не удалось сохранить копию", result.UserMessage, StringComparison.Ordinal);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(1, sentCopy.AppendCount);
    }

    [Fact]
    public async Task UnexpectedSentAppendFailure_IsPartialSuccessAndIsNotRetried()
    {
        RecordingSmtpSubmissionClient smtp = new();
        RecordingImapSentCopyClient sentCopy = new() { Failure = new InvalidOperationException("append") };
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp, sentCopy: sentCopy);
        MailAccount account = Account(MailProviderType.Yandex);

        MailSendResult result = await provider.SendAsync(account, Request(account));

        Assert.True(result.IsMessageSent);
        Assert.True(result.IsPartialSuccess);
        Assert.Equal(MailSentCopyFailureKind.Unexpected, result.SentCopyFailureKind);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(1, sentCopy.AppendCount);
    }

    [Fact]
    public async Task DoubleSend_ExecutesOneSmtpSendAndAtMostOneSentAppend()
    {
        DeferredSmtpSubmissionClient smtp = new();
        RecordingImapSentCopyClient sentCopy = new();
        SmtpMailSendProvider provider = CreateSmtpProvider(smtp, sentCopy: sentCopy);
        using MailComposeViewModel compose = CreateCompose(provider);
        OpenCompose(compose, Account(MailProviderType.Yandex));

        Task first = compose.SendCommand.ExecuteAsync(null);
        await smtp.Started.Task;
        Task second = compose.SendCommand.ExecuteAsync(null);
        Assert.Equal(0, sentCopy.AppendCount);
        smtp.Completion.TrySetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(1, sentCopy.AppendCount);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task MailKitSubmission_UsesConnectAuthenticateSendDisconnectLifecycle()
    {
        RecordingSmtpSession session = new();
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;
        MailboxAddress sender = message.From.Mailboxes.Single();

        await client.SendAsync(
            new MailServerSettings
            {
                Host = "smtp.example.test",
                Port = 465,
                Username = "sender@example.test",
                SecureSocketMode = MailSecureSocketMode.SslOnConnect
            },
            "secret",
            message,
            sender,
            message.To.Mailboxes.ToArray());

        Assert.Equal(["connect", "authenticate", "send", "disconnect", "dispose"], session.Events);
    }

    [Fact]
    public async Task MailKitSubmission_CleansConnectionAndMarksTransportLossAmbiguous()
    {
        RecordingSmtpSession session = new() { SendFailure = new IOException("transport") };
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        MailSubmissionException exception = await Assert.ThrowsAsync<MailSubmissionException>(() => client.SendAsync(
            new MailServerSettings { Host = "smtp.example.test", Port = 465, Username = "sender@example.test" },
            "secret",
            message,
            message.From.Mailboxes.Single(),
            message.To.Mailboxes.ToArray()));

        Assert.Equal(MailSendFailureKind.Ambiguous, exception.FailureKind);
        Assert.Equal(SmtpSubmissionStage.DataAcceptance, exception.SmtpFailure!.Stage);
        Assert.Equal("ambiguous-post-submission-transport", exception.SmtpFailure.SanitizedReasonCategory);
        Assert.Equal(["connect", "authenticate", "send", "disconnect", "dispose"], session.Events);
    }

    [Fact]
    public async Task MailKitSubmission_AuthenticationFailureIsDefiniteAndCleansConnection()
    {
        RecordingSmtpSession session = new()
        {
            AuthenticationFailure = new MailKit.Security.AuthenticationException(
                "rejected",
                new MailKit.Net.Smtp.SmtpCommandException(
                    MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode,
                    MailKit.Net.Smtp.SmtpStatusCode.AuthenticationInvalidCredentials,
                    "5.7.8 rejected"))
        };
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        MailSubmissionException exception = await Assert.ThrowsAsync<MailSubmissionException>(() => client.SendAsync(
            new MailServerSettings { Host = "smtp.example.test", Port = 465, Username = "sender@example.test" },
            "secret",
            message,
            message.From.Mailboxes.Single(),
            message.To.Mailboxes.ToArray()));

        Assert.Equal(MailSendFailureKind.AuthenticationFailed, exception.FailureKind);
        Assert.Equal(SmtpSubmissionStage.Authentication, exception.SmtpFailure!.Stage);
        Assert.Equal(535, exception.SmtpFailure.StatusCode);
        Assert.Equal("5.7.8", exception.SmtpFailure.EnhancedStatusCode);
        Assert.Equal("authentication-rejected", exception.SmtpFailure.SanitizedReasonCategory);
        Assert.Equal(["connect", "authenticate", "disconnect", "dispose"], session.Events);
    }

    [Theory]
    [InlineData(MailKit.Net.Smtp.SmtpErrorCode.SenderNotAccepted, MailSendFailureKind.SenderRejected, "MailFrom", "sender-rejected")]
    [InlineData(MailKit.Net.Smtp.SmtpErrorCode.RecipientNotAccepted, MailSendFailureKind.RecipientRejected, "RcptTo", "recipient-rejected")]
    [InlineData(MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted, MailSendFailureKind.PolicyRejected, "DataAcceptance", "security-or-antispam-policy-rejected")]
    [InlineData(MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode, MailSendFailureKind.ProtocolRejected, "SubmissionProtocol", "unexpected-status")]
    public async Task MailKitSubmission_ExplicitSmtpRejectionIsDefinite(
        MailKit.Net.Smtp.SmtpErrorCode errorCode,
        MailSendFailureKind expected,
        string expectedStage,
        string expectedReason)
    {
        RecordingSmtpSession session = new()
        {
            SendFailure = new MailKit.Net.Smtp.SmtpCommandException(
                errorCode,
                MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable,
                "5.7.1 rejected")
        };
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        MailSubmissionException exception = await Assert.ThrowsAsync<MailSubmissionException>(() => client.SendAsync(
            new MailServerSettings { Host = "smtp.example.test", Port = 465, Username = "sender@example.test" },
            "secret",
            message,
            message.From.Mailboxes.Single(),
            message.To.Mailboxes.ToArray()));

        Assert.Equal(expected, exception.FailureKind);
        Assert.Equal(expectedStage, exception.SmtpFailure!.Stage.ToString());
        Assert.Equal(errorCode.ToString(), exception.SmtpFailure.MailKitErrorCategory);
        Assert.Equal(550, exception.SmtpFailure.StatusCode);
        Assert.Equal("5.7.1", exception.SmtpFailure.EnhancedStatusCode);
        Assert.Equal(expectedReason, exception.SmtpFailure.SanitizedReasonCategory);
        Assert.Contains("disconnect", session.Events);
    }

    [Fact]
    public async Task MailKitSubmission_YandexStyle554With571IsClassifiedAsPolicyRejection()
    {
        RecordingSmtpSession session = new()
        {
            SendFailure = new MailKit.Net.Smtp.SmtpCommandException(
                MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted,
                MailKit.Net.Smtp.SmtpStatusCode.TransactionFailed,
                "5.7.1 policy rejection")
        };
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        MailSubmissionException exception = await Assert.ThrowsAsync<MailSubmissionException>(() => client.SendAsync(
            new MailServerSettings { Host = "smtp.yandex.com", Port = 465, Username = "sender@example.test" },
            "secret",
            message,
            message.From.Mailboxes.Single(),
            message.To.Mailboxes.ToArray()));

        Assert.Equal(MailSendFailureKind.PolicyRejected, exception.FailureKind);
        Assert.Equal(SmtpSubmissionStage.DataAcceptance, exception.SmtpFailure!.Stage);
        Assert.Equal(nameof(MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted), exception.SmtpFailure.MailKitErrorCategory);
        Assert.Equal(554, exception.SmtpFailure.StatusCode);
        Assert.Equal("5.7.1", exception.SmtpFailure.EnhancedStatusCode);
        Assert.Equal("security-or-antispam-policy-rejected", exception.SmtpFailure.SanitizedReasonCategory);
        Assert.Contains("безопасности", exception.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["connect", "authenticate", "send", "disconnect", "dispose"], session.Events);
    }

    [Fact]
    public async Task MailKitSubmission_NonPolicyMessageRejectionRemainsMessageRejected()
    {
        RecordingSmtpSession session = new()
        {
            SendFailure = new MailKit.Net.Smtp.SmtpCommandException(
                MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted,
                MailKit.Net.Smtp.SmtpStatusCode.TransactionFailed,
                "5.6.0 malformed content")
        };
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        MailSubmissionException exception = await Assert.ThrowsAsync<MailSubmissionException>(() => client.SendAsync(
            new MailServerSettings { Host = "smtp.example.test", Port = 465, Username = "sender@example.test" },
            "secret",
            message,
            message.From.Mailboxes.Single(),
            message.To.Mailboxes.ToArray()));

        Assert.Equal(MailSendFailureKind.MessageRejected, exception.FailureKind);
        Assert.Equal("5.6.0", exception.SmtpFailure!.EnhancedStatusCode);
        Assert.Equal("message-or-policy-rejected", exception.SmtpFailure.SanitizedReasonCategory);
    }

    [Fact]
    public async Task MailKitSubmission_ConnectionFailureIsDefiniteBeforeSend()
    {
        RecordingSmtpSession session = new() { ConnectFailure = new IOException("dns") };
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        MailSubmissionException exception = await Assert.ThrowsAsync<MailSubmissionException>(() => client.SendAsync(
            new MailServerSettings { Host = "smtp.example.test", Port = 465, Username = "sender@example.test" },
            "secret",
            message,
            message.From.Mailboxes.Single(),
            message.To.Mailboxes.ToArray()));

        Assert.Equal(MailSendFailureKind.ConnectionFailed, exception.FailureKind);
        Assert.Equal(SmtpSubmissionStage.Connection, exception.SmtpFailure!.Stage);
        Assert.Equal("connection-or-tls-failure", exception.SmtpFailure.SanitizedReasonCategory);
        Assert.DoesNotContain("send", session.Events);
        Assert.Equal(["connect", "dispose"], session.Events);
    }

    [Fact]
    public async Task MailKitSubmission_CommandRejectionDuringConnectIsNotMisreportedAsMessageRejection()
    {
        RecordingSmtpSession session = new()
        {
            ConnectFailure = new MailKit.Net.Smtp.SmtpCommandException(
                MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode,
                MailKit.Net.Smtp.SmtpStatusCode.ServiceNotAvailable,
                "4.3.2 unavailable")
        };
        MailKitSmtpSubmissionClient client = new(new RecordingSmtpSessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        MailSubmissionException exception = await Assert.ThrowsAsync<MailSubmissionException>(() => client.SendAsync(
            new MailServerSettings { Host = "smtp.example.test", Port = 465, Username = "sender@example.test" },
            "secret",
            message,
            message.From.Mailboxes.Single(),
            message.To.Mailboxes.ToArray()));

        Assert.Equal(MailSendFailureKind.ConnectionFailed, exception.FailureKind);
        Assert.Equal(SmtpSubmissionStage.Connection, exception.SmtpFailure!.Stage);
        Assert.Equal(421, exception.SmtpFailure.StatusCode);
        Assert.Equal("4.3.2", exception.SmtpFailure.EnhancedStatusCode);
        Assert.DoesNotContain("send", session.Events);
    }

    [Fact]
    public async Task ImapSentCopy_UsesOnlyServerDeclaredSentWithSeenAndShortSession()
    {
        RecordingImapSentCopySession session = new();
        MailKitImapSentCopyClient client = new(new RecordingImapSentCopySessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        ImapSentCopyResult result = await client.AppendAsync(
            new MailServerSettings
            {
                Host = "imap.example.test",
                Port = 993,
                Username = "sender@example.test",
                SecureSocketMode = MailSecureSocketMode.SslOnConnect
            },
            "secret",
            message,
            MailKit.MessageFlags.Seen);

        Assert.True(result.IsSaved);
        Assert.Equal(MailSentCopyFailureKind.None, result.FailureKind);
        Assert.Equal(MailKit.SpecialFolder.Sent, session.SpecialFolder);
        Assert.Same(message, session.Message);
        Assert.Equal(MailKit.MessageFlags.Seen, session.Flags);
        Assert.Equal(["connect", "authenticate", "append", "disconnect", "dispose"], session.Events);
    }

    [Fact]
    public async Task ImapSentCopy_NoServerDeclaredSentReturnsUnavailableWithoutGuessing()
    {
        RecordingImapSentCopySession session = new() { AppendResult = false };
        MailKitImapSentCopyClient client = new(new RecordingImapSentCopySessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        ImapSentCopyResult result = await client.AppendAsync(
            new MailServerSettings { Host = "imap.example.test", Port = 993, Username = "sender@example.test" },
            "secret",
            message,
            MailKit.MessageFlags.Seen);

        Assert.False(result.IsSaved);
        Assert.Equal(MailSentCopyFailureKind.FolderUnavailable, result.FailureKind);
        Assert.Equal(MailKit.SpecialFolder.Sent, session.SpecialFolder);
        Assert.Equal(1, session.AppendCount);
        Assert.Equal(["connect", "authenticate", "append", "disconnect", "dispose"], session.Events);
    }

    [Fact]
    public async Task ImapSentCopy_AmbiguousAppendIsNeverAutomaticallyRetried()
    {
        RecordingImapSentCopySession session = new() { AppendFailure = new IOException("transport lost") };
        MailKitImapSentCopyClient client = new(new RecordingImapSentCopySessionFactory(session));
        MimeMessage message = CreateMimeFactory().Create(
            Account(MailProviderType.Yandex),
            Request(Account(MailProviderType.Yandex))).Message;

        ImapSentCopyResult result = await client.AppendAsync(
            new MailServerSettings { Host = "imap.example.test", Port = 993, Username = "sender@example.test" },
            "secret",
            message,
            MailKit.MessageFlags.Seen);

        Assert.False(result.IsSaved);
        Assert.Equal(MailSentCopyFailureKind.Ambiguous, result.FailureKind);
        Assert.Equal(1, session.AppendCount);
        Assert.Equal(["connect", "authenticate", "append", "disconnect", "dispose"], session.Events);
    }

    [Fact]
    public async Task Compose_SuccessClosesClearsAndRaisesSentOnce()
    {
        RecordingSendProvider provider = new(MailSendResult.Success("provider-id"));
        using MailComposeViewModel compose = CreateCompose(provider);
        MailAccount account = Account(MailProviderType.Gmail);
        int sentCount = 0;
        compose.Sent += (_, args) =>
        {
            Assert.Equal(account.Id, args.AccountId);
            sentCount++;
        };
        OpenCompose(compose, account);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.False(compose.IsOpen);
        Assert.Null(compose.Draft);
        Assert.Equal(0, compose.DraftCount);
        Assert.Equal("Письмо отправлено", compose.StatusMessage);
        Assert.Equal(1, sentCount);
        Assert.Equal(1, provider.SendCount);
    }

    [Fact]
    public async Task Compose_SentButCopyNotSavedClosesDraftAndPreventsAccidentalResend()
    {
        RecordingSendProvider provider = new(MailSendResult.SentButCopyNotSaved(
            MailSentCopyFailureKind.Ambiguous));
        using MailComposeViewModel compose = CreateCompose(provider);
        MailAccount account = Account(MailProviderType.Yandex);
        MailSentEventArgs? sent = null;
        compose.Sent += (_, eventArgs) => sent = eventArgs;
        OpenCompose(compose, account);

        await compose.SendCommand.ExecuteAsync(null);
        await compose.SendCommand.ExecuteAsync(null);

        Assert.False(compose.IsOpen);
        Assert.Null(compose.Draft);
        Assert.Equal(0, compose.DraftCount);
        Assert.Contains("Письмо отправлено", compose.StatusMessage!, StringComparison.Ordinal);
        Assert.Contains("не удалось сохранить копию", compose.StatusMessage!, StringComparison.Ordinal);
        Assert.NotNull(sent);
        Assert.False(sent!.SentCopySaved);
        Assert.Equal(1, provider.SendCount);
    }

    [Fact]
    public async Task Compose_DefiniteFailurePreservesDraftForManualRetry()
    {
        RecordingSendProvider provider = new(MailSendResult.Failure(
            MailSendFailureKind.RecipientRejected,
            "Recipient rejected."));
        using MailComposeViewModel compose = CreateCompose(provider);
        OpenCompose(compose, Account(MailProviderType.Yandex));

        await compose.SendCommand.ExecuteAsync(null);

        Assert.True(compose.IsOpen);
        Assert.Equal("Body", compose.Draft!.TextBody);
        Assert.Equal("Recipient rejected.", compose.ErrorMessage);
        Assert.Equal(1, provider.SendCount);
    }

    [Fact]
    public async Task Compose_AmbiguousFailureDoesNotRetryAutomatically()
    {
        RecordingSendProvider provider = new(MailSendResult.Failure(
            MailSendFailureKind.Ambiguous,
            "Не удалось подтвердить отправку. Перед повторной отправкой проверьте папку «Отправленные»."));
        using MailComposeViewModel compose = CreateCompose(provider);
        OpenCompose(compose, Account(MailProviderType.Gmail));

        await compose.SendCommand.ExecuteAsync(null);

        Assert.True(compose.IsOpen);
        Assert.Contains("проверьте папку", compose.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, provider.SendCount);
    }

    [Fact]
    public async Task DoubleSend_IsSingleFlight()
    {
        DeferredSendProvider provider = new();
        using MailComposeViewModel compose = CreateCompose(provider);
        OpenCompose(compose, Account(MailProviderType.Gmail));

        Task first = compose.SendCommand.ExecuteAsync(null);
        await provider.Started.Task;
        Task second = compose.SendCommand.ExecuteAsync(null);
        provider.Completion.SetResult(MailSendResult.Success());
        await Task.WhenAll(first, second);

        Assert.Equal(1, provider.SendCount);
    }

    [Fact]
    public async Task EmptySubjectAndBody_RequiresConfirmation()
    {
        RecordingSendProvider provider = new(MailSendResult.Success());
        RecordingConfirmationService confirmation = new() { EmptyMessageResult = false };
        using MailComposeViewModel compose = CreateCompose(provider, confirmation);
        compose.ActivateAccount(Account(MailProviderType.Gmail));
        compose.NewMessageCommand.Execute(null);
        compose.Draft!.To = "to@example.test";

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.EmptyMessageCount);
        Assert.Equal(0, provider.SendCount);
        Assert.True(compose.IsOpen);
    }

    [Fact]
    public async Task NonEmptyDiscard_RequiresConfirmation()
    {
        RecordingConfirmationService confirmation = new() { DiscardResult = false };
        using MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()), confirmation);
        OpenCompose(compose, Account(MailProviderType.Gmail));

        await compose.CancelCommand.ExecuteAsync(null);
        Assert.True(compose.IsOpen);
        confirmation.DiscardResult = true;
        await compose.CancelCommand.ExecuteAsync(null);

        Assert.False(compose.IsOpen);
        Assert.Equal(2, confirmation.DiscardCount);
    }

    [Fact]
    public void ComposeState_IsMemoryOnlyPerAccountAndSurvivesMailWebMail()
    {
        using MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()));
        MailAccount gmail = Account(MailProviderType.Gmail, 1);
        MailAccount yandex = Account(MailProviderType.Yandex, 2);
        OpenCompose(compose, gmail);
        compose.Draft!.TextBody = "Gmail RAM draft";

        compose.ActivateAccount(null);
        compose.ActivateAccount(yandex);
        compose.NewMessageCommand.Execute(null);
        compose.Draft!.TextBody = "Yandex RAM draft";
        compose.ActivateAccount(gmail);

        Assert.Equal("Gmail RAM draft", compose.Draft!.TextBody);
        Assert.Equal(2, compose.DraftCount);
        Assert.DoesNotContain("Compose", JsonSerializer.Serialize(AppSettings.CreateDefault()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DraftBody", JsonSerializer.Serialize(AppSettings.CreateDefault()), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.Yandex)]
    public void OpeningCompose_NotifiesCanEditAndEnablesTheSharedWpfSurface(MailProviderType providerType)
    {
        using MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()));
        List<string?> changedProperties = [];
        compose.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        compose.ActivateAccount(Account(providerType));
        compose.NewMessageCommand.Execute(null);

        Assert.True(compose.IsOpen);
        Assert.True(compose.CanEdit);
        Assert.Contains(nameof(MailComposeViewModel.CanEdit), changedProperties);
    }

    [Theory]
    [InlineData(MailProviderType.Gmail)]
    [InlineData(MailProviderType.Yandex)]
    public async Task ComposeClose_RetainsCachedMessageWithoutProviderRefetch(MailProviderType providerType)
    {
        SingleMessageReadProvider readProvider = new();
        using MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()));
        using MailInboxViewModel inbox = new(new FixedReadProviderFactory(readProvider), compose);
        MailAccount account = Account(providerType);
        await inbox.ActivateAsync(account);
        inbox.SelectedMessageSummary = Assert.Single(inbox.Messages);
        await inbox.CurrentMessageLoadTask;
        MailMessageContent content = Assert.IsType<MailMessageContent>(inbox.SelectedMessageContent);

        compose.NewMessageCommand.Execute(null);
        Assert.Same(content, inbox.SelectedMessageContent);
        await compose.CancelCommand.ExecuteAsync(null);

        Assert.False(compose.IsOpen);
        Assert.Same(content, inbox.SelectedMessageContent);
        Assert.Equal(1, readProvider.GetMessageCount);
    }

    [Fact]
    public void ApplicationSessionReset_ClearsComposeState()
    {
        MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()));
        OpenCompose(compose, Account(MailProviderType.Gmail));

        compose.Dispose();

        Assert.Equal(0, compose.DraftCount);
        Assert.Null(compose.Draft);
    }

    [Fact]
    public void Reply_UsesValidReplyToAndNormalizesSubject()
    {
        MailMessageContent source = Content(
            replyMetadata: new MailReplyMetadata(
                "Support <reply@example.test>",
                "message@example.test",
                []));

        MailComposeTemplate reply = new MailComposePreparationService().CreateReply(source);

        Assert.Contains("reply@example.test", reply.To, StringComparison.Ordinal);
        Assert.Equal("Re: Subject", reply.Subject);
    }

    [Fact]
    public void Reply_InvalidReplyToFallsBackToFrom()
    {
        MailMessageContent source = Content(
            replyMetadata: new MailReplyMetadata("invalid", "message@example.test", []));

        MailComposeTemplate reply = new MailComposePreparationService().CreateReply(source);

        Assert.Contains("sender@example.test", reply.To, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Subject", "Re: Subject")]
    [InlineData("Re: Subject", "Re: Subject")]
    [InlineData("RE: Re: Subject", "Re: Subject")]
    [InlineData("Ответ: Subject", "Re: Subject")]
    public void ReplySubject_DoesNotDuplicatePrefix(string input, string expected) =>
        Assert.Equal(expected, MailComposePreparationService.NormalizeReplySubject(input));

    [Fact]
    public void Reply_ExtendsReferencesAndCarriesGmailThreadWithoutInventing()
    {
        MailComposePreparationService service = new();
        MailComposeTemplate threaded = service.CreateReply(Content(
            replyMetadata: new MailReplyMetadata(
                string.Empty,
                "current@example.test",
                ["older@example.test"])
            {
                ProviderThreadId = "thread-id"
            }));
        MailComposeTemplate unthreaded = service.CreateReply(Content(replyMetadata: null));

        Assert.Equal("current@example.test", threaded.ReplyContext!.InReplyTo);
        Assert.Equal("thread-id", threaded.ReplyContext.ProviderThreadId);
        Assert.Equal(["older@example.test", "current@example.test"], threaded.ReplyContext.References);
        Assert.Null(unthreaded.ReplyContext);
    }

    [Fact]
    public void GmailReply_MissingMessageIdDoesNotInventOrForceThreadIdentity()
    {
        MailComposeTemplate reply = new MailComposePreparationService().CreateReply(Content(
            replyMetadata: new MailReplyMetadata(
                string.Empty,
                null,
                [])
            {
                ProviderThreadId = "provider-thread-without-rfc-id"
            }));

        Assert.Null(reply.ReplyContext);
    }

    [Fact]
    public void Reply_QuotesOnlySafePlainTextAndDoesNotMutateSource()
    {
        MailMessageContent source = Content(
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<p>Visible <b>HTML</b></p>",
            safePlain: "Visible HTML");

        MailComposeTemplate reply = new MailComposePreparationService().CreateReply(source);

        Assert.Contains("> Visible HTML", reply.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", reply.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("<p>Visible <b>HTML</b></p>", source.BodyContent);
        Assert.True(source.IsUnread);
    }

    [Theory]
    [InlineData("Subject", "Fwd: Subject")]
    [InlineData("Fwd: Subject", "Fwd: Subject")]
    [InlineData("FW: Fwd: Subject", "Fwd: Subject")]
    [InlineData("Пересл.: Subject", "Fwd: Subject")]
    public void ForwardSubject_DoesNotDuplicatePrefix(string input, string expected) =>
        Assert.Equal(expected, MailComposePreparationService.NormalizeForwardSubject(input));

    [Fact]
    public void Forward_StartsWithoutRecipientsAndUsesSafeTextualMetadata()
    {
        MailMessageContent source = Content(
            bodyKind: MailMessageBodyKind.SanitizedHtml,
            body: "<p>HTML source</p>",
            safePlain: "Plain source",
            hasAttachments: true);

        MailComposeTemplate forward = new MailComposePreparationService().CreateForward(source);

        Assert.Empty(forward.To);
        Assert.Empty(forward.Cc);
        Assert.Empty(forward.Bcc);
        Assert.Contains("Отправитель —", forward.TextBody, StringComparison.Ordinal);
        Assert.Contains("Дата отправки —", forward.TextBody, StringComparison.Ordinal);
        Assert.Contains("Тема сообщения —", forward.TextBody, StringComparison.Ordinal);
        Assert.Contains("Получатель —", forward.TextBody, StringComparison.Ordinal);
        Assert.Contains("> Plain source", forward.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\nОт:", forward.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\nДата:", forward.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\nТема:", forward.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\nКому:", forward.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", forward.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attachment", forward.TextBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forward_IsIndependentNewTextPlainMessageWithoutThreadMetadata()
    {
        MailAccount account = Account(MailProviderType.Yandex);
        MailMessageContent source = Content(
            replyMetadata: new MailReplyMetadata(
                "reply@example.test",
                "source-message@example.test",
                ["older@example.test"])
            {
                ProviderThreadId = "source-thread"
            },
            body: "Привет",
            safePlain: "Привет");
        MailComposeTemplate template = new MailComposePreparationService().CreateForward(source);
        MailComposeRequest request = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "recipient@example.test",
                template.Cc,
                template.Bcc,
                template.Subject,
                template.TextBody,
                template.ReplyContext));
        MimeMessage forward = CreateMimeFactory().Create(account, request).Message;
        TextPart body = Assert.IsType<TextPart>(forward.Body);

        Assert.Null(template.ReplyContext);
        Assert.Null(request.ReplyContext);
        Assert.Null(forward.InReplyTo);
        Assert.Empty(forward.References);
        Assert.NotEqual("source-message@example.test", forward.MessageId);
        Assert.Equal(FixedNow, forward.Date);
        Assert.Equal("sender@example.test", forward.From.Mailboxes.Single().Address);
        Assert.Equal("recipient@example.test", forward.To.Mailboxes.Single().Address);
        Assert.Equal("plain", body.ContentType.MediaSubtype);
        Assert.Equal("utf-8", body.ContentType.Charset, ignoreCase: true);
        Assert.Equal(ContentEncoding.QuotedPrintable, body.ContentTransferEncoding);
        Assert.Single(forward.BodyParts);
        Assert.DoesNotContain('\r', request.TextBody);
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(
            Serialize(forward),
            @"(?<!\r)\n",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(
            body.Text,
            @"(?im)^(?:from|to|cc|bcc|date|subject|message-id|references|in-reply-to|return-path|received|authentication-results|dkim-signature):",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        Assert.DoesNotContain(
            forward.Headers,
            header => header.Field.StartsWith("X-GM-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("source-message@example.test", body.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("source-thread", body.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Forward_MimeTopologyMatchesNormalComposeExceptSubjectAndBody()
    {
        MailAccount account = Account(MailProviderType.Yandex);
        MailComposeRequest normalRequest = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "recipient@example.test",
                string.Empty,
                string.Empty,
                "Subject",
                "Привет"));
        MailComposeTemplate forwardTemplate = new MailComposePreparationService().CreateForward(Content(
            body: "Привет",
            safePlain: "Привет"));
        MailComposeRequest forwardRequest = CreateRequestFactory().Create(
            account,
            new MailComposeInput(
                "recipient@example.test",
                string.Empty,
                string.Empty,
                forwardTemplate.Subject,
                forwardTemplate.TextBody,
                forwardTemplate.ReplyContext));

        MimeMessage normal = CreateMimeFactory().Create(account, normalRequest).Message;
        MimeMessage forward = CreateMimeFactory().Create(account, forwardRequest).Message;
        TextPart normalBody = Assert.IsType<TextPart>(normal.Body);
        TextPart forwardBody = Assert.IsType<TextPart>(forward.Body);

        Assert.Equal(
            normal.Headers.Select(header => header.Field).Order(StringComparer.OrdinalIgnoreCase),
            forward.Headers.Select(header => header.Field).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(normal.From.ToString(), forward.From.ToString());
        Assert.Equal(normal.To.ToString(), forward.To.ToString());
        Assert.Equal(normal.Cc.ToString(), forward.Cc.ToString());
        Assert.Equal(normal.Bcc.ToString(), forward.Bcc.ToString());
        Assert.Equal(normal.Date, forward.Date);
        Assert.Equal(normalBody.ContentType.MimeType, forwardBody.ContentType.MimeType);
        Assert.Equal(normalBody.ContentType.Charset, forwardBody.ContentType.Charset, ignoreCase: true);
        Assert.Equal(normalBody.ContentTransferEncoding, forwardBody.ContentTransferEncoding);
        Assert.Equal(normal.BodyParts.Count(), forward.BodyParts.Count());
        Assert.NotEqual(normal.Subject, forward.Subject);
        Assert.NotEqual(normal.TextBody, forward.TextBody);
    }

    [Fact]
    public void Forward_DoesNotCopyTechnicalSourceHeadersOrRawMime()
    {
        MimeMessage sourceMime = new()
        {
            Subject = "Source",
            Date = FixedNow.AddDays(-1),
            MessageId = "source-message@example.test",
            Body = new TextPart("plain") { Text = "Привет" }
        };
        sourceMime.From.Add(new MailboxAddress("Source", "source@example.test"));
        sourceMime.To.Add(new MailboxAddress("Recipient", "recipient@example.test"));
        sourceMime.Headers.Add(HeaderId.ReturnPath, "<bounce@example.test>");
        sourceMime.Headers.Add(HeaderId.Received, "from relay.example.test by mx.example.test");
        sourceMime.Headers.Add(HeaderId.AuthenticationResults, "mx.example.test; spf=pass");
        sourceMime.Headers.Add(HeaderId.DkimSignature, "v=1; d=example.test; b=fake");
        MailMessageContent source = new MailContentExtractor(new MailHtmlSanitizer())
            .Extract("message-key", sourceMime, isUnread: false);

        MailComposeTemplate forward = new MailComposePreparationService().CreateForward(source);

        Assert.DoesNotContain("Return-Path", forward.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Received:", forward.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authentication-Results", forward.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DKIM-Signature", forward.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source-message@example.test", forward.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Type:", forward.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MIME-Version:", forward.TextBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContentExtractor_CapturesReplyMetadataAndSafeHtmlTextWithoutNetwork()
    {
        MimeMessage message = new()
        {
            Subject = "Subject",
            Date = FixedNow,
            MessageId = "message@example.test",
            Body = new TextPart("html") { Text = "<p>Hello <b>world</b></p><script>secret()</script>" }
        };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.test"));
        message.ReplyTo.Add(new MailboxAddress("Reply", "reply@example.test"));
        message.References.Add("older@example.test");
        MailContentExtractor extractor = new(new MailHtmlSanitizer());

        MailMessageContent content = extractor.Extract("message-key", message, isUnread: true);

        Assert.Equal("Hello world", content.SafePlainTextContent);
        Assert.DoesNotContain("secret", content.SafePlainTextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reply@example.test", content.ReplyMetadata!.ReplyTo, StringComparison.Ordinal);
        Assert.Equal("message@example.test", content.ReplyMetadata.MessageId);
        Assert.Contains("older@example.test", content.ReplyMetadata.References);
    }

    [Fact]
    public async Task SuccessfulSend_MarksOnlySentFolderStateStale()
    {
        RecordingSendProvider sendProvider = new(MailSendResult.Success());
        using MailComposeViewModel compose = CreateCompose(sendProvider);
        FolderReadProvider readProvider = new();
        using MailInboxViewModel inbox = new(new FixedReadProviderFactory(readProvider), compose);
        MailAccount account = Account(MailProviderType.Gmail);
        await inbox.ActivateAsync(account);
        inbox.SelectedFolder = inbox.Folders.Single(folder => folder.Kind is MailFolderKind.Sent);
        await inbox.CurrentFolderLoadTask;
        Assert.False(inbox.IsFolderStateStale(account.Id, MailFolderKind.Sent));
        OpenCompose(compose, account);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.True(inbox.IsFolderStateStale(account.Id, MailFolderKind.Sent));
        Assert.Equal(0, readProvider.MutationCount);
        Assert.Equal(MailInboxViewModel.MessageBodyCacheCapacity, 20);
    }

    [Fact]
    public async Task PartialSuccess_DoesNotClaimSentFolderStateChanged()
    {
        RecordingSendProvider sendProvider = new(MailSendResult.SentButCopyNotSaved(
            MailSentCopyFailureKind.FolderUnavailable));
        using MailComposeViewModel compose = CreateCompose(sendProvider);
        FolderReadProvider readProvider = new();
        using MailInboxViewModel inbox = new(new FixedReadProviderFactory(readProvider), compose);
        MailAccount account = Account(MailProviderType.Yandex);
        await inbox.ActivateAsync(account);
        inbox.SelectedFolder = inbox.Folders.Single(folder => folder.Kind is MailFolderKind.Sent);
        await inbox.CurrentFolderLoadTask;
        Assert.False(inbox.IsFolderStateStale(account.Id, MailFolderKind.Sent));
        OpenCompose(compose, account);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.False(inbox.IsFolderStateStale(account.Id, MailFolderKind.Sent));
        Assert.False(compose.IsOpen);
        Assert.Contains("не удалось сохранить копию", compose.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeUi_IsNativeWpfAndContainsRequiredActions()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src",
            "UnifiedMessenger.App",
            "Views",
            "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        string[] buttonContents = view.Descendants(presentation + "Button")
            .Select(element => (string?)element.Attribute("Content") ?? string.Empty)
            .ToArray();

        Assert.Contains("Написать", buttonContents);
        Assert.Contains("Ответить", buttonContents);
        Assert.Contains("Переслать", buttonContents);
        Assert.Contains("{Binding Compose.SendButtonText}", buttonContents);
        Assert.DoesNotContain(view.Descendants(), element => element.Name.LocalName.Contains("WebView", StringComparison.Ordinal));
        Assert.Contains(
            view.Descendants(presentation + "TextBox"),
            element => string.Equals(
                (string?)element.Attribute("Text"),
                "{Binding Compose.FromAddress, Mode=OneWay}",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ComposeUi_DefinesFocusableToSubjectAndBodyWithDeferredInitialFocus()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src",
            "UnifiedMessenger.App",
            "Views",
            "MailInboxView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement composeSurface = Assert.Single(
            view.Descendants(presentation + "Grid"),
            element => (string?)element.Attribute(xaml + "Name") == "ComposeSurface");
        XElement to = Assert.Single(
            view.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute(xaml + "Name") == "ComposeToTextBox");
        XElement subject = Assert.Single(
            view.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute(xaml + "Name") == "ComposeSubjectTextBox");
        XElement body = Assert.Single(
            view.Descendants(presentation + "TextBox"),
            element => (string?)element.Attribute(xaml + "Name") == "ComposeBodyTextBox");

        Assert.Equal("ComposeSurface_IsVisibleChanged", (string?)composeSurface.Attribute("IsVisibleChanged"));
        Assert.Equal("1", (string?)to.Attribute("TabIndex"));
        Assert.Equal("4", (string?)subject.Attribute("TabIndex"));
        Assert.Equal("5", (string?)body.Attribute("TabIndex"));
        Assert.NotEqual("True", (string?)to.Attribute("IsReadOnly"));
        Assert.NotEqual("True", (string?)subject.Attribute("IsReadOnly"));
        Assert.NotEqual("True", (string?)body.Attribute("IsReadOnly"));
    }

    [Fact]
    public void Stage75_DoesNotChangeSettingsSchemaOrPersistMailContent()
    {
        Assert.Equal(4, AppSettings.CurrentSchemaVersion);
        string settings = JsonSerializer.Serialize(AppSettings.CreateDefault());
        string[] forbidden = ["Compose", "Recipient", "Subject", "TextBody", "ReplyContext", "ThreadId", "RawMime"];

        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, settings, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SendPipeline_HasNoLoggerOrProtocolTranscriptDependency()
    {
        Type[] types =
        [
            typeof(MailComposeViewModel),
            typeof(GmailMailSendProvider),
            typeof(SmtpMailSendProvider),
            typeof(GmailApiSendClient),
            typeof(MailKitSmtpSubmissionClient),
            typeof(MailKitImapSentCopyClient),
            typeof(MailKitImapSentCopySession)
        ];

        foreach (Type type in types)
        {
            Assert.DoesNotContain(
                type.GetConstructors(
                        System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType.Name.Contains("Logger", StringComparison.OrdinalIgnoreCase)
                    || parameter.ParameterType.Name.Contains("ProtocolLogger", StringComparison.OrdinalIgnoreCase)
                    || parameter.ParameterType.Name.Contains("Settings", StringComparison.OrdinalIgnoreCase)
                    && type == typeof(MailComposeViewModel));
        }
    }

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static MailComposeRequestFactory CreateRequestFactory() => new();
    private static MailMimeMessageFactory CreateMimeFactory() => new(new FixedTimeProvider(FixedNow));

    private static MailAccount Account(MailProviderType provider, int suffix = 1) => new()
    {
        Id = Guid.Parse($"{suffix:D8}-1111-1111-1111-111111111111"),
        Provider = provider,
        EmailAddress = "sender@example.test",
        DisplayName = "Test Sender",
        CredentialKey = $"{suffix:D32}",
        AuthenticationKind = provider is MailProviderType.Gmail
            ? MailAuthenticationKind.OAuth
            : MailAuthenticationKind.Password,
        IsEnabled = true
    };

    private static MailComposeRequest Request(MailAccount account) =>
        CreateRequestFactory().Create(
            account,
            new MailComposeInput("to@example.test", string.Empty, string.Empty, "Subject", "Body"));

    private static MailMessageContent Content(
        MailReplyMetadata? replyMetadata = null,
        MailMessageBodyKind bodyKind = MailMessageBodyKind.PlainText,
        string body = "Original body",
        string safePlain = "Original body",
        bool hasAttachments = false) =>
        new(
            "message-key",
            "Subject",
            "Sender",
            "sender@example.test",
            "recipient@example.test",
            FixedNow,
            bodyKind,
            body,
            [],
            true,
            hasAttachments,
            safePlain,
            replyMetadata);

    private static MailCredential ModifyCredential() =>
        MailCredential.CreateGmailOAuth(
            "refresh-token",
            "client-id",
            "client-secret",
            GmailOAuthConstants.ModifyScope);

    private static MailCredential ReadOnlyCredential() =>
        MailCredential.CreateGmailOAuth("refresh-token", "client-id", "client-secret");

    private static SmtpMailSendProvider CreateSmtpProvider(
        ISmtpSubmissionClient smtp,
        FixedCredentialStore? store = null,
        IImapSentCopyClient? sentCopy = null)
    {
        NoOpConnectionValidator validator = new();
        MailProviderFactory providerFactory = new(
        [
            new GmailApiProvider(),
            new YandexMailProvider(validator),
            new MailRuMailProvider(validator),
            new GenericImapMailProvider(validator)
        ]);
        return new SmtpMailSendProvider(
            store ?? new FixedCredentialStore(MailCredential.CreatePassword("app-password")),
            providerFactory,
            smtp,
            sentCopy ?? new RecordingImapSentCopyClient(),
            CreateMimeFactory());
    }

    private static MailComposeViewModel CreateCompose(
        IMailSendProvider provider,
        RecordingConfirmationService? confirmation = null) =>
        new(
            new FixedSendProviderFactory(provider),
            CreateRequestFactory(),
            new MailComposePreparationService(),
            confirmation ?? new RecordingConfirmationService());

    private static void OpenCompose(MailComposeViewModel compose, MailAccount account)
    {
        compose.ActivateAccount(account);
        compose.NewMessageCommand.Execute(null);
        compose.Draft!.To = "to@example.test";
        compose.Draft.Subject = "Subject";
        compose.Draft.TextBody = "Body";
    }

    private static string Serialize(MimeMessage message)
    {
        using MemoryStream stream = new();
        message.WriteTo(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Repository file was not found.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedCredentialStore(MailCredential? credential) : IMailCredentialStore
    {
        public int LoadCount { get; private set; }
        public string? LastLoadedKey { get; private set; }

        public Task SaveAsync(string credentialKey, MailCredential value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LastLoadedKey = credentialKey;
            return Task.FromResult(credential);
        }

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingGmailApiClient : IGmailApiSendClient
    {
        public int SendCount { get; private set; }
        public byte[]? RawMime { get; private set; }
        public string? ThreadId { get; private set; }

        public Task<GmailApiSendReceipt> SendAsync(
            MailCredential credential,
            Guid accountId,
            byte[] rawMime,
            string? threadId,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            RawMime = rawMime;
            ThreadId = threadId;
            return Task.FromResult(new GmailApiSendReceipt("sent-id", threadId));
        }
    }

    private sealed class RecordingSmtpSubmissionClient : ISmtpSubmissionClient
    {
        public int SendCount { get; private set; }
        public MailServerSettings? Server { get; private set; }
        public string? Secret { get; private set; }
        public MimeMessage? Message { get; private set; }
        public IReadOnlyList<MailboxAddress>? EnvelopeRecipients { get; private set; }
        public MailSubmissionException? Failure { get; init; }

        public Task SendAsync(
            MailServerSettings server,
            string secret,
            MimeMessage message,
            MailboxAddress envelopeSender,
            IReadOnlyList<MailboxAddress> envelopeRecipients,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            Server = server;
            Secret = secret;
            Message = message;
            EnvelopeRecipients = envelopeRecipients;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class DeferredSmtpSubmissionClient : ISmtpSubmissionClient
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }

        public async Task SendAsync(
            MailServerSettings server,
            string secret,
            MimeMessage message,
            MailboxAddress envelopeSender,
            IReadOnlyList<MailboxAddress> envelopeRecipients,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            Started.TrySetResult(true);
            await Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingImapSentCopyClient : IImapSentCopyClient
    {
        public int AppendCount { get; private set; }
        public MailServerSettings? Server { get; private set; }
        public string? Secret { get; private set; }
        public MimeMessage? Message { get; private set; }
        public MailKit.MessageFlags Flags { get; private set; }
        public ImapSentCopyResult Result { get; init; } = ImapSentCopyResult.Saved;
        public Exception? Failure { get; init; }

        public Task<ImapSentCopyResult> AppendAsync(
            MailServerSettings server,
            string secret,
            MimeMessage message,
            MailKit.MessageFlags flags,
            CancellationToken cancellationToken = default)
        {
            AppendCount++;
            Server = server;
            Secret = secret;
            Message = message;
            Flags = flags;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<ImapSentCopyResult>(Failure);
        }
    }

    private sealed class RecordingSmtpSessionFactory(RecordingSmtpSession session) : ISmtpClientSessionFactory
    {
        public ISmtpClientSession Create() => session;
    }

    private sealed class RecordingImapSentCopySessionFactory(RecordingImapSentCopySession session)
        : IImapSentCopySessionFactory
    {
        public IImapSentCopySession Create() => session;
    }

    private sealed class RecordingImapSentCopySession : IImapSentCopySession
    {
        public List<string> Events { get; } = [];
        public bool IsConnected { get; private set; }
        public bool AppendResult { get; init; } = true;
        public Exception? AppendFailure { get; init; }
        public int AppendCount { get; private set; }
        public MailKit.SpecialFolder? SpecialFolder { get; private set; }
        public MimeMessage? Message { get; private set; }
        public MailKit.MessageFlags Flags { get; private set; }

        public Task ConnectAsync(
            string host,
            int port,
            MailSecureSocketMode secureSocketMode,
            CancellationToken cancellationToken)
        {
            Events.Add("connect");
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string username, string secret, CancellationToken cancellationToken)
        {
            Events.Add("authenticate");
            return Task.CompletedTask;
        }

        public Task<bool> AppendToSpecialFolderAsync(
            MailKit.SpecialFolder specialFolder,
            MimeMessage message,
            MailKit.MessageFlags flags,
            CancellationToken cancellationToken)
        {
            Events.Add("append");
            AppendCount++;
            SpecialFolder = specialFolder;
            Message = message;
            Flags = flags;
            return AppendFailure is null
                ? Task.FromResult(AppendResult)
                : Task.FromException<bool>(AppendFailure);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Events.Add("disconnect");
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Dispose() => Events.Add("dispose");
    }

    private sealed class RecordingSmtpSession : ISmtpClientSession
    {
        public List<string> Events { get; } = [];
        public Exception? ConnectFailure { get; init; }
        public Exception? AuthenticationFailure { get; init; }
        public Exception? SendFailure { get; init; }
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(
            string host,
            int port,
            MailSecureSocketMode secureSocketMode,
            CancellationToken cancellationToken)
        {
            Events.Add("connect");
            if (ConnectFailure is not null)
            {
                return Task.FromException(ConnectFailure);
            }

            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string username, string secret, CancellationToken cancellationToken)
        {
            Events.Add("authenticate");
            return AuthenticationFailure is null
                ? Task.CompletedTask
                : Task.FromException(AuthenticationFailure);
        }

        public Task SendAsync(
            MimeMessage message,
            MailboxAddress envelopeSender,
            IReadOnlyList<MailboxAddress> envelopeRecipients,
            CancellationToken cancellationToken)
        {
            Events.Add("send");
            return SendFailure is null ? Task.CompletedTask : Task.FromException(SendFailure);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Events.Add("disconnect");
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Dispose() => Events.Add("dispose");
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

    private sealed class FixedSendProviderFactory(IMailSendProvider provider) : IMailSendProviderFactory
    {
        public IMailSendProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class RecordingSendProvider(MailSendResult result) : IMailSendProvider
    {
        public int SendCount { get; private set; }
        public bool Supports(MailProviderType providerType) => true;

        public Task<MailSendResult> SendAsync(
            MailAccount account,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class DeferredSendProvider : IMailSendProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MailSendResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }
        public bool Supports(MailProviderType providerType) => true;

        public Task<MailSendResult> SendAsync(
            MailAccount account,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            Started.TrySetResult(true);
            return Completion.Task;
        }
    }

    private sealed class RecordingConfirmationService : IMailComposeConfirmationService
    {
        public bool EmptyMessageResult { get; set; } = true;
        public bool DiscardResult { get; set; } = true;
        public int EmptyMessageCount { get; private set; }
        public int DiscardCount { get; private set; }

        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default)
        {
            EmptyMessageCount++;
            return Task.FromResult(EmptyMessageResult);
        }

        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default)
        {
            DiscardCount++;
            return Task.FromResult(DiscardResult);
        }
    }

    private sealed class FolderReadProvider : IMailReadProvider
    {
        public int MutationCount { get; private set; }
        public bool Supports(MailProviderType providerType) => true;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>(
            [
                MailFolderCatalog.Inbox(),
                MailFolderCatalog.Create(MailFolderKind.Sent, "SENT")
            ]);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>([], null));

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
            Task.FromResult(Content());
    }

    private sealed class SingleMessageReadProvider : IMailReadProvider
    {
        private readonly MailMessageSummary _summary = new(
            "message-key",
            "Subject",
            "Sender",
            "sender@example.test",
            FixedNow,
            "Preview",
            true);

        public int GetMessageCount { get; private set; }

        public bool Supports(MailProviderType providerType) => true;

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>([_summary], null));

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            GetMessageCount++;
            return Task.FromResult(Content());
        }
    }

    private sealed class FixedReadProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }
}
