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
    InvalidMessage,
    FolderUnavailable,
    MutationNotAuthorized,
    MutationFailed
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

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
        MailAccount account,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailFolder>>([MailFolderCatalog.Inbox()]);

    Task<MailPage<MailMessageSummary>> GetPageAsync(
        MailAccount account,
        MailFolder folder,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (folder.Kind is not MailFolderKind.Inbox)
        {
            throw new MailReadException(
                MailReadFailureKind.FolderUnavailable,
                "Эта папка недоступна.");
        }

        return GetInboxPageAsync(account, continuationToken, pageSize, cancellationToken);
    }

    Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        MailFolder folder,
        string messageKey,
        CancellationToken cancellationToken = default) =>
        GetMessageAsync(account, messageKey, cancellationToken);
}

public sealed record MailReadStateCapability(
    bool CanSetReadState,
    bool RequiresAuthorization,
    string? UserMessage)
{
    public static MailReadStateCapability Available { get; } = new(true, false, null);
    public static MailReadStateCapability Unsupported { get; } = new(false, false, null);
}

public interface IMailMessageStateProvider
{
    Task<MailReadStateCapability> GetReadStateCapabilityAsync(
        MailAccount account,
        MailFolder folder,
        CancellationToken cancellationToken = default);

    Task SetReadStateAsync(
        MailAccount account,
        MailFolder folder,
        string messageKey,
        bool isRead,
        CancellationToken cancellationToken = default);
}

public interface IMailInboxUnreadCountProvider
{
    Task<int> GetInboxUnreadCountAsync(
        MailAccount account,
        CancellationToken cancellationToken = default);
}

public sealed record GmailScopeUpgradeResult(bool IsSuccess, string UserMessage)
{
    public static GmailScopeUpgradeResult Success() =>
        new(true, "Доступ Google для изменения статуса писем предоставлен.");

    public static GmailScopeUpgradeResult Failure(string message) => new(false, message);
}

public interface IGmailScopeUpgradeService
{
    Task<GmailScopeUpgradeResult> UpgradeAsync(
        MailAccount account,
        CancellationToken cancellationToken = default);
}

public enum GmailReauthenticationOutcome
{
    Success,
    Canceled,
    Failed,
    WrongAccount
}

public sealed record GmailReauthenticationResult(
    GmailReauthenticationOutcome Outcome,
    string? UserMessage = null)
{
    public bool IsSuccess => Outcome is GmailReauthenticationOutcome.Success;

    public static GmailReauthenticationResult Success() =>
        new(GmailReauthenticationOutcome.Success);

    public static GmailReauthenticationResult Canceled() =>
        new(GmailReauthenticationOutcome.Canceled);

    public static GmailReauthenticationResult Failure() =>
        new(
            GmailReauthenticationOutcome.Failed,
            "Не удалось войти в Google. Попробуйте ещё раз.");

    public static GmailReauthenticationResult WrongAccount(string expectedEmail) =>
        new(
            GmailReauthenticationOutcome.WrongAccount,
            $"Вы вошли в другой аккаунт Google. Войдите как {expectedEmail}.");
}

public interface IGmailReauthenticationService
{
    Task<GmailReauthenticationResult> ReauthenticateAsync(
        MailAccount account,
        CancellationToken cancellationToken = default);
}

public interface IMailReadProviderFactory
{
    IMailReadProvider Get(MailProviderType providerType);
    IGmailScopeUpgradeService? GmailScopeUpgradeService => null;
    IGmailReauthenticationService? GmailReauthenticationService => null;
    IGmailMailboxManagementService? GmailMailboxManagementService => null;
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
