using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
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
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _isInitialized;
    private bool _disposed;

    public MainWindowViewModel(
        IBuiltInServiceCatalog serviceCatalog,
        IWebViewSessionManager webViewSessionManager,
        IApplicationSettingsStore settingsStore,
        IServiceActivityCoordinator activityCoordinator,
        IWebNotificationCoordinator notificationCoordinator)
    {
        _serviceCatalog = serviceCatalog;
        _webViewSessionManager = webViewSessionManager;
        _settingsStore = settingsStore;
        _activityCoordinator = activityCoordinator;
        _notificationCoordinator = notificationCoordinator;
        AvailableServices = serviceCatalog.All
            .Where(definition => definition.IsWebViewService && definition.ServiceType != ServiceType.Gmail)
            .ToArray();
        _webViewSessionManager.StateChanged += OnWebViewSessionStateChanged;
    }

    public event EventHandler? SelectedServiceChanged;

    public ObservableCollection<ServiceInstance> Services { get; } = [];
    public IReadOnlyList<ServiceDefinition> AvailableServices { get; }

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

    public bool HasSelectedService => SelectedService is not null;
    public bool HasActiveWebView => SelectedService?.IsEnabled == true;
    public bool IsSelectedServiceDisabled => SelectedService is { IsEnabled: false };
    public bool HasWebViewError => HasActiveWebView
        && WebViewStatus is WebViewSessionStatus.Offline or WebViewSessionStatus.Failed;
    public bool IsWebViewInitializing => HasActiveWebView
        && WebViewStatus is WebViewSessionStatus.Uninitialized or WebViewSessionStatus.Initializing;
    public bool CloseToTray => _settings.CloseToTray;
    public bool HasShownTrayHint => _settings.HasShownTrayHint;
    public bool DoNotDisturb => _settings.Notifications.DoNotDisturb;
    public bool IsSelectedServiceMuted => SelectedService?.IsMuted == true;
    public string MuteButtonText => IsSelectedServiceMuted ? "🔕" : "🔔";
    public string MuteButtonToolTip => IsSelectedServiceMuted
        ? "Включить уведомления аккаунта"
        : "Отключить уведомления аккаунта";

    public string WindowTitle => SelectedService is null
        ? "UnifiedMessenger"
        : $"{SelectedService.DisplayName} — UnifiedMessenger";

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

        Services.Clear();
        foreach (ServiceInstance service in ServiceInstanceManager.Sort(settings.Services))
        {
            Services.Add(service);
        }

        ServiceInstance? restoredService = settings.RestoreLastService && settings.LastServiceId is Guid lastServiceId
            ? Services.FirstOrDefault(service => service.Id == lastServiceId)
            : null;
        SelectedService = restoredService ?? Services.FirstOrDefault(service => service.IsEnabled) ?? Services.FirstOrDefault();
        _isInitialized = true;
    }

    public async Task<ServiceInstance> AddServiceAsync(ServiceType serviceType, string? displayName)
    {
        ThrowIfDisposed();
        ServiceDefinition definition = _serviceCatalog.Get(serviceType);
        ServiceInstance service = ServiceInstanceManager.Add(_settings, definition, displayName);
        Services.Add(service);
        SelectedService = service;
        await SaveSettingsAsync();
        return service;
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

        if (wasSelected)
        {
            SelectedService = Services.Count == 0
                ? null
                : Services[Math.Min(removedIndex, Services.Count - 1)];
        }

        await SaveSettingsAsync();
    }

    public Task PersistSelectionAsync() => SaveSettingsAsync();

    public void SelectService(Guid serviceInstanceId)
    {
        ServiceInstance? service = Services.FirstOrDefault(candidate => candidate.Id == serviceInstanceId);
        if (service is not null)
        {
            SelectedService = service;
        }
    }

    public void MarkSelectedServiceViewed(bool isMainWindowVisible, bool isMainWindowActive)
    {
        if (isMainWindowVisible && isMainWindowActive && SelectedService is ServiceInstance service)
        {
            _activityCoordinator.Clear(service);
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

    public void SetRuntimeInfo(WebViewRuntimeInfo runtimeInfo)
    {
        ArgumentNullException.ThrowIfNull(runtimeInfo);
        WebViewRuntimeVersion = runtimeInfo.Version;
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
        _settings.LastServiceId = SelectedService?.Id;
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
        _settings.Notifications.DoNotDisturb = !_settings.Notifications.DoNotDisturb;
        OnPropertyChanged(nameof(DoNotDisturb));
        await SaveSettingsAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedService))]
    private async Task ToggleSelectedMute()
    {
        if (SelectedService is not ServiceInstance service)
        {
            return;
        }

        service.IsMuted = !service.IsMuted;
        if (service.IsMuted)
        {
            _notificationCoordinator.DiscardPending(service.Id);
        }
        OnPropertyChanged(nameof(IsSelectedServiceMuted));
        OnPropertyChanged(nameof(MuteButtonText));
        OnPropertyChanged(nameof(MuteButtonToolTip));
        await SaveSettingsAsync();
    }

    private bool CanUseSelectedWebView() => HasActiveWebView;

    partial void OnSelectedServiceChanged(ServiceInstance? value)
    {
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
