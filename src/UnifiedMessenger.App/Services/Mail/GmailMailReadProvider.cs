using System.IO;
using System.Net;
using System.Net.Http;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MimeKit;
using UnifiedMessenger.App.Models;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;
using GmailMessagePart = Google.Apis.Gmail.v1.Data.MessagePart;

namespace UnifiedMessenger.App.Services.Mail;

internal sealed record GmailApiSummaryData(
    string Id,
    string? Subject,
    string? From,
    long? InternalDate,
    string? Snippet,
    IReadOnlyList<string> LabelIds)
{
    public MailMessageAttachmentSummary AttachmentSummary { get; init; } = MailMessageAttachmentSummary.Empty;
}

internal sealed record GmailApiInboxPage(
    IReadOnlyList<GmailApiSummaryData> Items,
    string? NextPageToken);

internal sealed record GmailApiInboxTechnicalSnapshot(
    int UnreadCount,
    IReadOnlyList<string> MessageIds);

internal sealed record GmailApiRawMessage(byte[] RawMime, bool IsUnread, string? ThreadId = null);
internal sealed record GmailApiUserLabel(string Id, string Name);
internal sealed record GmailApiTrashResult(
    IReadOnlySet<string> SucceededMessageIds,
    IReadOnlyDictionary<string, Exception> FailedMessages);

internal interface IGmailApiReadClient
{
    Task<GmailApiInboxTechnicalSnapshot> GetInboxTechnicalSnapshotAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GmailApiInboxTechnicalSnapshot>(new NotSupportedException());

    Task<int> GetInboxUnreadCountAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    Task<GmailApiInboxPage> GetInboxPageAsync(
        MailCredential credential,
        Guid accountId,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<GmailApiRawMessage> GetRawMessageAsync(
        MailCredential credential,
        Guid accountId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetSystemLabelIdsAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(
            new HashSet<string>(GmailSystemFolders.LabelIds, StringComparer.Ordinal));

    Task<GmailApiInboxPage> GetFolderPageAsync(
        MailCredential credential,
        Guid accountId,
        string labelId,
        bool includeSpamTrash,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(labelId, GmailSystemFolders.Inbox, StringComparison.Ordinal))
        {
            throw new MailReadException(MailReadFailureKind.FolderUnavailable, "Эта папка Gmail недоступна.");
        }

        return GetInboxPageAsync(credential, accountId, pageToken, pageSize, cancellationToken);
    }

    Task SetReadStateAsync(
        MailCredential credential,
        Guid accountId,
        string messageId,
        bool isRead,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new MailReadException(
                MailReadFailureKind.MutationFailed,
                "Не удалось изменить статус письма Gmail."));

}

internal interface IGmailMailboxApiClient
{
    Task<IReadOnlyList<GmailApiUserLabel>> GetUserLabelsAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GmailApiUserLabel>>([]);

    Task ModifyLabelsAsync(
        MailCredential credential,
        Guid accountId,
        IReadOnlyCollection<string> messageIds,
        IReadOnlyCollection<string> addLabelIds,
        IReadOnlyCollection<string> removeLabelIds,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException());

    Task<GmailApiTrashResult> MoveToTrashAsync(
        MailCredential credential,
        Guid accountId,
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GmailApiTrashResult(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, Exception>(StringComparer.Ordinal)));
}

internal static class GmailSystemFolders
{
    public const string Inbox = "INBOX";
    public const string Starred = "STARRED";
    public const string Sent = "SENT";
    public const string Draft = "DRAFT";
    public const string Spam = "SPAM";
    public const string Trash = "TRASH";
    public const string Unread = "UNREAD";

    public static IReadOnlyList<string> LabelIds { get; } = [Inbox, Starred, Sent, Draft, Spam, Trash];

    public static IReadOnlyList<MailFolder> Map(IReadOnlySet<string> availableLabels)
    {
        (MailFolderKind Kind, string Label)[] definitions =
        [
            (MailFolderKind.Inbox, Inbox),
            (MailFolderKind.Starred, Starred),
            (MailFolderKind.Sent, Sent),
            (MailFolderKind.Drafts, Draft),
            (MailFolderKind.Spam, Spam),
            (MailFolderKind.Trash, Trash)
        ];
        return definitions
            .Where(item => availableLabels.Contains(item.Label))
            .Select(item => MailFolderCatalog.Create(item.Kind, item.Label))
            .ToArray();
    }
}

internal sealed class GmailMailReadProvider(
    IMailCredentialStore credentialStore,
    IGmailApiReadClient apiClient,
    IMailContentExtractor contentExtractor,
    MailMessageSourceCache? sourceCache = null) : IMailReadProvider, IMailMessageStateProvider, IMailAttachmentContentProvider, IMailInboxUnreadCountProvider, IMailInboxTechnicalSnapshotProvider
{
    private const string MessageKeyPrefix = "gmail:";
    private readonly MailMessageSourceCache _sourceCache = sourceCache ?? new MailMessageSourceCache();

    public bool Supports(MailProviderType providerType) => providerType == MailProviderType.Gmail;

    public async Task<int> GetInboxUnreadCountAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        return await apiClient.GetInboxUnreadCountAsync(credential, account.Id, cancellationToken);
    }

    async Task<MailInboxTechnicalSnapshot> IMailInboxTechnicalSnapshotProvider.GetInboxTechnicalSnapshotAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        ValidateAccount(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        GmailApiInboxTechnicalSnapshot snapshot = await apiClient
            .GetInboxTechnicalSnapshotAsync(credential, account.Id, cancellationToken);
        return new MailInboxTechnicalSnapshot(
            snapshot.UnreadCount,
            "gmail-inbox",
            snapshot.MessageIds);
    }

    public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
        MailAccount account,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetPageAsync(
            account,
            MailFolderCatalog.Create(MailFolderKind.Inbox, GmailSystemFolders.Inbox),
            continuationToken,
            pageSize,
            cancellationToken);

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        IReadOnlySet<string> labels = await apiClient.GetSystemLabelIdsAsync(
            credential,
            account.Id,
            cancellationToken);
        return GmailSystemFolders.Map(labels);
    }

    public async Task<MailPage<MailMessageSummary>> GetPageAsync(
        MailAccount account,
        MailFolder folder,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize);
        ValidateFolder(folder);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        GmailApiInboxPage page = await apiClient.GetFolderPageAsync(
            credential,
            account.Id,
            folder.ProviderLocator,
            folder.Kind is MailFolderKind.Spam or MailFolderKind.Trash,
            continuationToken,
            pageSize,
            cancellationToken);
        return new MailPage<MailMessageSummary>(page.Items.Select(MapSummary).ToArray(), page.NextPageToken);
    }

    public Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        string messageKey,
        CancellationToken cancellationToken = default) =>
        GetMessageAsync(
            account,
            MailFolderCatalog.Create(MailFolderKind.Inbox, GmailSystemFolders.Inbox),
            messageKey,
            cancellationToken);

    public async Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        MailFolder folder,
        string messageKey,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize: 1);
        ValidateFolder(folder);
        string messageId = ParseMessageKey(messageKey);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        GmailApiRawMessage raw = await apiClient.GetRawMessageAsync(
            credential,
            account.Id,
            messageId,
            cancellationToken);

        try
        {
            using MemoryStream stream = new(raw.RawMime, writable: false);
            MimeMessage message = await MimeMessage.LoadAsync(stream, cancellationToken);
            MailMessageContent content = contentExtractor.Extract(messageKey, message, raw.IsUnread);
            if (content.Attachments.Count > 0)
            {
                _sourceCache.Set(account.Id, messageKey, message, raw.RawMime.LongLength);
            }
            if (!string.IsNullOrWhiteSpace(raw.ThreadId))
            {
                MailReplyMetadata metadata = content.ReplyMetadata
                    ?? new MailReplyMetadata(string.Empty, null, []);
                content = content with
                {
                    ReplyMetadata = metadata with { ProviderThreadId = raw.ThreadId }
                };
            }

            return content;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or IOException)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidMessage,
                "Не удалось безопасно прочитать содержимое письма.");
        }
    }

    public async Task<MailAttachmentContent> GetAsync(
        MailAccount account,
        string messageKey,
        string attachmentKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAccount(account, pageSize: 1);
            if (_sourceCache.TryGet(account.Id, messageKey, out MimeMessage? cached) && cached is not null)
            {
                return MailMimeAttachmentCatalog.GetContent(cached, attachmentKey, cancellationToken);
            }

            string messageId = ParseMessageKey(messageKey);
            MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
            GmailApiRawMessage raw = await apiClient.GetRawMessageAsync(
                credential,
                account.Id,
                messageId,
                cancellationToken);
            using MemoryStream stream = new(raw.RawMime, writable: false);
            MimeMessage message = await MimeMessage.LoadAsync(stream, cancellationToken);
            _sourceCache.Set(account.Id, messageKey, message, raw.RawMime.LongLength);
            return MailMimeAttachmentCatalog.GetContent(message, attachmentKey, cancellationToken);
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

    public async Task<MailReadStateCapability> GetReadStateCapabilityAsync(
        MailAccount account,
        MailFolder folder,
        CancellationToken cancellationToken = default)
    {
        if (!folder.SupportsReadState)
        {
            return MailReadStateCapability.Unsupported;
        }

        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        return credential.HasGmailModifyScope
            ? MailReadStateCapability.Available
            : new MailReadStateCapability(
                false,
                true,
                "Чтобы менять статус писем, нужно снова разрешить доступ Google.");
    }

    public async Task SetReadStateAsync(
        MailAccount account,
        MailFolder folder,
        string messageKey,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize: 1);
        ValidateFolder(folder);
        if (!folder.SupportsReadState)
        {
            throw new MailReadException(MailReadFailureKind.MutationFailed, "Для этой папки действие недоступно.");
        }

        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        if (!credential.HasGmailModifyScope)
        {
            throw new MailReadException(
                MailReadFailureKind.MutationNotAuthorized,
                "Чтобы менять статус писем, нужно снова разрешить доступ Google.");
        }

        await apiClient.SetReadStateAsync(
            credential,
            account.Id,
            ParseMessageKey(messageKey),
            isRead,
            cancellationToken);
    }

    internal static MailMessageSummary MapSummary(GmailApiSummaryData item)
    {
        InternetAddressList? addresses = TryParseAddresses(item.From);
        (string name, string address) = MailContentExtractor.GetPrimaryMailbox(addresses);
        DateTimeOffset receivedAt = item.InternalDate is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(item.InternalDate.Value)
            : DateTimeOffset.MinValue;
        return new MailMessageSummary(
            MessageKeyPrefix + item.Id,
            MailContentExtractor.NormalizeSubject(item.Subject),
            name,
            address,
            receivedAt,
            MailContentExtractor.NormalizePreview(WebUtility.HtmlDecode(item.Snippet ?? string.Empty)),
            item.LabelIds.Contains(GmailSystemFolders.Unread, StringComparer.Ordinal))
        {
            AttachmentSummary = item.AttachmentSummary,
            IsStarred = item.LabelIds.Contains(GmailSystemFolders.Starred, StringComparer.Ordinal),
            ProviderLabelIds = item.LabelIds.ToHashSet(StringComparer.Ordinal)
        };
    }

    private async Task<MailCredential> LoadCredentialAsync(MailAccount account, CancellationToken cancellationToken)
    {
        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.GmailOAuthRefreshToken } || !credential.IsValid())
        {
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");
        }

        return credential;
    }

    private static void ValidateAccount(MailAccount account, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Provider != MailProviderType.Gmail || pageSize is < 1 or > 100)
        {
            throw new MailReadException(MailReadFailureKind.InvalidConfiguration, "Почтовый аккаунт настроен некорректно.");
        }
    }

    private static void ValidateFolder(MailFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!GmailSystemFolders.LabelIds.Contains(folder.ProviderLocator, StringComparer.Ordinal))
        {
            throw new MailReadException(MailReadFailureKind.FolderUnavailable, "Эта папка Gmail недоступна.");
        }
    }

    internal static string ParseMessageKey(string messageKey)
    {
        if (string.IsNullOrWhiteSpace(messageKey)
            || !messageKey.StartsWith(MessageKeyPrefix, StringComparison.Ordinal)
            || messageKey.Length == MessageKeyPrefix.Length)
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, "Письмо больше недоступно.");
        }

        return messageKey[MessageKeyPrefix.Length..];
    }

    private static InternetAddressList? TryParseAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return InternetAddressList.Parse(value);
        }
        catch (ParseException)
        {
            return null;
        }
    }
}

internal sealed class GmailApiReadClient : IGmailApiReadClient, IGmailMailboxApiClient
{
    internal const int MaximumMetadataConcurrency = 5;
    internal const int MaximumTrashConcurrency = 4;
    internal const int MetadataMimeTreeDepth = 8;
    internal const int BackgroundSnapshotMessageLimit = 100;
    internal const string InboxLabel = GmailSystemFolders.Inbox;
    internal static string MetadataFieldsProjection { get; } = BuildMetadataFieldsProjection();

    public Task<GmailApiInboxPage> GetInboxPageAsync(
        MailCredential credential,
        Guid accountId,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetFolderPageAsync(credential, accountId, InboxLabel, false, pageToken, pageSize, cancellationToken);

    public async Task<int> GetInboxUnreadCountAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            Google.Apis.Gmail.v1.Data.Label inbox = await session.Service.Users.Labels.Get("me", GmailSystemFolders.Inbox)
                .ExecuteAsync(cancellationToken);
            long unread = inbox.MessagesUnread ?? 0;
            return unread >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)unread);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapListException(exception);
        }
    }

    public async Task<GmailApiInboxTechnicalSnapshot> GetInboxTechnicalSnapshotAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(
                credential,
                accountId,
                cancellationToken);
            GmailService service = session.Service;
            Google.Apis.Gmail.v1.Data.Label inbox = await service.Users.Labels
                .Get("me", GmailSystemFolders.Inbox)
                .ExecuteAsync(cancellationToken);

            UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");
            request.LabelIds = new[] { GmailSystemFolders.Inbox };
            request.IncludeSpamTrash = false;
            request.MaxResults = BackgroundSnapshotMessageLimit;
            request.Fields = "messages/id";
            ListMessagesResponse response = await request.ExecuteAsync(cancellationToken);
            string[] messageIds = response.Messages?
                .Select(message => message.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
                ?? [];
            long unread = inbox.MessagesUnread ?? 0;
            int unreadCount = unread >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)unread);
            return new GmailApiInboxTechnicalSnapshot(unreadCount, messageIds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapListException(exception);
        }
    }

    public async Task<IReadOnlySet<string>> GetSystemLabelIdsAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            ListLabelsResponse response = await session.Service.Users.Labels.List("me").ExecuteAsync(cancellationToken);
            return response.Labels?
                .Where(label => label.Type is "system" && !string.IsNullOrWhiteSpace(label.Id))
                .Select(label => label.Id)
                .ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapListException(exception);
        }
    }

    public async Task<GmailApiInboxPage> GetFolderPageAsync(
        MailCredential credential,
        Guid accountId,
        string labelId,
        bool includeSpamTrash,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            GmailService service = session.Service;
            UsersResource.MessagesResource.ListRequest listRequest = service.Users.Messages.List("me");
            listRequest.LabelIds = new[] { labelId };
            listRequest.IncludeSpamTrash = includeSpamTrash;
            listRequest.MaxResults = pageSize;
            listRequest.PageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
            ListMessagesResponse response = await listRequest.ExecuteAsync(cancellationToken);
            GmailMessage[] listed = response.Messages?.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToArray() ?? [];

            using SemaphoreSlim gate = new(MaximumMetadataConcurrency, MaximumMetadataConcurrency);
            Task<(int Index, GmailApiSummaryData Summary)>[] tasks = listed
                .Select((item, index) => LoadMetadataAsync(service, item.Id, index, gate, cancellationToken))
                .ToArray();
            (int Index, GmailApiSummaryData Summary)[] metadata = await Task.WhenAll(tasks);
            return new GmailApiInboxPage(
                metadata.OrderBy(item => item.Index).Select(item => item.Summary).ToArray(),
                response.NextPageToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapListException(exception);
        }
    }

    public async Task<GmailApiRawMessage> GetRawMessageAsync(
        MailCredential credential,
        Guid accountId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            GmailService service = session.Service;
            UsersResource.MessagesResource.GetRequest request = service.Users.Messages.Get("me", messageId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
            GmailMessage response = await request.ExecuteAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(response.Raw))
            {
                throw new MailReadException(MailReadFailureKind.InvalidMessage, "Не удалось безопасно прочитать содержимое письма.");
            }

            return new GmailApiRawMessage(
                DecodeBase64Url(response.Raw),
                response.LabelIds?.Contains(GmailSystemFolders.Unread, StringComparer.Ordinal) == true,
                response.ThreadId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailReadException)
        {
            throw;
        }
        catch (Exception exception) when (GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");
        }
        catch (Exception exception) when (IsExpectedApiException(exception))
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, "Не удалось загрузить выбранное письмо.");
        }
    }

    public async Task SetReadStateAsync(
        MailCredential credential,
        Guid accountId,
        string messageId,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ModifyLabelsAsync(
                credential,
                accountId,
                [messageId],
                isRead ? [] : [GmailSystemFolders.Unread],
                isRead ? [GmailSystemFolders.Unread] : [],
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");
        }
        catch (Exception exception) when (IsExpectedApiException(exception))
        {
            throw new MailReadException(MailReadFailureKind.MutationFailed, "Не удалось изменить статус письма Gmail.");
        }
    }

    public async Task<IReadOnlyList<GmailApiUserLabel>> GetUserLabelsAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(
            credential,
            accountId,
            cancellationToken);
        ListLabelsResponse response = await session.Service.Users.Labels.List("me").ExecuteAsync(cancellationToken);
        return response.Labels?
            .Where(label => label.Type is "user"
                && !string.IsNullOrWhiteSpace(label.Id)
                && !string.IsNullOrWhiteSpace(label.Name))
            .Select(label => new GmailApiUserLabel(label.Id, label.Name))
            .OrderBy(label => label.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray()
            ?? [];
    }

    public async Task ModifyLabelsAsync(
        MailCredential credential,
        Guid accountId,
        IReadOnlyCollection<string> messageIds,
        IReadOnlyCollection<string> addLabelIds,
        IReadOnlyCollection<string> removeLabelIds,
        CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(
            credential,
            accountId,
            cancellationToken);
        if (messageIds.Count == 1)
        {
            ModifyMessageRequest body = new()
            {
                AddLabelIds = addLabelIds.ToArray(),
                RemoveLabelIds = removeLabelIds.ToArray()
            };
            await session.Service.Users.Messages.Modify(body, "me", messageIds.Single())
                .ExecuteAsync(cancellationToken);
            return;
        }

        BatchModifyMessagesRequest batch = new()
        {
            Ids = messageIds.ToArray(),
            AddLabelIds = addLabelIds.ToArray(),
            RemoveLabelIds = removeLabelIds.ToArray()
        };
        await session.Service.Users.Messages.BatchModify(batch, "me").ExecuteAsync(cancellationToken);
    }

    public async Task<GmailApiTrashResult> MoveToTrashAsync(
        MailCredential credential,
        Guid accountId,
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(
            credential,
            accountId,
            cancellationToken);
        using SemaphoreSlim gate = new(MaximumTrashConcurrency, MaximumTrashConcurrency);
        Task<(string Id, Exception? Error)>[] tasks = messageIds
            .Distinct(StringComparer.Ordinal)
            .Select(async messageId =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await session.Service.Users.Messages.Trash("me", messageId).ExecuteAsync(cancellationToken);
                    return (messageId, (Exception?)null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return (messageId, exception);
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToArray();
        (string Id, Exception? Error)[] outcomes = await Task.WhenAll(tasks);
        return new GmailApiTrashResult(
            outcomes.Where(item => item.Error is null).Select(item => item.Id).ToHashSet(StringComparer.Ordinal),
            outcomes.Where(item => item.Error is not null).ToDictionary(
                item => item.Id,
                item => item.Error!,
                StringComparer.Ordinal));
    }

    internal static byte[] DecodeBase64Url(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    internal static ModifyMessageRequest CreateReadStateRequest(bool isRead) =>
        isRead
            ? new ModifyMessageRequest { RemoveLabelIds = [GmailSystemFolders.Unread] }
            : new ModifyMessageRequest { AddLabelIds = [GmailSystemFolders.Unread] };

    private static async Task<AuthorizedGmailSession> CreateAuthorizedServiceAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        GoogleAuthorizationCodeFlow flow = CreateFlow(credential);
        UserCredential userCredential = CreateUserCredential(flow, credential, accountId);
        try
        {
            await RefreshAccessTokenAsync(userCredential, cancellationToken);
            return new AuthorizedGmailSession(flow, CreateService(userCredential));
        }
        catch
        {
            flow.Dispose();
            throw;
        }
    }

    private static GmailService CreateService(UserCredential credential) =>
        new(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = GmailOAuthConstants.ApplicationName
            });

    private static async Task<(int Index, GmailApiSummaryData Summary)> LoadMetadataAsync(
        GmailService service,
        string messageId,
        int index,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            UsersResource.MessagesResource.GetRequest request = service.Users.Messages.Get("me", messageId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            request.Fields = MetadataFieldsProjection;
            GmailMessage message = await request.ExecuteAsync(cancellationToken);
            return (
                index,
                new GmailApiSummaryData(
                    message.Id,
                    GetHeader(message, "Subject"),
                    GetHeader(message, "From"),
                    message.InternalDate,
                    message.Snippet,
                    message.LabelIds?.ToArray() ?? [])
                {
                    AttachmentSummary = GetAttachmentSummary(message.Payload)
                });
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? GetHeader(GmailMessage message, string name) =>
        message.Payload?.Headers?.FirstOrDefault(header =>
            string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    internal static MailMessageAttachmentSummary GetAttachmentSummary(GmailMessagePart? payload)
    {
        List<MailAttachmentPreviewItem> attachments = [];
        CollectAttachments(payload, attachments);
        return MailMessageAttachmentSummary.Create(attachments);
    }

    private static void CollectAttachments(
        GmailMessagePart? part,
        ICollection<MailAttachmentPreviewItem> attachments)
    {
        if (part is null)
        {
            return;
        }

        string? disposition = GetPartHeader(part, "Content-Disposition");
        bool explicitAttachment = disposition?.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) == true;
        bool inline = disposition?.TrimStart().StartsWith("inline", StringComparison.OrdinalIgnoreCase) == true
            || !string.IsNullOrWhiteSpace(GetPartHeader(part, "Content-ID"));
        bool hasFileName = !string.IsNullOrWhiteSpace(part.Filename);
        if (!inline && (explicitAttachment || hasFileName))
        {
            attachments.Add(new MailAttachmentPreviewItem(
                MailAttachmentFileName.Sanitize(part.Filename),
                string.IsNullOrWhiteSpace(part.MimeType) ? "application/octet-stream" : part.MimeType,
                part.Body?.Size is int size && size >= 0 ? size : null));
        }

        if (part.Parts is null)
        {
            return;
        }

        foreach (GmailMessagePart child in part.Parts)
        {
            CollectAttachments(child, attachments);
        }
    }

    private static string? GetPartHeader(GmailMessagePart part, string name) =>
        part.Headers?.FirstOrDefault(header =>
            string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string BuildMetadataFieldsProjection()
    {
        string partFields = "filename,mimeType,body/size,headers";
        for (int depth = 0; depth < MetadataMimeTreeDepth; depth++)
        {
            partFields = $"filename,mimeType,body/size,headers,parts({partFields})";
        }

        return $"id,internalDate,labelIds,snippet,payload({partFields})";
    }

    private static GoogleAuthorizationCodeFlow CreateFlow(MailCredential credential) =>
        new(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = credential.OAuthClientId,
                    ClientSecret = credential.OAuthClientSecret
                },
                Scopes = [credential.HasGmailModifyScope
                    ? GmailOAuthConstants.ModifyScope
                    : GmailOAuthConstants.ReadOnlyScope]
            });

    private static UserCredential CreateUserCredential(
        GoogleAuthorizationCodeFlow flow,
        MailCredential credential,
        Guid accountId) =>
        new(flow, accountId.ToString("N"), new TokenResponse { RefreshToken = credential.Secret });

    private static async Task RefreshAccessTokenAsync(UserCredential credential, CancellationToken cancellationToken)
    {
        if (!await credential.RefreshTokenAsync(cancellationToken)
            || string.IsNullOrWhiteSpace(credential.Token.AccessToken))
        {
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");
        }
    }

    private static MailReadException MapListException(Exception exception) =>
        GmailAuthorizationFailureClassifier.RequiresReauthorization(exception)
            ? new MailReadException(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.")
            : new MailReadException(MailReadFailureKind.ConnectionFailed, "Не удалось загрузить почту. Проверьте подключение к сети.");

    private static bool IsExpectedApiException(Exception exception) =>
        exception is GoogleApiException
            or HttpRequestException
            or IOException
            or InvalidOperationException
            or FormatException;

    private sealed class AuthorizedGmailSession(
        GoogleAuthorizationCodeFlow flow,
        GmailService service) : IDisposable
    {
        public GmailService Service { get; } = service;

        public void Dispose()
        {
            Service.Dispose();
            flow.Dispose();
        }
    }
}
