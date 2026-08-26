using System.Globalization;
using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using UnifiedMessenger.App.Models;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.App.Services.Mail;

internal sealed record ImapSummaryData(
    uint UniqueId,
    string? Subject,
    string FromDisplayName,
    string FromAddress,
    DateTimeOffset ReceivedAt,
    bool IsUnread,
    uint UidValidity = 0)
{
    public MailMessageAttachmentSummary AttachmentSummary { get; init; } = MailMessageAttachmentSummary.Empty;
}

internal sealed record ImapInboxPageData(IReadOnlyList<ImapSummaryData> Items, string? NextCursor);
internal sealed record ImapMessageData(MimeMessage Message, bool IsUnread);
internal sealed record ImapFolderDescriptor(MailFolderKind Kind, string FullName);

internal interface IImapInboxClient
{
    Task<int> GetInboxUnreadCountAsync(
        MailServerSettings server,
        string secret,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

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

    Task<IReadOnlyList<ImapFolderDescriptor>> GetFoldersAsync(
        MailServerSettings server,
        string secret,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ImapFolderDescriptor>>(
            [new ImapFolderDescriptor(MailFolderKind.Inbox, "INBOX")]);

    Task<ImapInboxPageData> GetFolderPageAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (folder.Kind is not MailFolderKind.Inbox)
        {
            throw new MailReadException(MailReadFailureKind.FolderUnavailable, "Эта папка недоступна.");
        }

        return GetInboxPageAsync(server, secret, cursor, pageSize, cancellationToken);
    }

    Task<ImapMessageData> GetMessageAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        uint uniqueId,
        uint expectedUidValidity,
        CancellationToken cancellationToken = default)
    {
        if (folder.Kind is not MailFolderKind.Inbox)
        {
            throw new MailReadException(MailReadFailureKind.FolderUnavailable, "Эта папка недоступна.");
        }

        return GetMessageAsync(server, secret, uniqueId, cancellationToken);
    }

    Task SetReadStateAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        uint uniqueId,
        uint expectedUidValidity,
        bool isRead,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new MailReadException(MailReadFailureKind.MutationFailed, "Не удалось изменить статус письма."));
}

internal sealed class ImapMailReadProvider(
    IMailCredentialStore credentialStore,
    IMailProviderFactory providerFactory,
    IImapInboxClient inboxClient,
    IMailContentExtractor contentExtractor,
    MailMessageSourceCache? sourceCache = null) : IMailReadProvider, IMailMessageStateProvider, IMailAttachmentContentProvider, IMailInboxUnreadCountProvider
{
    private const string MessageKeyPrefix = "imap:";
    private readonly MailMessageSourceCache _sourceCache = sourceCache ?? new MailMessageSourceCache();

    public bool Supports(MailProviderType providerType) =>
        providerType is MailProviderType.Yandex or MailProviderType.MailRu or MailProviderType.GenericImap;

    public async Task<int> GetInboxUnreadCountAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        return await inboxClient.GetInboxUnreadCountAsync(server, credential.Secret, cancellationToken);
    }

    public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
        MailAccount account,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetPageAsync(
            account,
            MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"),
            continuationToken,
            pageSize,
            cancellationToken);

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        IReadOnlyList<ImapFolderDescriptor> folders = await inboxClient.GetFoldersAsync(
            server,
            credential.Secret,
            cancellationToken);
        return folders
            .GroupBy(folder => folder.Kind)
            .Select(group => group.First())
            .OrderBy(folder => folder.Kind)
            .Select(folder => MailFolderCatalog.Create(folder.Kind, folder.FullName))
            .ToArray();
    }

    public async Task<MailPage<MailMessageSummary>> GetPageAsync(
        MailAccount account,
        MailFolder folder,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize);
        ValidateFolder(folder);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        ImapInboxPageData page = await inboxClient.GetFolderPageAsync(
            server,
            credential.Secret,
            ToDescriptor(folder),
            continuationToken,
            pageSize,
            cancellationToken);
        return new MailPage<MailMessageSummary>(
            page.Items.Select(item => MapSummary(folder.Kind, item)).ToArray(),
            page.NextCursor);
    }

    public Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        string messageKey,
        CancellationToken cancellationToken = default) =>
        GetMessageAsync(
            account,
            MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"),
            messageKey,
            cancellationToken);

    public async Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        MailFolder folder,
        string messageKey,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
        ValidateFolder(folder);
        (uint uidValidity, uint uniqueId) = ParseMessageKey(folder.Kind, messageKey);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        ImapMessageData result = await inboxClient.GetMessageAsync(
            server,
            credential.Secret,
            ToDescriptor(folder),
            uniqueId,
            uidValidity,
            cancellationToken);
        MailMessageContent content = contentExtractor.Extract(messageKey, result.Message, result.IsUnread);
        if (content.Attachments.Count > 0)
        {
            _sourceCache.Set(
                account.Id,
                messageKey,
                result.Message,
                MailMimeAttachmentCatalog.EstimateEncodedSize(result.Message));
        }

        return content;
    }

    public async Task<MailAttachmentContent> GetAsync(
        MailAccount account,
        string messageKey,
        string attachmentKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
            if (_sourceCache.TryGet(account.Id, messageKey, out MimeMessage? cached) && cached is not null)
            {
                return MailMimeAttachmentCatalog.GetContent(cached, attachmentKey, cancellationToken);
            }

            MailFolderKind folderKind = ParseFolderKind(messageKey);
            MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
            ImapFolderDescriptor folder = (await inboxClient.GetFoldersAsync(
                    server,
                    credential.Secret,
                    cancellationToken))
                .FirstOrDefault(item => item.Kind == folderKind)
                ?? throw new MailReadException(
                    MailReadFailureKind.FolderUnavailable,
                    "Исходная папка больше недоступна.");
            (uint uidValidity, uint uniqueId) = ParseMessageKey(folderKind, messageKey);
            ImapMessageData result = await inboxClient.GetMessageAsync(
                server,
                credential.Secret,
                folder,
                uniqueId,
                uidValidity,
                cancellationToken);
            _sourceCache.Set(
                account.Id,
                messageKey,
                result.Message,
                MailMimeAttachmentCatalog.EstimateEncodedSize(result.Message));
            return MailMimeAttachmentCatalog.GetContent(result.Message, attachmentKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailAttachmentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MailReadException or IOException or FormatException)
        {
            throw new MailAttachmentException(
                MailAttachmentFailureKind.ProviderFailure,
                "Не удалось загрузить вложение.",
                exception);
        }
    }

    public Task<MailReadStateCapability> GetReadStateCapabilityAsync(
        MailAccount account,
        MailFolder folder,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(folder.SupportsReadState
            ? MailReadStateCapability.Available
            : MailReadStateCapability.Unsupported);

    public async Task SetReadStateAsync(
        MailAccount account,
        MailFolder folder,
        string messageKey,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
        ValidateFolder(folder);
        if (!folder.SupportsReadState)
        {
            throw new MailReadException(MailReadFailureKind.MutationFailed, "Для этой папки действие недоступно.");
        }

        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        (uint uidValidity, uint uniqueId) = ParseMessageKey(folder.Kind, messageKey);
        await inboxClient.SetReadStateAsync(
            server,
            credential.Secret,
            ToDescriptor(folder),
            uniqueId,
            uidValidity,
            isRead,
            cancellationToken);
    }

    internal static MailMessageSummary MapSummary(MailFolderKind folderKind, ImapSummaryData item) =>
        new(
            CreateMessageKey(folderKind, item.UidValidity, item.UniqueId),
            MailContentExtractor.NormalizeSubject(item.Subject),
            item.FromDisplayName,
            item.FromAddress,
            item.ReceivedAt,
            string.Empty,
            item.IsUnread)
        {
            AttachmentSummary = item.AttachmentSummary
        };

    internal static MailMessageSummary MapSummary(ImapSummaryData item) =>
        new(
            $"imap:{item.UniqueId.ToString(CultureInfo.InvariantCulture)}",
            MailContentExtractor.NormalizeSubject(item.Subject),
            item.FromDisplayName,
            item.FromAddress,
            item.ReceivedAt,
            string.Empty,
            item.IsUnread)
        {
            AttachmentSummary = item.AttachmentSummary
        };

    internal static string CreateMessageKey(MailFolderKind folderKind, uint uidValidity, uint uniqueId) =>
        $"{MessageKeyPrefix}{(int)folderKind}:{uidValidity.ToString(CultureInfo.InvariantCulture)}:{uniqueId.ToString(CultureInfo.InvariantCulture)}";

    private MailServerSettings ResolveImapSettings(MailAccount account, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!Supports(account.Provider) || pageSize is < 1 or > 100
            || providerFactory.Get(account.Provider) is not PasswordMailProvider provider)
        {
            throw new MailReadException(MailReadFailureKind.InvalidConfiguration, "Почтовый аккаунт настроен некорректно.");
        }

        MailConnectionSettings? settings = provider.CreateConnectionSettings(
            new MailAccountConnectionRequest(
                account.Provider,
                account.EmailAddress,
                account.DisplayName,
                account.GenericConnectionSettings),
            account.EmailAddress.Trim());
        return settings?.Imap ?? throw new MailReadException(
            MailReadFailureKind.InvalidConfiguration,
            "Проверьте параметры IMAP в настройках аккаунта.");
    }

    private async Task<MailCredential> LoadCredentialAsync(MailAccount account, CancellationToken cancellationToken)
    {
        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.Password } || !credential.IsValid())
        {
            throw new MailReadException(MailReadFailureKind.CredentialMissing, "Не удалось войти в почту. Проверьте пароль приложения.");
        }

        return credential;
    }

    private static void ValidateFolder(MailFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (string.IsNullOrWhiteSpace(folder.ProviderLocator))
        {
            throw new MailReadException(MailReadFailureKind.FolderUnavailable, "Эта папка недоступна.");
        }
    }

    private static ImapFolderDescriptor ToDescriptor(MailFolder folder) => new(folder.Kind, folder.ProviderLocator);

    private static (uint UidValidity, uint UniqueId) ParseMessageKey(
        MailFolderKind expectedFolder,
        string messageKey)
    {
        string prefix = $"{MessageKeyPrefix}{(int)expectedFolder}:";
        if (string.IsNullOrWhiteSpace(messageKey)
            || !messageKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, "Письмо больше недоступно.");
        }

        ReadOnlySpan<char> locator = messageKey.AsSpan(prefix.Length);
        int separator = locator.IndexOf(':');
        if (separator <= 0
            || !uint.TryParse(locator[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out uint uidValidity)
            || !uint.TryParse(locator[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid)
            || uid == 0)
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, "Письмо больше недоступно.");
        }

        return (uidValidity, uid);
    }

    private static MailFolderKind ParseFolderKind(string messageKey)
    {
        if (string.IsNullOrWhiteSpace(messageKey)
            || !messageKey.StartsWith(MessageKeyPrefix, StringComparison.Ordinal))
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, "Письмо больше недоступно.");
        }

        ReadOnlySpan<char> value = messageKey.AsSpan(MessageKeyPrefix.Length);
        int separator = value.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(value[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out int kind)
            || !Enum.IsDefined((MailFolderKind)kind))
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, "Письмо больше недоступно.");
        }

        return (MailFolderKind)kind;
    }

}

internal sealed class MailKitImapInboxClient : IImapInboxClient
{
    private const string CursorPrefix = "imap-index:";
    internal static FolderAccess InboxAccess => FolderAccess.ReadOnly;
    internal static FolderAccess MutationAccess => FolderAccess.ReadWrite;

    public async Task<int> GetInboxUnreadCountAsync(
        MailServerSettings server,
        string secret,
        CancellationToken cancellationToken = default)
    {
        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            IMailFolder inbox = client.Inbox;
            await inbox.StatusAsync(StatusItems.Unread, cancellationToken);
            return Math.Max(0, inbox.Unread);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            throw new MailReadException(MailReadFailureKind.AuthenticationFailed, "Не удалось войти в почту. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Не удалось получить число непрочитанных писем.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public Task<ImapInboxPageData> GetInboxPageAsync(
        MailServerSettings server,
        string secret,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetFolderPageAsync(
            server,
            secret,
            new ImapFolderDescriptor(MailFolderKind.Inbox, "INBOX"),
            cursor,
            pageSize,
            cancellationToken);

    public Task<ImapMessageData> GetMessageAsync(
        MailServerSettings server,
        string secret,
        uint uniqueId,
        CancellationToken cancellationToken = default) =>
        GetMessageAsync(
            server,
            secret,
            new ImapFolderDescriptor(MailFolderKind.Inbox, "INBOX"),
            uniqueId,
            expectedUidValidity: 0,
            cancellationToken);

    public async Task<IReadOnlyList<ImapFolderDescriptor>> GetFoldersAsync(
        MailServerSettings server,
        string secret,
        CancellationToken cancellationToken = default)
    {
        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            List<ImapFolderDescriptor> folders =
            [
                new(MailFolderKind.Inbox, client.Inbox.FullName)
            ];
            AddSpecialFolder(client, folders, MailFolderKind.Sent, SpecialFolder.Sent);
            AddSpecialFolder(client, folders, MailFolderKind.Drafts, SpecialFolder.Drafts);
            AddSpecialFolder(client, folders, MailFolderKind.Spam, SpecialFolder.Junk);
            AddSpecialFolder(client, folders, MailFolderKind.Trash, SpecialFolder.Trash);
            return folders;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            throw new MailReadException(MailReadFailureKind.AuthenticationFailed, "Не удалось войти в почту. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Не удалось загрузить папки почты.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task<ImapInboxPageData> GetFolderPageAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            IMailFolder mailFolder = ResolveFolder(client, folder);
            FolderAccess access = await mailFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (access != FolderAccess.ReadOnly)
            {
                throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Сервер не открыл папку только для чтения.");
            }

            if (mailFolder.Count == 0)
            {
                return new ImapInboxPageData([], null);
            }

            int endIndex = Math.Min(ParseCursor(cursor) ?? mailFolder.Count - 1, mailFolder.Count - 1);
            if (endIndex < 0)
            {
                return new ImapInboxPageData([], null);
            }

            int startIndex = Math.Max(0, endIndex - pageSize + 1);
            IList<IMessageSummary> fetched = await mailFolder.FetchAsync(
                startIndex,
                endIndex,
                MessageSummaryItems.UniqueId
                    | MessageSummaryItems.Envelope
                    | MessageSummaryItems.InternalDate
                    | MessageSummaryItems.Flags
                    | MessageSummaryItems.BodyStructure,
                cancellationToken);
            ImapSummaryData[] summaries = fetched
                .Where(summary => summary.UniqueId.IsValid)
                .Select(summary => MapProtocolSummary(mailFolder.UidValidity, summary))
                .OrderByDescending(summary => summary.ReceivedAt)
                .ThenByDescending(summary => summary.UniqueId)
                .ToArray();
            return new ImapInboxPageData(summaries, CreateOlderPageCursor(startIndex));
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
            throw new MailReadException(MailReadFailureKind.AuthenticationFailed, "Не удалось войти в почту. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Не удалось загрузить почту. Проверьте подключение к сети.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task<ImapMessageData> GetMessageAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        uint uniqueId,
        uint expectedUidValidity,
        CancellationToken cancellationToken = default)
    {
        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            IMailFolder mailFolder = ResolveFolder(client, folder);
            FolderAccess access = await mailFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (access != FolderAccess.ReadOnly)
            {
                throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Сервер не открыл папку только для чтения.");
            }

            EnsureUidValidity(mailFolder, expectedUidValidity);

            UniqueId uid = new(uniqueId);
            IList<IMessageSummary> flags = await mailFolder.FetchAsync([uid], MessageSummaryItems.Flags, cancellationToken);
            bool isUnread = flags.Count == 0 || flags[0].Flags?.HasFlag(MessageFlags.Seen) != true;
            MimeMessage message = await mailFolder.GetMessageAsync(uid, cancellationToken);
            return new ImapMessageData(message, isUnread);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailKit.Security.AuthenticationException)
        {
            throw new MailReadException(MailReadFailureKind.AuthenticationFailed, "Не удалось войти в почту. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, "Не удалось загрузить выбранное письмо.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task SetReadStateAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        uint uniqueId,
        uint expectedUidValidity,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            IMailFolder mailFolder = ResolveFolder(client, folder);
            FolderAccess access = await mailFolder.OpenAsync(MutationAccess, cancellationToken);
            if (access != FolderAccess.ReadWrite)
            {
                throw new MailReadException(MailReadFailureKind.MutationFailed, "Сервер не разрешил изменить статус письма.");
            }

            EnsureUidValidity(mailFolder, expectedUidValidity);

            UniqueId uid = new(uniqueId);
            (MessageFlags flags, bool add) = CreateSeenMutation(isRead);
            if (add)
            {
                await mailFolder.AddFlagsAsync(uid, flags, silent: true, cancellationToken);
            }
            else
            {
                await mailFolder.RemoveFlagsAsync(uid, flags, silent: true, cancellationToken);
            }
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
            throw new MailReadException(MailReadFailureKind.AuthenticationFailed, "Не удалось войти в почту. Проверьте пароль приложения.");
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(MailReadFailureKind.MutationFailed, "Не удалось изменить статус письма.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    internal static string? CreateOlderPageCursor(int startIndex) =>
        startIndex > 0 ? CursorPrefix + (startIndex - 1).ToString(CultureInfo.InvariantCulture) : null;

    internal static (MessageFlags Flags, bool Add) CreateSeenMutation(bool isRead) =>
        (MessageFlags.Seen, isRead);

    internal static int? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (!cursor.StartsWith(CursorPrefix, StringComparison.Ordinal)
            || !int.TryParse(cursor.AsSpan(CursorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || index < 0)
        {
            throw new MailReadException(MailReadFailureKind.InvalidConfiguration, "Не удалось продолжить загрузку списка писем. Обновите папку.");
        }

        return index;
    }

    private static void AddSpecialFolder(
        ImapClient client,
        ICollection<ImapFolderDescriptor> folders,
        MailFolderKind kind,
        SpecialFolder specialFolder)
    {
        IMailFolder? folder = client.GetFolder(specialFolder);
        if (folder is not null && !folder.Attributes.HasFlag(FolderAttributes.NonExistent))
        {
            folders.Add(new ImapFolderDescriptor(kind, folder.FullName));
        }
    }

    private static IMailFolder ResolveFolder(ImapClient client, ImapFolderDescriptor descriptor) =>
        descriptor.Kind is MailFolderKind.Inbox
            ? client.Inbox
            : client.GetFolder(descriptor.FullName);

    private static ImapSummaryData MapProtocolSummary(uint uidValidity, IMessageSummary summary)
    {
        (string name, string address) = MailContentExtractor.GetPrimaryMailbox(summary.Envelope?.From);
        return new ImapSummaryData(
            summary.UniqueId.Id,
            summary.Envelope?.Subject,
            name,
            address,
            summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.MinValue,
            summary.Flags?.HasFlag(MessageFlags.Seen) != true,
            uidValidity)
        {
            AttachmentSummary = GetAttachmentSummary(summary.Attachments)
        };
    }

    internal static MailMessageAttachmentSummary GetAttachmentSummary(IEnumerable<BodyPartBasic>? attachments)
    {
        IEnumerable<MailAttachmentPreviewItem> previews = (attachments ?? [])
            .Where(part => string.IsNullOrWhiteSpace(part.ContentId))
            .Select(part => new MailAttachmentPreviewItem(
                MailAttachmentFileName.Sanitize(part.FileName),
                part.ContentType?.MimeType ?? "application/octet-stream",
                part.Octets >= 0 ? part.Octets : null));

        return MailMessageAttachmentSummary.Create(previews);
    }

    private static void EnsureUidValidity(IMailFolder folder, uint expectedUidValidity)
    {
        if (expectedUidValidity != 0 && folder.UidValidity != expectedUidValidity)
        {
            throw new MailReadException(
                MailReadFailureKind.MessageUnavailable,
                "Список папки изменился. Обновите её и повторите действие.");
        }
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
                _ => throw new MailReadException(MailReadFailureKind.InvalidConfiguration, "Параметры защищённого подключения IMAP заданы некорректно.")
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
            or MailKit.CommandException
            or FolderNotFoundException
            or FolderNotOpenException;

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
            // The operation already produced a provider-neutral result.
        }
    }
}
