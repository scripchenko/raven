using MailKit;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public enum MailSendFailureKind
{
    None,
    InvalidRequest,
    CredentialMissing,
    AuthenticationFailed,
    CapabilityUnavailable,
    ConnectionFailed,
    SenderRejected,
    RecipientRejected,
    MessageRejected,
    PolicyRejected,
    ProtocolRejected,
    AttachmentUnavailable,
    MessageTooLarge,
    Ambiguous,
    CanceledBeforeSubmission
}

public enum MailSendOutcome
{
    Failed,
    Sent,
    SentButCopyNotSaved
}

public enum MailSentCopyFailureKind
{
    None,
    ConfigurationUnavailable,
    FolderUnavailable,
    AuthenticationFailed,
    ConnectionFailed,
    AppendRejected,
    Ambiguous,
    Canceled,
    Unexpected
}

public sealed record MailAddress(string DisplayName, string Address);

public sealed record MailReplyContext(
    string? InReplyTo,
    IReadOnlyList<string> References)
{
    internal string? ProviderThreadId { get; init; }
}

public sealed record MailComposeRequest(
    Guid AccountId,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> Bcc,
    string Subject,
    string TextBody,
    MailReplyContext? ReplyContext = null)
{
    public int RecipientCount => To.Count + Cc.Count + Bcc.Count;
    public IReadOnlyList<OutgoingMailAttachment> Attachments { get; init; } = [];
}

public sealed record MailComposeInput(
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string TextBody,
    MailReplyContext? ReplyContext = null)
{
    public IReadOnlyList<OutgoingMailAttachment> Attachments { get; init; } = [];
}

public sealed record MailForwardAttachmentOffer(
    string MessageKey,
    MailAttachmentInfo Attachment);

public sealed record MailComposeTemplate(
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string TextBody,
    MailReplyContext? ReplyContext = null)
{
    public IReadOnlyList<MailForwardAttachmentOffer> ForwardAttachments { get; init; } = [];
}

public sealed record MailSendResult(
    MailSendOutcome Outcome,
    MailSendFailureKind FailureKind,
    MailSentCopyFailureKind SentCopyFailureKind,
    string UserMessage,
    string? ProviderMessageIdentity = null)
{
    public bool IsSuccess => Outcome is MailSendOutcome.Sent;
    public bool IsMessageSent => Outcome is not MailSendOutcome.Failed;
    public bool IsPartialSuccess => Outcome is MailSendOutcome.SentButCopyNotSaved;
    public bool SentCopySaved => Outcome is MailSendOutcome.Sent;

    public static MailSendResult Success(string? providerMessageIdentity = null) =>
        new(
            MailSendOutcome.Sent,
            MailSendFailureKind.None,
            MailSentCopyFailureKind.None,
            "Письмо отправлено",
            providerMessageIdentity);

    public static MailSendResult SentButCopyNotSaved(MailSentCopyFailureKind failureKind) =>
        new(
            MailSendOutcome.SentButCopyNotSaved,
            MailSendFailureKind.None,
            failureKind,
            "Письмо отправлено, но не удалось сохранить копию в папке «Отправленные».");

    public static MailSendResult Failure(MailSendFailureKind kind, string message) =>
        new(MailSendOutcome.Failed, kind, MailSentCopyFailureKind.None, message);
}

public sealed class MailComposeValidationException(string userMessage) : Exception(userMessage)
{
    public string UserMessage { get; } = userMessage;
}

public interface IMailComposeRequestFactory
{
    MailComposeRequest Create(MailAccount account, MailComposeInput input);
}

public interface IMailComposePreparationService
{
    MailComposeTemplate CreateReply(MailMessageContent source);
    MailComposeTemplate CreateForward(MailMessageContent source);
}

public interface IMailSendProvider
{
    bool Supports(MailProviderType providerType);

    Task<MailSendResult> SendAsync(
        MailAccount account,
        MailComposeRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMailSendProviderFactory
{
    IMailSendProvider Get(MailProviderType providerType);
}

public interface IMailComposeConfirmationService
{
    Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default);
    Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default);
}

internal sealed record MailMimeSubmission(
    MimeMessage Message,
    MailboxAddress EnvelopeSender,
    IReadOnlyList<MailboxAddress> EnvelopeRecipients);

internal interface IMailMimeMessageFactory
{
    MailMimeSubmission Create(
        MailAccount account,
        MailComposeRequest request,
        IReadOnlyList<MaterializedMailAttachment>? attachments = null);
}

internal sealed record GmailApiSendReceipt(string? MessageId, string? ThreadId);

internal interface IGmailApiSendClient
{
    Task<GmailApiSendReceipt> SendAsync(
        MailCredential credential,
        Guid accountId,
        byte[] rawMime,
        string? threadId,
        CancellationToken cancellationToken = default);
}

internal interface ISmtpSubmissionClient
{
    Task SendAsync(
        MailServerSettings server,
        string secret,
        MimeMessage message,
        MailboxAddress envelopeSender,
        IReadOnlyList<MailboxAddress> envelopeRecipients,
        CancellationToken cancellationToken = default);
}

internal sealed record ImapSentCopyResult(
    bool IsSaved,
    MailSentCopyFailureKind FailureKind)
{
    public static ImapSentCopyResult Saved { get; } = new(true, MailSentCopyFailureKind.None);
    public static ImapSentCopyResult Failed(MailSentCopyFailureKind failureKind) => new(false, failureKind);
}

internal interface IImapSentCopyClient
{
    Task<ImapSentCopyResult> AppendAsync(
        MailServerSettings server,
        string secret,
        MimeMessage message,
        MessageFlags flags,
        CancellationToken cancellationToken = default);
}

internal interface ISmtpClientSessionFactory
{
    ISmtpClientSession Create();
}

internal interface ISmtpClientSession : IDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(
        string host,
        int port,
        MailSecureSocketMode secureSocketMode,
        CancellationToken cancellationToken);

    Task AuthenticateAsync(string username, string secret, CancellationToken cancellationToken);

    Task SendAsync(
        MimeMessage message,
        MailboxAddress envelopeSender,
        IReadOnlyList<MailboxAddress> envelopeRecipients,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal enum SmtpSubmissionStage
{
    Connection,
    Authentication,
    MailFrom,
    RcptTo,
    DataAcceptance,
    SubmissionProtocol
}

internal sealed record SmtpFailureDetails(
    SmtpSubmissionStage Stage,
    string MailKitErrorCategory,
    int? StatusCode,
    string? EnhancedStatusCode,
    string SanitizedReasonCategory);

internal sealed class MailSubmissionException(
    MailSendFailureKind failureKind,
    string userMessage,
    Exception? innerException = null,
    SmtpFailureDetails? smtpFailure = null) : Exception(userMessage, innerException)
{
    public MailSendFailureKind FailureKind { get; } = failureKind;
    public string UserMessage { get; } = userMessage;
    public SmtpFailureDetails? SmtpFailure { get; } = smtpFailure;
}
