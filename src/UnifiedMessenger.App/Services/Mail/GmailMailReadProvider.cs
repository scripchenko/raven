using System.Net;
using System.Net.Http;
using System.IO;
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

internal sealed record GmailApiRawMessage(byte[] RawMime, bool IsUnread);

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
}

internal sealed class GmailMailReadProvider(
    IMailCredentialStore credentialStore,
    IGmailApiReadClient apiClient,
    IMailContentExtractor contentExtractor) : IMailReadProvider
{
    private const string MessageKeyPrefix = "gmail:";

    public bool Supports(MailProviderType providerType) => providerType == MailProviderType.Gmail;

    public async Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
        MailAccount account,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        GmailApiInboxPage page = await apiClient.GetInboxPageAsync(
            credential,
            account.Id,
            continuationToken,
            pageSize,
            cancellationToken);

        MailMessageSummary[] summaries = page.Items.Select(MapSummary).ToArray();
        return new MailPage<MailMessageSummary>(summaries, page.NextPageToken);
    }

    public async Task<MailMessageContent> GetMessageAsync(
        MailAccount account,
        string messageKey,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account, pageSize: 1);
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
            return contentExtractor.Extract(messageKey, message, raw.IsUnread);
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
            item.LabelIds.Contains("UNREAD", StringComparer.Ordinal));
    }

    private async Task<MailCredential> LoadCredentialAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        MailCredential? credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        if (credential is not { Kind: MailCredentialKind.GmailOAuthRefreshToken } || !credential.IsValid())
        {
            throw new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google.");
        }

        return credential;
    }

    private static void ValidateAccount(MailAccount account, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Provider != MailProviderType.Gmail || pageSize is < 1 or > 100)
        {
            throw new MailReadException(
                MailReadFailureKind.InvalidConfiguration,
                "Почтовый аккаунт настроен некорректно.");
        }
    }

    private static string ParseMessageKey(string messageKey)
    {
        if (string.IsNullOrWhiteSpace(messageKey)
            || !messageKey.StartsWith(MessageKeyPrefix, StringComparison.Ordinal)
            || messageKey.Length == MessageKeyPrefix.Length)
        {
            throw new MailReadException(
                MailReadFailureKind.MessageUnavailable,
                "Письмо больше недоступно.");
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
    internal const string InboxLabel = "INBOX";

    public async Task<GmailApiInboxPage> GetInboxPageAsync(
        MailCredential credential,
        Guid accountId,
        string? pageToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using GoogleAuthorizationCodeFlow flow = CreateFlow(credential);
            UserCredential userCredential = CreateUserCredential(flow, credential, accountId);
            await RefreshAccessTokenAsync(userCredential, cancellationToken);
            using GmailService service = CreateService(userCredential);

            UsersResource.MessagesResource.ListRequest listRequest = service.Users.Messages.List("me");
            listRequest.LabelIds = new[] { InboxLabel };
            listRequest.MaxResults = pageSize;
            listRequest.PageToken = string.IsNullOrWhiteSpace(pageToken) ? null : pageToken;
            ListMessagesResponse response = await listRequest.ExecuteAsync(cancellationToken);
            GmailMessage[] listed = response.Messages?.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToArray() ?? [];

            using SemaphoreSlim gate = new(MaximumMetadataConcurrency, MaximumMetadataConcurrency);
            Task<(int Index, GmailApiSummaryData Summary)>[] metadataTasks = listed
                .Select((item, index) => LoadMetadataAsync(service, item.Id, index, gate, cancellationToken))
                .ToArray();
            (int Index, GmailApiSummaryData Summary)[] metadata = await Task.WhenAll(metadataTasks);
            GmailApiSummaryData[] ordered = metadata.OrderBy(item => item.Index).Select(item => item.Summary).ToArray();
            return new GmailApiInboxPage(ordered, response.NextPageToken);
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
            throw new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google.");
        }
        catch (Exception exception) when (IsExpectedApiException(exception))
        {
            throw new MailReadException(
                MailReadFailureKind.ConnectionFailed,
                "Не удалось загрузить почту. Проверьте подключение к сети.");
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
            using GoogleAuthorizationCodeFlow flow = CreateFlow(credential);
            UserCredential userCredential = CreateUserCredential(flow, credential, accountId);
            await RefreshAccessTokenAsync(userCredential, cancellationToken);
            using GmailService service = CreateService(userCredential);
            UsersResource.MessagesResource.GetRequest request = service.Users.Messages.Get("me", messageId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
            GmailMessage response = await request.ExecuteAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(response.Raw))
            {
                throw new MailReadException(
                    MailReadFailureKind.InvalidMessage,
                    "Не удалось безопасно прочитать содержимое письма.");
            }

            return new GmailApiRawMessage(
                DecodeBase64Url(response.Raw),
                response.LabelIds?.Contains("UNREAD", StringComparer.Ordinal) == true);
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
            throw new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google.");
        }
        catch (Exception exception) when (IsExpectedApiException(exception))
        {
            throw new MailReadException(
                MailReadFailureKind.MessageUnavailable,
                "Не удалось загрузить выбранное письмо.");
        }
    }

    internal static byte[] DecodeBase64Url(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

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
                Scopes = [GmailOAuthConstants.ReadOnlyScope]
            });

    private static UserCredential CreateUserCredential(
        GoogleAuthorizationCodeFlow flow,
        MailCredential credential,
        Guid accountId) =>
        new(
            flow,
            accountId.ToString("N"),
            new TokenResponse { RefreshToken = credential.Secret });

    private static async Task RefreshAccessTokenAsync(
        UserCredential credential,
        CancellationToken cancellationToken)
    {
        if (!await credential.RefreshTokenAsync(cancellationToken)
            || string.IsNullOrWhiteSpace(credential.Token.AccessToken))
        {
            throw new MailReadException(
                MailReadFailureKind.ReauthorizationRequired,
                "Требуется повторный вход в Google.");
        }
    }

    private static GmailService CreateService(UserCredential credential) =>
        new(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = GmailOAuthConstants.ApplicationName
            });

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
}
