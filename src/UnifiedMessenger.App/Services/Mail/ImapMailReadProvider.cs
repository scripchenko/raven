using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
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

internal sealed record ImapInboxPageData(
    IReadOnlyList<ImapSummaryData> Items,
    string? NextCursor,
    long? TotalCount = null);
internal readonly record struct ImapChronologyEntry(uint UniqueId, DateTimeOffset ReceivedAt);
internal sealed record ImapMessageData(MimeMessage Message, bool IsUnread);
internal sealed record ImapFolderDescriptor(MailFolderKind Kind, string FullName)
{
    public bool CanAcceptArchive { get; init; }
}
internal sealed record ImapInboxTechnicalSnapshot(
    int UnreadCount,
    uint UidValidity,
    IReadOnlyList<uint> UniqueIds)
{
    public IReadOnlyDictionary<uint, MailNotificationPreview> NotificationPreviews { get; init; } =
        new Dictionary<uint, MailNotificationPreview>();
}

internal interface IImapInboxClient
{
    Task<ImapInboxTechnicalSnapshot> GetInboxTechnicalSnapshotAsync(
        MailServerSettings server,
        string secret,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ImapInboxTechnicalSnapshot>(new NotSupportedException());

    Task<MailNotificationPreview?> GetInboxNotificationPreviewAsync(
        MailServerSettings server,
        string secret,
        uint uniqueId,
        uint expectedUidValidity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<MailNotificationPreview?>(null);

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

    Task<ImapInboxPageData> GetUidSafeFolderPageAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        string? query,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            return Task.FromException<ImapInboxPageData>(
                new MailReadException(MailReadFailureKind.InvalidSearchQuery, "Поиск для этого аккаунта недоступен."));
        }

        return GetFolderPageAsync(server, secret, folder, cursor, pageSize, cancellationToken);
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
    MailMessageSourceCache? sourceCache = null) : IMailReadProvider, IMailSearchProvider, IMailMessageStateProvider, IMailAttachmentContentProvider, IMailInboxUnreadCountProvider, IMailInboxTechnicalSnapshotProvider, IMailInboxNotificationPreviewProvider
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

    async Task<MailInboxTechnicalSnapshot> IMailInboxTechnicalSnapshotProvider.GetInboxTechnicalSnapshotAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        ImapInboxTechnicalSnapshot snapshot = await inboxClient
            .GetInboxTechnicalSnapshotAsync(server, credential.Secret, cancellationToken);
        string identityScope = $"imap-inbox:{snapshot.UidValidity.ToString(CultureInfo.InvariantCulture)}";
        string[] identities = snapshot.UniqueIds
            .Where(uniqueId => uniqueId > 0)
            .Select(uniqueId => uniqueId.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new MailInboxTechnicalSnapshot(snapshot.UnreadCount, identityScope, identities)
        {
            NotificationPreviews = snapshot.NotificationPreviews
                .Where(pair => pair.Key > 0)
                .ToDictionary(
                    pair => pair.Key.ToString(CultureInfo.InvariantCulture),
                    pair => pair.Value,
                    StringComparer.Ordinal)
        };
    }

    async Task<MailNotificationPreview?> IMailInboxNotificationPreviewProvider.GetInboxNotificationPreviewAsync(
        MailAccount account,
        string identityScope,
        string messageIdentity,
        CancellationToken cancellationToken)
    {
        if (account.Provider is not MailProviderType.Yandex
            || !TryParseInboxNotificationIdentity(identityScope, messageIdentity, out uint uidValidity, out uint uniqueId))
        {
            return null;
        }

        MailServerSettings server = ResolveImapSettings(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        return await inboxClient.GetInboxNotificationPreviewAsync(
            server,
            credential.Secret,
            uniqueId,
            uidValidity,
            cancellationToken);
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
            .Where(folder => folder.Kind is not MailFolderKind.Archive
                || MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap)
            .GroupBy(folder => folder.Kind)
            .Select(group => group.First())
            .OrderBy(folder => folder.Kind)
            .Select(folder => MailFolderCatalog.Create(folder.Kind, folder.FullName,
                canAcceptArchive: folder.CanAcceptArchive))
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
        ImapInboxPageData page = MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap
            ? await inboxClient.GetUidSafeFolderPageAsync(
                server,
                credential.Secret,
                ToDescriptor(folder),
                query: null,
                continuationToken,
                pageSize,
                cancellationToken)
            : await inboxClient.GetFolderPageAsync(
                server,
                credential.Secret,
                ToDescriptor(folder),
                continuationToken,
                pageSize,
                cancellationToken);
        return new MailPage<MailMessageSummary>(
            page.Items.Select(item => MapSummary(folder.Kind, item)).ToArray(),
            page.NextCursor,
            page.TotalCount);
    }

    public Task<MailPage<MailMessageSummary>> SearchAsync(
        MailAccount account,
        string query,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        SearchAsync(
            account,
            MailFolderCatalog.Create(MailFolderKind.Inbox, "INBOX"),
            query,
            continuationToken,
            pageSize,
            cancellationToken);

    public async Task<MailPage<MailMessageSummary>> SearchAsync(
        MailAccount account,
        MailFolder folder,
        string query,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        MailServerSettings server = ResolveImapSettings(account, pageSize);
        ValidateFolder(folder);
        if (!MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap
            || string.IsNullOrWhiteSpace(query))
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidSearchQuery,
                "Не удалось выполнить поиск. Проверьте запрос.");
        }

        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        ImapInboxPageData page = await inboxClient.GetUidSafeFolderPageAsync(
            server,
            credential.Secret,
            ToDescriptor(folder),
            query.Trim(),
            continuationToken,
            pageSize,
            cancellationToken);
        return new MailPage<MailMessageSummary>(
            page.Items.Select(item => MapSummary(folder.Kind, item)).ToArray(),
            page.NextCursor,
            page.TotalCount);
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

    internal static (uint UidValidity, uint UniqueId) ParseMessageKey(
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

    private static bool TryParseInboxNotificationIdentity(
        string identityScope,
        string messageIdentity,
        out uint uidValidity,
        out uint uniqueId)
    {
        const string scopePrefix = "imap-inbox:";
        uidValidity = 0;
        uniqueId = 0;
        if (!identityScope.StartsWith(scopePrefix, StringComparison.Ordinal)
            || !uint.TryParse(
                identityScope.AsSpan(scopePrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uidValidity)
            || !uint.TryParse(
                messageIdentity,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uniqueId))
        {
            return false;
        }

        return uidValidity > 0 && uniqueId > 0;
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
    private const string UidCursorPrefix = "imap-uid:2:";
    private const int NotificationPreviewFetchByteLimit = 8192;
    private const int NotificationPreviewCharacterLimit = 280;
    private const int ChronologyFetchBatchSize = 1000;
    internal const int BackgroundSnapshotMessageLimit = 100;
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
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(MailReadFailureKind.ConnectionFailed, "Не удалось получить число непрочитанных писем.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task<ImapInboxTechnicalSnapshot> GetInboxTechnicalSnapshotAsync(
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
            int unreadCount = Math.Max(0, inbox.Unread);
            FolderAccess access = await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (access != FolderAccess.ReadOnly)
            {
                throw new MailReadException(
                    MailReadFailureKind.ConnectionFailed,
                    "Сервер не открыл папку только для чтения.");
            }

            if (inbox.Count == 0)
            {
                return new ImapInboxTechnicalSnapshot(unreadCount, inbox.UidValidity, []);
            }

            int startIndex = Math.Max(0, inbox.Count - BackgroundSnapshotMessageLimit);
            IList<IMessageSummary> fetched = await inbox.FetchAsync(
                startIndex,
                inbox.Count - 1,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope,
                cancellationToken);
            uint[] uniqueIds = fetched
                .Where(summary => summary.UniqueId.IsValid)
                .Select(summary => summary.UniqueId.Id)
                .Distinct()
                .ToArray();
            ImapInboxTechnicalSnapshot result = new(
                unreadCount,
                inbox.UidValidity,
                uniqueIds);
            result = result with
            {
                NotificationPreviews = fetched
                    .Where(summary => summary.UniqueId.IsValid)
                    .ToDictionary(
                        summary => summary.UniqueId.Id,
                        MapNotificationPreview)
            };
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailReadException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                "Не удалось проверить новые письма.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task<MailNotificationPreview?> GetInboxNotificationPreviewAsync(
        MailServerSettings server,
        string secret,
        uint uniqueId,
        uint expectedUidValidity,
        CancellationToken cancellationToken = default)
    {
        if (uniqueId == 0)
        {
            return null;
        }

        using ImapClient client = new();
        try
        {
            await ConnectAndAuthenticateAsync(client, server, secret, cancellationToken);
            IMailFolder inbox = client.Inbox;
            FolderAccess access = await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (access != FolderAccess.ReadOnly)
            {
                return null;
            }

            EnsureUidValidity(inbox, expectedUidValidity);
            UniqueId uid = new(uniqueId);
            IList<IMessageSummary> summaries = await inbox.FetchAsync(
                [uid],
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure,
                cancellationToken);
            IMessageSummary? summary = summaries.FirstOrDefault(item => item.UniqueId == uid);
            BodyPartText? textPart = FindNotificationPreviewTextPart(summary?.Body);
            if (summary is null || textPart is null)
            {
                return null;
            }

            string section = ResolveNotificationPreviewSection(textPart);
            using Stream bodyStream = await inbox.GetStreamAsync(
                uid,
                section,
                0,
                NotificationPreviewFetchByteLimit,
                cancellationToken,
                null);
            string snippet = ExtractNotificationPreviewText(textPart, await ReadLimitedBytesAsync(bodyStream, cancellationToken));
            if (string.IsNullOrWhiteSpace(snippet))
            {
                return null;
            }

            MailNotificationPreview envelope = MapNotificationPreview(summary);
            return envelope with { Snippet = snippet };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailReadException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                "Не удалось получить предпросмотр нового письма.");
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
            if (client.GetFolder(SpecialFolder.Archive) is IMailFolder archive
                && DescribeArchiveFolder(archive.FullName, client.Inbox.FullName,
                    archive.Attributes, client.Capabilities) is ImapFolderDescriptor descriptor)
            {
                folders.Add(descriptor);
            }
            return folders;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    internal static ImapFolderDescriptor? DescribeArchiveFolder(
        string fullName, string inboxName, FolderAttributes attributes, ImapCapabilities capabilities)
    {
        // A display name is not an archive contract. Only an actual, selectable
        // server-designated archive may be offered as a destination.
        const FolderAttributes conflicting = FolderAttributes.NoSelect | FolderAttributes.NonExistent
            | FolderAttributes.Inbox | FolderAttributes.All | FolderAttributes.Junk
            | FolderAttributes.Trash | FolderAttributes.Sent | FolderAttributes.Drafts;
        if (!attributes.HasFlag(FolderAttributes.Archive) || (attributes & conflicting) != 0
            || string.IsNullOrWhiteSpace(fullName)
            || string.Equals(fullName, inboxName, StringComparison.Ordinal))
        {
            return null;
        }

        return new(MailFolderKind.Archive, fullName)
        {
            CanAcceptArchive = (capabilities & (ImapCapabilities.Move | ImapCapabilities.UidPlus)) != 0
        };
    }

    public async Task<ImapInboxPageData> GetUidSafeFolderPageAsync(
        MailServerSettings server,
        string secret,
        ImapFolderDescriptor folder,
        string? query,
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

            string normalizedQuery = query?.Trim() ?? string.Empty;
            string scope = CreateUidCursorScope(folder.FullName, normalizedQuery);
            SearchQuery searchQuery = normalizedQuery.Length == 0
                ? SearchQuery.All
                : SearchQuery.MessageContains(normalizedQuery);
            ImapUidPageCursor? parsed = ParseUidCursor(cursor, scope);
            uint snapshotMaxUid;
            long totalCount;
            long? displayedTotalCount;
            IList<UniqueId>? initialSnapshotMatches = null;
            if (parsed is ImapUidPageCursor continuation)
            {
                EnsureUidCursorValidity(continuation, mailFolder.UidValidity);

                snapshotMaxUid = continuation.SnapshotMaxUid;
                totalCount = continuation.TotalCount;
                // The initial snapshot total remains truthful for navigation.
                // Local mailbox mutations adjust the displayed count in the VM;
                // avoid overwriting that adjustment with a stale cursor value.
                displayedTotalCount = null;
            }
            else
            {
                UniqueId? uidNext = mailFolder.UidNext;
                snapshotMaxUid = uidNext is { IsValid: true } && uidNext.Value.Id > 1
                    ? uidNext.Value.Id - 1
                    : 0;
                if (snapshotMaxUid == 0)
                {
                    initialSnapshotMatches = await mailFolder.SearchAsync(searchQuery, cancellationToken);
                    snapshotMaxUid = initialSnapshotMatches
                        .Where(uid => uid.IsValid)
                        .Select(uid => uid.Id)
                        .DefaultIfEmpty()
                        .Max();
                }
                totalCount = 0;
                displayedTotalCount = 0;
            }

            if (snapshotMaxUid == 0)
            {
                return new ImapInboxPageData([], null, displayedTotalCount);
            }

            UniqueIdRange snapshotRange = new(new UniqueId(1), new UniqueId(snapshotMaxUid));
            IList<UniqueId> matches = initialSnapshotMatches ?? await mailFolder.SearchAsync(
                    snapshotRange,
                    searchQuery,
                    cancellationToken);
            UniqueId[] snapshotUids = matches
                .Where(uid => uid.IsValid && uid.Id <= snapshotMaxUid)
                .Distinct()
                .ToArray();
            if (parsed is null)
            {
                totalCount = snapshotUids.LongLength;
                displayedTotalCount = totalCount;
            }

            if (snapshotUids.Length == 0)
            {
                return new ImapInboxPageData([], null, displayedTotalCount);
            }

            IReadOnlyList<ImapChronologyEntry> chronology = await FetchChronologyAsync(
                mailFolder,
                snapshotUids,
                cancellationToken);
            IReadOnlyList<ImapChronologyEntry> candidates = SelectChronologicalPage(
                chronology,
                parsed,
                pageSize + 1);
            ImapChronologyEntry[] pageEntries = candidates.Take(pageSize).ToArray();
            UniqueId[] pageUids = pageEntries.Select(entry => new UniqueId(entry.UniqueId)).ToArray();
            if (pageUids.Length == 0)
            {
                return new ImapInboxPageData([], null, displayedTotalCount);
            }

            IList<IMessageSummary> fetched = await mailFolder.FetchAsync(
                pageUids,
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
            bool hasMore = candidates.Count > pageSize;
            ImapChronologyEntry last = pageEntries[^1];
            string? nextCursor = hasMore
                ? CreateUidCursor(
                    scope,
                    mailFolder.UidValidity,
                    snapshotMaxUid,
                    last.ReceivedAt.UtcTicks,
                    last.UniqueId,
                    totalCount)
                : null;
            return new ImapInboxPageData(summaries, nextCursor, displayedTotalCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailReadException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedConnectionException(exception))
        {
            throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                string.IsNullOrWhiteSpace(query)
                    ? "Не удалось загрузить почту. Проверьте подключение к сети."
                    : "Не удалось выполнить поиск. Проверьте подключение к сети.");
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    private static async Task<IReadOnlyList<ImapChronologyEntry>> FetchChronologyAsync(
        IMailFolder folder,
        IReadOnlyList<UniqueId> uniqueIds,
        CancellationToken cancellationToken)
    {
        List<ImapChronologyEntry> chronology = new(uniqueIds.Count);
        for (int offset = 0; offset < uniqueIds.Count; offset += ChronologyFetchBatchSize)
        {
            UniqueId[] batch = uniqueIds.Skip(offset).Take(ChronologyFetchBatchSize).ToArray();
            IList<IMessageSummary> summaries = await folder.FetchAsync(
                batch,
                MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope,
                cancellationToken);
            chronology.AddRange(summaries
                .Where(summary => summary.UniqueId.IsValid)
                .Select(summary => new ImapChronologyEntry(
                    summary.UniqueId.Id,
                    summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.MinValue)));
        }

        return chronology;
    }

    internal static IReadOnlyList<ImapChronologyEntry> SelectChronologicalPage(
        IEnumerable<ImapChronologyEntry> entries,
        ImapUidPageCursor? cursor,
        int requestedCount) =>
        entries
            .DistinctBy(entry => entry.UniqueId)
            .Where(entry => cursor is not ImapUidPageCursor continuation
                || entry.ReceivedAt.UtcTicks < continuation.AnchorUtcTicks
                || (entry.ReceivedAt.UtcTicks == continuation.AnchorUtcTicks
                    && entry.UniqueId < continuation.AnchorUid))
            .OrderByDescending(entry => entry.ReceivedAt)
            .ThenByDescending(entry => entry.UniqueId)
            .Take(Math.Max(0, requestedCount))
            .ToArray();

    internal static string CreateUidCursorScope(string folderLocator, string query)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(folderLocator + "\0" + query));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    internal static string CreateUidCursor(
        string scope,
        uint uidValidity,
        uint snapshotMaxUid,
        long anchorUtcTicks,
        uint anchorUid,
        long totalCount) =>
        string.Join(
            ':',
            UidCursorPrefix.TrimEnd(':'),
            scope,
            uidValidity.ToString(CultureInfo.InvariantCulture),
            snapshotMaxUid.ToString(CultureInfo.InvariantCulture),
            anchorUtcTicks.ToString(CultureInfo.InvariantCulture),
            anchorUid.ToString(CultureInfo.InvariantCulture),
            totalCount.ToString(CultureInfo.InvariantCulture));

    internal static ImapUidPageCursor? ParseUidCursor(string? cursor, string expectedScope)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string[] parts = cursor.Split(':');
        if (parts.Length != 8
            || !string.Equals(parts[0], "imap-uid", StringComparison.Ordinal)
            || !string.Equals(parts[1], "2", StringComparison.Ordinal)
            || !string.Equals(parts[2], expectedScope, StringComparison.Ordinal)
            || !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint uidValidity)
            || !uint.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out uint snapshotMaxUid)
            || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out long anchorUtcTicks)
            || !uint.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out uint anchorUid)
            || !long.TryParse(parts[7], NumberStyles.None, CultureInfo.InvariantCulture, out long totalCount)
            || uidValidity == 0
            || snapshotMaxUid == 0
            || anchorUtcTicks < DateTimeOffset.MinValue.UtcTicks
            || anchorUtcTicks > DateTimeOffset.MaxValue.UtcTicks
            || anchorUid == 0
            || anchorUid > snapshotMaxUid
            || totalCount < 0)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Не удалось продолжить загрузку списка писем. Обновите папку.");
        }

        return new ImapUidPageCursor(uidValidity, snapshotMaxUid, anchorUtcTicks, anchorUid, totalCount);
    }

    internal static void EnsureUidCursorValidity(ImapUidPageCursor cursor, uint currentUidValidity)
    {
        if (cursor.UidValidity != currentUidValidity)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Состав папки изменился. Обновите её, чтобы начать загрузку заново.");
        }
    }

    internal readonly record struct ImapUidPageCursor(
        uint UidValidity,
        uint SnapshotMaxUid,
        long AnchorUtcTicks,
        uint AnchorUid,
        long TotalCount);

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

    private static MailNotificationPreview MapNotificationPreview(IMessageSummary summary)
    {
        (string displayName, string address) = MailContentExtractor.GetPrimaryMailbox(summary.Envelope?.From);
        return new MailNotificationPreview(
            displayName,
            address,
            MailContentExtractor.NormalizeSubject(summary.Envelope?.Subject),
            string.Empty);
    }

    internal static BodyPartText? FindNotificationPreviewTextPart(BodyPart? root)
    {
        if (root is null)
        {
            return null;
        }

        List<BodyPartText> plainTextParts = [];
        List<BodyPartText> htmlTextParts = [];
        CollectNotificationPreviewTextParts(root, plainTextParts, htmlTextParts);
        return plainTextParts.FirstOrDefault() ?? htmlTextParts.FirstOrDefault();
    }

    internal static string ResolveNotificationPreviewSection(BodyPartText textPart) =>
        string.IsNullOrWhiteSpace(textPart.PartSpecifier) ? "TEXT" : textPart.PartSpecifier;

    private static void CollectNotificationPreviewTextParts(
        BodyPart part,
        List<BodyPartText> plainTextParts,
        List<BodyPartText> htmlTextParts)
    {
        if ((part is BodyPartBasic basic && (basic.IsAttachment || basic.ContentDisposition?.IsAttachment == true))
            || (part is BodyPartMultipart multipartContainer && multipartContainer.ContentDisposition?.IsAttachment == true)
            || part is BodyPartMessage
            || IsTechnicalNotificationPart(part))
        {
            return;
        }

        if (part is BodyPartText text)
        {
            if (text.IsPlain)
            {
                plainTextParts.Add(text);
            }
            else if (text.IsHtml)
            {
                htmlTextParts.Add(text);
            }

            return;
        }

        if (part is BodyPartMultipart multipart)
        {
            foreach (BodyPart child in multipart.BodyParts)
            {
                CollectNotificationPreviewTextParts(child, plainTextParts, htmlTextParts);
            }
        }
    }

    private static bool IsTechnicalNotificationPart(BodyPart part)
    {
        string mimeType = part.ContentType?.MimeType ?? string.Empty;
        return mimeType.Equals("multipart/report", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("message/delivery-status", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("message/disposition-notification", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("text/rfc822-headers", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ExtractNotificationPreviewText(BodyPartText textPart, ReadOnlySpan<byte> bodyBytes)
    {
        if (textPart is null || bodyBytes.IsEmpty)
        {
            return string.Empty;
        }

        if (!TryDecodeTransferEncoding(textPart.ContentTransferEncoding, bodyBytes, out byte[] transferDecoded)
            || !TryDecodePreviewCharset(textPart.ContentType?.Charset, transferDecoded, out string decodedText))
        {
            return string.Empty;
        }

        try
        {
            string visible = textPart.IsHtml
                ? MailContentExtractor.ExtractSafePlainText(decodedText)
                : MailContentExtractor.NormalizePlainText(decodedText);
            visible = RemoveLeadingTransportHeaders(visible);
            return TruncateNotificationPreview(MailContentExtractor.NormalizePreview(visible));
        }
        catch (Exception)
        {
            // The source body is deliberately partial. Bad MIME must only remove the optional preview.
            return string.Empty;
        }
    }

    private static bool TryDecodeTransferEncoding(
        string? transferEncoding,
        ReadOnlySpan<byte> source,
        out byte[] decoded)
    {
        string kind = transferEncoding?.Trim() ?? string.Empty;
        if (kind.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeBoundedBase64(source, out decoded);
        }

        if (kind.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeBoundedQuotedPrintable(source, out decoded);
        }

        if (kind.Length == 0
            || kind.Equals("7bit", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("8bit", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("binary", StringComparison.OrdinalIgnoreCase))
        {
            decoded = source.ToArray();
            return true;
        }

        decoded = [];
        return false;
    }

    private static bool TryDecodeBoundedBase64(ReadOnlySpan<byte> source, out byte[] decoded)
    {
        List<byte> encoded = new(source.Length);
        foreach (byte value in source)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            if ((value is >= (byte)'A' and <= (byte)'Z')
                || (value is >= (byte)'a' and <= (byte)'z')
                || (value is >= (byte)'0' and <= (byte)'9')
                || value is (byte)'+' or (byte)'/' or (byte)'=')
            {
                encoded.Add(value);
                continue;
            }

            decoded = [];
            return false;
        }

        List<byte> result = new((encoded.Count / 4) * 3);
        Span<char> quartet = stackalloc char[4];
        Span<byte> block = stackalloc byte[3];
        for (int index = 0; index + 4 <= encoded.Count; index += 4)
        {
            for (int offset = 0; offset < quartet.Length; offset++)
            {
                quartet[offset] = (char)encoded[index + offset];
            }

            bool hasPadding = quartet.IndexOf('=') >= 0;
            if (hasPadding && index + 4 != encoded.Count)
            {
                decoded = [];
                return false;
            }

            if (!Convert.TryFromBase64Chars(quartet, block, out int decodedCount))
            {
                decoded = [];
                return false;
            }

            for (int offset = 0; offset < decodedCount; offset++)
            {
                result.Add(block[offset]);
            }
        }

        decoded = result.ToArray();
        return decoded.Length > 0;
    }

    private static bool TryDecodeBoundedQuotedPrintable(ReadOnlySpan<byte> source, out byte[] decoded)
    {
        List<byte> result = new(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            byte value = source[index];
            if (value != (byte)'=')
            {
                result.Add(value);
                continue;
            }

            if (index + 1 >= source.Length)
            {
                break;
            }

            byte next = source[index + 1];
            if (next == (byte)'\n')
            {
                index++;
                continue;
            }

            if (next == (byte)'\r')
            {
                if (index + 2 >= source.Length)
                {
                    break;
                }

                if (source[index + 2] != (byte)'\n')
                {
                    decoded = [];
                    return false;
                }

                index += 2;
                continue;
            }

            if (index + 2 >= source.Length)
            {
                break;
            }

            int high = GetHexValue(next);
            int low = GetHexValue(source[index + 2]);
            if (high < 0 || low < 0)
            {
                decoded = [];
                return false;
            }

            result.Add((byte)((high << 4) | low));
            index += 2;
        }

        decoded = result.ToArray();
        return decoded.Length > 0;
    }

    private static int GetHexValue(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' ? value - (byte)'0'
        : value is >= (byte)'A' and <= (byte)'F' ? value - (byte)'A' + 10
        : value is >= (byte)'a' and <= (byte)'f' ? value - (byte)'a' + 10
        : -1;

    private static bool TryDecodePreviewCharset(string? charset, byte[] source, out string text)
    {
        text = string.Empty;
        if (source.Length == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(charset))
        {
            return TryDecodeWithEncoding(Encoding.UTF8, TrimIncompleteCharacterTail("utf-8", source), out text)
                || (source.All(value => value <= 0x7f)
                    && TryDecodeWithEncoding(Encoding.ASCII, source, out text));
        }

        Encoding? encoding = ResolvePreviewEncoding(charset);
        return encoding is not null
            && TryDecodeWithEncoding(encoding, TrimIncompleteCharacterTail(charset, source), out text);
    }

    private static bool TryDecodeWithEncoding(Encoding encoding, byte[] source, out string text)
    {
        text = string.Empty;
        if (source.Length == 0)
        {
            return false;
        }

        try
        {
            string decoded = encoding.GetString(source);
            if (decoded.Contains('\ufffd'))
            {
                return false;
            }

            text = decoded;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] TrimIncompleteCharacterTail(string charset, byte[] source)
    {
        if (charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            return TrimIncompleteUtf8Tail(source);
        }

        if (charset.Equals("utf-16", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf-16le", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf-16be", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("unicode", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("bigendianunicode", StringComparison.OrdinalIgnoreCase))
        {
            return source.Length % 2 == 0 ? source : source[..^1];
        }

        if (charset.Equals("utf-32", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf-32le", StringComparison.OrdinalIgnoreCase)
            || charset.Equals("utf-32be", StringComparison.OrdinalIgnoreCase))
        {
            int remainder = source.Length % 4;
            return remainder == 0 ? source : source[..^remainder];
        }

        return source;
    }

    private static byte[] TrimIncompleteUtf8Tail(byte[] source)
    {
        int continuationCount = 0;
        int leadingIndex = source.Length - 1;
        while (leadingIndex >= 0 && source[leadingIndex] is >= 0x80 and <= 0xbf)
        {
            continuationCount++;
            leadingIndex--;
        }

        if (leadingIndex < 0)
        {
            return [];
        }

        byte leading = source[leadingIndex];
        int expectedContinuationCount = leading is >= 0xc2 and <= 0xdf ? 1
            : leading is >= 0xe0 and <= 0xef ? 2
            : leading is >= 0xf0 and <= 0xf4 ? 3
            : -1;
        return expectedContinuationCount >= 0 && continuationCount < expectedContinuationCount
            ? source[..leadingIndex]
            : source;
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[NotificationPreviewFetchByteLimit];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead == buffer.Length ? buffer : buffer[..totalRead];
    }

    private static string TruncateNotificationPreview(string value) =>
        value.Length <= NotificationPreviewCharacterLimit
            ? value
            : value[..NotificationPreviewCharacterLimit].TrimEnd() + "…";

    private static string RemoveLeadingTransportHeaders(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length == 0 || !IsTransportHeader(lines[0]))
        {
            return value;
        }

        int index = 1;
        for (; index < lines.Length; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Join("\n", lines[(index + 1)..]).Trim();
            }

            if (IsTransportHeader(line) || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            // A partial header block without a terminating blank line is not safe preview text.
            return string.Empty;
        }

        return string.Empty;
    }

    private static bool IsTransportHeader(string line)
    {
        int separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        return line[..separator].Trim() switch
        {
            var name when name.Equals("Received", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("Return-Path", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("Delivered-To", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    private static Encoding? ResolvePreviewEncoding(string charset)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(
                charset,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
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
        try
        {
            await client.AuthenticateAsync(server.Username, secret, cancellationToken);
        }
        catch (MailKit.Security.AuthenticationException)
        {
            throw new MailReadException(
                MailReadFailureKind.AuthenticationFailed,
                "Не удалось войти в почту. Проверьте пароль приложения.");
        }
    }

    private static bool IsExpectedConnectionException(Exception exception) =>
        exception is IOException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or System.Security.Authentication.AuthenticationException
            or MailKit.Security.AuthenticationException
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
