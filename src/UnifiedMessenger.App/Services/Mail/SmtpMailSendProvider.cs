using System.IO;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

internal sealed class SmtpMailSendProvider(
    IMailCredentialStore credentialStore,
    IMailProviderFactory providerFactory,
    ISmtpSubmissionClient smtpClient,
    IImapSentCopyClient sentCopyClient,
    IMailMimeMessageFactory mimeMessageFactory,
    IMailOutgoingAttachmentMaterializer? attachmentMaterializer = null) : IMailSendProvider
{
    public bool Supports(MailProviderType providerType) =>
        providerType is MailProviderType.Yandex or MailProviderType.MailRu or MailProviderType.GenericImap;

    public async Task<MailSendResult> SendAsync(
        MailAccount account,
        MailComposeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(request);
        if (!Supports(account.Provider) || account.Id != request.AccountId)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.InvalidRequest,
                L.Instance.Get("Invalid SMTP send settings."));
        }

        MailCredential? credential;
        try
        {
            credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.CanceledBeforeSubmission,
                L.Instance.Get("Sending was canceled before the message was transmitted."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.CredentialMissing,
                L.Instance.Get("Could not read the protected app password."));
        }

        if (credential is not { Kind: MailCredentialKind.Password } || !credential.IsValid())
        {
            return MailSendResult.Failure(
                MailSendFailureKind.CredentialMissing,
                L.Instance.Get("No saved app password was found for sending."));
        }

        try
        {
            MailConnectionSettings settings = ResolveConnectionSettings(account);
            MailServerSettings smtpServer = settings.Smtp;
            IReadOnlyList<MaterializedMailAttachment> attachments = request.Attachments.Count == 0
                ? []
                : attachmentMaterializer is not null
                    ? await attachmentMaterializer.MaterializeAsync(account, request.Attachments, cancellationToken)
                    : throw new MailAttachmentException(
                        MailAttachmentFailureKind.Unavailable,
                        L.Instance.Get("Attachments are unavailable for sending."));
            MailMimeSubmission submission = mimeMessageFactory.Create(account, request, attachments);
            submission.Message.Bcc.Clear();
            await smtpClient.SendAsync(
                smtpServer,
                credential.Secret,
                submission.Message,
                submission.EnvelopeSender,
                submission.EnvelopeRecipients,
                cancellationToken);

            if (!IsConfigured(settings.Imap))
            {
                return MailSendResult.SentButCopyNotSaved(
                    MailSentCopyFailureKind.ConfigurationUnavailable);
            }

            ImapSentCopyResult sentCopy;
            try
            {
                sentCopy = await sentCopyClient.AppendAsync(
                    settings.Imap,
                    credential.Secret,
                    submission.Message,
                    MessageFlags.Seen,
                    cancellationToken);
            }
            catch (Exception)
            {
                return MailSendResult.SentButCopyNotSaved(MailSentCopyFailureKind.Unexpected);
            }

            return sentCopy.IsSaved
                ? MailSendResult.Success()
                : MailSendResult.SentButCopyNotSaved(sentCopy.FailureKind);
        }
        catch (MailComposeValidationException exception)
        {
            return MailSendResult.Failure(MailSendFailureKind.InvalidRequest, exception.UserMessage);
        }
        catch (MailSubmissionException exception)
        {
            return MailSendResult.Failure(exception.FailureKind, exception.UserMessage);
        }
        catch (MailAttachmentException exception)
        {
            return MailSendResult.Failure(
                exception.FailureKind is MailAttachmentFailureKind.MessageTooLarge
                    ? MailSendFailureKind.MessageTooLarge
                    : MailSendFailureKind.AttachmentUnavailable,
                exception.UserMessage);
        }
    }

    private MailConnectionSettings ResolveConnectionSettings(MailAccount account)
    {
        if (providerFactory.Get(account.Provider) is not PasswordMailProvider provider)
        {
            throw new MailSubmissionException(
                MailSendFailureKind.CapabilityUnavailable,
                L.Instance.Get("SMTP sending is not configured for this account."));
        }

        MailConnectionSettings? settings = provider.CreateConnectionSettings(
            new MailAccountConnectionRequest(
                account.Provider,
                account.EmailAddress,
                account.DisplayName,
                account.GenericConnectionSettings),
            account.EmailAddress.Trim());
        if (settings is null || !IsConfigured(settings.Smtp))
        {
            throw new MailSubmissionException(
                MailSendFailureKind.CapabilityUnavailable,
                L.Instance.Get("SMTP settings are missing for this account."));
        }

        return settings;
    }

    private static bool IsConfigured(MailServerSettings server) =>
        !string.IsNullOrWhiteSpace(server.Host)
        && !string.IsNullOrWhiteSpace(server.Username)
        && server.Port is >= 1 and <= 65535;
}

internal sealed class MailKitSmtpSubmissionClient(
    ISmtpClientSessionFactory sessionFactory) : ISmtpSubmissionClient
{
    public async Task SendAsync(
        MailServerSettings server,
        string secret,
        MimeMessage message,
        MailboxAddress envelopeSender,
        IReadOnlyList<MailboxAddress> envelopeRecipients,
        CancellationToken cancellationToken = default)
    {
        using ISmtpClientSession client = sessionFactory.Create();
        bool submissionStarted = false;
        string username = server.Username;
        SmtpSubmissionStage stage = SmtpSubmissionStage.Connection;
        try
        {
            await client.ConnectAsync(
                server.Host,
                server.Port,
                server.SecureSocketMode,
                cancellationToken);
            stage = SmtpSubmissionStage.Authentication;
            await client.AuthenticateAsync(username, secret, cancellationToken);
            stage = SmtpSubmissionStage.MailFrom;
            submissionStarted = true;
            await client.SendAsync(
                message,
                envelopeSender,
                envelopeRecipients,
                cancellationToken);
        }
        catch (MailSubmissionException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            MailSubmissionException mapped = submissionStarted
                ? Ambiguous(exception)
                : new MailSubmissionException(
                    MailSendFailureKind.CanceledBeforeSubmission,
                    L.Instance.Get("Sending was canceled before the message was transmitted."),
                    exception);
            throw mapped;
        }
        catch (MailKit.Security.AuthenticationException exception)
        {
            MailSubmissionException mapped = MapAuthenticationFailure(exception);
            throw mapped;
        }
        catch (SmtpCommandException exception)
        {
            MailSubmissionException mapped = MapCommandFailure(stage, exception);
            throw mapped;
        }
        catch (Exception exception) when (submissionStarted && IsTransportFailure(exception))
        {
            MailSubmissionException mapped = Ambiguous(exception);
            throw mapped;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            MailSubmissionException mapped = new(
                MailSendFailureKind.ConnectionFailed,
                L.Instance.Get("Could not connect to SMTP. Check your network and account settings."),
                exception,
                new SmtpFailureDetails(
                    stage,
                    exception.GetType().Name,
                    null,
                    null,
                    stage is SmtpSubmissionStage.Authentication
                        ? "authentication-transport-failure"
                        : "connection-or-tls-failure"));
            throw mapped;
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException
            or SmtpProtocolException
            or ServiceNotConnectedException
            or ServiceNotAuthenticatedException;

    private static MailSubmissionException MapAuthenticationFailure(
        MailKit.Security.AuthenticationException exception)
    {
        SmtpCommandException? commandException = FindSmtpCommandException(exception);
        return new MailSubmissionException(
            MailSendFailureKind.AuthenticationFailed,
            L.Instance.Get("SMTP rejected the credentials. Check your app password."),
            exception,
            new SmtpFailureDetails(
                SmtpSubmissionStage.Authentication,
                commandException?.ErrorCode.ToString() ?? nameof(MailKit.Security.AuthenticationException),
                commandException is null ? null : (int)commandException.StatusCode,
                TryExtractEnhancedStatusCode(commandException?.Message),
                "authentication-rejected"));
    }

    private static MailSubmissionException MapCommandFailure(
        SmtpSubmissionStage currentStage,
        SmtpCommandException exception)
    {
        string? enhancedStatusCode = TryExtractEnhancedStatusCode(exception.Message);
        (MailSendFailureKind kind, SmtpSubmissionStage stage, string userMessage, string reason) =
            currentStage switch
            {
                SmtpSubmissionStage.Connection => (
                    MailSendFailureKind.ConnectionFailed,
                    SmtpSubmissionStage.Connection,
                    L.Instance.Get("SMTP server rejected the connection."),
                    "connection-rejected"),
                SmtpSubmissionStage.Authentication => (
                    MailSendFailureKind.AuthenticationFailed,
                    SmtpSubmissionStage.Authentication,
                    L.Instance.Get("SMTP rejected the credentials. Check your app password."),
                    "authentication-rejected"),
                _ => exception.ErrorCode switch
                {
                    SmtpErrorCode.SenderNotAccepted => (
                        MailSendFailureKind.SenderRejected,
                        SmtpSubmissionStage.MailFrom,
                        L.Instance.Get("SMTP server rejected the sender address."),
                        "sender-rejected"),
                    SmtpErrorCode.RecipientNotAccepted => (
                        MailSendFailureKind.RecipientRejected,
                        SmtpSubmissionStage.RcptTo,
                        L.Instance.Get("SMTP server rejected one or more recipients."),
                        "recipient-rejected"),
                    SmtpErrorCode.MessageNotAccepted when IsSecurityOrPolicyRejection(enhancedStatusCode) => (
                        MailSendFailureKind.PolicyRejected,
                        SmtpSubmissionStage.DataAcceptance,
                        L.Instance.Get("SMTP server rejected the message under security or spam rules."),
                        "security-or-antispam-policy-rejected"),
                    SmtpErrorCode.MessageNotAccepted => (
                        MailSendFailureKind.MessageRejected,
                        SmtpSubmissionStage.DataAcceptance,
                        L.Instance.Get("SMTP server rejected the message."),
                        "message-or-policy-rejected"),
                    _ => (
                        MailSendFailureKind.ProtocolRejected,
                        SmtpSubmissionStage.SubmissionProtocol,
                        L.Instance.Get("SMTP server returned an unexpected response while sending."),
                        "unexpected-status")
                }
            };

        return new MailSubmissionException(
            kind,
            userMessage,
            exception,
            new SmtpFailureDetails(
                stage,
                exception.ErrorCode.ToString(),
                (int)exception.StatusCode,
                enhancedStatusCode,
                reason));
    }

    private static bool IsSecurityOrPolicyRejection(string? enhancedStatusCode) =>
        enhancedStatusCode?.StartsWith("5.7.", StringComparison.Ordinal) == true;

    private static SmtpCommandException? FindSmtpCommandException(Exception exception)
    {
        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SmtpCommandException commandException)
            {
                return commandException;
            }
        }

        return null;
    }

    private static string? TryExtractEnhancedStatusCode(string? message)
    {
        Match match = Regex.Match(
            message ?? string.Empty,
            @"(?<!\d)[245]\.\d{1,3}\.\d{1,3}(?!\d)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    private static MailSubmissionException Ambiguous(Exception exception) =>
        new(
            MailSendFailureKind.Ambiguous,
            L.Instance.Get("Could not confirm sending. Check Sent before sending again."),
            exception,
            new SmtpFailureDetails(
                SmtpSubmissionStage.DataAcceptance,
                exception.GetType().Name,
                null,
                null,
                "ambiguous-post-submission-transport"));

    private static async Task DisconnectQuietlyAsync(ISmtpClientSession client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            await client.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception exception) when (IsTransportFailure(exception) || exception is SmtpCommandException)
        {
            // Submission already has a provider-neutral result; disconnect details are not user-facing.
        }
    }
}

internal sealed class MailKitSmtpClientSessionFactory : ISmtpClientSessionFactory
{
    public ISmtpClientSession Create() => new MailKitSmtpClientSession();
}

internal sealed class MailKitSmtpClientSession : ISmtpClientSession
{
    private readonly SmtpClient _client = new();

    public bool IsConnected => _client.IsConnected;

    public Task ConnectAsync(
        string host,
        int port,
        MailSecureSocketMode secureSocketMode,
        CancellationToken cancellationToken) =>
        _client.ConnectAsync(
            host,
            port,
            secureSocketMode switch
            {
                MailSecureSocketMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
                MailSecureSocketMode.StartTls => SecureSocketOptions.StartTls,
                _ => throw new MailSubmissionException(
                    MailSendFailureKind.CapabilityUnavailable,
                    L.Instance.Get("Invalid secure SMTP connection settings."))
            },
            cancellationToken);

    public Task AuthenticateAsync(string username, string secret, CancellationToken cancellationToken) =>
        _client.AuthenticateAsync(username, secret, cancellationToken);

    public Task SendAsync(
        MimeMessage message,
        MailboxAddress envelopeSender,
        IReadOnlyList<MailboxAddress> envelopeRecipients,
        CancellationToken cancellationToken) =>
        _client.SendAsync(message, envelopeSender, envelopeRecipients, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        _client.DisconnectAsync(true, cancellationToken);

    public void Dispose() => _client.Dispose();
}
