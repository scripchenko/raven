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

public sealed record GmailDraftIdentity(
    string DraftId,
    string? MessageId,
    string? ThreadId);

public sealed record GmailDraftLoadResult(
    GmailDraftIdentity Identity,
    MailComposeTemplate Template,
    bool IsReadOnly,
    string? RestrictionMessage);

public sealed class GmailDraftException(
    MailSendFailureKind failureKind,
    string userMessage,
    Exception? innerException = null) : Exception(userMessage, innerException)
{
    public MailSendFailureKind FailureKind { get; } = failureKind;
    public string UserMessage { get; } = userMessage;
}

public interface IGmailDraftService
{
    Task<GmailDraftLoadResult> LoadAsync(
        MailAccount account,
        string draftId,
        CancellationToken cancellationToken = default);

    Task<GmailDraftIdentity> SaveAsync(
        MailAccount account,
        GmailDraftIdentity? identity,
        MailComposeRequest request,
        CancellationToken cancellationToken = default);

    Task<MailSendResult> SendAsync(
        MailAccount account,
        GmailDraftIdentity identity,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        MailAccount account,
        GmailDraftIdentity identity,
        CancellationToken cancellationToken = default);
}

public interface IMailDraftAutosaveScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemMailDraftAutosaveScheduler : IMailDraftAutosaveScheduler
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

internal sealed record GmailApiDraftContent(
    GmailDraftIdentity Identity,
    byte[] RawMime);

internal interface IGmailDraftApiClient
{
    Task<GmailApiDraftContent> GetAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        CancellationToken cancellationToken = default);

    Task<GmailDraftIdentity> CreateAsync(
        MailCredential credential,
        Guid accountId,
        byte[] rawMime,
        string? threadId,
        CancellationToken cancellationToken = default);

    Task<GmailDraftIdentity> UpdateAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        byte[] rawMime,
        string? threadId,
        CancellationToken cancellationToken = default);

    Task<GmailApiSendReceipt> SendAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        CancellationToken cancellationToken = default);
}

internal sealed class GmailDraftService(
    IMailCredentialStore credentialStore,
    IGmailDraftApiClient apiClient,
    IMailOutgoingAttachmentMaterializer attachmentMaterializer,
    IMailMimeMessageFactory mimeMessageFactory) : IGmailDraftService
{
    private const string RichDraftWarning =
        "Этот черновик содержит форматирование, которое raven не может сохранить без потерь. Откройте его в Gmail.";

    public async Task<GmailDraftLoadResult> LoadAsync(
        MailAccount account,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        GmailApiDraftContent content = await apiClient.GetAsync(
            credential,
            account.Id,
            draftId,
            cancellationToken);

        try
        {
            using MemoryStream stream = new(content.RawMime, writable: false);
            MimeMessage message = await MimeMessage.LoadAsync(stream, cancellationToken);
            bool hasUnsupportedRichContent = !string.IsNullOrWhiteSpace(message.HtmlBody);
            IReadOnlyList<OutgoingMailAttachment> attachments = ExtractAttachments(message, cancellationToken);
            MailReplyContext? replyContext = CreateReplyContext(message, content.Identity.ThreadId);
            MailComposeTemplate template = new(
                MailContentExtractor.FormatAddresses(message.To),
                MailContentExtractor.FormatAddresses(message.Cc),
                MailContentExtractor.FormatAddresses(message.Bcc),
                message.Subject?.Trim() ?? string.Empty,
                MailContentExtractor.NormalizePlainText(message.TextBody),
                replyContext)
            {
                ExistingAttachments = attachments,
                IsReadOnly = hasUnsupportedRichContent,
                RestrictionMessage = hasUnsupportedRichContent ? RichDraftWarning : null
            };
            return new GmailDraftLoadResult(
                content.Identity,
                template,
                hasUnsupportedRichContent,
                template.RestrictionMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GmailDraftException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or FormatException or ParseException)
        {
            throw new GmailDraftException(
                MailSendFailureKind.InvalidRequest,
                "Не удалось безопасно открыть черновик Gmail.",
                exception);
        }
    }

    public async Task<GmailDraftIdentity> SaveAsync(
        MailAccount account,
        GmailDraftIdentity? identity,
        MailComposeRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        ArgumentNullException.ThrowIfNull(request);
        if (request.AccountId != account.Id)
        {
            throw new GmailDraftException(
                MailSendFailureKind.InvalidRequest,
                "Параметры черновика Gmail некорректны.");
        }

        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        try
        {
            IReadOnlyList<MaterializedMailAttachment> attachments = request.Attachments.Count == 0
                ? []
                : await attachmentMaterializer.MaterializeAsync(account, request.Attachments, cancellationToken);
            MailMimeSubmission submission = mimeMessageFactory.CreateDraft(account, request, attachments);
            byte[] rawMime = await SerializeAsync(submission.Message, cancellationToken);
            string? threadId = request.ReplyContext?.ProviderThreadId ?? identity?.ThreadId;
            return identity is null
                ? await apiClient.CreateAsync(credential, account.Id, rawMime, threadId, cancellationToken)
                : await apiClient.UpdateAsync(
                    credential,
                    account.Id,
                    identity.DraftId,
                    rawMime,
                    threadId,
                    cancellationToken);
        }
        catch (GmailDraftException)
        {
            throw;
        }
        catch (MailComposeValidationException)
        {
            throw;
        }
        catch (MailAttachmentException exception)
        {
            throw new GmailDraftException(
                exception.FailureKind is MailAttachmentFailureKind.MessageTooLarge
                    ? MailSendFailureKind.MessageTooLarge
                    : MailSendFailureKind.AttachmentUnavailable,
                exception.UserMessage,
                exception);
        }
    }

    public async Task<MailSendResult> SendAsync(
        MailAccount account,
        GmailDraftIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        ArgumentNullException.ThrowIfNull(identity);
        MailCredential credential;
        try
        {
            credential = await LoadCredentialAsync(account, cancellationToken);
            GmailApiSendReceipt receipt = await apiClient.SendAsync(
                credential,
                account.Id,
                identity.DraftId,
                cancellationToken);
            return MailSendResult.Success(receipt.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.CanceledBeforeSubmission,
                "Отправка отменена до передачи письма.");
        }
        catch (GmailDraftException exception)
        {
            return MailSendResult.Failure(exception.FailureKind, exception.UserMessage);
        }
    }

    public async Task DeleteAsync(
        MailAccount account,
        GmailDraftIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        ArgumentNullException.ThrowIfNull(identity);
        MailCredential credential = await LoadCredentialAsync(account, cancellationToken);
        await apiClient.DeleteAsync(
            credential,
            account.Id,
            identity.DraftId,
            cancellationToken);
    }

    private static IReadOnlyList<OutgoingMailAttachment> ExtractAttachments(
        MimeMessage message,
        CancellationToken cancellationToken)
    {
        List<OutgoingMailAttachment> result = [];
        foreach (MailAttachmentInfo attachment in MailMimeAttachmentCatalog.Extract(message)
                     .Where(item => item.IsDownloadable))
        {
            MailAttachmentContent content = MailMimeAttachmentCatalog.GetContent(
                message,
                attachment.AttachmentKey,
                cancellationToken);
            result.Add(OutgoingMailAttachment.FromMemory(content));
        }

        return result;
    }

    private static MailReplyContext? CreateReplyContext(MimeMessage message, string? threadId)
    {
        string? inReplyTo = string.IsNullOrWhiteSpace(message.InReplyTo) ? null : message.InReplyTo;
        string[] references = message.References
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (inReplyTo is null && references.Length == 0 && string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return new MailReplyContext(inReplyTo, references)
        {
            ProviderThreadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId
        };
    }

    private async Task<MailCredential> LoadCredentialAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        MailCredential? credential;
        try
        {
            credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GmailDraftException(
                MailSendFailureKind.CredentialMissing,
                "Не удалось прочитать защищённые данные Gmail.",
                exception);
        }

        if (credential is not { Kind: MailCredentialKind.GmailOAuthRefreshToken } || !credential.IsValid())
        {
            throw new GmailDraftException(
                MailSendFailureKind.ReauthorizationRequired,
                "Требуется вход в Google. Войдите снова, чтобы продолжить работу с черновиком.");
        }

        if (!credential.HasGmailModifyScope)
        {
            throw new GmailDraftException(
                MailSendFailureKind.CapabilityUnavailable,
                "Текущий доступ Google не разрешает работу с черновиками. Подключите Gmail повторно с разрешением gmail.modify.");
        }

        return credential;
    }

    private static async Task<byte[]> SerializeAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using MailSizeLimitedMemoryStream stream = new(MailAttachmentLimits.GmailMaximumRawMessageBytes);
        await message.WriteToAsync(stream, cancellationToken);
        MailAttachmentLimits.ValidateGmailRawMessageSize(stream.Length);
        return stream.ToArray();
    }

    private static void ValidateAccount(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Provider is not MailProviderType.Gmail || account.Id == Guid.Empty || !account.IsEnabled)
        {
            throw new GmailDraftException(
                MailSendFailureKind.InvalidRequest,
                "Почтовый аккаунт Gmail недоступен.");
        }
    }
}

internal sealed class GmailDraftApiClient : IGmailDraftApiClient
{
    public async Task<GmailApiDraftContent> GetAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedDraftSession session = await CreateSessionAsync(credential, accountId, cancellationToken);
            UsersResource.DraftsResource.GetRequest request = session.Service.Users.Drafts.Get("me", draftId);
            request.Format = UsersResource.DraftsResource.GetRequest.FormatEnum.Raw;
            Draft draft = await request.ExecuteAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(draft.Id)
                || string.IsNullOrWhiteSpace(draft.Message?.Id)
                || string.IsNullOrWhiteSpace(draft.Message.Raw))
            {
                throw new GmailDraftException(
                    MailSendFailureKind.InvalidRequest,
                    "Черновик Gmail больше недоступен.");
            }

            return new GmailApiDraftContent(
                ToIdentity(draft),
                GmailApiReadClient.DecodeBase64Url(draft.Message.Raw));
        }
        catch (GmailDraftException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapFailure(exception, "Не удалось открыть черновик Gmail.");
        }
    }

    public Task<GmailDraftIdentity> CreateAsync(
        MailCredential credential,
        Guid accountId,
        byte[] rawMime,
        string? threadId,
        CancellationToken cancellationToken = default) =>
        SaveAsync(credential, accountId, null, rawMime, threadId, cancellationToken);

    public Task<GmailDraftIdentity> UpdateAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        byte[] rawMime,
        string? threadId,
        CancellationToken cancellationToken = default) =>
        SaveAsync(credential, accountId, draftId, rawMime, threadId, cancellationToken);

    public async Task<GmailApiSendReceipt> SendAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        bool submissionStarted = false;
        try
        {
            using AuthorizedDraftSession session = await CreateSessionAsync(credential, accountId, cancellationToken);
            submissionStarted = true;
            GmailMessage sent = await session.Service.Users.Drafts.Send(
                    new Draft { Id = draftId },
                    "me")
                .ExecuteAsync(cancellationToken);
            return new GmailApiSendReceipt(sent.Id, sent.ThreadId);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw submissionStarted ? Ambiguous(exception) : new GmailDraftException(
                MailSendFailureKind.CanceledBeforeSubmission,
                "Отправка отменена до передачи письма.",
                exception);
        }
        catch (Exception exception) when (GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            throw Reauthorization(exception);
        }
        catch (Exception exception) when (submissionStarted && IsTransportFailure(exception))
        {
            throw Ambiguous(exception);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            throw new GmailDraftException(
                MailSendFailureKind.MessageRejected,
                "Gmail отклонил отправку черновика. Проверьте письмо и повторите попытку.",
                exception);
        }
        catch (Exception exception)
        {
            throw new GmailDraftException(
                MailSendFailureKind.ConnectionFailed,
                "Не удалось подключиться к Gmail. Проверьте сеть и повторите попытку.",
                exception);
        }
    }

    public async Task DeleteAsync(
        MailCredential credential,
        Guid accountId,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using AuthorizedDraftSession session = await CreateSessionAsync(credential, accountId, cancellationToken);
            await session.Service.Users.Drafts.Delete("me", draftId).ExecuteAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapFailure(exception, "Не удалось удалить черновик Gmail.");
        }
    }

    private static async Task<GmailDraftIdentity> SaveAsync(
        MailCredential credential,
        Guid accountId,
        string? draftId,
        byte[] rawMime,
        string? threadId,
        CancellationToken cancellationToken)
    {
        bool submissionStarted = false;
        try
        {
            using AuthorizedDraftSession session = await CreateSessionAsync(credential, accountId, cancellationToken);
            Draft body = new()
            {
                Id = draftId,
                Message = new GmailMessage
                {
                    Raw = GmailApiSendClient.EncodeBase64Url(rawMime),
                    ThreadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId
                }
            };
            submissionStarted = true;
            Draft saved = string.IsNullOrWhiteSpace(draftId)
                ? await session.Service.Users.Drafts.Create(body, "me").ExecuteAsync(cancellationToken)
                : await session.Service.Users.Drafts.Update(body, "me", draftId).ExecuteAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(saved.Id))
            {
                throw new GmailDraftException(
                    MailSendFailureKind.ConnectionFailed,
                    "Gmail не подтвердил сохранение черновика.");
            }

            return ToIdentity(saved);
        }
        catch (GmailDraftException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (submissionStarted && string.IsNullOrWhiteSpace(draftId))
            {
                throw new GmailDraftException(
                    MailSendFailureKind.Ambiguous,
                    "Не удалось подтвердить создание черновика. Проверьте папку «Черновики» перед повтором.",
                    exception);
            }

            throw;
        }
        catch (Exception exception) when (
            submissionStarted
            && string.IsNullOrWhiteSpace(draftId)
            && IsTransportFailure(exception))
        {
            throw new GmailDraftException(
                MailSendFailureKind.Ambiguous,
                "Не удалось подтвердить создание черновика. Проверьте папку «Черновики» перед повтором.",
                exception);
        }
        catch (Exception exception)
        {
            throw MapFailure(exception, "Не удалось сохранить черновик Gmail.");
        }
    }

    private static GmailDraftIdentity ToIdentity(Draft draft) =>
        new(
            draft.Id ?? string.Empty,
            string.IsNullOrWhiteSpace(draft.Message?.Id) ? null : draft.Message.Id,
            string.IsNullOrWhiteSpace(draft.Message?.ThreadId) ? null : draft.Message.ThreadId);

    private static async Task<AuthorizedDraftSession> CreateSessionAsync(
        MailCredential credential,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        GoogleAuthorizationCodeFlow flow = new(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = credential.OAuthClientId,
                    ClientSecret = credential.OAuthClientSecret
                },
                Scopes = [GmailOAuthConstants.ModifyScope]
            });
        UserCredential userCredential = new(
            flow,
            accountId.ToString("N"),
            new TokenResponse { RefreshToken = credential.Secret });
        try
        {
            if (!await userCredential.RefreshTokenAsync(cancellationToken)
                || string.IsNullOrWhiteSpace(userCredential.Token.AccessToken))
            {
                throw Reauthorization();
            }

            GmailService service = new(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = userCredential,
                    ApplicationName = GmailOAuthConstants.ApplicationName
                });
            return new AuthorizedDraftSession(flow, service);
        }
        catch
        {
            flow.Dispose();
            throw;
        }
    }

    private static GmailDraftException MapFailure(Exception exception, string fallbackMessage)
    {
        if (GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            return Reauthorization(exception);
        }

        if (exception is GoogleApiException apiException
            && apiException.HttpStatusCode is HttpStatusCode.NotFound)
        {
            return new GmailDraftException(
                MailSendFailureKind.InvalidRequest,
                "Черновик Gmail больше недоступен.",
                exception);
        }

        return new GmailDraftException(
            MailSendFailureKind.ConnectionFailed,
            fallbackMessage,
            exception);
    }

    private static bool IsTransportFailure(Exception exception) =>
        exception is GoogleApiException apiException && apiException.HttpStatusCode >= HttpStatusCode.InternalServerError
        || exception is HttpRequestException
        || exception is IOException
        || exception is TimeoutException
        || exception is InvalidOperationException;

    private static GmailDraftException Reauthorization(Exception? exception = null) =>
        new(
            MailSendFailureKind.ReauthorizationRequired,
            "Требуется вход в Google. Войдите снова, чтобы продолжить работу с черновиком.",
            exception);

    private static GmailDraftException Ambiguous(Exception exception) =>
        new(
            MailSendFailureKind.Ambiguous,
            "Не удалось подтвердить отправку. Перед повторной отправкой проверьте папку «Отправленные».",
            exception);

    private sealed class AuthorizedDraftSession(
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
