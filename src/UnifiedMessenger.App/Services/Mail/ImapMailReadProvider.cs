using System.Globalization;
using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

internal sealed record ImapSummaryData(
    uint UniqueId,
    string? Subject,
    string FromDisplayName,
    string FromAddress,
    DateTimeOffset ReceivedAt,
    bool IsUnread);

internal sealed record ImapInboxPageData(
    IReadOnlyList<ImapSummaryData> Items,
    string? NextCursor);

internal sealed record ImapMessageData(MimeMessage Message, bool IsUnread);

internal interface IImapInboxClient
{
    Task<ImapInboxPageData> GetInboxPageAsync(
        MailServerSettings server,
        string secret,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ImapMessageData> GetMessageAsync(
        MailServerSettings server,
        string secret,
        uint uniqueId,
        CancellationToken cancellationToken = default);
}

internal sealed class ImapMailReadProvider(
    IMailCredentialStore credentialStore,
    IMailProviderFactory providerFactory,
    IImapInboxClient inboxClient,
    IMailContentExtractor contentExtractor) : IMailReadProvider
{
    private const string MessageKeyPrefix = "imap:";

    public bool Supports(MailProviderType providerType) =>
        providerType is MailProviderType.Yandex or MailProviderType.MailRu or MailProviderType.GenericImap;

    public async Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
        MailAccount account,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        ImapInboxPageData page = await inboxClient.GetInboxPageAsync(
            server,
            credential.Secret,
            continuationToken,
            pageSize,
            cancellationToken);
        return new MailPage<MailMessageSummary>(page.Items.Select(MapSummary).ToArray(), page.NextCursor);
    }

    public async Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        string messageKey,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
        uint uniqueId = ParseMessageKey(messageKey);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        ImapMessageData result = await inboxClient.GetMessageAsync(
            server,
            credential.Secret,
            uniqueId,
            cancellationToken);
        return contentExtractor.Extract(messageKey, result.Message, result.IsUnread);
    }

    internal static MailMessageSummary MapSummary(ImapSummaryData item) =>
        new(
            MessageKeyPrefix + item.UniqueId.ToString(CultureInfo.InvariantCulture),
            MailContentExtractor.NormalizeSubject(item.Subject),
            item.FromDisplayName,
            item.FromAddress,
            item.ReceivedAt,
            string.Empty,
            item.IsUnread);

    private MailServerSettings ResolveImapSettings(MailAccount account, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!Supports(account.Provider) || pageSize is < 1 or > 100)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Почтовый аккаунт настроен некорректно.");
        }

        if (providerFactory.Get(account.Provider) is not PasswordMailProvider provider)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Почтовый аккаунт настроен некорректно.");
        }

        MailConnectionSettings? settings = provider.CreateConnectionSettings(
            new MailAccountConnectionRequest(
                account.Provider,
                account.EmailAddress,
                account.DisplayName,
                account.GenericConnectionSettings),
            account.EmailAddress.Trim());
        if (settings is null)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Проверьте параметры IMAP в настройках аккаунта.");
        }

        return settings.Imap;
    }

    private async Task<MailCredential> LoadCredentialAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.Password } || !credential.IsValid())
        {
            throw new MailReadException(
                MailReadFailureKind.CredentialMissing,
                "Не удалось войти в почту. Проверьте пароль приложения.");
        }

        return credential;
    }

    private static uint ParseMessageKey(string messageKey)
    {
        if (string.IsNullOrWhiteSpace(messageKey)
            || !messageKey.StartsWith(MessageKeyPrefix, StringComparison.Ordinal)
            || !uint.TryParse(
                messageKey.AsSpan(MessageKeyPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint uniqueId)
            || uniqueId == 0)
        {
            throw new MailReadException(
                MailReadFailureKind.MessageUnavailable,
                "Письмо больше недоступно.");
        }

        return uniqueId;
    }
}

internal sealed class MailKitImapInboxClient : IImapInboxClient
{
    private const string CursorPrefix = "imap-index:";
    internal static FolderAccess InboxAccess => FolderAccess.ReadOnly;

    public async Task<ImapInboxPageData> GetInboxPageAsync(
        MailServerSettings server,
        string secret,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            IMailFolder inbox = client.Inbox;
            FolderAccess access = await inbox.OpenAsync(InboxAccess, cancellationToken);
            if (access != FolderAccess.ReadOnly)
            {
                throw new MailReadException(
                    MailReadFailureKind.ConnectionFailed,
                    "Сервер не открыл входящие в безопасном режиме только для чтения.");
            }

            if (inbox.Count == 0)
            {
                return new ImapInboxPageData([], null);
            }

            int endIndex = ParseCursor(cursor) ?? inbox.Count - 1;
            endIndex = Math.Min(endIndex, inbox.Count - 1);
            if (endIndex < 0)
            {
                return new ImapInboxPageData([], null);
            }

            int startIndex = Math.Max(0, endIndex - pageSize + 1);
            IList<IMessageSummary> fetched = await inbox.FetchAsync(
                startIndex,
                endIndex,
                MessageSummaryItems.UniqueId
                    | MessageSummaryItems.Envelope
                    | MessageSummaryItems.InternalDate
                    | MessageSummaryItems.Flags,
                cancellationToken);

            ImapSummaryData[] summaries = fetched
                .Where(summary => summary.UniqueId.IsValid)
                .Select(MapProtocolSummary)
                .OrderByDescending(summary => summary.ReceivedAt)
                .ThenByDescending(summary => summary.UniqueId)
                .ToArray();
            string? nextCursor = startIndex > 0
                ? CursorPrefix + (startIndex - 1).ToString(CultureInfo.InvariantCulture)
                : null;
            return new ImapInboxPageData(summaries, nextCursor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailReadException)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            throw new MailReadException(
                MailReadFailureKind.AuthenticationFailed,
                "Не удалось войти в почту. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                "Не удалось загрузить почту. Проверьте подключение к сети.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task<ImapMessageData> GetMessageAsync(
        MailServerSettings server,
        string secret,
        uint uniqueId,
        CancellationToken cancellationToken = default)
    {
        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            IMailFolder inbox = client.Inbox;
            FolderAccess access = await inbox.OpenAsync(InboxAccess, cancellationToken);
            if (access != FolderAccess.ReadOnly)
            {
                throw new MailReadException(
                    MailReadFailureKind.ConnectionFailed,
                    "Сервер не открыл входящие в безопасном режиме только для чтения.");
            }

            UniqueId id = new(uniqueId);
            IList<IMessageSummary> flags = await inbox.FetchAsync(
                [id],
                MessageSummaryItems.Flags,
                cancellationToken);
            bool isUnread = flags.Count == 0 || flags[0].Flags?.HasFlag(MessageFlags.Seen) != true;
            MimeMessage message = await inbox.GetMessageAsync(id, cancellationToken);
            return new ImapMessageData(message, isUnread);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailReadException)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            throw new MailReadException(
                MailReadFailureKind.AuthenticationFailed,
                "Не удалось войти в почту. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(
                MailReadFailureKind.MessageUnavailable,
                "Не удалось загрузить выбранное письмо.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    internal static string? CreateOlderPageCursor(int startIndex) =>
        startIndex > 0
            ? CursorPrefix + (startIndex - 1).ToString(CultureInfo.InvariantCulture)
            : null;

    internal static int? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (!cursor.StartsWith(CursorPrefix, StringComparison.Ordinal)
            || !int.TryParse(
                cursor.AsSpan(CursorPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int index)
            || index < 0)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Не удалось продолжить загрузку списка писем. Обновите входящие.");
        }

        return index;
    }

    private static ImapSummaryData MapProtocolSummary(IMessageSummary summary)
    {
        (string name, string address) = MailContentExtractor.GetPrimaryMailbox(summary.Envelope?.From);
        return new ImapSummaryData(
            summary.UniqueId.Id,
            summary.Envelope?.Subject,
            name,
            address,
            summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.MinValue,
            summary.Flags?.HasFlag(MessageFlags.Seen) != true);
    }

    private static async Task ConnectAndAuthenticateAsync(
        ImapClient client,
        MailServerSettings server,
        string secret,
        CancellationToken cancellationToken)
    {
        await client.ConnectAsync(
            server.Host,
            server.Port,
            server.SecureSocketMode switch
            {
                MailSecureSocketMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
                MailSecureSocketMode.StartTls => SecureSocketOptions.StartTls,
                _ => throw new MailReadException(
                    MailReadFailureKind.InvalidConfiguration,
                    "Параметры защищённого подключения IMAP заданы некорректно.")
            },
            cancellationToken);
        await client.AuthenticateAsync(server.Username, secret, cancellationToken);
    }

    private static bool IsExpectedConnectionException(Exception exception) =>
        exception is IOException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException
            or MailKit.ProtocolException
            or MailKit.CommandException;

    private static async Task DisconnectQuietlyAsync(ImapClient client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            await client.DisconnectAsync(true, CancellationToken.None);
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            // The read operation has already produced its sanitized result.
        }
    }
}
