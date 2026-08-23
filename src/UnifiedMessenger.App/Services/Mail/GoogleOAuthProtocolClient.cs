using Google;
using System.Net.Http;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class GoogleOAuthProtocolClient : IGoogleOAuthProtocolClient
{
    public GoogleOAuthAuthorizationRequest CreateAuthorizationRequest(
        GoogleOAuthClientConfiguration configuration,
        Uri redirectUri,
        string state) =>
        CreateAuthorizationRequest(configuration, redirectUri, state, GmailOAuthConstants.ReadOnlyScope);

    public GoogleOAuthAuthorizationRequest CreateAuthorizationRequest(
        GoogleOAuthClientConfiguration configuration,
        Uri redirectUri,
        string state,
        string scope)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        ValidateScope(scope);
        using PkceGoogleAuthorizationCodeFlow flow = CreateFlow(configuration, scope);
        AuthorizationCodeRequestUrl requestBase = flow.CreateAuthorizationCodeRequest(
            redirectUri.AbsoluteUri,
            out string codeVerifier);
        if (requestBase is not GoogleAuthorizationCodeRequestUrl request)
        {
            throw new GoogleOAuthProtocolException("Google did not create an installed-app authorization request.");
        }

        request.State = state;
        request.AccessType = "offline";
        request.Prompt = "consent";
        return new GoogleOAuthAuthorizationRequest(request.Build(), codeVerifier, scope);
    }

    public async Task<GoogleOAuthTokenResult> ExchangeCodeAsync(
        GoogleOAuthClientConfiguration configuration,
        string authorizationCode,
        string codeVerifier,
        Uri redirectUri,
        CancellationToken cancellationToken = default) =>
        await ExchangeCodeAsync(
            configuration,
            authorizationCode,
            codeVerifier,
            redirectUri,
            GmailOAuthConstants.ReadOnlyScope,
            cancellationToken);

    public async Task<GoogleOAuthTokenResult> ExchangeCodeAsync(
        GoogleOAuthClientConfiguration configuration,
        string authorizationCode,
        string codeVerifier,
        Uri redirectUri,
        string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentNullException.ThrowIfNull(redirectUri);

        try
        {
            ValidateScope(scope);
            using PkceGoogleAuthorizationCodeFlow flow = CreateFlow(configuration, scope);
            TokenResponse response = await flow.ExchangeCodeForTokenAsync(
                "gmail-desktop-oauth",
                authorizationCode,
                codeVerifier,
                redirectUri.AbsoluteUri,
                cancellationToken);
            return new GoogleOAuthTokenResult(
                response.AccessToken ?? string.Empty,
                response.RefreshToken ?? string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is TokenResponseException
                or HttpRequestException
                or GoogleApiException
                or InvalidOperationException)
        {
            throw new GoogleOAuthProtocolException("Google OAuth token exchange failed.", exception);
        }
    }

    public async Task<GmailUserProfile> GetProfileAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        try
        {
            GoogleCredential credential = GoogleCredential.FromAccessToken(accessToken);
            using GmailService service = new(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = GmailOAuthConstants.ApplicationName
                });
            Profile profile = await service.Users.GetProfile("me").ExecuteAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(profile.EmailAddress))
            {
                throw new GoogleOAuthProtocolException("Gmail profile did not contain an email address.");
            }

            return new GmailUserProfile(
                profile.EmailAddress.Trim(),
                profile.HistoryId?.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GoogleOAuthProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is GoogleApiException
                or HttpRequestException
                or InvalidOperationException)
        {
            throw new GoogleOAuthProtocolException("Gmail profile request failed.", exception);
        }
    }

    private static PkceGoogleAuthorizationCodeFlow CreateFlow(
        GoogleOAuthClientConfiguration configuration,
        string scope) =>
        new(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = configuration.ClientId,
                    ClientSecret = configuration.ClientSecret
                },
                Scopes = [scope]
            });

    private static void ValidateScope(string scope)
    {
        if (scope is not GmailOAuthConstants.ReadOnlyScope and not GmailOAuthConstants.ModifyScope)
        {
            throw new ArgumentException("Unsupported Gmail OAuth scope.", nameof(scope));
        }
    }
}
