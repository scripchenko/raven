using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class GmailMailboxManagementTests
{
    [Fact]
    public async Task Service_StarUnstarArchiveAndReadUseLabelMutations()
    {
        FakeGmailApiClient api = new();
        GmailMailboxManagementService service = CreateService(api);
        MailAccount account = Account(1);

        await service.SetStarredAsync(account, ["gmail:first"], true);
        Assert.Equal([GmailSystemFolders.Starred], api.Mutations[0].Added);
        Assert.Empty(api.Mutations[0].Removed);

        await service.SetStarredAsync(account, ["gmail:first"], false);
        Assert.Equal([GmailSystemFolders.Starred], api.Mutations[1].Removed);

        await service.ArchiveAsync(account, ["gmail:first"]);
        Assert.Equal([GmailSystemFolders.Inbox], api.Mutations[2].Removed);

        await service.SetReadStateAsync(account, ["gmail:first"], true);
        Assert.Equal([GmailSystemFolders.Unread], api.Mutations[3].Removed);
        await service.SetReadStateAsync(account, ["gmail:first"], false);
        Assert.Equal([GmailSystemFolders.Unread], api.Mutations[4].Added);
    }

    [Fact]
    public async Task Service_MultiMessageLabelsUseNativeBatchesOfAtMostOneThousand()
    {
        FakeGmailApiClient api = new();
        GmailMailboxManagementService service = CreateService(api);
        string[] keys = Enumerable.Range(0, 1001).Select(index => $"gmail:{index}").ToArray();

        GmailMailboxMutationResult result = await service.SetStarredAsync(Account(1), keys, true);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, api.Mutations.Count);
        Assert.Equal(1000, api.Mutations[0].MessageIds.Count);
        Assert.Single(api.Mutations[1].MessageIds);
    }

    [Fact]
    public async Task Service_TrashReturnsHonestPartialResultAndNeverPermanentlyDeletes()
    {
        FakeGmailApiClient api = new() { TrashFailureId = "second" };
        GmailMailboxManagementService service = CreateService(api);

        GmailMailboxMutationResult result = await service.MoveToTrashAsync(
            Account(1),
            ["gmail:first", "gmail:second"]);

        Assert.True(result.IsPartialSuccess);
        Assert.Equal(["gmail:first"], result.SucceededMessageKeys);
        Assert.Equal("gmail:second", Assert.Single(result.FailedMessages).MessageKey);
        Assert.Equal(["first", "second"], api.TrashedIds);
        Assert.DoesNotContain(
            typeof(IGmailMailboxApiClient).GetMethods(),
            method => method.Name.Contains("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Service_RestoreUsesDedicatedUntrashAndReturnsHonestPartialResult()
    {
        FakeGmailApiClient api = new() { UntrashFailureId = "second" };
        GmailMailboxManagementService service = CreateService(api);

        GmailMailboxMutationResult result = await service.RestoreFromTrashAsync(
            Account(1),
            ["gmail:first", "gmail:second"]);

        Assert.True(result.IsPartialSuccess);
        Assert.Equal(["gmail:first"], result.SucceededMessageKeys);
        Assert.Equal("gmail:second", Assert.Single(result.FailedMessages).MessageKey);
        Assert.Equal(["first", "second"], api.UntrashedIds);
        Assert.Empty(api.Mutations);
    }

    [Fact]
    public async Task Service_NotSpamAddsInboxRemovesSpamAndUsesNativeBatching()
    {
        FakeGmailApiClient api = new();
        GmailMailboxManagementService service = CreateService(api);
        string[] keys = Enumerable.Range(0, 1001).Select(index => $"gmail:{index}").ToArray();

        GmailMailboxMutationResult result = await service.MarkNotSpamAsync(Account(1), keys);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, api.Mutations.Count);
        Assert.Equal(1000, api.Mutations[0].MessageIds.Count);
        Assert.Single(api.Mutations[1].MessageIds);
        Assert.All(api.Mutations, mutation =>
        {
            Assert.Equal([GmailSystemFolders.Inbox], mutation.Added);
            Assert.Equal([GmailSystemFolders.Spam], mutation.Removed);
        });
    }

    [Fact]
    public async Task Service_NotSpamReportsPartialFailureAcrossNativeBatches()
    {
        FakeGmailApiClient api = new() { MutationFailureCall = 2 };
        GmailMailboxManagementService service = CreateService(api);
        string[] keys = Enumerable.Range(0, 1001).Select(index => $"gmail:{index}").ToArray();

        GmailMailboxMutationResult result = await service.MarkNotSpamAsync(Account(1), keys);

        Assert.True(result.IsPartialSuccess);
        Assert.Equal(1000, result.SucceededMessageKeys.Count);
        Assert.Equal("gmail:1000", Assert.Single(result.FailedMessages).MessageKey);
    }

    [Fact]
    public async Task Service_ReportSpamAddsSpamRemovesInboxAndUsesNativeBatching()
    {
        FakeGmailApiClient api = new();
        GmailMailboxManagementService service = CreateService(api);
        string[] keys = Enumerable.Range(0, 1001).Select(index => $"gmail:{index}").ToArray();

        GmailMailboxMutationResult result = await service.ReportSpamAsync(Account(1), keys);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, api.Mutations.Count);
        Assert.Equal(1000, api.Mutations[0].MessageIds.Count);
        Assert.Single(api.Mutations[1].MessageIds);
        Assert.All(api.Mutations, mutation =>
        {
            Assert.Equal([GmailSystemFolders.Spam], mutation.Added);
            Assert.Equal([GmailSystemFolders.Inbox], mutation.Removed);
        });
    }

    [Fact]
    public async Task Service_UserLabelsAreAccountScopedCachedAndSystemLabelCannotEnterGenericMutation()
    {
        FakeGmailApiClient api = new();
        api.LabelsByAccount[Account(1).Id] = [new GmailApiUserLabel("Label_1", "Projects")];
        api.LabelsByAccount[Account(2).Id] = [new GmailApiUserLabel("Label_2", "Family")];
        GmailMailboxManagementService service = CreateService(api);

        GmailUserLabelResult first = await service.GetUserLabelsAsync(Account(1));
        GmailUserLabelResult firstCached = await service.GetUserLabelsAsync(Account(1));
        GmailUserLabelResult second = await service.GetUserLabelsAsync(Account(2));
        GmailMailboxMutationResult unsafeResult = await service.SetUserLabelAsync(
            Account(1),
            ["gmail:first"],
            GmailSystemFolders.Inbox,
            true);

        Assert.Equal("Projects", Assert.Single(first.Labels).DisplayName);
        Assert.Equal("Projects", Assert.Single(firstCached.Labels).DisplayName);
        Assert.Equal("Family", Assert.Single(second.Labels).DisplayName);
        Assert.Equal(2, api.LabelLoadCount);
        Assert.False(unsafeResult.IsSuccess);
        Assert.Empty(api.Mutations);
    }

    [Fact]
    public async Task Service_AuthorizationFailureIsTypedAndDoesNotReportSuccess()
    {
        FakeGmailApiClient api = new() { MutationException = AuthRequired() };
        GmailMailboxManagementService service = CreateService(api);

        GmailMailboxMutationResult result = await service.SetStarredAsync(
            Account(1),
            ["gmail:first"],
            true);

        Assert.Empty(result.SucceededMessageKeys);
        Assert.Equal(GmailMailboxFailureKind.ReauthorizationRequired, result.FailureKind);
    }

    [Fact]
    public async Task Service_UserLabelAddAndRemoveUseOnlyDiscoveredUserLabelId()
    {
        FakeGmailApiClient api = new();
        MailAccount account = Account(1);
        api.LabelsByAccount[account.Id] = [new GmailApiUserLabel("Label_1", "Projects")];
        GmailMailboxManagementService service = CreateService(api);

        GmailMailboxMutationResult added = await service.SetUserLabelAsync(
            account,
            ["gmail:first", "gmail:second"],
            "Label_1",
            true);
        GmailMailboxMutationResult removed = await service.SetUserLabelAsync(
            account,
            ["gmail:first"],
            "Label_1",
            false);

        Assert.True(added.IsSuccess);
        Assert.True(removed.IsSuccess);
        Assert.Equal(["Label_1"], api.Mutations[0].Added);
        Assert.Equal(["Label_1"], api.Mutations[1].Removed);
        Assert.Equal(1, api.LabelLoadCount);
    }

    [Fact]
    public void GmailSummaryReflectsServerStarAndLabelState()
    {
        GmailApiSummaryData data = new(
            "message",
            "Subject",
            "Sender <sender@example.test>",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "Preview",
            [GmailSystemFolders.Inbox, GmailSystemFolders.Starred, "Label_1"]);

        MailMessageSummary summary = GmailMailReadProvider.MapSummary(data);

        Assert.True(summary.IsStarred);
        Assert.Contains(GmailSystemFolders.Starred, summary.ProviderLabelIds);
        Assert.Contains("Label_1", summary.ProviderLabelIds);
    }

    [Fact]
    public async Task Selection_SelectOneMultipleAllClearAndAccountSwitchAreIsolated()
    {
        FakeReadProvider provider = new();
        MailAccount firstAccount = Account(1);
        MailAccount secondAccount = Account(2);
        provider.SetPage(firstAccount.Id, MailFolderKind.Inbox, [Summary("gmail:a"), Summary("gmail:b")]);
        provider.SetPage(secondAccount.Id, MailFolderKind.Inbox, [Summary("gmail:c")]);
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(firstAccount);

        viewModel.ToggleMessageSelectionCommand.Execute(viewModel.Messages[0]);
        Assert.Equal(1, viewModel.SelectedMessageCount);
        viewModel.ToggleMessageSelectionCommand.Execute(viewModel.Messages[1]);
        Assert.Equal(2, viewModel.SelectedMessageCount);
        viewModel.ClearSelectionCommand.Execute(null);
        Assert.False(viewModel.HasSelectedMessages);
        viewModel.SelectAllLoadedCommand.Execute(null);
        Assert.True(viewModel.AreAllLoadedMessagesSelected);

        await viewModel.ActivateAsync(secondAccount);
        Assert.False(viewModel.HasSelectedMessages);
        await viewModel.ActivateAsync(firstAccount);
        Assert.False(viewModel.HasSelectedMessages);
    }

    [Fact]
    public async Task Selection_FolderSwitchClearsAndRefreshPreservesOnlySurvivingIds()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a"), Summary("gmail:b")]);
        provider.SetPage(account.Id, MailFolderKind.Starred, [Summary("gmail:s", starred: true)]);
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(account);
        viewModel.SelectAllLoadedCommand.Execute(null);

        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Starred);
        await viewModel.CurrentFolderLoadTask;
        Assert.False(viewModel.HasSelectedMessages);

        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Inbox);
        await viewModel.CurrentFolderLoadTask;
        viewModel.SelectAllLoadedCommand.Execute(null);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:b"), Summary("gmail:c")]);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.True(viewModel.Messages.Single(message => message.MessageKey == "gmail:b").IsSelected);
        Assert.False(viewModel.Messages.Single(message => message.MessageKey == "gmail:c").IsSelected);
        Assert.Equal(1, viewModel.SelectedMessageCount);
    }

    [Fact]
    public async Task StarIsServerFirstAndUnstarFromStarredRemovesMessage()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a")]);
        provider.SetPage(account.Id, MailFolderKind.Starred, [Summary("gmail:s", starred: true)]);
        FakeMailboxService mailbox = new();
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        MailMessageSummary inboxMessage = Assert.Single(viewModel.Messages);

        mailbox.NextResult = Failure("gmail:a");
        await viewModel.ToggleStarCommand.ExecuteAsync(inboxMessage);
        Assert.False(inboxMessage.IsStarred);
        Assert.True(viewModel.HasMailboxActionError);

        mailbox.NextResult = null;
        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Starred);
        await viewModel.CurrentFolderLoadTask;
        await viewModel.ToggleStarCommand.ExecuteAsync(Assert.Single(viewModel.Messages));
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public async Task DeleteSelectedRemovesOnlySuccessesAndDetailDeleteClosesDetail()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a"), Summary("gmail:b")]);
        FakeMailboxService mailbox = new();
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.SelectAllLoadedCommand.Execute(null);
        mailbox.NextResult = new GmailMailboxMutationResult(
            ["gmail:a"],
            [new GmailMailboxItemFailure("gmail:b", GmailMailboxFailureKind.TransientFailure)]);

        await viewModel.DeleteSelectedCommand.ExecuteAsync(null);
        MailMessageSummary remaining = Assert.Single(viewModel.Messages);
        Assert.Equal("gmail:b", remaining.MessageKey);
        Assert.True(viewModel.HasMailboxActionError);

        mailbox.NextResult = null;
        viewModel.OpenMessageCommand.Execute(remaining);
        await viewModel.CurrentMessageLoadTask;
        Assert.True(viewModel.IsMessageDetailVisible);
        await viewModel.DeleteDetailCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsMessageListVisible);
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public async Task ArchiveAndTrashUpdateInboxMembershipAndUnreadBadgeAfterServerSuccess()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        account.InboxUnreadCount = 2;
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a"), Summary("gmail:b")]);
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(account);

        viewModel.ToggleMessageSelectionCommand.Execute(viewModel.Messages[0]);
        await viewModel.ArchiveSelectedCommand.ExecuteAsync(null);
        Assert.Single(viewModel.Messages);
        Assert.Equal(1, account.InboxUnreadCount);

        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Messages);
        Assert.Equal(0, account.InboxUnreadCount);
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Trash));
    }

    [Fact]
    public async Task MultiSelectionReadMutationAffectsOnlySelectedAndUsesActiveAccount()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(2);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a"), Summary("gmail:b")]);
        FakeMailboxService mailbox = new();
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.ToggleMessageSelectionCommand.Execute(viewModel.Messages[1]);

        await viewModel.MarkSelectedReadCommand.ExecuteAsync(null);

        Assert.True(viewModel.Messages[0].IsUnread);
        Assert.False(viewModel.Messages[1].IsUnread);
        Assert.Equal(account.Id, mailbox.LastAccountId);
        Assert.Equal(["gmail:b"], mailbox.LastMessageKeys);
    }

    [Fact]
    public async Task ReauthorizationFailureLeavesMessageVisibleAndRaisesAccountRecoveryState()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a")]);
        FakeMailboxService mailbox = new()
        {
            NextResult = new GmailMailboxMutationResult(
                [],
                [new GmailMailboxItemFailure("gmail:a", GmailMailboxFailureKind.ReauthorizationRequired)])
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Messages);
        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.True(viewModel.HasMailboxActionError);
    }

    [Fact]
    public async Task LabelMenuShowsMixedStateAndAppliesUserLabelToEverySelectedMessage()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(
            account.Id,
            MailFolderKind.Inbox,
            [Summary("gmail:a", labels: ["Label_1"]), Summary("gmail:b")]);
        FakeMailboxService mailbox = new() { Labels = [new GmailUserLabel("Label_1", "Projects")] };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.SelectAllLoadedCommand.Execute(null);

        await viewModel.OpenLabelsForSelectionCommand.ExecuteAsync(null);
        GmailUserLabelOption option = Assert.Single(viewModel.UserLabels);
        Assert.Null(option.IsApplied);
        await viewModel.ToggleUserLabelCommand.ExecuteAsync(option);

        Assert.True(option.IsApplied);
        Assert.All(viewModel.Messages, message => Assert.Contains("Label_1", message.ProviderLabelIds));
        Assert.Equal(account.Id, mailbox.LastAccountId);
        Assert.Equal(["gmail:a", "gmail:b"], mailbox.LastMessageKeys.Order());
    }

    [Fact]
    public async Task DetailToolbarCommandsRemainAvailableWithReplyAndForwardStateUntouched()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a", labels: [GmailSystemFolders.Inbox])]);
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(account);
        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;

        Assert.True(viewModel.IsMessageDetailVisible);
        Assert.True(viewModel.ShowArchiveAction);
        Assert.DoesNotContain(viewModel.Folders, folder => folder.CanAcceptArchive);
        Assert.True(viewModel.ArchiveDetailCommand.CanExecute(null));
        Assert.True(viewModel.DeleteDetailCommand.CanExecute(null));
        Assert.True(viewModel.OpenLabelsForDetailCommand.CanExecute(null));
        Assert.True(viewModel.ToggleStarCommand.CanExecute(viewModel.SelectedMessageSummary));
        Assert.NotNull(viewModel.Compose.ReplyCommand);
        Assert.NotNull(viewModel.Compose.ForwardCommand);
    }

    [Fact]
    public async Task DetailMailboxCommandsAreDisabledWhileLoadingAndRequeryImmediatelyAfterSuccess()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a", labels: [GmailSystemFolders.Inbox])]);
        TaskCompletionSource<MailMessageContent> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.PendingMessage = pending;
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(account);
        MailMessageSummary summary = Assert.Single(viewModel.Messages);
        int archiveRequeries = 0;
        viewModel.ArchiveDetailCommand.CanExecuteChanged += (_, _) => archiveRequeries++;

        viewModel.OpenMessageCommand.Execute(summary);
        Assert.True(viewModel.IsMessageLoading);
        Assert.False(viewModel.ArchiveDetailCommand.CanExecute(null));
        Assert.False(viewModel.DeleteDetailCommand.CanExecute(null));
        Assert.False(viewModel.ToggleStarCommand.CanExecute(summary));
        Assert.False(viewModel.OpenLabelsForDetailCommand.CanExecute(null));

        pending.SetResult(Content(account, summary.MessageKey));
        await viewModel.CurrentMessageLoadTask;

        Assert.False(viewModel.IsMessageLoading);
        Assert.True(viewModel.ArchiveDetailCommand.CanExecute(null));
        Assert.True(viewModel.DeleteDetailCommand.CanExecute(null));
        Assert.True(viewModel.ToggleStarCommand.CanExecute(viewModel.SelectedMessageSummary));
        Assert.True(viewModel.OpenLabelsForDetailCommand.CanExecute(null));
        Assert.True(archiveRequeries > 0);
    }

    [Fact]
    public async Task FailedDetailLoadKeepsMailboxMutationsDisabled()
    {
        FakeReadProvider provider = new() { MessageException = new MailReadException(
            MailReadFailureKind.MessageUnavailable,
            "Письмо недоступно.") };
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:a", labels: [GmailSystemFolders.Inbox])]);
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(account);
        MailMessageSummary summary = Assert.Single(viewModel.Messages);

        viewModel.OpenMessageCommand.Execute(summary);
        await viewModel.CurrentMessageLoadTask;

        Assert.True(viewModel.HasMessageError);
        Assert.False(viewModel.ArchiveDetailCommand.CanExecute(null));
        Assert.False(viewModel.DeleteDetailCommand.CanExecute(null));
        Assert.False(viewModel.ToggleStarCommand.CanExecute(summary));
        Assert.False(viewModel.OpenLabelsForDetailCommand.CanExecute(null));
    }

    [Fact]
    public async Task AccountSwitchInvalidatesOldPendingDetailCommands()
    {
        FakeReadProvider provider = new();
        MailAccount first = Account(1);
        MailAccount second = Account(2);
        provider.SetPage(first.Id, MailFolderKind.Inbox, [Summary("gmail:a", labels: [GmailSystemFolders.Inbox])]);
        provider.SetPage(second.Id, MailFolderKind.Inbox, [Summary("gmail:b", labels: [GmailSystemFolders.Inbox])]);
        TaskCompletionSource<MailMessageContent> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.PendingMessage = pending;
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(first);
        MailMessageSummary oldSummary = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(oldSummary);
        Task oldLoad = viewModel.CurrentMessageLoadTask;

        provider.PendingMessage = null;
        await viewModel.ActivateAsync(second);
        pending.SetResult(Content(first, oldSummary.MessageKey));
        await oldLoad;

        Assert.Equal(second.Id, viewModel.ActiveAccount?.Id);
        Assert.True(viewModel.IsMessageListVisible);
        Assert.False(viewModel.ToggleStarCommand.CanExecute(oldSummary));
        Assert.False(viewModel.DeleteDetailCommand.CanExecute(null));
    }

    [Fact]
    public async Task DetailFolderRulesAndInitialServerStarStateAreAvailableWithoutMutation()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:inbox", starred: true, labels: [GmailSystemFolders.Inbox, GmailSystemFolders.Starred])]);
        provider.SetPage(account.Id, MailFolderKind.Trash, [Summary("gmail:trash", labels: [GmailSystemFolders.Trash])]);
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(account);
        MailMessageSummary starred = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(starred);
        await viewModel.CurrentMessageLoadTask;

        Assert.True(starred.IsStarred);
        Assert.Equal("Снять пометку", viewModel.DetailStarActionText);
        Assert.True(viewModel.ArchiveDetailCommand.CanExecute(null));

        viewModel.BackToMessageListCommand.Execute(null);
        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind is MailFolderKind.Trash);
        await viewModel.CurrentFolderLoadTask;
        MailMessageSummary trash = Assert.Single(viewModel.Messages);
        viewModel.OpenMessageCommand.Execute(trash);
        await viewModel.CurrentMessageLoadTask;

        Assert.False(viewModel.ArchiveDetailCommand.CanExecute(null));
        Assert.False(viewModel.DeleteDetailCommand.CanExecute(null));
        Assert.True(viewModel.ToggleStarCommand.CanExecute(trash));
        Assert.True(viewModel.OpenLabelsForDetailCommand.CanExecute(null));
    }

    [Fact]
    public async Task FolderSpecificRecoveryAndSpamCommandsAreAvailableOnlyInTheirFolders()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [Summary("gmail:inbox")]);
        provider.SetPage(account.Id, MailFolderKind.Trash, [Summary("gmail:trash", labels: [GmailSystemFolders.Trash])]);
        provider.SetPage(account.Id, MailFolderKind.Spam, [Summary("gmail:spam", labels: [GmailSystemFolders.Spam])]);
        MailInboxViewModel viewModel = CreateViewModel(provider, new FakeMailboxService());
        await viewModel.ActivateAsync(account);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        Assert.True(viewModel.ShowReportSpamAction);
        Assert.True(viewModel.ReportSelectedSpamCommand.CanExecute(null));
        Assert.False(viewModel.ShowRestoreAction);
        Assert.False(viewModel.ShowNotSpamAction);
        Assert.False(viewModel.RestoreSelectedCommand.CanExecute(null));
        Assert.False(viewModel.MarkSelectedNotSpamCommand.CanExecute(null));

        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        Assert.False(viewModel.ShowReportSpamAction);
        Assert.False(viewModel.ReportSelectedSpamCommand.CanExecute(null));
        Assert.True(viewModel.ShowRestoreAction);
        Assert.False(viewModel.ShowNotSpamAction);
        Assert.True(viewModel.RestoreSelectedCommand.CanExecute(null));
        Assert.False(viewModel.MarkSelectedNotSpamCommand.CanExecute(null));

        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        Assert.False(viewModel.ShowReportSpamAction);
        Assert.False(viewModel.ReportSelectedSpamCommand.CanExecute(null));
        Assert.False(viewModel.ShowRestoreAction);
        Assert.True(viewModel.ShowNotSpamAction);
        Assert.False(viewModel.RestoreSelectedCommand.CanExecute(null));
        Assert.True(viewModel.MarkSelectedNotSpamCommand.CanExecute(null));

        foreach (MailFolderKind kind in new[]
                 {
                     MailFolderKind.Starred,
                     MailFolderKind.Sent,
                     MailFolderKind.Drafts,
                     MailFolderKind.AllMail
                 })
        {
            await SelectFolderAsync(viewModel, kind);
            Assert.False(viewModel.ShowReportSpamAction);
            Assert.False(viewModel.ReportSelectedSpamCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task ReportSpamBatchUpdatesInboxAndReloadsCachedSpamAndAllMailOnOpen()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        account.InboxUnreadCount = 2;
        MailMessageSummary first = Summary(
            "gmail:first",
            labels: [GmailSystemFolders.Inbox, GmailSystemFolders.Starred, "IMPORTANT", "Label_1"]);
        MailMessageSummary second = Summary(
            "gmail:second",
            labels: [GmailSystemFolders.Inbox, "CATEGORY_UPDATES"]);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [first, second]);
        provider.SetPage(account.Id, MailFolderKind.Spam, []);
        provider.SetPage(account.Id, MailFolderKind.AllMail, [first, second]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "ReportSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Inbox, []);
                provider.SetPage(account.Id, MailFolderKind.Spam,
                [
                    Summary("gmail:first", labels: [GmailSystemFolders.Spam, GmailSystemFolders.Starred, "IMPORTANT", "Label_1"]),
                    Summary("gmail:second", labels: [GmailSystemFolders.Spam, "CATEGORY_UPDATES"])
                ]);
                provider.SetPage(account.Id, MailFolderKind.AllMail, []);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);
        viewModel.SelectAllLoadedCommand.Execute(null);

        await viewModel.ReportSelectedSpamCommand.ExecuteAsync(null);

        Assert.Equal("ReportSpam", mailbox.LastOperation);
        Assert.Equal(["gmail:first", "gmail:second"], mailbox.LastMessageKeys);
        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.HasSelectedMessages);
        Assert.Equal(0, account.InboxUnreadCount);
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Spam));
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));
        Assert.Equal(2, provider.GetPageLoadCount(account.Id, MailFolderKind.Inbox));

        await SelectFolderAsync(viewModel, MailFolderKind.Spam);

        Assert.Equal(["gmail:first", "gmail:second"], viewModel.Messages.Select(message => message.MessageKey));
        MailMessageSummary moved = viewModel.Messages[0];
        Assert.Contains(GmailSystemFolders.Spam, moved.ProviderLabelIds);
        Assert.DoesNotContain(GmailSystemFolders.Inbox, moved.ProviderLabelIds);
        Assert.Contains(GmailSystemFolders.Starred, moved.ProviderLabelIds);
        Assert.Contains("IMPORTANT", moved.ProviderLabelIds);
        Assert.Contains("Label_1", moved.ProviderLabelIds);
        Assert.Equal(2, provider.GetPageLoadCount(account.Id, MailFolderKind.Spam));

        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);

        Assert.Empty(viewModel.Messages);
        Assert.Equal(2, provider.GetPageLoadCount(account.Id, MailFolderKind.AllMail));
    }

    [Fact]
    public async Task ReportSpamSingleSelectionRemovesTheInboxRow()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox,
            [Summary("gmail:single", labels: [GmailSystemFolders.Inbox])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "ReportSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Inbox, []);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.ReportSelectedSpamCommand.ExecuteAsync(null);

        Assert.Equal(["gmail:single"], mailbox.LastMessageKeys);
        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.HasSelectedMessages);
    }

    [Fact]
    public async Task ReportSpamDetailReturnsToTheRefreshedInboxList()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox,
            [Summary("gmail:detail", labels: [GmailSystemFolders.Inbox])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "ReportSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Inbox, []);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;

        Assert.True(viewModel.ReportDetailSpamCommand.CanExecute(null));
        await viewModel.ReportDetailSpamCommand.ExecuteAsync(null);

        Assert.Equal(["gmail:detail"], mailbox.LastMessageKeys);
        Assert.True(viewModel.IsMessageListVisible);
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public async Task ReportSpamPartialFailureMovesSuccessAndKeepsFailedInboxRow()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        MailMessageSummary first = Summary("gmail:first", labels: [GmailSystemFolders.Inbox]);
        MailMessageSummary second = Summary("gmail:second", labels: [GmailSystemFolders.Inbox]);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [first, second]);
        provider.SetPage(account.Id, MailFolderKind.Spam, []);
        provider.SetPage(account.Id, MailFolderKind.AllMail, [first, second]);
        FakeMailboxService mailbox = new()
        {
            NextResult = new GmailMailboxMutationResult(
                ["gmail:first"],
                [new GmailMailboxItemFailure("gmail:second", GmailMailboxFailureKind.TransientFailure)])
        };
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "ReportSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Inbox, [second]);
                provider.SetPage(account.Id, MailFolderKind.Spam,
                    [Summary("gmail:first", labels: [GmailSystemFolders.Spam])]);
                provider.SetPage(account.Id, MailFolderKind.AllMail, [second]);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);
        viewModel.SelectAllLoadedCommand.Execute(null);

        await viewModel.ReportSelectedSpamCommand.ExecuteAsync(null);

        Assert.Equal("gmail:second", Assert.Single(viewModel.Messages).MessageKey);
        Assert.False(viewModel.HasSelectedMessages);
        Assert.Contains("Часть выбранных писем не изменена", viewModel.MailboxActionErrorMessage, StringComparison.Ordinal);
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Spam));
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));
    }

    [Fact]
    public async Task RestoreSelectionUsesActiveAccountRefreshesTrashAndMarksOnlyAllMailStale()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, []);
        provider.SetPage(account.Id, MailFolderKind.AllMail, []);
        provider.SetPage(account.Id, MailFolderKind.Trash, [Summary("gmail:trash", labels: [GmailSystemFolders.Trash, "Label_1"])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "Restore")
            {
                provider.SetPage(account.Id, MailFolderKind.Trash, []);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Equal("Restore", mailbox.LastOperation);
        Assert.Equal(account.Id, mailbox.LastAccountId);
        Assert.Equal(["gmail:trash"], mailbox.LastMessageKeys);
        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.HasSelectedMessages);
        Assert.Equal(2, provider.GetPageLoadCount(account.Id, MailFolderKind.Trash));
        Assert.False(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Inbox));
        Assert.False(viewModel.IsInboxStale(account.Id));
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));
        Assert.False(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Trash));
    }

    [Fact]
    public async Task InboxTrashUntrashThenAllMailLoadsRestoredMessageWithoutInboxLabel()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        MailMessageSummary inbox = Summary(
            "gmail:restored",
            labels: [GmailSystemFolders.Inbox, "CATEGORY_UPDATES", "IMPORTANT"]);
        MailMessageSummary trashed = Summary(
            "gmail:restored",
            labels: [GmailSystemFolders.Trash, "CATEGORY_UPDATES", "IMPORTANT"]);
        MailMessageSummary restored = Summary(
            "gmail:restored",
            labels: ["CATEGORY_UPDATES", "IMPORTANT"]);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [inbox]);
        provider.SetPage(account.Id, MailFolderKind.Trash, []);
        provider.SetPage(account.Id, MailFolderKind.AllMail, []);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "Trash")
            {
                provider.SetPage(account.Id, MailFolderKind.Trash, [trashed]);
            }
            else if (operation == "Restore")
            {
                provider.SetPage(account.Id, MailFolderKind.Trash, []);
                provider.SetPage(account.Id, MailFolderKind.AllMail, [restored]);
            }
        };
        using MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.DeleteSelectedCommand.ExecuteAsync(null);
        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);

        MailMessageSummary visible = Assert.Single(viewModel.Messages);
        Assert.Equal("gmail:restored", visible.MessageKey);
        Assert.DoesNotContain(GmailSystemFolders.Inbox, visible.ProviderLabelIds);
        Assert.False(viewModel.HasListError);
    }

    [Fact]
    public async Task RestorePartialFailureRemovesSuccessKeepsFailureAndClearsSelection()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        MailMessageSummary first = Summary("gmail:first", labels: [GmailSystemFolders.Trash]);
        MailMessageSummary second = Summary("gmail:second", labels: [GmailSystemFolders.Trash]);
        provider.SetPage(account.Id, MailFolderKind.Trash, [first, second]);
        FakeMailboxService mailbox = new()
        {
            NextResult = new GmailMailboxMutationResult(
                ["gmail:first"],
                [new GmailMailboxItemFailure("gmail:second", GmailMailboxFailureKind.TransientFailure)])
        };
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "Restore")
            {
                provider.SetPage(account.Id, MailFolderKind.Trash, [second]);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        viewModel.SelectAllLoadedCommand.Execute(null);

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Equal("gmail:second", Assert.Single(viewModel.Messages).MessageKey);
        Assert.False(viewModel.HasSelectedMessages);
        Assert.Contains("Часть выбранных писем не изменена", viewModel.MailboxActionErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreDetailIsEnabledImmediatelyAndReturnsToRefreshedTrashList()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Trash, [Summary("gmail:trash", labels: [GmailSystemFolders.Trash])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "Restore")
            {
                provider.SetPage(account.Id, MailFolderKind.Trash, []);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;

        Assert.True(viewModel.RestoreDetailCommand.CanExecute(null));
        await viewModel.RestoreDetailCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsMessageListVisible);
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public async Task NotSpamBatchReloadsCachedInboxAndAllMailOnNextOpen()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        account.InboxUnreadCount = 0;
        provider.SetPage(account.Id, MailFolderKind.Inbox, []);
        provider.SetPage(account.Id, MailFolderKind.AllMail, []);
        MailMessageSummary first = Summary(
            "gmail:first",
            labels: [GmailSystemFolders.Spam, GmailSystemFolders.Starred, "IMPORTANT", "Label_1"]);
        MailMessageSummary second = Summary("gmail:second", labels: [GmailSystemFolders.Spam]);
        provider.SetPage(account.Id, MailFolderKind.Spam,
        [
            first,
            second
        ]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "NotSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Spam, []);
                provider.SetPage(account.Id, MailFolderKind.Inbox,
                [
                    Summary("gmail:first", labels: [GmailSystemFolders.Inbox, GmailSystemFolders.Starred, "IMPORTANT", "Label_1"]),
                    Summary("gmail:second", labels: [GmailSystemFolders.Inbox])
                ]);
                provider.SetPage(account.Id, MailFolderKind.AllMail,
                [
                    Summary("gmail:first", labels: [GmailSystemFolders.Inbox, GmailSystemFolders.Starred, "IMPORTANT", "Label_1"]),
                    Summary("gmail:second", labels: [GmailSystemFolders.Inbox])
                ]);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        viewModel.SelectAllLoadedCommand.Execute(null);

        await viewModel.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.Equal("NotSpam", mailbox.LastOperation);
        Assert.Equal(["gmail:first", "gmail:second"], mailbox.LastMessageKeys);
        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.HasSelectedMessages);
        Assert.Equal(2, account.InboxUnreadCount);
        Assert.Equal(2, provider.GetPageLoadCount(account.Id, MailFolderKind.Spam));
        Assert.True(viewModel.IsInboxStale(account.Id));
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Inbox));
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));
        Assert.False(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Spam));

        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);

        Assert.Equal(["gmail:first", "gmail:second"], viewModel.Messages.Select(message => message.MessageKey));
        Assert.Equal(2, provider.GetPageLoadCount(account.Id, MailFolderKind.Inbox));
        Assert.False(viewModel.IsInboxStale(account.Id));
        Assert.False(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Inbox));

        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);

        Assert.Equal(["gmail:first", "gmail:second"], viewModel.Messages.Select(message => message.MessageKey));
        Assert.Equal(2, provider.GetPageLoadCount(account.Id, MailFolderKind.AllMail));
        Assert.False(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));
    }

    [Fact]
    public async Task NotSpamPartialFailureRemovesSuccessKeepsFailureAndClearsSelection()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        MailMessageSummary first = Summary("gmail:first", labels: [GmailSystemFolders.Spam]);
        MailMessageSummary second = Summary("gmail:second", labels: [GmailSystemFolders.Spam]);
        provider.SetPage(account.Id, MailFolderKind.Inbox, []);
        provider.SetPage(account.Id, MailFolderKind.AllMail, []);
        provider.SetPage(account.Id, MailFolderKind.Spam, [first, second]);
        FakeMailboxService mailbox = new()
        {
            NextResult = new GmailMailboxMutationResult(
                ["gmail:first"],
                [new GmailMailboxItemFailure("gmail:second", GmailMailboxFailureKind.TransientFailure)])
        };
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "NotSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Spam, [second]);
                provider.SetPage(account.Id, MailFolderKind.Inbox,
                    [Summary("gmail:first", labels: [GmailSystemFolders.Inbox])]);
                provider.SetPage(account.Id, MailFolderKind.AllMail,
                    [Summary("gmail:first", labels: [GmailSystemFolders.Inbox])]);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        viewModel.SelectAllLoadedCommand.Execute(null);

        await viewModel.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.Equal("gmail:second", Assert.Single(viewModel.Messages).MessageKey);
        Assert.False(viewModel.HasSelectedMessages);
        Assert.Contains("Часть выбранных писем не изменена", viewModel.MailboxActionErrorMessage, StringComparison.Ordinal);
        Assert.True(viewModel.IsInboxStale(account.Id));
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));

        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);
        Assert.Equal("gmail:first", Assert.Single(viewModel.Messages).MessageKey);

        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        Assert.Equal("gmail:first", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task NotSpamDetailIsEnabledImmediatelyAndReturnsToRefreshedSpamList()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, []);
        provider.SetPage(account.Id, MailFolderKind.AllMail, []);
        provider.SetPage(account.Id, MailFolderKind.Spam, [Summary("gmail:spam", labels: [GmailSystemFolders.Spam])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "NotSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Spam, []);
                provider.SetPage(account.Id, MailFolderKind.Inbox,
                    [Summary("gmail:spam", labels: [GmailSystemFolders.Inbox])]);
                provider.SetPage(account.Id, MailFolderKind.AllMail,
                    [Summary("gmail:spam", labels: [GmailSystemFolders.Inbox])]);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        viewModel.OpenMessageCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.CurrentMessageLoadTask;

        Assert.True(viewModel.MarkDetailNotSpamCommand.CanExecute(null));
        await viewModel.MarkDetailNotSpamCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsMessageListVisible);
        Assert.Empty(viewModel.Messages);
        Assert.True(viewModel.IsInboxStale(account.Id));
        Assert.True(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));

        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);
        Assert.Equal("gmail:spam", Assert.Single(viewModel.Messages).MessageKey);

        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        Assert.Equal("gmail:spam", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task NotSpamSingleSelectionUsesTheSameInboxFreshnessContract()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Inbox, []);
        provider.SetPage(account.Id, MailFolderKind.Spam,
            [Summary("gmail:single", labels: [GmailSystemFolders.Spam])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "NotSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Spam, []);
                provider.SetPage(account.Id, MailFolderKind.Inbox,
                    [Summary("gmail:single", labels: [GmailSystemFolders.Inbox])]);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsInboxStale(account.Id));
        await SelectFolderAsync(viewModel, MailFolderKind.Inbox);
        Assert.Equal("gmail:single", Assert.Single(viewModel.Messages).MessageKey);
        Assert.False(viewModel.IsInboxStale(account.Id));
    }

    [Fact]
    public async Task NotSpamTotalFailureDoesNotInvalidateOrMutateCachedFolders()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        MailMessageSummary cachedInbox = Summary("gmail:inbox");
        MailMessageSummary cachedAllMail = Summary("gmail:all-mail", labels: []);
        MailMessageSummary spam = Summary("gmail:failed", labels: [GmailSystemFolders.Spam]);
        provider.SetPage(account.Id, MailFolderKind.Inbox, [cachedInbox]);
        provider.SetPage(account.Id, MailFolderKind.AllMail, [cachedAllMail]);
        provider.SetPage(account.Id, MailFolderKind.Spam, [spam]);
        FakeMailboxService mailbox = new()
        {
            NextResult = new GmailMailboxMutationResult(
                [],
                [new GmailMailboxItemFailure("gmail:failed", GmailMailboxFailureKind.TransientFailure)])
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.AllMail);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.Equal("gmail:failed", Assert.Single(viewModel.Messages).MessageKey);
        Assert.True(viewModel.HasSelectedMessages);
        Assert.False(viewModel.IsInboxStale(account.Id));
        Assert.False(viewModel.IsFolderStateStale(account.Id, MailFolderKind.Inbox));
        Assert.False(viewModel.IsFolderStateStale(account.Id, MailFolderKind.AllMail));
        Assert.Equal(1, provider.GetPageLoadCount(account.Id, MailFolderKind.Spam));
    }

    [Fact]
    public async Task FolderExitMutationReauthorizationIsNotRetriedAndKeepsMessageVisible()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(account.Id, MailFolderKind.Trash, [Summary("gmail:trash", labels: [GmailSystemFolders.Trash])]);
        FakeMailboxService mailbox = new()
        {
            NextResult = new GmailMailboxMutationResult(
                [],
                [new GmailMailboxItemFailure("gmail:trash", GmailMailboxFailureKind.ReauthorizationRequired)])
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Messages);
        Assert.True(viewModel.RequiresGmailReauthentication);
        Assert.Equal(1, provider.GetPageLoadCount(account.Id, MailFolderKind.Trash));
        Assert.Equal("Restore", mailbox.LastOperation);
    }

    [Fact]
    public async Task RestoreAndNotSpamRemainIsolatedToTheActiveAccount()
    {
        FakeReadProvider provider = new();
        MailAccount first = Account(1);
        MailAccount second = Account(2);
        provider.SetPage(first.Id, MailFolderKind.Trash, [Summary("gmail:first", labels: [GmailSystemFolders.Trash])]);
        provider.SetPage(second.Id, MailFolderKind.Spam, [Summary("gmail:second", labels: [GmailSystemFolders.Spam])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "Restore")
            {
                provider.SetPage(first.Id, MailFolderKind.Trash, []);
            }
            else if (operation == "NotSpam")
            {
                provider.SetPage(second.Id, MailFolderKind.Spam, []);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);

        await viewModel.ActivateAsync(first);
        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);
        Assert.Equal(first.Id, mailbox.LastAccountId);
        Assert.Equal(["gmail:first"], mailbox.LastMessageKeys);

        await viewModel.ActivateAsync(second);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));
        await viewModel.MarkSelectedNotSpamCommand.ExecuteAsync(null);
        Assert.Equal(second.Id, mailbox.LastAccountId);
        Assert.Equal(["gmail:second"], mailbox.LastMessageKeys);
    }

    [Fact]
    public async Task ReportSpamRemainsIsolatedToTheActiveAccount()
    {
        FakeReadProvider provider = new();
        MailAccount first = Account(1);
        MailAccount second = Account(2);
        provider.SetPage(first.Id, MailFolderKind.Inbox,
            [Summary("gmail:first", labels: [GmailSystemFolders.Inbox])]);
        provider.SetPage(second.Id, MailFolderKind.Inbox,
            [Summary("gmail:second", labels: [GmailSystemFolders.Inbox])]);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "ReportSpam")
            {
                provider.SetPage(first.Id, MailFolderKind.Inbox, []);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(first);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.ReportSelectedSpamCommand.ExecuteAsync(null);
        await viewModel.ActivateAsync(second);

        Assert.Equal(first.Id, mailbox.LastAccountId);
        Assert.Equal(["gmail:first"], mailbox.LastMessageKeys);
        Assert.Equal("gmail:second", Assert.Single(viewModel.Messages).MessageKey);
    }

    [Fact]
    public async Task FolderExitMutationKeepsCurrentPageAndFallsBackWhenItBecomesEmpty()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(
            account.Id,
            MailFolderKind.Trash,
            null,
            [Summary("gmail:first-page", labels: [GmailSystemFolders.Trash])],
            "trash-next");
        provider.SetPage(
            account.Id,
            MailFolderKind.Trash,
            "trash-next",
            [Summary("gmail:second-page", labels: [GmailSystemFolders.Trash])],
            null);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "Restore")
            {
                provider.SetPage(account.Id, MailFolderKind.Trash, "trash-next", [], null);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Trash);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.RestoreSelectedCommand.ExecuteAsync(null);

        Assert.Equal("gmail:first-page", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("1–1", viewModel.PageRangeText);
        Assert.False(viewModel.CanNavigateToPreviousPage);
        Assert.Equal([null, "trash-next", "trash-next", null],
            provider.GetPageTokens(account.Id, MailFolderKind.Trash));
    }

    [Fact]
    public async Task NotSpamKeepsCurrentSpamPageAndFallsBackWhenItBecomesEmpty()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(
            account.Id,
            MailFolderKind.Spam,
            null,
            [Summary("gmail:first-page", labels: [GmailSystemFolders.Spam])],
            "spam-next");
        provider.SetPage(
            account.Id,
            MailFolderKind.Spam,
            "spam-next",
            [Summary("gmail:second-page", labels: [GmailSystemFolders.Spam])],
            null);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "NotSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Spam, "spam-next", [], null);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await SelectFolderAsync(viewModel, MailFolderKind.Spam);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.Equal("gmail:first-page", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("1–1", viewModel.PageRangeText);
        Assert.False(viewModel.CanNavigateToPreviousPage);
        Assert.Equal([null, "spam-next", "spam-next", null],
            provider.GetPageTokens(account.Id, MailFolderKind.Spam));
    }

    [Fact]
    public async Task ReportSpamKeepsCurrentInboxPageAndFallsBackWhenItBecomesEmpty()
    {
        FakeReadProvider provider = new();
        MailAccount account = Account(1);
        provider.SetPage(
            account.Id,
            MailFolderKind.Inbox,
            null,
            [Summary("gmail:first-page", labels: [GmailSystemFolders.Inbox])],
            "inbox-next");
        provider.SetPage(
            account.Id,
            MailFolderKind.Inbox,
            "inbox-next",
            [Summary("gmail:second-page", labels: [GmailSystemFolders.Inbox])],
            null);
        FakeMailboxService mailbox = new();
        mailbox.ResultApplied = (operation, _) =>
        {
            if (operation == "ReportSpam")
            {
                provider.SetPage(account.Id, MailFolderKind.Inbox, "inbox-next", [], null);
            }
        };
        MailInboxViewModel viewModel = CreateViewModel(provider, mailbox);
        await viewModel.ActivateAsync(account);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        viewModel.ToggleMessageSelectionCommand.Execute(Assert.Single(viewModel.Messages));

        await viewModel.ReportSelectedSpamCommand.ExecuteAsync(null);

        Assert.Equal("gmail:first-page", Assert.Single(viewModel.Messages).MessageKey);
        Assert.Equal("1–1", viewModel.PageRangeText);
        Assert.False(viewModel.CanNavigateToPreviousPage);
        Assert.Equal([null, "inbox-next", "inbox-next", null],
            provider.GetPageTokens(account.Id, MailFolderKind.Inbox));
    }

    private static async Task SelectFolderAsync(MailInboxViewModel viewModel, MailFolderKind kind)
    {
        viewModel.SelectedFolder = viewModel.Folders.Single(folder => folder.Kind == kind);
        await viewModel.CurrentFolderLoadTask;
    }

    private static GmailMailboxManagementService CreateService(FakeGmailApiClient api) =>
        new(new CredentialStore(), api);

    private static MailInboxViewModel CreateViewModel(
        FakeReadProvider provider,
        IGmailMailboxManagementService mailbox) =>
        new(new ReadProviderFactory(provider, mailbox));

    private static MailAccount Account(int number) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
        Provider = MailProviderType.Gmail,
        DisplayName = $"Gmail {number}",
        EmailAddress = $"account{number}@gmail.test",
        CredentialKey = $"credential{number:D22}",
        AuthenticationKind = MailAuthenticationKind.OAuth,
        IsEnabled = true
    };

    private static MailMessageSummary Summary(
        string key,
        bool starred = false,
        IReadOnlyCollection<string>? labels = null)
    {
        HashSet<string> providerLabels = labels?.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>([GmailSystemFolders.Inbox], StringComparer.Ordinal);
        if (starred)
        {
            providerLabels.Add(GmailSystemFolders.Starred);
        }

        return new MailMessageSummary(
            key,
            $"Subject {key}",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            true)
        {
            IsStarred = starred,
            ProviderLabelIds = providerLabels
        };
    }

    private static MailMessageContent Content(MailAccount account, string messageKey) =>
        new(
            messageKey,
            $"Subject {messageKey}",
            "Sender",
            "sender@example.test",
            account.EmailAddress,
            DateTimeOffset.UtcNow,
            MailMessageBodyKind.PlainText,
            "Body",
            [],
            true,
            false);

    private static GmailMailboxMutationResult Failure(string key) =>
        new([], [new GmailMailboxItemFailure(key, GmailMailboxFailureKind.TransientFailure)]);

    private static MailReadException AuthRequired() =>
        new(MailReadFailureKind.ReauthorizationRequired, "Требуется повторный вход в Google.");

    private sealed class CredentialStore : IMailCredentialStore
    {
        private static readonly MailCredential Credential = MailCredential.CreateGmailOAuth(
            "refresh",
            "client",
            "secret",
            GmailOAuthConstants.ModifyScope);

        public Task SaveAsync(string credentialKey, MailCredential credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<MailCredential?>(Credential);

        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGmailApiClient : IGmailMailboxApiClient
    {
        private int _mutationCallCount;

        public Dictionary<Guid, IReadOnlyList<GmailApiUserLabel>> LabelsByAccount { get; } = [];
        public List<ApiMutation> Mutations { get; } = [];
        public List<string> TrashedIds { get; } = [];
        public List<string> UntrashedIds { get; } = [];
        public string? TrashFailureId { get; init; }
        public string? UntrashFailureId { get; init; }
        public Exception? MutationException { get; init; }
        public int? MutationFailureCall { get; init; }
        public int LabelLoadCount { get; private set; }

        public Task<GmailApiInboxPage> GetInboxPageAsync(MailCredential credential, Guid accountId, string? pageToken, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiInboxPage([], null));

        public Task<GmailApiRawMessage> GetRawMessageAsync(MailCredential credential, Guid accountId, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiRawMessage([], false));

        public Task<IReadOnlyList<GmailApiUserLabel>> GetUserLabelsAsync(MailCredential credential, Guid accountId, CancellationToken cancellationToken = default)
        {
            LabelLoadCount++;
            return Task.FromResult(LabelsByAccount.GetValueOrDefault(accountId) ?? []);
        }

        public Task ModifyLabelsAsync(
            MailCredential credential,
            Guid accountId,
            IReadOnlyCollection<string> messageIds,
            IReadOnlyCollection<string> addLabelIds,
            IReadOnlyCollection<string> removeLabelIds,
            CancellationToken cancellationToken = default)
        {
            _mutationCallCount++;
            if (MutationException is not null)
            {
                return Task.FromException(MutationException);
            }

            if (MutationFailureCall == _mutationCallCount)
            {
                return Task.FromException(new HttpRequestException());
            }

            Mutations.Add(new ApiMutation(messageIds.ToArray(), addLabelIds.ToArray(), removeLabelIds.ToArray()));
            return Task.CompletedTask;
        }

        public Task<GmailApiTrashResult> MoveToTrashAsync(
            MailCredential credential,
            Guid accountId,
            IReadOnlyCollection<string> messageIds,
            CancellationToken cancellationToken = default)
        {
            TrashedIds.AddRange(messageIds);
            Dictionary<string, Exception> failures = messageIds
                .Where(id => id == TrashFailureId)
                .ToDictionary(id => id, _ => (Exception)new HttpRequestException(), StringComparer.Ordinal);
            return Task.FromResult(new GmailApiTrashResult(
                messageIds.Where(id => id != TrashFailureId).ToHashSet(StringComparer.Ordinal),
                failures));
        }

        public Task<GmailApiUntrashResult> RestoreFromTrashAsync(
            MailCredential credential,
            Guid accountId,
            IReadOnlyCollection<string> messageIds,
            CancellationToken cancellationToken = default)
        {
            UntrashedIds.AddRange(messageIds);
            Dictionary<string, Exception> failures = messageIds
                .Where(id => id == UntrashFailureId)
                .ToDictionary(id => id, _ => (Exception)new HttpRequestException(), StringComparer.Ordinal);
            return Task.FromResult(new GmailApiUntrashResult(
                messageIds.Where(id => id != UntrashFailureId).ToHashSet(StringComparer.Ordinal),
                failures));
        }
    }

    private sealed record ApiMutation(
        IReadOnlyList<string> MessageIds,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed);

    private sealed class FakeMailboxService : IGmailMailboxManagementService
    {
        public IReadOnlyList<GmailUserLabel> Labels { get; init; } = [];
        public GmailMailboxMutationResult? NextResult { get; set; }
        public Guid LastAccountId { get; private set; }
        public IReadOnlyList<string> LastMessageKeys { get; private set; } = [];
        public string? LastOperation { get; private set; }
        public Action<string, GmailMailboxMutationResult>? ResultApplied { get; set; }

        public Task<GmailUserLabelResult> GetUserLabelsAsync(MailAccount account, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailUserLabelResult(Labels));

        public Task<GmailMailboxMutationResult> SetStarredAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, bool isStarred, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "Star");

        public Task<GmailMailboxMutationResult> SetReadStateAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, bool isRead, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "Read");

        public Task<GmailMailboxMutationResult> ArchiveAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "Archive");

        public Task<GmailMailboxMutationResult> MoveToTrashAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "Trash");

        public Task<GmailMailboxMutationResult> RestoreFromTrashAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "Restore");

        public Task<GmailMailboxMutationResult> MarkNotSpamAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "NotSpam");

        public Task<GmailMailboxMutationResult> ReportSpamAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "ReportSpam");

        public Task<GmailMailboxMutationResult> SetUserLabelAsync(MailAccount account, IReadOnlyCollection<string> messageKeys, string labelId, bool isApplied, CancellationToken cancellationToken = default) =>
            Result(account, messageKeys, "Label");

        public void RemoveAccount(Guid accountId) { }

        private Task<GmailMailboxMutationResult> Result(
            MailAccount account,
            IReadOnlyCollection<string> keys,
            string operation)
        {
            LastAccountId = account.Id;
            LastMessageKeys = keys.ToArray();
            LastOperation = operation;
            GmailMailboxMutationResult result = NextResult ?? new GmailMailboxMutationResult(keys.ToArray(), []);
            NextResult = null;
            ResultApplied?.Invoke(operation, result);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeReadProvider : IMailReadProvider, IMailMessageStateProvider
    {
        private readonly Dictionary<(Guid AccountId, MailFolderKind Kind, string Token), MailPage<MailMessageSummary>> _pages = [];
        private readonly Dictionary<(Guid AccountId, MailFolderKind Kind), int> _pageLoadCounts = [];
        private readonly List<(Guid AccountId, MailFolderKind Kind, string? Token)> _pageLoads = [];

        public TaskCompletionSource<MailMessageContent>? PendingMessage { get; set; }
        public Exception? MessageException { get; init; }

        public void SetPage(Guid accountId, MailFolderKind kind, IReadOnlyList<MailMessageSummary> messages) =>
            SetPage(accountId, kind, null, messages, null);

        public void SetPage(
            Guid accountId,
            MailFolderKind kind,
            string? token,
            IReadOnlyList<MailMessageSummary> messages,
            string? nextToken) =>
            _pages[(accountId, kind, token ?? string.Empty)] = new MailPage<MailMessageSummary>(messages, nextToken);

        public int GetPageLoadCount(Guid accountId, MailFolderKind kind) =>
            _pageLoadCounts.GetValueOrDefault((accountId, kind));

        public IReadOnlyList<string?> GetPageTokens(Guid accountId, MailFolderKind kind) =>
            _pageLoads
                .Where(call => call.AccountId == accountId && call.Kind == kind)
                .Select(call => call.Token)
                .ToArray();

        public bool Supports(MailProviderType providerType) => providerType is MailProviderType.Gmail;

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>(
            [
                MailFolderCatalog.Create(MailFolderKind.Inbox, GmailSystemFolders.Inbox),
                MailFolderCatalog.Create(MailFolderKind.Starred, GmailSystemFolders.Starred),
                MailFolderCatalog.Create(MailFolderKind.Sent, GmailSystemFolders.Sent),
                MailFolderCatalog.Create(MailFolderKind.Drafts, GmailSystemFolders.Draft),
                MailFolderCatalog.Create(MailFolderKind.AllMail, GmailSystemFolders.AllMailView),
                MailFolderCatalog.Create(MailFolderKind.Spam, GmailSystemFolders.Spam),
                MailFolderCatalog.Create(MailFolderKind.Trash, GmailSystemFolders.Trash)
            ]);

        public Task<MailPage<MailMessageSummary>> GetPageAsync(MailAccount account, MailFolder folder, string? continuationToken, int pageSize, CancellationToken cancellationToken = default)
        {
            (Guid AccountId, MailFolderKind Kind) key = (account.Id, folder.Kind);
            _pageLoadCounts[key] = _pageLoadCounts.GetValueOrDefault(key) + 1;
            _pageLoads.Add((account.Id, folder.Kind, continuationToken));
            return Task.FromResult(
                _pages.GetValueOrDefault((account.Id, folder.Kind, continuationToken ?? string.Empty))
                ?? new MailPage<MailMessageSummary>([], null));
        }

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(MailAccount account, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Inbox(), continuationToken, pageSize, cancellationToken);

        public Task<MailMessageContent> GetMessageAsync(MailAccount account, MailFolder folder, string messageKey, CancellationToken cancellationToken = default) =>
            PendingMessage?.Task
            ?? (MessageException is null
                ? Task.FromResult(Content(account, messageKey))
                : Task.FromException<MailMessageContent>(MessageException));

        public Task<MailMessageContent> GetMessageAsync(MailAccount account, string messageKey, CancellationToken cancellationToken = default) =>
            GetMessageAsync(account, MailFolderCatalog.Inbox(), messageKey, cancellationToken);

        public Task<MailReadStateCapability> GetReadStateCapabilityAsync(MailAccount account, MailFolder folder, CancellationToken cancellationToken = default) =>
            Task.FromResult(MailReadStateCapability.Available);

        public Task SetReadStateAsync(MailAccount account, MailFolder folder, string messageKey, bool isRead, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ReadProviderFactory(
        IMailReadProvider provider,
        IGmailMailboxManagementService mailbox) : IMailReadProviderFactory
    {
        public IGmailMailboxManagementService? GmailMailboxManagementService => mailbox;
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }
}
