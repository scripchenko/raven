using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

[Flags]
public enum MailProviderCapabilities
{
    None = 0,
    ConnectionValidation = 1,
    IdentityValidation = 2,
    OAuthAuthentication = 4,
    PasswordAuthentication = 8,
    FolderListing = 16,
    MessageListing = 32,
    MessageReading = 64,
    MarkRead = 128,
    Sending = 256,
    Deleting = 512,
    Moving = 1024,
    Search = 2048,
    BackgroundSync = 4096,
    PushNotifications = 8192
}

public enum MailConnectionFailureKind
{
    None,
    InvalidConfiguration,
    AuthenticationFailed,
    ConnectionFailed,
    SmtpValidationFailed,
    OAuthNotAvailable,
    OAuthConfigurationMissing,
    OAuthConfigurationInvalid,
    OAuthBrowserLaunchFailed,
    OAuthDenied,
    OAuthStateMismatch,
    OAuthTimeout,
    OAuthTokenExchangeFailed,
    OAuthRefreshTokenMissing,
    GmailProfileFailed,
    OperationCanceled,
    CredentialStorageFailed,
    PersistenceFailed,
    AlreadyExists
}

public sealed record MailIdentity(string EmailAddress, string? DisplayName);

public sealed record MailConnectionValidationResult(
    bool IsSuccess,
    MailIdentity? Identity,
    MailConnectionFailureKind FailureKind,
    string UserMessage)
{
    public static MailConnectionValidationResult Success(MailIdentity identity) =>
        new(true, identity, MailConnectionFailureKind.None, "Подключение проверено.");

    public static MailConnectionValidationResult Failure(
        MailConnectionFailureKind kind,
        string message) =>
        new(false, null, kind, message);
}

public sealed record MailAccountConnectionRequest(
    MailProviderType Provider,
    string EmailAddress,
    string? DisplayName,
    MailConnectionSettings? GenericConnectionSettings = null);

public sealed record MailAccountProvisioningResult(
    bool IsSuccess,
    MailAccount? Account,
    MailConnectionFailureKind FailureKind,
    string UserMessage)
{
    public static MailAccountProvisioningResult Success(MailAccount account) =>
        new(true, account, MailConnectionFailureKind.None, "Почтовый аккаунт подключён.");

    public static MailAccountProvisioningResult Failure(
        MailConnectionFailureKind kind,
        string message) =>
        new(false, null, kind, message);
}

public sealed record MailAccountPasswordReplacementResult(
    bool IsSuccess,
    MailConnectionFailureKind FailureKind,
    string UserMessage)
{
    public static MailAccountPasswordReplacementResult Success() =>
        new(true, MailConnectionFailureKind.None, "Пароль приложения обновлён.");

    public static MailAccountPasswordReplacementResult Failure(
        MailConnectionFailureKind kind,
        string message) =>
        new(false, kind, message);
}

public sealed record MailProviderDescriptor(
    MailProviderType Provider,
    string DisplayName,
    MailAuthenticationKind AuthenticationKind,
    MailProviderCapabilities Capabilities,
    string Guidance);

public interface IMailConnectionValidator
{
    Task<MailConnectionValidationResult> ValidateAsync(
        MailConnectionSettings settings,
        string emailAddress,
        string secret,
        CancellationToken cancellationToken = default);
}

public interface IMailProvider
{
    MailProviderType ProviderType { get; }
    MailProviderDescriptor Descriptor { get; }

    Task<MailConnectionValidationResult> ValidateAsync(
        MailAccountConnectionRequest request,
        string secret,
        CancellationToken cancellationToken = default);
}

public interface IMailProviderFactory
{
    IReadOnlyList<MailProviderDescriptor> Providers { get; }
    IMailProvider Get(MailProviderType providerType);
}

public interface IMailCredentialStore
{
    Task SaveAsync(
        string credentialKey,
        MailCredential credential,
        CancellationToken cancellationToken = default);

    Task<MailCredential?> LoadAsync(
        string credentialKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default);
}

public interface IMailCredentialProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

public interface IMailAccountProvisioningService
{
    Task<MailAccountProvisioningResult> ConnectAsync(
        MailAccountConnectionRequest request,
        string secret,
        CancellationToken cancellationToken = default);

    Task<MailAccountProvisioningResult> ConnectGmailAsync(
        CancellationToken cancellationToken = default);

    Task<MailAccountPasswordReplacementResult> ReplaceYandexPasswordAsync(
        MailAccount account,
        string newPassword,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(MailAccountPasswordReplacementResult.Failure(
            MailConnectionFailureKind.InvalidConfiguration,
            "Изменение пароля приложения недоступно."));

    Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default);
}
