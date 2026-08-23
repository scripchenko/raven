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
        CancellationToken cancellationToken = default)
    {
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
                "Локальная конфигурация Google OAuth повреждена или недоступна.");
        }

        if (configuration is null)
        {
            return GmailOAuthAuthorizationResult.Failure(
                MailConnectionFailureKind.OAuthConfigurationMissing,
                $"Не найден OAuth Client типа Desktop app. Поместите загруженный файл в {configurationSource.ConfigurationPath}");
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
                "Не удалось запустить локальный OAuth callback на 127.0.0.1.");
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
                    state);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or GoogleOAuthProtocolException)
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthConfigurationInvalid,
                    "Не удалось подготовить безопасный запрос Google OAuth.");
            }

            if (!browserLauncher.TryOpen(request.AuthorizationUri))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthBrowserLaunchFailed,
                    "Не удалось открыть системный браузер для входа в Google.");
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
                    "Время ожидания входа в Google истекло. Попробуйте ещё раз.");
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or InvalidDataException or InvalidOperationException)
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.ConnectionFailed,
                    "Не удалось получить безопасный ответ Google OAuth.");
            }

            if (!FixedTimeEquals(state, callback.State))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthStateMismatch,
                    "Ответ Google OAuth не прошёл проверку state.");
            }

            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthDenied,
                    "Доступ к Gmail не был предоставлен.");
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.ConnectionFailed,
                    "Google OAuth не вернул authorization code.");
            }

            GoogleOAuthTokenResult tokens;
            try
            {
                tokens = await protocolClient.ExchangeCodeAsync(
                    configuration,
                    callback.Code,
                    request.CodeVerifier,
                    listener.RedirectUri,
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
                    "Google не смог обменять authorization code на токены.");
            }

            if (string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthTokenExchangeFailed,
                    "Google OAuth не вернул access token.");
            }

            if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                return GmailOAuthAuthorizationResult.Failure(
                    MailConnectionFailureKind.OAuthRefreshTokenMissing,
                    "Google OAuth не вернул refresh token для автономного доступа.");
            }

            MailCredential persistentCredential = MailCredential.CreateGmailOAuth(
                tokens.RefreshToken,
                configuration.ClientId,
                configuration.ClientSecret);
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
            return GmailProfileResult.Failure("Получение профиля Gmail отменено.");
        }
        catch (GoogleOAuthProtocolException)
        {
            return GmailProfileResult.Failure(
                "Не удалось получить профиль Gmail. Проверьте доступность Gmail API и разрешение аккаунта.");
        }
    }

    private static GmailOAuthAuthorizationResult Canceled() =>
        GmailOAuthAuthorizationResult.Failure(
            MailConnectionFailureKind.OperationCanceled,
            "Подключение Gmail отменено.");

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
