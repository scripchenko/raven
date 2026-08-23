using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.App.ViewModels;

public sealed class MailInboxViewModel : ObservableObject, IDisposable
{
    public const int InitialPageSize = 30;

    private readonly IMailReadProviderFactory _providerFactory;
    private readonly Dictionary<Guid, AccountInboxState> _accountStates = [];
    private readonly HashSet<RemoteImageConsentKey> _remoteImageConsents = [];
    private CancellationTokenSource? _activationCancellation;
    private CancellationTokenSource? _listCancellation;
    private CancellationTokenSource? _messageCancellation;
    private MailAccount? _activeAccount;
    private MailMessageSummary? _selectedMessageSummary;
    private MailMessageContent? _selectedMessageContent;
    private bool _isListLoading;
    private bool _isMessageLoading;
    private bool _isRemoteImageLoading;
    private bool _hasLoaded;
    private string? _listErrorMessage;
    private string? _messageErrorMessage;
    private MailReadFailureKind? _failureKind;
    private string? _continuationToken;
    private long _activationVersion;
    private bool _isApplyingState;
    private bool _disposed;

    public MailInboxViewModel(IMailReadProviderFactory providerFactory)
    {
        _providerFactory = providerFactory;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, CanLoadMore);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        RetryMessageCommand = new AsyncRelayCommand(RetryMessageAsync, CanRetryMessage);
    }

    public ObservableCollection<MailMessageSummary> Messages { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand RetryMessageCommand { get; }

    internal Task CurrentMessageLoadTask { get; private set; } = Task.CompletedTask;

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
                NotifyCommandStates();
            }
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
            if (_isApplyingState || ActiveAccount is null)
            {
                return;
            }

            AccountInboxState state = GetState(ActiveAccount.Id);
            state.SelectedMessageKey = value?.MessageKey;
            if (value is null)
            {
                state.SelectedContent = null;
                SelectedMessageContent = null;
                MessageErrorMessage = null;
                return;
            }

            if (state.SelectedContent?.MessageKey == value.MessageKey)
            {
                SelectedMessageContent = state.SelectedContent;
                MessageErrorMessage = null;
                return;
            }

            state.SelectedContent = null;
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
                OnPropertyChanged(nameof(IsSelectedMessagePlainText));
                OnPropertyChanged(nameof(IsSelectedMessageHtml));
                RaiseRemoteImageConsentStateChanged();
            }
        }
    }

    public bool AreRemoteImagesShown =>
        TryGetCurrentRemoteImageConsentKey(out RemoteImageConsentKey key)
        && _remoteImageConsents.Contains(key);

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
    public bool HasMessages => Messages.Count > 0;
    public bool HasMore => !string.IsNullOrWhiteSpace(ContinuationToken);
    public bool HasListError => !string.IsNullOrWhiteSpace(ListErrorMessage);
    public bool HasBlockingListError => HasListError && !HasMessages;
    public bool HasMessageError => !string.IsNullOrWhiteSpace(MessageErrorMessage);
    public bool IsInitialLoading => IsListLoading && !HasMessages;
    public bool IsEmpty => HasLoaded && !IsListLoading && !HasMessages && !HasListError;
    public bool HasSelectedMessage => SelectedMessageSummary is not null;
    public bool HasSelectedContent => SelectedMessageContent is not null;
    public bool IsSelectedMessagePlainText =>
        SelectedMessageContent?.BodyKind is MailMessageBodyKind.PlainText;
    public bool IsSelectedMessageHtml =>
        SelectedMessageContent?.BodyKind is MailMessageBodyKind.SanitizedHtml;
    public bool ShowRemoteImagesBanner =>
        SelectedMessageContent?.HasRemoteImages == true && !AreRemoteImagesShown;
    public bool CanShowRemoteImages => ShowRemoteImagesBanner && !IsRemoteImageLoading;
    public string RemoteImagesButtonText => IsRemoteImageLoading
        ? "Загружаем…"
        : "Показать";
    public bool ShowMessagePlaceholder => !HasSelectedContent && !IsMessageLoading && !HasMessageError;
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
        MailReadFailureKind.AuthenticationFailed or MailReadFailureKind.CredentialMissing =>
            "Не удалось войти в почту",
        _ => "Не удалось загрузить почту"
    };

    public void SetRemoteImageLoading(bool isLoading)
    {
        if (_disposed)
        {
            return;
        }

        IsRemoteImageLoading = isLoading;
    }

    public void MarkRemoteImagesShown()
    {
        if (_disposed)
        {
            return;
        }

        IsRemoteImageLoading = false;
        if (TryGetCurrentRemoteImageConsentKey(out RemoteImageConsentKey key)
            && _remoteImageConsents.Add(key))
        {
            RaiseRemoteImageConsentStateChanged();
        }
    }

    public async Task ActivateAsync(
        MailAccount? account,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CancelActivation();
        long version = ++_activationVersion;
        _activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ActiveAccount = account;
        OnPropertyChanged(nameof(IsActive));
        IsListLoading = false;
        IsMessageLoading = false;

        if (account is null || !account.IsEnabled)
        {
            ClearDisplayedState();
            return;
        }

        AccountInboxState state = GetState(account.Id);
        ApplyState(state);
        if (state.HasLoaded)
        {
            return;
        }

        await LoadPageAsync(account, state, replace: true, version, _activationCancellation.Token);
    }

    public void RemoveAccount(Guid accountId)
    {
        CancelActivation();
        _accountStates.Remove(accountId);
        _remoteImageConsents.RemoveWhere(key => key.AccountId == accountId);
        if (ActiveAccount?.Id == accountId)
        {
            ActiveAccount = null;
            ClearDisplayedState();
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
        _accountStates.Clear();
        _remoteImageConsents.Clear();
    }

    private async Task RefreshAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account)
        {
            return;
        }

        AccountInboxState state = GetState(account.Id);
        state.PrepareRefresh();
        ContinuationToken = null;
        ListErrorMessage = null;
        FailureKind = null;
        await LoadPageAsync(account, state, replace: true, _activationVersion, GetActivationToken());
    }

    private async Task LoadMoreAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account)
        {
            return;
        }

        AccountInboxState state = GetState(account.Id);
        if (string.IsNullOrWhiteSpace(state.ContinuationToken))
        {
            return;
        }

        await LoadPageAsync(account, state, replace: false, _activationVersion, GetActivationToken());
    }

    private async Task RetryAsync()
    {
        if (ActiveAccount is not { IsEnabled: true } account)
        {
            return;
        }

        AccountInboxState state = GetState(account.Id);
        if (state.Messages.Count == 0)
        {
            state.Reset();
            ApplyState(state);
            await LoadPageAsync(account, state, replace: true, _activationVersion, GetActivationToken());
        }
        else
        {
            await LoadPageAsync(account, state, replace: false, _activationVersion, GetActivationToken());
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
        AccountInboxState state,
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
            MailPage<MailMessageSummary> page = await provider.GetInboxPageAsync(
                account,
                replace ? null : state.ContinuationToken,
                InitialPageSize,
                cancellationToken);
            if (!IsCurrent(account.Id, version, cancellationToken))
            {
                return;
            }

            if (replace)
            {
                MailMessageSummary? retainedSelection = state.SelectedMessageKey is null
                    ? null
                    : state.Messages.FirstOrDefault(
                        message => message.MessageKey == state.SelectedMessageKey);
                state.Messages.Clear();

                HashSet<string> refreshedKeys = new(StringComparer.Ordinal);
                foreach (MailMessageSummary summary in page.Items)
                {
                    if (refreshedKeys.Add(summary.MessageKey))
                    {
                        state.Messages.Add(summary);
                    }
                }

                if (retainedSelection is not null
                    && refreshedKeys.Add(retainedSelection.MessageKey))
                {
                    state.Messages.Add(retainedSelection);
                }
            }
            else
            {
                HashSet<string> existingKeys = state.Messages
                    .Select(message => message.MessageKey)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (MailMessageSummary summary in page.Items)
                {
                    if (existingKeys.Add(summary.MessageKey))
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
                && state.SelectedContent is null
                && SelectedMessageSummary is MailMessageSummary restoredSelection
                && CurrentMessageLoadTask.IsCompleted)
            {
                CurrentMessageLoadTask = LoadSelectedMessageAsync(restoredSelection);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer account selection or request owns the UI state.
        }
        catch (MailReadException exception) when (IsCurrent(account.Id, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = exception.UserMessage;
            state.FailureKind = exception.FailureKind;
            ApplyState(state);
        }
        catch (Exception) when (IsCurrent(account.Id, version, cancellationToken))
        {
            state.HasLoaded = true;
            state.ListErrorMessage = "Не удалось загрузить почту. Попробуйте ещё раз.";
            state.FailureKind = MailReadFailureKind.ConnectionFailed;
            ApplyState(state);
        }
        finally
        {
            if (IsCurrent(account.Id, version, cancellationToken))
            {
                IsListLoading = false;
            }
        }
    }

    private async Task LoadSelectedMessageAsync(MailMessageSummary summary)
    {
        if (ActiveAccount is not { IsEnabled: true } account)
        {
            return;
        }

        long version = _activationVersion;
        CancelMessageOperation();
        _messageCancellation = CancellationTokenSource.CreateLinkedTokenSource(GetActivationToken());
        CancellationToken cancellationToken = _messageCancellation.Token;
        IsMessageLoading = true;
        MessageErrorMessage = null;
        SelectedMessageContent = null;
        try
        {
            IMailReadProvider provider = _providerFactory.Get(account.Provider);
            MailMessageContent content = await provider.GetMessageAsync(
                account,
                summary.MessageKey,
                cancellationToken);
            if (!IsCurrent(account.Id, version, cancellationToken)
                || SelectedMessageSummary?.MessageKey != summary.MessageKey)
            {
                return;
            }

            AccountInboxState state = GetState(account.Id);
            state.SelectedMessageKey = summary.MessageKey;
            state.SelectedContent = content;
            SelectedMessageContent = content;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer message or account selection owns the viewer.
        }
        catch (MailReadException exception) when (IsCurrent(account.Id, version, cancellationToken))
        {
            MessageErrorMessage = exception.UserMessage;
        }
        catch (Exception) when (IsCurrent(account.Id, version, cancellationToken))
        {
            MessageErrorMessage = "Не удалось загрузить выбранное письмо.";
        }
        finally
        {
            if (IsCurrent(account.Id, version, cancellationToken))
            {
                IsMessageLoading = false;
            }
        }
    }

    private void ApplyState(AccountInboxState state)
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
            MailMessageSummary? selectedMessage = state.SelectedMessageKey is null
                ? null
                : Messages.FirstOrDefault(message => message.MessageKey == state.SelectedMessageKey);
            if (!ReferenceEquals(_selectedMessageSummary, selectedMessage))
            {
                _selectedMessageSummary = selectedMessage;
                OnPropertyChanged(nameof(SelectedMessageSummary));
                OnPropertyChanged(nameof(HasSelectedMessage));
            }

            SelectedMessageContent = state.SelectedContent;
            MessageErrorMessage = null;
            RaiseListStateChanged();
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void ClearDisplayedState()
    {
        _isApplyingState = true;
        try
        {
            Messages.Clear();
            SelectedMessageSummary = null;
            SelectedMessageContent = null;
            ContinuationToken = null;
            HasLoaded = false;
            IsListLoading = false;
            IsMessageLoading = false;
            ListErrorMessage = null;
            MessageErrorMessage = null;
            FailureKind = null;
            RaiseListStateChanged();
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private AccountInboxState GetState(Guid accountId)
    {
        if (!_accountStates.TryGetValue(accountId, out AccountInboxState? state))
        {
            state = new AccountInboxState();
            _accountStates.Add(accountId, state);
        }

        return state;
    }

    private bool IsCurrent(Guid accountId, long version, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ActiveAccount?.Id == accountId
        && _activationVersion == version;

    private CancellationToken GetActivationToken() =>
        _activationCancellation?.Token ?? CancellationToken.None;

    private bool CanRefresh() => IsActive && !IsListLoading;
    private bool CanLoadMore() => IsActive && HasMore && !IsListLoading;
    private bool CanRetry() => IsActive && HasListError && !IsListLoading;
    private bool CanRetryMessage() => IsActive && HasMessageError && !IsMessageLoading;

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

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RetryMessageCommand.NotifyCanExecuteChanged();
    }

    private bool TryGetCurrentRemoteImageConsentKey(out RemoteImageConsentKey key)
    {
        if (ActiveAccount is MailAccount account
            && SelectedMessageContent is MailMessageContent content)
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class AccountInboxState
    {
        public List<MailMessageSummary> Messages { get; } = [];
        public string? ContinuationToken { get; set; }
        public string? SelectedMessageKey { get; set; }
        public MailMessageContent? SelectedContent { get; set; }
        public bool HasLoaded { get; set; }
        public string? ListErrorMessage { get; set; }
        public MailReadFailureKind? FailureKind { get; set; }

        public void Reset()
        {
            Messages.Clear();
            ContinuationToken = null;
            SelectedMessageKey = null;
            SelectedContent = null;
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
    }

    private readonly record struct RemoteImageConsentKey(Guid AccountId, string MessageKey);
}
