using System.IO;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailKitConnectionValidator : IMailConnectionValidator
{
    public async Task<MailConnectionValidationResult> ValidateAsync(
        MailConnectionSettings settings,
        string emailAddress,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValidServer(settings.Imap) || !IsValidServer(settings.Smtp)
            || string.IsNullOrWhiteSpace(emailAddress) || string.IsNullOrEmpty(secret))
        {
            return MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.InvalidConfiguration,
                "Проверьте адрес, пароль приложения и параметры серверов.");
        }

        using ImapClient imap = new();
        try
        {
            await imap.ConnectAsync(
                settings.Imap.Host,
                settings.Imap.Port,
                MapSecureSocketOptions(settings.Imap.SecureSocketMode),
                cancellationToken);
            await imap.AuthenticateAsync(settings.Imap.Username, secret, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.AuthenticationFailed,
                "IMAP отклонил учётные данные. Проверьте адрес и пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            return MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.ConnectionFailed,
                "Не удалось безопасно подключиться к IMAP-серверу.");
        }
        finally
        {
            await DisconnectQuietlyAsync(imap);
        }

        using SmtpClient smtp = new();
        try
        {
            await smtp.ConnectAsync(
                settings.Smtp.Host,
                settings.Smtp.Port,
                MapSecureSocketOptions(settings.Smtp.SecureSocketMode),
                cancellationToken);
            await smtp.AuthenticateAsync(settings.Smtp.Username, secret, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.AuthenticationFailed,
                "SMTP отклонил учётные данные. Проверьте адрес и пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            return MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.SmtpValidationFailed,
                "IMAP доступен, но не удалось безопасно проверить SMTP-сервер.");
        }
        finally
        {
            await DisconnectQuietlyAsync(smtp);
        }

        return MailConnectionValidationResult.Success(new MailIdentity(emailAddress.Trim(), null));
    }

    private static bool IsValidServer(MailServerSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Host)
        && !string.IsNullOrWhiteSpace(settings.Username)
        && settings.Port is > 0 and <= 65535;

    private static SecureSocketOptions MapSecureSocketOptions(MailSecureSocketMode mode) =>
        mode switch
        {
            MailSecureSocketMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
            MailSecureSocketMode.StartTls => SecureSocketOptions.StartTls,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static bool IsExpectedConnectionException(Exception exception) =>
        exception is IOException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException
            or MailKit.ProtocolException;

    private static async Task DisconnectQuietlyAsync(MailKit.IMailService service)
    {
        if (!service.IsConnected)
        {
            return;
        }

        try
        {
            await service.DisconnectAsync(true, CancellationToken.None);
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            // Validation has already produced its sanitized result; disconnect errors contain no useful user action.
        }
    }
}
