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

namespace UnifiedMessenger.App.Services.Mail;

internal sealed record GmailApiSummaryData(
    string Id,
    string? Subject,
    string? From,
    long? InternalDate,
    string? Snippet,
    IReadOnlyList<string> LabelIds);

internal sealed record GmailApiInboxPage(
    IReadOnlyList<GmailApiSummaryData> Items,
    string? NextPageToken);

internal sealed record GmailApiRawMessage(byte[] RawMime, bool IsUnread, string? ThreadId = null);

internal interface IGmailApiReadClient
{
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

internal static class GmailSystemFolders
{
    public const string Inbox = "INBOX";
    public const string Sent = "SENT";
    public const string Draft = "DRAFT";
    public const string Spam = "SPAM";
    public const string Trash = "TRASH";
    public const string Unread = "UNREAD";

    public static IReadOnlyList<string> LabelIds { get; } = [Inbox, Sent, Draft, Spam, Trash];

    public static IReadOnlyList<MailFolder> Map(IReadOnlySet<string> availableLabels)
    {
        (MailFolderKind Kind, string Label)[] definitions =
        [
            (MailFolderKind.Inbox, Inbox),
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
    IMailContentExtractor contentExtractor) : IMailReadProvider, IMailMessageStateProvider
{
    private const string MessageKeyPrefix = "gmail:";

    public bool Supports(MailProviderType providerType) => providerType == MailProviderType.Gmail;

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
            item.LabelIds.Contains(GmailSystemFolders.Unread, StringComparer.Ordinal));
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

    private static string ParseMessageKey(string messageKey)
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

internal sealed class GmailApiReadClient : IGmailApiReadClient
{
    internal const int MaximumMetadataConcurrency = 5;
    internal const string InboxLabel = GmailSystemFolders.Inbox;

    public Task<GmailApiInboxPage> GetInboxPageAsync(
        MailCredential credential,
        Guid accountId,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        GetFolderPageAsync(credential, accountId, InboxLabel, false, pageToken, pageSize, cancellationToken);

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
        catch (Exception exception) when (IsAuthenticationException(exception))
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
            using AuthorizedGmailSession session = await CreateAuthorizedServiceAsync(credential, accountId, cancellationToken);
            GmailService service = session.Service;
            ModifyMessageRequest body = CreateReadStateRequest(isRead);
            await service.Users.Messages.Modify(body, "me", messageId).ExecuteAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAuthenticationException(exception))
        {
            throw new MailReadException(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");
        }
        catch (Exception exception) when (IsExpectedApiException(exception))
        {
            throw new MailReadException(MailReadFailureKind.MutationFailed, "Не удалось изменить статус письма Gmail.");
        }
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
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
            request.MetadataHeaders = new[] { "Subject", "From", "Date" };
            GmailMessage message = await request.ExecuteAsync(cancellationToken);
            return (
                index,
                new GmailApiSummaryData(
                    message.Id,
                    GetHeader(message, "Subject"),
                    GetHeader(message, "From"),
                    message.InternalDate,
                    message.Snippet,
                    message.LabelIds?.ToArray() ?? []));
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? GetHeader(GmailMessage message, string name) =>
        message.Payload?.Headers?.FirstOrDefault(header =>
            string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

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
        IsAuthenticationException(exception)
            ? new MailReadException(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.")
            : new MailReadException(MailReadFailureKind.ConnectionFailed, "Не удалось загрузить почту. Проверьте подключение к сети.");

    private static bool IsAuthenticationException(Exception exception) =>
        exception is TokenResponseException
        || exception is GoogleApiException apiException
            && apiException.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

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
