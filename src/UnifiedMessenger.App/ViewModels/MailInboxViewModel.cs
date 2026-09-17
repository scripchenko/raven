using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.App.ViewModels;

public enum MailInboxPresentationMode
{
    MessageList,
    MessageDetail,
    Compose
}

public sealed class MailInboxViewModel : ObservableObject, IDisposable, IMailInboxFreshnessService
{
    public const int PageSize = 50;
    public const int MessageBodyCacheCapacity = 20;
    public const int RemoteImageConsentCacheCapacity = 20;
    public static readonly TimeSpan MailReadDwellDelay = TimeSpan.FromSeconds(3);

    private readonly IMailReadProviderFactory _providerFactory;
    private readonly IGmailMailboxManagementService? _gmailMailboxService;
    private readonly IMailMailboxManagementService? _mailboxService;
    private readonly IMailAttachmentSaveService? _attachmentSaveService;
    private readonly MailMessageSourceCache? _messageSourceCache;
    private readonly IMailReadDwellScheduler _readDwellScheduler;
    private readonly IRemoteImageSenderTrustStore _remoteImageSenderTrustStore;
    private readonly Dictionary<FolderStateKey, FolderState> _folderStates = [];
    private readonly Dictionary<Guid, AccountFolderState> _accountFolderStates = [];
    private readonly Dictionary<Guid, InboxFreshnessState> _inboxFreshnessStates = [];
    private readonly HashSet<Guid> _gmailReauthenticationRequiredAccounts = [];
    private readonly HashSet<string> _labelMenuMessageKeys = new(StringComparer.Ordinal);
    private readonly BoundedLruCache<MessageBodyCacheKey, MailMessageContent> _messageBodyCache =
        new(MessageBodyCacheCapacity);
    private readonly BoundedLruCache<RemoteImageConsentKey, bool> _remoteImageConsents =
        new(RemoteImageConsentCacheCapacity);
    private CancellationTokenSource? _activationCancellation;
    private CancellationTokenSource? _listCancellation;
    private CancellationTokenSource? _messageCancellation;
    private CancellationTokenSource? _mutationCancellation;
    private CancellationTokenSource? _attachmentCancellation;
    private CancellationTokenSource? _readDwellCancellation;
    private CancellationTokenSource? _remoteImageSenderTrustCancellation;
    private FolderState? _displayedListState;
    private FolderState? _searchState;
    private Guid? _searchAccountId;
    private string _searchText = string.Empty;
    private string? _activeSearchQuery;
    private MailAccount? _activeAccount;
    private MailFolder? _selectedFolder;
    private MailMessageSummary? _selectedMessageSummary;
    private MailMessageContent? _selectedMessageContent;
    private bool _isListLoading;
    private bool _isMessageLoading;
    private bool _isRemoteImageLoading;
    private bool _automaticallyShowRemoteImages;
    private bool _isPrintAvailable;
    private bool _isCurrentRemoteImageSenderTrusted;
    private bool _canTrustCurrentRemoteImageSender;
    private bool _isReadStateChanging;
    private bool _isGmailReauthenticating;
    private bool _isMailboxChanging;
    private bool _requiresMailboxAuthorization;
    private bool _isLabelMenuOpen;
    private bool _labelMenuTargetsDetail;
    private bool _hasLoaded;
    private string? _listErrorMessage;
    private string? _messageErrorMessage;
    private string? _readStateErrorMessage;
    private string? _authorizationMessage;
    private string? _gmailReauthenticationErrorMessage;
    private string? _mailboxActionErrorMessage;
    private string? _attachmentStatusMessage;
    private bool _isAttachmentSaving;
    private MailReadFailureKind? _failureKind;
    private MailReadFailureKind? _messageFailureKind;
    private MailReadFailureKind? _readStateFailureKind;
    private MailReadStateCapability _readStateCapability = MailReadStateCapability.Unsupported;
    private string? _continuationToken;
    private long _viewVersion;
    private bool _isApplyingState;
    private bool _isReadStateMetadataUpdate;
    private bool _isDetailHostActive;
    private MailInboxPresentationMode _contentMode = MailInboxPresentationMode.MessageList;
    private MailReadDwellTarget? _readDwellTarget;
    private MailReadDwellTarget? _readDwellAttemptedTarget;
    private MailReadDwellTarget? _automaticReadMutationTarget;
    private MailReadDwellTarget? _manualUnreadSuppressionTarget;
    private Task _currentReadDwellTask = Task.CompletedTask;
    private bool _disposed;

    public MailInboxViewModel(
        IMailReadProviderFactory providerFactory,
        MailComposeViewModel? composeViewModel = null,
        IMailAttachmentSaveService? attachmentSaveService = null,
        MailMessageSourceCache? messageSourceCache = null,
        IRemoteImageSenderTrustStore? remoteImageSenderTrustStore = null)
        : this(
            providerFactory,
            composeViewModel,
            attachmentSaveService,
            messageSourceCache,
            SystemMailReadDwellScheduler.Instance,
            remoteImageSenderTrustStore)
    {
    }

    internal MailInboxViewModel(
        IMailReadProviderFactory providerFactory,
        MailComposeViewModel? composeViewModel,
        IMailAttachmentSaveService? attachmentSaveService,
        MailMessageSourceCache? messageSourceCache,
        IMailReadDwellScheduler readDwellScheduler,
        IRemoteImageSenderTrustStore? remoteImageSenderTrustStore = null)
    {
        _providerFactory = providerFactory;
        _gmailMailboxService = providerFactory.GmailMailboxManagementService;
        _mailboxService = providerFactory.MailboxManagementService;
        _attachmentSaveService = attachmentSaveService;
        _messageSourceCache = messageSourceCache;
        _readDwellScheduler = readDwellScheduler;
        _remoteImageSenderTrustStore = remoteImageSenderTrustStore ?? NullRemoteImageSenderTrustStore.Instance;
        Compose = composeViewModel ?? MailComposeViewModel.CreateUnavailable();
        Compose.PropertyChanged += OnComposePropertyChanged;
        Compose.Sent += OnMailSent;
        Compose.GmailDraftChanged += OnGmailDraftChanged;
        Compose.ManagedImapDraftChanged += OnManagedImapDraftChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        PreviousPageCommand = new AsyncRelayCommand(PreviousPageAsync, CanGoToPreviousPage);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, CanGoToNextPage);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        SearchCommand = new AsyncRelayCommand(
            SearchAsync,
            CanSearch,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ClearSearchCommand = new RelayCommand(ClearSearch, CanClearSearchCommand);
        ReauthenticateGmailCommand = new AsyncRelayCommand(ReauthenticateGmailAsync, CanReauthenticateGmail);
        RetryMessageCommand = new AsyncRelayCommand(RetryMessageAsync, CanRetryMessage);
        SetReadStateCommand = new AsyncRelayCommand(SetReadStateAsync, CanSetReadState);
        AuthorizeGmailCommand = new AsyncRelayCommand(AuthorizeGmailAsync, CanAuthorizeGmail);
        SaveAttachmentCommand = new AsyncRelayCommand<MailAttachmentInfo>(SaveAttachmentAsync, CanSaveAttachment);
        OpenMessageCommand = new RelayCommand<MailMessageSummary>(OpenMessage, CanOpenMessage);
        BackToMessageListCommand = new RelayCommand(ShowMessageList, CanShowMessageList);
        ToggleMessageSelectionCommand = new RelayCommand<MailMessageSummary>(ToggleMessageSelection, CanToggleMessageSelection);
        SelectAllLoadedCommand = new RelayCommand(ToggleSelectAllLoaded, CanSelectAllLoaded);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasSelectedMessages);
        ToggleStarCommand = new AsyncRelayCommand<MailMessageSummary>(ToggleStarAsync, CanToggleStar);
        ToggleSelectedStarCommand = new AsyncRelayCommand(ToggleSelectedStarAsync,
            () => IsGmailMailboxAvailable && CanMutateSelection());
        ArchiveSelectedCommand = new AsyncRelayCommand(ArchiveSelectedAsync, CanArchiveSelection);
        ArchiveDetailCommand = new AsyncRelayCommand(ArchiveDetailAsync, CanArchiveDetail);
        ReportSelectedSpamCommand = new AsyncRelayCommand(ReportSelectedSpamAsync, CanReportSelectionSpam);
        ReportDetailSpamCommand = new AsyncRelayCommand(ReportDetailSpamAsync, CanReportDetailSpam);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, CanDeleteSelection);
        DeleteDetailCommand = new AsyncRelayCommand(DeleteDetailAsync, CanDeleteDetail);
        RestoreSelectedCommand = new AsyncRelayCommand(RestoreSelectedAsync, CanRestoreSelection);
        RestoreDetailCommand = new AsyncRelayCommand(RestoreDetailAsync, CanRestoreDetail);
        MarkSelectedNotSpamCommand = new AsyncRelayCommand(MarkSelectedNotSpamAsync, CanMarkSelectionNotSpam);
        MarkDetailNotSpamCommand = new AsyncRelayCommand(MarkDetailNotSpamAsync, CanMarkDetailNotSpam);
        MarkSelectedReadCommand = new AsyncRelayCommand(() => SetSelectedReadStateAsync(true), CanChangeSelectedReadState);
        MarkSelectedUnreadCommand = new AsyncRelayCommand(() => SetSelectedReadStateAsync(false), CanChangeSelectedReadState);
        ToggleYandexSelectedReadStateCommand = new AsyncRelayCommand(
            ToggleYandexSelectedReadStateAsync, CanToggleYandexSelectedReadState);
        ToggleYandexDetailReadStateCommand = new AsyncRelayCommand(
            ToggleYandexDetailReadStateAsync, CanToggleYandexDetailReadState);
        OpenLabelsForSelectionCommand = new AsyncRelayCommand(() => OpenLabelsAsync(targetsDetail: false), () => IsGmailMailboxAvailable && CanMutateSelection());
        OpenLabelsForDetailCommand = new AsyncRelayCommand(() => OpenLabelsAsync(targetsDetail: true), () => IsGmailMailboxAvailable && CanMutateDetail());
        ToggleUserLabelCommand = new AsyncRelayCommand<GmailUserLabelOption>(ToggleUserLabelAsync, CanToggleUserLabel);
        CloseLabelsCommand = new RelayCommand(CloseLabels);
    }

    public ObservableCollection<MailFolder> Folders { get; } = [];
    public ObservableCollection<MailMessageSummary> Messages { get; } = [];
    public ObservableCollection<GmailUserLabelOption> UserLabels { get; } = [];
    public MailComposeViewModel Compose { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PreviousPageCommand { get; }
    public IAsyncRelayCommand NextPageCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IRelayCommand ClearSearchCommand { get; }
    public IAsyncRelayCommand ReauthenticateGmailCommand { get; }
    public IAsyncRelayCommand RetryMessageCommand { get; }
    public IAsyncRelayCommand SetReadStateCommand { get; }
    public IAsyncRelayCommand AuthorizeGmailCommand { get; }
    public IAsyncRelayCommand<MailAttachmentInfo> SaveAttachmentCommand { get; }
    public IRelayCommand<MailMessageSummary> OpenMessageCommand { get; }
    public IRelayCommand BackToMessageListCommand { get; }
    public IRelayCommand<MailMessageSummary> ToggleMessageSelectionCommand { get; }
    public IRelayCommand SelectAllLoadedCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IAsyncRelayCommand<MailMessageSummary> ToggleStarCommand { get; }
    public IAsyncRelayCommand ToggleSelectedStarCommand { get; }
    public IAsyncRelayCommand ArchiveSelectedCommand { get; }
    public IAsyncRelayCommand ArchiveDetailCommand { get; }
    public IAsyncRelayCommand ReportSelectedSpamCommand { get; }
    public IAsyncRelayCommand ReportDetailSpamCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }
    public IAsyncRelayCommand DeleteDetailCommand { get; }
    public IAsyncRelayCommand RestoreSelectedCommand { get; }
    public IAsyncRelayCommand RestoreDetailCommand { get; }
    public IAsyncRelayCommand MarkSelectedNotSpamCommand { get; }
    public IAsyncRelayCommand MarkDetailNotSpamCommand { get; }
    public IAsyncRelayCommand MarkSelectedReadCommand { get; }
    public IAsyncRelayCommand MarkSelectedUnreadCommand { get; }
    public IAsyncRelayCommand ToggleYandexSelectedReadStateCommand { get; }
    public IAsyncRelayCommand ToggleYandexDetailReadStateCommand { get; }
    public IAsyncRelayCommand OpenLabelsForSelectionCommand { get; }
    public IAsyncRelayCommand OpenLabelsForDetailCommand { get; }
    public IAsyncRelayCommand<GmailUserLabelOption> ToggleUserLabelCommand { get; }
    public IRelayCommand CloseLabelsCommand { get; }

    internal Task CurrentMessageLoadTask { get; private set; } = Task.CompletedTask;
    internal Task CurrentFolderLoadTask { get; private set; } = Task.CompletedTask;
    internal Task CurrentSearchTask { get; private set; } = Task.CompletedTask;
    internal int CachedMessageBodyCount => _messageBodyCache.Count;
    internal int CachedRemoteImageConsentCount => _remoteImageConsents.Count;
    internal bool IsReadStateMetadataUpdate => _isReadStateMetadataUpdate;
    internal Task CurrentReadDwellTask => _currentReadDwellTask;
    internal bool IsReadDwellPending => _readDwellCancellation is not null;
    internal Task CurrentRemoteImageSenderTrustTask { get; private set; } = Task.CompletedTask;

    internal bool IsInboxStale(Guid accountId) =>
        GetInboxFreshnessState(accountId).IsStale;

    internal Task GetCurrentInboxRefreshTask(Guid accountId) =>
        GetInboxFreshnessState(accountId).CurrentRefreshTask;

    public MailAccount? ActiveAccount
    {
        get => _activeAccount;
        private set
        {
            if (SetProperty(ref _activeAccount, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(AccountDisplayName));
                OnPropertyChanged(nameof(ProviderDisplayName));
                OnPropertyChanged(nameof(EmailAddress));
                OnPropertyChanged(nameof(IsGmailMailboxAvailable));
                OnPropertyChanged(nameof(IsMailboxManagementAvailable));
                OnPropertyChanged(nameof(IsYandexMailbox));
                OnPropertyChanged(nameof(IsManagedImapMailbox));
                OnPropertyChanged(nameof(IsSearchAvailable));
                OnPropertyChanged(nameof(CanUseMailboxActions));
                OnPropertyChanged(nameof(RequiresGmailReauthentication));
                OnPropertyChanged(nameof(RequiresGmailMessageReauthentication));
                OnPropertyChanged(nameof(RequiresGmailReadStateReauthentication));
                OnPropertyChanged(nameof(RequiresGmailComposeReauthentication));
                OnPropertyChanged(nameof(ShowTransientRetryAction));
                OnPropertyChanged(nameof(ShowMessageRetryAction));
                RaisePresentationStateChanged();
                BeginRemoteImageSenderTrustLookup();
                RaiseReadStateChanged();
                NotifyCommandStates();
            }
        }
    }

    public MailFolder? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!SetProperty(ref _selectedFolder, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanDeleteCurrentFolder));
            OnPropertyChanged(nameof(ShowReportSpamAction));
            OnPropertyChanged(nameof(ShowRestoreAction));
            OnPropertyChanged(nameof(ShowNotSpamAction));
            OnPropertyChanged(nameof(EmptyListMessage));
            if (_isApplyingState || value is null || ActiveAccount is null)
            {
                return;
            }

            ExitSearchMode(clearText: true, applyFolderState: false);
            AccountFolderState catalog = GetAccountFolderState(ActiveAccount.Id);
            catalog.SelectedFolderKey = value.Key;
            ClearSelectionsForAccount(ActiveAccount.Id);
            CloseLabels();
            CancelReadDwell(resetDetailSession: true);
            CurrentFolderLoadTask = SwitchFolderAsync(ActiveAccount, value);
        }
    }

    public MailMessageSummary? SelectedMessageSummary
    {
        get => _selectedMessageSummary;
        set
        {
            if (!SetProperty(ref _selectedMessageSummary, value))
            {
                return;
            }

            CancelReadDwell(resetDetailSession: true);
            OnPropertyChanged(nameof(HasSelectedMessage));
            OnPropertyChanged(nameof(DetailStarActionText));
            NotifyMailboxCommandStates();
            RaiseReadStateChanged();
            if (_isApplyingState || ActiveAccount is null || SelectedFolder is null)
            {
                return;
            }

            FolderState state = GetCurrentListState(ActiveAccount.Id, SelectedFolder.Key);
            state.SelectedMessageKey = value?.MessageKey;
            SetContentMode(
                value is null
                    ? MailInboxPresentationMode.MessageList
                    : MailInboxPresentationMode.MessageDetail,
                state);
            CancelAttachmentOperation();
            AttachmentStatusMessage = null;
            CancelMessageOperation();
            IsMessageLoading = false;
            if (value is null)
            {
                SelectedMessageContent = null;
                MessageFailureKind = null;
                MessageErrorMessage = null;
                return;
            }

            MessageBodyCacheKey cacheKey = new(ActiveAccount.Id, value.MessageKey);
            if (_messageBodyCache.TryGet(cacheKey, out MailMessageContent? cachedContent))
            {
                SelectedMessageContent = cachedContent;
                MessageFailureKind = null;
                MessageErrorMessage = null;
                CurrentMessageLoadTask = Task.CompletedTask;
                CurrentFolderLoadTask = RefreshReadStateCapabilityAsync();
                return;
            }

            CurrentMessageLoadTask = LoadSelectedMessageAsync(value);
        }
    }

    public MailMessageContent? SelectedMessageContent
    {
        get => _selectedMessageContent;
        private set
        {
            if (SetProperty(ref _selectedMessageContent, value))
            {
                bool isReadStateMetadataUpdate = _isReadStateMetadataUpdate;
                if (!isReadStateMetadataUpdate)
                {
                    SetPrintAvailable(isAvailable: false);
                    IsRemoteImageLoading = false;
                }

                OnPropertyChanged(nameof(HasSelectedContent));
                OnPropertyChanged(nameof(HasAttachments));
                OnPropertyChanged(nameof(IsSelectedMessagePlainText));
                OnPropertyChanged(nameof(IsSelectedMessageHtml));
                OnPropertyChanged(nameof(ShouldDisplayHtmlRenderer));
                OnPropertyChanged(nameof(SelectedMessageDisplayDate));
                OnPropertyChanged(nameof(ShowPrintAction));
                OnPropertyChanged(nameof(CanPrintMessage));
                if (!isReadStateMetadataUpdate && !AutomaticallyShowRemoteImages)
                {
                    BeginRemoteImageSenderTrustLookup();
                }

                RaiseReadStateChanged();
                NotifyMailboxCommandStates();
                SaveAttachmentCommand.NotifyCanExecuteChanged();
                UpdateReadDwellState();
            }
        }
    }

    public bool AreRemoteImagesShown =>
        TryGetCurrentRemoteImageConsentKey(out RemoteImageConsentKey key)
        && _remoteImageConsents.TryGet(key, out _);

    public bool IsCurrentRemoteImageSenderTrusted => _isCurrentRemoteImageSenderTrusted;

    public bool AutomaticallyShowRemoteImages => _automaticallyShowRemoteImages;

    public bool CanTrustCurrentRemoteImageSender => _canTrustCurrentRemoteImageSender;

    public bool IsRemoteImageLoading
    {
        get => _isRemoteImageLoading;
        private set
        {
            if (SetProperty(ref _isRemoteImageLoading, value))
            {
                OnPropertyChanged(nameof(CanShowRemoteImages));
                OnPropertyChanged(nameof(RemoteImagesButtonText));
                OnPropertyChanged(nameof(CanAlwaysShowRemoteImagesFromSender));
            }
        }
    }

    public bool IsListLoading
    {
        get => _isListLoading;
        private set
        {
            if (SetProperty(ref _isListLoading, value))
            {
                RaiseListStateChanged();
                NotifyCommandStates();
                if (IsManagedImapMailbox)
                {
                    OnPropertyChanged(nameof(CanUseMailboxActions));
                    NotifyMailboxCommandStates();
                }
            }
        }
    }

    public bool IsMessageLoading
    {
        get => _isMessageLoading;
        private set
        {
            if (SetProperty(ref _isMessageLoading, value))
            {
                OnPropertyChanged(nameof(ShowMessagePlaceholder));
                OnPropertyChanged(nameof(ShowPrintAction));
                OnPropertyChanged(nameof(CanPrintMessage));
                RetryMessageCommand.NotifyCanExecuteChanged();
                NotifyMailboxCommandStates();
            }
        }
    }

    public bool IsReadStateChanging
    {
        get => _isReadStateChanging;
        private set
        {
            if (SetProperty(ref _isReadStateChanging, value))
            {
                RaiseReadStateChanged();
                if (IsManagedImapMailbox)
                {
                    OnPropertyChanged(nameof(CanUseMailboxActions));
                    NotifyMailboxCommandStates();
                }
            }
        }
    }

    public bool IsGmailReauthenticating
    {
        get => _isGmailReauthenticating;
        private set
        {
            if (SetProperty(ref _isGmailReauthenticating, value))
            {
                ReauthenticateGmailCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsMailboxChanging
    {
        get => _isMailboxChanging;
        private set
        {
            if (SetProperty(ref _isMailboxChanging, value))
            {
                OnPropertyChanged(nameof(CanUseMailboxActions));
                NotifyMailboxCommandStates();
                if (IsManagedImapMailbox)
                {
                    RaiseReadStateChanged();
                    RefreshCommand.NotifyCanExecuteChanged();
                    PreviousPageCommand.NotifyCanExecuteChanged();
                    NextPageCommand.NotifyCanExecuteChanged();
                    if (!value && ActiveAccount is MailAccount account)
                    {
                        TryStartInboxAutoRefresh(account.Id, GetInboxFreshnessState(account.Id));
                    }
                }
            }
        }
    }

    public bool IsLabelMenuOpen
    {
        get => _isLabelMenuOpen;
        private set => SetProperty(ref _isLabelMenuOpen, value);
    }

    public string? MailboxActionErrorMessage
    {
        get => _mailboxActionErrorMessage;
        private set
        {
            if (SetProperty(ref _mailboxActionErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasMailboxActionError));
            }
        }
    }

    public bool HasLoaded
    {
        get => _hasLoaded;
        private set
        {
            if (SetProperty(ref _hasLoaded, value))
            {
                RaiseListStateChanged();
            }
        }
    }

    public string? ListErrorMessage
    {
        get => _listErrorMessage;
        private set
        {
            if (SetProperty(ref _listErrorMessage, value))
            {
                RaiseListStateChanged();
            }
        }
    }

    public string? MessageErrorMessage
    {
        get => _messageErrorMessage;
        private set
        {
            if (SetProperty(ref _messageErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasMessageError));
                OnPropertyChanged(nameof(RequiresGmailMessageReauthentication));
                OnPropertyChanged(nameof(ShowMessageRetryAction));
                OnPropertyChanged(nameof(ShowMessagePlaceholder));
                OnPropertyChanged(nameof(ShowPrintAction));
                OnPropertyChanged(nameof(CanPrintMessage));
                RetryMessageCommand.NotifyCanExecuteChanged();
                NotifyMailboxCommandStates();
            }
        }
    }

    public string? ReadStateErrorMessage
    {
        get => _readStateErrorMessage;
        private set
        {
            if (SetProperty(ref _readStateErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasReadStateError));
                OnPropertyChanged(nameof(RequiresGmailReadStateReauthentication));
            }
        }
    }

    public MailReadFailureKind? MessageFailureKind
    {
        get => _messageFailureKind;
        private set
        {
            if (SetProperty(ref _messageFailureKind, value))
            {
                OnPropertyChanged(nameof(RequiresGmailMessageReauthentication));
                OnPropertyChanged(nameof(ShowMessageRetryAction));
            }
        }
    }

    public MailReadFailureKind? ReadStateFailureKind
    {
        get => _readStateFailureKind;
        private set
        {
            if (SetProperty(ref _readStateFailureKind, value))
            {
                OnPropertyChanged(nameof(RequiresGmailReadStateReauthentication));
            }
        }
    }

    public string? AuthorizationMessage
    {
        get => _authorizationMessage;
        private set
        {
            if (SetProperty(ref _authorizationMessage, value))
            {
                RaiseReadStateChanged();
            }
        }
    }

    public string? GmailReauthenticationErrorMessage
    {
        get => _gmailReauthenticationErrorMessage;
        private set
        {
            if (SetProperty(ref _gmailReauthenticationErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasGmailReauthenticationError));
            }
        }
    }

    public MailReadFailureKind? FailureKind
    {
        get => _failureKind;
        private set
        {
            if (SetProperty(ref _failureKind, value))
            {
                OnPropertyChanged(nameof(ErrorTitle));
                RaiseListStateChanged();
            }
        }
    }

    public string? ContinuationToken
    {
        get => _continuationToken;
        private set
        {
            if (SetProperty(ref _continuationToken, value))
            {
                OnPropertyChanged(nameof(HasMore));
                RaisePaginationStateChanged();
            }
        }
    }

    public bool IsActive => ActiveAccount is { IsEnabled: true };
    public bool IsGmailMailboxAvailable =>
        ActiveAccount is { Provider: MailProviderType.Gmail, IsEnabled: true }
        && _gmailMailboxService is not null;
    public bool IsYandexMailbox =>
        ActiveAccount is { IsEnabled: true } account
        && MailProviderFeaturePolicies.Get(account.Provider).UsesYandexPresentation;
    public bool IsManagedImapMailbox =>
        ActiveAccount is { IsEnabled: true } account
        && MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap;
    public bool IsMailboxManagementAvailable => IsGmailMailboxAvailable
        || ActiveAccount is { IsEnabled: true } account && _mailboxService?.Supports(account.Provider) == true;
    public bool CanUseMailboxActions => IsMailboxManagementAvailable && !IsMailboxChanging
        && !(IsManagedImapMailbox && (IsReadStateChanging || IsListLoading));
    public bool ShowArchiveAction => IsGmailMailboxAvailable
        || IsManagedImapMailbox && ActiveAccount is MailAccount account
        && _accountFolderStates.TryGetValue(account.Id, out AccountFolderState? catalog)
        && catalog.HasLoaded
        && catalog.Folders.Any(folder => folder.IsAvailable && folder.CanAcceptArchive);
    public bool IsSearchAvailable =>
        ActiveAccount is { IsEnabled: true } account
        && SupportsServerSearch(account)
        && !IsComposeOpen;
    public bool IsSearchActive =>
        ActiveAccount is MailAccount account
        && _searchAccountId == account.Id
        && !string.IsNullOrWhiteSpace(_activeSearchQuery)
        && _searchState is not null;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanClearSearch));
                SearchCommand.NotifyCanExecuteChanged();
                ClearSearchCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public string? ActiveSearchQuery => IsSearchActive ? _activeSearchQuery : null;
    public bool CanClearSearch => IsSearchActive || !string.IsNullOrWhiteSpace(SearchText);
    public int SelectedMessageCount => Messages.Count(message => message.IsSelected);
    public bool HasSelectedMessages => SelectedMessageCount > 0;
    public bool AreAllLoadedMessagesSelected
    {
        get
        {
            MailMessageSummary[] selectable = Messages.Where(CanUseMailboxActionsForMessage).ToArray();
            return selectable.Length > 0 && selectable.All(message => message.IsSelected);
        }
    }
    public bool AreAllSelectedMessagesStarred =>
        HasSelectedMessages && GetSelectedMessages().All(message => message.IsStarred);
    public bool? LoadedSelectionState => !HasSelectedMessages
        ? false
        : AreAllLoadedMessagesSelected ? true : null;
    public bool HasMailboxActionError => !string.IsNullOrWhiteSpace(MailboxActionErrorMessage);
    public bool HasUserLabels => UserLabels.Count > 0;
    public string LabelMenuTitle => _labelMenuTargetsDetail
        ? "Ярлыки письма"
        : $"Ярлыки: выбрано {SelectedMessageCount}";
    public string SelectedStarActionText =>
        AreAllSelectedMessagesStarred ? "Снять пометку" : "Пометить";
    public string DetailStarActionText => SelectedMessageSummary?.IsStarred == true
        ? "Снять пометку"
        : "Пометить";
    public bool CanDeleteCurrentFolder => IsSearchActive || SelectedFolder?.Kind is not MailFolderKind.Trash;
    public bool ShowReportSpamAction =>
        (IsGmailMailboxAvailable || IsManagedImapMailbox)
        && !IsSearchActive
        && SelectedFolder?.Kind is MailFolderKind.Inbox;
    public bool ShowRestoreAction => !IsSearchActive && SelectedFolder?.Kind is MailFolderKind.Trash;
    public bool ShowNotSpamAction => !IsSearchActive && SelectedFolder?.Kind is MailFolderKind.Spam;
    public bool IsComposeOpen => Compose.IsOpen;
    public MailInboxPresentationMode PresentationMode => IsComposeOpen
        ? MailInboxPresentationMode.Compose
        : _contentMode;
    public bool IsMessageListVisible => IsActive && PresentationMode is MailInboxPresentationMode.MessageList;
    public bool IsMessageDetailVisible => IsActive && PresentationMode is MailInboxPresentationMode.MessageDetail;
    public bool ShouldDisplayHtmlRenderer => IsMessageDetailVisible && IsSelectedMessageHtml;
    public bool HasFolders => Folders.Count > 0;
    public bool HasMessages => Messages.Count > 0;
    public bool HasMore => !string.IsNullOrWhiteSpace(ContinuationToken);
    public bool IsPageNavigationVisible => HasLoaded && !HasBlockingListError;
    public bool CanNavigateToPreviousPage => _displayedListState?.PageIndex > 0;
    public bool CanNavigateToNextPage => HasMore;
    public string PageRangeText
    {
        get
        {
            FolderState? state = _displayedListState;
            if (state is null || Messages.Count == 0)
            {
                return state?.TotalCount is long emptyTotal
                    ? $"0–0 из {emptyTotal}"
                    : "0–0";
            }

            long first = ((long)state.PageIndex * PageSize) + 1;
            long last = first + Messages.Count - 1;
            return state.TotalCount is long exactTotal
                ? $"{first}–{last} из {Math.Max(exactTotal, last)}"
                : $"{first}–{last}";
        }
    }
    public bool HasListError => !string.IsNullOrWhiteSpace(ListErrorMessage);
    public bool HasBlockingListError => HasListError && !HasMessages;
    public bool HasGmailReauthenticationError => !string.IsNullOrWhiteSpace(GmailReauthenticationErrorMessage);
    public bool RequiresGmailReauthentication =>
        ActiveAccount is { Provider: MailProviderType.Gmail } account
        && _gmailReauthenticationRequiredAccounts.Contains(account.Id);
    public bool RequiresGmailMessageReauthentication =>
        RequiresGmailReauthentication
        && MessageFailureKind is MailReadFailureKind.ReauthorizationRequired
        && HasMessageError;
    public bool RequiresGmailReadStateReauthentication =>
        RequiresGmailReauthentication
        && ReadStateFailureKind is MailReadFailureKind.ReauthorizationRequired
        && HasReadStateError;
    public bool RequiresGmailComposeReauthentication =>
        RequiresGmailReauthentication && IsComposeOpen;
    public bool ShowTransientRetryAction => HasBlockingListError && !RequiresGmailReauthentication;
    public bool HasMessageError => !string.IsNullOrWhiteSpace(MessageErrorMessage);
    public bool ShowMessageRetryAction => HasMessageError && !RequiresGmailMessageReauthentication;
    public bool HasReadStateError => !string.IsNullOrWhiteSpace(ReadStateErrorMessage);
    public bool IsInitialLoading => IsListLoading && !HasMessages;
    public bool IsEmpty => HasLoaded && !IsListLoading && !HasMessages && !HasListError;
    public string EmptyListMessage => IsSearchActive
        ? "По вашему запросу ничего не найдено."
        : SelectedFolder?.IsUserLabel == true
            ? "В этом ярлыке нет писем."
            : "В этой папке пока нет писем.";
    public bool HasSelectedMessage => SelectedMessageSummary is not null;
    public bool HasSelectedContent => SelectedMessageContent is not null;
    public bool HasAttachments => SelectedMessageContent?.Attachments.Count > 0;
    public bool IsSelectedMessagePlainText => SelectedMessageContent?.BodyKind is MailMessageBodyKind.PlainText;
    public bool IsSelectedMessageHtml => SelectedMessageContent?.BodyKind is MailMessageBodyKind.SanitizedHtml;
    public bool ShowRemoteImagesBanner =>
        !AutomaticallyShowRemoteImages
        && SelectedMessageContent?.HasRemoteImages == true
        && (IsCurrentRemoteImageSenderTrusted || !AreRemoteImagesShown);
    public bool ShowOneTimeRemoteImagesAction =>
        ShowRemoteImagesBanner
        && !IsCurrentRemoteImageSenderTrusted
        && !AreRemoteImagesShown;
    public bool ShowAlwaysRemoteImagesFromSenderAction =>
        ShowOneTimeRemoteImagesAction && CanTrustCurrentRemoteImageSender;
    public bool ShowRevokeRemoteImagesFromSenderAction =>
        ShowRemoteImagesBanner && IsCurrentRemoteImageSenderTrusted;
    public bool CanShowRemoteImages => ShowOneTimeRemoteImagesAction && !IsRemoteImageLoading;
    public bool CanAlwaysShowRemoteImagesFromSender =>
        ShowAlwaysRemoteImagesFromSenderAction && !IsRemoteImageLoading;
    public string RemoteImagesButtonText => IsRemoteImageLoading ? "Загружаем…" : "Показать";
    public string RemoteImagesBannerText => IsCurrentRemoteImageSenderTrusted
        ? "Внешние изображения автоматически разрешены для этого отправителя."
        : "Внешние изображения заблокированы для защиты конфиденциальности.";
    public bool IsAttachmentSaving
    {
        get => _isAttachmentSaving;
        private set
        {
            if (SetProperty(ref _isAttachmentSaving, value))
            {
                SaveAttachmentCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? AttachmentStatusMessage
    {
        get => _attachmentStatusMessage;
        private set
        {
            if (SetProperty(ref _attachmentStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasAttachmentStatus));
            }
        }
    }

    public bool HasAttachmentStatus => !string.IsNullOrWhiteSpace(AttachmentStatusMessage);
    public bool ShowMessagePlaceholder => !HasSelectedContent && !IsMessageLoading && !HasMessageError;
    public bool ShowPrintAction =>
        IsMessageDetailVisible
        && HasSelectedContent
        && IsSelectedMessageHtml
        && !IsMessageLoading
        && !HasMessageError
        && !IsComposeOpen;
    public bool CanPrintMessage => ShowPrintAction && _isPrintAvailable;
    public bool ShowReadStateAction =>
        HasSelectedContent
        && (IsSearchActive || SelectedFolder?.SupportsReadState == true)
        && _readStateCapability.CanSetReadState;
    public bool ShowStandardReadStateAction => ShowReadStateAction && !IsYandexMailbox;
    public bool ShowYandexSelectedReadStateAction =>
        IsYandexMailbox
        && IsMessageListVisible
        && HasSelectedMessages
        && (IsSearchActive || SelectedFolder?.SupportsReadState == true);
    public bool ShowYandexDetailReadStateAction =>
        IsYandexMailbox && ShowReadStateAction;
    public bool YandexSelectedReadStateWillMarkRead =>
        GetSelectedMessages().Any(message => message.IsUnread);
    public string YandexSelectedReadStateActionText =>
        YandexSelectedReadStateWillMarkRead ? "Прочитано" : "Непрочитано";
    public bool YandexDetailReadStateWillMarkRead => SelectedMessageSummary?.IsUnread == true;
    public string YandexDetailReadStateActionText =>
        YandexDetailReadStateWillMarkRead ? "Прочитано" : "Непрочитано";
    public bool RequiresGmailAuthorization =>
        ActiveAccount?.Provider is MailProviderType.Gmail
        && (_readStateCapability.RequiresAuthorization || _requiresMailboxAuthorization);
    public bool CanChangeReadState =>
        _readStateCapability.CanSetReadState
        && HasSelectedContent
        && !IsReadStateChanging
        && !(IsManagedImapMailbox && IsMailboxChanging);
    public string ReadStateActionText => IsReadStateChanging
        ? "Сохраняем…"
        : SelectedMessageSummary?.IsUnread == true
            ? "Отметить как прочитанное"
            : "Отметить как непрочитанное";
    public string SelectedMessageDisplayDate => SelectedMessageContent is null
        ? string.Empty
        : SelectedMessageContent.ReceivedAt.ToLocalTime().ToString(
            "ddd, d MMM, HH:mm",
            CultureInfo.CurrentCulture);
    public string ReadStateAuthorizationText =>
        AuthorizationMessage
        ?? _readStateCapability.UserMessage
        ?? "Чтобы менять статус писем, нужно снова разрешить доступ Google.";
    public string AccountDisplayName => ActiveAccount?.DisplayLabel ?? string.Empty;
    public string EmailAddress => ActiveAccount?.EmailAddress ?? string.Empty;
    public string ProviderDisplayName => ActiveAccount?.Provider switch
    {
        MailProviderType.Gmail => "Gmail",
        MailProviderType.Yandex => "Яндекс Почта",
        MailProviderType.MailRu => "Почта Mail.ru",
        MailProviderType.GenericImap => "IMAP",
        _ => "Почта"
    };

    public string ErrorTitle => FailureKind switch
    {
        MailReadFailureKind.ReauthorizationRequired => "Требуется вход в Google",
        MailReadFailureKind.AuthenticationFailed or MailReadFailureKind.CredentialMissing => "Не удалось войти в почту",
        MailReadFailureKind.FolderUnavailable => "Папка недоступна",
        MailReadFailureKind.InvalidSearchQuery => "Не удалось выполнить поиск",
        _ => "Не удалось загрузить почту"
    };
    public string? ListErrorDescription => FailureKind is MailReadFailureKind.ReauthorizationRequired
        ? "Срок действия подключения истёк или доступ был отозван. Войдите в Google снова, чтобы продолжить получать почту."
        : ListErrorMessage;

    public void SetRemoteImageLoading(bool isLoading)
    {
        if (!_disposed)
        {
            IsRemoteImageLoading = isLoading;
        }
    }

    public void MarkRemoteImagesShown()
    {
        if (_disposed)
        {
            return;
        }

        IsRemoteImageLoading = false;
        if (TryGetCurrentRemoteImageConsentKey(out RemoteImageConsentKey key))
        {
            _remoteImageConsents.Set(key, true);
            RaiseRemoteImageConsentStateChanged();
        }
    }

    internal void ForgetRemoteImagesShown(Guid accountId, string messageKey)
    {
        _remoteImageConsents.RemoveWhere(
            key => key.AccountId == accountId
                && string.Equals(key.MessageKey, messageKey, StringComparison.Ordinal));
        if (ActiveAccount?.Id == accountId
            && string.Equals(SelectedMessageContent?.MessageKey, messageKey, StringComparison.Ordinal))
        {
            RaiseRemoteImageConsentStateChanged();
        }
    }

    public async Task ActivateAsync(MailAccount? account, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelActivation();
        ResetSearchState(clearText: true);
        ClearSelection();
        CloseLabels();
        _readStateCapability = MailReadStateCapability.Unsupported;
        long version = ++_viewVersion;
        _activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ActiveAccount = account;
        if (account is not null)
        {
            ClearSelectionsForAccount(account.Id);
        }
        Compose.ActivateAccount(account);
        IsListLoading = false;
        IsMessageLoading = false;
        MessageFailureKind = null;
        MessageErrorMessage = null;
        ReadStateErrorMessage = null;
        ReadStateFailureKind = null;
        AuthorizationMessage = null;
        _requiresMailboxAuthorization = false;
        GmailReauthenticationErrorMessage = null;
        MailboxActionErrorMessage = null;

        if (account is null || !account.IsEnabled)
        {
            ClearDisplayedState(clearFolders: true);
            return;
        }

        AccountFolderState folderState = GetAccountFolderState(account.Id);
        if (!folderState.HasLoaded)
        {
            await LoadFoldersAsync(account, folderState, version, _activationCancellation.Token);
            return;
        }

        ApplyFolders(folderState);
        MailFolder? folder = ResolveSelectedFolder(folderState);
        if (folder is null)
        {
            ShowNoFoldersError();
            return;
        }

        SetSelectedFolderWithoutSwitch(folder);
        FolderState state = GetState(account.Id, folder.Key);
        ApplyState(state);
        bool requiresFreshInbox = IsTrackedInbox(account, folder) && IsInboxStale(account.Id);
        if (!state.HasLoaded || requiresFreshInbox)
        {
            if (folder.Kind is not MailFolderKind.Inbox)
            {
                await RefreshInboxUnreadCountAsync(account, version, _activationCancellation.Token);
            }

            if (IsTrackedInbox(account, folder))
            {
                await RefreshInboxFirstPageAsync(
                    account,
                    folder,
                    state,
                    version,
                    _activationCancellation.Token);
            }
            else
            {
                await LoadPageAsync(account, folder, state, PageRequest.First, version, _activationCancellation.Token);
            }
        }
        else
        {
            await RefreshInboxUnreadCountAsync(account, version, _activationCancellation.Token);
            await RefreshReadStateCapabilityAsync();
        }
    }

    public void SetAutomaticallyShowRemoteImages(bool enabled)
    {
        if (_disposed || _automaticallyShowRemoteImages == enabled)
        {
            return;
        }

        _automaticallyShowRemoteImages = enabled;
        OnPropertyChanged(nameof(AutomaticallyShowRemoteImages));
        if (enabled)
        {
            CancelRemoteImageSenderTrustLookup();
            RaiseRemoteImageConsentStateChanged();
        }
        else
        {
            BeginRemoteImageSenderTrustLookup();
        }
    }

    public void OpenInbox(Guid accountId)
    {
        ThrowIfDisposed();
        AccountFolderState accountState = GetAccountFolderState(accountId);
        accountState.SelectedFolderKey = MailFolderCatalog.InboxKey;
        if (ActiveAccount?.Id != accountId)
        {
            return;
        }

        MailFolder? inbox = accountState.Folders.FirstOrDefault(
            folder => folder.Kind == MailFolderKind.Inbox);
        if (inbox is not null)
        {
            if (IsSearchActive && ReferenceEquals(SelectedFolder, inbox))
            {
                ExitSearchMode(clearText: true, applyFolderState: true);
            }
            else
            {
                SelectedFolder = inbox;
            }
        }
    }

    void IMailInboxFreshnessService.OnNewMailDetected(
        Guid mailAccountId,
        bool isAccountActivelyViewed) =>
        OnNewMailDetected(mailAccountId, isAccountActivelyViewed);

    void IMailInboxFreshnessService.RequireFreshInbox(Guid mailAccountId) =>
        RequireFreshInbox(mailAccountId);

    internal void OnNewMailDetected(Guid mailAccountId, bool isAccountActivelyViewed)
    {
        if (_disposed)
        {
            return;
        }

        InboxFreshnessState freshness = GetInboxFreshnessState(mailAccountId);
        freshness.MarkChanged();
        MarkFolderStale(mailAccountId, MailFolderKind.AllMail);
        MarkUserLabelFoldersStale(mailAccountId);
        if (isAccountActivelyViewed && IsActiveTrackedInbox(mailAccountId))
        {
            freshness.AutoRefreshRequested = true;
            TryStartInboxAutoRefresh(mailAccountId, freshness);
        }
    }

    internal void RequireFreshInbox(Guid mailAccountId)
    {
        if (_disposed)
        {
            return;
        }

        InboxFreshnessState freshness = GetInboxFreshnessState(mailAccountId);
        freshness.MarkChanged();
        MarkFolderStale(mailAccountId, MailFolderKind.AllMail);
        if (IsActiveTrackedInbox(mailAccountId))
        {
            freshness.AutoRefreshRequested = true;
            TryStartInboxAutoRefresh(mailAccountId, freshness);
        }
    }

    public void RemoveAccount(Guid accountId)
    {
        CancelActivation();
        if (_searchAccountId == accountId)
        {
            ResetSearchState(clearText: true);
        }
        _accountFolderStates.Remove(accountId);
        _inboxFreshnessStates.Remove(accountId);
        _gmailReauthenticationRequiredAccounts.Remove(accountId);
        foreach (FolderStateKey key in _folderStates.Keys.Where(key => key.AccountId == accountId).ToArray())
        {
            _folderStates.Remove(key);
        }

        _remoteImageConsents.RemoveWhere(key => key.AccountId == accountId);
        _messageBodyCache.RemoveWhere(key => key.AccountId == accountId);
        _messageSourceCache?.RemoveAccount(accountId);
        _gmailMailboxService?.RemoveAccount(accountId);
        Compose.RemoveAccount(accountId);
        if (ActiveAccount?.Id == accountId)
        {
            ActiveAccount = null;
            ClearDisplayedState(clearFolders: true);
        }
    }

    internal async Task RefreshAfterPasswordReplacementAsync(
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(account);
        if (!MailProviderFeaturePolicies.Get(account.Provider).SupportsAppPasswordReplacement)
        {
            return;
        }

        foreach ((FolderStateKey key, FolderState state) in _folderStates)
        {
            if (key.AccountId == account.Id)
            {
                state.MarkStale();
            }
        }

        if (_accountFolderStates.TryGetValue(account.Id, out AccountFolderState? folderState))
        {
            folderState.HasLoaded = false;
        }

        if (ActiveAccount?.Id == account.Id)
        {
            await ActivateAsync(account, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SetDetailHostActive(false);
        _disposed = true;
        CancelRemoteImageSenderTrustLookup();
        CancelActivation();
        Compose.PropertyChanged -= OnComposePropertyChanged;
        Compose.Sent -= OnMailSent;
        Compose.GmailDraftChanged -= OnGmailDraftChanged;
        Compose.ManagedImapDraftChanged -= OnManagedImapDraftChanged;
        Compose.Dispose();
        _folderStates.Clear();
        _accountFolderStates.Clear();
        _inboxFreshnessStates.Clear();
        _gmailReauthenticationRequiredAccounts.Clear();
        ResetSearchState(clearText: true);
        _messageBodyCache.Clear();
        _remoteImageConsents.Clear();
        _messageSourceCache?.Clear();
    }

    private async Task LoadFoldersAsync(
        MailAccount account,
        AccountFolderState accountState,
        long version,
        CancellationToken cancellationToken)
    {
        IsListLoading = true;
        ClearDisplayedState(clearFolders: true, preserveLoading: true);
        try
        {
            IMailReadProvider provider = _providerFactory.Get(account.Provider);
            IReadOnlyList<MailFolder> folders = await provider.GetFoldersAsync(account, cancellationToken);
            if (!IsCurrentAccount(account.Id, version, cancellationToken))
            {
                return;
            }

            accountState.Folders.Clear();
            accountState.Folders.AddRange(folders.Where(folder => folder.IsAvailable));
            PrimeUserLabelCatalog(account, accountState.Folders);
            accountState.HasLoaded = true;
            ApplyFolders(accountState);
            MailFolder? selected = ResolveSelectedFolder(accountState);
            if (selected is null)
            {
                ShowNoFoldersError();
                return;
            }

            accountState.SelectedFolderKey = selected.Key;
            SetSelectedFolderWithoutSwitch(selected);
            FolderState state = GetState(account.Id, selected.Key);
            ApplyState(state);
            if (IsTrackedInbox(account, selected))
            {
                await RefreshInboxFirstPageAsync(account, selected, state, version, cancellationToken);
            }
            else
            {
                await LoadPageAsync(account, selected, state, PageRequest.First, version, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MailReadException exception) when (IsCurrentAccount(account.Id, version, cancellationToken))
        {
            HasLoaded = true;
            ListErrorMessage = exception.UserMessage;
            FailureKind = exception.FailureKind;
            MarkGmailReauthenticationRequired(account, exception.FailureKind);
        }
        catch (Exception) when (IsCurrentAccount(account.Id, version, cancellationToken))
        {
            HasLoaded = true;
            ListErrorMessage = "Не удалось загрузить папки почты. Попробуйте ещё раз.";
            FailureKind = MailReadFailureKind.ConnectionFailed;
        }
        finally
        {
            if (IsCurrentAccount(account.Id, version, cancellationToken))
            {
                IsListLoading = false;
            }
        }
    }

    private async Task SwitchFolderAsync(MailAccount account, MailFolder folder)
    {
        long version = ++_viewVersion;
        CancelReadDwell(resetDetailSession: true);
        CancelListOperation();
        CancelMessageOperation();
        CancelMutationOperation();
        ReadStateFailureKind = null;
        ReadStateErrorMessage = null;
        AuthorizationMessage = null;
        MailboxActionErrorMessage = null;
        FolderState state = GetState(account.Id, folder.Key);
        ApplyState(state);
        bool requiresFreshInbox = IsTrackedInbox(account, folder) && IsInboxStale(account.Id);
        if (!state.HasLoaded || requiresFreshInbox)
        {
            if (IsTrackedInbox(account, folder))
            {
                await RefreshInboxFirstPageAsync(account, folder, state, version, GetActivationToken());
            }
            else
            {
                await LoadPageAsync(account, folder, state, PageRequest.First, version, GetActivationToken());
            }
        }
        else
        {
            await RefreshReadStateCapabilityAsync();
        }
    }

    private Task SearchAsync()
    {
        CurrentSearchTask = SearchCoreAsync();
        return CurrentSearchTask;
    }

    private async Task SearchCoreAsync()
    {
        string query = SearchText.Trim();
        if (query.Length == 0)
        {
            ClearSearch();
            return;
        }

        if (ActiveAccount is not { IsEnabled: true } account
            || !SupportsServerSearch(account)
            || SelectedFolder is null
            || _providerFactory.Get(account.Provider) is not IMailSearchProvider)
        {
            return;
        }

        long version = ++_viewVersion;
        CancelReadDwell(resetDetailSession: true);
        CancelListOperation();
        CancelMessageOperation();
        CancelMutationOperation();
        CancelAttachmentOperation();
        ClearSelection();
        CloseLabels();
        _searchAccountId = account.Id;
        _activeSearchQuery = query;
        _searchState = new FolderState();
        RaiseSearchStateChanged();
        ApplyState(_searchState);
        await LoadSearchPageAsync(
            account,
            _searchState,
            PageRequest.First,
            version,
            GetActivationToken());
    }

    private void ClearSearch()
    {
        if (_disposed || !CanClearSearch)
        {
            return;
        }

        ExitSearchMode(clearText: true, applyFolderState: true);
    }

    private void ExitSearchMode(bool clearText, bool applyFolderState)
    {
        bool hadSearch = IsSearchActive || _searchState is not null || !string.IsNullOrWhiteSpace(_activeSearchQuery);
        bool hadText = !string.IsNullOrEmpty(_searchText);
        if (!hadSearch && (!clearText || !hadText))
        {
            return;
        }

        if (hadSearch)
        {
            ++_viewVersion;
            CancelReadDwell(resetDetailSession: true);
            CancelListOperation();
            CancelMessageOperation();
            CancelMutationOperation();
            CancelAttachmentOperation();
            CloseLabels();
            IsListLoading = false;
        }

        ResetSearchState(clearText);
        if (!hadSearch
            || !applyFolderState
            || ActiveAccount is not { IsEnabled: true } account
            || SelectedFolder is not MailFolder folder)
        {
            return;
        }

        FolderState state = GetState(account.Id, folder.Key);
        state.SelectedMessageKey = null;
        state.ContentMode = MailInboxPresentationMode.MessageList;
        ApplyState(state);
        bool requiresFreshInbox = IsTrackedInbox(account, folder) && IsInboxStale(account.Id);
        if (!state.HasLoaded || requiresFreshInbox)
        {
            CurrentFolderLoadTask = IsTrackedInbox(account, folder)
                ? RefreshInboxFirstPageAsync(
                    account,
                    folder,
                    state,
                    _viewVersion,
                    GetActivationToken())
                : LoadPageAsync(
                    account,
                    folder,
                    state,
                    PageRequest.First,
                    _viewVersion,
                    GetActivationToken());
        }
    }

    private void ResetSearchState(bool clearText)
    {
        _searchState = null;
        _searchAccountId = null;
        _activeSearchQuery = null;
        if (clearText)
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
        }

        RaiseSearchStateChanged();
    }

    private void RaiseSearchStateChanged()
    {
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(ActiveSearchQuery));
        OnPropertyChanged(nameof(CanClearSearch));
        OnPropertyChanged(nameof(EmptyListMessage));
        OnPropertyChanged(nameof(CanDeleteCurrentFolder));
        OnPropertyChanged(nameof(ShowReportSpamAction));
        OnPropertyChanged(nameof(ShowRestoreAction));
        OnPropertyChanged(nameof(ShowNotSpamAction));
        SearchCommand.NotifyCanExecuteChanged();
        ClearSearchCommand.NotifyCanExecuteChanged();
        RaiseListStateChanged();
        NotifyMailboxCommandStates();
    }

    private async Task<bool> LoadSearchPageAsync(
        MailAccount account,
        FolderState state,
        PageRequest request,
        long version,
        CancellationToken activationToken)
    {
        string query = _activeSearchQuery ?? string.Empty;
        CancelListOperation();
        CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(activationToken);
        _listCancellation = operationCancellation;
        CancellationToken cancellationToken = operationCancellation.Token;
        IsListLoading = true;
        ListErrorMessage = null;
        FailureKind = null;
        try
        {
            if (_providerFactory.Get(account.Provider) is not IMailSearchProvider searchProvider)
            {
                return false;
            }

            MailFolder folder = SelectedFolder
                ?? throw new MailReadException(MailReadFailureKind.FolderUnavailable, "Эта папка недоступна.");
            MailPage<MailMessageSummary> page = await searchProvider.SearchAsync(
                account,
                folder,
                query,
                request.Token,
                PageSize,
                cancellationToken);
            if (!IsCurrentSearch(account.Id, query, version, cancellationToken))
            {
                return false;
            }

            bool preserveSelection = request.PageIndex == state.PageIndex;
            HashSet<string> selectedKeys = preserveSelection
                ? state.Messages
                    .Where(message => message.IsSelected)
                    .Select(message => message.MessageKey)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            state.Messages.Clear();
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (MailMessageSummary summary in page.Items)
            {
                if (keys.Add(summary.MessageKey))
                {
                    summary.IsSelected = selectedKeys.Contains(summary.MessageKey);
                    state.Messages.Add(summary);
                }
            }

            if (state.SelectedMessageKey is not null
                && state.Messages.All(message => message.MessageKey != state.SelectedMessageKey))
            {
                state.SelectedMessageKey = null;
                state.ContentMode = MailInboxPresentationMode.MessageList;
            }

            state.ApplyPage(
                request,
                page.ContinuationToken,
                IsManagedImap(account)
                    ? page.TotalCount ?? state.TotalCount
                    : null);
            state.HasLoaded = true;
            state.ListErrorMessage = null;
            state.FailureKind = null;
            ApplyState(state);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (MailReadException exception) when (IsCurrentSearch(account.Id, query, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = exception.UserMessage;
            state.FailureKind = exception.FailureKind;
            MarkGmailReauthenticationRequired(account, exception.FailureKind);
            ApplyState(state);
            return false;
        }
        catch (Exception) when (IsCurrentSearch(account.Id, query, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = "Не удалось выполнить поиск. Проверьте подключение к сети.";
            state.FailureKind = MailReadFailureKind.ConnectionFailed;
            ApplyState(state);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_listCancellation, operationCancellation))
            {
                _listCancellation = null;
                operationCancellation.Dispose();
                IsListLoading = false;
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account || SelectedFolder is not MailFolder folder)
        {
            return;
        }

        if (IsSearchActive && _searchState is FolderState searchState)
        {
            searchState.PrepareRefresh();
            await LoadSearchPageAsync(
                account,
                searchState,
                IsManagedImap(account)
                    ? PageRequest.First
                    : searchState.CurrentPageRequest,
                _viewVersion,
                GetActivationToken());
            return;
        }

        if (account.Provider is MailProviderType.Gmail)
        {
            MailFolder? refreshedFolder = await RefreshUserLabelCatalogAsync(
                account,
                folder,
                _viewVersion,
                GetActivationToken());
            if (refreshedFolder is null)
            {
                return;
            }

            folder = refreshedFolder;
        }

        FolderState state = GetState(account.Id, folder.Key);
        ContinuationToken = null;
        ListErrorMessage = null;
        FailureKind = null;
        if (IsManagedImap(account))
        {
            state.PrepareRefresh();
            await LoadPageAsync(account, folder, state, PageRequest.First, _viewVersion, GetActivationToken());
        }
        else if (IsTrackedInbox(account, folder) && state.PageIndex == 0)
        {
            await RefreshInboxFirstPageAsync(account, folder, state, _viewVersion, GetActivationToken());
        }
        else
        {
            state.PrepareRefresh();
            await LoadPageAsync(account, folder, state, state.CurrentPageRequest, _viewVersion, GetActivationToken());
        }
    }

    private Task PreviousPageAsync() => NavigatePageAsync(forward: false);

    private Task NextPageAsync() => NavigatePageAsync(forward: true);

    private async Task NavigatePageAsync(bool forward)
    {
        if (ActiveAccount is not { IsEnabled: true } account || SelectedFolder is not MailFolder folder)
        {
            return;
        }

        FolderState state = GetCurrentListState(account.Id, folder.Key);
        PageRequest request;
        bool hasRequest = forward
            ? state.TryCreateNextPageRequest(out request)
            : state.TryCreatePreviousPageRequest(out request);
        if (!hasRequest)
        {
            return;
        }

        ClearSelection();
        CloseLabels();
        if (IsSearchActive)
        {
            await LoadSearchPageAsync(account, state, request, _viewVersion, GetActivationToken());
        }
        else
        {
            await LoadPageAsync(account, folder, state, request, _viewVersion, GetActivationToken());
        }
    }

    private async Task RetryAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account)
        {
            return;
        }

        if (IsSearchActive && _searchState is FolderState searchState)
        {
            searchState.PrepareRefresh();
            await LoadSearchPageAsync(
                account,
                searchState,
                searchState.CurrentPageRequest,
                _viewVersion,
                GetActivationToken());
            return;
        }

        if (!HasFolders)
        {
            AccountFolderState accountState = GetAccountFolderState(account.Id);
            accountState.HasLoaded = false;
            await LoadFoldersAsync(account, accountState, _viewVersion, GetActivationToken());
            return;
        }

        if (SelectedFolder is not MailFolder folder)
        {
            return;
        }

        FolderState state = GetState(account.Id, folder.Key);
        if (state.Messages.Count == 0)
        {
            state.Reset();
            ApplyState(state);
            await LoadPageAsync(account, folder, state, PageRequest.First, _viewVersion, GetActivationToken());
        }
        else
        {
            await LoadPageAsync(account, folder, state, state.CurrentPageRequest, _viewVersion, GetActivationToken());
        }
    }

    private async Task ReauthenticateGmailAsync()
    {
        if (ActiveAccount is not { Provider: MailProviderType.Gmail, IsEnabled: true } account
            || _providerFactory.GmailReauthenticationService is not IGmailReauthenticationService reauthenticationService)
        {
            return;
        }

        Guid accountId = account.Id;
        long version = _viewVersion;
        CancellationToken cancellationToken = GetActivationToken();
        bool recoverList = FailureKind is MailReadFailureKind.ReauthorizationRequired && HasListError;
        bool recoverMessage = RequiresGmailMessageReauthentication;
        bool recoverReadState = RequiresGmailReadStateReauthentication;
        IsGmailReauthenticating = true;
        GmailReauthenticationErrorMessage = null;
        try
        {
            GmailReauthenticationResult result = await reauthenticationService.ReauthenticateAsync(
                account,
                cancellationToken);
            if (!IsCurrentAccount(accountId, version, cancellationToken))
            {
                return;
            }

            if (result.Outcome is GmailReauthenticationOutcome.Canceled)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                GmailReauthenticationErrorMessage = result.UserMessage
                    ?? "Не удалось войти в Google. Попробуйте ещё раз.";
                return;
            }

            ClearGmailReauthenticationRequired(accountId);
            _requiresMailboxAuthorization = false;
            MailboxActionErrorMessage = null;
            Compose.ClearGmailReauthenticationError(accountId);
            MessageFailureKind = null;
            MessageErrorMessage = null;
            ReadStateFailureKind = null;
            ReadStateErrorMessage = null;

            if (recoverList || (!recoverMessage && !recoverReadState && !Compose.IsOpen))
            {
                await RefreshAfterGmailReauthenticationAsync(account, version, cancellationToken);
            }

            if ((recoverMessage || recoverReadState)
                && IsCurrentAccount(accountId, version, cancellationToken)
                && SelectedMessageSummary is MailMessageSummary summary)
            {
                CurrentMessageLoadTask = LoadSelectedMessageAsync(summary);
                await CurrentMessageLoadTask;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Account changes cancel stale OAuth UI without altering the account or its error state.
        }
        finally
        {
            IsGmailReauthenticating = false;
        }
    }

    private async Task RefreshAfterGmailReauthenticationAsync(
        MailAccount account,
        long version,
        CancellationToken cancellationToken)
    {
        GmailReauthenticationErrorMessage = null;
        ListErrorMessage = null;
        FailureKind = null;
        if (IsSearchActive && _searchState is FolderState searchState)
        {
            searchState.PrepareRefresh();
            await LoadSearchPageAsync(
                account,
                searchState,
                searchState.CurrentPageRequest,
                version,
                cancellationToken);
            return;
        }

        AccountFolderState accountState = GetAccountFolderState(account.Id);
        if (!HasFolders || SelectedFolder is not MailFolder folder)
        {
            accountState.HasLoaded = false;
            await LoadFoldersAsync(account, accountState, version, cancellationToken);
            return;
        }

        FolderState state = GetState(account.Id, folder.Key);
        state.PrepareRefresh();
        ContinuationToken = null;
        if (IsTrackedInbox(account, folder))
        {
            await RefreshInboxFirstPageAsync(account, folder, state, version, cancellationToken);
        }
        else
        {
            await LoadPageAsync(account, folder, state, PageRequest.First, version, cancellationToken);
        }
    }

    private void MarkGmailReauthenticationRequired(MailAccount account, MailReadFailureKind failureKind)
    {
        if (account.Provider is MailProviderType.Gmail
            && failureKind is MailReadFailureKind.ReauthorizationRequired
            && _gmailReauthenticationRequiredAccounts.Add(account.Id))
        {
            RaiseGmailReauthenticationStateChanged();
        }
    }

    private void MarkGmailReauthenticationRequired(MailAccount account, MailSendFailureKind failureKind)
    {
        if (account.Provider is MailProviderType.Gmail
            && failureKind is MailSendFailureKind.ReauthorizationRequired
            && _gmailReauthenticationRequiredAccounts.Add(account.Id))
        {
            RaiseGmailReauthenticationStateChanged();
        }
    }

    private void ClearGmailReauthenticationRequired(Guid accountId)
    {
        if (_gmailReauthenticationRequiredAccounts.Remove(accountId))
        {
            RaiseGmailReauthenticationStateChanged();
        }
    }

    private void RaiseGmailReauthenticationStateChanged()
    {
        OnPropertyChanged(nameof(RequiresGmailReauthentication));
        OnPropertyChanged(nameof(RequiresGmailMessageReauthentication));
        OnPropertyChanged(nameof(RequiresGmailReadStateReauthentication));
        OnPropertyChanged(nameof(RequiresGmailComposeReauthentication));
        OnPropertyChanged(nameof(ShowTransientRetryAction));
        RaisePaginationStateChanged();
        OnPropertyChanged(nameof(ShowMessageRetryAction));
        ReauthenticateGmailCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RetryMessageCommand.NotifyCanExecuteChanged();
    }

    private async Task RetryMessageAsync()
    {
        if (SelectedMessageSummary is MailMessageSummary summary)
        {
            CurrentMessageLoadTask = LoadSelectedMessageAsync(summary);
            await CurrentMessageLoadTask;
        }
    }

    private async Task<bool> RefreshInboxFirstPageAsync(
        MailAccount account,
        MailFolder folder,
        FolderState state,
        long version,
        CancellationToken cancellationToken)
    {
        InboxFreshnessState freshness = GetInboxFreshnessState(account.Id);
        long refreshGeneration = freshness.ChangeGeneration;
        state.PrepareRefresh();
        bool succeeded = await LoadPageAsync(
            account,
            folder,
            state,
            PageRequest.First,
            version,
            cancellationToken);
        if (succeeded)
        {
            freshness.MarkRefreshedThrough(refreshGeneration);
            TryStartInboxAutoRefresh(account.Id, freshness);
        }
        else
        {
            freshness.AutoRefreshRequested = false;
        }

        return succeeded;
    }

    private void TryStartInboxAutoRefresh(Guid accountId, InboxFreshnessState freshness)
    {
        if (_disposed
            || freshness.IsAutoRefreshRunning
            || !freshness.AutoRefreshRequested
            || !freshness.IsStale
            || IsListLoading
            || (IsManagedImapMailbox && IsMailboxChanging)
            || !IsActiveTrackedInbox(accountId))
        {
            return;
        }

        freshness.IsAutoRefreshRunning = true;
        freshness.CurrentRefreshTask = RunInboxAutoRefreshAsync(accountId, freshness);
    }

    private async Task RunInboxAutoRefreshAsync(Guid accountId, InboxFreshnessState freshness)
    {
        try
        {
            while (freshness.AutoRefreshRequested
                && freshness.IsStale
                && ActiveAccount is { IsEnabled: true } account
                && SupportsTrackedInbox(account.Provider)
                && account.Id == accountId
                && SelectedFolder is { Kind: MailFolderKind.Inbox } folder
                && !IsSearchActive
                && !IsComposeOpen
                && !(IsManagedImapMailbox && IsMailboxChanging))
            {
                freshness.AutoRefreshRequested = false;
                FolderState state = GetState(accountId, folder.Key);
                bool succeeded = await RefreshInboxFirstPageAsync(
                    account,
                    folder,
                    state,
                    _viewVersion,
                    GetActivationToken());
                if (!succeeded)
                {
                    return;
                }
            }
        }
        finally
        {
            freshness.IsAutoRefreshRunning = false;
        }
    }

    private bool IsActiveTrackedInbox(Guid accountId) =>
        ActiveAccount is { IsEnabled: true } account
        && SupportsTrackedInbox(account.Provider)
        && account.Id == accountId
        && SelectedFolder?.Kind is MailFolderKind.Inbox
        && !IsSearchActive
        && !IsComposeOpen;

    private static bool IsTrackedInbox(MailAccount account, MailFolder folder) =>
        SupportsTrackedInbox(account.Provider)
        && folder.Kind is MailFolderKind.Inbox;

    private static bool SupportsTrackedInbox(MailProviderType provider) =>
        provider is MailProviderType.Gmail || MailProviderFeaturePolicies.Get(provider).IsManagedImap;

    private InboxFreshnessState GetInboxFreshnessState(Guid accountId)
    {
        if (!_inboxFreshnessStates.TryGetValue(accountId, out InboxFreshnessState? state))
        {
            state = new InboxFreshnessState();
            _inboxFreshnessStates.Add(accountId, state);
        }

        return state;
    }

    private async Task<bool> LoadPageAsync(
        MailAccount account,
        MailFolder folder,
        FolderState state,
        PageRequest request,
        long version,
        CancellationToken activationToken)
    {
        CancelListOperation();
        CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(activationToken);
        _listCancellation = operationCancellation;
        CancellationToken cancellationToken = operationCancellation.Token;
        IsListLoading = true;
        ListErrorMessage = null;
        FailureKind = null;
        try
        {
            IMailReadProvider provider = _providerFactory.Get(account.Provider);
            Task<int?> unreadCountTask = request.PageIndex == 0 && folder.Kind is MailFolderKind.Inbox
                ? TryGetInboxUnreadCountAsync(provider, account, cancellationToken)
                : Task.FromResult<int?>(null);
            MailPage<MailMessageSummary> page = await provider.GetPageAsync(
                account,
                folder,
                request.Token,
                PageSize,
                cancellationToken);
            if (!IsCurrent(account.Id, folder.Key, version, cancellationToken))
            {
                return false;
            }

            int? inboxUnreadCount = await unreadCountTask;
            if (!IsCurrent(account.Id, folder.Key, version, cancellationToken))
            {
                return false;
            }

            if (inboxUnreadCount is int exactUnreadCount)
            {
                account.InboxUnreadCount = exactUnreadCount;
            }

            bool preserveSelection = request.PageIndex == state.PageIndex;
            HashSet<string> selectedKeys = preserveSelection
                ? state.Messages
                    .Where(message => message.IsSelected)
                    .Select(message => message.MessageKey)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            MailMessageSummary? retained = preserveSelection && state.SelectedMessageKey is not null
                ? state.Messages.FirstOrDefault(message => message.MessageKey == state.SelectedMessageKey)
                : null;
            state.Messages.Clear();
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (MailMessageSummary summary in page.Items)
            {
                if (keys.Add(summary.MessageKey))
                {
                    summary.IsSelected = selectedKeys.Contains(summary.MessageKey);
                    state.Messages.Add(summary);
                }
            }

            if (request.PageIndex == 0 && state.PendingVisibleMessages.Count > 0)
            {
                uint? pageUidValidity = TryGetImapPageUidValidity(folder.Kind, page.Items);
                if (pageUidValidity is uint currentUidValidity)
                {
                    foreach (string pendingKey in state.PendingVisibleMessages.Keys.ToArray())
                    {
                        try
                        {
                            (uint pendingUidValidity, _) =
                                ImapMailReadProvider.ParseMessageKey(folder.Kind, pendingKey);
                            if (pendingUidValidity != currentUidValidity)
                            {
                                state.PendingVisibleMessages.Remove(pendingKey);
                            }
                        }
                        catch (MailReadException)
                        {
                            state.PendingVisibleMessages.Remove(pendingKey);
                        }
                    }
                }

                foreach (string confirmedKey in keys)
                {
                    state.PendingVisibleMessages.Remove(confirmedKey);
                }

                MailMessageSummary[] pending = state.PendingVisibleMessages.Values
                    .Where(message => !keys.Contains(message.MessageKey))
                    .ToArray();
                if (pending.Length > 0)
                {
                    MailMessageSummary[] chronologicalPage = state.Messages
                        .Concat(pending)
                        .OrderByDescending(message => message.ReceivedAt)
                        .ThenByDescending(message => message.MessageKey, StringComparer.Ordinal)
                        .Take(PageSize)
                        .ToArray();
                    state.Messages.Clear();
                    keys.Clear();
                    foreach (MailMessageSummary message in chronologicalPage)
                    {
                        if (keys.Add(message.MessageKey))
                        {
                            state.Messages.Add(message);
                        }
                    }
                }
            }

            if (retained is not null && keys.Add(retained.MessageKey))
            {
                retained.IsSelected = false;
                state.Messages.Add(retained);
            }

            state.ApplyPage(
                request,
                page.ContinuationToken,
                IsManagedImap(account) && request.PageIndex > 0
                    ? page.TotalCount ?? state.TotalCount
                    : page.TotalCount);
            state.HasLoaded = true;
            state.ListErrorMessage = null;
            state.FailureKind = null;
            ApplyState(state);
            if (preserveSelection
                && SelectedMessageContent is null
                && SelectedMessageSummary is MailMessageSummary restored
                && CurrentMessageLoadTask.IsCompleted)
            {
                CurrentMessageLoadTask = LoadSelectedMessageAsync(restored);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (MailReadException exception) when (
            exception.FailureKind is MailReadFailureKind.FolderUnavailable
            && folder.IsUserLabel
            && IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            if (await TryRecoverUnavailableUserLabelAsync(
                account,
                folder,
                version,
                activationToken))
            {
                return false;
            }

            state.HasLoaded = true;
            state.ListErrorMessage = exception.UserMessage;
            state.FailureKind = exception.FailureKind;
            ApplyState(state);
            return false;
        }
        catch (MailReadException exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = exception.UserMessage;
            state.FailureKind = exception.FailureKind;
            MarkGmailReauthenticationRequired(account, exception.FailureKind);
            ApplyState(state);
            return false;
        }
        catch (Exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = "Не удалось загрузить почту. Попробуйте ещё раз.";
            state.FailureKind = MailReadFailureKind.ConnectionFailed;
            ApplyState(state);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_listCancellation, operationCancellation))
            {
                _listCancellation = null;
                operationCancellation.Dispose();
                IsListLoading = false;
            }
        }
    }

    private static uint? TryGetImapPageUidValidity(
        MailFolderKind folderKind,
        IReadOnlyList<MailMessageSummary> messages)
    {
        foreach (MailMessageSummary message in messages)
        {
            try
            {
                return ImapMailReadProvider.ParseMessageKey(folderKind, message.MessageKey).UidValidity;
            }
            catch (MailReadException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<bool> TryRecoverUnavailableUserLabelAsync(
        MailAccount account,
        MailFolder unavailableFolder,
        long version,
        CancellationToken activationToken)
    {
        IReadOnlyList<GmailUserLabel>? labels = await TryLoadUserLabelCatalogAsync(
            account,
            version,
            activationToken);
        if (labels is null || !IsCurrentAccount(account.Id, version, activationToken))
        {
            return false;
        }

        SynchronizeUserLabelFolders(account, labels);
        if (SelectedFolder is not MailFolder safeFolder
            || string.Equals(safeFolder.Key, unavailableFolder.Key, StringComparison.Ordinal))
        {
            return false;
        }

        MailboxActionErrorMessage = "Ярлык больше недоступен. Открыты входящие.";
        FolderState safeState = GetState(account.Id, safeFolder.Key);
        safeState.PrepareRefresh();
        ApplyState(safeState);
        if (IsTrackedInbox(account, safeFolder))
        {
            await RefreshInboxFirstPageAsync(
                account,
                safeFolder,
                safeState,
                version,
                activationToken);
        }
        else
        {
            await LoadPageAsync(
                account,
                safeFolder,
                safeState,
                PageRequest.First,
                version,
                activationToken);
        }

        return true;
    }

    private async Task LoadSelectedMessageAsync(MailMessageSummary summary)
    {
        if (ActiveAccount is not { IsEnabled: true } account || SelectedFolder is not MailFolder folder)
        {
            return;
        }

        long version = _viewVersion;
        _messageCancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        CancellationToken cancellationToken = _messageCancellation.Token;
        IsMessageLoading = true;
        MessageFailureKind = null;
        MessageErrorMessage = null;
        SelectedMessageContent = null;
        try
        {
            IMailReadProvider provider = _providerFactory.Get(account.Provider);
            MailMessageContent content = await provider.GetMessageAsync(account, folder, summary.MessageKey, cancellationToken);
            if (!string.Equals(content.MessageKey, summary.MessageKey, StringComparison.Ordinal))
            {
                throw new MailReadException(
                    MailReadFailureKind.InvalidMessage,
                    "Поставщик вернул содержимое другого письма.");
            }

            if (_disposed || !_accountFolderStates.ContainsKey(account.Id))
            {
                return;
            }

            _messageBodyCache.Set(new MessageBodyCacheKey(account.Id, summary.MessageKey), content);
            if (!IsCurrent(account.Id, folder.Key, version, cancellationToken)
                || SelectedMessageSummary?.MessageKey != summary.MessageKey)
            {
                return;
            }

            FolderState state = GetCurrentListState(account.Id, folder.Key);
            state.SelectedMessageKey = summary.MessageKey;
            SelectedMessageContent = content;
            MessageFailureKind = null;
            await RefreshReadStateCapabilityAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MailReadException exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            MessageFailureKind = exception.FailureKind;
            MessageErrorMessage = exception.UserMessage;
            MarkGmailReauthenticationRequired(account, exception.FailureKind);
        }
        catch (Exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            MessageFailureKind = MailReadFailureKind.ConnectionFailed;
            MessageErrorMessage = "Не удалось загрузить выбранное письмо.";
        }
        finally
        {
            if (IsCurrent(account.Id, folder.Key, version, cancellationToken))
            {
                IsMessageLoading = false;
            }
        }
    }

    private async Task RefreshReadStateCapabilityAsync()
    {
        _readStateCapability = MailReadStateCapability.Unsupported;
        if (ActiveAccount is not MailAccount account
            || SelectedFolder is not MailFolder folder
            || _providerFactory.Get(account.Provider) is not IMailMessageStateProvider stateProvider)
        {
            RaiseReadStateChanged();
            return;
        }

        MailFolder readStateFolder = IsSearchActive ? MailFolderCatalog.Inbox() : folder;
        if (!readStateFolder.SupportsReadState)
        {
            RaiseReadStateChanged();
            return;
        }

        try
        {
            ReadStateFailureKind = null;
            _readStateCapability = await stateProvider.GetReadStateCapabilityAsync(
                account,
                readStateFolder,
                GetActivationToken());
        }
        catch (MailReadException exception)
        {
            ReadStateFailureKind = exception.FailureKind;
            ReadStateErrorMessage = exception.UserMessage;
            MarkGmailReauthenticationRequired(account, exception.FailureKind);
        }
        finally
        {
            RaiseReadStateChanged();
        }
    }

    private async Task SetReadStateAsync()
    {
        if (!TryGetCurrentReadDwellTarget(out MailReadDwellTarget target)
            || SelectedMessageSummary is not MailMessageSummary summary)
        {
            return;
        }

        CancelReadDwell(resetDetailSession: false);
        _readDwellAttemptedTarget = target;
        await ChangeReadStateAsync(summary.IsUnread, ReadStateMutationOrigin.Manual, target);
    }

    public void SetPrintAvailable(bool isAvailable)
    {
        if (_disposed || _isPrintAvailable == isAvailable)
        {
            return;
        }

        _isPrintAvailable = isAvailable;
        OnPropertyChanged(nameof(CanPrintMessage));
    }

    private async Task ChangeReadStateAsync(
        bool isRead,
        ReadStateMutationOrigin origin,
        MailReadDwellTarget expectedTarget,
        CancellationToken operationToken = default)
    {
        if (ActiveAccount is not MailAccount account
            || SelectedFolder is not MailFolder folder
            || SelectedMessageSummary is not MailMessageSummary summary
            || !expectedTarget.Matches(account.Id, folder.Key, summary.MessageKey, _viewVersion)
            || _providerFactory.Get(account.Provider) is not IMailMessageStateProvider provider)
        {
            return;
        }

        CancelMutationOperation();
        _mutationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            GetActivationToken(),
            operationToken);
        CancellationToken cancellationToken = _mutationCancellation.Token;
        IsReadStateChanging = true;
        ReadStateFailureKind = null;
        ReadStateErrorMessage = null;
        try
        {
            if (origin is ReadStateMutationOrigin.AutomaticDwell
                && !IsCurrentReadDwellTarget(expectedTarget))
            {
                return;
            }

            MailFolder readStateFolder = IsSearchActive ? MailFolderCatalog.Inbox() : folder;
            await provider.SetReadStateAsync(
                account,
                readStateFolder,
                summary.MessageKey,
                isRead,
                cancellationToken);
            if (!IsCurrent(account.Id, folder.Key, expectedTarget.ViewVersion, cancellationToken)
                || SelectedMessageSummary?.MessageKey != summary.MessageKey
                || (origin is ReadStateMutationOrigin.AutomaticDwell
                    && !IsCurrentReadDwellTarget(expectedTarget)))
            {
                return;
            }

            if (origin is ReadStateMutationOrigin.Manual && !isRead)
            {
                _manualUnreadSuppressionTarget = expectedTarget;
            }

            bool isUnread = !isRead;
            bool affectsInboxUnreadCount = summary.ProviderLabelIds.Contains(GmailSystemFolders.Inbox)
                || (!IsSearchActive && folder.Kind is MailFolderKind.Inbox);
            if (affectsInboxUnreadCount
                && account.InboxUnreadCount is int inboxUnreadCount
                && summary.IsUnread != isUnread)
            {
                account.InboxUnreadCount = Math.Max(0, inboxUnreadCount + (isUnread ? 1 : -1));
            }

            HashSet<string> updatedLabels = summary.ProviderLabelIds.ToHashSet(StringComparer.Ordinal);
            if (isUnread)
            {
                updatedLabels.Add(GmailSystemFolders.Unread);
            }
            else
            {
                updatedLabels.Remove(GmailSystemFolders.Unread);
            }

            MailMessageSummary updatedSummary = summary with
            {
                IsUnread = isUnread,
                ProviderLabelIds = updatedLabels
            };
            FolderState state = GetCurrentListState(account.Id, folder.Key);
            int stateIndex = state.Messages.FindIndex(message => message.MessageKey == summary.MessageKey);
            if (stateIndex >= 0)
            {
                state.Messages[stateIndex] = updatedSummary;
            }

            int visibleIndex = Messages.IndexOf(summary);
            MailMessageContent? selectedContent = SelectedMessageContent?.MessageKey == summary.MessageKey
                ? SelectedMessageContent
                : _messageBodyCache.TryGet(
                    new MessageBodyCacheKey(account.Id, summary.MessageKey),
                    out MailMessageContent? cachedContent)
                    ? cachedContent
                    : null;

            _isApplyingState = true;
            try
            {
                if (visibleIndex >= 0)
                {
                    Messages[visibleIndex] = updatedSummary;
                }

                _selectedMessageSummary = updatedSummary;
                OnPropertyChanged(nameof(SelectedMessageSummary));
                OnPropertyChanged(nameof(HasSelectedMessage));
            }
            finally
            {
                _isApplyingState = false;
            }

            if (selectedContent is not null)
            {
                MailMessageContent updatedContent = selectedContent with { IsUnread = isUnread };
                _messageBodyCache.Set(
                    new MessageBodyCacheKey(account.Id, summary.MessageKey),
                    updatedContent);
                _isReadStateMetadataUpdate = true;
                try
                {
                    SelectedMessageContent = updatedContent;
                }
                finally
                {
                    _isReadStateMetadataUpdate = false;
                }
            }

            RaiseReadStateChanged();
            if (IsCurrentSearch(account.Id, _activeSearchQuery, expectedTarget.ViewVersion, CancellationToken.None)
                && _searchState is FolderState searchState)
            {
                searchState.PrepareRefresh();
                await LoadSearchPageAsync(
                    account,
                    searchState,
                    PageRequest.First,
                    expectedTarget.ViewVersion,
                    GetActivationToken());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MailReadException exception)
        {
            ReadStateFailureKind = exception.FailureKind;
            ReadStateErrorMessage = exception.UserMessage;
            MarkGmailReauthenticationRequired(account, exception.FailureKind);
        }
        catch (Exception)
        {
            ReadStateFailureKind = MailReadFailureKind.MutationFailed;
            ReadStateErrorMessage = "Не удалось изменить статус письма. Попробуйте ещё раз.";
        }
        finally
        {
            IsReadStateChanging = false;
        }
    }

    public async Task TrustCurrentRemoteImageSenderAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentRemoteImageSenderTrustTarget(out RemoteImageSenderTrustTarget target))
        {
            return;
        }

        await _remoteImageSenderTrustStore.TrustAsync(target.AccountId, target.NormalizedAddress, cancellationToken);
        if (IsCurrentRemoteImageSenderTrustTarget(target))
        {
            SetCurrentRemoteImageSenderTrustState(canTrust: true, isTrusted: true);
        }
    }

    public async Task RevokeCurrentRemoteImageSenderTrustAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentRemoteImageSenderTrustTarget(out RemoteImageSenderTrustTarget target))
        {
            return;
        }

        await _remoteImageSenderTrustStore.RevokeAsync(target.AccountId, target.NormalizedAddress, cancellationToken);
        if (IsCurrentRemoteImageSenderTrustTarget(target))
        {
            SetCurrentRemoteImageSenderTrustState(canTrust: true, isTrusted: false);
        }
    }

    public Task DeleteRemoteImageSenderTrustAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        _remoteImageSenderTrustStore.DeleteAccountAsync(accountId, cancellationToken);

    internal void SetDetailHostActive(bool isActive)
    {
        if (_disposed || _isDetailHostActive == isActive)
        {
            return;
        }

        _isDetailHostActive = isActive;
        if (!isActive)
        {
            CancelReadDwell(resetDetailSession: false);
            return;
        }

        UpdateReadDwellState();
    }

    private async Task AuthorizeGmailAsync()
    {
        if (ActiveAccount is not MailAccount account
            || _providerFactory.GmailScopeUpgradeService is not IGmailScopeUpgradeService upgradeService)
        {
            return;
        }

        IsReadStateChanging = true;
        AuthorizationMessage = null;
        try
        {
            GmailScopeUpgradeResult result = await upgradeService.UpgradeAsync(account, GetActivationToken());
            if (!result.IsSuccess)
            {
                AuthorizationMessage = result.UserMessage;
                return;
            }

            AuthorizationMessage = null;
            _requiresMailboxAuthorization = false;
            MailboxActionErrorMessage = null;
            await RefreshReadStateCapabilityAsync();
        }
        catch (OperationCanceledException)
        {
            AuthorizationMessage = "Разрешение Google не изменено.";
        }
        finally
        {
            IsReadStateChanging = false;
        }
    }

    private void ApplyFolders(AccountFolderState accountState)
    {
        _isApplyingState = true;
        try
        {
            Folders.Clear();
            foreach (MailFolder folder in accountState.Folders)
            {
                Folders.Add(folder);
            }

            OnPropertyChanged(nameof(HasFolders));
            NotifyMailboxCommandStates();
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private MailFolder? ResolveSelectedFolder(AccountFolderState accountState) =>
        accountState.SelectedFolderKey is string key
            ? accountState.Folders.FirstOrDefault(folder => folder.Key == key)
                ?? accountState.Folders.FirstOrDefault(folder => folder.Kind is MailFolderKind.Inbox)
                ?? accountState.Folders.FirstOrDefault()
            : accountState.Folders.FirstOrDefault(folder => folder.Kind is MailFolderKind.Inbox)
                ?? accountState.Folders.FirstOrDefault();

    private void SetSelectedFolderWithoutSwitch(MailFolder folder)
    {
        _isApplyingState = true;
        try
        {
            SelectedFolder = folder;
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void ApplyState(FolderState state)
    {
        _displayedListState = state;
        _isApplyingState = true;
        try
        {
            SetContentMode(state.ContentMode, state);
            Messages.Clear();
            foreach (MailMessageSummary summary in state.Messages)
            {
                Messages.Add(summary);
            }

            ContinuationToken = state.ContinuationToken;
            HasLoaded = state.HasLoaded;
            ListErrorMessage = state.ListErrorMessage;
            FailureKind = state.FailureKind;
            MailMessageSummary? selected = state.SelectedMessageKey is null
                ? null
                : Messages.FirstOrDefault(message => message.MessageKey == state.SelectedMessageKey);
            if (!ReferenceEquals(_selectedMessageSummary, selected))
            {
                _selectedMessageSummary = selected;
                OnPropertyChanged(nameof(SelectedMessageSummary));
                OnPropertyChanged(nameof(HasSelectedMessage));
            }

            SelectedMessageContent = selected is not null
                && _messageBodyCache.TryGet(
                    new MessageBodyCacheKey(ActiveAccount!.Id, selected.MessageKey),
                    out MailMessageContent? cachedContent)
                    ? cachedContent
                    : null;
            MessageFailureKind = null;
            MessageErrorMessage = null;
            ReadStateFailureKind = null;
            ReadStateErrorMessage = null;
            RaiseListStateChanged();
            RaiseReadStateChanged();
            RaiseMailboxStateChanged();
            RaisePaginationStateChanged();
        }
        finally
        {
            _isApplyingState = false;
        }

        UpdateReadDwellState();
    }

    private void ClearDisplayedState(bool clearFolders, bool preserveLoading = false)
    {
        _displayedListState = null;
        _isApplyingState = true;
        try
        {
            SetContentMode(MailInboxPresentationMode.MessageList);
            if (clearFolders)
            {
                Folders.Clear();
                _selectedFolder = null;
                OnPropertyChanged(nameof(SelectedFolder));
                OnPropertyChanged(nameof(HasFolders));
            }

            Messages.Clear();
            _selectedMessageSummary = null;
            OnPropertyChanged(nameof(SelectedMessageSummary));
            SelectedMessageContent = null;
            ContinuationToken = null;
            HasLoaded = false;
            if (!preserveLoading)
            {
                IsListLoading = false;
            }
            IsMessageLoading = false;
            ListErrorMessage = null;
            MessageFailureKind = null;
            MessageErrorMessage = null;
            ReadStateFailureKind = null;
            ReadStateErrorMessage = null;
            GmailReauthenticationErrorMessage = null;
            FailureKind = null;
            _readStateCapability = MailReadStateCapability.Unsupported;
            RaiseListStateChanged();
            RaiseReadStateChanged();
            RaisePaginationStateChanged();
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void ShowNoFoldersError()
    {
        HasLoaded = true;
        ListErrorMessage = "Почтовый сервер не предоставил доступные системные папки.";
        FailureKind = MailReadFailureKind.FolderUnavailable;
    }

    private FolderState GetState(Guid accountId, string folderKey)
    {
        FolderStateKey key = new(accountId, folderKey);
        if (!_folderStates.TryGetValue(key, out FolderState? state))
        {
            state = new FolderState();
            _folderStates.Add(key, state);
        }

        return state;
    }

    private FolderState GetCurrentListState(Guid accountId, string folderKey) =>
        IsSearchActive
        && _searchAccountId == accountId
        && _searchState is FolderState searchState
            ? searchState
            : GetState(accountId, folderKey);

    internal bool IsFolderStateStale(Guid accountId, MailFolderKind kind)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        MailFolder? folder = account.Folders.FirstOrDefault(item => item.Kind == kind);
        return folder is not null && !GetState(accountId, folder.Key).HasLoaded;
    }

    internal bool IsUserLabelFolderStateStale(Guid accountId, string labelId)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        MailFolder? folder = account.Folders.FirstOrDefault(item =>
            item.IsUserLabel
            && string.Equals(item.ProviderLocator, labelId, StringComparison.Ordinal));
        return folder is not null && !GetState(accountId, folder.Key).HasLoaded;
    }

    private void OnComposePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MailComposeViewModel.IsOpen)
            or nameof(MailComposeViewModel.IsClosed)
            or nameof(MailComposeViewModel.Draft))
        {
            if (Compose.IsOpen)
            {
                CancelReadDwell(resetDetailSession: true);
            }

            OnPropertyChanged(nameof(IsComposeOpen));
            OnPropertyChanged(nameof(IsSearchAvailable));
            OnPropertyChanged(nameof(RequiresGmailComposeReauthentication));
            SearchCommand.NotifyCanExecuteChanged();
            RaisePresentationStateChanged();

            if (!Compose.IsOpen
                && ActiveAccount is MailAccount mailAccount
                && (mailAccount.Provider is MailProviderType.Gmail || IsManagedImap(mailAccount))
                && SelectedFolder is { } folder
                && (folder.Kind is MailFolderKind.Drafts
                    || mailAccount.Provider is MailProviderType.Gmail && folder.Kind is MailFolderKind.AllMail)
                && !GetState(mailAccount.Id, folder.Key).HasLoaded
                && !IsListLoading)
            {
                CurrentFolderLoadTask = RefreshAsync();
            }
        }

        if (eventArgs.PropertyName is nameof(MailComposeViewModel.RequiresGmailReauthentication)
            && Compose.ActiveAccount is MailAccount account)
        {
            if (Compose.FailureKind is MailSendFailureKind failureKind)
            {
                MarkGmailReauthenticationRequired(account, failureKind);
            }

            RaiseGmailReauthenticationStateChanged();
        }
    }

    private void PrimeUserLabelCatalog(MailAccount account, IReadOnlyList<MailFolder> folders)
    {
        if (account.Provider is not MailProviderType.Gmail || _gmailMailboxService is null)
        {
            return;
        }

        _gmailMailboxService.UseUserLabelCatalog(
            account.Id,
            folders
                .Where(folder => folder.IsUserLabel)
                .Select(folder => new GmailUserLabel(folder.ProviderLocator, folder.DisplayName))
                .ToArray());
    }

    private async Task<MailFolder?> RefreshUserLabelCatalogAsync(
        MailAccount account,
        MailFolder currentFolder,
        long version,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GmailUserLabel>? labels = await TryLoadUserLabelCatalogAsync(
            account,
            version,
            cancellationToken);
        if (labels is null)
        {
            return currentFolder;
        }

        string currentFolderKey = currentFolder.Key;
        SynchronizeUserLabelFolders(account, labels);
        MailFolder? refreshedFolder = SelectedFolder;
        if (currentFolder.IsUserLabel
            && !string.Equals(currentFolderKey, refreshedFolder?.Key, StringComparison.Ordinal))
        {
            MailboxActionErrorMessage = "Ярлык больше недоступен.";
        }

        return refreshedFolder;
    }

    private async Task<IReadOnlyList<GmailUserLabel>?> TryLoadUserLabelCatalogAsync(
        MailAccount account,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<MailFolder> folders = await _providerFactory
                .Get(account.Provider)
                .GetFoldersAsync(account, cancellationToken);
            if (!IsCurrentAccount(account.Id, version, cancellationToken))
            {
                return null;
            }

            return folders
                .Where(folder => folder.IsUserLabel)
                .Select(folder => new GmailUserLabel(folder.ProviderLocator, folder.DisplayName))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (MailReadException exception)
        {
            MarkGmailReauthenticationRequired(account, exception.FailureKind);
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SynchronizeUserLabelFolders(
        MailAccount account,
        IReadOnlyList<GmailUserLabel> labels)
    {
        AccountFolderState accountState = GetAccountFolderState(account.Id);
        string? selectedKey = accountState.SelectedFolderKey ??
            (ActiveAccount?.Id == account.Id ? SelectedFolder?.Key : null);
        HashSet<string> previousKeys = accountState.Folders
            .Where(folder => folder.IsUserLabel)
            .Select(folder => folder.Key)
            .ToHashSet(StringComparer.Ordinal);
        GmailUserLabel[] normalized = labels
            .Where(label => !string.IsNullOrWhiteSpace(label.Id)
                && !string.IsNullOrWhiteSpace(label.DisplayName))
            .GroupBy(label => label.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(label => label.DisplayName, GmailUserLabelNameComparer.Instance)
            .ThenBy(label => label.Id, StringComparer.Ordinal)
            .ToArray();

        accountState.Folders.RemoveAll(folder => folder.IsUserLabel);
        for (int index = 0; index < normalized.Length; index++)
        {
            GmailUserLabel label = normalized[index];
            accountState.Folders.Add(MailFolderCatalog.CreateUserLabel(
                label.Id,
                label.DisplayName,
                showsSectionHeader: index == 0));
        }

        _gmailMailboxService?.UseUserLabelCatalog(account.Id, normalized);
        HashSet<string> currentKeys = accountState.Folders
            .Where(folder => folder.IsUserLabel)
            .Select(folder => folder.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string removedKey in previousKeys.Except(currentKeys, StringComparer.Ordinal))
        {
            _folderStates.Remove(new FolderStateKey(account.Id, removedKey));
        }

        MailFolder? replacement = selectedKey is null
            ? null
            : accountState.Folders.FirstOrDefault(folder =>
                string.Equals(folder.Key, selectedKey, StringComparison.Ordinal));
        replacement ??= accountState.Folders.FirstOrDefault(folder => folder.Kind is MailFolderKind.Inbox)
            ?? accountState.Folders.FirstOrDefault();
        accountState.SelectedFolderKey = replacement?.Key;

        if (ActiveAccount?.Id == account.Id)
        {
            ApplyFolders(accountState);
            if (replacement is not null)
            {
                SetSelectedFolderWithoutSwitch(replacement);
            }
        }
    }

    private void ToggleMessageSelection(MailMessageSummary? message)
    {
        if (!CanToggleMessageSelection(message) || message is null)
        {
            return;
        }

        message.IsSelected = !message.IsSelected;
        RaiseMailboxStateChanged();
    }

    private bool CanToggleMessageSelection(MailMessageSummary? message) =>
        message is not null
        && CanUseMailboxActionsForMessage(message)
        && CanUseMailboxActions
        && IsMessageListVisible;

    private void ToggleSelectAllLoaded()
    {
        bool select = !AreAllLoadedMessagesSelected;
        foreach (MailMessageSummary message in Messages.Where(CanUseMailboxActionsForMessage))
        {
            message.IsSelected = select;
        }

        RaiseMailboxStateChanged();
    }

    private bool CanSelectAllLoaded() =>
        CanUseMailboxActions
        && IsMessageListVisible
        && Messages.Any(CanUseMailboxActionsForMessage);

    private void ClearSelection()
    {
        foreach (MailMessageSummary message in Messages.Where(message => message.IsSelected))
        {
            message.IsSelected = false;
        }

        RaiseMailboxStateChanged();
    }

    private void ClearSelectionsForAccount(Guid accountId)
    {
        foreach ((FolderStateKey key, FolderState state) in _folderStates)
        {
            if (key.AccountId != accountId)
            {
                continue;
            }

            foreach (MailMessageSummary message in state.Messages)
            {
                message.IsSelected = false;
            }
        }

        if (_searchAccountId == accountId && _searchState is FolderState searchState)
        {
            foreach (MailMessageSummary message in searchState.Messages)
            {
                message.IsSelected = false;
            }
        }

        foreach (MailMessageSummary message in Messages)
        {
            message.IsSelected = false;
        }

        RaiseMailboxStateChanged();
    }

    private IReadOnlyList<MailMessageSummary> GetSelectedMessages() =>
        Messages.Where(message => message.IsSelected).ToArray();

    private async Task ToggleStarAsync(MailMessageSummary? message)
    {
        if (!CanToggleStar(message) || message is null)
        {
            return;
        }

        bool newValue = !message.IsStarred;
        await ExecuteMailboxMutationAsync(
            [message.MessageKey],
            "Не удалось изменить пометку.",
            (service, account, keys, token) => service.SetStarredAsync(account, keys, newValue, token),
            keys => ApplyStarState(keys, newValue));
    }

    private async Task ToggleSelectedStarAsync()
    {
        if (!IsGmailMailboxAvailable || !CanMutateSelection())
        {
            return;
        }

        IReadOnlyList<MailMessageSummary> selected = GetSelectedMessages();
        bool newValue = selected.Any(message => !message.IsStarred);
        await ExecuteMailboxMutationAsync(
            selected.Select(message => message.MessageKey).ToArray(),
            "Не удалось изменить пометку выбранных писем.",
            (service, account, keys, token) => service.SetStarredAsync(account, keys, newValue, token),
            keys => ApplyStarState(keys, newValue));
    }

    private Task ArchiveSelectedAsync() => ArchiveAsync(
        GetSelectedMessages()
            .Where(message => IsManagedImapMailbox || message.ProviderLabelIds.Contains(GmailSystemFolders.Inbox))
            .Select(message => message.MessageKey)
            .ToArray());

    private Task ArchiveDetailAsync() => ArchiveAsync(
        SelectedMessageSummary is null ? [] : [SelectedMessageSummary.MessageKey]);

    private Task ArchiveAsync(IReadOnlyCollection<string> messageKeys) =>
        ExecuteMailboxMutationAsync(
            messageKeys,
            "Не удалось архивировать письмо.",
            (service, account, keys, token) => service.ArchiveAsync(account, keys, token),
            ApplyArchive,
            imapAction: MailMailboxAction.Archive);

    private Task ReportSelectedSpamAsync() => ReportSpamAsync(
        GetSelectedMessages().Select(message => message.MessageKey).ToArray());

    private Task ReportDetailSpamAsync() => ReportSpamAsync(
        SelectedMessageSummary is null ? [] : [SelectedMessageSummary.MessageKey]);

    private Task ReportSpamAsync(IReadOnlyCollection<string> messageKeys) =>
        ExecuteMailboxMutationAsync(
            messageKeys,
            "Не удалось переместить письмо в спам.",
            (service, account, keys, token) => service.ReportSpamAsync(account, keys, token),
            ApplyReportSpam,
            clearSelectionAfterSuccess: true,
            refreshCurrentFolderAfterSuccess: true,
            imapAction: MailMailboxAction.Spam);

    private Task DeleteSelectedAsync() => DeleteAsync(
        GetSelectedMessages().Select(message => message.MessageKey).ToArray());

    private Task DeleteDetailAsync() => DeleteAsync(
        SelectedMessageSummary is null ? [] : [SelectedMessageSummary.MessageKey]);

    private Task DeleteAsync(IReadOnlyCollection<string> messageKeys) =>
        ExecuteMailboxMutationAsync(
            messageKeys,
            "Не удалось удалить письмо.",
            (service, account, keys, token) => service.MoveToTrashAsync(account, keys, token),
            ApplyTrash,
            imapAction: MailMailboxAction.Trash);

    private Task RestoreSelectedAsync() => RestoreFromTrashAsync(
        GetSelectedMessages().Select(message => message.MessageKey).ToArray());

    private Task RestoreDetailAsync() => RestoreFromTrashAsync(
        SelectedMessageSummary is null ? [] : [SelectedMessageSummary.MessageKey]);

    private Task RestoreFromTrashAsync(IReadOnlyCollection<string> messageKeys) =>
        ExecuteMailboxMutationAsync(
            messageKeys,
            "Не удалось восстановить письмо.",
            (service, account, keys, token) => service.RestoreFromTrashAsync(account, keys, token),
            ApplyRestoreFromTrash,
            clearSelectionAfterSuccess: true,
            refreshCurrentFolderAfterSuccess: true,
            imapAction: MailMailboxAction.Restore);

    private Task MarkSelectedNotSpamAsync() => MarkNotSpamAsync(
        GetSelectedMessages().Select(message => message.MessageKey).ToArray());

    private Task MarkDetailNotSpamAsync() => MarkNotSpamAsync(
        SelectedMessageSummary is null ? [] : [SelectedMessageSummary.MessageKey]);

    private Task MarkNotSpamAsync(IReadOnlyCollection<string> messageKeys) =>
        ExecuteMailboxMutationAsync(
            messageKeys,
            "Не удалось убрать письмо из спама.",
            (service, account, keys, token) => service.MarkNotSpamAsync(account, keys, token),
            ApplyNotSpam,
            clearSelectionAfterSuccess: true,
            refreshCurrentFolderAfterSuccess: true,
            imapAction: MailMailboxAction.NotSpam);

    private Task SetSelectedReadStateAsync(bool isRead) =>
        ExecuteMailboxMutationAsync(
            GetSelectedMessages().Select(message => message.MessageKey).ToArray(),
            "Не удалось изменить статус прочтения.",
            (service, account, keys, token) => service.SetReadStateAsync(account, keys, isRead, token),
            keys => ApplyReadState(keys, isRead),
            imapAction: isRead ? MailMailboxAction.Read : MailMailboxAction.Unread);

    private Task ToggleYandexSelectedReadStateAsync() =>
        SetSelectedReadStateAsync(YandexSelectedReadStateWillMarkRead);

    private Task ToggleYandexDetailReadStateAsync() => SetReadStateAsync();

    private async Task OpenLabelsAsync(bool targetsDetail)
    {
        if (ActiveAccount is not MailAccount account || _gmailMailboxService is null)
        {
            return;
        }

        string[] keys = targetsDetail
            ? SelectedMessageSummary is null ? [] : [SelectedMessageSummary.MessageKey]
            : GetSelectedMessages().Select(message => message.MessageKey).ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        MailboxActionErrorMessage = null;
        CancelMutationOperation();
        CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        _mutationCancellation = operationCancellation;
        IsMailboxChanging = true;
        Guid accountId = account.Id;
        long version = _viewVersion;
        CancellationToken cancellationToken = operationCancellation.Token;
        try
        {
            GmailUserLabelResult result = await _gmailMailboxService.GetUserLabelsAsync(
                account,
                forceRefresh: true,
                cancellationToken);
            if (!IsCurrentAccount(accountId, version, cancellationToken))
            {
                return;
            }

            if (!result.IsSuccess)
            {
                HandleMailboxFailure(account, result.FailureKind, "Не удалось загрузить ярлыки.");
                return;
            }

            string? previousFolderKey = SelectedFolder?.Key;
            SynchronizeUserLabelFolders(account, result.Labels);
            if (!string.Equals(previousFolderKey, SelectedFolder?.Key, StringComparison.Ordinal))
            {
                MailboxActionErrorMessage = "Ярлык больше недоступен.";
                CurrentFolderLoadTask = RefreshAsync();
                return;
            }

            _labelMenuTargetsDetail = targetsDetail;
            _labelMenuMessageKeys.Clear();
            _labelMenuMessageKeys.UnionWith(keys);
            PopulateUserLabels(result.Labels, keys);
            IsLabelMenuOpen = true;
            OnPropertyChanged(nameof(LabelMenuTitle));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_mutationCancellation, operationCancellation))
            {
                _mutationCancellation = null;
                operationCancellation.Dispose();
                IsMailboxChanging = false;
            }
        }
    }

    private async Task ToggleUserLabelAsync(GmailUserLabelOption? option)
    {
        if (option is null || ActiveAccount is not MailAccount account)
        {
            return;
        }

        bool apply = option.IsApplied is not true;
        string[] keys = _labelMenuMessageKeys.ToArray();
        await ExecuteMailboxMutationAsync(
            keys,
            "Не удалось изменить ярлык.",
            (service, currentAccount, messageKeys, token) => service.SetUserLabelAsync(
                currentAccount,
                messageKeys,
                option.Id,
                apply,
                token),
            succeeded =>
            {
                ApplyProviderLabelState(succeeded, option.Id, apply);
                InvalidateFolderTotalByProviderLocator(account.Id, option.Id);
                if (apply)
                {
                    MarkFolderStaleByProviderLocator(account.Id, option.Id);
                }
                else
                {
                    RemoveMessagesFromFolderByProviderLocator(account.Id, option.Id, succeeded);
                }
                IReadOnlyList<MailMessageSummary> targets = FindCachedMessages(account.Id, keys);
                int appliedCount = targets.Count(message => message.ProviderLabelIds.Contains(option.Id));
                option.IsApplied = appliedCount == 0
                    ? false
                    : appliedCount == targets.Count ? true : null;
            },
            closeLabelMenu: false);
    }

    private bool CanToggleUserLabel(GmailUserLabelOption? option) =>
        option is not null && CanUseMailboxActions && IsLabelMenuOpen;

    private void PopulateUserLabels(IReadOnlyList<GmailUserLabel> labels, IReadOnlyCollection<string> messageKeys)
    {
        IReadOnlyList<MailMessageSummary> targets = FindCachedMessages(ActiveAccount!.Id, messageKeys);
        UserLabels.Clear();
        foreach (GmailUserLabel label in labels)
        {
            int appliedCount = targets.Count(message => message.ProviderLabelIds.Contains(label.Id));
            UserLabels.Add(new GmailUserLabelOption(
                label.Id,
                label.DisplayName,
                appliedCount == 0 ? false : appliedCount == targets.Count ? true : null));
        }

        OnPropertyChanged(nameof(HasUserLabels));
    }

    private void CloseLabels()
    {
        IsLabelMenuOpen = false;
        _labelMenuMessageKeys.Clear();
        UserLabels.Clear();
        OnPropertyChanged(nameof(HasUserLabels));
    }

    private async Task ExecuteMailboxMutationAsync(
        IReadOnlyCollection<string> messageKeys,
        string failureMessage,
        Func<IGmailMailboxManagementService, MailAccount, IReadOnlyCollection<string>, CancellationToken, Task<GmailMailboxMutationResult>> operation,
        Action<IReadOnlyCollection<string>> applySucceeded,
        bool closeLabelMenu = true,
        bool clearSelectionAfterSuccess = false,
        bool refreshCurrentFolderAfterSuccess = false,
        MailMailboxAction? imapAction = null)
    {
        if (IsManagedImapMailbox && imapAction is MailMailboxAction action)
        {
            await ExecuteImapMailboxMutationAsync(messageKeys, action);
            return;
        }
        if (messageKeys.Count == 0
            || ActiveAccount is not MailAccount account
            || SelectedFolder is not MailFolder folder
            || _gmailMailboxService is null)
        {
            return;
        }

        CancelMutationOperation();
        CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        _mutationCancellation = operationCancellation;
        CancellationToken cancellationToken = operationCancellation.Token;
        Guid accountId = account.Id;
        string folderKey = folder.Key;
        long version = _viewVersion;
        IsMailboxChanging = true;
        MailboxActionErrorMessage = null;
        try
        {
            GmailMailboxMutationResult result = await operation(
                _gmailMailboxService,
                account,
                messageKeys,
                cancellationToken);
            if (!IsCurrent(accountId, folderKey, version, cancellationToken))
            {
                return;
            }

            if (result.SucceededMessageKeys.Count > 0)
            {
                applySucceeded(result.SucceededMessageKeys);
                if (clearSelectionAfterSuccess)
                {
                    ClearSelection();
                }
            }

            if (result.FailedMessages.Count > 0)
            {
                string message = result.IsPartialSuccess
                    ? $"{failureMessage} Часть выбранных писем не изменена."
                    : failureMessage;
                HandleMailboxFailure(account, result.FailureKind, message);
            }

            if (closeLabelMenu)
            {
                CloseLabels();
            }

            ApplyState(GetCurrentListState(accountId, folderKey));
            if (result.SucceededMessageKeys.Count > 0 && refreshCurrentFolderAfterSuccess && !IsSearchActive)
            {
                FolderState currentState = GetState(accountId, folderKey);
                PageRequest refreshRequest = currentState.CurrentPageRequest;
                currentState.PrepareRefresh();
                bool refreshed = await LoadPageAsync(
                    account,
                    folder,
                    currentState,
                    refreshRequest,
                    version,
                    GetActivationToken());
                if (refreshed
                    && currentState.Messages.Count == 0
                    && currentState.TryCreatePreviousPageRequest(out PageRequest previousPage))
                {
                    await LoadPageAsync(
                        account,
                        folder,
                        currentState,
                        previousPage,
                        version,
                        GetActivationToken());
                }
            }
            else if (result.SucceededMessageKeys.Count > 0
                     && IsCurrentSearch(accountId, _activeSearchQuery, version, CancellationToken.None)
                     && _searchState is FolderState searchState)
            {
                searchState.PrepareRefresh();
                await LoadSearchPageAsync(
                    account,
                    searchState,
                    PageRequest.First,
                    version,
                    GetActivationToken());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_mutationCancellation, operationCancellation))
            {
                _mutationCancellation = null;
                operationCancellation.Dispose();
                IsMailboxChanging = false;
            }
        }
    }

    private bool CanApplyImapAction(MailMailboxAction action) =>
        SelectedFolder is MailFolder folder && _mailboxService?.CanApply(folder.Kind, action) == true
        && (action is not MailMailboxAction.Archive || ShowArchiveAction);

    private static MailFolderKind? DestinationFor(MailMailboxAction action) => action switch
    {
        MailMailboxAction.Archive => MailFolderKind.Archive,
        MailMailboxAction.Trash => MailFolderKind.Trash,
        MailMailboxAction.Spam => MailFolderKind.Spam,
        MailMailboxAction.NotSpam or MailMailboxAction.Restore => MailFolderKind.Inbox,
        _ => null
    };

    private async Task ExecuteImapMailboxMutationAsync(
        IReadOnlyCollection<string> messageKeys, MailMailboxAction action)
    {
        if (messageKeys.Count == 0 || !CanUseMailboxActions || !CanApplyImapAction(action)
            || ActiveAccount is not MailAccount account || SelectedFolder is not MailFolder folder
            || _mailboxService is null)
        {
            return;
        }

        CancelReadDwell(resetDetailSession: false);
        CancelMutationOperation();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        CancellationToken cancellationToken = cancellation.Token;
        _mutationCancellation = cancellation;
        long version = _viewVersion;
        bool searchWasActive = IsSearchActive && _searchState is not null;
        FolderState folderState = GetState(account.Id, folder.Key);
        FolderState state = searchWasActive ? _searchState! : folderState;
        Dictionary<string, MailMessageSummary> sourceMessages = state.Messages
            .Where(message => messageKeys.Contains(message.MessageKey, StringComparer.Ordinal))
            .ToDictionary(message => message.MessageKey, StringComparer.Ordinal);
        PageRequest currentPage = state.CurrentPageRequest;
        IsMailboxChanging = true;
        MailboxActionErrorMessage = null;
        try
        {
            MailMailboxMutationResult result = await _mailboxService.ApplyAsync(
                account, folder, messageKeys, action, cancellationToken);
            if (!_accountFolderStates.ContainsKey(account.Id))
            {
                return;
            }

            HashSet<string> succeeded = result.SucceededMessageKeys.ToHashSet(StringComparer.Ordinal);
            bool structural = result.DestinationFolder is not null;
            if (result.RequiresRefresh || succeeded.Count > 0)
            {
                if (result.DestinationFolder is MailFolderKind destination)
                {
                    MarkFolderStale(account.Id, destination);
                    QueueConfirmedDestinationMessages(account.Id, destination, sourceMessages, result);
                }
                if (structural)
                {
                    RemoveMessages(state, succeeded);
                    // An interrupted MOVE may have succeeded without a confirmed
                    // response. Do not retain its old detail across the fresh page.
                    if (state.SelectedMessageKey is string selected && messageKeys.Contains(selected))
                    {
                        state.SelectedMessageKey = null;
                        state.ContentMode = MailInboxPresentationMode.MessageList;
                    }
                    state.MarkStale();
                    if (searchWasActive)
                    {
                        folderState.MarkStale();
                    }
                    foreach (MailMessageSummary message in state.Messages)
                    {
                        message.IsSelected = false;
                    }
                    _messageBodyCache.RemoveWhere(key => key.AccountId == account.Id && succeeded.Contains(key.MessageKey));
                }
                else
                {
                    for (int index = 0; index < state.Messages.Count; index++)
                    {
                        MailMessageSummary message = state.Messages[index];
                        if (!succeeded.Contains(message.MessageKey))
                        {
                            continue;
                        }
                        state.Messages[index] = action switch
                        {
                            MailMailboxAction.Read => message with { IsUnread = false },
                            MailMailboxAction.Unread => message with { IsUnread = true },
                            _ => message
                        };
                        if (state.PendingVisibleMessages.ContainsKey(message.MessageKey))
                        {
                            state.PendingVisibleMessages[message.MessageKey] = state.Messages[index];
                        }
                        MessageBodyCacheKey cacheKey = new(account.Id, message.MessageKey);
                        if (action is MailMailboxAction.Read or MailMailboxAction.Unread
                            && _messageBodyCache.TryGet(cacheKey, out MailMessageContent? content) && content is not null)
                        {
                            _messageBodyCache.Set(cacheKey, content with { IsUnread = action is MailMailboxAction.Unread });
                        }
                    }
                    state.HasLoaded = false;
                    if (searchWasActive)
                    {
                        folderState.MarkStale();
                    }
                }
            }

            if (!IsCurrent(account.Id, folder.Key, version, cancellationToken))
            {
                return;
            }
            ApplyState(state);
            if (result.RequiresRefresh || succeeded.Count > 0)
            {
                // Structural changes create a fresh UID snapshot. Flag changes may
                // retain the confirmed snapshot, but the visible search is always
                // reconciled against the server after the mutation.
                if (searchWasActive)
                {
                    await LoadSearchPageAsync(
                        account,
                        state,
                        structural ? PageRequest.First : currentPage,
                        version,
                        GetActivationToken());
                }
                else
                {
                    await LoadPageAsync(
                        account,
                        folder,
                        state,
                        structural ? PageRequest.First : currentPage,
                        version,
                        GetActivationToken());
                }
                if (result.DestinationFolder is MailFolderKind.Inbox
                    && IsCurrent(account.Id, folder.Key, version, cancellationToken))
                {
                    _ = RefreshInboxUnreadCountAfterMutationAsync(
                        account,
                        version,
                        GetActivationToken());
                }
            }
            if (IsCurrent(account.Id, folder.Key, version, cancellationToken))
            {
                MailboxActionErrorMessage = result.UserMessage;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            state.MarkStale();
            if (_accountFolderStates.ContainsKey(account.Id)
                && DestinationFor(action) is MailFolderKind destination)
            {
                MarkFolderStale(account.Id, destination);
            }
        }
        catch (Exception)
        {
            state.MarkStale();
            if (_accountFolderStates.ContainsKey(account.Id)
                && DestinationFor(action) is MailFolderKind destination)
            {
                MarkFolderStale(account.Id, destination);
            }
            if (IsCurrent(account.Id, folder.Key, version, CancellationToken.None))
            {
                MailboxActionErrorMessage =
                    "Не удалось подтвердить изменение. Исходная и целевая папки будут обновлены без повторной отправки команды.";
                ApplyState(state);
                if (searchWasActive)
                {
                    await LoadSearchPageAsync(
                        account,
                        state,
                        PageRequest.First,
                        version,
                        GetActivationToken());
                }
                else
                {
                    await LoadPageAsync(
                        account,
                        folder,
                        state,
                        PageRequest.First,
                        version,
                        GetActivationToken());
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_mutationCancellation, cancellation))
            {
                _mutationCancellation = null;
                IsMailboxChanging = false;
            }
        }
    }

    private void ApplyStarState(IReadOnlyCollection<string> messageKeys, bool isStarred)
    {
        ApplyProviderLabelState(messageKeys, GmailSystemFolders.Starred, isStarred, summary => summary with
        {
            IsStarred = isStarred
        });
        if (!isStarred && ActiveAccount is MailAccount account)
        {
            RemoveMessagesFromFolder(account.Id, MailFolderKind.Starred, messageKeys);
        }
        else if (isStarred && ActiveAccount is MailAccount active)
        {
            MarkFolderStale(active.Id, MailFolderKind.Starred);
        }
    }

    private void ApplyArchive(IReadOnlyCollection<string> messageKeys)
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        AdjustInboxUnreadForRemoval(account, messageKeys);
        ApplyProviderLabelState(messageKeys, GmailSystemFolders.Inbox, isApplied: false);
        RemoveMessagesFromFolder(account.Id, MailFolderKind.Inbox, messageKeys);
    }

    private void ApplyReportSpam(IReadOnlyCollection<string> messageKeys)
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        AdjustInboxUnreadForRemoval(account, messageKeys);
        ApplyProviderLabelState(messageKeys, GmailSystemFolders.Inbox, isApplied: false);
        ApplyProviderLabelState(messageKeys, GmailSystemFolders.Spam, isApplied: true);
        RemoveMessagesFromFolder(account.Id, MailFolderKind.Inbox, messageKeys);
        MarkFolderStale(account.Id, MailFolderKind.Spam);
        MarkFolderStale(account.Id, MailFolderKind.AllMail);
    }

    private void ApplyTrash(IReadOnlyCollection<string> messageKeys)
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        AdjustInboxUnreadForRemoval(account, messageKeys);
        HashSet<string> keys = messageKeys.ToHashSet(StringComparer.Ordinal);
        foreach ((FolderStateKey stateKey, FolderState state) in _folderStates)
        {
            if (stateKey.AccountId == account.Id && stateKey.FolderKey != MailFolderCatalog.TrashKey)
            {
                RemoveMessages(state, keys);
            }
        }

        if (_searchAccountId == account.Id && _searchState is FolderState searchState)
        {
            RemoveMessages(searchState, keys);
        }

        MarkFolderStale(account.Id, MailFolderKind.Trash);
    }

    private void ApplyRestoreFromTrash(IReadOnlyCollection<string> messageKeys)
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        ApplyProviderLabelState(messageKeys, GmailSystemFolders.Trash, isApplied: false);
        RemoveMessagesFromFolder(account.Id, MailFolderKind.Trash, messageKeys);
        MarkFolderStale(account.Id, MailFolderKind.AllMail);
    }

    private void ApplyNotSpam(IReadOnlyCollection<string> messageKeys)
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        AdjustInboxUnreadForAddition(account, messageKeys);
        ApplyProviderLabelState(messageKeys, GmailSystemFolders.Spam, isApplied: false);
        ApplyProviderLabelState(messageKeys, GmailSystemFolders.Inbox, isApplied: true);
        RemoveMessagesFromFolder(account.Id, MailFolderKind.Spam, messageKeys);
        MarkFolderStale(account.Id, MailFolderKind.Inbox);
        RequireFreshInbox(account.Id);
    }

    private void AdjustInboxUnreadForAddition(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys)
    {
        if (account.InboxUnreadCount is not int unreadCount)
        {
            return;
        }

        int addedUnread = FindCachedMessages(account.Id, messageKeys)
            .Count(message => message.IsUnread
                && !message.ProviderLabelIds.Contains(GmailSystemFolders.Inbox));
        account.InboxUnreadCount = Math.Max(0, unreadCount + addedUnread);
    }

    private void AdjustInboxUnreadForRemoval(
        MailAccount account,
        IReadOnlyCollection<string> messageKeys)
    {
        if (account.InboxUnreadCount is not int unreadCount)
        {
            return;
        }

        int removedUnread = FindCachedMessages(account.Id, messageKeys)
            .Count(message => message.IsUnread
                && message.ProviderLabelIds.Contains(GmailSystemFolders.Inbox));
        account.InboxUnreadCount = Math.Max(0, unreadCount - removedUnread);
    }

    private void ApplyReadState(IReadOnlyCollection<string> messageKeys, bool isRead)
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        IReadOnlyList<MailMessageSummary> before = FindCachedMessages(account.Id, messageKeys);
        int unreadDelta = before
            .Where(message => message.ProviderLabelIds.Contains(GmailSystemFolders.Inbox)
                && message.IsUnread != !isRead)
            .Sum(_ => isRead ? -1 : 1);
        if (account.InboxUnreadCount is int unreadCount)
        {
            account.InboxUnreadCount = Math.Max(0, unreadCount + unreadDelta);
        }

        ApplyProviderLabelState(
            messageKeys,
            GmailSystemFolders.Unread,
            !isRead,
            summary => summary with { IsUnread = !isRead });
        if (SelectedMessageContent is MailMessageContent content
            && messageKeys.Contains(content.MessageKey, StringComparer.Ordinal))
        {
            MailMessageContent updated = content with { IsUnread = !isRead };
            _messageBodyCache.Set(new MessageBodyCacheKey(account.Id, content.MessageKey), updated);
            _isReadStateMetadataUpdate = true;
            try
            {
                SelectedMessageContent = updated;
            }
            finally
            {
                _isReadStateMetadataUpdate = false;
            }
        }
    }

    private void ApplyProviderLabelState(
        IReadOnlyCollection<string> messageKeys,
        string labelId,
        bool isApplied,
        Func<MailMessageSummary, MailMessageSummary>? additionalUpdate = null)
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        HashSet<string> keys = messageKeys.ToHashSet(StringComparer.Ordinal);
        foreach ((FolderStateKey stateKey, FolderState state) in _folderStates)
        {
            if (stateKey.AccountId != account.Id)
            {
                continue;
            }

            for (int index = 0; index < state.Messages.Count; index++)
            {
                MailMessageSummary summary = state.Messages[index];
                if (!keys.Contains(summary.MessageKey))
                {
                    continue;
                }

                HashSet<string> labels = summary.ProviderLabelIds.ToHashSet(StringComparer.Ordinal);
                if (isApplied)
                {
                    labels.Add(labelId);
                }
                else
                {
                    labels.Remove(labelId);
                }

                MailMessageSummary updated = summary with { ProviderLabelIds = labels };
                state.Messages[index] = additionalUpdate?.Invoke(updated) ?? updated;
            }
        }

        if (_searchAccountId == account.Id && _searchState is FolderState searchState)
        {
            ApplyProviderLabelState(searchState, keys, labelId, isApplied, additionalUpdate);
        }
    }

    private void RemoveMessagesFromFolder(
        Guid accountId,
        MailFolderKind folderKind,
        IReadOnlyCollection<string> messageKeys)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        MailFolder? folder = account.Folders.FirstOrDefault(item => item.Kind == folderKind);
        if (folder is null)
        {
            return;
        }

        RemoveMessages(GetState(accountId, folder.Key), messageKeys.ToHashSet(StringComparer.Ordinal));
    }

    private static void RemoveMessages(FolderState state, IReadOnlySet<string> messageKeys)
    {
        foreach (string messageKey in messageKeys)
        {
            state.PendingVisibleMessages.Remove(messageKey);
        }

        int previousCount = state.Messages.Count;
        state.Messages.RemoveAll(message => messageKeys.Contains(message.MessageKey));
        int removedCount = previousCount - state.Messages.Count;
        if (state.TotalCount is long totalCount)
        {
            state.TotalCount = removedCount == messageKeys.Count
                ? Math.Max(0, totalCount - removedCount)
                : null;
        }

        if (state.SelectedMessageKey is string selected && messageKeys.Contains(selected))
        {
            state.SelectedMessageKey = null;
            state.ContentMode = MailInboxPresentationMode.MessageList;
        }
    }

    private void InvalidateFolderTotalByProviderLocator(Guid accountId, string providerLocator)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        foreach (MailFolder folder in account.Folders.Where(folder =>
                     string.Equals(folder.ProviderLocator, providerLocator, StringComparison.Ordinal)))
        {
            FolderState state = GetState(accountId, folder.Key);
            state.TotalCount = null;
            if (ReferenceEquals(_displayedListState, state))
            {
                RaisePaginationStateChanged();
            }
        }
    }

    private void MarkFolderStaleByProviderLocator(Guid accountId, string providerLocator)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        foreach (MailFolder folder in account.Folders.Where(folder =>
                     string.Equals(folder.ProviderLocator, providerLocator, StringComparison.Ordinal)))
        {
            GetState(accountId, folder.Key).MarkStale();
        }
    }

    private void MarkUserLabelFoldersStale(Guid accountId)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        foreach (MailFolder folder in account.Folders.Where(folder => folder.IsUserLabel))
        {
            GetState(accountId, folder.Key).MarkStale();
        }
    }

    private void RemoveMessagesFromFolderByProviderLocator(
        Guid accountId,
        string providerLocator,
        IReadOnlyCollection<string> messageKeys)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        HashSet<string> keys = messageKeys.ToHashSet(StringComparer.Ordinal);
        foreach (MailFolder folder in account.Folders.Where(folder =>
                     string.Equals(folder.ProviderLocator, providerLocator, StringComparison.Ordinal)))
        {
            RemoveMessages(GetState(accountId, folder.Key), keys);
        }
    }

    private void MarkFolderStale(Guid accountId, MailFolderKind kind)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        MailFolder? folder = account.Folders.FirstOrDefault(item => item.Kind == kind);
        if (folder is not null)
        {
            GetState(accountId, folder.Key).MarkStale();
        }
    }

    private void QueueConfirmedDestinationMessages(
        Guid accountId,
        MailFolderKind destination,
        IReadOnlyDictionary<string, MailMessageSummary> sourceMessages,
        MailMailboxMutationResult result)
    {
        if (result.ItemResults is null
            || !_accountFolderStates.TryGetValue(accountId, out AccountFolderState? accountState)
            || accountState.Folders.FirstOrDefault(
                folder => folder.Kind == destination) is not MailFolder destinationFolder)
        {
            return;
        }

        FolderState destinationState = GetState(accountId, destinationFolder.Key);
        foreach (MailMailboxMutationItemResult item in result.ItemResults)
        {
            if (item.Status is not (MailMailboxMutationItemStatus.Succeeded
                    or MailMailboxMutationItemStatus.Ambiguous)
                || string.IsNullOrWhiteSpace(item.DestinationMessageKey)
                || !sourceMessages.TryGetValue(item.SourceMessageKey, out MailMessageSummary? source))
            {
                continue;
            }

            MailMessageSummary moved = source with { MessageKey = item.DestinationMessageKey };
            moved.IsSelected = false;
            destinationState.PendingVisibleMessages[item.DestinationMessageKey] = moved;
        }
    }

    private async Task RefreshInboxUnreadCountAfterMutationAsync(
        MailAccount account,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            await RefreshInboxUnreadCountAsync(account, version, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private IReadOnlyList<MailMessageSummary> FindCachedMessages(
        Guid accountId,
        IReadOnlyCollection<string> messageKeys)
    {
        HashSet<string> keys = messageKeys.ToHashSet(StringComparer.Ordinal);
        IEnumerable<MailMessageSummary> current = ActiveAccount?.Id == accountId
            ? Messages.Where(message => keys.Contains(message.MessageKey))
            : [];
        return _folderStates
            .Where(item => item.Key.AccountId == accountId)
            .SelectMany(item => item.Value.Messages)
            .Where(message => keys.Contains(message.MessageKey))
            .Concat(_searchAccountId == accountId && _searchState is FolderState searchState
                ? searchState.Messages.Where(message => keys.Contains(message.MessageKey))
                : [])
            .Concat(current)
            .GroupBy(message => message.MessageKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static void ApplyProviderLabelState(
        FolderState state,
        IReadOnlySet<string> messageKeys,
        string labelId,
        bool isApplied,
        Func<MailMessageSummary, MailMessageSummary>? additionalUpdate)
    {
        for (int index = 0; index < state.Messages.Count; index++)
        {
            MailMessageSummary summary = state.Messages[index];
            if (!messageKeys.Contains(summary.MessageKey))
            {
                continue;
            }

            HashSet<string> labels = summary.ProviderLabelIds.ToHashSet(StringComparer.Ordinal);
            if (isApplied)
            {
                labels.Add(labelId);
            }
            else
            {
                labels.Remove(labelId);
            }

            MailMessageSummary updated = summary with { ProviderLabelIds = labels };
            state.Messages[index] = additionalUpdate?.Invoke(updated) ?? updated;
        }
    }

    private void HandleMailboxFailure(
        MailAccount account,
        GmailMailboxFailureKind? failureKind,
        string message)
    {
        MailboxActionErrorMessage = failureKind is GmailMailboxFailureKind.NotAuthorized
            ? "Чтобы управлять письмами, нужно снова разрешить доступ Google."
            : message;
        if (failureKind is GmailMailboxFailureKind.NotAuthorized)
        {
            _requiresMailboxAuthorization = true;
            AuthorizationMessage = MailboxActionErrorMessage;
        }
        if (failureKind is GmailMailboxFailureKind.ReauthorizationRequired)
        {
            MarkGmailReauthenticationRequired(account, MailReadFailureKind.ReauthorizationRequired);
        }
    }

    private void OpenMessage(MailMessageSummary? summary)
    {
        if (summary is null || ActiveAccount is null || SelectedFolder is null)
        {
            return;
        }

        if (ActiveAccount is { Provider: MailProviderType.Gmail } account
            && summary.ProviderDraftId is string draftId)
        {
            CurrentMessageLoadTask = OpenGmailDraftAsync(account, draftId);
            return;
        }

        if (ActiveAccount is MailAccount managedImapAccount
            && MailProviderFeaturePolicies.Get(managedImapAccount.Provider).IsManagedImap
            && SelectedFolder.Kind is MailFolderKind.Drafts)
        {
            CurrentMessageLoadTask = OpenManagedImapDraftAsync(managedImapAccount, summary.MessageKey);
            return;
        }

        if (!ReferenceEquals(SelectedMessageSummary, summary))
        {
            SelectedMessageSummary = summary;
        }
        else
        {
            SetContentMode(MailInboxPresentationMode.MessageDetail);
        }
    }

    private bool CanOpenMessage(MailMessageSummary? summary) =>
        IsActive && summary is not null && !IsComposeOpen;

    private void ShowMessageList() => SetContentMode(MailInboxPresentationMode.MessageList);

    private bool CanShowMessageList() => IsMessageDetailVisible;

    private void SetContentMode(
        MailInboxPresentationMode mode,
        FolderState? state = null)
    {
        if (mode is MailInboxPresentationMode.Compose)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Compose mode is derived from the compose view model.");
        }

        FolderState? currentState = state;
        if (currentState is null && ActiveAccount is MailAccount account && SelectedFolder is MailFolder folder)
        {
            currentState = GetCurrentListState(account.Id, folder.Key);
        }

        if (currentState is not null)
        {
            currentState.ContentMode = mode;
        }

        MailInboxPresentationMode previousMode = _contentMode;
        if (previousMode == mode)
        {
            RaisePresentationStateChanged();
            return;
        }

        if (previousMode is MailInboxPresentationMode.MessageDetail
            || mode is MailInboxPresentationMode.MessageDetail)
        {
            CancelReadDwell(resetDetailSession: true);
        }

        _contentMode = mode;
        RaisePresentationStateChanged();
    }

    private void RaisePresentationStateChanged()
    {
        OnPropertyChanged(nameof(PresentationMode));
        OnPropertyChanged(nameof(IsMessageListVisible));
        OnPropertyChanged(nameof(IsMessageDetailVisible));
        OnPropertyChanged(nameof(ShouldDisplayHtmlRenderer));
        OnPropertyChanged(nameof(ShowPrintAction));
        OnPropertyChanged(nameof(CanPrintMessage));
        OpenMessageCommand.NotifyCanExecuteChanged();
        BackToMessageListCommand.NotifyCanExecuteChanged();
        NotifyMailboxCommandStates();
        UpdateReadDwellState();
    }

    private void OnMailSent(object? sender, MailSentEventArgs eventArgs)
    {
        if (!eventArgs.SentCopySaved)
        {
            return;
        }

        if (!_accountFolderStates.TryGetValue(eventArgs.AccountId, out AccountFolderState? accountState))
        {
            return;
        }

        MailFolder? sentFolder = accountState.Folders.FirstOrDefault(folder => folder.Kind is MailFolderKind.Sent);
        if (sentFolder is not null)
        {
            GetState(eventArgs.AccountId, sentFolder.Key).MarkStale();
        }
        MarkFolderStale(eventArgs.AccountId, MailFolderKind.AllMail);
    }

    private void OnGmailDraftChanged(object? sender, GmailDraftChangedEventArgs eventArgs)
    {
        MarkFolderStale(eventArgs.AccountId, MailFolderKind.Drafts);
        MarkFolderStale(eventArgs.AccountId, MailFolderKind.AllMail);
    }

    private void OnManagedImapDraftChanged(object? sender, ManagedImapDraftChangedEventArgs eventArgs)
    {
        MarkFolderStale(eventArgs.AccountId, MailFolderKind.Drafts);
    }

    private AccountFolderState GetAccountFolderState(Guid accountId)
    {
        if (!_accountFolderStates.TryGetValue(accountId, out AccountFolderState? state))
        {
            state = new AccountFolderState();
            _accountFolderStates.Add(accountId, state);
        }

        return state;
    }

    private bool IsCurrentAccount(Guid accountId, long version, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ActiveAccount?.Id == accountId
        && _viewVersion == version;

    private bool IsCurrent(Guid accountId, string folderKey, long version, CancellationToken cancellationToken) =>
        IsCurrentAccount(accountId, version, cancellationToken)
        && SelectedFolder?.Key == folderKey;

    private bool IsCurrentSearch(
        Guid accountId,
        string? query,
        long version,
        CancellationToken cancellationToken) =>
        IsCurrentAccount(accountId, version, cancellationToken)
        && IsSearchActive
        && string.Equals(_activeSearchQuery, query, StringComparison.Ordinal);

    private CancellationToken GetActivationToken() => _activationCancellation?.Token ?? CancellationToken.None;
    private bool CanRefresh() => IsActive && SelectedFolder is not null && !IsListLoading
        && !(IsManagedImapMailbox && IsMailboxChanging);
    private bool CanGoToPreviousPage() =>
        IsActive && CanNavigateToPreviousPage && !RequiresGmailReauthentication && !IsListLoading
        && !(IsManagedImapMailbox && IsMailboxChanging);
    private bool CanGoToNextPage() =>
        IsActive && CanNavigateToNextPage && !RequiresGmailReauthentication && !IsListLoading
        && !(IsManagedImapMailbox && IsMailboxChanging);
    private bool CanRetry() => IsActive && HasListError && !RequiresGmailReauthentication && !IsListLoading;
    private bool CanSearch() =>
        ActiveAccount is { IsEnabled: true } account
        && SupportsServerSearch(account)
        && !IsComposeOpen
        && (IsSearchActive || !string.IsNullOrWhiteSpace(SearchText));

    private static bool SupportsServerSearch(MailAccount account) =>
        account.Provider is MailProviderType.Gmail || IsManagedImap(account);

    private static bool IsManagedImap(MailAccount account) =>
        MailProviderFeaturePolicies.Get(account.Provider).IsManagedImap;
    private bool CanClearSearchCommand() => CanClearSearch;
    private bool CanReauthenticateGmail() =>
        IsActive
        && RequiresGmailReauthentication
        && !IsGmailReauthenticating
        && _providerFactory.GmailReauthenticationService is not null;
    private bool CanRetryMessage() => IsActive && ShowMessageRetryAction && !IsMessageLoading;
    private bool CanSetReadState() => CanChangeReadState;
    private bool CanToggleYandexSelectedReadState() =>
        IsYandexMailbox && CanChangeSelectedReadState();
    private bool CanToggleYandexDetailReadState() =>
        IsYandexMailbox && CanSetReadState();
    private bool CanAuthorizeGmail() => RequiresGmailAuthorization && !IsReadStateChanging && _providerFactory.GmailScopeUpgradeService is not null;
    private bool CanSaveAttachment(MailAttachmentInfo? attachment) =>
        _attachmentSaveService is not null
        && attachment is { IsDownloadable: true }
        && ActiveAccount is not null
        && SelectedMessageContent is not null
        && !IsAttachmentSaving;

    private bool CanToggleStar(MailMessageSummary? message) =>
        message is not null
        && CanUseMailboxActionsForMessage(message)
        && CanUseMailboxActions
        && IsGmailMailboxAvailable
        && (IsMessageDetailVisible
            ? CanMutateDetail() && ReferenceEquals(message, SelectedMessageSummary)
            : IsMessageListVisible && Messages.Any(current => ReferenceEquals(current, message)));

    private bool CanMutateSelection() =>
        CanUseMailboxActions && IsMessageListVisible && HasSelectedMessages;

    private bool CanMutateDetail() =>
        CanUseMailboxActions
        && IsMessageDetailVisible
        && !IsMessageLoading
        && !HasMessageError
        && SelectedMessageSummary is MailMessageSummary summary
        && CanUseMailboxActionsForMessage(summary)
        && SelectedMessageContent is MailMessageContent content
        && string.Equals(summary.MessageKey, content.MessageKey, StringComparison.Ordinal);

    private bool CanArchiveSelection() =>
        CanMutateSelection()
        && (IsManagedImapMailbox ? CanApplyImapAction(MailMailboxAction.Archive)
            : GetSelectedMessages().Any(message => message.ProviderLabelIds.Contains(GmailSystemFolders.Inbox)));

    private bool CanArchiveDetail() =>
        CanMutateDetail()
        && (IsManagedImapMailbox ? CanApplyImapAction(MailMailboxAction.Archive)
            : SelectedMessageSummary!.ProviderLabelIds.Contains(GmailSystemFolders.Inbox));

    private bool CanReportSelectionSpam() =>
        CanMutateSelection() && ShowReportSpamAction;

    private bool CanReportDetailSpam() =>
        CanMutateDetail() && ShowReportSpamAction;

    private bool CanDeleteSelection() => CanMutateSelection() && CanDeleteCurrentFolder;

    private bool CanDeleteDetail() => CanMutateDetail() && CanDeleteCurrentFolder;

    private bool CanRestoreSelection() =>
        CanMutateSelection() && ShowRestoreAction;

    private bool CanRestoreDetail() =>
        CanMutateDetail() && ShowRestoreAction;

    private bool CanMarkSelectionNotSpam() =>
        CanMutateSelection() && ShowNotSpamAction;

    private bool CanMarkDetailNotSpam() =>
        CanMutateDetail() && ShowNotSpamAction;

    private bool CanChangeSelectedReadState() =>
        CanMutateSelection() && (IsSearchActive || SelectedFolder?.SupportsReadState == true);

    private static bool CanUseMailboxActionsForMessage(MailMessageSummary message) =>
        string.IsNullOrWhiteSpace(message.ProviderDraftId);

    private async Task SaveAttachmentAsync(MailAttachmentInfo? attachment)
    {
        if (!CanSaveAttachment(attachment)
            || attachment is null
            || ActiveAccount is not MailAccount account
            || SelectedMessageContent is not MailMessageContent content
            || _attachmentSaveService is null)
        {
            return;
        }

        CancelAttachmentOperation();
        CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        _attachmentCancellation = operationCancellation;
        CancellationToken cancellationToken = operationCancellation.Token;
        Guid accountId = account.Id;
        string messageKey = content.MessageKey;
        IsAttachmentSaving = true;
        AttachmentStatusMessage = null;
        try
        {
            MailAttachmentSaveResult result = await _attachmentSaveService.SaveAsync(
                account,
                messageKey,
                attachment,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || ActiveAccount?.Id != accountId
                || !string.Equals(SelectedMessageContent?.MessageKey, messageKey, StringComparison.Ordinal))
            {
                return;
            }

            AttachmentStatusMessage = result.Outcome is MailAttachmentSaveOutcome.Canceled
                ? null
                : result.UserMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Selection/account changes cancel the stale save without surfacing an error to the user.
        }
        finally
        {
            if (ReferenceEquals(_attachmentCancellation, operationCancellation))
            {
                operationCancellation.Dispose();
                _attachmentCancellation = null;
            }

            IsAttachmentSaving = false;
        }
    }

    private void RaiseListStateChanged()
    {
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasListError));
        OnPropertyChanged(nameof(HasBlockingListError));
        OnPropertyChanged(nameof(RequiresGmailReauthentication));
        OnPropertyChanged(nameof(ShowTransientRetryAction));
        RaisePaginationStateChanged();
        OnPropertyChanged(nameof(ListErrorDescription));
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyListMessage));
        RefreshCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        ClearSearchCommand.NotifyCanExecuteChanged();
        ReauthenticateGmailCommand.NotifyCanExecuteChanged();
        OpenMessageCommand.NotifyCanExecuteChanged();
    }

    private void RaisePaginationStateChanged()
    {
        OnPropertyChanged(nameof(IsPageNavigationVisible));
        OnPropertyChanged(nameof(CanNavigateToPreviousPage));
        OnPropertyChanged(nameof(CanNavigateToNextPage));
        OnPropertyChanged(nameof(PageRangeText));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private void RaiseReadStateChanged()
    {
        OnPropertyChanged(nameof(ShowReadStateAction));
        OnPropertyChanged(nameof(ShowStandardReadStateAction));
        OnPropertyChanged(nameof(ShowYandexDetailReadStateAction));
        OnPropertyChanged(nameof(YandexDetailReadStateWillMarkRead));
        OnPropertyChanged(nameof(YandexDetailReadStateActionText));
        OnPropertyChanged(nameof(RequiresGmailAuthorization));
        OnPropertyChanged(nameof(CanChangeReadState));
        OnPropertyChanged(nameof(ReadStateActionText));
        OnPropertyChanged(nameof(ReadStateAuthorizationText));
        SetReadStateCommand.NotifyCanExecuteChanged();
        ToggleYandexDetailReadStateCommand.NotifyCanExecuteChanged();
        AuthorizeGmailCommand.NotifyCanExecuteChanged();
        OpenMessageCommand.NotifyCanExecuteChanged();
        BackToMessageListCommand.NotifyCanExecuteChanged();
        SaveAttachmentCommand.NotifyCanExecuteChanged();
        UpdateReadDwellState();
    }

    private async Task RefreshInboxUnreadCountAsync(
        MailAccount account,
        long version,
        CancellationToken cancellationToken)
    {
        IMailReadProvider provider = _providerFactory.Get(account.Provider);
        int? count = await TryGetInboxUnreadCountAsync(provider, account, cancellationToken);
        if (count is int exactUnreadCount && IsCurrentAccount(account.Id, version, cancellationToken))
        {
            account.InboxUnreadCount = exactUnreadCount;
        }
    }

    private static async Task<int?> TryGetInboxUnreadCountAsync(
        IMailReadProvider provider,
        MailAccount account,
        CancellationToken cancellationToken)
    {
        if (provider is not IMailInboxUnreadCountProvider unreadCountProvider)
        {
            return null;
        }

        try
        {
            int count = await unreadCountProvider.GetInboxUnreadCountAsync(account, cancellationToken);
            return Math.Max(0, count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailReadException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        ClearSearchCommand.NotifyCanExecuteChanged();
        ReauthenticateGmailCommand.NotifyCanExecuteChanged();
        RetryMessageCommand.NotifyCanExecuteChanged();
        SetReadStateCommand.NotifyCanExecuteChanged();
        AuthorizeGmailCommand.NotifyCanExecuteChanged();
        OpenMessageCommand.NotifyCanExecuteChanged();
        BackToMessageListCommand.NotifyCanExecuteChanged();
        SaveAttachmentCommand.NotifyCanExecuteChanged();
        NotifyMailboxCommandStates();
    }

    private void RaiseMailboxStateChanged()
    {
        OnPropertyChanged(nameof(SelectedMessageCount));
        OnPropertyChanged(nameof(HasSelectedMessages));
        OnPropertyChanged(nameof(AreAllLoadedMessagesSelected));
        OnPropertyChanged(nameof(AreAllSelectedMessagesStarred));
        OnPropertyChanged(nameof(LoadedSelectionState));
        OnPropertyChanged(nameof(SelectedStarActionText));
        OnPropertyChanged(nameof(DetailStarActionText));
        OnPropertyChanged(nameof(LabelMenuTitle));
        OnPropertyChanged(nameof(ShowYandexSelectedReadStateAction));
        OnPropertyChanged(nameof(YandexSelectedReadStateWillMarkRead));
        OnPropertyChanged(nameof(YandexSelectedReadStateActionText));
        NotifyMailboxCommandStates();
    }

    private void NotifyMailboxCommandStates()
    {
        OnPropertyChanged(nameof(ShowArchiveAction));
        ToggleMessageSelectionCommand.NotifyCanExecuteChanged();
        SelectAllLoadedCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        ToggleStarCommand.NotifyCanExecuteChanged();
        ToggleSelectedStarCommand.NotifyCanExecuteChanged();
        ArchiveSelectedCommand.NotifyCanExecuteChanged();
        ArchiveDetailCommand.NotifyCanExecuteChanged();
        ReportSelectedSpamCommand.NotifyCanExecuteChanged();
        ReportDetailSpamCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DeleteDetailCommand.NotifyCanExecuteChanged();
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        RestoreDetailCommand.NotifyCanExecuteChanged();
        MarkSelectedNotSpamCommand.NotifyCanExecuteChanged();
        MarkDetailNotSpamCommand.NotifyCanExecuteChanged();
        MarkSelectedReadCommand.NotifyCanExecuteChanged();
        MarkSelectedUnreadCommand.NotifyCanExecuteChanged();
        ToggleYandexSelectedReadStateCommand.NotifyCanExecuteChanged();
        OpenLabelsForSelectionCommand.NotifyCanExecuteChanged();
        OpenLabelsForDetailCommand.NotifyCanExecuteChanged();
        ToggleUserLabelCommand.NotifyCanExecuteChanged();
    }

    private bool TryGetCurrentRemoteImageConsentKey(out RemoteImageConsentKey key)
    {
        if (ActiveAccount is MailAccount account && SelectedMessageContent is MailMessageContent content)
        {
            key = new RemoteImageConsentKey(account.Id, content.MessageKey);
            return true;
        }

        key = default;
        return false;
    }

    private void BeginRemoteImageSenderTrustLookup()
    {
        CancelRemoteImageSenderTrustLookup();
        bool canTrust = TryGetCurrentRemoteImageSenderTrustTarget(out RemoteImageSenderTrustTarget target);
        SetCurrentRemoteImageSenderTrustState(canTrust, isTrusted: false);
        if (!canTrust || _disposed)
        {
            CurrentRemoteImageSenderTrustTask = Task.CompletedTask;
            return;
        }

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        _remoteImageSenderTrustCancellation = cancellation;
        CurrentRemoteImageSenderTrustTask = ResolveRemoteImageSenderTrustAsync(target, cancellation);
    }

    private async Task ResolveRemoteImageSenderTrustAsync(
        RemoteImageSenderTrustTarget target,
        CancellationTokenSource cancellation)
    {
        try
        {
            bool trusted = await _remoteImageSenderTrustStore.IsTrustedAsync(
                target.AccountId,
                target.NormalizedAddress,
                cancellation.Token);
            if (!cancellation.IsCancellationRequested && IsCurrentRemoteImageSenderTrustTarget(target))
            {
                SetCurrentRemoteImageSenderTrustState(canTrust: true, isTrusted: trusted);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A different account or message owns the current sender-trust state.
        }
        catch (Exception exception) when (
            exception is System.IO.IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
        {
            if (!cancellation.IsCancellationRequested && IsCurrentRemoteImageSenderTrustTarget(target))
            {
                SetCurrentRemoteImageSenderTrustState(canTrust: true, isTrusted: false);
            }
        }
        finally
        {
            if (ReferenceEquals(_remoteImageSenderTrustCancellation, cancellation))
            {
                _remoteImageSenderTrustCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private bool TryGetCurrentRemoteImageSenderTrustTarget(out RemoteImageSenderTrustTarget target)
    {
        if (ActiveAccount is MailAccount account
            && SelectedMessageContent is MailMessageContent
            {
                HasRemoteImages: true,
                HasUnambiguousFromAddress: true
            } content
            && RemoteImageSenderIdentity.TryNormalize(content.FromAddress, out string normalizedAddress))
        {
            target = new RemoteImageSenderTrustTarget(account.Id, content.MessageKey, normalizedAddress);
            return true;
        }

        target = default;
        return false;
    }

    private bool IsCurrentRemoteImageSenderTrustTarget(RemoteImageSenderTrustTarget target) =>
        ActiveAccount?.Id == target.AccountId
        && string.Equals(SelectedMessageContent?.MessageKey, target.MessageKey, StringComparison.Ordinal)
        && SelectedMessageContent?.HasUnambiguousFromAddress == true
        && RemoteImageSenderIdentity.TryNormalize(SelectedMessageContent.FromAddress, out string normalizedAddress)
        && string.Equals(normalizedAddress, target.NormalizedAddress, StringComparison.Ordinal);

    private void SetCurrentRemoteImageSenderTrustState(bool canTrust, bool isTrusted)
    {
        bool changed = _canTrustCurrentRemoteImageSender != canTrust
            || _isCurrentRemoteImageSenderTrusted != isTrusted;
        _canTrustCurrentRemoteImageSender = canTrust;
        _isCurrentRemoteImageSenderTrusted = isTrusted;
        if (changed)
        {
            OnPropertyChanged(nameof(CanTrustCurrentRemoteImageSender));
            OnPropertyChanged(nameof(IsCurrentRemoteImageSenderTrusted));
        }

        RaiseRemoteImageConsentStateChanged();
    }

    private void CancelRemoteImageSenderTrustLookup()
    {
        _remoteImageSenderTrustCancellation?.Cancel();
        _remoteImageSenderTrustCancellation?.Dispose();
        _remoteImageSenderTrustCancellation = null;
    }

    private void RaiseRemoteImageConsentStateChanged()
    {
        OnPropertyChanged(nameof(AreRemoteImagesShown));
        OnPropertyChanged(nameof(ShowRemoteImagesBanner));
        OnPropertyChanged(nameof(CanShowRemoteImages));
        OnPropertyChanged(nameof(ShowOneTimeRemoteImagesAction));
        OnPropertyChanged(nameof(ShowAlwaysRemoteImagesFromSenderAction));
        OnPropertyChanged(nameof(ShowRevokeRemoteImagesFromSenderAction));
        OnPropertyChanged(nameof(CanAlwaysShowRemoteImagesFromSender));
        OnPropertyChanged(nameof(RemoteImagesBannerText));
    }

    private void UpdateReadDwellState()
    {
        if (!CanStartReadDwell(out MailReadDwellTarget target))
        {
            if (_readDwellCancellation is not null
                && _readDwellTarget == _automaticReadMutationTarget)
            {
                return;
            }

            if (_readDwellCancellation is not null)
            {
                CancelReadDwell(resetDetailSession: false);
            }

            return;
        }

        if (_readDwellCancellation is not null && _readDwellTarget == target)
        {
            return;
        }

        CancelReadDwell(resetDetailSession: false);
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        _readDwellCancellation = cancellation;
        _readDwellTarget = target;
        _currentReadDwellTask = RunReadDwellAsync(target, cancellation);
    }

    private bool CanStartReadDwell(out MailReadDwellTarget target)
    {
        if (!TryGetCurrentReadDwellTarget(out target)
            || !_isDetailHostActive
            || _isApplyingState
            || !IsMessageDetailVisible
            || IsComposeOpen
            || SelectedMessageSummary?.IsUnread != true
            || SelectedMessageContent?.IsUnread != true
            || (!IsSearchActive && SelectedFolder?.SupportsReadState != true)
            || !_readStateCapability.CanSetReadState
            || IsReadStateChanging
            || (IsManagedImapMailbox && IsMailboxChanging)
            || _readDwellAttemptedTarget == target
            || _manualUnreadSuppressionTarget == target)
        {
            target = default;
            return false;
        }

        return true;
    }

    private async Task RunReadDwellAsync(
        MailReadDwellTarget target,
        CancellationTokenSource cancellation)
    {
        CancellationToken cancellationToken = cancellation.Token;
        try
        {
            await _readDwellScheduler.DelayAsync(MailReadDwellDelay, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentReadDwellTarget(target)
                || !CanStartReadDwell(out MailReadDwellTarget currentTarget)
                || currentTarget != target)
            {
                return;
            }

            _readDwellAttemptedTarget = target;
            _automaticReadMutationTarget = target;
            try
            {
                await ChangeReadStateAsync(
                    isRead: true,
                    ReadStateMutationOrigin.AutomaticDwell,
                    target,
                    cancellationToken);
            }
            finally
            {
                _automaticReadMutationTarget = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_readDwellCancellation, cancellation))
            {
                _readDwellCancellation = null;
                _readDwellTarget = null;
                cancellation.Dispose();
            }
        }
    }

    private bool TryGetCurrentReadDwellTarget(out MailReadDwellTarget target)
    {
        if (ActiveAccount is MailAccount account
            && SelectedFolder is MailFolder folder
            && SelectedMessageSummary is MailMessageSummary summary
            && SelectedMessageContent is MailMessageContent content
            && string.Equals(summary.MessageKey, content.MessageKey, StringComparison.Ordinal))
        {
            target = new MailReadDwellTarget(
                account.Id,
                folder.Key,
                summary.MessageKey,
                _viewVersion);
            return true;
        }

        target = default;
        return false;
    }

    private bool IsCurrentReadDwellTarget(MailReadDwellTarget target) =>
        TryGetCurrentReadDwellTarget(out MailReadDwellTarget current)
        && current == target
        && _isDetailHostActive
        && IsMessageDetailVisible
        && !IsComposeOpen;

    private void CancelReadDwell(bool resetDetailSession)
    {
        CancellationTokenSource? cancellation = _readDwellCancellation;
        _readDwellCancellation = null;
        _readDwellTarget = null;
        _automaticReadMutationTarget = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (resetDetailSession)
        {
            _readDwellAttemptedTarget = null;
            _manualUnreadSuppressionTarget = null;
        }
    }

    private void CancelActivation()
    {
        CancelReadDwell(resetDetailSession: true);
        CancelListOperation();
        CancelMessageOperation();
        CancelMutationOperation();
        CancelAttachmentOperation();
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationCancellation = null;
    }

    private void CancelListOperation()
    {
        CancellationTokenSource? operation = _listCancellation;
        _listCancellation = null;
        operation?.Cancel();
        operation?.Dispose();
        if (operation is not null && IsListLoading)
        {
            IsListLoading = false;
        }
    }

    private void CancelMessageOperation()
    {
        _messageCancellation?.Cancel();
        _messageCancellation?.Dispose();
        _messageCancellation = null;
    }

    private void CancelMutationOperation()
    {
        _mutationCancellation?.Cancel();
        _mutationCancellation?.Dispose();
        _mutationCancellation = null;
        if (IsMailboxChanging)
        {
            IsMailboxChanging = false;
        }
    }

    private async Task OpenGmailDraftAsync(MailAccount account, string draftId)
    {
        bool opened = await Compose.OpenGmailDraftAsync(account, draftId);
        if (!opened && ActiveAccount?.Id == account.Id)
        {
            MessageFailureKind = MailReadFailureKind.MessageUnavailable;
            MessageErrorMessage = Compose.ErrorMessage ?? "Не удалось открыть черновик Gmail.";
        }
    }

    private async Task OpenManagedImapDraftAsync(MailAccount account, string messageKey)
    {
        bool opened = await Compose.OpenManagedImapDraftAsync(account, messageKey);
        if (!opened && ActiveAccount?.Id == account.Id)
        {
            MessageFailureKind = MailReadFailureKind.MessageUnavailable;
            MessageErrorMessage = Compose.ErrorMessage ?? "Не удалось открыть черновик Яндекс Почты.";
        }
    }

    private void CancelAttachmentOperation()
    {
        _attachmentCancellation?.Cancel();
        _attachmentCancellation?.Dispose();
        _attachmentCancellation = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private enum ReadStateMutationOrigin
    {
        Manual,
        AutomaticDwell
    }

    private readonly record struct MailReadDwellTarget(
        Guid AccountId,
        string FolderKey,
        string MessageKey,
        long ViewVersion)
    {
        public bool Matches(Guid accountId, string folderKey, string messageKey, long viewVersion) =>
            AccountId == accountId
            && string.Equals(FolderKey, folderKey, StringComparison.Ordinal)
            && string.Equals(MessageKey, messageKey, StringComparison.Ordinal)
            && ViewVersion == viewVersion;
    }

    private sealed class AccountFolderState
    {
        public List<MailFolder> Folders { get; } = [];
        public string? SelectedFolderKey { get; set; }
        public bool HasLoaded { get; set; }
    }

    private sealed class InboxFreshnessState
    {
        public long ChangeGeneration { get; private set; }
        public long RefreshedGeneration { get; private set; }
        public bool AutoRefreshRequested { get; set; }
        public bool IsAutoRefreshRunning { get; set; }
        public Task CurrentRefreshTask { get; set; } = Task.CompletedTask;
        public bool IsStale => RefreshedGeneration < ChangeGeneration;

        public void MarkChanged() => ChangeGeneration++;

        public void MarkRefreshedThrough(long generation) =>
            RefreshedGeneration = Math.Max(RefreshedGeneration, generation);
    }

    private sealed class FolderState
    {
        private readonly List<string?> _pageTokens = [null];

        public List<MailMessageSummary> Messages { get; } = [];
        public Dictionary<string, MailMessageSummary> PendingVisibleMessages { get; } = new(StringComparer.Ordinal);
        public string? ContinuationToken { get; set; }
        public long? TotalCount { get; set; }
        public int PageIndex { get; private set; }
        public string? SelectedMessageKey { get; set; }
        public bool HasLoaded { get; set; }
        public string? ListErrorMessage { get; set; }
        public MailReadFailureKind? FailureKind { get; set; }
        public MailInboxPresentationMode ContentMode { get; set; } = MailInboxPresentationMode.MessageList;
        public PageRequest CurrentPageRequest => new(PageIndex, _pageTokens[PageIndex]);

        public bool TryCreateNextPageRequest(out PageRequest request)
        {
            if (string.IsNullOrWhiteSpace(ContinuationToken))
            {
                request = default;
                return false;
            }

            request = new PageRequest(PageIndex + 1, ContinuationToken);
            return true;
        }

        public bool TryCreatePreviousPageRequest(out PageRequest request)
        {
            if (PageIndex <= 0)
            {
                request = default;
                return false;
            }

            int previousIndex = PageIndex - 1;
            request = new PageRequest(previousIndex, _pageTokens[previousIndex]);
            return true;
        }

        public void ApplyPage(PageRequest request, string? nextPageToken, long? totalCount)
        {
            if (request.PageIndex == 0)
            {
                _pageTokens.Clear();
                _pageTokens.Add(null);
            }
            else
            {
                while (_pageTokens.Count <= request.PageIndex)
                {
                    _pageTokens.Add(null);
                }

                _pageTokens[request.PageIndex] = request.Token;
                if (_pageTokens.Count > request.PageIndex + 1)
                {
                    _pageTokens.RemoveRange(
                        request.PageIndex + 1,
                        _pageTokens.Count - request.PageIndex - 1);
                }
            }

            PageIndex = request.PageIndex;
            ContinuationToken = nextPageToken;
            TotalCount = totalCount;
        }

        public void Reset()
        {
            Messages.Clear();
            PendingVisibleMessages.Clear();
            ResetPagination();
            SelectedMessageKey = null;
            HasLoaded = false;
            ListErrorMessage = null;
            FailureKind = null;
            ContentMode = MailInboxPresentationMode.MessageList;
        }

        public void PrepareRefresh()
        {
            ListErrorMessage = null;
            FailureKind = null;
        }

        public void MarkStale()
        {
            HasLoaded = false;
            ResetPagination();
            ListErrorMessage = null;
            FailureKind = null;
        }

        private void ResetPagination()
        {
            _pageTokens.Clear();
            _pageTokens.Add(null);
            PageIndex = 0;
            ContinuationToken = null;
            TotalCount = null;
        }
    }

    private readonly record struct PageRequest(int PageIndex, string? Token)
    {
        public static PageRequest First => new(0, null);
    }

    private readonly record struct FolderStateKey(Guid AccountId, string FolderKey);
    private readonly record struct MessageBodyCacheKey(Guid AccountId, string MessageKey);
    private readonly record struct RemoteImageConsentKey(Guid AccountId, string MessageKey);
    private readonly record struct RemoteImageSenderTrustTarget(
        Guid AccountId,
        string MessageKey,
        string NormalizedAddress);
}

public sealed class GmailUserLabelOption(
    string id,
    string displayName,
    bool? isApplied) : ObservableObject
{
    private bool? _isApplied = isApplied;

    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;

    public bool? IsApplied
    {
        get => _isApplied;
        set => SetProperty(ref _isApplied, value);
    }
}

internal interface IMailReadDwellScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemMailReadDwellScheduler : IMailReadDwellScheduler
{
    public static SystemMailReadDwellScheduler Instance { get; } = new();

    private SystemMailReadDwellScheduler()
    {
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
