using System.Net;
using Google;
using Google.Apis.Requests;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailOperationReauthenticationTests
{
    [Fact]
    public async Task DetailAuthenticationFailure_OffersGoogleLoginAndReloadsSameMessageAfterReauthentication()
    {
        OperationReadProvider provider = new();
        provider.EnqueueMessageFailure(AuthRequired());
        provider.EnqueueMessage(Content("message", unread: true));
        MailAccount account = Account(1);
        RecordingReauthenticationService reauthentication = new(GmailReauthenticationResult.Success());
        using MailInboxViewModel viewModel = CreateViewModel(provider, reauthentication);

        await ActivateAndOpenAsync(viewModel, account);

        Assert.True(viewModel.RequiresGmailMessageReauthentication);
        Assert.True(viewModel.ReauthenticateGmailCommand.CanExecute(null));
        Assert.False(viewModel.ShowMessageRetryAction);
        Assert.Null(viewModel.SelectedMessageContent);

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.Equal([account.Id], reauthentication.AccountIds);
        Assert.Equal(2, provider.MessageCallCount);
        Assert.Equal("message", viewModel.SelectedMessageContent?.MessageKey);
        Assert.False(viewModel.RequiresGmailReauthentication);
    }

    [Fact]
    public async Task DetailTransientFailure_RemainsRetryAndDoesNotOfferGoogleLogin()
    {
        OperationReadProvider provider = new();
        provider.EnqueueMessageFailure(new MailReadException(
            MailReadFailureKind.ConnectionFailed,
            "Temporary network failure."));
        using MailInboxViewModel viewModel = CreateViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Success()));

        await ActivateAndOpenAsync(viewModel, Account(1));

        Assert.False(viewModel.RequiresGmailReauthentication);
        Assert.False(viewModel.RequiresGmailMessageReauthentication);
        Assert.True(viewModel.ShowMessageRetryAction);
        Assert.True(viewModel.RetryMessageCommand.CanExecute(null));
        Assert.False(viewModel.ReauthenticateGmailCommand.CanExecute(null));
    }

    [Fact]
    public async Task DetailMissingAfterSuccessfulReauthentication_BecomesOrdinaryMessageErrorWithoutAuthLoop()
    {
        OperationReadProvider provider = new();
        provider.EnqueueMessageFailure(AuthRequired());
        provider.EnqueueMessageFailure(new MailReadException(
            MailReadFailureKind.MessageUnavailable,
            "Message is no longer available."));
        using MailInboxViewModel viewModel = CreateViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Success()));
        await ActivateAndOpenAsync(viewModel, Account(1));

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.False(viewModel.RequiresGmailReauthentication);
        Assert.Equal(MailReadFailureKind.MessageUnavailable, viewModel.MessageFailureKind);
        Assert.True(viewModel.ShowMessageRetryAction);
        Assert.True(viewModel.RetryMessageCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualReadMutationAuthenticationFailure_DoesNotReportFalseLocalSuccess(bool initiallyUnread)
    {
        OperationReadProvider provider = new() { MessageIsUnread = initiallyUnread };
        provider.MutationFailure = AuthRequired();
        using MailInboxViewModel viewModel = CreateViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Success()));
        await ActivateAndOpenAsync(viewModel, Account(1));

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.Equal(initiallyUnread, viewModel.SelectedMessageSummary?.IsUnread);
        Assert.Equal(initiallyUnread, viewModel.SelectedMessageContent?.IsUnread);
        Assert.Equal(initiallyUnread, Assert.Single(provider.Mutations).IsRead);
        Assert.True(viewModel.RequiresGmailReadStateReauthentication);
        Assert.True(viewModel.ReauthenticateGmailCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReadMutationRecovery_RefreshesServerStateWithoutAutomaticallyRepeatingMutation()
    {
        OperationReadProvider provider = new() { MessageIsUnread = true };
        provider.MutationFailure = AuthRequired();
        using MailInboxViewModel viewModel = CreateViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Success()));
        await ActivateAndOpenAsync(viewModel, Account(1));
        await viewModel.SetReadStateCommand.ExecuteAsync(null);
        provider.MutationFailure = null;

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.Single(provider.Mutations);
        Assert.True(viewModel.SelectedMessageSummary?.IsUnread);
        Assert.True(viewModel.SelectedMessageContent?.IsUnread);
        Assert.False(viewModel.RequiresGmailReauthentication);

        await viewModel.SetReadStateCommand.ExecuteAsync(null);

        Assert.Equal(2, provider.Mutations.Count);
        Assert.False(viewModel.SelectedMessageSummary?.IsUnread);
        Assert.False(viewModel.SelectedMessageContent?.IsUnread);
    }

    [Fact]
    public async Task AutomaticReadAuthenticationFailure_EntersSoftAuthStateWithoutRetryLoop()
    {
        OperationReadProvider provider = new() { MessageIsUnread = true };
        provider.MutationFailure = AuthRequired();
        ManualDwellScheduler scheduler = new();
        using MailInboxViewModel viewModel = CreateViewModel(
            provider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Success()),
            scheduler: scheduler);
        await ActivateAndOpenAsync(viewModel, Account(1));
        viewModel.SetDetailHostActive(true);

        Task dwell = viewModel.CurrentReadDwellTask;
        scheduler.Complete();
        await dwell;

        Assert.True(viewModel.SelectedMessageSummary?.IsUnread);
        Assert.True(viewModel.SelectedMessageContent?.IsUnread);
        Assert.True(viewModel.RequiresGmailReadStateReauthentication);
        Assert.Single(provider.Mutations);
        Assert.Equal(1, scheduler.RequestCount);

        viewModel.SetDetailHostActive(false);
        viewModel.SetDetailHostActive(true);
        await Task.Yield();

        Assert.Single(provider.Mutations);
        Assert.Equal(1, scheduler.RequestCount);
    }

    [Fact]
    public async Task SendAuthenticationFailure_PreservesCompleteDraftAndRequiresExplicitSendAfterLogin()
    {
        OperationReadProvider readProvider = new();
        QueueSendProvider sendProvider = new(
            AuthSendFailure(),
            MailSendResult.Success("sent-id"));
        MailComposeViewModel compose = CreateCompose(sendProvider);
        RecordingReauthenticationService reauthentication = new(GmailReauthenticationResult.Success());
        using MailInboxViewModel viewModel = CreateViewModel(readProvider, reauthentication, compose);
        MailAccount account = Account(1);
        await viewModel.ActivateAsync(account);
        compose.NewMessageCommand.Execute(null);
        MailComposeDraft draft = compose.Draft!;
        draft.To = "to@example.test";
        draft.Cc = "cc@example.test";
        draft.Bcc = "bcc@example.test";
        draft.Subject = "Preserved subject";
        draft.TextBody = "Preserved body";
        draft.AddLocalAttachments([
            new OutgoingMailAttachment("attachment-id", "draft.txt", "text/plain", 3)
        ]);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Same(draft, compose.Draft);
        AssertDraftPreserved(draft);
        Assert.True(viewModel.RequiresGmailComposeReauthentication);
        Assert.Equal(1, sendProvider.SendCount);

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.Same(draft, compose.Draft);
        AssertDraftPreserved(draft);
        Assert.Equal(1, sendProvider.SendCount);
        Assert.False(compose.HasError);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, sendProvider.SendCount);
        Assert.False(compose.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplyOrForwardDraft_IsPreservedThroughAuthenticationRecovery(bool forward)
    {
        OperationReadProvider readProvider = new();
        QueueSendProvider sendProvider = new(AuthSendFailure());
        MailComposeViewModel compose = CreateCompose(sendProvider);
        using MailInboxViewModel viewModel = CreateViewModel(
            readProvider,
            new RecordingReauthenticationService(GmailReauthenticationResult.Success()),
            compose);
        await viewModel.ActivateAsync(Account(1));
        MailMessageContent source = Content("source", unread: false) with
        {
            ReplyMetadata = new MailReplyMetadata(
                "reply@example.test",
                "source-message@example.test",
                ["older@example.test"])
            {
                ProviderThreadId = "thread-id"
            },
            Attachments =
            [
                new MailAttachmentInfo(
                    "source-attachment",
                    "source.txt",
                    "text/plain",
                    4,
                    IsInline: false,
                    IsDownloadable: true)
            ]
        };
        if (forward)
        {
            await compose.ForwardCommand.ExecuteAsync(source);
            compose.Draft!.To = "forward@example.test";
            compose.Draft.Attachments.Single().IsSelected = true;
        }
        else
        {
            await compose.ReplyCommand.ExecuteAsync(source);
        }

        MailComposeDraft draft = compose.Draft!;
        string to = draft.To;
        string subject = draft.Subject;
        string body = draft.TextBody;
        MailReplyContext? replyContext = draft.ReplyContext;
        bool[] attachmentSelections = draft.Attachments.Select(item => item.IsSelected).ToArray();

        await compose.SendCommand.ExecuteAsync(null);
        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.Same(draft, compose.Draft);
        Assert.Equal(to, draft.To);
        Assert.Equal(subject, draft.Subject);
        Assert.Equal(body, draft.TextBody);
        Assert.Same(replyContext, draft.ReplyContext);
        Assert.Equal(attachmentSelections, draft.Attachments.Select(item => item.IsSelected));
        Assert.Equal(1, sendProvider.SendCount);
    }

    [Fact]
    public async Task ReplyAllDraft_IsPreservedAndIsNotAutomaticallySentAfterAuthenticationRecovery()
    {
        OperationReadProvider readProvider = new();
        QueueSendProvider sendProvider = new(
            AuthSendFailure(),
            MailSendResult.Success("sent-id"));
        MailComposeViewModel compose = CreateCompose(sendProvider);
        RecordingReauthenticationService reauthentication = new(GmailReauthenticationResult.Success());
        using MailInboxViewModel viewModel = CreateViewModel(readProvider, reauthentication, compose);
        MailAccount account = Account(1);
        await viewModel.ActivateAsync(account);
        MailMessageContent source = Content("source", unread: false) with
        {
            ReplyMetadata = new MailReplyMetadata(
                "Reply Target <reply@example.test>",
                "source-message@example.test",
                ["older@example.test"])
            {
                OriginalTo =
                [
                    new MailMessageAddress("Current user", account.EmailAddress),
                    new MailMessageAddress("Second recipient", "second@example.test")
                ],
                OriginalCc = [new MailMessageAddress("Copy", "copy@example.test")],
                ProviderThreadId = "thread-id"
            }
        };
        await compose.ReplyAllCommand.ExecuteAsync(source);
        compose.Draft!.AddLocalAttachments(
        [
            new OutgoingMailAttachment("attachment-id", "reply-all.txt", "text/plain", 4)
        ]);
        MailComposeDraft draft = compose.Draft;
        string to = draft.To;
        string cc = draft.Cc;
        string subject = draft.Subject;
        string body = draft.TextBody;
        MailReplyContext? replyContext = draft.ReplyContext;

        await compose.SendCommand.ExecuteAsync(null);

        Assert.True(viewModel.RequiresGmailComposeReauthentication);
        Assert.Equal(1, sendProvider.SendCount);
        Assert.Same(draft, compose.Draft);

        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);

        Assert.Equal(1, sendProvider.SendCount);
        Assert.Same(draft, compose.Draft);
        Assert.Equal(to, draft.To);
        Assert.Equal(cc, draft.Cc);
        Assert.Equal(subject, draft.Subject);
        Assert.Equal(body, draft.TextBody);
        Assert.Same(replyContext, draft.ReplyContext);
        Assert.Equal("reply-all.txt", Assert.Single(draft.Attachments).FileName);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, sendProvider.SendCount);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task GmailAuthenticationStateAndDraft_AreIsolatedPerAccount()
    {
        OperationReadProvider readProvider = new();
        QueueSendProvider sendProvider = new(AuthSendFailure());
        MailComposeViewModel compose = CreateCompose(sendProvider);
        RecordingReauthenticationService reauthentication = new(GmailReauthenticationResult.Success());
        using MailInboxViewModel viewModel = CreateViewModel(readProvider, reauthentication, compose);
        MailAccount first = Account(1);
        MailAccount second = Account(2);
        await viewModel.ActivateAsync(first);
        compose.NewMessageCommand.Execute(null);
        compose.Draft!.To = "first@example.test";
        compose.Draft.Subject = "First draft";
        await compose.SendCommand.ExecuteAsync(null);
        MailComposeDraft firstDraft = compose.Draft;

        await viewModel.ActivateAsync(second);

        Assert.False(viewModel.RequiresGmailReauthentication);
        Assert.False(viewModel.RequiresGmailComposeReauthentication);
        Assert.Null(compose.Draft);

        await viewModel.ActivateAsync(first);

        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.True(viewModel.RequiresGmailComposeReauthentication);
        Assert.Same(firstDraft, compose.Draft);
        await viewModel.ReauthenticateGmailCommand.ExecuteAsync(null);
        Assert.Equal([first.Id], reauthentication.AccountIds);
    }

    [Fact]
    public async Task MissingGmailCredential_IsTypedAsReauthenticationRequiredBeforeSubmission()
    {
        RecordingGmailApiSendClient apiClient = new();
        GmailMailSendProvider provider = new(
            new NullCredentialStore(),
            apiClient,
            new MailMimeMessageFactory(TimeProvider.System));
        MailAccount account = Account(1);
        MailComposeRequest request = new MailComposeRequestFactory().Create(
            account,
            new MailComposeInput(
                "to@example.test",
                string.Empty,
                string.Empty,
                "Subject",
                "Body"));

        MailSendResult result = await provider.SendAsync(account, request);

        Assert.Equal(MailSendFailureKind.ReauthorizationRequired, result.FailureKind);
        Assert.False(result.IsMessageSent);
        Assert.Equal(0, apiClient.SendCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public void GoogleApiFailures_AreClassifiedWithoutTurningTransientFailuresIntoReauthentication(
        HttpStatusCode statusCode,
        bool expectedReauthentication)
    {
        GoogleApiException exception = new("Gmail", "sanitized failure")
        {
            HttpStatusCode = statusCode
        };

        Assert.Equal(
            expectedReauthentication,
            GmailAuthorizationFailureClassifier.RequiresReauthorization(exception));
    }

    [Fact]
    public void ForbiddenGoogleAuthReason_RequiresReauthenticationWithoutClassifyingOtherForbiddenFailures()
    {
        GoogleApiException authenticationFailure = new("Gmail", "sanitized failure")
        {
            HttpStatusCode = HttpStatusCode.Forbidden,
            Error = new RequestError
            {
                Errors = [new SingleError { Reason = "authError" }]
            }
        };
        GoogleApiException quotaFailure = new("Gmail", "sanitized failure")
        {
            HttpStatusCode = HttpStatusCode.Forbidden,
            Error = new RequestError
            {
                Errors = [new SingleError { Reason = "rateLimitExceeded" }]
            }
        };

        Assert.True(GmailAuthorizationFailureClassifier.RequiresReauthorization(authenticationFailure));
        Assert.False(GmailAuthorizationFailureClassifier.RequiresReauthorization(quotaFailure));
    }

    private static async Task ActivateAndOpenAsync(MailInboxViewModel viewModel, MailAccount account)
    {
        await viewModel.ActivateAsync(account);
        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;
    }

    private static MailInboxViewModel CreateViewModel(
        OperationReadProvider readProvider,
        IGmailReauthenticationService reauthentication,
        MailComposeViewModel? compose = null,
        IMailReadDwellScheduler? scheduler = null) =>
        new(
            new OperationReadProviderFactory(readProvider, reauthentication),
            compose,
            attachmentSaveService: null,
            messageSourceCache: null,
            scheduler ?? SystemMailReadDwellScheduler.Instance);

    private static MailComposeViewModel CreateCompose(IMailSendProvider sendProvider) =>
        new(
            new SendProviderFactory(sendProvider),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            new AlwaysConfirmService());

    private static MailAccount Account(int number) => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Gmail,
        DisplayName = $"Gmail {number}",
        EmailAddress = $"account{number}@gmail.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = MailAuthenticationKind.OAuth,
        IsEnabled = true
    };

    private static MailReadException AuthRequired() =>
        new(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");

    private static MailSendResult AuthSendFailure() =>
        MailSendResult.Failure(
            MailSendFailureKind.ReauthorizationRequired,
            "Требуется вход в Google. Войдите снова и затем нажмите «Отправить».");

    private static MailMessageSummary Summary(string key, bool unread) =>
        new(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            unread);

    private static MailMessageContent Content(string key, bool unread) =>
        new(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            "recipient@example.test",
            DateTimeOffset.UtcNow,
            MailMessageBodyKind.PlainText,
            "Body",
            [],
            unread,
            false);

    private static void AssertDraftPreserved(MailComposeDraft draft)
    {
        Assert.Equal("to@example.test", draft.To);
        Assert.Equal("cc@example.test", draft.Cc);
        Assert.Equal("bcc@example.test", draft.Bcc);
        Assert.Equal("Preserved subject", draft.Subject);
        Assert.Equal("Preserved body", draft.TextBody);
        MailComposeAttachmentItem attachment = Assert.Single(draft.Attachments);
        Assert.Equal("draft.txt", attachment.FileName);
        Assert.True(attachment.IsSelected);
    }

    private sealed class OperationReadProviderFactory(
        OperationReadProvider provider,
        IGmailReauthenticationService reauthentication) : IMailReadProviderFactory
    {
        public IGmailReauthenticationService? GmailReauthenticationService => reauthentication;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class OperationReadProvider : IMailReadProvider, IMailMessageStateProvider
    {
        private readonly Queue<object> _messageResponses = [];

        public bool MessageIsUnread { get; set; } = true;
        public MailReadException? MutationFailure { get; set; }
        public int MessageCallCount { get; private set; }
        public List<ReadMutation> Mutations { get; } = [];

        public void EnqueueMessage(MailMessageContent content) => _messageResponses.Enqueue(content);
        public void EnqueueMessageFailure(MailReadException exception) => _messageResponses.Enqueue(exception);
        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>([Summary("message", MessageIsUnread)], null));

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            MessageCallCount++;
            object response = _messageResponses.TryDequeue(out object? queued)
                ? queued
                : Content(messageKey, MessageIsUnread);
            return response is Exception exception
                ? Task.FromException<MailMessageContent>(exception)
                : Task.FromResult((MailMessageContent)response);
        }

        public Task<MailReadStateCapability> GetReadStateCapabilityAsync(
            MailAccount account,
            MailFolder folder,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MailReadStateCapability.Available);

        public Task SetReadStateAsync(
            MailAccount account,
            MailFolder folder,
            string messageKey,
            bool isRead,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add(new ReadMutation(account.Id, messageKey, isRead));
            if (MutationFailure is not null)
            {
                return Task.FromException(MutationFailure);
            }

            MessageIsUnread = !isRead;
            return Task.CompletedTask;
        }
    }

    private sealed record ReadMutation(Guid AccountId, string MessageKey, bool IsRead);

    private sealed class QueueSendProvider(params MailSendResult[] results) : IMailSendProvider
    {
        private readonly Queue<MailSendResult> _results = new(results);
        public int SendCount { get; private set; }
        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<MailSendResult> SendAsync(
            MailAccount account,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class SendProviderFactory(IMailSendProvider provider) : IMailSendProviderFactory
    {
        public IMailSendProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class RecordingReauthenticationService(GmailReauthenticationResult result)
        : IGmailReauthenticationService
    {
        public List<Guid> AccountIds { get; } = [];

        public Task<GmailReauthenticationResult> ReauthenticateAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            AccountIds.Add(account.Id);
            return Task.FromResult(result);
        }
    }

    private sealed class AlwaysConfirmService : IMailComposeConfirmationService
    {
        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class ManualDwellScheduler : IMailReadDwellScheduler
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            RequestCount++;
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult(true);
    }

    private sealed class NullCredentialStore : IMailCredentialStore
    {
        public Task SaveAsync(
            string credentialKey,
            MailCredential credential,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<MailCredential?> LoadAsync(
            string credentialKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MailCredential?>(null);

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingGmailApiSendClient : IGmailApiSendClient
    {
        public int SendCount { get; private set; }

        public Task<GmailApiSendReceipt> SendAsync(
            MailCredential credential,
            Guid accountId,
            byte[] rawMime,
            string? threadId,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(new GmailApiSendReceipt("message-id", threadId));
        }
    }
}
