using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Branding;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IBuiltInServiceCatalog _serviceCatalog;
    private readonly IWebViewSessionManager _webViewSessionManager;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly IServiceActivityCoordinator _activityCoordinator;
    private readonly IWebNotificationCoordinator _notificationCoordinator;
    private readonly IMailActivityCoordinator? _mailActivityCoordinator;
    private readonly ILanternSoundFileService? _lanternSoundFileService;
    private readonly INotificationSoundPlayer? _notificationSoundPlayer;
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _isInitialized;
    private bool _disposed;
    private bool _isSynchronizingSelection;

    public MainWindowViewModel(
        IBuiltInServiceCatalog serviceCatalog,
        IWebViewSessionManager webViewSessionManager,
        IApplicationSettingsStore settingsStore,
        IServiceActivityCoordinator activityCoordinator,
        IWebNotificationCoordinator notificationCoordinator,
        IMailActivityCoordinator? mailActivityCoordinator = null,
        ILanternSoundFileService? lanternSoundFileService = null,
        INotificationSoundPlayer? notificationSoundPlayer = null)
    {
        _serviceCatalog = serviceCatalog;
        _webViewSessionManager = webViewSessionManager;
        _settingsStore = settingsStore;
        _activityCoordinator = activityCoordinator;
        _notificationCoordinator = notificationCoordinator;
        _mailActivityCoordinator = mailActivityCoordinator;
        _lanternSoundFileService = lanternSoundFileService;
        _notificationSoundPlayer = notificationSoundPlayer;
        AvailableServices = serviceCatalog.All
            .Where(definition => definition.IsWebViewService && definition.ServiceType != ServiceType.Gmail)
            .ToArray();
        _webViewSessionManager.StateChanged += OnWebViewSessionStateChanged;
    }

    public event EventHandler? SelectedServiceChanged;

    public ObservableCollection<ServiceInstance> Services { get; } = [];
    public ObservableCollection<MailAccount> MailAccounts { get; } = [];
    public ObservableCollection<NavigationAccountItem> NavigationItems { get; } = [];
    public IReadOnlyList<ServiceDefinition> AvailableServices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAccount))]
    [NotifyPropertyChangedFor(nameof(IsMailSelected))]
    [NotifyPropertyChangedFor(nameof(IsSelectedMailAccountEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSelectedMailAccountDisabled))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedAccountDisplayName))]
    [NotifyPropertyChangedFor(nameof(SelectedAccountLabel))]
    private NavigationAccountItem? _selectedNavigationItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMailSelected))]
    [NotifyPropertyChangedFor(nameof(IsSelectedMailAccountEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSelectedMailAccountDisabled))]
    private MailAccount? _selectedMailAccount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedService))]
    [NotifyPropertyChangedFor(nameof(HasActiveWebView))]
    [NotifyPropertyChangedFor(nameof(IsSelectedServiceDisabled))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedServiceLabel))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(NavigateHomeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSelectedMuteCommand))]
    [NotifyPropertyChangedFor(nameof(IsSelectedServiceMuted))]
    [NotifyPropertyChangedFor(nameof(MuteButtonText))]
    [NotifyPropertyChangedFor(nameof(MuteButtonToolTip))]
    private ServiceInstance? _selectedService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWebViewError))]
    [NotifyPropertyChangedFor(nameof(IsWebViewInitializing))]
    private WebViewSessionStatus _webViewStatus = WebViewSessionStatus.Uninitialized;

    [ObservableProperty]
    private string? _webViewErrorTitle;

    [ObservableProperty]
    private string? _webViewErrorMessage;

    [ObservableProperty]
    private string? _webViewErrorCode;

    [ObservableProperty]
    private string? _webViewRuntimeVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isSettingsOpen;

    public bool HasSelectedService => SelectedService is not null;
    public bool HasSelectedAccount => SelectedNavigationItem is not null;
    public bool HasActiveWebView => SelectedService?.IsEnabled == true;
    public bool IsSelectedServiceDisabled => SelectedService is { IsEnabled: false };
    public bool IsMailSelected => SelectedMailAccount is not null;
    public bool IsSelectedMailAccountEnabled => SelectedMailAccount is { IsEnabled: true };
    public bool IsSelectedMailAccountDisabled => SelectedMailAccount is { IsEnabled: false };
    public bool HasWebViewError => HasActiveWebView
        && WebViewStatus is WebViewSessionStatus.Offline or WebViewSessionStatus.Failed;
    public bool IsWebViewInitializing => HasActiveWebView
        && WebViewStatus is WebViewSessionStatus.Uninitialized or WebViewSessionStatus.Initializing;
    public bool CloseToTray => _settings.CloseToTray;
    public bool HasShownTrayHint => _settings.HasShownTrayHint;
    public bool NotificationsEnabled => _settings.Notifications.IsEnabled;
    public bool DoNotDisturb => _settings.Notifications.DoNotDisturb;
    public bool ShowNotificationPreview => _settings.Notifications.ShowNotificationPreview;
    public bool NotificationSoundEnabled => _settings.Notifications.PlaySound;
    public NotificationSoundMode TelegramNotificationSoundMode =>
        _settings.Notifications.TelegramSoundMode;
    public NotificationSoundMode WhatsAppNotificationSoundMode =>
        _settings.Notifications.WhatsAppSoundMode;
    public NotificationSoundMode MaxNotificationSoundMode =>
        _settings.Notifications.MaxSoundMode;
    public LanternSoundSource LanternSoundSource => _settings.Notifications.LanternSoundSource;
    public string? LanternCustomSoundDisplayName => _settings.Notifications.CustomSoundDisplayName;
    public bool IsSelectedServiceMuted => SelectedService?.IsMuted == true;
    public string MuteButtonText => IsSelectedServiceMuted ? "🔕" : "🔔";
    public string MuteButtonToolTip => IsSelectedServiceMuted
        ? "Включить уведомления аккаунта"
        : "Отключить уведомления аккаунта";

    public string WindowTitle => IsSettingsOpen
        ? BrandIdentity.CreateWindowTitle("Настройки")
        : BrandIdentity.CreateWindowTitle(SelectedNavigationItem?.DisplayName);

    public string SelectedAccountDisplayName => SelectedNavigationItem?.DisplayName ?? BrandIdentity.DisplayName;

    public string SelectedAccountLabel => SelectedMailAccount is MailAccount mailAccount
        ? $"{GetMailProviderDisplayName(mailAccount.Provider)} · Почта"
        : SelectedServiceLabel;

    public string SelectedServiceLabel => SelectedService is null
        ? "WebView2"
        : $"{_serviceCatalog.Get(SelectedService.ServiceType).DisplayName} · WebView2";

    public WindowSettings WindowSettings => _settings.Window;

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _settingsStore.Initialize(settings);
        _isInitialized = false;
        _activityCoordinator.Reset(settings.Services);
        _mailActivityCoordinator?.Reset(settings.MailAccounts);

        Services.Clear();
        foreach (ServiceInstance service in ServiceInstanceManager.Sort(settings.Services))
        {
            Services.Add(service);
        }

        MailAccounts.Clear();
        foreach (MailAccount account in settings.MailAccounts.OrderBy(account => account.SortOrder))
        {
            MailAccounts.Add(account);
        }

        RebuildNavigationItems();

        Guid? restoredId = settings.RestoreLastService
            ? settings.LastNavigationAccountId ?? settings.LastServiceId
            : null;
        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.Id == restoredId)
            ?? NavigationItems.FirstOrDefault(item => item.IsEnabled)
            ?? NavigationItems.FirstOrDefault();
        SelectedService = SelectedNavigationItem?.Service;
        SelectedMailAccount = SelectedNavigationItem?.MailAccount;
        _isInitialized = true;
    }

    public async Task<ServiceInstance> AddServiceAsync(ServiceType serviceType, string? displayName)
    {
        ThrowIfDisposed();
        ServiceDefinition definition = _serviceCatalog.Get(serviceType);
        ServiceInstance service = ServiceInstanceManager.Add(_settings, definition, displayName);
        Services.Add(service);
        NavigationAccountItem item = NavigationAccountItem.FromService(service);
        NavigationItems.Add(item);
        SelectedNavigationItem = item;
        await SaveSettingsAsync();
        return service;
    }

    public void AddConnectedMailAccount(MailAccount account)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(account);
        if (MailAccounts.Any(existing => existing.Id == account.Id))
        {
            return;
        }

        MailAccounts.Add(account);
        NavigationAccountItem item = NavigationAccountItem.FromMail(account);
        NavigationItems.Add(item);
        SelectedNavigationItem = item;
        IsSettingsOpen = false;
    }

    public async Task RenameMailAccountAsync(MailAccount account, string displayName)
    {
        MailAccount target = GetExistingMailAccount(account);
        target.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SelectedAccountDisplayName));
        await SaveSettingsAsync();
    }

    public async Task SetMailAccountEnabledAsync(MailAccount account, bool isEnabled)
    {
        MailAccount target = GetExistingMailAccount(account);
        target.IsEnabled = isEnabled;
        if (!isEnabled)
        {
            _mailActivityCoordinator?.Clear(target);
        }
        OnPropertyChanged(nameof(IsSelectedMailAccountEnabled));
        OnPropertyChanged(nameof(IsSelectedMailAccountDisabled));
        await SaveSettingsAsync();
    }

    public void RemoveMailAccountFromNavigation(MailAccount account)
    {
        MailAccount target = GetExistingMailAccount(account);
        _mailActivityCoordinator?.Clear(target);
        NavigationAccountItem? item = NavigationItems.FirstOrDefault(candidate => candidate.Id == target.Id);
        bool wasSelected = SelectedNavigationItem?.Id == target.Id;
        int removedIndex = item is null ? -1 : NavigationItems.IndexOf(item);
        MailAccounts.Remove(target);
        if (item is not null)
        {
            NavigationItems.Remove(item);
            item.Dispose();
        }

        if (wasSelected)
        {
            SelectedNavigationItem = NavigationItems.Count == 0
                ? null
                : NavigationItems[Math.Clamp(removedIndex, 0, NavigationItems.Count - 1)];
        }
    }

    public async Task RenameServiceAsync(ServiceInstance service, string displayName)
    {
        ThrowIfDisposed();
        ServiceInstance target = GetExistingService(service);
        ServiceInstanceManager.Rename(target, displayName);
        OnPropertyChanged(nameof(WindowTitle));
        await SaveSettingsAsync();
    }

    public async Task SetServiceEnabledAsync(ServiceInstance service, bool isEnabled)
    {
        ThrowIfDisposed();
        ServiceInstance target = GetExistingService(service);
        ServiceInstanceManager.SetEnabled(target, isEnabled);
        if (!isEnabled)
        {
            _activityCoordinator.Clear(target);
            _notificationCoordinator.DiscardPending(target.Id);
        }

        NotifySelectedServiceStateChanged();
        await SaveSettingsAsync();
        if (!isEnabled
            && !target.IsEnabled
            && target.ServiceType is ServiceType.Telegram)
        {
            _webViewSessionManager.ReleaseSession(target.Id);
        }
    }

    public async Task SetServiceMutedAsync(ServiceInstance service, bool isMuted)
    {
        ThrowIfDisposed();
        ServiceInstance target = GetExistingService(service);
        if (target.IsMuted == isMuted)
        {
            return;
        }

        target.IsMuted = isMuted;
        if (isMuted)
        {
            _notificationCoordinator.DiscardPending(target.Id);
        }

        if (SelectedService?.Id == target.Id)
        {
            OnPropertyChanged(nameof(IsSelectedServiceMuted));
            OnPropertyChanged(nameof(MuteButtonText));
            OnPropertyChanged(nameof(MuteButtonToolTip));
        }

        await SaveSettingsAsync();
    }

    public async Task<bool> MoveServiceAsync(ServiceInstance service, int offset)
    {
        ThrowIfDisposed();
        ServiceInstance target = GetExistingService(service);
        int currentIndex = Services.IndexOf(target);
        if (!ServiceInstanceManager.Move(_settings.Services, target.Id, offset))
        {
            return false;
        }

        int targetIndex = currentIndex + offset;
        Services.Move(currentIndex, targetIndex);
        await SaveSettingsAsync();
        return true;
    }

    public async Task RemoveServiceAsync(ServiceInstance service, bool profileWasDeleted)
    {
        ThrowIfDisposed();
        ServiceInstance target = GetExistingService(service);
        int removedIndex = Services.IndexOf(target);
        bool wasSelected = SelectedService?.Id == target.Id;

        if (!profileWasDeleted
            && !_settings.PendingProfileDeletions.Contains(target.ProfileName, StringComparer.Ordinal))
        {
            _settings.PendingProfileDeletions.Add(target.ProfileName);
        }

        _settings.PendingProfileDeletions.RemoveAll(profileName =>
            profileWasDeleted && string.Equals(profileName, target.ProfileName, StringComparison.Ordinal));

        _ = ServiceInstanceManager.Remove(_settings.Services, target.Id);
        Services.Remove(target);
        NavigationAccountItem? navigationItem = NavigationItems.FirstOrDefault(item => item.Id == target.Id);
        if (navigationItem is not null)
        {
            NavigationItems.Remove(navigationItem);
            navigationItem.Dispose();
        }

        if (wasSelected)
        {
            SelectedNavigationItem = NavigationItems.Count == 0
                ? null
                : NavigationItems[Math.Min(removedIndex, NavigationItems.Count - 1)];
        }

        await SaveSettingsAsync();
    }

    public Task PersistSelectionAsync() => SaveSettingsAsync();

    public void SelectService(Guid serviceInstanceId)
    {
        ServiceInstance? service = Services.FirstOrDefault(candidate => candidate.Id == serviceInstanceId);
        if (service is not null)
        {
            SelectedNavigationItem = NavigationItems.First(item => item.Id == service.Id);
            IsSettingsOpen = false;
        }
    }

    public void SelectMailAccount(Guid mailAccountId)
    {
        NavigationAccountItem? item = NavigationItems.FirstOrDefault(candidate =>
            candidate.Id == mailAccountId && candidate.IsMailAccount);
        if (item is not null)
        {
            SelectedNavigationItem = item;
            IsSettingsOpen = false;
        }
    }

    public void MarkSelectedServiceViewed(bool isMainWindowVisible, bool isMainWindowActive)
    {
        if (IsSettingsOpen || !isMainWindowVisible || !isMainWindowActive)
        {
            return;
        }

        if (SelectedService is ServiceInstance service)
        {
            _activityCoordinator.Clear(service);
        }

        if (SelectedMailAccount is MailAccount mailAccount)
        {
            _mailActivityCoordinator?.Clear(mailAccount);
        }
    }

    public async Task<bool> MarkTrayHintShownAsync()
    {
        if (_settings.HasShownTrayHint)
        {
            return false;
        }

        _settings.HasShownTrayHint = true;
        OnPropertyChanged(nameof(HasShownTrayHint));
        await SaveSettingsAsync();
        return true;
    }

    public async Task ResetTrayHintAsync()
    {
        ThrowIfDisposed();
        if (!_settings.HasShownTrayHint)
        {
            return;
        }

        _settings.HasShownTrayHint = false;
        OnPropertyChanged(nameof(HasShownTrayHint));
        await SaveSettingsAsync();
    }

    public async Task SetCloseToTrayAsync(bool value)
    {
        ThrowIfDisposed();
        if (_settings.CloseToTray == value)
        {
            return;
        }

        _settings.CloseToTray = value;
        OnPropertyChanged(nameof(CloseToTray));
        await SaveSettingsAsync();
    }

    public async Task SetNotificationsEnabledAsync(bool value)
    {
        ThrowIfDisposed();
        if (_settings.Notifications.IsEnabled == value)
        {
            return;
        }

        _settings.Notifications.IsEnabled = value;
        OnPropertyChanged(nameof(NotificationsEnabled));
        await SaveSettingsAsync();
    }

    public async Task SetDoNotDisturbAsync(bool value)
    {
        ThrowIfDisposed();
        if (_settings.Notifications.DoNotDisturb == value)
        {
            return;
        }

        _settings.Notifications.DoNotDisturb = value;
        OnPropertyChanged(nameof(DoNotDisturb));
        await SaveSettingsAsync();
    }

    public async Task SetShowNotificationPreviewAsync(bool value)
    {
        ThrowIfDisposed();
        if (_settings.Notifications.ShowNotificationPreview == value)
        {
            return;
        }

        _settings.Notifications.ShowNotificationPreview = value;
        OnPropertyChanged(nameof(ShowNotificationPreview));
        await SaveSettingsAsync();
    }

    public async Task SetNotificationSoundEnabledAsync(bool value)
    {
        ThrowIfDisposed();
        if (_settings.Notifications.PlaySound == value)
        {
            return;
        }

        _settings.Notifications.PlaySound = value;
        OnPropertyChanged(nameof(NotificationSoundEnabled));
        await SaveSettingsAsync();
    }

    public async Task SetNotificationSoundModeAsync(
        ServiceType serviceType,
        NotificationSoundMode mode)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(mode)
            || serviceType is not (ServiceType.Telegram or ServiceType.WhatsApp or ServiceType.Max)
            || _settings.Notifications.GetSoundMode(serviceType) == mode)
        {
            return;
        }

        _ = _settings.Notifications.TrySetSoundMode(serviceType, mode);
        OnPropertyChanged(GetSoundModePropertyName(serviceType));
        await SaveSettingsAsync();
    }

    public async Task<LanternSoundImportResult> ImportCustomLanternSoundAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_lanternSoundFileService is null)
        {
            return LanternSoundImportResult.Failed("Выбор пользовательского звука недоступен.");
        }

        LanternSoundImportResult result = await _lanternSoundFileService.ImportAsync(
            sourceFilePath,
            cancellationToken);
        if (!result.Success)
        {
            return result;
        }

        NotificationSettings notifications = _settings.Notifications;
        notifications.CustomSoundInternalFileName = result.InternalFileName;
        notifications.CustomSoundDisplayName = result.DisplayName;
        notifications.LanternSoundSource = LanternSoundSource.Custom;
        NotifyLanternSoundSettingsChanged();
        await SaveSettingsAsync();
        return result;
    }

    public async Task<bool> UseExistingCustomLanternSoundAsync()
    {
        ThrowIfDisposed();
        NotificationSettings notifications = _settings.Notifications;
        if (_lanternSoundFileService is null
            || !LanternSoundFilePolicy.IsSafeInternalFileName(notifications.CustomSoundInternalFileName)
            || string.Equals(
                _lanternSoundFileService.ResolvePlaybackPath(notifications),
                _lanternSoundFileService.DefaultSoundPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (notifications.LanternSoundSource is not LanternSoundSource.Custom)
        {
            notifications.LanternSoundSource = LanternSoundSource.Custom;
            NotifyLanternSoundSettingsChanged();
            await SaveSettingsAsync();
        }

        return true;
    }

    public async Task RestoreDefaultLanternSoundAsync()
    {
        ThrowIfDisposed();
        NotificationSettings notifications = _settings.Notifications;
        string? previousInternalFileName = notifications.CustomSoundInternalFileName;
        notifications.LanternSoundSource = LanternSoundSource.Default;
        notifications.CustomSoundInternalFileName = null;
        notifications.CustomSoundDisplayName = null;
        NotifyLanternSoundSettingsChanged();
        await SaveSettingsAsync();
        _lanternSoundFileService?.DeleteInternalCopy(previousInternalFileName);
    }

    public bool PreviewLanternSound()
    {
        ThrowIfDisposed();
        return _notificationSoundPlayer?.TryPreviewLanternSound() == true;
    }

    public void SetRuntimeInfo(WebViewRuntimeInfo runtimeInfo)
    {
        ArgumentNullException.ThrowIfNull(runtimeInfo);
        WebViewRuntimeVersion = runtimeInfo.Version;
    }

    private static string GetSoundModePropertyName(ServiceType serviceType) =>
        serviceType switch
        {
            ServiceType.Telegram => nameof(TelegramNotificationSoundMode),
            ServiceType.WhatsApp => nameof(WhatsAppNotificationSoundMode),
            ServiceType.Max => nameof(MaxNotificationSoundMode),
            _ => throw new ArgumentOutOfRangeException(nameof(serviceType))
        };

    private void NotifyLanternSoundSettingsChanged()
    {
        OnPropertyChanged(nameof(LanternSoundSource));
        OnPropertyChanged(nameof(LanternCustomSoundDisplayName));
    }

    public void NotifySelectedServiceStateChanged()
    {
        OnPropertyChanged(nameof(HasActiveWebView));
        OnPropertyChanged(nameof(IsSelectedServiceDisabled));
        OnPropertyChanged(nameof(HasWebViewError));
        OnPropertyChanged(nameof(IsWebViewInitializing));
        ReloadCommand.NotifyCanExecuteChanged();
        NavigateHomeCommand.NotifyCanExecuteChanged();
    }

    public void UpdateWindowSettings(double width, double height, double left, double top, bool isMaximized)
    {
        if (!double.IsNaN(width) && width >= 880)
        {
            _settings.Window.Width = width;
        }

        if (!double.IsNaN(height) && height >= 560)
        {
            _settings.Window.Height = height;
        }

        _settings.Window.Left = left;
        _settings.Window.Top = top;
        _settings.Window.IsMaximized = isMaximized;
    }

    public AppSettings CreateSettingsSnapshot()
    {
        _settings.Services = ServiceInstanceManager.Sort(Services).ToList();
        _settings.MailAccounts = MailAccounts.OrderBy(account => account.SortOrder).ToList();
        _settings.LastNavigationAccountId = SelectedNavigationItem?.Id;
        if (SelectedService is not null)
        {
            _settings.LastServiceId = SelectedService.Id;
        }
        return _settings;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _webViewSessionManager.StateChanged -= OnWebViewSessionStateChanged;
        foreach (NavigationAccountItem item in NavigationItems)
        {
            item.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    private void GoBack() => _webViewSessionManager.GoBack();

    private bool CanNavigateBack() => HasActiveWebView && CanGoBack;

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    private void GoForward() => _webViewSessionManager.GoForward();

    private bool CanNavigateForward() => HasActiveWebView && CanGoForward;

    [RelayCommand(CanExecute = nameof(CanUseSelectedWebView))]
    private void Reload() => _webViewSessionManager.Reload();

    [RelayCommand(CanExecute = nameof(CanUseSelectedWebView))]
    private void NavigateHome() => _webViewSessionManager.NavigateHome();

    [RelayCommand]
    private void Retry() => _webViewSessionManager.Retry();

    [RelayCommand]
    private async Task ToggleDoNotDisturb()
    {
        await SetDoNotDisturbAsync(!_settings.Notifications.DoNotDisturb);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedService))]
    private async Task ToggleSelectedMute()
    {
        if (SelectedService is not ServiceInstance service)
        {
            return;
        }

        await SetServiceMutedAsync(service, !service.IsMuted);
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    private bool CanUseSelectedWebView() => HasActiveWebView;

    partial void OnSelectedServiceChanged(ServiceInstance? value)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        NavigationAccountItem? item = value is null
            ? null
            : NavigationItems.FirstOrDefault(candidate => candidate.Id == value.Id);
        if (!ReferenceEquals(SelectedNavigationItem, item))
        {
            SelectedNavigationItem = item;
            return;
        }

        if (!_isInitialized)
        {
            return;
        }

        _settings.LastServiceId = value?.Id;
        if (value is not null)
        {
            value.LastOpenedAt = DateTimeOffset.UtcNow;
        }

        OnPropertyChanged(nameof(IsSelectedServiceMuted));
        OnPropertyChanged(nameof(MuteButtonText));
        OnPropertyChanged(nameof(MuteButtonToolTip));

        SelectedServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedNavigationItemChanged(NavigationAccountItem? value)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        _isSynchronizingSelection = true;
        try
        {
            SelectedService = value?.Service;
            SelectedMailAccount = value?.MailAccount;
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        if (!_isInitialized)
        {
            return;
        }

        _settings.LastNavigationAccountId = value?.Id;
        if (value?.Service is ServiceInstance service)
        {
            _settings.LastServiceId = service.Id;
            service.LastOpenedAt = DateTimeOffset.UtcNow;
        }

        if (value?.MailAccount is MailAccount mailAccount)
        {
            _mailActivityCoordinator?.Clear(mailAccount);
        }

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SelectedAccountDisplayName));
        OnPropertyChanged(nameof(SelectedAccountLabel));
        OnPropertyChanged(nameof(IsSelectedServiceMuted));
        OnPropertyChanged(nameof(MuteButtonText));
        OnPropertyChanged(nameof(MuteButtonToolTip));
        NotifySelectedServiceStateChanged();
        SelectedServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveSettingsAsync()
    {
        if (!_isInitialized || _disposed)
        {
            return;
        }

        _ = CreateSettingsSnapshot();
        await _settingsStore.SaveAsync();
    }

    private ServiceInstance GetExistingService(ServiceInstance service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return Services.FirstOrDefault(existing => existing.Id == service.Id)
            ?? throw new InvalidOperationException("The service account is no longer available.");
    }

    private MailAccount GetExistingMailAccount(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return MailAccounts.FirstOrDefault(existing => existing.Id == account.Id)
            ?? throw new InvalidOperationException("The mail account is no longer available.");
    }

    private void RebuildNavigationItems()
    {
        foreach (NavigationAccountItem item in NavigationItems)
        {
            item.Dispose();
        }

        NavigationItems.Clear();
        foreach (ServiceInstance service in Services)
        {
            NavigationItems.Add(NavigationAccountItem.FromService(service));
        }

        foreach (MailAccount account in MailAccounts.OrderBy(account => account.SortOrder))
        {
            NavigationItems.Add(NavigationAccountItem.FromMail(account));
        }
    }

    private static string GetMailProviderDisplayName(MailProviderType provider) => provider switch
    {
        MailProviderType.Gmail => "Gmail",
        MailProviderType.Yandex => "Яндекс Почта",
        MailProviderType.MailRu => "Почта Mail.ru",
        MailProviderType.GenericImap => "IMAP/SMTP",
        _ => "Почта"
    };

    private void OnWebViewSessionStateChanged(object? sender, WebViewSessionStateChangedEventArgs eventArgs)
    {
        CanGoBack = eventArgs.State.CanGoBack;
        CanGoForward = eventArgs.State.CanGoForward;
        IsLoading = eventArgs.State.IsLoading;
        WebViewErrorTitle = eventArgs.State.ErrorTitle;
        WebViewErrorMessage = eventArgs.State.ErrorMessage;
        WebViewErrorCode = eventArgs.State.ErrorCode;
        WebViewStatus = eventArgs.State.Status;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
