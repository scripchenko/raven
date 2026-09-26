using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class GmailOAuthService(
    IGoogleOAuthClientConfigurationSource configurationSource,
    ISystemBrowserLauncher browserLauncher,
    IOAuthStateGenerator stateGenerator,
    IOAuthLoopbackListenerFactory listenerFactory,
    IGoogleOAuthProtocolClient protocolClient,
    GmailOAuthOptions options) : IGmailOAuthService
{
    public async Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
        CancellationToken cancellationToken = default) =>
        await AuthorizeAsync(GmailOAuthConstants.ReadOnlyScope, cancellationToken);

    public async Task<GmailOAuthAuthorizationResult> AuthorizeAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        if (scope is not GmailOAuthConstants.ReadOnlyScope and not GmailOAuthConstants.ModifyScope)
        {
            return GmailOAuthAuthorizationResult.Failure(
                MailConnectionFailureKind.InvalidConfiguration,
                L.Instance.Get("Unsupported Gmail permission requested."));
        }
        GoogleOAuthClientConfiguration? configuration;
        try
        {
            configuration = await configurationSource.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return GmailOAuthAuthorizationResult.Failure(
                MailConnectionFailureKind.OAuthConfigurationInvalid,
                L.Instance.Get("Local Google OAuth configuration is damaged or unavailable."));
        }

        if (configuration is null)
        {
            return GmailOAuthAuthorizationResult.Failure(
                MailConnectionFailureKind.OAuthConfigurationMissing,
                L.Instance.Format("No Desktop app OAuth client was found. Put the downloaded file at {0}", configurationSource.ConfigurationPath));
        }

        IOAuthLoopbackListener listener;
        try
        {
            listener = listenerFactory.Create();
        }
        catch (Exception exception) when (exception is SocketException or IOException or InvalidOperationException)
        {
            return GmailOAuthAuthorizationResult.Failure(
                MailConnectionFailureKind.ConnectionFailed,
                L.Instance.Get("Could not start the local OAuth callback on 127.0.0.1."));
        }

        await using (listener)
        {
            string state = stateGenerator.CreateState();
            GoogleOAuthAuthorizationRequest request;
            try
            {
                request = protocolClient.CreateAuthorizationRequest(
                    configuration,
                    listener.RedirectUri,
                    state,
                    scope);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or GoogleOAuthProtocolException)
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthConfigurationInvalid,
                    L.Instance.Get("Could not prepare a secure Google OAuth request."));
            }

            if (!browserLauncher.TryOpen(request.AuthorizationUri))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthBrowserLaunchFailed,
                    L.Instance.Get("Could not open the system browser for Google sign-in."));
            }

            using CancellationTokenSource timeout = new(options.AuthorizationTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            OAuthLoopbackResponse callback;
            try
            {
                callback = await listener.WaitForCallbackAsync(linked.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Canceled();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthTimeout,
                    L.Instance.Get("Google sign-in timed out. Please retry."));
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or InvalidDataException or InvalidOperationException)
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.ConnectionFailed,
                    L.Instance.Get("Could not obtain a secure Google OAuth response."));
            }

            if (!FixedTimeEquals(state, callback.State))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthStateMismatch,
                    L.Instance.Get("Google OAuth response failed state validation."));
            }

            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthDenied,
                    L.Instance.Get("Gmail access was not granted."));
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.ConnectionFailed,
                    L.Instance.Get("Google OAuth did not return an authorization code."));
            }

            GoogleOAuthTokenResult tokens;
            try
            {
                tokens = await protocolClient.ExchangeCodeAsync(
                    configuration,
                    callback.Code,
                    request.CodeVerifier,
                    listener.RedirectUri,
                    scope,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Canceled();
            }
            catch (GoogleOAuthProtocolException)
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthTokenExchangeFailed,
                    L.Instance.Get("Google could not exchange the authorization code for tokens."));
            }

            if (string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthTokenExchangeFailed,
                    L.Instance.Get("Google OAuth did not return an access token."));
            }

            if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthRefreshTokenMissing,
                    L.Instance.Get("Google OAuth did not return a refresh token for offline access."));
            }

            MailCredential persistentCredential = MailCredential.CreateGmailOAuth(
                tokens.RefreshToken,
                configuration.ClientId,
                configuration.ClientSecret,
                scope);
            return GmailOAuthAuthorizationResult.Success(
                new GmailOAuthSession(tokens.AccessToken, persistentCredential));
        }
    }

    public async Task<GmailProfileResult> GetProfileAsync(
        GmailOAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            GmailUserProfile profile = await protocolClient.GetProfileAsync(
                session.AccessToken,
                cancellationToken);
            return GmailProfileResult.Success(profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return GmailProfileResult.Failure(L.Instance.Get("Retrieving the Gmail profile was canceled."));
        }
        catch (GoogleOAuthProtocolException)
        {
            return GmailProfileResult.Failure(
                L.Instance.Get("Could not retrieve the Gmail profile. Check Gmail API availability and account permission."));
        }
    }

    private static GmailOAuthAuthorizationResult Canceled() =>
        GmailOAuthAuthorizationResult.Failure(
            MailConnectionFailureKind.OperationCanceled,
            L.Instance.Get("Connecting Gmail was canceled."));

    private static bool FixedTimeEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);
        try
        {
            return expectedBytes.Length == actualBytes.Length
                && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }
}
