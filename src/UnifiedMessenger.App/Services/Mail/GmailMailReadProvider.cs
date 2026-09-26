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
    public string? DraftId { get; init; }
}

internal sealed record GmailApiInboxPage(
    IReadOnlyList<GmailApiSummaryData> Items,
    string? NextPageToken,
    long? LabelMessagesTotal = null,
    long? ResultSizeEstimate = null);

internal sealed record GmailApiHistoryBaseline(
    int UnreadCount,
    ulong HistoryId);

internal sealed record GmailApiHistoryDelta(
    int UnreadCount,
    ulong HistoryId,
    IReadOnlyCollection<string> NewInboxMessageIds,
    bool IsRebaseline = false)
{
    public GmailApiSummaryData? NotificationPreview { get; init; }
}

internal sealed record GmailApiRawMessage(byte[] RawMime, bool IsUnread, string? ThreadId = null);
internal sealed record GmailApiUserLabel(string Id, string Name);
internal sealed record GmailApiLabelCatalog(
    IReadOnlySet<string> SystemLabelIds,
    IReadOnlyList<GmailApiUserLabel> UserLabels);
internal sealed record GmailApiTrashResult(
    IReadOnlySet<string> SucceededMessageIds,
    IReadOnlyDictionary<string, Exception> FailedMessages);
internal sealed record GmailApiUntrashResult(
    IReadOnlySet<string> SucceededMessageIds,
    IReadOnlyDictionary<string, Exception> FailedMessages);

internal interface IGmailApiReadClient
{
    Task<GmailApiHistoryBaseline> GetHistoryBaselineAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GmailApiHistoryBaseline>(new NotSupportedException());

    Task<GmailApiHistoryDelta> GetHistoryDeltaAsync(
        MailCredential credential,
        Guid accountId,
        ulong historyId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GmailApiHistoryDelta>(new NotSupportedException());

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

    Task<GmailApiInboxPage> SearchPageAsync(
        MailCredential credential,
        Guid accountId,
        string query,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GmailApiInboxPage>(new NotSupportedException());

    Task<GmailApiInboxPage> GetAllMailPageAsync(
        MailCredential credential,
        Guid accountId,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        Task.FromException<GmailApiInboxPage>(new NotSupportedException());

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

    async Task<GmailApiLabelCatalog> GetLabelCatalogAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        new(
            await GetSystemLabelIdsAsync(credential, accountId, cancellationToken),
            []);

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
            throw new MailReadException(MailReadFailureKind.FolderUnavailable, L.Instance.Get("This Gmail folder is unavailable."));
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
                L.Instance.Get("Could not change the Gmail message read status.")));

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

    Task<GmailApiUntrashResult> RestoreFromTrashAsync(
        MailCredential credential,
        Guid accountId,
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GmailApiUntrashResult(
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, Exception>(StringComparer.Ordinal)));
}

internal static class GmailSystemFolders
{
    public const string AllMailView = "special:all-mail";
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
            (MailFolderKind.Drafts, Draft)
        ];
        List<MailFolder> folders = definitions
            .Where(item => availableLabels.Contains(item.Label))
            .Select(item => MailFolderCatalog.Create(item.Kind, item.Label))
            .ToList();
        folders.Add(MailFolderCatalog.Create(MailFolderKind.AllMail, AllMailView));
        if (availableLabels.Contains(Spam))
        {
            folders.Add(MailFolderCatalog.Create(MailFolderKind.Spam, Spam));
        }
        if (availableLabels.Contains(Trash))
        {
            folders.Add(MailFolderCatalog.Create(MailFolderKind.Trash, Trash));
        }
        return folders;
    }

    public static IReadOnlyList<MailFolder> Map(GmailApiLabelCatalog catalog)
    {
        List<MailFolder> folders = Map(catalog.SystemLabelIds).ToList();
        GmailApiUserLabel[] userLabels = catalog.UserLabels
            .Where(label => !string.IsNullOrWhiteSpace(label.Id)
                && !string.IsNullOrWhiteSpace(label.Name))
            .GroupBy(label => label.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(label => label.Name, GmailUserLabelNameComparer.Instance)
            .ThenBy(label => label.Id, StringComparer.Ordinal)
            .ToArray();
        for (int index = 0; index < userLabels.Length; index++)
        {
            GmailApiUserLabel label = userLabels[index];
            folders.Add(MailFolderCatalog.CreateUserLabel(
                label.Id,
                label.Name,
                showsSectionHeader: index == 0));
        }

        return folders;
    }
}

internal sealed class GmailMailReadProvider(
    IMailCredentialStore credentialStore,
    IGmailApiReadClient apiClient,
    IMailContentExtractor contentExtractor,
    MailMessageSourceCache? sourceCache = null) : IMailReadProvider, IMailSearchProvider, IMailMessageStateProvider, IMailAttachmentContentProvider, IMailInboxUnreadCountProvider, IMailNewMessageHistoryProvider
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

    async Task<MailHistoryPollResult> IMailNewMessageHistoryProvider.PollHistoryAsync(
        MailAccount account,
        ulong? historyCursor,
        CancellationToken cancellationToken)
    {
        ValidateAccount(account, pageSize: 1);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        if (historyCursor is null)
        {
            GmailApiHistoryBaseline baseline = await apiClient
                .GetHistoryBaselineAsync(credential, account.Id, cancellationToken);
            return new MailHistoryPollResult(baseline.UnreadCount, baseline.HistoryId, []);
        }

        GmailApiHistoryDelta delta = await apiClient
            .GetHistoryDeltaAsync(credential, account.Id, historyCursor.Value, cancellationToken);
        return new MailHistoryPollResult(
            delta.UnreadCount,
            delta.HistoryId,
            delta.NewInboxMessageIds,
            delta.IsRebaseline)
        {
            NotificationPreview = MapNotificationPreview(delta.NotificationPreview)
        };
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
        GmailApiLabelCatalog labels = await apiClient.GetLabelCatalogAsync(
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
        GmailApiInboxPage page = folder.Kind is MailFolderKind.AllMail
            ? await apiClient.GetAllMailPageAsync(
                credential,
                account.Id,
                continuationToken,
                pageSize,
                cancellationToken)
            : await apiClient.GetFolderPageAsync(
                credential,
                account.Id,
                folder.ProviderLocator,
                folder.Kind is MailFolderKind.Spam or MailFolderKind.Trash,
                continuationToken,
                pageSize,
                cancellationToken);
        return new MailPage<MailMessageSummary>(
            page.Items.Select(MapSummary).ToArray(),
            page.NextPageToken,
            folder.Kind is MailFolderKind.AllMail ? null : page.LabelMessagesTotal);
    }

    public async Task<MailPage<MailMessageSummary>> SearchAsync(
        MailAccount account,
        string query,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidSearchQuery,
                L.Instance.Get("Could not search mail. Check your query."));
        }

        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        GmailApiInboxPage page = await apiClient.SearchPageAsync(
            credential,
            account.Id,
            query,
            continuationToken,
            pageSize,
            cancellationToken);
        return new MailPage<MailMessageSummary>(
            page.Items.Select(MapSummary).ToArray(),
            page.NextPageToken);
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
                L.Instance.Get("Could not safely read the message content."));
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
                L.Instance.Get("Could not load the attachment."),
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
                L.Instance.Get("Allow Google access again to change message read status."));
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
            throw new MailReadException(MailReadFailureKind.MutationFailed, L.Instance.Get("This action is unavailable for this folder."));
        }

        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        if (!credential.HasGmailModifyScope)
        {
            throw new MailReadException(
                MailReadFailureKind.MutationNotAuthorized,
                L.Instance.Get("Allow Google access again to change message read status."));
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
            ProviderLabelIds = item.LabelIds.ToHashSet(StringComparer.Ordinal),
            ProviderDraftId = string.IsNullOrWhiteSpace(item.DraftId) ? null : item.DraftId
        };
    }

    private static MailNotificationPreview? MapNotificationPreview(GmailApiSummaryData? item)
    {
        if (item is null)
        {
            return null;
        }

        MailMessageSummary summary = MapSummary(item);
        return new MailNotificationPreview(
            summary.FromDisplayName,
            summary.FromAddress,
            summary.Subject,
            summary.Preview);
    }

    private async Task<MailCredential> LoadCredentialAsync(MailAccount account, CancellationToken cancellationToken)
    {
        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.GmailOAuthRefreshToken } || !credential.IsValid())
        {
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, L.Instance.Get("Google sign-in is required again."));
        }

        return credential;
    }

    private static void ValidateAccount(MailAccount account, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Provider != MailProviderType.Gmail || pageSize is < 1 or > 100)
        {
            throw new MailReadException(MailReadFailureKind.InvalidConfiguration, L.Instance.Get("Mail account configuration is invalid."));
        }
    }

    private static void ValidateFolder(MailFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        bool isValid = folder.Kind is MailFolderKind.AllMail
            ? string.Equals(folder.ProviderLocator, GmailSystemFolders.AllMailView, StringComparison.Ordinal)
            : folder.Kind is MailFolderKind.UserLabel
                ? !string.IsNullOrWhiteSpace(folder.ProviderLocator)
                : GmailSystemFolders.LabelIds.Contains(folder.ProviderLocator, StringComparer.Ordinal);
        if (!isValid)
        {
            throw new MailReadException(MailReadFailureKind.FolderUnavailable, L.Instance.Get("This Gmail folder is unavailable."));
        }
    }

    internal static string ParseMessageKey(string messageKey)
    {
        if (string.IsNullOrWhiteSpace(messageKey)
            || !messageKey.StartsWith(MessageKeyPrefix, StringComparison.Ordinal)
            || messageKey.Length == MessageKeyPrefix.Length)
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, L.Instance.Get("Message no longer available."));
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
    internal const int MaximumRateLimitRetries = 2;
    internal const int MaximumPerMessageMutationConcurrency = 4;
    internal const int MetadataMimeTreeDepth = 8;
    internal const int HistoryPageSize = 500;
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

    public async Task<GmailApiHistoryBaseline> GetHistoryBaselineAsync(
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
            return await ReadCurrentHistoryBaselineAsync(session.Service, cancellationToken);
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

    public async Task<GmailApiHistoryDelta> GetHistoryDeltaAsync(
        MailCredential credential,
        Guid accountId,
        ulong historyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(
                credential,
                accountId,
                cancellationToken);
            GmailService service = session.Service;

            (ulong nextHistoryId, IReadOnlyCollection<string> messageIds) historyDelta;
            try
            {
                historyDelta = await ReadHistoryPagesAsync(
                    historyId,
                    async (pageToken, token) =>
                    {
                        UsersResource.HistoryResource.ListRequest request = service.Users.History.List("me");
                        ConfigureHistoryRequest(request, historyId, pageToken);
                        return await request.ExecuteAsync(token);
                    },
                    cancellationToken);
            }
            catch (GoogleApiException exception) when (IsStaleHistoryCursor(exception))
            {
                GmailApiHistoryBaseline baseline = await ReadCurrentHistoryBaselineAsync(service, cancellationToken);
                return new GmailApiHistoryDelta(
                    baseline.UnreadCount,
                    baseline.HistoryId,
                    [],
                    IsRebaseline: true);
            }

            int unreadCount = await ReadInboxUnreadCountAsync(service, cancellationToken);
            GmailApiSummaryData? notificationPreview = historyDelta.messageIds.Count == 1
                ? await TryLoadNotificationPreviewAsync(
                    service,
                    historyDelta.messageIds.Single(),
                    cancellationToken)
                : null;
            return new GmailApiHistoryDelta(
                unreadCount,
                historyDelta.nextHistoryId,
                historyDelta.messageIds)
            {
                NotificationPreview = notificationPreview
            };
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

    internal static void ConfigureHistoryRequest(
        UsersResource.HistoryResource.ListRequest request,
        ulong historyId,
        string? pageToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.StartHistoryId = historyId;
        request.HistoryTypes = UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded;
        request.LabelId = GmailSystemFolders.Inbox;
        request.MaxResults = HistoryPageSize;
        request.PageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
    }

    internal static void ConfigureNotificationPreviewRequest(
        UsersResource.MessagesResource.GetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
        request.MetadataHeaders = new[] { "From", "Subject" };
        request.Fields = "snippet,payload/headers";
    }

    private static async Task<GmailApiSummaryData?> TryLoadNotificationPreviewAsync(
        GmailService service,
        string messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            UsersResource.MessagesResource.GetRequest request = service.Users.Messages.Get("me", messageId);
            ConfigureNotificationPreviewRequest(request);
            GmailMessage message = await request.ExecuteAsync(cancellationToken);
            return new GmailApiSummaryData(
                messageId,
                GetHeader(message, "Subject"),
                GetHeader(message, "From"),
                null,
                message.Snippet,
                []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Notification preview metadata is optional; detection and cursor advancement remain authoritative.
            return null;
        }
    }

    internal static async Task<(ulong HistoryId, IReadOnlyCollection<string> MessageIds)> ReadHistoryPagesAsync(
        ulong startHistoryId,
        Func<string?, CancellationToken, Task<ListHistoryResponse>> loadPageAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadPageAsync);
        HashSet<string> messageIds = new(StringComparer.Ordinal);
        HashSet<string> visitedPageTokens = new(StringComparer.Ordinal);
        string? pageToken = null;
        ulong latestHistoryId = startHistoryId;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListHistoryResponse response = await loadPageAsync(pageToken, cancellationToken);
            if (response.HistoryId is ulong responseHistoryId)
            {
                latestHistoryId = responseHistoryId;
            }

            foreach (History history in response.History ?? [])
            {
                foreach (HistoryMessageAdded added in history.MessagesAdded ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(added.Message?.Id))
                    {
                        messageIds.Add(added.Message.Id);
                    }
                }
            }

            pageToken = string.IsNullOrWhiteSpace(response.NextPageToken)
                ? null
                : response.NextPageToken;
            if (pageToken is not null && !visitedPageTokens.Add(pageToken))
            {
                throw new InvalidOperationException(L.Instance.Get("Gmail returned a repeated history page token."));
            }
        }
        while (pageToken is not null);

        return (latestHistoryId, messageIds);
    }

    internal static bool IsStaleHistoryCursor(GoogleApiException exception) =>
        exception.HttpStatusCode == HttpStatusCode.NotFound;

    private static async Task<GmailApiHistoryBaseline> ReadCurrentHistoryBaselineAsync(
        GmailService service,
        CancellationToken cancellationToken)
    {
        Profile profile = await service.Users.GetProfile("me").ExecuteAsync(cancellationToken);
        if (profile.HistoryId is not ulong historyId)
        {
            throw new InvalidOperationException(L.Instance.Get("Gmail did not return a history ID."));
        }

        int unreadCount = await ReadInboxUnreadCountAsync(service, cancellationToken);
        return new GmailApiHistoryBaseline(unreadCount, historyId);
    }

    private static async Task<int> ReadInboxUnreadCountAsync(
        GmailService service,
        CancellationToken cancellationToken)
    {
        Google.Apis.Gmail.v1.Data.Label inbox = await service.Users.Labels
            .Get("me", GmailSystemFolders.Inbox)
            .ExecuteAsync(cancellationToken);
        long unread = inbox.MessagesUnread ?? 0;
        return unread >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)unread);
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
            return MapLabelCatalog(response.Labels).SystemLabelIds;
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
            if (string.Equals(labelId, GmailSystemFolders.Draft, StringComparison.Ordinal))
            {
                return await GetDraftPageAsync(service, pageToken, pageSize, cancellationToken);
            }

            UsersResource.MessagesResource.ListRequest listRequest = service.Users.Messages.List("me");
            ConfigureFolderRequest(listRequest, labelId, includeSpamTrash, pageToken, pageSize);
            ListMessagesResponse response = await ExecuteWithRateLimitRetryAsync(
                () => listRequest.ExecuteAsync(cancellationToken),
                cancellationToken);
            GmailMessage[] listed = response.Messages?.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToArray() ?? [];
            long? labelMessagesTotal = SupportsExactLabelTotal(labelId)
                ? await TryGetMessageOrientedLabelTotalAsync(service, labelId, cancellationToken)
                : null;

            using SemaphoreSlim gate = new(MaximumMetadataConcurrency, MaximumMetadataConcurrency);
            Task<(int Index, GmailApiSummaryData Summary)?>[] tasks = listed
                .Select((item, index) => LoadAvailableMetadataAsync(
                    service,
                    item.Id,
                    index,
                    gate,
                    cancellationToken))
                .ToArray();
            (int Index, GmailApiSummaryData Summary)?[] metadata = await Task.WhenAll(tasks);
            IReadOnlyList<GmailApiSummaryData> summaries = await ResolveDraftIdentitiesAsync(
                service,
                SelectAvailableSummaries(metadata),
                cancellationToken);
            return new GmailApiInboxPage(
                summaries,
                response.NextPageToken,
                labelMessagesTotal,
                response.ResultSizeEstimate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailableUserLabelFailure(exception, labelId))
        {
            throw new MailReadException(
                MailReadFailureKind.FolderUnavailable,
                L.Instance.Get("This Gmail label is no longer available."));
        }
        catch (Exception exception)
        {
            throw MapListException(exception);
        }
    }

    public async Task<GmailApiLabelCatalog> GetLabelCatalogAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            ListLabelsResponse response = await session.Service.Users.Labels.List("me").ExecuteAsync(cancellationToken);
            return MapLabelCatalog(response.Labels);
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

    internal static void ConfigureFolderRequest(
        UsersResource.MessagesResource.ListRequest request,
        string labelId,
        bool includeSpamTrash,
        string? pageToken,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.LabelIds = new[] { labelId };
        request.IncludeSpamTrash = includeSpamTrash;
        request.MaxResults = pageSize;
        request.PageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
    }

    internal static bool IsUnavailableUserLabelFailure(Exception exception, string labelId) =>
        !GmailSystemFolders.LabelIds.Contains(labelId, StringComparer.Ordinal)
        && exception is GoogleApiException
        {
            HttpStatusCode: HttpStatusCode.BadRequest or HttpStatusCode.NotFound
        };

    internal static bool SupportsExactLabelTotal(string labelId) =>
        GmailSystemFolders.LabelIds.Contains(labelId, StringComparer.Ordinal);

    internal static GmailApiLabelCatalog MapLabelCatalog(
        IEnumerable<Google.Apis.Gmail.v1.Data.Label>? labels)
    {
        Google.Apis.Gmail.v1.Data.Label[] safeLabels = labels?.ToArray() ?? [];
        HashSet<string> systemLabelIds = safeLabels
            .Where(label => string.Equals(label.Type, "system", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(label.Id))
            .Select(label => label.Id)
            .ToHashSet(StringComparer.Ordinal);
        GmailApiUserLabel[] userLabels = safeLabels
            .Where(label => string.Equals(label.Type, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(label.Id)
                && !string.IsNullOrWhiteSpace(label.Name))
            .Select(label => new GmailApiUserLabel(label.Id, label.Name))
            .GroupBy(label => label.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(label => label.Name, GmailUserLabelNameComparer.Instance)
            .ThenBy(label => label.Id, StringComparer.Ordinal)
            .ToArray();
        return new GmailApiLabelCatalog(systemLabelIds, userLabels);
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
                throw new MailReadException(MailReadFailureKind.InvalidMessage, L.Instance.Get("Could not safely read the message content."));
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
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, L.Instance.Get("Google sign-in is required again."));
        }
        catch (Exception exception) when (IsExpectedApiException(exception))
        {
            throw new MailReadException(MailReadFailureKind.MessageUnavailable, L.Instance.Get("Could not load the selected message."));
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
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, L.Instance.Get("Google sign-in is required again."));
        }
        catch (Exception exception) when (IsExpectedApiException(exception))
        {
            throw new MailReadException(MailReadFailureKind.MutationFailed, L.Instance.Get("Could not change the Gmail message read status."));
        }
    }

    public async Task<GmailApiInboxPage> GetAllMailPageAsync(
        MailCredential credential,
        Guid accountId,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            GmailService service = session.Service;
            UsersResource.MessagesResource.ListRequest listRequest = service.Users.Messages.List("me");
            ConfigureAllMailRequest(listRequest, pageToken, pageSize);
            ListMessagesResponse response = await ExecuteWithRateLimitRetryAsync(
                () => listRequest.ExecuteAsync(cancellationToken),
                cancellationToken);
            GmailMessage[] listed = response.Messages?.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToArray() ?? [];

            using SemaphoreSlim gate = new(MaximumMetadataConcurrency, MaximumMetadataConcurrency);
            Task<(int Index, GmailApiSummaryData Summary)?>[] tasks = listed
                .Select((item, index) => LoadAvailableMetadataAsync(
                    service,
                    item.Id,
                    index,
                    gate,
                    cancellationToken))
                .ToArray();
            (int Index, GmailApiSummaryData Summary)?[] metadata = await Task.WhenAll(tasks);
            IReadOnlyList<GmailApiSummaryData> summaries = await ResolveDraftIdentitiesAsync(
                service,
                SelectAvailableSummaries(metadata),
                cancellationToken);
            return new GmailApiInboxPage(
                summaries,
                response.NextPageToken,
                LabelMessagesTotal: null,
                response.ResultSizeEstimate);
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

    private static async Task<(int Index, GmailApiSummaryData Summary)?> LoadAvailableMetadataAsync(
        GmailService service,
        string messageId,
        int index,
        SemaphoreSlim gate,
        CancellationToken cancellationToken,
        string? draftId = null)
    {
        (int Index, GmailApiSummaryData Summary)? loaded = await LoadAvailableListedItemAsync(
            () => LoadMetadataAsync(
                service,
                messageId,
                index,
                gate,
                cancellationToken,
                draftId));
        return loaded is { } result
            && (draftId is null || IsCurrentDraftSummary(result.Summary))
                ? result
                : null;
    }

    internal static bool IsStaleListedItem(Exception exception) =>
        exception is GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound };

    internal static async Task<(int Index, GmailApiSummaryData Summary)?> LoadAvailableListedItemAsync(
        Func<Task<(int Index, GmailApiSummaryData Summary)>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        try
        {
            return await load();
        }
        catch (Exception exception) when (IsStaleListedItem(exception))
        {
            // Gmail list and per-item reads are separate calls. A message can disappear between
            // them; omitting that stale row keeps the remaining server-authoritative page usable.
            return null;
        }
    }

    internal static bool IsCurrentDraftSummary(GmailApiSummaryData summary) =>
        summary.LabelIds.Contains(GmailSystemFolders.Draft, StringComparer.Ordinal);

    internal static bool IsRateLimitFailure(Exception exception)
    {
        if (exception is not GoogleApiException apiException)
        {
            return false;
        }

        if (apiException.HttpStatusCode is HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return apiException.HttpStatusCode is HttpStatusCode.Forbidden
            && apiException.Error?.Errors?.Any(error =>
                string.Equals(error.Reason, "rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(error.Reason, "userRateLimitExceeded", StringComparison.OrdinalIgnoreCase)) == true;
    }

    internal static async Task<T> ExecuteWithRateLimitRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        delay ??= Task.Delay;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (
                attempt < MaximumRateLimitRetries
                && IsRateLimitFailure(exception))
            {
                await delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
            }
        }
    }

    internal static IReadOnlyList<GmailApiSummaryData> SelectAvailableSummaries(
        IEnumerable<(int Index, GmailApiSummaryData Summary)?> metadata) =>
        metadata
            .Where(item => item.HasValue)
            .Select(item => item.GetValueOrDefault())
            .OrderBy(item => item.Index)
            .Select(item => item.Summary)
            .ToArray();

    public async Task<GmailApiInboxPage> SearchPageAsync(
        MailCredential credential,
        Guid accountId,
        string query,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            GmailService service = session.Service;
            UsersResource.MessagesResource.ListRequest listRequest = service.Users.Messages.List("me");
            ConfigureSearchRequest(listRequest, query, pageToken, pageSize);
            ListMessagesResponse response = await ExecuteWithRateLimitRetryAsync(
                () => listRequest.ExecuteAsync(cancellationToken),
                cancellationToken);
            GmailMessage[] listed = response.Messages?.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToArray() ?? [];

            using SemaphoreSlim gate = new(MaximumMetadataConcurrency, MaximumMetadataConcurrency);
            Task<(int Index, GmailApiSummaryData Summary)?>[] tasks = listed
                .Select((item, index) => LoadAvailableMetadataAsync(
                    service,
                    item.Id,
                    index,
                    gate,
                    cancellationToken))
                .ToArray();
            (int Index, GmailApiSummaryData Summary)?[] metadata = await Task.WhenAll(tasks);
            IReadOnlyList<GmailApiSummaryData> summaries = await ResolveDraftIdentitiesAsync(
                service,
                SelectAvailableSummaries(metadata),
                cancellationToken);
            return new GmailApiInboxPage(
                summaries,
                response.NextPageToken,
                null,
                response.ResultSizeEstimate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapSearchException(exception);
        }
    }

    private static async Task<GmailApiInboxPage> GetDraftPageAsync(
        GmailService service,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        UsersResource.DraftsResource.ListRequest listRequest = service.Users.Drafts.List("me");
        listRequest.MaxResults = pageSize;
        listRequest.PageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
        ListDraftsResponse response = await ExecuteWithRateLimitRetryAsync(
            () => listRequest.ExecuteAsync(cancellationToken),
            cancellationToken);
        Draft[] listed = response.Drafts?
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Message?.Id))
            .ToArray()
            ?? [];
        long? labelMessagesTotal = await TryGetMessageOrientedLabelTotalAsync(
            service,
            GmailSystemFolders.Draft,
            cancellationToken);

        using SemaphoreSlim gate = new(MaximumMetadataConcurrency, MaximumMetadataConcurrency);
        Task<(int Index, GmailApiSummaryData Summary)?>[] tasks = listed
            .Select((item, index) => LoadAvailableMetadataAsync(
                service,
                item.Message.Id,
                index,
                gate,
                cancellationToken,
                item.Id))
            .ToArray();
        (int Index, GmailApiSummaryData Summary)?[] metadata = await Task.WhenAll(tasks);
        return new GmailApiInboxPage(
            SelectAvailableSummaries(metadata),
            response.NextPageToken,
            labelMessagesTotal,
            response.ResultSizeEstimate);
    }

    internal static void ConfigureSearchRequest(
        UsersResource.MessagesResource.ListRequest request,
        string query,
        string? pageToken,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Q = query;
        request.IncludeSpamTrash = true;
        request.MaxResults = pageSize;
        request.PageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
    }

    internal static void ConfigureAllMailRequest(
        UsersResource.MessagesResource.ListRequest request,
        string? pageToken,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.LabelIds = null;
        request.IncludeSpamTrash = false;
        request.MaxResults = pageSize;
        request.PageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
    }

    private static async Task<IReadOnlyList<GmailApiSummaryData>> ResolveDraftIdentitiesAsync(
        GmailService service,
        IReadOnlyList<GmailApiSummaryData> summaries,
        CancellationToken cancellationToken)
    {
        HashSet<string> pendingMessageIds = summaries
            .Where(IsDraftSummary)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (pendingMessageIds.Count == 0)
        {
            return summaries;
        }

        Dictionary<string, string> draftIdsByMessageId = new(StringComparer.Ordinal);
        HashSet<string> visitedPageTokens = new(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            UsersResource.DraftsResource.ListRequest request = service.Users.Drafts.List("me");
            request.MaxResults = 500;
            request.PageToken = pageToken;
            request.Fields = "drafts(id,message/id),nextPageToken";
            ListDraftsResponse response = await ExecuteWithRateLimitRetryAsync(
                () => request.ExecuteAsync(cancellationToken),
                cancellationToken);
            foreach (Draft draft in response.Drafts ?? [])
            {
                if (!string.IsNullOrWhiteSpace(draft.Id)
                    && !string.IsNullOrWhiteSpace(draft.Message?.Id)
                    && pendingMessageIds.Remove(draft.Message.Id))
                {
                    draftIdsByMessageId[draft.Message.Id] = draft.Id;
                }
            }

            pageToken = string.IsNullOrWhiteSpace(response.NextPageToken)
                ? null
                : response.NextPageToken;
            if (pageToken is not null && !visitedPageTokens.Add(pageToken))
            {
                throw new InvalidOperationException(L.Instance.Get("Gmail returned a repeated draft page token."));
            }
        }
        while (pageToken is not null && pendingMessageIds.Count > 0);

        return ApplyDraftIdentities(summaries, draftIdsByMessageId);
    }

    internal static IReadOnlyList<GmailApiSummaryData> ApplyDraftIdentities(
        IReadOnlyList<GmailApiSummaryData> summaries,
        IReadOnlyDictionary<string, string> draftIdsByMessageId) =>
        summaries
            .Where(item => !IsDraftSummary(item) || draftIdsByMessageId.ContainsKey(item.Id))
            .Select(item => draftIdsByMessageId.TryGetValue(item.Id, out string? draftId)
                ? item with { DraftId = draftId }
                : item)
            .ToArray();

    private static bool IsDraftSummary(GmailApiSummaryData item) =>
        item.LabelIds.Contains(GmailSystemFolders.Draft, StringComparer.Ordinal);

    private static async Task<long?> TryGetMessageOrientedLabelTotalAsync(
        GmailService service,
        string labelId,
        CancellationToken cancellationToken)
    {
        try
        {
            UsersResource.LabelsResource.GetRequest request = service.Users.Labels.Get("me", labelId);
            request.Fields = "messagesTotal";
            Google.Apis.Gmail.v1.Data.Label label = await ExecuteWithRateLimitRetryAsync(
                () => request.ExecuteAsync(cancellationToken),
                cancellationToken);
            return GetMessageOrientedLabelTotal(label);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static long? GetMessageOrientedLabelTotal(Google.Apis.Gmail.v1.Data.Label? label)
    {
        // raven currently renders one row per Message. ThreadsTotal is only valid for a future thread-oriented list.
        return label?.MessagesTotal is int messagesTotal
            ? Math.Max(0L, messagesTotal)
            : null;
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
        return MapLabelCatalog(response.Labels).UserLabels;
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
        using SemaphoreSlim gate = new(
            MaximumPerMessageMutationConcurrency,
            MaximumPerMessageMutationConcurrency);
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

    public async Task<GmailApiUntrashResult> RestoreFromTrashAsync(
        MailCredential credential,
        Guid accountId,
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(
            credential,
            accountId,
            cancellationToken);
        using SemaphoreSlim gate = new(
            MaximumPerMessageMutationConcurrency,
            MaximumPerMessageMutationConcurrency);
        Task<(string Id, Exception? Error)>[] tasks = messageIds
            .Distinct(StringComparer.Ordinal)
            .Select(async messageId =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await session.Service.Users.Messages.Untrash("me", messageId).ExecuteAsync(cancellationToken);
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
        return new GmailApiUntrashResult(
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
        CancellationToken cancellationToken,
        string? draftId = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            UsersResource.MessagesResource.GetRequest request = service.Users.Messages.Get("me", messageId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            request.Fields = MetadataFieldsProjection;
            GmailMessage message = await ExecuteWithRateLimitRetryAsync(
                () => request.ExecuteAsync(cancellationToken),
                cancellationToken);
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
                    AttachmentSummary = GetAttachmentSummary(message.Payload),
                    DraftId = draftId
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
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, L.Instance.Get("Google sign-in is required again."));
        }
    }

    internal static MailReadException MapListException(Exception exception)
    {
        if (exception is MailReadException mailReadException)
        {
            return mailReadException;
        }

        if (GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            return new MailReadException(MailReadFailureKind.ReauthorizationRequired, L.Instance.Get("Google sign-in is required again."));
        }

        if (IsRateLimitFailure(exception))
        {
            return new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                L.Instance.Get("Gmail is temporarily rate-limiting requests. Please retry."));
        }

        return IsNetworkFailure(exception)
            ? new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                L.Instance.Get("Could not load mail. Check your network connection."))
            : new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                L.Instance.Get("Could not load mail. Please retry."));
    }

    internal static MailReadException MapSearchException(Exception exception)
    {
        if (exception is MailReadException mailReadException)
        {
            return mailReadException;
        }

        if (GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            return new MailReadException(MailReadFailureKind.ReauthorizationRequired, L.Instance.Get("Google sign-in is required again."));
        }

        return exception switch
        {
            GoogleApiException { HttpStatusCode: HttpStatusCode.BadRequest } => new MailReadException(
                MailReadFailureKind.InvalidSearchQuery,
                L.Instance.Get("Could not search mail. Check your query.")),
            GoogleApiException => new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                L.Instance.Get("Gmail search is temporarily unavailable. Please retry later.")),
            _ when IsNetworkFailure(exception) => new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                L.Instance.Get("Could not search mail. Check your network connection.")),
            _ => new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                L.Instance.Get("Could not search mail. Please retry."))
        };
    }

    private static bool IsNetworkFailure(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException
        || exception is TaskCanceledException;

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
