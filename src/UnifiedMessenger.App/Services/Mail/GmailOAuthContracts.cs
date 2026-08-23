namespace UnifiedMessenger.App.Services.Mail;

public static class GmailOAuthConstants
{
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/gmail.readonly";
    public const string CallbackPath = "/oauth2/callback/";
    public const string ApplicationName = "UnifiedMessenger";
}

public sealed record GoogleOAuthClientConfiguration(string ClientId, string ClientSecret);

public sealed record GoogleOAuthAuthorizationRequest(Uri AuthorizationUri, string CodeVerifier);

public sealed record GoogleOAuthTokenResult(string AccessToken, string RefreshToken);

public sealed record GmailOAuthSession(string AccessToken, MailCredential PersistentCredential);

public sealed record GmailUserProfile(string EmailAddress, string? HistoryId);

public sealed record OAuthLoopbackResponse(string? Code, string? State, string? Error);

public sealed record GmailOAuthAuthorizationResult(
    bool IsSuccess,
    GmailOAuthSession? Session,
    MailConnectionFailureKind FailureKind,
    string UserMessage)
{
    public static GmailOAuthAuthorizationResult Success(GmailOAuthSession session) =>
        new(true, session, MailConnectionFailureKind.None, "Авторизация Google завершена.");

    public static GmailOAuthAuthorizationResult Failure(
        MailConnectionFailureKind kind,
        string message) =>
        new(false, null, kind, message);
}

public sealed record GmailProfileResult(
    bool IsSuccess,
    GmailUserProfile? Profile,
    MailConnectionFailureKind FailureKind,
    string UserMessage)
{
    public static GmailProfileResult Success(GmailUserProfile profile) =>
        new(true, profile, MailConnectionFailureKind.None, "Профиль Gmail получен.");

    public static GmailProfileResult Failure(string message) =>
        new(false, null, MailConnectionFailureKind.GmailProfileFailed, message);
}

public sealed record GmailOAuthOptions(TimeSpan AuthorizationTimeout)
{
    public static GmailOAuthOptions Default { get; } = new(TimeSpan.FromMinutes(3));
}

public interface IGoogleOAuthClientConfigurationSource
{
    string ConfigurationPath { get; }
    Task<GoogleOAuthClientConfiguration?> LoadAsync(CancellationToken cancellationToken = default);
}

public interface ISystemBrowserLauncher
{
    bool TryOpen(Uri uri);
}

public interface IOAuthStateGenerator
{
    string CreateState();
}

public interface IOAuthLoopbackListener : IAsyncDisposable
{
    Uri RedirectUri { get; }
    Task<OAuthLoopbackResponse> WaitForCallbackAsync(CancellationToken cancellationToken = default);
}

public interface IOAuthLoopbackListenerFactory
{
    IOAuthLoopbackListener Create();
}

public interface IGoogleOAuthProtocolClient
{
    GoogleOAuthAuthorizationRequest CreateAuthorizationRequest(
        GoogleOAuthClientConfiguration configuration,
        Uri redirectUri,
        string state);

    Task<GoogleOAuthTokenResult> ExchangeCodeAsync(
        GoogleOAuthClientConfiguration configuration,
        string authorizationCode,
        string codeVerifier,
        Uri redirectUri,
        CancellationToken cancellationToken = default);

    Task<GmailUserProfile> GetProfileAsync(
        string accessToken,
        CancellationToken cancellationToken = default);
}

public interface IGmailOAuthService
{
    Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
        CancellationToken cancellationToken = default);

    Task<GmailProfileResult> GetProfileAsync(
        GmailOAuthSession session,
        CancellationToken cancellationToken = default);
}

public sealed class GoogleOAuthProtocolException : Exception
{
    public GoogleOAuthProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
