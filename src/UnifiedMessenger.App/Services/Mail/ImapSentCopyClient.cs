using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

internal interface IImapSentCopySessionFactory
{
    IImapSentCopySession Create();
}

internal interface IImapSentCopySession : IDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(
        string host,
        int port,
        MailSecureSocketMode secureSocketMode,
        CancellationToken cancellationToken);

    Task AuthenticateAsync(string username, string secret, CancellationToken cancellationToken);

    Task<bool> AppendToSpecialFolderAsync(
        SpecialFolder specialFolder,
        MimeMessage message,
        MessageFlags flags,
        CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal sealed class MailKitImapSentCopyClient(
    IImapSentCopySessionFactory sessionFactory) : IImapSentCopyClient
{
    public async Task<ImapSentCopyResult> AppendAsync(
        MailServerSettings server,
        string secret,
        MimeMessage message,
        MessageFlags flags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(message);
        using IImapSentCopySession session = sessionFactory.Create();
        SentCopyStage stage = SentCopyStage.Connection;
        bool appendAttempted = false;
        try
        {
            await session.ConnectAsync(
                server.Host,
                server.Port,
                server.SecureSocketMode,
                cancellationToken);
            stage = SentCopyStage.Authentication;
            await session.AuthenticateAsync(server.Username, secret, cancellationToken);
            stage = SentCopyStage.SentFolder;
            appendAttempted = true;
            bool saved = await session.AppendToSpecialFolderAsync(
                SpecialFolder.Sent,
                message,
                flags,
                cancellationToken);
            return saved
                ? ImapSentCopyResult.Saved
                : ImapSentCopyResult.Failed(MailSentCopyFailureKind.FolderUnavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ImapSentCopyResult.Failed(
                appendAttempted
                    ? MailSentCopyFailureKind.Ambiguous
                    : MailSentCopyFailureKind.Canceled);
        }
        catch (MailKit.Security.AuthenticationException)
        {
            return ImapSentCopyResult.Failed(MailSentCopyFailureKind.AuthenticationFailed);
        }
        catch (FolderNotFoundException)
        {
            return ImapSentCopyResult.Failed(MailSentCopyFailureKind.FolderUnavailable);
        }
        catch (CommandException) when (stage is SentCopyStage.SentFolder)
        {
            return ImapSentCopyResult.Failed(MailSentCopyFailureKind.AppendRejected);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            return ImapSentCopyResult.Failed(
                appendAttempted
                    ? MailSentCopyFailureKind.Ambiguous
                    : MailSentCopyFailureKind.ConnectionFailed);
        }
        catch (Exception)
        {
            return ImapSentCopyResult.Failed(MailSentCopyFailureKind.Unexpected);
        }
        finally
        {
            await DisconnectQuietlyAsync(session);
        }
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException
            or ProtocolException
            or ServiceNotConnectedException
            or ServiceNotAuthenticatedException;

    private static async Task DisconnectQuietlyAsync(IImapSentCopySession session)
    {
        if (!session.IsConnected)
        {
            return;
        }

        try
        {
            await session.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception exception) when (IsTransportFailure(exception) || exception is CommandException)
        {
            // The SMTP delivery and Sent-copy result are already known.
        }
    }

    private enum SentCopyStage
    {
        Connection,
        Authentication,
        SentFolder
    }
}

internal sealed class MailKitImapSentCopySessionFactory : IImapSentCopySessionFactory
{
    public IImapSentCopySession Create() => new MailKitImapSentCopySession();
}

internal sealed class MailKitImapSentCopySession : IImapSentCopySession
{
    private readonly ImapClient _client = new();

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
                _ => throw new NotSupportedException("Secure IMAP transport is required.")
            },
            cancellationToken);

    public Task AuthenticateAsync(string username, string secret, CancellationToken cancellationToken) =>
        _client.AuthenticateAsync(username, secret, cancellationToken);

    public async Task<bool> AppendToSpecialFolderAsync(
        SpecialFolder specialFolder,
        MimeMessage message,
        MessageFlags flags,
        CancellationToken cancellationToken)
    {
        IMailFolder? folder = _client.GetFolder(specialFolder);
        if (folder is null || folder.Attributes.HasFlag(FolderAttributes.NonExistent))
        {
            return false;
        }

        await folder.AppendAsync(new AppendRequest(message, flags), cancellationToken);
        return true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        _client.DisconnectAsync(true, cancellationToken);

    public void Dispose() => _client.Dispose();
}
