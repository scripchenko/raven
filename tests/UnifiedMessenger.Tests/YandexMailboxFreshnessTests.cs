using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class YandexMailboxFreshnessTests
{
    [Fact]
    public async Task YandexSelectionReadToggle_UsesOneActionAndChangesDirectionFromUnreadState()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        vm.SelectAllLoadedCommand.Execute(null);

        Assert.True(vm.ShowYandexSelectedReadStateAction);
        Assert.True(vm.YandexSelectedReadStateWillMarkRead);
        Assert.Equal("Прочитано", vm.YandexSelectedReadStateActionText);
        Assert.True(vm.ToggleYandexSelectedReadStateCommand.CanExecute(null));
        List<bool> visibilityUpdates = [];
        List<string> actionTextUpdates = [];
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MailInboxViewModel.ShowYandexSelectedReadStateAction))
            {
                visibilityUpdates.Add(vm.ShowYandexSelectedReadStateAction);
            }
            else if (args.PropertyName == nameof(MailInboxViewModel.YandexSelectedReadStateActionText))
            {
                actionTextUpdates.Add(vm.YandexSelectedReadStateActionText);
            }
        };
        await vm.ToggleYandexSelectedReadStateCommand.ExecuteAsync(null);

        Assert.All(vm.Messages, message => Assert.False(message.IsUnread));
        Assert.Equal([MailMailboxAction.Read], provider.MailboxActions);
        Assert.NotEmpty(visibilityUpdates);
        Assert.All(visibilityUpdates, Assert.True);
        Assert.Contains("Непрочитано", actionTextUpdates);
        Assert.True(vm.ShowYandexSelectedReadStateAction);
        Assert.False(vm.YandexSelectedReadStateWillMarkRead);
        Assert.Equal("Непрочитано", vm.YandexSelectedReadStateActionText);
        await vm.ToggleYandexSelectedReadStateCommand.ExecuteAsync(null);

        Assert.All(vm.Messages, message => Assert.True(message.IsUnread));
        Assert.Equal([MailMailboxAction.Read, MailMailboxAction.Unread], provider.MailboxActions);
    }

    [Fact]
    public async Task YandexSelectionReadToggle_UsesReadWhenAnySelectedMessageIsUnread()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        vm.SelectAllLoadedCommand.Execute(null);
        await vm.MarkSelectedReadCommand.ExecuteAsync(null);
        vm.ToggleMessageSelectionCommand.Execute(vm.Messages[0]);
        await vm.MarkSelectedUnreadCommand.ExecuteAsync(null);
        vm.ToggleMessageSelectionCommand.Execute(vm.Messages[0]);

        Assert.Contains(vm.Messages, message => message.IsUnread);
        Assert.Contains(vm.Messages, message => !message.IsUnread);
        Assert.True(vm.YandexSelectedReadStateWillMarkRead);
        Assert.Equal("Прочитано", vm.YandexSelectedReadStateActionText);
        await vm.ToggleYandexSelectedReadStateCommand.ExecuteAsync(null);
        Assert.All(vm.Messages, message => Assert.False(message.IsUnread));
    }

    [Fact]
    public async Task YandexDetailReadToggle_UsesOneActionAndUpdatesTheCurrentMessageImmediately()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        vm.OpenMessageCommand.Execute(Assert.Single(vm.Messages));
        await vm.CurrentMessageLoadTask;

        Assert.True(vm.ShowYandexDetailReadStateAction);
        Assert.True(vm.YandexDetailReadStateWillMarkRead);
        Assert.Equal("Прочитано", vm.YandexDetailReadStateActionText);
        await vm.ToggleYandexDetailReadStateCommand.ExecuteAsync(null);

        Assert.False(vm.SelectedMessageSummary!.IsUnread);
        Assert.False(vm.SelectedMessageContent!.IsUnread);
        Assert.False(vm.YandexDetailReadStateWillMarkRead);
        Assert.Equal("Непрочитано", vm.YandexDetailReadStateActionText);
        await vm.ToggleYandexDetailReadStateCommand.ExecuteAsync(null);
        Assert.True(vm.SelectedMessageSummary!.IsUnread);
        Assert.True(vm.SelectedMessageContent!.IsUnread);
        Assert.Equal([true, false], provider.DetailReadStates);
    }

    [Fact]
    public async Task YandexStarCommands_AreUnavailableWithoutDisablingOtherMailboxActions()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        MailMessageSummary message = Assert.Single(vm.Messages);
        vm.SelectAllLoadedCommand.Execute(null);
        Assert.False(vm.ToggleStarCommand.CanExecute(message));
        Assert.False(vm.ToggleSelectedStarCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
        Assert.True(vm.ReportSelectedSpamCommand.CanExecute(null));
        Assert.True(vm.MarkSelectedReadCommand.CanExecute(null));
        Assert.True(vm.MarkSelectedUnreadCommand.CanExecute(null));
        await vm.ToggleStarCommand.ExecuteAsync(message);
        await vm.ToggleSelectedStarCommand.ExecuteAsync(null);
        vm.OpenMessageCommand.Execute(message);
        await vm.CurrentMessageLoadTask;
        Assert.False(vm.ToggleStarCommand.CanExecute(vm.SelectedMessageSummary));
        Assert.True(vm.DeleteDetailCommand.CanExecute(null));
        Assert.True(vm.ReportDetailSpamCommand.CanExecute(null));
        await vm.ToggleStarCommand.ExecuteAsync(vm.SelectedMessageSummary);
        Assert.Equal(0, provider.MutationCalls);
        Assert.False(vm.HasMailboxActionError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedArchive_IsHiddenAndCannotExecuteFromListOrDetail(bool folderExists)
    {
        Provider provider = new();
        MailAccount account = Account();
        if (folderExists) provider.ArchiveUnavailable.Add(account.Id);
        else provider.WithoutArchive.Add(account.Id);
        provider.Seed(account, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        Assert.False(vm.ShowArchiveAction);
        await vm.ActivateAsync(account);
        Assert.Equal(folderExists, vm.Folders.Any(folder => folder.Kind is MailFolderKind.Archive));
        Assert.False(vm.ShowArchiveAction);
        vm.SelectAllLoadedCommand.Execute(null);
        Assert.False(vm.ArchiveSelectedCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
        Assert.True(vm.ReportSelectedSpamCommand.CanExecute(null));
        await vm.ArchiveSelectedCommand.ExecuteAsync(null);
        vm.OpenMessageCommand.Execute(Assert.Single(vm.Messages));
        await vm.CurrentMessageLoadTask;
        Assert.False(vm.ArchiveDetailCommand.CanExecute(null));
        await vm.ArchiveDetailCommand.ExecuteAsync(null);
        Assert.Equal(0, provider.MutationCalls);
        Assert.False(vm.HasMailboxActionError);
        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task ArchiveAvailability_IsAccountScopedAcrossSwitchesAndCacheReuse()
    {
        Provider provider = new();
        MailAccount supported = Account();
        MailAccount unsupported = Account();
        provider.WithoutArchive.Add(unsupported.Id);
        provider.Seed(supported, MailFolderKind.Inbox, 1);
        provider.Seed(unsupported, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        foreach (MailAccount account in new[] { supported, unsupported, supported, unsupported })
        {
            await vm.ActivateAsync(account);
            if (vm.IsMessageDetailVisible) vm.BackToMessageListCommand.Execute(null);
            vm.ClearSelectionCommand.Execute(null);
            vm.SelectAllLoadedCommand.Execute(null);
            bool expected = account.Id == supported.Id;
            Assert.Equal(expected, vm.ShowArchiveAction);
            Assert.Equal(expected, vm.ArchiveSelectedCommand.CanExecute(null));
            vm.OpenMessageCommand.Execute(Assert.Single(vm.Messages));
            await vm.CurrentMessageLoadTask;
            Assert.Equal(expected, vm.ArchiveDetailCommand.CanExecute(null));
        }
        Assert.Equal(0, provider.MutationCalls);
    }

    [Theory]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Archive, MailFolderKind.Archive)]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Trash, MailFolderKind.Trash)]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Spam, MailFolderKind.Spam)]
    [InlineData(MailFolderKind.Spam, MailMailboxAction.NotSpam, MailFolderKind.Inbox)]
    [InlineData(MailFolderKind.Spam, MailMailboxAction.Trash, MailFolderKind.Trash)]
    [InlineData(MailFolderKind.Trash, MailMailboxAction.Restore, MailFolderKind.Inbox)]
    public async Task BatchMove_RemovesSourceAndRefreshesCachedDestination(
        MailFolderKind source, MailMailboxAction action, MailFolderKind destination)
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, source, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, destination);
        int targetReads = provider.Reads.GetValueOrDefault((account.Id, destination));
        await Open(vm, source);
        vm.SelectAllLoadedCommand.Execute(null);
        Assert.Equal(2, vm.SelectedMessageCount);

        await Execute(vm, action, detail: false);

        Assert.Empty(vm.Messages);
        Assert.False(vm.HasSelectedMessages);
        Assert.False(vm.HasMailboxActionError);
        await Open(vm, destination);
        Assert.Equal(2, vm.Messages.Count);
        Assert.True(provider.Reads[(account.Id, destination)] > targetReads);
        Assert.All(vm.Messages, item => Assert.StartsWith($"imap:{(int)destination}:", item.MessageKey));
        Assert.False(vm.IsMailboxChanging);
        Assert.False(vm.IsListLoading);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.SelectAllLoadedCommand.CanExecute(null));
    }

    [Fact]
    public async Task BatchNotSpam_ShowsConfirmedDestinationUidsUntilInboxListingCatchesUp()
    {
        Provider provider = new() { HideMovedMessagesFromReads = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Spam, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);

        await vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        await Open(vm, MailFolderKind.Inbox);
        Assert.Equal(2, vm.Messages.Count);
        Assert.All(vm.Messages, message =>
            Assert.StartsWith($"imap:{(int)MailFolderKind.Inbox}:9:", message.MessageKey));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Messages.Count);

        provider.HideMovedMessagesFromReads = false;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(2, vm.Messages.Select(message => message.MessageKey).Distinct().Count());
        Assert.False(vm.IsMailboxChanging);
        Assert.False(vm.IsListLoading);
    }

    [Fact]
    public async Task PendingDestinationRows_DoNotSkipAFullServerPage()
    {
        Provider provider = new() { HideMovedMessagesFromReads = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 100);
        provider.Seed(account, MailFolderKind.Spam, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);
        await vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        await Open(vm, MailFolderKind.Inbox);
        string[] firstPageKeys = vm.Messages.Select(message => message.MessageKey).ToArray();
        Assert.Equal(52, firstPageKeys.Length);
        Assert.Equal(52, firstPageKeys.Distinct().Count());
        Assert.True(vm.NextPageCommand.CanExecute(null));

        await vm.NextPageCommand.ExecuteAsync(null);
        string[] secondPageKeys = vm.Messages.Select(message => message.MessageKey).ToArray();
        Assert.Equal(50, secondPageKeys.Length);
        Assert.Empty(firstPageKeys.Intersect(secondPageKeys, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PendingDestinationUid_IsDiscardedWhenInboxUidValidityChanges()
    {
        Provider provider = new() { HideMovedMessagesFromReads = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 1, uidValidity: 10);
        provider.Seed(account, MailFolderKind.Spam, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);
        await vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        await Open(vm, MailFolderKind.Inbox);

        MailMessageSummary message = Assert.Single(vm.Messages);
        Assert.StartsWith($"imap:{(int)MailFolderKind.Inbox}:10:", message.MessageKey);
    }

    [Fact]
    public async Task BatchNotSpam_PartialResultMovesConfirmedItemAndLeavesFailedItemSelectable()
    {
        Provider provider = new() { PartialFailure = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Spam, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);

        await vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        MailMessageSummary remaining = Assert.Single(vm.Messages);
        Assert.True(vm.HasMailboxActionError);
        Assert.False(vm.IsMailboxChanging);
        Assert.False(vm.IsListLoading);
        Assert.True(vm.ToggleMessageSelectionCommand.CanExecute(remaining));
        await Open(vm, MailFolderKind.Inbox);
        Assert.Single(vm.Messages);
        Assert.Equal(1, provider.MutationCalls);
    }

    [Fact]
    public async Task BatchNotSpam_AmbiguousMappedResultReconcilesBothFoldersWithoutRetry()
    {
        Provider provider = new() { AmbiguousMove = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Spam, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);

        await vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.True(vm.HasMailboxActionError);
        Assert.Equal(1, provider.MutationCalls);
        await Open(vm, MailFolderKind.Inbox);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(1, provider.MutationCalls);
        Assert.False(vm.IsMailboxChanging);
        Assert.False(vm.IsListLoading);
    }

    [Fact]
    public async Task ExceptionAfterServerMove_ReconcilesNotSpamWithoutRepeatingMutation()
    {
        Provider provider = new() { ThrowAfterServerMutation = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Spam, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);

        await vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.True(vm.HasMailboxActionError);
        Assert.Equal(1, provider.MutationCalls);
        Assert.False(vm.IsMailboxChanging);
        Assert.False(vm.IsListLoading);
        await Open(vm, MailFolderKind.Inbox);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(1, provider.MutationCalls);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task FolderSwitchDuringPostMutationRefresh_ClearsBusyOwnerAndRestoresCommands()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 1);
        provider.Seed(account, MailFolderKind.Sent, 1);
        provider.Seed(account, MailFolderKind.Spam, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Sent);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);
        provider.BlockNextReadFolder = MailFolderKind.Spam;
        provider.PageReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task mutation = vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);
        await provider.PageReadStarted.Task;
        vm.SelectedFolder = vm.Folders.Single(folder => folder.Kind == MailFolderKind.Sent);
        await vm.CurrentFolderLoadTask;
        await mutation;

        Assert.Equal(MailFolderKind.Sent, vm.SelectedFolder?.Kind);
        Assert.False(vm.IsListLoading);
        Assert.False(vm.IsMailboxChanging);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        MailMessageSummary message = Assert.Single(vm.Messages);
        Assert.True(vm.ToggleMessageSelectionCommand.CanExecute(message));
        vm.ToggleMessageSelectionCommand.Execute(message);
        Assert.True(message.IsSelected);
    }

    [Fact]
    public async Task ConfirmedNotSpamDestinationRows_AreIsolatedPerAccount()
    {
        Provider provider = new() { HideMovedMessagesFromReads = true };
        MailAccount first = Account();
        MailAccount second = Account();
        provider.Seed(first, MailFolderKind.Spam, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(first);
        await Open(vm, MailFolderKind.Spam);
        vm.SelectAllLoadedCommand.Execute(null);
        await vm.MarkSelectedNotSpamCommand.ExecuteAsync(null);

        await vm.ActivateAsync(second);
        Assert.Empty(vm.Messages);
        await vm.ActivateAsync(first);
        await Open(vm, MailFolderKind.Inbox);
        Assert.Single(vm.Messages);
        Assert.StartsWith(
            $"imap:{(int)MailFolderKind.Inbox}:",
            Assert.Single(vm.Messages).MessageKey);
    }

    [Theory]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Trash, MailFolderKind.Trash)]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Archive, MailFolderKind.Archive)]
    [InlineData(MailFolderKind.Inbox, MailMailboxAction.Spam, MailFolderKind.Spam)]
    [InlineData(MailFolderKind.Spam, MailMailboxAction.NotSpam, MailFolderKind.Inbox)]
    [InlineData(MailFolderKind.Trash, MailMailboxAction.Restore, MailFolderKind.Inbox)]
    public async Task DetailMove_ReturnsToListWithoutResurrectingMovedMessage(
        MailFolderKind source, MailMailboxAction action, MailFolderKind destination)
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, source, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, source);
        vm.OpenMessageCommand.Execute(Assert.Single(vm.Messages));
        await vm.CurrentMessageLoadTask;
        Assert.True(vm.IsMessageDetailVisible);

        await Execute(vm, action, detail: true);

        Assert.True(vm.IsMessageListVisible);
        Assert.Null(vm.SelectedMessageSummary);
        Assert.Null(vm.SelectedMessageContent);
        Assert.Empty(vm.Messages);
        await Open(vm, destination);
        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task BatchReadState_RoundTripWithoutChangingFolderOrLosingSelection()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        vm.SelectAllLoadedCommand.Execute(null);
        await vm.MarkSelectedReadCommand.ExecuteAsync(null);
        Assert.All(vm.Messages, item => Assert.False(item.IsUnread));
        Assert.Equal(2, vm.SelectedMessageCount);
        await vm.MarkSelectedUnreadCommand.ExecuteAsync(null);
        Assert.All(vm.Messages, item => Assert.True(item.IsUnread));
        Assert.Equal(2, vm.SelectedMessageCount);
        Assert.Equal(MailFolderKind.Inbox, vm.SelectedFolder?.Kind);
        Assert.False(vm.OpenLabelsForSelectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task MoveOnSecondPage_ResetsStalePaginationAndKeepsRemainingMailReachable()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 51);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal("51–51", vm.PageRangeText);
        vm.SelectAllLoadedCommand.Execute(null);
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal(50, vm.Messages.Count);
        Assert.Equal("1–50", vm.PageRangeText);
        Assert.False(vm.CanNavigateToPreviousPage);
        Assert.False(vm.CanNavigateToNextPage);
    }

    [Fact]
    public async Task ActiveInbox_NewMailSignalRefreshesWithoutManualRefresh()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        provider.Seed(account, MailFolderKind.Inbox, 2);
        vm.OnNewMailDetected(account.Id, true);
        await vm.GetCurrentInboxRefreshTask(account.Id);
        Assert.Equal(2, vm.Messages.Count);
        Assert.False(vm.IsInboxStale(account.Id));
    }

    [Fact]
    public async Task InactiveInbox_NewMailSignalRefreshesOnReturn_OnlyForItsAccount()
    {
        Provider provider = new();
        MailAccount first = Account();
        MailAccount second = Account();
        provider.Seed(first, MailFolderKind.Inbox, 1);
        provider.Seed(second, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(first);
        await vm.ActivateAsync(second);
        provider.Seed(first, MailFolderKind.Inbox, 3);
        vm.OnNewMailDetected(first.Id, false);
        Assert.Single(vm.Messages);
        Assert.True(vm.IsInboxStale(first.Id));
        Assert.False(vm.IsInboxStale(second.Id));
        await vm.ActivateAsync(first);
        Assert.Equal(3, vm.Messages.Count);
        Assert.False(vm.IsInboxStale(first.Id));
    }

    [Fact]
    public async Task PartialFailure_RefreshesBothFoldersAndReportsFailure()
    {
        Provider provider = new() { PartialFailure = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 2);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        vm.SelectAllLoadedCommand.Execute(null);
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Single(vm.Messages);
        Assert.True(vm.HasMailboxActionError);
        await Open(vm, MailFolderKind.Trash);
        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task AmbiguousDetailMove_RefreshesWithoutRetainingAStaleMessage()
    {
        Provider provider = new() { AmbiguousMove = true };
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        vm.OpenMessageCommand.Execute(Assert.Single(vm.Messages));
        await vm.CurrentMessageLoadTask;

        await vm.DeleteDetailCommand.ExecuteAsync(null);

        Assert.True(vm.IsMessageListVisible);
        Assert.Empty(vm.Messages);
        Assert.Null(vm.SelectedMessageSummary);
        Assert.Null(vm.SelectedMessageContent);
        Assert.True(vm.HasMailboxActionError);
        await Open(vm, MailFolderKind.Trash);
        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task AccountSwitchDuringMutation_DoesNotApplyResultToNewAccount_AndOldFoldersBecomeStale()
    {
        Provider provider = new() { MutationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        MailAccount first = Account();
        MailAccount second = Account();
        provider.Seed(first, MailFolderKind.Inbox, 1);
        provider.Seed(second, MailFolderKind.Inbox, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(first);
        vm.SelectAllLoadedCommand.Execute(null);
        Task mutation = vm.DeleteSelectedCommand.ExecuteAsync(null);
        await provider.MutationStarted.Task;
        await vm.ActivateAsync(second);
        provider.MutationRelease.SetResult();
        await mutation;
        Assert.Equal(second.Id, vm.ActiveAccount?.Id);
        Assert.Single(vm.Messages);
        await vm.ActivateAsync(first);
        Assert.Empty(vm.Messages);
        await Open(vm, MailFolderKind.Trash);
        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task TrashOffersRestoreAndNoPermanentDelete()
    {
        Provider provider = new();
        MailAccount account = Account();
        provider.Seed(account, MailFolderKind.Trash, 1);
        using MailInboxViewModel vm = Create(provider);
        await vm.ActivateAsync(account);
        await Open(vm, MailFolderKind.Trash);
        vm.SelectAllLoadedCommand.Execute(null);
        Assert.True(vm.RestoreSelectedCommand.CanExecute(null));
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
        Assert.False(vm.ArchiveSelectedCommand.CanExecute(null));
        Assert.False(vm.ReportSelectedSpamCommand.CanExecute(null));
    }

    private static MailAccount Account() => new() { Id = Guid.NewGuid(), Provider = MailProviderType.Yandex,
        EmailAddress = "account@example.test", CredentialKey = Guid.NewGuid().ToString("N"), IsEnabled = true };
    private static MailInboxViewModel Create(Provider provider) => new(new MailReadProviderFactory([provider], mailboxManagementService: provider));
    private static async Task Open(MailInboxViewModel vm, MailFolderKind kind)
    {
        vm.SelectedFolder = vm.Folders.Single(folder => folder.Kind == kind);
        await vm.CurrentFolderLoadTask;
    }
    private static Task Execute(MailInboxViewModel vm, MailMailboxAction action, bool detail) => action switch
    {
        MailMailboxAction.Archive => (detail ? vm.ArchiveDetailCommand : vm.ArchiveSelectedCommand).ExecuteAsync(null),
        MailMailboxAction.Trash => (detail ? vm.DeleteDetailCommand : vm.DeleteSelectedCommand).ExecuteAsync(null),
        MailMailboxAction.Spam => (detail ? vm.ReportDetailSpamCommand : vm.ReportSelectedSpamCommand).ExecuteAsync(null),
        MailMailboxAction.NotSpam => (detail ? vm.MarkDetailNotSpamCommand : vm.MarkSelectedNotSpamCommand).ExecuteAsync(null),
        MailMailboxAction.Restore => (detail ? vm.RestoreDetailCommand : vm.RestoreSelectedCommand).ExecuteAsync(null),
        _ => throw new NotSupportedException()
    };

    private sealed class Provider : IMailReadProvider, IMailMailboxManagementService, IMailMessageStateProvider
    {
        private readonly Dictionary<(Guid, MailFolderKind), List<MailMessageSummary>> _mail = [];
        private readonly HashSet<(Guid AccountId, string MessageKey)> _movedMessageKeys = [];
        public Dictionary<(Guid, MailFolderKind), int> Reads { get; } = [];
        public HashSet<Guid> WithoutArchive { get; } = [];
        public HashSet<Guid> ArchiveUnavailable { get; } = [];
        public int MutationCalls { get; private set; }
        public List<MailMailboxAction> MailboxActions { get; } = [];
        public List<bool> DetailReadStates { get; } = [];
        public bool PartialFailure { get; init; }
        public bool AmbiguousMove { get; init; }
        public bool ThrowAfterServerMutation { get; init; }
        public bool HideMovedMessagesFromReads { get; set; }
        public MailFolderKind? BlockNextReadFolder { get; set; }
        public TaskCompletionSource? PageReadRelease { get; set; }
        public TaskCompletionSource PageReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? MutationRelease { get; init; }
        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Supports(MailProviderType provider) => provider is MailProviderType.Yandex;
        public bool CanApply(MailFolderKind source, MailMailboxAction action) => action switch
        {
            MailMailboxAction.Archive or MailMailboxAction.Spam => source is MailFolderKind.Inbox,
            MailMailboxAction.Restore => source is MailFolderKind.Trash,
            MailMailboxAction.NotSpam => source is MailFolderKind.Spam,
            MailMailboxAction.Trash => source is not MailFolderKind.Trash,
            _ => true
        };
        private List<MailMessageSummary> Items(Guid account, MailFolderKind folder)
        {
            if (!_mail.TryGetValue((account, folder), out var items)) _mail[(account, folder)] = items = [];
            return items;
        }
        public void Seed(
            MailAccount account,
            MailFolderKind folder,
            int count,
            uint uidValidity = 9)
        {
            _mail[(account.Id, folder)] = Enumerable.Range(1, count).Select(index => new MailMessageSummary(
                ImapMailReadProvider.CreateMessageKey(folder, uidValidity, (uint)index), "Subject", "Sender", "sender@example.test",
                DateTimeOffset.UnixEpoch, "", true)).ToList();
        }
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>(new[] { MailFolderKind.Inbox, MailFolderKind.Sent, MailFolderKind.Drafts,
                MailFolderKind.Spam, MailFolderKind.Trash, MailFolderKind.Archive }
                .Where(kind => kind is not MailFolderKind.Archive || !WithoutArchive.Contains(account.Id))
                .Select(kind => MailFolderCatalog.Create(kind, $"server/{kind}",
                    canAcceptArchive: kind is MailFolderKind.Archive && !ArchiveUnavailable.Contains(account.Id))).ToArray());
        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(MailAccount account, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            GetPageAsync(account, MailFolderCatalog.Inbox(), continuationToken, pageSize, cancellationToken);
        public async Task<MailPage<MailMessageSummary>> GetPageAsync(MailAccount account, MailFolder folder, string? continuationToken, int pageSize, CancellationToken cancellationToken = default)
        {
            Reads[(account.Id, folder.Kind)] = Reads.GetValueOrDefault((account.Id, folder.Kind)) + 1;
            if (BlockNextReadFolder == folder.Kind)
            {
                BlockNextReadFolder = null;
                PageReadStarted.TrySetResult();
                if (PageReadRelease is not null)
                {
                    await PageReadRelease.Task.WaitAsync(cancellationToken);
                }
            }
            int offset = continuationToken is null ? 0 : int.Parse(continuationToken, System.Globalization.CultureInfo.InvariantCulture);
            IReadOnlyList<MailMessageSummary> items = Items(account.Id, folder.Kind)
                .Where(item => !HideMovedMessagesFromReads
                    || !_movedMessageKeys.Contains((account.Id, item.MessageKey)))
                .ToArray();
            return new MailPage<MailMessageSummary>(items.Skip(offset).Take(pageSize).Select(item => item with { }).ToArray(),
                offset + pageSize < items.Count ? (offset + pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }
        public Task<MailMessageContent> GetMessageAsync(MailAccount account, string messageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailMessageContent(messageKey, "Subject", "Sender", "sender@example.test", "recipient@example.test",
                DateTimeOffset.UnixEpoch, MailMessageBodyKind.PlainText, "Body", [], true, false));
        public Task<MailReadStateCapability> GetReadStateCapabilityAsync(MailAccount account, MailFolder folder,
            CancellationToken cancellationToken = default) => Task.FromResult(MailReadStateCapability.Available);
        public Task SetReadStateAsync(MailAccount account, MailFolder folder, string messageKey, bool isRead,
            CancellationToken cancellationToken = default)
        {
            DetailReadStates.Add(isRead);
            List<MailMessageSummary> items = Items(account.Id, folder.Kind);
            int index = items.FindIndex(message => message.MessageKey == messageKey);
            if (index >= 0) items[index] = items[index] with { IsUnread = !isRead };
            return Task.CompletedTask;
        }
        public async Task<MailMailboxMutationResult> ApplyAsync(MailAccount account, MailFolder source, IReadOnlyCollection<string> messageKeys, MailMailboxAction action, CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            MailboxActions.Add(action);
            MutationStarted.TrySetResult();
            if (MutationRelease is not null) await MutationRelease.Task;
            var destination = ImapMailboxManagementService.Destination(action);
            string[] successes = PartialFailure ? messageKeys.Take(1).ToArray() : messageKeys.ToArray();
            var items = Items(account.Id, source.Kind);
            List<MailMailboxMutationItemResult> itemResults = [];
            foreach (string key in successes)
            {
                int index = items.FindIndex(item => item.MessageKey == key);
                if (index < 0) continue;
                var message = items[index];
                if (destination is MailFolderKind target)
                {
                    var targetItems = Items(account.Id, target);
                    string destinationKey = ImapMailReadProvider.CreateMessageKey(
                        target,
                        9,
                        (uint)(100 + targetItems.Count));
                    targetItems.Add(message with { MessageKey = destinationKey });
                    _movedMessageKeys.Add((account.Id, destinationKey));
                    items.RemoveAt(index);
                    itemResults.Add(new(
                        key,
                        AmbiguousMove
                            ? MailMailboxMutationItemStatus.Ambiguous
                            : MailMailboxMutationItemStatus.Succeeded,
                        destinationKey));
                }
                else
                {
                    items[index] = action switch
                    {
                        MailMailboxAction.Read => message with { IsUnread = false },
                        MailMailboxAction.Unread => message with { IsUnread = true },
                        _ => message
                    };
                    itemResults.Add(new(key, MailMailboxMutationItemStatus.Succeeded));
                }
            }
            foreach (string failedKey in messageKeys.Except(successes, StringComparer.Ordinal))
            {
                itemResults.Add(new(failedKey, MailMailboxMutationItemStatus.Failed));
            }
            if (ThrowAfterServerMutation)
            {
                throw new IOException("Synthetic response loss after server mutation.");
            }
            return AmbiguousMove
                ? new([], messageKeys.ToArray(), destination,
                    "Ответ сервера потерян. Папки обновлены.", true, itemResults)
                : new(successes, messageKeys.Except(successes).ToArray(), destination,
                    PartialFailure ? "Часть писем не изменена." : null, true, itemResults);
        }
    }
}
