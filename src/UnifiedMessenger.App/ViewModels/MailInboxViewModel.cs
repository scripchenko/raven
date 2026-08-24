using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.App.ViewModels;

public sealed class MailInboxViewModel : ObservableObject, IDisposable
{
    public const int InitialPageSize = 30;
    public const int MessageBodyCacheCapacity = 20;
    public const int RemoteImageConsentCacheCapacity = 20;

    private readonly IMailReadProviderFactory _providerFactory;
    private readonly IMailAttachmentSaveService? _attachmentSaveService;
    private readonly MailMessageSourceCache? _messageSourceCache;
    private readonly Dictionary<FolderStateKey, FolderState> _folderStates = [];
    private readonly Dictionary<Guid, AccountFolderState> _accountFolderStates = [];
    private readonly BoundedLruCache<MessageBodyCacheKey, MailMessageContent> _messageBodyCache =
        new(MessageBodyCacheCapacity);
    private readonly BoundedLruCache<RemoteImageConsentKey, bool> _remoteImageConsents =
        new(RemoteImageConsentCacheCapacity);
    private CancellationTokenSource? _activationCancellation;
    private CancellationTokenSource? _listCancellation;
    private CancellationTokenSource? _messageCancellation;
    private CancellationTokenSource? _mutationCancellation;
    private CancellationTokenSource? _attachmentCancellation;
    private MailAccount? _activeAccount;
    private MailFolder? _selectedFolder;
    private MailMessageSummary? _selectedMessageSummary;
    private MailMessageContent? _selectedMessageContent;
    private bool _isListLoading;
    private bool _isMessageLoading;
    private bool _isRemoteImageLoading;
    private bool _isReadStateChanging;
    private bool _hasLoaded;
    private string? _listErrorMessage;
    private string? _messageErrorMessage;
    private string? _readStateErrorMessage;
    private string? _authorizationMessage;
    private string? _attachmentStatusMessage;
    private bool _isAttachmentSaving;
    private MailReadFailureKind? _failureKind;
    private MailReadStateCapability _readStateCapability = MailReadStateCapability.Unsupported;
    private string? _continuationToken;
    private long _viewVersion;
    private bool _isApplyingState;
    private bool _isReadStateMetadataUpdate;
    private bool _disposed;

    public MailInboxViewModel(
        IMailReadProviderFactory providerFactory,
        MailComposeViewModel? composeViewModel = null,
        IMailAttachmentSaveService? attachmentSaveService = null,
        MailMessageSourceCache? messageSourceCache = null)
    {
        _providerFactory = providerFactory;
        _attachmentSaveService = attachmentSaveService;
        _messageSourceCache = messageSourceCache;
        Compose = composeViewModel ?? MailComposeViewModel.CreateUnavailable();
        Compose.PropertyChanged += OnComposePropertyChanged;
        Compose.Sent += OnMailSent;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, CanLoadMore);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        RetryMessageCommand = new AsyncRelayCommand(RetryMessageAsync, CanRetryMessage);
        SetReadStateCommand = new AsyncRelayCommand(SetReadStateAsync, CanSetReadState);
        AuthorizeGmailCommand = new AsyncRelayCommand(AuthorizeGmailAsync, CanAuthorizeGmail);
        SaveAttachmentCommand = new AsyncRelayCommand<MailAttachmentInfo>(SaveAttachmentAsync, CanSaveAttachment);
    }

    public ObservableCollection<MailFolder> Folders { get; } = [];
    public ObservableCollection<MailMessageSummary> Messages { get; } = [];
    public MailComposeViewModel Compose { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand RetryMessageCommand { get; }
    public IAsyncRelayCommand SetReadStateCommand { get; }
    public IAsyncRelayCommand AuthorizeGmailCommand { get; }
    public IAsyncRelayCommand<MailAttachmentInfo> SaveAttachmentCommand { get; }

    internal Task CurrentMessageLoadTask { get; private set; } = Task.CompletedTask;
    internal Task CurrentFolderLoadTask { get; private set; } = Task.CompletedTask;
    internal int CachedMessageBodyCount => _messageBodyCache.Count;
    internal int CachedRemoteImageConsentCount => _remoteImageConsents.Count;
    internal bool IsReadStateMetadataUpdate => _isReadStateMetadataUpdate;

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
                RaiseRemoteImageConsentStateChanged();
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
            if (!SetProperty(ref _selectedFolder, value) || _isApplyingState || value is null || ActiveAccount is null)
            {
                return;
            }

            AccountFolderState catalog = GetAccountFolderState(ActiveAccount.Id);
            catalog.SelectedFolderKey = value.Key;
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

            OnPropertyChanged(nameof(HasSelectedMessage));
            RaiseReadStateChanged();
            if (_isApplyingState || ActiveAccount is null || SelectedFolder is null)
            {
                return;
            }

            FolderState state = GetState(ActiveAccount.Id, SelectedFolder.Key);
            state.SelectedMessageKey = value?.MessageKey;
            CancelAttachmentOperation();
            AttachmentStatusMessage = null;
            CancelMessageOperation();
            IsMessageLoading = false;
            if (value is null)
            {
                SelectedMessageContent = null;
                MessageErrorMessage = null;
                return;
            }

            MessageBodyCacheKey cacheKey = new(ActiveAccount.Id, value.MessageKey);
            if (_messageBodyCache.TryGet(cacheKey, out MailMessageContent? cachedContent))
            {
                SelectedMessageContent = cachedContent;
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
                IsRemoteImageLoading = false;
                OnPropertyChanged(nameof(HasSelectedContent));
                OnPropertyChanged(nameof(HasAttachments));
                OnPropertyChanged(nameof(IsSelectedMessagePlainText));
                OnPropertyChanged(nameof(IsSelectedMessageHtml));
                RaiseRemoteImageConsentStateChanged();
                RaiseReadStateChanged();
                SaveAttachmentCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool AreRemoteImagesShown =>
        TryGetCurrentRemoteImageConsentKey(out RemoteImageConsentKey key)
        && _remoteImageConsents.TryGet(key, out _);

    public bool IsRemoteImageLoading
    {
        get => _isRemoteImageLoading;
        private set
        {
            if (SetProperty(ref _isRemoteImageLoading, value))
            {
                OnPropertyChanged(nameof(CanShowRemoteImages));
                OnPropertyChanged(nameof(RemoteImagesButtonText));
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
                RetryMessageCommand.NotifyCanExecuteChanged();
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
                OnPropertyChanged(nameof(ShowMessagePlaceholder));
                RetryMessageCommand.NotifyCanExecuteChanged();
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

    public MailReadFailureKind? FailureKind
    {
        get => _failureKind;
        private set
        {
            if (SetProperty(ref _failureKind, value))
            {
                OnPropertyChanged(nameof(ErrorTitle));
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
                LoadMoreCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsActive => ActiveAccount is { IsEnabled: true };
    public bool IsComposeOpen => Compose.IsOpen;
    public bool HasFolders => Folders.Count > 0;
    public bool HasMessages => Messages.Count > 0;
    public bool HasMore => !string.IsNullOrWhiteSpace(ContinuationToken);
    public bool HasListError => !string.IsNullOrWhiteSpace(ListErrorMessage);
    public bool HasBlockingListError => HasListError && !HasMessages;
    public bool HasMessageError => !string.IsNullOrWhiteSpace(MessageErrorMessage);
    public bool HasReadStateError => !string.IsNullOrWhiteSpace(ReadStateErrorMessage);
    public bool IsInitialLoading => IsListLoading && !HasMessages;
    public bool IsEmpty => HasLoaded && !IsListLoading && !HasMessages && !HasListError;
    public bool HasSelectedMessage => SelectedMessageSummary is not null;
    public bool HasSelectedContent => SelectedMessageContent is not null;
    public bool HasAttachments => SelectedMessageContent?.Attachments.Count > 0;
    public bool IsSelectedMessagePlainText => SelectedMessageContent?.BodyKind is MailMessageBodyKind.PlainText;
    public bool IsSelectedMessageHtml => SelectedMessageContent?.BodyKind is MailMessageBodyKind.SanitizedHtml;
    public bool ShowRemoteImagesBanner => SelectedMessageContent?.HasRemoteImages == true && !AreRemoteImagesShown;
    public bool CanShowRemoteImages => ShowRemoteImagesBanner && !IsRemoteImageLoading;
    public string RemoteImagesButtonText => IsRemoteImageLoading ? "Загружаем…" : "Показать";
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
    public bool ShowReadStateAction =>
        HasSelectedContent
        && SelectedFolder?.SupportsReadState == true
        && _readStateCapability.CanSetReadState;
    public bool RequiresGmailAuthorization =>
        ActiveAccount?.Provider is MailProviderType.Gmail
        && _readStateCapability.RequiresAuthorization;
    public bool CanChangeReadState =>
        _readStateCapability.CanSetReadState
        && HasSelectedContent
        && !IsReadStateChanging;
    public string ReadStateActionText => IsReadStateChanging
        ? "Сохраняем…"
        : SelectedMessageSummary?.IsUnread == true
            ? "Отметить прочитанным"
            : "Отметить непрочитанным";
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
        MailReadFailureKind.ReauthorizationRequired => "Требуется повторный вход в Google",
        MailReadFailureKind.AuthenticationFailed or MailReadFailureKind.CredentialMissing => "Не удалось войти в почту",
        MailReadFailureKind.FolderUnavailable => "Папка недоступна",
        _ => "Не удалось загрузить почту"
    };

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
        long version = ++_viewVersion;
        _activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ActiveAccount = account;
        Compose.ActivateAccount(account);
        IsListLoading = false;
        IsMessageLoading = false;
        ReadStateErrorMessage = null;
        AuthorizationMessage = null;

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
        if (!state.HasLoaded)
        {
            await LoadPageAsync(account, folder, state, true, version, _activationCancellation.Token);
        }
        else
        {
            await RefreshReadStateCapabilityAsync();
        }
    }

    public void RemoveAccount(Guid accountId)
    {
        CancelActivation();
        _accountFolderStates.Remove(accountId);
        foreach (FolderStateKey key in _folderStates.Keys.Where(key => key.AccountId == accountId).ToArray())
        {
            _folderStates.Remove(key);
        }

        _remoteImageConsents.RemoveWhere(key => key.AccountId == accountId);
        _messageBodyCache.RemoveWhere(key => key.AccountId == accountId);
        _messageSourceCache?.RemoveAccount(accountId);
        Compose.RemoveAccount(accountId);
        if (ActiveAccount?.Id == accountId)
        {
            ActiveAccount = null;
            ClearDisplayedState(clearFolders: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelActivation();
        Compose.PropertyChanged -= OnComposePropertyChanged;
        Compose.Sent -= OnMailSent;
        Compose.Dispose();
        _folderStates.Clear();
        _accountFolderStates.Clear();
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
            await LoadPageAsync(account, selected, state, true, version, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MailReadException exception) when (IsCurrentAccount(account.Id, version, cancellationToken))
        {
            HasLoaded = true;
            ListErrorMessage = exception.UserMessage;
            FailureKind = exception.FailureKind;
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
        CancelListOperation();
        CancelMessageOperation();
        CancelMutationOperation();
        ReadStateErrorMessage = null;
        AuthorizationMessage = null;
        FolderState state = GetState(account.Id, folder.Key);
        ApplyState(state);
        if (!state.HasLoaded)
        {
            await LoadPageAsync(account, folder, state, true, version, GetActivationToken());
        }
        else
        {
            await RefreshReadStateCapabilityAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account || SelectedFolder is not MailFolder folder)
        {
            return;
        }

        FolderState state = GetState(account.Id, folder.Key);
        state.PrepareRefresh();
        ContinuationToken = null;
        ListErrorMessage = null;
        FailureKind = null;
        await LoadPageAsync(account, folder, state, true, _viewVersion, GetActivationToken());
    }

    private async Task LoadMoreAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account || SelectedFolder is not MailFolder folder)
        {
            return;
        }

        FolderState state = GetState(account.Id, folder.Key);
        if (!string.IsNullOrWhiteSpace(state.ContinuationToken))
        {
            await LoadPageAsync(account, folder, state, false, _viewVersion, GetActivationToken());
        }
    }

    private async Task RetryAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account)
        {
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
            await LoadPageAsync(account, folder, state, true, _viewVersion, GetActivationToken());
        }
        else
        {
            await LoadPageAsync(account, folder, state, false, _viewVersion, GetActivationToken());
        }
    }

    private async Task RetryMessageAsync()
    {
        if (SelectedMessageSummary is MailMessageSummary summary)
        {
            CurrentMessageLoadTask = LoadSelectedMessageAsync(summary);
            await CurrentMessageLoadTask;
        }
    }

    private async Task LoadPageAsync(
        MailAccount account,
        MailFolder folder,
        FolderState state,
        bool replace,
        long version,
        CancellationToken activationToken)
    {
        CancelListOperation();
        _listCancellation = CancellationTokenSource.CreateLinkedTokenSource(activationToken);
        CancellationToken cancellationToken = _listCancellation.Token;
        IsListLoading = true;
        ListErrorMessage = null;
        FailureKind = null;
        try
        {
            IMailReadProvider provider = _providerFactory.Get(account.Provider);
            MailPage<MailMessageSummary> page = await provider.GetPageAsync(
                account,
                folder,
                replace ? null : state.ContinuationToken,
                InitialPageSize,
                cancellationToken);
            if (!IsCurrent(account.Id, folder.Key, version, cancellationToken))
            {
                return;
            }

            if (replace)
            {
                MailMessageSummary? retained = state.SelectedMessageKey is null
                    ? null
                    : state.Messages.FirstOrDefault(message => message.MessageKey == state.SelectedMessageKey);
                state.Messages.Clear();
                HashSet<string> keys = new(StringComparer.Ordinal);
                foreach (MailMessageSummary summary in page.Items)
                {
                    if (keys.Add(summary.MessageKey))
                    {
                        state.Messages.Add(summary);
                    }
                }

                if (retained is not null && keys.Add(retained.MessageKey))
                {
                    state.Messages.Add(retained);
                }
            }
            else
            {
                HashSet<string> keys = state.Messages.Select(message => message.MessageKey).ToHashSet(StringComparer.Ordinal);
                foreach (MailMessageSummary summary in page.Items)
                {
                    if (keys.Add(summary.MessageKey))
                    {
                        state.Messages.Add(summary);
                    }
                }
            }

            state.ContinuationToken = page.ContinuationToken;
            state.HasLoaded = true;
            state.ListErrorMessage = null;
            state.FailureKind = null;
            ApplyState(state);
            if (replace
                && SelectedMessageContent is null
                && SelectedMessageSummary is MailMessageSummary restored
                && CurrentMessageLoadTask.IsCompleted)
            {
                CurrentMessageLoadTask = LoadSelectedMessageAsync(restored);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MailReadException exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = exception.UserMessage;
            state.FailureKind = exception.FailureKind;
            ApplyState(state);
        }
        catch (Exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = "Не удалось загрузить почту. Попробуйте ещё раз.";
            state.FailureKind = MailReadFailureKind.ConnectionFailed;
            ApplyState(state);
        }
        finally
        {
            if (IsCurrent(account.Id, folder.Key, version, cancellationToken))
            {
                IsListLoading = false;
            }
        }
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

            FolderState state = GetState(account.Id, folder.Key);
            state.SelectedMessageKey = summary.MessageKey;
            SelectedMessageContent = content;
            await RefreshReadStateCapabilityAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MailReadException exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
            MessageErrorMessage = exception.UserMessage;
        }
        catch (Exception) when (IsCurrent(account.Id, folder.Key, version, cancellationToken))
        {
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
            || !folder.SupportsReadState
            || _providerFactory.Get(account.Provider) is not IMailMessageStateProvider stateProvider)
        {
            RaiseReadStateChanged();
            return;
        }

        try
        {
            _readStateCapability = await stateProvider.GetReadStateCapabilityAsync(account, folder, GetActivationToken());
        }
        catch (MailReadException exception)
        {
            ReadStateErrorMessage = exception.UserMessage;
        }
        finally
        {
            RaiseReadStateChanged();
        }
    }

    private async Task SetReadStateAsync()
    {
        if (ActiveAccount is not MailAccount account
            || SelectedFolder is not MailFolder folder
            || SelectedMessageSummary is not MailMessageSummary summary
            || _providerFactory.Get(account.Provider) is not IMailMessageStateProvider provider)
        {
            return;
        }

        CancelMutationOperation();
        _mutationCancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        CancellationToken cancellationToken = _mutationCancellation.Token;
        bool isRead = summary.IsUnread;
        IsReadStateChanging = true;
        ReadStateErrorMessage = null;
        try
        {
            await provider.SetReadStateAsync(account, folder, summary.MessageKey, isRead, cancellationToken);
            if (!IsCurrent(account.Id, folder.Key, _viewVersion, cancellationToken)
                || SelectedMessageSummary?.MessageKey != summary.MessageKey)
            {
                return;
            }

            bool isUnread = !isRead;
            MailMessageSummary updatedSummary = summary with { IsUnread = isUnread };
            FolderState state = GetState(account.Id, folder.Key);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (MailReadException exception)
        {
            ReadStateErrorMessage = exception.UserMessage;
        }
        catch (Exception)
        {
            ReadStateErrorMessage = "Не удалось изменить статус письма. Попробуйте ещё раз.";
        }
        finally
        {
            IsReadStateChanging = false;
        }
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
        _isApplyingState = true;
        try
        {
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
            MessageErrorMessage = null;
            ReadStateErrorMessage = null;
            RaiseListStateChanged();
            RaiseReadStateChanged();
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void ClearDisplayedState(bool clearFolders, bool preserveLoading = false)
    {
        _isApplyingState = true;
        try
        {
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
            MessageErrorMessage = null;
            ReadStateErrorMessage = null;
            FailureKind = null;
            _readStateCapability = MailReadStateCapability.Unsupported;
            RaiseListStateChanged();
            RaiseReadStateChanged();
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

    internal bool IsFolderStateStale(Guid accountId, MailFolderKind kind)
    {
        AccountFolderState account = GetAccountFolderState(accountId);
        MailFolder? folder = account.Folders.FirstOrDefault(item => item.Kind == kind);
        return folder is not null && !GetState(accountId, folder.Key).HasLoaded;
    }

    private void OnComposePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MailComposeViewModel.IsOpen)
            or nameof(MailComposeViewModel.IsClosed)
            or nameof(MailComposeViewModel.Draft))
        {
            OnPropertyChanged(nameof(IsComposeOpen));
        }
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

    private CancellationToken GetActivationToken() => _activationCancellation?.Token ?? CancellationToken.None;
    private bool CanRefresh() => IsActive && SelectedFolder is not null && !IsListLoading;
    private bool CanLoadMore() => IsActive && HasMore && !IsListLoading;
    private bool CanRetry() => IsActive && HasListError && !IsListLoading;
    private bool CanRetryMessage() => IsActive && HasMessageError && !IsMessageLoading;
    private bool CanSetReadState() => CanChangeReadState;
    private bool CanAuthorizeGmail() => RequiresGmailAuthorization && !IsReadStateChanging && _providerFactory.GmailScopeUpgradeService is not null;
    private bool CanSaveAttachment(MailAttachmentInfo? attachment) =>
        _attachmentSaveService is not null
        && attachment is { IsDownloadable: true }
        && ActiveAccount is not null
        && SelectedMessageContent is not null
        && !IsAttachmentSaving;

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
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(IsEmpty));
        RefreshCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    private void RaiseReadStateChanged()
    {
        OnPropertyChanged(nameof(ShowReadStateAction));
        OnPropertyChanged(nameof(RequiresGmailAuthorization));
        OnPropertyChanged(nameof(CanChangeReadState));
        OnPropertyChanged(nameof(ReadStateActionText));
        OnPropertyChanged(nameof(ReadStateAuthorizationText));
        SetReadStateCommand.NotifyCanExecuteChanged();
        AuthorizeGmailCommand.NotifyCanExecuteChanged();
        SaveAttachmentCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RetryMessageCommand.NotifyCanExecuteChanged();
        SetReadStateCommand.NotifyCanExecuteChanged();
        AuthorizeGmailCommand.NotifyCanExecuteChanged();
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

    private void RaiseRemoteImageConsentStateChanged()
    {
        OnPropertyChanged(nameof(AreRemoteImagesShown));
        OnPropertyChanged(nameof(ShowRemoteImagesBanner));
        OnPropertyChanged(nameof(CanShowRemoteImages));
    }

    private void CancelActivation()
    {
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
        _listCancellation?.Cancel();
        _listCancellation?.Dispose();
        _listCancellation = null;
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
    }

    private void CancelAttachmentOperation()
    {
        _attachmentCancellation?.Cancel();
        _attachmentCancellation?.Dispose();
        _attachmentCancellation = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class AccountFolderState
    {
        public List<MailFolder> Folders { get; } = [];
        public string? SelectedFolderKey { get; set; }
        public bool HasLoaded { get; set; }
    }

    private sealed class FolderState
    {
        public List<MailMessageSummary> Messages { get; } = [];
        public string? ContinuationToken { get; set; }
        public string? SelectedMessageKey { get; set; }
        public bool HasLoaded { get; set; }
        public string? ListErrorMessage { get; set; }
        public MailReadFailureKind? FailureKind { get; set; }

        public void Reset()
        {
            Messages.Clear();
            ContinuationToken = null;
            SelectedMessageKey = null;
            HasLoaded = false;
            ListErrorMessage = null;
            FailureKind = null;
        }

        public void PrepareRefresh()
        {
            ContinuationToken = null;
            ListErrorMessage = null;
            FailureKind = null;
        }

        public void MarkStale()
        {
            HasLoaded = false;
            ContinuationToken = null;
            ListErrorMessage = null;
            FailureKind = null;
        }
    }

    private readonly record struct FolderStateKey(Guid AccountId, string FolderKey);
    private readonly record struct MessageBodyCacheKey(Guid AccountId, string MessageKey);
    private readonly record struct RemoteImageConsentKey(Guid AccountId, string MessageKey);
}
