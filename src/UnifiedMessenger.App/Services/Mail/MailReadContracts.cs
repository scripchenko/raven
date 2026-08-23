using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public enum MailReadFailureKind
{
    InvalidConfiguration,
    CredentialMissing,
    AuthenticationFailed,
    ReauthorizationRequired,
    ConnectionFailed,
    MessageUnavailable,
    InvalidMessage
}

public sealed class MailReadException : Exception
{
    public MailReadException(MailReadFailureKind failureKind, string userMessage)
        : base(userMessage)
    {
        FailureKind = failureKind;
        UserMessage = userMessage;
    }

    public MailReadFailureKind FailureKind { get; }
    public string UserMessage { get; }
}

public interface IMailReadProvider
{
    bool Supports(MailProviderType providerType);

    Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
        MailAccount account,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        string messageKey,
        CancellationToken cancellationToken = default);
}

public interface IMailReadProviderFactory
{
    IMailReadProvider Get(MailProviderType providerType);
}

public interface IMailContentExtractor
{
    MailMessageContent Extract(
        string messageKey,
        MimeMessage message,
        bool isUnread);
}

public interface IMailHtmlSanitizer
{
    MailHtmlSanitizationResult Sanitize(string html, MimeMessage message);
}

public interface IMailHtmlDocumentBuilder
{
    string Build(
        MailMessageContent content,
        IReadOnlyDictionary<string, MailImageContent>? remoteImages = null);
}


public sealed record MailHtmlSanitizationResult(
    string SanitizedHtml,
    IReadOnlyList<MailRemoteImageReference> RemoteImages,
    int InlineImageCount);
