using System.IO;
using System.Net;
using System.Net.Http;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using UnifiedMessenger.App.Models;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace UnifiedMessenger.App.Services.Mail;

internal sealed class GmailMailSendProvider(
    IMailCredentialStore credentialStore,
    IGmailApiSendClient apiClient,
    IMailMimeMessageFactory mimeMessageFactory,
    IMailOutgoingAttachmentMaterializer? attachmentMaterializer = null) : IMailSendProvider
{
    public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

    public async Task<MailSendResult> SendAsync(
        MailAccount account,
        MailComposeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(request);
        if (account.Provider is not MailProviderType.Gmail || account.Id != request.AccountId)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.InvalidRequest,
                L.Instance.Get("Invalid Gmail send settings."));
        }

        MailCredential? credential;
        try
        {
            credential = await credentialStore.LoadAsync(account.CredentialKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.CanceledBeforeSubmission,
                L.Instance.Get("Sending was canceled before the message was transmitted."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.CredentialMissing,
                L.Instance.Get("Could not read protected Gmail credentials."));
        }

        if (credential is not { Kind: MailCredentialKind.GmailOAuthRefreshToken } || !credential.IsValid())
        {
            return MailSendResult.Failure(
                MailSendFailureKind.ReauthorizationRequired,
                L.Instance.Get("Google sign-in is required. Sign in again, then select Send."));
        }

        if (!credential.HasGmailModifyScope)
        {
            return MailSendResult.Failure(
                MailSendFailureKind.CapabilityUnavailable,
                L.Instance.Get("Your current Google access does not allow sending. Reconnect Gmail with gmail.modify permission."));
        }

        try
        {
            IReadOnlyList<MaterializedMailAttachment> attachments = request.Attachments.Count == 0
                ? []
                : attachmentMaterializer is not null
                    ? await attachmentMaterializer.MaterializeAsync(account, request.Attachments, cancellationToken)
                    : throw new MailAttachmentException(
                        MailAttachmentFailureKind.Unavailable,
                        L.Instance.Get("Attachments are unavailable for sending."));
            MailMimeSubmission submission = mimeMessageFactory.Create(account, request, attachments);
            using MailSizeLimitedMemoryStream stream = new(MailAttachmentLimits.GmailMaximumRawMessageBytes);
            await submission.Message.WriteToAsync(stream, cancellationToken);
            MailAttachmentLimits.ValidateGmailRawMessageSize(stream.Length);
            GmailApiSendReceipt receipt = await apiClient.SendAsync(
                credential,
                account.Id,
                stream.ToArray(),
                request.ReplyContext?.ProviderThreadId,
                cancellationToken);
            return MailSendResult.Success(receipt.MessageId);
        }
        catch (MailComposeValidationException exception)
        {
            return MailSendResult.Failure(MailSendFailureKind.InvalidRequest, exception.UserMessage);
        }
        catch (MailSubmissionException exception)
        {
            return MailSendResult.Failure(exception.FailureKind, exception.UserMessage);
        }
        catch (MailAttachmentException exception)
        {
            return MailSendResult.Failure(
                exception.FailureKind is MailAttachmentFailureKind.MessageTooLarge
                    ? MailSendFailureKind.MessageTooLarge
                    : MailSendFailureKind.AttachmentUnavailable,
                exception.UserMessage);
        }
    }
}

internal sealed class GmailApiSendClient : IGmailApiSendClient
{
    public async Task<GmailApiSendReceipt> SendAsync(
        MailCredential credential,
        Guid accountId,
        byte[] rawMime,
        string? threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(rawMime);
        bool submissionStarted = false;
        GoogleAuthorizationCodeFlow? flow = null;
        GmailService? service = null;
        try
        {
            flow = CreateFlow(credential);
            UserCredential userCredential = new(
                flow,
                accountId.ToString("N"),
                new TokenResponse { RefreshToken = credential.Secret });
            if (!await userCredential.RefreshTokenAsync(cancellationToken)
                || string.IsNullOrWhiteSpace(userCredential.Token.AccessToken))
            {
                throw new MailSubmissionException(
                    MailSendFailureKind.ReauthorizationRequired,
                    L.Instance.Get("Google sign-in is required. Sign in again, then select Send."));
            }

            service = new GmailService(
                new BaseClientService.Initializer
                {
                    HttpClientInitializer = userCredential,
                    ApplicationName = GmailOAuthConstants.ApplicationName
                });
            GmailMessage message = new()
            {
                Raw = EncodeBase64Url(rawMime),
                ThreadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId
            };
            submissionStarted = true;
            GmailMessage sent = await service.Users.Messages.Send(message, "me")
                .ExecuteAsync(cancellationToken);
            return new GmailApiSendReceipt(sent.Id, sent.ThreadId);
        }
        catch (MailSubmissionException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw submissionStarted
                ? Ambiguous(exception)
                : new MailSubmissionException(
                    MailSendFailureKind.CanceledBeforeSubmission,
                    L.Instance.Get("Sending was canceled before the message was transmitted."),
                    exception);
        }
        catch (Exception exception) when (GmailAuthorizationFailureClassifier.RequiresReauthorization(exception))
        {
            throw new MailSubmissionException(
                MailSendFailureKind.ReauthorizationRequired,
                L.Instance.Get("Google sign-in is required. Sign in again, then select Send."),
                exception);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            throw new MailSubmissionException(
                MailSendFailureKind.MessageRejected,
                L.Instance.Get("Gmail rejected the message. Check addresses and retry."),
                exception);
        }
        catch (Exception exception) when (submissionStarted && IsAmbiguousTransportFailure(exception))
        {
            throw Ambiguous(exception);
        }
        catch (Exception exception) when (IsAmbiguousTransportFailure(exception))
        {
            throw new MailSubmissionException(
                MailSendFailureKind.ConnectionFailed,
                L.Instance.Get("Could not connect to Gmail. Check your network and retry."),
                exception);
        }
        finally
        {
            service?.Dispose();
            flow?.Dispose();
        }
    }

    internal static string EncodeBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static GoogleAuthorizationCodeFlow CreateFlow(MailCredential credential) =>
        new(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = credential.OAuthClientId,
                    ClientSecret = credential.OAuthClientSecret
                },
                Scopes = [GmailOAuthConstants.ModifyScope]
            });

    private static bool IsAmbiguousTransportFailure(Exception exception) =>
        exception is GoogleApiException apiException && apiException.HttpStatusCode >= HttpStatusCode.InternalServerError
        || exception is HttpRequestException
        || exception is IOException
        || exception is TimeoutException
        || exception is InvalidOperationException;

    private static MailSubmissionException Ambiguous(Exception exception) =>
        new(
            MailSendFailureKind.Ambiguous,
            L.Instance.Get("Could not confirm sending. Check Sent before sending again."),
            exception);
}
