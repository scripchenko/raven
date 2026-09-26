using MailKit;
using MimeKit;
using System.Text;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.ViewModels;
using MailFolder = UnifiedMessenger.App.Models.MailFolder;

namespace UnifiedMessenger.Tests;

public sealed class ManagedImapServerDraftTests
{
    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task CreateThenUpdate_AppendsNewBeforeExactOldUidDeletion(
        MailProviderType providerType)
    {
        DraftSession session = new();
        ManagedImapDraftService service = CreateService(session, providerType);
        MailAccount account = Account(providerType);
        MailComposeRequest firstRequest = Request(account, "first") with
        {
            Attachments = [OutgoingMailAttachment.FromMemory(
                new MailAttachmentContent("proof.txt", "text/plain", "proof"u8.ToArray()))]
        };

        ManagedImapDraftSaveResult first = await service.SaveAsync(account, null, null, firstRequest);
        ManagedImapDraftSaveResult second = await service.SaveAsync(
            account,
            first.Identity,
            first.LogicalId,
            Request(account, "second"));

        Assert.True(first.IsSaved);
        Assert.True(second.IsSaved);
        Assert.Equal(first.LogicalId, second.LogicalId);
        Assert.Equal(102u, second.Identity!.UniqueId);
        Assert.Equal(["append:101", "append:102", "delete:101"], session.Writes);
        Assert.All(session.Messages, message =>
            Assert.Equal(first.LogicalId, message.Headers[ManagedImapDraftService.LogicalIdHeader]));
        Assert.Equal("to@example.test", Assert.Single(session.Messages[0].To.Mailboxes).Address);
        Assert.Equal("cc@example.test", Assert.Single(session.Messages[0].Cc.Mailboxes).Address);
        Assert.Equal("bcc@example.test", Assert.Single(session.Messages[0].Bcc.Mailboxes).Address);
        Assert.Single(session.Messages[0].Attachments);
        Assert.Equal("second", session.Messages[^1].TextBody);
    }

    [Fact]
    public async Task ServerReplace_IsPreferredAndKeepsUidScopedIdentity()
    {
        DraftSession session = new() { SupportsReplace = true };
        ManagedImapDraftService service = CreateService(session);
        MailAccount account = Account();
        ManagedImapDraftSaveResult first = await service.SaveAsync(account, null, null, Request(account, "first"));

        ManagedImapDraftSaveResult second = await service.SaveAsync(
            account,
            first.Identity,
            first.LogicalId,
            Request(account, "second"));

        Assert.True(second.IsSaved);
        Assert.Equal(["append:101", "replace:101:102"], session.Writes);
        Assert.Equal(9u, second.Identity!.UidValidity);
        Assert.Equal(102u, second.Identity.UniqueId);
    }

    [Fact]
    public async Task MissingAppendUid_IsReconciledByOpaqueLogicalHeader()
    {
        DraftSession session = new() { ReturnAppendUid = false };
        ManagedImapDraftService service = CreateService(session);
        MailAccount account = Account();

        ManagedImapDraftSaveResult result = await service.SaveAsync(
            account,
            null,
            null,
            Request(account, "body"));

        Assert.True(result.IsSaved);
        Assert.Equal(101u, result.Identity!.UniqueId);
        Assert.Matches("^[0-9a-f]{32}$", result.LogicalId);
    }

    [Fact]
    public async Task ExistingDraftWithoutReplaceOrUidPlus_IsNotMutated()
    {
        DraftSession session = new() { SupportsUidPlus = false };
        ManagedImapDraftService service = CreateService(session);
        MailAccount account = Account();
        const string token = "11111111111111111111111111111111";
        session.Seed(44, token);
        ManagedImapDraftIdentity identity = new("Drafts", 9, 44, token);

        ManagedImapDraftSaveResult result = await service.SaveAsync(
            account,
            identity,
            token,
            Request(account, "new body"));

        Assert.Equal(ManagedImapDraftSaveStatus.Failed, result.Status);
        Assert.Equal(MailSendFailureKind.CapabilityUnavailable, result.FailureKind);
        Assert.Empty(session.Writes);
        Assert.Contains(44u, session.KnownUids);
    }

    [Fact]
    public async Task AmbiguousAppend_DoesNotDeleteOnlyConfirmedOldDraft()
    {
        DraftSession session = new() { ThrowAfterAppend = true };
        ManagedImapDraftService service = CreateService(session);
        MailAccount account = Account();
        const string token = "22222222222222222222222222222222";
        session.Seed(44, token);
        ManagedImapDraftIdentity identity = new("Drafts", 9, 44, token);

        ManagedImapDraftSaveResult result = await service.SaveAsync(
            account,
            identity,
            token,
            Request(account, "new body"));

        Assert.Equal(ManagedImapDraftSaveStatus.Ambiguous, result.Status);
        Assert.Equal(identity, result.Identity);
        Assert.Equal(["append:101"], session.Writes);
        Assert.Contains(44u, session.KnownUids);
    }

    [Fact]
    public async Task UidValidityChange_DropsStaleIdentityWithoutDeletingItsUid()
    {
        DraftSession session = new() { UidValidity = 10 };
        ManagedImapDraftService service = CreateService(session);
        MailAccount account = Account();
        ManagedImapDraftIdentity stale = new("Drafts", 9, 44, "33333333333333333333333333333333");

        ManagedImapDraftSaveResult result = await service.SaveAsync(
            account,
            stale,
            stale.LogicalId,
            Request(account, "new namespace"));

        Assert.True(result.IsSaved);
        Assert.Equal(10u, result.Identity!.UidValidity);
        Assert.DoesNotContain("delete:44", session.Writes);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task LoadExistingDraft_RestoresEnvelopeBodyAttachmentsAndUidIdentity(
        MailProviderType providerType)
    {
        DraftSession session = new();
        const string token = "44444444444444444444444444444444";
        BodyBuilder body = new() { TextBody = "draft body" };
        body.Attachments.Add("proof.txt", "proof"u8.ToArray(), ContentType.Parse("text/plain"));
        MimeMessage message = new()
        {
            Subject = "draft subject",
            Body = body.ToMessageBody()
        };
        message.From.Add(MailboxAddress.Parse("owner@yandex.test"));
        message.To.Add(MailboxAddress.Parse("Recipient <to@example.test>"));
        message.Cc.Add(MailboxAddress.Parse("cc@example.test"));
        message.Bcc.Add(MailboxAddress.Parse("bcc@example.test"));
        message.Headers[ManagedImapDraftService.LogicalIdHeader] = token;
        session.SeedMessage(44, message);
        MailAccount account = Account(providerType);

        ManagedImapDraftLoadResult loaded = await CreateService(session, providerType).LoadAsync(
            account,
            ImapMailReadProvider.CreateMessageKey(MailFolderKind.Drafts, 9, 44));

        Assert.Equal(new ManagedImapDraftIdentity("Drafts", 9, 44, token), loaded.Identity);
        Assert.Contains("to@example.test", loaded.Template.To, StringComparison.Ordinal);
        Assert.Contains("cc@example.test", loaded.Template.Cc, StringComparison.Ordinal);
        Assert.Contains("bcc@example.test", loaded.Template.Bcc, StringComparison.Ordinal);
        Assert.Equal("draft subject", loaded.Template.Subject);
        Assert.Equal("draft body", loaded.Template.TextBody);
        OutgoingMailAttachment attachment = Assert.Single(loaded.Template.ExistingAttachments);
        Assert.Equal("proof.txt", attachment.FileName);
        Assert.False(loaded.Template.IsReadOnly);
    }

    [Fact]
    public async Task ExternalRichHtmlDraft_IsLoadedWithoutDestructiveEditPermission()
    {
        DraftSession session = new();
        MimeMessage message = new()
        {
            Subject = "rich",
            Body = new TextPart("html") { Text = "<strong>formatted</strong>" }
        };
        message.From.Add(MailboxAddress.Parse("owner@yandex.test"));
        session.SeedMessage(45, message);

        ManagedImapDraftLoadResult loaded = await CreateService(session).LoadAsync(
            Account(),
            ImapMailReadProvider.CreateMessageKey(MailFolderKind.Drafts, 9, 45));

        Assert.True(loaded.Template.IsReadOnly);
        Assert.Contains("HTML", loaded.Template.RestrictionMessage, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{32}$", loaded.Identity.LogicalId);
    }

    [Fact]
    public async Task DraftWithoutLanternHeader_GetsOpaqueIdentityAndUpdatesExactOriginalUid()
    {
        DraftSession session = new();
        MimeMessage message = new()
        {
            Subject = "external plain draft",
            Body = new TextPart("plain") { Text = "old" }
        };
        message.From.Add(MailboxAddress.Parse("owner@yandex.test"));
        session.SeedMessage(46, message);
        MailAccount account = Account();
        ManagedImapDraftService service = CreateService(session);
        ManagedImapDraftLoadResult loaded = await service.LoadAsync(account, Key(46));

        ManagedImapDraftSaveResult saved = await service.SaveAsync(
            account,
            loaded.Identity,
            loaded.Identity.LogicalId,
            Request(account, "new"));

        Assert.True(saved.IsSaved);
        Assert.Equal(["append:101", "delete:46"], session.Writes);
        Assert.DoesNotContain(46u, session.KnownUids);
        Assert.Contains(101u, session.KnownUids);
    }

    [Fact]
    public async Task DeleteExistingDraft_ValidatesFolderIdentityAndDeletesExactUidOnly()
    {
        DraftSession session = new();
        const string token = "55555555555555555555555555555555";
        session.Seed(70, token);
        session.Seed(71, Guid.NewGuid().ToString("N"));
        MailAccount account = Account();
        ManagedImapDraftService service = CreateService(session);

        await service.DeleteAsync(account, new ManagedImapDraftIdentity("Drafts", 9, 70, token));

        Assert.Equal(["delete:70"], session.Writes);
        Assert.DoesNotContain(70u, session.KnownUids);
        Assert.Contains(71u, session.KnownUids);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ComposeAutosave_CoalescesAndUpdatesSameLogicalDraft(
        MailProviderType providerType)
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account(providerType));

        compose.Draft!.TextBody = "a";
        compose.Draft.TextBody = "ab";
        compose.Draft.TextBody = "latest";
        await ReleaseAsync(compose, scheduler);
        compose.Draft.TextBody = "next";
        await ReleaseAsync(compose, scheduler);

        Assert.Equal(2, drafts.Saves.Count);
        Assert.Equal("latest", drafts.Saves[0].Request.TextBody);
        Assert.Equal("next", drafts.Saves[1].Request.TextBody);
        Assert.Equal(drafts.Saves[0].Result.LogicalId, drafts.Saves[1].LogicalId);
        Assert.Equal(drafts.Saves[0].Result.Identity, drafts.Saves[1].Identity);
        Assert.Equal("Saved", compose.DraftSaveStatusText);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task AutosaveFailure_PreservesTextAndSuccessfulRetryClearsErrorAndAllowsClose(
        MailProviderType providerType)
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { FailNext = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account(providerType));
        compose.Draft!.TextBody = "must remain";

        await ReleaseAsync(compose, scheduler);
        Assert.Equal("must remain", compose.Draft.TextBody);
        Assert.Equal("Could not save", compose.DraftSaveStatusText);
        Assert.True(compose.RetryDraftSaveCommand.CanExecute(null));

        await compose.RetryDraftSaveCommand.ExecuteAsync(null);
        Assert.Equal("Saved", compose.DraftSaveStatusText);
        Assert.Equal(2, drafts.Saves.Count);
        Assert.Null(compose.FailureKind);
        Assert.Null(compose.ErrorMessage);
        Assert.False(compose.RetryDraftSaveCommand.CanExecute(null));

        await compose.CancelCommand.ExecuteAsync(null);

        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task EditDuringSave_SerializesWritesAndNewestGenerationWins()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { BlockFirstSave = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "generation one";
        scheduler.ReleaseLatest();
        await drafts.FirstSaveStarted.Task;

        compose.Draft.TextBody = "generation two";
        scheduler.ReleaseLatest();
        drafts.ReleaseFirstSave();
        await compose.CurrentDraftAutosaveTask;

        Assert.Equal(2, drafts.Saves.Count);
        Assert.Equal("generation one", drafts.Saves[0].Request.TextBody);
        Assert.Equal("generation two", drafts.Saves[1].Request.TextBody);
        Assert.Equal("Saved", compose.DraftSaveStatusText);
    }

    [Fact]
    public async Task AmbiguousSave_StopsAutomaticWritesAndCannotBlindlyDiscard()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { AmbiguousNext = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "possibly saved";
        await ReleaseAsync(compose, scheduler);

        compose.Draft.TextBody = "newest in memory";
        scheduler.ReleaseLatest();
        await compose.CurrentDraftAutosaveTask;
        await compose.DiscardDraftCommand.ExecuteAsync(null);

        Assert.Single(drafts.Saves);
        Assert.Empty(drafts.Deletes);
        Assert.True(compose.IsOpen);
        Assert.Equal("newest in memory", compose.Draft.TextBody);
        Assert.Equal(MailSendFailureKind.Ambiguous, compose.FailureKind);
    }

    [Fact]
    public async Task CloseKeepsServerDraft_AndSendDeletesOnlyAfterSuccessfulSmtp()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new();
        RecordingSendProvider sender = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler, sender);
        MailAccount account = Account();
        OpenNew(compose, account);
        compose.Draft!.TextBody = "saved";
        await ReleaseAsync(compose, scheduler);

        await compose.CancelCommand.ExecuteAsync(null);
        Assert.False(compose.IsOpen);
        Assert.Empty(drafts.Deletes);

        OpenNew(compose, account);
        compose.Draft!.To = "recipient@example.test";
        compose.Draft.TextBody = "send";
        await compose.SendCommand.ExecuteAsync(null);

        Assert.Equal(1, sender.CallCount);
        Assert.Single(drafts.Deletes);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task SeparateAccounts_NeverShareDraftIdentity()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        MailAccount first = Account();
        MailAccount second = Account();

        OpenNew(compose, first);
        compose.Draft!.TextBody = "first";
        await ReleaseAsync(compose, scheduler);
        OpenNew(compose, second);
        compose.Draft!.TextBody = "second";
        await ReleaseAsync(compose, scheduler);

        Assert.Equal(2, drafts.Saves.Select(call => call.AccountId).Distinct().Count());
        Assert.NotEqual(drafts.Saves[0].Result.LogicalId, drafts.Saves[1].Result.LogicalId);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task SavedDraft_InvalidatesDraftFolderAndRefreshesItAfterComposeCloses(
        MailProviderType providerType)
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        using MailInboxViewModel inbox = new(
            new DraftFolderProviderFactory(new DraftFolderReadProvider(drafts)),
            compose);
        MailAccount account = Account(providerType);
        await inbox.ActivateAsync(account);
        inbox.SelectedFolder = inbox.Folders.Single(folder => folder.Kind is MailFolderKind.Drafts);
        await inbox.CurrentFolderLoadTask;
        Assert.False(inbox.IsFolderStateStale(account.Id, MailFolderKind.Drafts));

        compose.NewMessageCommand.Execute(null);
        compose.Draft!.Subject = "server draft";
        compose.Draft.TextBody = "visible without restart";
        await ReleaseAsync(compose, scheduler);
        Assert.True(inbox.IsFolderStateStale(account.Id, MailFolderKind.Drafts));

        await compose.CancelCommand.ExecuteAsync(null);
        await inbox.CurrentFolderLoadTask;

        Assert.False(inbox.IsFolderStateStale(account.Id, MailFolderKind.Drafts));
        Assert.Single(inbox.Messages);
        Assert.Equal("server draft", inbox.Messages[0].Subject);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task OpenExistingDraft_PopulatesComposeAndTracksReplacementUid(
        MailProviderType providerType)
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { ReplaceUidOnSave = true };
        MailAccount account = Account(providerType);
        drafts.SetLoaded(account, 44, "original body", withAttachment: true);
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        compose.ActivateAccount(account);

        Assert.True(await compose.OpenManagedImapDraftAsync(account, Key(44)));
        Assert.True(compose.IsOpen);
        Assert.Equal("to@example.test", compose.Draft!.To);
        Assert.Equal("cc@example.test", compose.Draft.Cc);
        Assert.Equal("bcc@example.test", compose.Draft.Bcc);
        Assert.Equal("loaded subject", compose.Draft.Subject);
        Assert.Equal("original body", compose.Draft.TextBody);
        Assert.Single(compose.Draft.Attachments);

        compose.Draft.TextBody = "latest body";
        await ReleaseAsync(compose, scheduler);

        SaveCall save = Assert.Single(drafts.Saves);
        Assert.Equal(44u, save.Identity!.UniqueId);
        Assert.Equal(101u, save.Result.Identity!.UniqueId);
        await compose.CancelCommand.ExecuteAsync(null);
        Assert.True(await compose.OpenManagedImapDraftAsync(account, Key(101)));
        Assert.Equal("latest body", compose.Draft!.TextBody);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task OpenDraftSendSuccess_DeletesLatestUid_SendFailureKeepsDraftEditable(
        MailProviderType providerType)
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { ReplaceUidOnSave = true };
        RecordingSendProvider sender = new();
        MailAccount account = Account(providerType);
        drafts.SetLoaded(account, 44, "body");
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler, sender);
        compose.ActivateAccount(account);
        Assert.True(await compose.OpenManagedImapDraftAsync(account, Key(44)));
        compose.Draft!.TextBody = "updated";
        await ReleaseAsync(compose, scheduler);

        sender.Result = MailSendResult.Failure(MailSendFailureKind.ConnectionFailed, "send failed");
        await compose.SendCommand.ExecuteAsync(null);
        Assert.True(compose.IsOpen);
        Assert.Empty(drafts.Deletes);
        Assert.Equal("updated", compose.Draft.TextBody);

        sender.Result = MailSendResult.Success("sent");
        await compose.SendCommand.ExecuteAsync(null);
        Assert.False(compose.IsOpen);
        Assert.Equal(101u, Assert.Single(drafts.Deletes).UniqueId);
    }

    [Fact]
    public async Task DiscardExistingDraft_DeletesOnlyExactLoadedUid()
    {
        RecordingManagedImapDraftService drafts = new();
        MailAccount account = Account();
        drafts.SetLoaded(account, 77, "discard me");
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualScheduler());
        compose.ActivateAccount(account);
        Assert.True(await compose.OpenManagedImapDraftAsync(account, Key(77)));

        await compose.DiscardDraftCommand.ExecuteAsync(null);

        ManagedImapDraftIdentity deleted = Assert.Single(drafts.Deletes);
        Assert.Equal(77u, deleted.UniqueId);
        Assert.False(compose.IsOpen);
    }

    [Fact]
    public async Task ManagedImapDraftRowOpensCompose_NormalInboxRowStillOpensDetail()
    {
        RecordingManagedImapDraftService drafts = new();
        MailAccount account = Account();
        drafts.SetLoaded(account, 44, "draft body");
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualScheduler());
        NavigationReadProvider read = new(account);
        using MailInboxViewModel inbox = new(new DraftFolderProviderFactory(read), compose);
        await inbox.ActivateAsync(account);
        inbox.SelectedFolder = inbox.Folders.Single(folder => folder.Kind is MailFolderKind.Drafts);
        await inbox.CurrentFolderLoadTask;

        MailMessageSummary draft = Assert.Single(inbox.Messages);
        inbox.OpenMessageCommand.Execute(draft);
        await inbox.CurrentMessageLoadTask;

        Assert.True(compose.IsOpen);
        Assert.False(inbox.IsMessageDetailVisible);
        Assert.Null(inbox.SelectedMessageContent);

        await compose.CancelCommand.ExecuteAsync(null);
        inbox.SelectedFolder = inbox.Folders.Single(folder => folder.Kind is MailFolderKind.Inbox);
        await inbox.CurrentFolderLoadTask;
        MailMessageSummary normal = Assert.Single(inbox.Messages);
        inbox.OpenMessageCommand.Execute(normal);
        await inbox.CurrentMessageLoadTask;

        Assert.False(compose.IsOpen);
        Assert.True(inbox.IsMessageDetailVisible);
        Assert.Equal(normal.MessageKey, inbox.SelectedMessageContent!.MessageKey);
    }

    [Fact]
    public async Task ExistingDraftLoad_IsAccountScopedAndFailureLeavesUiUsable()
    {
        RecordingManagedImapDraftService drafts = new();
        MailAccount first = Account();
        MailAccount second = Account();
        drafts.SetLoaded(first, 44, "first");
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualScheduler());
        compose.ActivateAccount(first);
        Assert.True(await compose.OpenManagedImapDraftAsync(first, Key(44)));
        await compose.CancelCommand.ExecuteAsync(null);

        drafts.LoadException = new ManagedImapDraftException(
            MailSendFailureKind.ConnectionFailed,
            "load failed");
        compose.ActivateAccount(second);
        Assert.False(await compose.OpenManagedImapDraftAsync(second, Key(44)));

        Assert.False(compose.IsOpen);
        Assert.Equal("load failed", compose.ErrorMessage);
        Assert.Equal([first.Id, second.Id], drafts.LoadAccounts);
        Assert.True(compose.NewMessageCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExplicitExitFlush_SuccessConfirmsNewestDirtyManagedImapDraft()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "latest before exit";

        ServerDraftFlushResult result = await compose.FlushPendingServerDraftsAsync();

        Assert.Equal(ServerDraftFlushStatus.AllConfirmedSaved, result.Status);
        Assert.True(result.CanShutdown);
        Assert.Equal("latest before exit", Assert.Single(drafts.Saves).Request.TextBody);
    }

    [Fact]
    public async Task ExplicitExitFlush_FailureKeepsComposeIntactAndEditable()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { FailNext = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "unsaved";

        ServerDraftFlushResult result = await compose.FlushPendingServerDraftsAsync();
        compose.Draft.TextBody = "still editable";

        Assert.Equal(ServerDraftFlushStatus.Failed, result.Status);
        Assert.False(result.CanShutdown);
        Assert.True(compose.IsOpen);
        Assert.Equal("still editable", compose.Draft.TextBody);
    }

    [Fact]
    public async Task ExplicitExitFlush_TimeoutReturnsStructuredTimeoutResult()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { BlockFirstSave = true };
        using MailComposeViewModel compose = CreateCompose(
            drafts,
            scheduler,
            finalAutosaveTimeout: TimeSpan.FromMilliseconds(30));
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "dirty";

        ServerDraftFlushResult result = await compose.FlushPendingServerDraftsAsync();

        Assert.Equal(ServerDraftFlushStatus.TimedOutOrCanceled, result.Status);
        Assert.False(result.CanShutdown);
        Assert.True(compose.IsOpen);
    }

    [Fact]
    public async Task ExplicitExitFlush_CancellationReturnsStructuredResultAndKeepsDraft()
    {
        using MailComposeViewModel compose = CreateCompose(
            new RecordingManagedImapDraftService(),
            new ManualScheduler());
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "cancelled but retained";
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        ServerDraftFlushResult result = await compose.FlushPendingServerDraftsAsync(cancellation.Token);

        Assert.Equal(ServerDraftFlushStatus.TimedOutOrCanceled, result.Status);
        Assert.Equal("cancelled but retained", compose.Draft.TextBody);
    }

    [Fact]
    public async Task ExplicitExitFlush_AmbiguousResultCancelsExitWithoutBlindRetry()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { AmbiguousNext = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "possibly saved";

        ServerDraftFlushResult result = await compose.FlushPendingServerDraftsAsync();

        Assert.Equal(ServerDraftFlushStatus.Ambiguous, result.Status);
        Assert.False(result.CanShutdown);
        Assert.Single(drafts.Saves);
        Assert.True(compose.IsOpen);
    }

    [Fact]
    public async Task FailedExit_RetrySucceedsAndFollowingExitCanProceed()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { FailNext = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "retry me";
        Assert.False((await compose.FlushPendingServerDraftsAsync()).CanShutdown);

        await compose.RetryDraftSaveCommand.ExecuteAsync(null);
        ServerDraftFlushResult second = await compose.FlushPendingServerDraftsAsync();

        Assert.Equal(ServerDraftFlushStatus.NoDirtyDrafts, second.Status);
        Assert.True(second.CanShutdown);
        Assert.Equal(2, drafts.Saves.Count);
    }

    [Fact]
    public async Task InFlightAutosaveRacingWithExit_PersistsNewestGeneration()
    {
        ManualScheduler scheduler = new();
        RecordingManagedImapDraftService drafts = new() { BlockFirstSave = true };
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "first";
        scheduler.ReleaseLatest();
        await drafts.FirstSaveStarted.Task;
        compose.Draft.TextBody = "newest";

        Task<ServerDraftFlushResult> flush = compose.FlushPendingServerDraftsAsync();
        drafts.ReleaseFirstSave();
        ServerDraftFlushResult result = await flush;

        Assert.True(result.CanShutdown);
        Assert.Equal("newest", drafts.Saves[^1].Request.TextBody);
        Assert.Equal(2, drafts.Saves.Count);
    }

    [Fact]
    public async Task FailedExitGuard_RestoresCoordinatorWindowAndAllowsLaterExit()
    {
        RecordingManagedImapDraftService drafts = new() { FailNext = true };
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualScheduler());
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "keep open";
        ApplicationExitCoordinator exit = new();
        FakeWindowActivation window = new();
        RecordingFailurePresenter presenter = new();
        ApplicationDraftShutdownGuard guard = new(compose, exit, window, presenter);
        exit.RequestExit();
        Assert.True(exit.TryBeginShutdown());

        Assert.False(await guard.TryPrepareExplicitExitAsync());
        Assert.Equal(ApplicationShutdownState.Running, exit.ShutdownState);
        Assert.False(exit.IsExplicitExitRequested);
        Assert.Equal(1, window.ShowCount);
        Assert.Equal(ServerDraftFlushStatus.Failed, presenter.Result!.Status);
        Assert.True(compose.IsOpen);

        exit.RequestExit();
        Assert.True(exit.TryBeginShutdown());
        Assert.True(await guard.TryPrepareExplicitExitAsync());
    }

    [Fact]
    public void SessionEnding_PersistsDirtyStateWithoutCallingNetwork()
    {
        RecordingManagedImapDraftService drafts = new();
        MemoryRecoveryStore recovery = new();
        using MailComposeViewModel compose = CreateCompose(drafts, new ManualScheduler(), recovery: recovery);
        MailAccount account = Account();
        OpenNew(compose, account);
        compose.Draft!.Subject = "protected";
        ApplicationDraftShutdownGuard guard = new(
            compose,
            new ApplicationExitCoordinator(),
            new FakeWindowActivation(),
            new RecordingFailurePresenter());

        Assert.True(guard.PersistSessionEndingRecovery());
        Assert.Empty(drafts.Saves);
        Assert.Equal("protected", Assert.Single(recovery.Items).Subject);
    }

    [Fact]
    public void SessionEnding_RecoveryWriteFailureKeepsApplicationAccessible()
    {
        MemoryRecoveryStore recovery = new() { UpsertSucceeds = false };
        using MailComposeViewModel compose = CreateCompose(
            new RecordingManagedImapDraftService(),
            new ManualScheduler(),
            recovery: recovery);
        OpenNew(compose, Account());
        compose.Draft!.TextBody = "must not be lost";
        FakeWindowActivation window = new();
        RecordingFailurePresenter presenter = new();
        ApplicationDraftShutdownGuard guard = new(
            compose,
            new ApplicationExitCoordinator(),
            window,
            presenter);

        Assert.False(guard.PersistSessionEndingRecovery());
        Assert.Equal(1, window.ShowCount);
        Assert.True(presenter.RecoveryFailureShown);
        Assert.Equal("must not be lost", compose.Draft.TextBody);
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    public async Task ProtectedRecovery_RestoresCorrectAccountFieldsAndAttachments(
        MailProviderType providerType)
    {
        MemoryRecoveryStore recovery = new();
        RecordingManagedImapDraftService drafts = new();
        MailAccount account = Account(providerType);
        using (MailComposeViewModel source = CreateCompose(drafts, new ManualScheduler(), recovery: recovery))
        {
            OpenNew(source, account);
            source.Draft!.To = "to@example.test";
            source.Draft.Cc = "cc@example.test";
            source.Draft.Bcc = "bcc@example.test";
            source.Draft.Subject = "recover subject";
            source.Draft.TextBody = "recover body";
            source.Draft.AddLocalAttachments([
                OutgoingMailAttachment.FromMemory(
                    new MailAttachmentContent("proof.txt", "text/plain", "proof"u8.ToArray()))]);
            Assert.True(source.PersistDirtyManagedImapDraftRecovery());
        }

        using MailComposeViewModel restored = CreateCompose(drafts, new ManualScheduler(), recovery: recovery);
        await restored.RestoreManagedImapDraftRecoveriesAsync([account]);
        restored.ActivateAccount(account);

        Assert.True(restored.IsOpen);
        Assert.Equal("to@example.test", restored.Draft!.To);
        Assert.Equal("cc@example.test", restored.Draft.Cc);
        Assert.Equal("bcc@example.test", restored.Draft.Bcc);
        Assert.Equal("recover subject", restored.Draft.Subject);
        Assert.Equal("recover body", restored.Draft.TextBody);
        Assert.Equal("proof.txt", Assert.Single(restored.Draft.Attachments).FileName);
    }

    [Fact]
    public async Task RecoveryWithIncompatibleServerIdentity_DetachesBeforeSaving()
    {
        MailAccount account = Account();
        string logicalId = Guid.NewGuid().ToString("N");
        MemoryRecoveryStore recovery = new([
            Snapshot(account, "local newest", new ManagedImapDraftIdentity("Drafts", 9, 44, logicalId), logicalId)]);
        RecordingManagedImapDraftService drafts = new()
        {
            LoadException = new ManagedImapDraftException(MailSendFailureKind.InvalidRequest, "stale")
        };
        ManualScheduler scheduler = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler, recovery: recovery);

        await compose.RestoreManagedImapDraftRecoveriesAsync([account]);
        compose.ActivateAccount(account);
        await ReleaseAsync(compose, scheduler);

        SaveCall save = Assert.Single(drafts.Saves);
        Assert.Null(save.Identity);
        Assert.Null(save.LogicalId);
        Assert.Empty(drafts.Deletes);
        Assert.Equal("local newest", save.Request.TextBody);
    }

    [Fact]
    public async Task RecoveryWithCompatibleIdentity_UpdatesTheConfirmedLogicalDraft()
    {
        MailAccount account = Account();
        RecordingManagedImapDraftService drafts = new();
        drafts.SetLoaded(account, 44, "confirmed server body");
        ManagedImapDraftIdentity identity = drafts.LoadResult!.Identity;
        MemoryRecoveryStore recovery = new([
            Snapshot(account, "newer recovered body", identity, identity.LogicalId)]);
        ManualScheduler scheduler = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler, recovery: recovery);

        await compose.RestoreManagedImapDraftRecoveriesAsync([account]);
        compose.ActivateAccount(account);
        await ReleaseAsync(compose, scheduler);

        SaveCall save = Assert.Single(drafts.Saves);
        Assert.Equal(identity, save.Identity);
        Assert.Equal(identity.LogicalId, save.LogicalId);
        Assert.Equal("newer recovered body", save.Request.TextBody);
    }

    [Fact]
    public async Task Recovery_IsRemovedOnlyAfterConfirmedSaveOrExplicitDiscard()
    {
        MailAccount savedAccount = Account();
        MailAccount discardedAccount = Account();
        MemoryRecoveryStore recovery = new([
            Snapshot(discardedAccount, "discard"),
            Snapshot(savedAccount, "save")]);
        RecordingManagedImapDraftService drafts = new() { FailNext = true };
        ManualScheduler scheduler = new();
        using MailComposeViewModel compose = CreateCompose(drafts, scheduler, recovery: recovery);
        await compose.RestoreManagedImapDraftRecoveriesAsync([savedAccount, discardedAccount]);

        compose.ActivateAccount(savedAccount);
        await ReleaseAsync(compose, scheduler);
        Assert.Contains(recovery.Items, item => item.AccountId == savedAccount.Id);
        await compose.RetryDraftSaveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(recovery.Items, item => item.AccountId == savedAccount.Id);

        compose.ActivateAccount(discardedAccount);
        await compose.DiscardDraftCommand.ExecuteAsync(null);
        Assert.DoesNotContain(recovery.Items, item => item.AccountId == discardedAccount.Id);
    }

    [Fact]
    public async Task Recovery_IsAccountScopedAcrossMultipleYandexAccounts()
    {
        MailAccount first = Account();
        MailAccount second = Account();
        MemoryRecoveryStore recovery = new([
            Snapshot(first, "first body"),
            Snapshot(second, "second body")]);
        using MailComposeViewModel compose = CreateCompose(
            new RecordingManagedImapDraftService(),
            new ManualScheduler(),
            recovery: recovery);

        await compose.RestoreManagedImapDraftRecoveriesAsync([first, second]);
        compose.ActivateAccount(first);
        Assert.Equal("first body", compose.Draft!.TextBody);
        compose.ActivateAccount(second);
        Assert.Equal("second body", compose.Draft!.TextBody);
        Assert.Equal(2, compose.DraftCount);
    }

    [Fact]
    public void FileRecoveryStore_DoesNotPersistDraftContentsInPlaintext()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Lantern-Y9-" + Guid.NewGuid().ToString("N"));
        try
        {
            TestPaths paths = new(folder);
            FileManagedImapDraftRecoveryStore store = new(paths, new XorProtector());
            MailAccount account = Account();
            ManagedImapDraftRecoverySnapshot snapshot = Snapshot(account, "secret-body-unique");
            snapshot = snapshot with { Subject = "secret-subject-unique" };

            Assert.True(store.Upsert([snapshot]));
            byte[] persisted = File.ReadAllBytes(paths.ManagedImapDraftRecoveryFilePath);
            string raw = Encoding.UTF8.GetString(persisted);

            Assert.DoesNotContain("secret-body-unique", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-subject-unique", raw, StringComparison.Ordinal);
            Assert.Equal("secret-body-unique", Assert.Single(store.Load()).TextBody);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StructuredYandexFlush_PreservesExistingGmailDraftFlushBehavior()
    {
        RecordingGmailDraftService gmail = new();
        using MailComposeViewModel compose = new(
            new SendProviderFactory(new RecordingSendProvider()),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            new AlwaysConfirmService(),
            gmailDraftService: gmail,
            draftAutosaveScheduler: new ManualScheduler());
        MailAccount account = new()
        {
            Id = Guid.NewGuid(),
            Provider = MailProviderType.Gmail,
            DisplayName = "Gmail",
            EmailAddress = "owner@gmail.test",
            CredentialKey = Guid.NewGuid().ToString("N"),
            AuthenticationKind = MailAuthenticationKind.OAuth,
            IsEnabled = true
        };
        compose.ActivateAccount(account);
        compose.NewMessageCommand.Execute(null);
        compose.Draft!.TextBody = "gmail draft";

        ServerDraftFlushResult result = await compose.FlushPendingServerDraftsAsync();

        Assert.Equal(ServerDraftFlushStatus.NoDirtyDrafts, result.Status);
        Assert.Equal("gmail draft", Assert.Single(gmail.Saves).TextBody);
    }

    private static ManagedImapDraftRecoverySnapshot Snapshot(
        MailAccount account,
        string body,
        ManagedImapDraftIdentity? identity = null,
        string? logicalId = null) => new(
            account.Id,
            DateTimeOffset.UtcNow,
            identity,
            logicalId,
            "to@example.test",
            "cc@example.test",
            "bcc@example.test",
            "subject",
            body,
            null,
            [],
            []);

    private static string Key(uint uid, uint uidValidity = 9) =>
        ImapMailReadProvider.CreateMessageKey(MailFolderKind.Drafts, uidValidity, uid);

    private static ManagedImapDraftService CreateService(
        DraftSession session,
        MailProviderType providerType = MailProviderType.Yandex) =>
        new(
            new PasswordCredentialStore(),
            new MailProviderFactory([
                new YandexMailProvider(new Validator()),
                new MailRuMailProvider(new Validator())
            ]),
            new DraftSessionFactory(session),
            new MemoryMaterializer(),
            new MailMimeMessageFactory(TimeProvider.System));

    private static MailComposeRequest Request(MailAccount account, string body) =>
        new MailComposeRequestFactory().CreateDraft(
            account,
            new MailComposeInput(
                "to@example.test",
                "cc@example.test",
                "bcc@example.test",
                "subject",
                body));

    private static MailComposeViewModel CreateCompose(
        RecordingManagedImapDraftService drafts,
        ManualScheduler scheduler,
        RecordingSendProvider? sender = null,
        IManagedImapDraftRecoveryStore? recovery = null,
        TimeSpan? finalAutosaveTimeout = null) =>
        new(
            new SendProviderFactory(sender ?? new RecordingSendProvider()),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            new AlwaysConfirmService(),
            attachmentDialogService: null,
            gmailDraftService: null,
            draftAutosaveScheduler: scheduler,
            managedImapDraftService: drafts,
            managedImapDraftRecoveryStore: recovery,
            finalAutosaveTimeout: finalAutosaveTimeout);

    private static void OpenNew(MailComposeViewModel compose, MailAccount account)
    {
        compose.ActivateAccount(account);
        compose.NewMessageCommand.Execute(null);
    }

    private static async Task ReleaseAsync(MailComposeViewModel compose, ManualScheduler scheduler)
    {
        scheduler.ReleaseLatest();
        await compose.CurrentDraftAutosaveTask;
    }

    private static MailAccount Account(MailProviderType providerType = MailProviderType.Yandex) => new()
    {
        Id = Guid.NewGuid(),
        Provider = providerType,
        DisplayName = providerType.ToString(),
        EmailAddress = $"{Guid.NewGuid():N}@{(providerType is MailProviderType.MailRu ? "mail.ru" : "yandex.test")}",
        CredentialKey = Guid.NewGuid().ToString("N"),
        AuthenticationKind = MailAuthenticationKind.Password,
        IsEnabled = true
    };

    private sealed class DraftSessionFactory(DraftSession session) : IManagedImapDraftSessionFactory
    {
        public IManagedImapDraftSession Create() => session;
    }

    private sealed class DraftSession : IManagedImapDraftSession
    {
        private readonly Dictionary<uint, string> _tokens = [];
        private readonly Dictionary<uint, MimeMessage> _storedMessages = [];
        private uint _nextUid = 100;
        public bool IsConnected { get; private set; }
        public bool SupportsReplace { get; set; }
        public bool SupportsUidPlus { get; set; } = true;
        public bool CanDelete { get; set; } = true;
        public bool ReturnAppendUid { get; set; } = true;
        public bool ThrowAfterAppend { get; set; }
        public uint UidValidity { get; set; } = 9;
        public List<uint> KnownUids => _tokens.Keys.Order().ToList();
        public List<string> Writes { get; } = [];
        public List<MimeMessage> Messages { get; } = [];

        public void Seed(uint uid, string token)
        {
            _tokens[uid] = token;
            _nextUid = Math.Max(_nextUid, uid);
        }

        public void SeedMessage(uint uid, MimeMessage message)
        {
            string token = message.Headers[ManagedImapDraftService.LogicalIdHeader] ?? string.Empty;
            _tokens[uid] = token;
            _storedMessages[uid] = message;
            _nextUid = Math.Max(_nextUid, uid);
        }

        public Task ConnectAsync(MailServerSettings server, string secret, CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task<ManagedImapDraftFolderState> OpenDraftsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ManagedImapDraftFolderState("Drafts", UidValidity, SupportsReplace, SupportsUidPlus, CanDelete));

        public Task<IReadOnlyList<uint>> FindByLogicalIdAsync(string logicalId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<uint>>(_tokens
                .Where(item => string.Equals(item.Value, logicalId, StringComparison.Ordinal))
                .Select(item => item.Key)
                .Order()
                .ToArray());

        public Task<bool> ExistsAsync(uint uid, CancellationToken cancellationToken) =>
            Task.FromResult(_tokens.ContainsKey(uid));

        public Task<UniqueId?> AppendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            uint uid = ++_nextUid;
            string token = message.Headers[ManagedImapDraftService.LogicalIdHeader]!;
            _tokens[uid] = token;
            _storedMessages[uid] = message;
            Messages.Add(message);
            Writes.Add($"append:{uid}");
            if (ThrowAfterAppend)
            {
                return Task.FromException<UniqueId?>(new IOException("ambiguous append"));
            }

            return Task.FromResult<UniqueId?>(ReturnAppendUid ? new UniqueId(UidValidity, uid) : null);
        }

        public Task<UniqueId?> ReplaceAsync(uint uid, MimeMessage message, CancellationToken cancellationToken)
        {
            uint replacement = ++_nextUid;
            _tokens.Remove(uid);
            _storedMessages.Remove(uid);
            _tokens[replacement] = message.Headers[ManagedImapDraftService.LogicalIdHeader]!;
            _storedMessages[replacement] = message;
            Messages.Add(message);
            Writes.Add($"replace:{uid}:{replacement}");
            return Task.FromResult<UniqueId?>(new UniqueId(UidValidity, replacement));
        }

        public Task<MimeMessage> GetMessageAsync(uint uid, CancellationToken cancellationToken) =>
            _storedMessages.TryGetValue(uid, out MimeMessage? message)
                ? Task.FromResult(message)
                : Task.FromException<MimeMessage>(new InvalidOperationException("No message was seeded."));

        public Task DeleteExactAsync(uint uid, CancellationToken cancellationToken)
        {
            _tokens.Remove(uid);
            _storedMessages.Remove(uid);
            Writes.Add($"delete:{uid}");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class PasswordCredentialStore : IMailCredentialStore
    {
        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<MailCredential?>(MailCredential.CreatePassword("synthetic-password"));
        public Task SaveAsync(string credentialKey, MailCredential credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class Validator : IMailConnectionValidator
    {
        public Task<MailConnectionValidationResult> ValidateAsync(
            MailConnectionSettings settings,
            string emailAddress,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryMaterializer : IMailOutgoingAttachmentMaterializer
    {
        public Task<IReadOnlyList<MaterializedMailAttachment>> MaterializeAsync(
            MailAccount account,
            IReadOnlyList<OutgoingMailAttachment> attachments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterializedMailAttachment>>(
                attachments.Select(item => new MaterializedMailAttachment(
                    item.FileName,
                    item.ContentType,
                    "attachment"u8.ToArray())).ToArray());
    }

    private sealed class ManualScheduler : IMailDraftAutosaveScheduler
    {
        private readonly List<TaskCompletionSource> _waiters = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Assert.Equal(MailComposeViewModel.GmailDraftAutosaveDelay, delay);
            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
            cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return waiter.Task;
        }

        public void ReleaseLatest() =>
            _waiters.LastOrDefault(waiter => !waiter.Task.IsCompleted)?.TrySetResult();
    }

    private sealed record SaveCall(
        Guid AccountId,
        ManagedImapDraftIdentity? Identity,
        string? LogicalId,
        MailComposeRequest Request,
        ManagedImapDraftSaveResult Result);

    private sealed class RecordingManagedImapDraftService : IManagedImapDraftService
    {
        private uint _uid = 100;
        private readonly TaskCompletionSource _firstSaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailNext { get; set; }
        public bool AmbiguousNext { get; set; }
        public bool ReplaceUidOnSave { get; set; }
        public bool BlockFirstSave { get; set; }
        public TaskCompletionSource FirstSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<SaveCall> Saves { get; } = [];
        public List<ManagedImapDraftIdentity> Deletes { get; } = [];
        public List<Guid> LoadAccounts { get; } = [];
        public ManagedImapDraftLoadResult? LoadResult { get; private set; }
        public ManagedImapDraftException? LoadException { get; set; }

        public Task<ManagedImapDraftLoadResult> LoadAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default)
        {
            LoadAccounts.Add(account.Id);
            if (LoadException is not null)
            {
                return Task.FromException<ManagedImapDraftLoadResult>(LoadException);
            }
            return Task.FromResult(LoadResult ?? throw new InvalidOperationException("No draft was configured."));
        }

        public void SetLoaded(MailAccount account, uint uid, string body, bool withAttachment = false)
        {
            string token = Guid.NewGuid().ToString("N");
            LoadResult = new(
                new ManagedImapDraftIdentity("Drafts", 9, uid, token),
                new MailComposeTemplate(
                    "to@example.test",
                    "cc@example.test",
                    "bcc@example.test",
                    "loaded subject",
                    body)
                {
                    ExistingAttachments = withAttachment
                        ? [OutgoingMailAttachment.FromMemory(
                            new MailAttachmentContent("loaded.txt", "text/plain", "data"u8.ToArray()))]
                        : []
                });
        }

        public async Task<ManagedImapDraftSaveResult> SaveAsync(
            MailAccount account,
            ManagedImapDraftIdentity? identity,
            string? logicalId,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            string token = logicalId ?? Guid.NewGuid().ToString("N");
            if (AmbiguousNext)
            {
                AmbiguousNext = false;
                ManagedImapDraftSaveResult ambiguous = new(
                    ManagedImapDraftSaveStatus.Ambiguous,
                    identity,
                    token,
                    MailSendFailureKind.Ambiguous,
                    "Состояние черновика неизвестно.");
                Saves.Add(new(account.Id, identity, logicalId, request, ambiguous));
                return ambiguous;
            }
            if (FailNext)
            {
                FailNext = false;
                ManagedImapDraftSaveResult failed = new(
                    ManagedImapDraftSaveStatus.Failed,
                    identity,
                    token,
                    MailSendFailureKind.ConnectionFailed,
                    "Не удалось сохранить черновик.");
                Saves.Add(new(account.Id, identity, logicalId, request, failed));
                return failed;
            }

            ManagedImapDraftIdentity savedIdentity = identity is null || ReplaceUidOnSave
                ? new ManagedImapDraftIdentity("Drafts", 9, ++_uid, token)
                : identity;
            ManagedImapDraftSaveResult result = new(ManagedImapDraftSaveStatus.Saved, savedIdentity, token);
            Saves.Add(new(account.Id, identity, logicalId, request, result));
            LoadResult = new(
                savedIdentity,
                new MailComposeTemplate(
                    string.Join("; ", request.To.Select(address => address.Address)),
                    string.Join("; ", request.Cc.Select(address => address.Address)),
                    string.Join("; ", request.Bcc.Select(address => address.Address)),
                    request.Subject,
                    request.TextBody,
                    request.ReplyContext)
                {
                    ExistingAttachments = request.Attachments
                });
            if (BlockFirstSave && Saves.Count == 1)
            {
                FirstSaveStarted.TrySetResult();
                await _firstSaveRelease.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public void ReleaseFirstSave() => _firstSaveRelease.TrySetResult();

        public Task DeleteAsync(
            MailAccount account,
            ManagedImapDraftIdentity identity,
            CancellationToken cancellationToken = default)
        {
            Deletes.Add(identity);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSendProvider : IMailSendProvider
    {
        public int CallCount { get; private set; }
        public MailSendResult Result { get; set; } = MailSendResult.Success("sent");
        public bool Supports(MailProviderType providerType) =>
            MailProviderFeaturePolicies.Get(providerType).IsManagedImap;
        public Task<MailSendResult> SendAsync(
            MailAccount account,
            MailComposeRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class NavigationReadProvider(MailAccount account) : IMailReadProvider
    {
        private readonly MailMessageSummary _inbox = new(
            ImapMailReadProvider.CreateMessageKey(MailFolderKind.Inbox, 9, 10),
            "normal",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "preview",
            false);
        private readonly MailMessageSummary _draft = new(
            Key(44),
            "loaded subject",
            account.EmailAddress,
            account.EmailAddress,
            DateTimeOffset.UtcNow,
            "draft body",
            false);

        public bool Supports(MailProviderType providerType) =>
            MailProviderFeaturePolicies.Get(providerType).IsManagedImap;
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount value, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([
                MailFolderCatalog.Inbox(),
                MailFolderCatalog.Create(MailFolderKind.Drafts, "Drafts")
            ]);
        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(MailAccount value, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>([_inbox], null));
        public Task<MailPage<MailMessageSummary>> GetPageAsync(MailAccount value, MailFolder folder, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>(folder.Kind is MailFolderKind.Drafts ? [_draft] : [_inbox], null));
        public Task<MailMessageContent> GetMessageAsync(MailAccount value, string messageKey, CancellationToken cancellationToken = default) =>
            GetMessageAsync(value, MailFolderCatalog.Inbox(), messageKey, cancellationToken);
        public Task<MailMessageContent> GetMessageAsync(MailAccount value, MailFolder folder, string messageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailMessageContent(
                messageKey,
                "normal",
                "Sender",
                "sender@example.test",
                value.EmailAddress,
                DateTimeOffset.UtcNow,
                MailMessageBodyKind.PlainText,
                "body",
                [],
                false,
                false));
    }

    private sealed class DraftFolderReadProvider(RecordingManagedImapDraftService drafts) : IMailReadProvider
    {
        public bool Supports(MailProviderType providerType) =>
            MailProviderFeaturePolicies.Get(providerType).IsManagedImap;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([
                MailFolderCatalog.Inbox(),
                MailFolderCatalog.Create(MailFolderKind.Drafts, "Drafts")
            ]);

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>([], null));

        public Task<MailPage<MailMessageSummary>> GetPageAsync(
            MailAccount account,
            MailFolder folder,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            SaveCall? saved = drafts.Saves.LastOrDefault(call => call.Result.IsSaved);
            if (folder.Kind is not MailFolderKind.Drafts || saved is null)
            {
                return Task.FromResult(new MailPage<MailMessageSummary>([], null));
            }

            MailMessageSummary summary = new(
                ImapMailReadProvider.CreateMessageKey(
                    MailFolderKind.Drafts,
                    saved.Result.Identity!.UidValidity,
                    saved.Result.Identity.UniqueId),
                saved.Request.Subject,
                account.EmailAddress,
                account.EmailAddress,
                DateTimeOffset.UtcNow,
                saved.Request.TextBody,
                false);
            return Task.FromResult(new MailPage<MailMessageSummary>([summary], null));
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

    private sealed class SendProviderFactory(IMailSendProvider provider) : IMailSendProviderFactory
    {
        public IMailSendProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class MemoryRecoveryStore : IManagedImapDraftRecoveryStore
    {
        private readonly Dictionary<Guid, ManagedImapDraftRecoverySnapshot> _items;

        public MemoryRecoveryStore(IEnumerable<ManagedImapDraftRecoverySnapshot>? items = null)
        {
            _items = (items ?? []).ToDictionary(item => item.AccountId);
        }

        public IReadOnlyList<ManagedImapDraftRecoverySnapshot> Items => _items.Values.ToArray();
        public bool UpsertSucceeds { get; set; } = true;
        public IReadOnlyList<ManagedImapDraftRecoverySnapshot> Load() => Items;

        public bool Upsert(IReadOnlyCollection<ManagedImapDraftRecoverySnapshot> snapshots)
        {
            if (!UpsertSucceeds)
            {
                return false;
            }

            foreach (ManagedImapDraftRecoverySnapshot snapshot in snapshots)
            {
                _items[snapshot.AccountId] = snapshot;
            }
            return true;
        }

        public bool Remove(Guid accountId)
        {
            _items.Remove(accountId);
            return true;
        }
    }

    private sealed class RecordingGmailDraftService : IGmailDraftService
    {
        public List<MailComposeRequest> Saves { get; } = [];
        public Task<GmailDraftLoadResult> LoadAsync(MailAccount account, string draftId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GmailDraftIdentity> SaveAsync(MailAccount account, GmailDraftIdentity? identity, MailComposeRequest request, CancellationToken cancellationToken = default)
        {
            Saves.Add(request);
            return Task.FromResult(identity ?? new GmailDraftIdentity("draft-id", "message-id", null));
        }
        public Task<MailSendResult> SendAsync(MailAccount account, GmailDraftIdentity identity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(MailAccount account, GmailDraftIdentity identity, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeWindowActivation : IWindowActivationService
    {
        public int ShowCount { get; private set; }
        public bool IsMainWindowActive => false;
        public bool IsMainWindowVisible => false;
        public Guid? SelectedServiceId => null;
        public void Attach(System.Windows.Window window, Func<Guid?> selectedServiceId, Action<Guid> selectService) { }
        public void Detach(System.Windows.Window window) { }
        public void ShowAndActivate(Guid? serviceInstanceId = null) => ShowCount++;
    }

    private sealed class RecordingFailurePresenter : IDraftShutdownFailurePresenter
    {
        public ServerDraftFlushResult? Result { get; private set; }
        public bool RecoveryFailureShown { get; private set; }
        public void Show(ServerDraftFlushResult result) => Result = result;
        public void ShowRecoveryWriteFailure() => RecoveryFailureShown = true;
    }

    private sealed class TestPaths(string root) : IAppPaths
    {
        public string RoamingDataFolder => root;
        public string LocalDataFolder => root;
        public string SettingsFilePath => Path.Combine(root, "settings.json");
        public string WebViewDataFolder => Path.Combine(root, "webview");
        public string ManagedImapDraftRecoveryFilePath =>
            Path.Combine(root, "Recovery", "ManagedImapDrafts", "recovery.bin");
        public string LogsFolder => Path.Combine(root, "logs");
    }

    private sealed class XorProtector : IMailCredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext);
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Transform(protectedData);

        private static byte[] Transform(ReadOnlySpan<byte> input)
        {
            byte[] result = input.ToArray();
            for (int index = 0; index < result.Length; index++)
            {
                result[index] ^= 0xA5;
            }
            return result;
        }
    }

    private sealed class AlwaysConfirmService : IMailComposeConfirmationService
    {
        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
