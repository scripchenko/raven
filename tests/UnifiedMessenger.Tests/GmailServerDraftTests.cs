using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;
using MimeKit;

namespace UnifiedMessenger.Tests;

public sealed class GmailServerDraftTests
{
    [Fact]
    public async Task EmptyCompose_DoesNotCreateServerDraft()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);

        OpenNew(compose, Account());
        scheduler.ReleaseLatest();
        await compose.CurrentDraftAutosaveTask;

        Assert.Empty(drafts.SaveCalls);
        Assert.Null(compose.DraftSaveStatusText);
    }

    [Fact]
    public async Task MeaningfulEdit_CreatesOnce_ThenUpdatesSameDraftId()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());

        compose.Draft!.TextBody = "first";
        await ReleaseAutosaveAsync(compose, scheduler);
        compose.Draft.TextBody = "second";
        await ReleaseAutosaveAsync(compose, scheduler);

        Assert.Equal(2, drafts.SaveCalls.Count);
        Assert.Null(drafts.SaveCalls[0].Identity);
        Assert.Equal(drafts.SaveCalls[0].ResultDraftId, drafts.SaveCalls[1].Identity?.DraftId);
        Assert.Equal("second", drafts.SaveCalls[1].Request.TextBody);
        Assert.Equal("Saved", compose.DraftSaveStatusText);
    }

    [Fact]
    public async Task RapidEdits_AreCoalescedIntoOneSave()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());

        compose.Draft!.TextBody = "a";
        compose.Draft.TextBody = "ab";
        compose.Draft.TextBody = "abc";
        await ReleaseAutosaveAsync(compose, scheduler);

        MailComposeRequest saved = Assert.Single(drafts.SaveCalls).Request;
        Assert.Equal("abc", saved.TextBody);
    }

    [Fact]
    public async Task Autosave_PreservesRecipientsSubjectBodyAndAttachments()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());

        compose.Draft!.To = "to@example.test";
        compose.Draft.Cc = "cc@example.test";
        compose.Draft.Bcc = "bcc@example.test";
        compose.Draft.Subject = "subject";
        compose.Draft.TextBody = "body";
        compose.Draft.AddLocalAttachments([
            OutgoingMailAttachment.FromMemory(
                new MailAttachmentContent("proof.txt", "text/plain", new byte[] { 1, 2, 3 }))
        ]);
        await ReleaseAutosaveAsync(compose, scheduler);

        MailComposeRequest request = Assert.Single(drafts.SaveCalls).Request;
        Assert.Equal("to@example.test", Assert.Single(request.To).Address);
        Assert.Equal("cc@example.test", Assert.Single(request.Cc).Address);
        Assert.Equal("bcc@example.test", Assert.Single(request.Bcc).Address);
        Assert.Equal("subject", request.Subject);
        Assert.Equal("body", request.TextBody);
        Assert.Single(request.Attachments);
    }

    [Fact]
    public async Task ExistingDraft_OpensEditable_AndUpdateReusesDraftId()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new()
        {
            LoadResult = Loaded("draft-external", "old body")
        };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        MailAccount account = Account();
        compose.ActivateAccount(account);

        Assert.True(await compose.OpenGmailDraftAsync(account, "draft-external"));
        Assert.Equal("old body", compose.Draft!.TextBody);
        Assert.True(compose.CanEdit);
        compose.Draft.TextBody = "new body";
        await ReleaseAutosaveAsync(compose, scheduler);

        SaveCall call = Assert.Single(drafts.SaveCalls);
        Assert.Equal("draft-external", call.Identity?.DraftId);
        Assert.Equal("new body", call.Request.TextBody);
    }

    [Fact]
    public async Task RichExternalDraft_OpensReadOnlyAndCannotBeOverwritten()
    {
        RecordingDraftService drafts = new()
        {
            LoadResult = Loaded("rich-draft", "plain fallback", isReadOnly: true)
        };
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualAutosaveScheduler());
        MailAccount account = Account();
        compose.ActivateAccount(account);

        Assert.True(await compose.OpenGmailDraftAsync(account, "rich-draft"));

        Assert.False(compose.CanEdit);
        Assert.True(compose.IsDraftReadOnly);
        Assert.Contains("Gmail", compose.DraftSaveStatusText, StringComparison.Ordinal);
        Assert.False(compose.SendCommand.CanExecute(null));
        Assert.Empty(drafts.SaveCalls);
    }

    [Fact]
    public async Task ExplicitSend_FlushesLatestDraftAndUsesDraftsSend()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        RecordingSendProvider fallback = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler, fallback);
        OpenNew(compose, Account());
        compose.Draft!.To = "to@example.test";
        compose.Draft.TextBody = "latest";

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Single(drafts.SaveCalls);
        Assert.Equal("latest", drafts.SaveCalls[0].Request.TextBody);
        Assert.Single(drafts.SendCalls);
        Assert.Equal(drafts.SaveCalls[0].ResultDraftId, drafts.SendCalls[0].Identity.DraftId);
        Assert.Equal(0, fallback.CallCount);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task FailedOrAmbiguousSend_KeepsDraft_AndDoesNotRetryAutomatically()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new()
        {
            SendResult = MailSendResult.Failure(
                MailSendFailureKind.Ambiguous,
                "Проверьте папку «Отправленные».")
        };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.To = "to@example.test";
        compose.Draft.TextBody = "body";

        await compose.SendCommand.ExecuteAsync(null);

        Assert.True(compose.IsOpen);
        Assert.Equal(MailSendFailureKind.Ambiguous, compose.FailureKind);
        Assert.Single(drafts.SendCalls);
        Assert.True(drafts.ServerDraftExists);
    }

    [Fact]
    public async Task ReauthorizationFailure_DoesNotAutoSend()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new()
        {
            SaveException = new GmailDraftException(
                MailSendFailureKind.ReauthorizationRequired,
                "Требуется вход в Google.")
        };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        MailAccount account = Account();
        OpenNew(compose, account);
        compose.Draft!.To = "to@example.test";
        compose.Draft.TextBody = "body";

        await compose.SendCommand.ExecuteAsync(null);
        compose.ClearGmailReauthenticationError(account.Id);

        Assert.True(compose.IsOpen);
        Assert.Empty(drafts.SendCalls);
    }

    [Fact]
    public async Task CloseKeepsSavedDraft_WhileExplicitDiscardDeletesIt()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        MailAccount account = Account();
        OpenNew(compose, account);
        compose.Draft!.TextBody = "keep";
        await ReleaseAutosaveAsync(compose, scheduler);

        await compose.CancelCommand.ExecuteAsync(null);

        Assert.False(compose.IsOpen);
        Assert.Empty(drafts.DeleteCalls);
        Assert.True(drafts.ServerDraftExists);

        compose.ActivateAccount(account);
        Assert.True(await compose.OpenGmailDraftAsync(account, drafts.SaveCalls[0].ResultDraftId));
        await compose.DiscardDraftCommand.ExecuteAsync(null);

        Assert.Single(drafts.DeleteCalls);
        Assert.False(drafts.ServerDraftExists);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task DiscardFailure_DoesNotCloseOrClaimSuccess()
    {
        RecordingDraftService drafts = new()
        {
            LoadResult = Loaded("draft-fail", "body"),
            DeleteException = new GmailDraftException(
                MailSendFailureKind.ConnectionFailed,
                "Не удалось удалить черновик Gmail.")
        };
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualAutosaveScheduler());
        MailAccount account = Account();
        compose.ActivateAccount(account);
        await compose.OpenGmailDraftAsync(account, "draft-fail");

        await compose.DiscardDraftCommand.ExecuteAsync(null);

        Assert.True(compose.IsOpen);
        Assert.Equal("Не удалось удалить черновик Gmail.", compose.ErrorMessage);
        Assert.True(drafts.ServerDraftExists);
    }

    [Fact]
    public async Task EditDuringSave_CoalescesToLatestGeneration()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new() { BlockFirstSave = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "generation 1";
        scheduler.ReleaseLatest();
        await drafts.FirstSaveStarted.Task;

        compose.Draft.TextBody = "generation 2";
        scheduler.ReleaseLatest();
        drafts.ReleaseFirstSave();
        await compose.CurrentDraftAutosaveTask;

        Assert.Equal(2, drafts.SaveCalls.Count);
        Assert.Equal("generation 2", drafts.SaveCalls[1].Request.TextBody);
        Assert.All(
            drafts.SaveCalls.Skip(1),
            call => Assert.Equal(drafts.SaveCalls[0].ResultDraftId, call.Identity?.DraftId));
    }

    [Fact]
    public async Task SendWaitsForRunningAutosave_WithoutDuplicateSave()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new() { BlockFirstSave = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.To = "to@example.test";
        compose.Draft.TextBody = "body";
        scheduler.ReleaseLatest();
        await drafts.FirstSaveStarted.Task;

        Task send = compose.SendCommand.ExecuteAsync(null);
        Assert.Empty(drafts.SendCalls);
        drafts.ReleaseFirstSave();
        await send;

        Assert.Single(drafts.SaveCalls);
        Assert.Single(drafts.SendCalls);
    }

    [Fact]
    public async Task AccountSwitch_KeepsLateSaveBoundToOriginalAccount()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new() { BlockFirstSave = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        MailAccount first = Account();
        MailAccount second = Account();
        OpenNew(compose, first);
        compose.Draft!.TextBody = "account A";
        scheduler.ReleaseLatest();
        await drafts.FirstSaveStarted.Task;

        OpenNew(compose, second);
        compose.Draft!.TextBody = "account B";
        drafts.ReleaseFirstSave();
        await ReleaseAutosaveAsync(compose, scheduler);

        Assert.Contains(drafts.SaveCalls, call => call.AccountId == first.Id && call.Request.TextBody == "account A");
        Assert.Contains(drafts.SaveCalls, call => call.AccountId == second.Id && call.Request.TextBody == "account B");
        Assert.NotEqual(
            drafts.SaveCalls.First(call => call.AccountId == first.Id).ResultDraftId,
            drafts.SaveCalls.First(call => call.AccountId == second.Id).ResultDraftId);
    }

    [Fact]
    public async Task ReplyThreading_IsPreservedInServerDraft()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        MailAccount account = Account();
        compose.ActivateAccount(account);
        MailMessageContent message = MessageWithReplyContext("thread-17");

        await compose.ReplyCommand.ExecuteAsync(message);
        await ReleaseAutosaveAsync(compose, scheduler);

        MailReplyContext context = Assert.IsType<MailReplyContext>(Assert.Single(drafts.SaveCalls).Request.ReplyContext);
        Assert.Equal("thread-17", context.ProviderThreadId);
        Assert.Equal("original@example.test", Assert.Single(Assert.Single(drafts.SaveCalls).Request.To).Address);
    }

    [Fact]
    public async Task GmailDraftService_CreatesMimeWithAllFieldsAndAttachment()
    {
        FakeDraftApiClient api = new();
        GmailDraftService service = CreateDraftService(api);
        MailAccount account = Account();
        OutgoingMailAttachment attachment = OutgoingMailAttachment.FromMemory(
            new MailAttachmentContent("proof.txt", "text/plain", "proof"u8.ToArray()));
        MailComposeRequest request = new MailComposeRequestFactory().CreateDraft(
            account,
            new MailComposeInput(
                "to@example.test",
                "cc@example.test",
                "bcc@example.test",
                "subject",
                "body",
                new MailReplyContext("original-id", ["reference-id"])
                {
                    ProviderThreadId = "thread-8"
                })
            {
                Attachments = [attachment]
            });

        GmailDraftIdentity identity = await service.SaveAsync(account, null, request);

        Assert.Equal("draft-created", identity.DraftId);
        Assert.Equal("thread-8", api.LastThreadId);
        using MemoryStream stream = new(Assert.IsType<byte[]>(api.LastRawMime), writable: false);
        MimeMessage message = await MimeMessage.LoadAsync(stream);
        Assert.Equal("to@example.test", Assert.Single(message.To.Mailboxes).Address);
        Assert.Equal("cc@example.test", Assert.Single(message.Cc.Mailboxes).Address);
        Assert.Equal("bcc@example.test", Assert.Single(message.Bcc.Mailboxes).Address);
        Assert.Equal("subject", message.Subject);
        Assert.Equal("body", message.TextBody);
        Assert.Equal("original-id", message.InReplyTo);
        Assert.Contains("reference-id", message.References);
        Assert.Single(message.Attachments);
    }

    [Fact]
    public async Task GmailDraftService_LoadsExternalPlainTextDraftWithAttachment()
    {
        MimeMessage source = new()
        {
            Subject = "external",
            Body = new BodyBuilder
            {
                TextBody = "editable",
                Attachments = { { "note.txt", "note"u8.ToArray() } }
            }.ToMessageBody()
        };
        source.To.Add(MailboxAddress.Parse("to@example.test"));
        source.Cc.Add(MailboxAddress.Parse("cc@example.test"));
        source.Bcc.Add(MailboxAddress.Parse("bcc@example.test"));
        FakeDraftApiClient api = new() { LoadedRawMime = Serialize(source) };
        GmailDraftService service = CreateDraftService(api);

        GmailDraftLoadResult loaded = await service.LoadAsync(Account(), "external-draft");

        Assert.False(loaded.IsReadOnly);
        Assert.Equal("external-draft", loaded.Identity.DraftId);
        Assert.Equal("editable", loaded.Template.TextBody);
        Assert.Equal("to@example.test", loaded.Template.To);
        Assert.Single(loaded.Template.ExistingAttachments);
    }

    [Fact]
    public async Task GmailDraftService_RichHtmlDraftUsesNonDestructiveReadOnlyPolicy()
    {
        MimeMessage source = new()
        {
            Subject = "rich",
            Body = new BodyBuilder
            {
                TextBody = "plain fallback",
                HtmlBody = "<strong>formatted</strong>"
            }.ToMessageBody()
        };
        FakeDraftApiClient api = new() { LoadedRawMime = Serialize(source) };
        GmailDraftService service = CreateDraftService(api);

        GmailDraftLoadResult loaded = await service.LoadAsync(Account(), "rich-draft");

        Assert.True(loaded.IsReadOnly);
        Assert.Contains("cannot save without losing it", loaded.RestrictionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftFolderSummary_PreservesGmailDraftResourceIdSeparately()
    {
        GmailApiSummaryData data = new(
            "message-id",
            "subject",
            "from@example.test",
            1,
            "preview",
            [GmailSystemFolders.Draft])
        {
            DraftId = "draft-id"
        };

        MailMessageSummary summary = GmailMailReadProvider.MapSummary(data);

        Assert.Equal("gmail:message-id", summary.MessageKey);
        Assert.Equal("draft-id", summary.ProviderDraftId);
    }

    [Fact]
    public async Task CloseWithPendingChanges_PerformsFinalSaveWithoutDiscard()
    {
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualAutosaveScheduler());
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "not yet debounced";

        await compose.CancelCommand.ExecuteAsync(null);

        Assert.Single(drafts.SaveCalls);
        Assert.Empty(drafts.DeleteCalls);
        Assert.True(drafts.ServerDraftExists);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task ShutdownFlush_SavesPendingChangesButNeverSends()
    {
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualAutosaveScheduler());
        OpenNew(compose, Account());
        compose.Draft!.Subject = "shutdown draft";

        await compose.FlushPendingGmailDraftsAsync();

        Assert.Single(drafts.SaveCalls);
        Assert.Empty(drafts.SendCalls);
        Assert.True(compose.IsOpen);
    }

    [Fact]
    public async Task DiscardWaitsForRunningAutosaveThenDeletesConfirmedDraft()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new() { BlockFirstSave = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "discard";
        scheduler.ReleaseLatest();
        await drafts.FirstSaveStarted.Task;

        Task discard = compose.DiscardDraftCommand.ExecuteAsync(null);
        Assert.Empty(drafts.DeleteCalls);
        drafts.ReleaseFirstSave();
        await discard;

        Assert.Single(drafts.SaveCalls);
        Assert.Single(drafts.DeleteCalls);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task AmbiguousFirstCreate_IsNotAutomaticallyRetriedOrSent()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new()
        {
            SaveException = new GmailDraftException(
                MailSendFailureKind.Ambiguous,
                "Проверьте папку «Черновики».")
        };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.To = "to@example.test";
        compose.Draft.TextBody = "body";
        await ReleaseAutosaveAsync(compose, scheduler);

        compose.Draft.TextBody = "new edit";
        scheduler.ReleaseLatest();
        await compose.CurrentDraftAutosaveTask;
        await compose.SendCommand.ExecuteAsync(null);
        await compose.DiscardDraftCommand.ExecuteAsync(null);

        Assert.Equal(1, drafts.SaveAttemptCount);
        Assert.Empty(drafts.SendCalls);
        Assert.True(compose.HasDraftSaveError);
        Assert.True(compose.IsOpen);
        Assert.Empty(drafts.DeleteCalls);
    }

    [Fact]
    public async Task ExplicitRetryAfterFailedAutosave_CanCreateDraft()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new()
        {
            SaveException = new GmailDraftException(
                MailSendFailureKind.ConnectionFailed,
                "Temporary")
        };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "body";
        await ReleaseAutosaveAsync(compose, scheduler);
        drafts.SaveException = null;

        await compose.RetryDraftSaveCommand.ExecuteAsync(null);

        Assert.Equal(2, drafts.SaveAttemptCount);
        Assert.Single(drafts.SaveCalls);
        Assert.Equal("Saved", compose.DraftSaveStatusText);
    }

    [Fact]
    public async Task ReplyAllAndForward_KeepExistingPreparationSemanticsInDrafts()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        MailAccount account = Account();
        compose.ActivateAccount(account);
        MailMessageContent source = MessageWithReplyContext("thread-19") with
        {
            ReplyMetadata = MessageWithReplyContext("thread-19").ReplyMetadata! with
            {
                OriginalTo =
                [
                    new MailMessageAddress(string.Empty, account.EmailAddress),
                    new MailMessageAddress(string.Empty, "other@example.test")
                ],
                OriginalCc = [new MailMessageAddress(string.Empty, "copy@example.test")]
            }
        };

        await compose.ReplyAllCommand.ExecuteAsync(source);
        await ReleaseAutosaveAsync(compose, scheduler);
        MailComposeRequest replyAll = Assert.Single(drafts.SaveCalls).Request;
        Assert.Contains(replyAll.To, item => item.Address == "other@example.test");
        Assert.Contains(replyAll.Cc, item => item.Address == "copy@example.test");
        Assert.Equal("thread-19", replyAll.ReplyContext?.ProviderThreadId);

        drafts.SaveCalls.Clear();
        await compose.ForwardCommand.ExecuteAsync(source);
        await ReleaseAutosaveAsync(compose, scheduler);
        MailComposeRequest forward = Assert.Single(drafts.SaveCalls).Request;
        Assert.StartsWith("Fwd:", forward.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Null(forward.ReplyContext);
    }

    [Fact]
    public async Task DraftCreateEvent_InvalidatesLoadedDraftFolderAndRefreshesAfterClose()
    {
        ManualAutosaveScheduler scheduler = new();
        RecordingDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        DraftFolderReadProvider read = new(drafts);
        using MailInboxViewModel inbox = new(new DraftFolderProviderFactory(read), compose);
        MailAccount account = Account();
        await inbox.ActivateAsync(account);
        inbox.SelectedFolder = inbox.Folders.Single(item => item.Kind is MailFolderKind.Drafts);
        await inbox.CurrentFolderLoadTask;
        Assert.False(inbox.IsFolderStateStale(account.Id, MailFolderKind.Drafts));

        compose.NewMessageCommand.Execute(null);
        compose.Draft!.TextBody = "folder freshness";
        await ReleaseAutosaveAsync(compose, scheduler);
        Assert.True(inbox.IsFolderStateStale(account.Id, MailFolderKind.Drafts));

        await compose.CancelCommand.ExecuteAsync(null);
        await inbox.CurrentFolderLoadTask;

        Assert.False(inbox.IsFolderStateStale(account.Id, MailFolderKind.Drafts));
        Assert.Equal("1–1 of 1", inbox.PageRangeText);
    }

    private static MailComposeViewModel CreateCompose(
        RecordingDraftService drafts,
        ManualAutosaveScheduler scheduler,
        RecordingSendProvider? fallback = null) =>
        new(
            new SendProviderFactory(fallback ?? new RecordingSendProvider()),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            new AlwaysConfirmService(),
            attachmentDialogService: null,
            drafts,
            scheduler);

    private static GmailDraftService CreateDraftService(FakeDraftApiClient api) =>
        new(
            new FixedCredentialStore(),
            api,
            new MemoryAttachmentMaterializer(),
            new MailMimeMessageFactory(TimeProvider.System));

    private static byte[] Serialize(MimeMessage message)
    {
        using MemoryStream stream = new();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private static void OpenNew(MailComposeViewModel compose, MailAccount account)
    {
        compose.ActivateAccount(account);
        compose.NewMessageCommand.Execute(null);
    }

    private static async Task ReleaseAutosaveAsync(
        MailComposeViewModel compose,
        ManualAutosaveScheduler scheduler)
    {
        scheduler.ReleaseLatest();
        await compose.CurrentDraftAutosaveTask;
    }

    private static GmailDraftLoadResult Loaded(
        string draftId,
        string body,
        bool isReadOnly = false) =>
        new(
            new GmailDraftIdentity(draftId, $"message-{draftId}", "thread-1"),
            new MailComposeTemplate(
                "to@example.test",
                string.Empty,
                string.Empty,
                "subject",
                body,
                new MailReplyContext("message-id", ["reference-id"])
                {
                    ProviderThreadId = "thread-1"
                })
            {
                IsReadOnly = isReadOnly,
                RestrictionMessage = isReadOnly
                    ? "Этот черновик содержит форматирование. Откройте его в Gmail."
                    : null
            },
            isReadOnly,
            isReadOnly ? "Этот черновик содержит форматирование. Откройте его в Gmail." : null);

    private static MailMessageContent MessageWithReplyContext(string threadId) =>
        new(
            "gmail:source",
            "Subject",
            "Original",
            "original@example.test",
            "account@gmail.test",
            DateTimeOffset.UtcNow,
            MailMessageBodyKind.PlainText,
            "Body",
            [],
            false,
            false,
            "Body",
            new MailReplyMetadata("original@example.test", "message-id", ["reference-id"])
            {
                ProviderThreadId = threadId
            });

    private static MailAccount Account() => new()
    {
        Id = Guid.NewGuid(),
        Provider = MailProviderType.Gmail,
        DisplayName = "Gmail",
        EmailAddress = "account@gmail.test",
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = MailAuthenticationKind.OAuth,
        IsEnabled = true
    };

    private sealed class ManualAutosaveScheduler : IMailDraftAutosaveScheduler
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _waiters = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Assert.Equal(MailComposeViewModel.GmailDraftAutosaveDelay, delay);
            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiters.Add(waiter);
            }

            cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return waiter.Task;
        }

        public void ReleaseLatest()
        {
            TaskCompletionSource? waiter;
            lock (_gate)
            {
                waiter = _waiters.LastOrDefault(item => !item.Task.IsCompleted);
            }

            waiter?.TrySetResult();
        }
    }

    private sealed class FixedCredentialStore : IMailCredentialStore
    {
        private static readonly MailCredential Credential = MailCredential.CreateGmailOAuth(
            "refresh",
            "client",
            "secret",
            GmailOAuthConstants.ModifyScope);

        public Task SaveAsync(
            string credentialKey,
            MailCredential credential,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<MailCredential?> LoadAsync(
            string credentialKey,
            CancellationToken cancellationToken = default) => Task.FromResult<MailCredential?>(Credential);

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MemoryAttachmentMaterializer : IMailOutgoingAttachmentMaterializer
    {
        public Task<IReadOnlyList<MaterializedMailAttachment>> MaterializeAsync(
            MailAccount account,
            IReadOnlyList<OutgoingMailAttachment> attachments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterializedMailAttachment>>(
                attachments.Select(item => new MaterializedMailAttachment(
                    item.FileName,
                    item.ContentType,
                    "proof"u8.ToArray())).ToArray());
    }

    private sealed class FakeDraftApiClient : IGmailDraftApiClient
    {
        public byte[]? LastRawMime { get; private set; }
        public string? LastThreadId { get; private set; }
        public byte[] LoadedRawMime { get; set; } = Serialize(new MimeMessage
        {
            Subject = "draft",
            Body = new TextPart("plain") { Text = "body" }
        });

        public Task<GmailApiDraftContent> GetAsync(
            MailCredential credential,
            Guid accountId,
            string draftId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiDraftContent(
                new GmailDraftIdentity(draftId, "message-loaded", "thread-loaded"),
                LoadedRawMime));

        public Task<GmailDraftIdentity> CreateAsync(
            MailCredential credential,
            Guid accountId,
            byte[] rawMime,
            string? threadId,
            CancellationToken cancellationToken = default)
        {
            LastRawMime = rawMime;
            LastThreadId = threadId;
            return Task.FromResult(new GmailDraftIdentity("draft-created", "message-created", threadId));
        }

        public Task<GmailDraftIdentity> UpdateAsync(
            MailCredential credential,
            Guid accountId,
            string draftId,
            byte[] rawMime,
            string? threadId,
            CancellationToken cancellationToken = default)
        {
            LastRawMime = rawMime;
            LastThreadId = threadId;
            return Task.FromResult(new GmailDraftIdentity(draftId, "message-updated", threadId));
        }

        public Task<GmailApiSendReceipt> SendAsync(
            MailCredential credential,
            Guid accountId,
            string draftId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiSendReceipt("sent", "thread"));

        public Task DeleteAsync(
            MailCredential credential,
            Guid accountId,
            string draftId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record SaveCall(
        Guid AccountId,
        GmailDraftIdentity? Identity,
        MailComposeRequest Request,
        string ResultDraftId);

    private sealed class RecordingDraftService : IGmailDraftService
    {
        private readonly TaskCompletionSource _firstSaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createdCount;

        public List<SaveCall> SaveCalls { get; } = [];
        public List<(Guid AccountId, GmailDraftIdentity Identity)> SendCalls { get; } = [];
        public List<(Guid AccountId, GmailDraftIdentity Identity)> DeleteCalls { get; } = [];
        public TaskCompletionSource FirstSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GmailDraftLoadResult LoadResult { get; set; } = Loaded("draft-1", "body");
        public MailSendResult SendResult { get; set; } = MailSendResult.Success("sent-1");
        public GmailDraftException? SaveException { get; set; }
        public GmailDraftException? DeleteException { get; set; }
        public bool BlockFirstSave { get; set; }
        public bool ServerDraftExists { get; private set; } = true;
        public int SaveAttemptCount { get; private set; }

        public Task<GmailDraftLoadResult> LoadAsync(
            MailAccount account,
            string draftId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LoadResult with
            {
                Identity = LoadResult.Identity with { DraftId = draftId }
            });

        public async Task<GmailDraftIdentity> SaveAsync(
            MailAccount account,
            GmailDraftIdentity? identity,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveAttemptCount++;
            if (SaveException is not null)
            {
                throw SaveException;
            }

            string draftId = identity?.DraftId ?? $"draft-{Interlocked.Increment(ref _createdCount)}-{account.Id:N}";
            SaveCalls.Add(new SaveCall(account.Id, identity, request, draftId));
            if (BlockFirstSave && SaveCalls.Count == 1)
            {
                FirstSaveStarted.TrySetResult();
                await _firstSaveRelease.Task.WaitAsync(cancellationToken);
            }

            ServerDraftExists = true;
            return new GmailDraftIdentity(draftId, $"message-{draftId}", request.ReplyContext?.ProviderThreadId);
        }

        public Task<MailSendResult> SendAsync(
            MailAccount account,
            GmailDraftIdentity identity,
            CancellationToken cancellationToken = default)
        {
            SendCalls.Add((account.Id, identity));
            if (SendResult.IsMessageSent)
            {
                ServerDraftExists = false;
            }

            return Task.FromResult(SendResult);
        }

        public Task DeleteAsync(
            MailAccount account,
            GmailDraftIdentity identity,
            CancellationToken cancellationToken = default)
        {
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            DeleteCalls.Add((account.Id, identity));
            ServerDraftExists = false;
            return Task.CompletedTask;
        }

        public void ReleaseFirstSave() => _firstSaveRelease.TrySetResult();
    }

    private sealed class RecordingSendProvider : IMailSendProvider
    {
        public int CallCount { get; private set; }
        public bool Supports(MailProviderType providerType) => true;

        public Task<MailSendResult> SendAsync(
            MailAccount account,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(MailSendResult.Success());
        }
    }

    private sealed class SendProviderFactory(IMailSendProvider provider) : IMailSendProviderFactory
    {
        public IMailSendProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class DraftFolderReadProvider(RecordingDraftService drafts) : IMailReadProvider
    {
        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>(
                [MailFolderCatalog.Inbox(), MailFolderCatalog.Create(MailFolderKind.Drafts, GmailSystemFolders.Draft)]);

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>([], null, 0));

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (folder.Kind is not MailFolderKind.Drafts || drafts.SaveCalls.Count == 0)
            {
                return Task.FromResult(new MailPage<MailMessageSummary>([], null, 0));
            }

            SaveCall saved = drafts.SaveCalls[^1];
            MailMessageSummary summary = new(
                $"gmail:message-{saved.ResultDraftId}",
                saved.Request.Subject,
                account.EmailAddress,
                account.EmailAddress,
                DateTimeOffset.UtcNow,
                saved.Request.TextBody,
                false)
            {
                ProviderDraftId = saved.ResultDraftId
            };
            return Task.FromResult(new MailPage<MailMessageSummary>([summary], null, 1));
        }

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            Task.FromException<MailMessageContent>(new NotSupportedException());
    }

    private sealed class DraftFolderProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class AlwaysConfirmService : IMailComposeConfirmationService
    {
        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
