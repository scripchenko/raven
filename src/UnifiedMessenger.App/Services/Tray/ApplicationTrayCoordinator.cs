using System.ComponentModel;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Branding;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.App.Services.Tray;

public sealed class ApplicationTrayCoordinator : IApplicationTrayCoordinator
{
    private readonly ITrayIconService _trayIcon;
    private readonly IWindowActivationService _windowActivation;
    private readonly IApplicationExitCoordinator _exitCoordinator;
    private readonly IWebNotificationCoordinator _notificationCoordinator;
    private readonly IServiceActivityCoordinator _activityCoordinator;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly MainWindowViewModel _viewModel;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ITaskbarActivityIndicator _taskbarActivityIndicator;
    private readonly IMailActivityCoordinator? _mailActivityCoordinator;
    private readonly IMailNotificationCoordinator? _mailNotificationCoordinator;
    private bool _initialized;
    private bool _subscribed;
    private bool _shutdownStarted;
    private bool _disposed;

    public ApplicationTrayCoordinator(
        ITrayIconService trayIcon,
        IWindowActivationService windowActivation,
        IApplicationExitCoordinator exitCoordinator,
        IWebNotificationCoordinator notificationCoordinator,
        IServiceActivityCoordinator activityCoordinator,
        IApplicationSettingsStore settingsStore,
        MainWindowViewModel viewModel,
        IUiDispatcher uiDispatcher,
        ITaskbarActivityIndicator taskbarActivityIndicator,
        IMailActivityCoordinator? mailActivityCoordinator = null,
        IMailNotificationCoordinator? mailNotificationCoordinator = null)
    {
        _trayIcon = trayIcon;
        _windowActivation = windowActivation;
        _exitCoordinator = exitCoordinator;
        _notificationCoordinator = notificationCoordinator;
        _activityCoordinator = activityCoordinator;
        _settingsStore = settingsStore;
        _viewModel = viewModel;
        _uiDispatcher = uiDispatcher;
        _taskbarActivityIndicator = taskbarActivityIndicator;
        _mailActivityCoordinator = mailActivityCoordinator;
        _mailNotificationCoordinator = mailNotificationCoordinator;
    }

    public bool IsAvailable => _initialized && !_shutdownStarted && !_disposed;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        Subscribe();
        try
        {
            _trayIcon.Show(
                _settingsStore.Current.Notifications.DoNotDisturb,
                _activityCoordinator.CreateTrayToolTip(_settingsStore.Current.Services));
            _initialized = true;
            UpdateActivityIndicators();
        }
        catch
        {
            Unsubscribe();
            _shutdownStarted = true;
            _trayIcon.BeginShutdown();
            _notificationCoordinator.Shutdown();
            _mailNotificationCoordinator?.Shutdown();
            throw;
        }
    }

    public bool TryShowCloseToTrayHint()
    {
        if (_disposed
            || _shutdownStarted
            || _settingsStore.Current.Notifications.DoNotDisturb
            || _notificationCoordinator.HasActiveNotification)
        {
            return false;
        }

        return _trayIcon.TryShowBalloon(
            BrandIdentity.DisplayName,
            "raven продолжает работать в области уведомлений");
    }

    public void BeginShutdown()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        Unsubscribe();
        _trayIcon.BeginShutdown();
        _notificationCoordinator.Shutdown();
        _mailNotificationCoordinator?.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        BeginShutdown();
        _disposed = true;
    }

    private void OnOpenRequested(object? sender, EventArgs eventArgs)
    {
        if (!_shutdownStarted)
        {
            _uiDispatcher.Post(() => _windowActivation.ShowAndActivate());
        }
    }

    private void OnSettingsRequested(object? sender, EventArgs eventArgs)
    {
        if (!_shutdownStarted)
        {
            _uiDispatcher.Post(
                () =>
                {
                    _viewModel.OpenSettingsCommand.Execute(null);
                    _windowActivation.ShowAndActivate();
                });
        }
    }

    private void OnDoNotDisturbToggleRequested(object? sender, EventArgs eventArgs)
    {
        if (!_shutdownStarted)
        {
            _uiDispatcher.Post(() => _ = ToggleDoNotDisturbAsync());
        }
    }

    private async Task ToggleDoNotDisturbAsync()
    {
        await _viewModel.ToggleDoNotDisturbCommand.ExecuteAsync(null);
    }

    private void OnExitRequested(object? sender, EventArgs eventArgs)
    {
        _exitCoordinator.RequestExit();
    }

    private void OnActivityChanged(object? sender, EventArgs eventArgs) =>
        _uiDispatcher.Post(UpdateActivityIndicators);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.DoNotDisturb))
        {
            _uiDispatcher.Post(SynchronizeDoNotDisturb);
        }
    }

    private void SynchronizeDoNotDisturb()
    {
        if (_shutdownStarted)
        {
            return;
        }

        bool enabled = _settingsStore.Current.Notifications.DoNotDisturb;
        _trayIcon.SetDoNotDisturb(enabled);
        _notificationCoordinator.OnDoNotDisturbChanged(enabled);
        _mailNotificationCoordinator?.OnDoNotDisturbChanged(enabled);
    }

    private void UpdateActivityIndicators()
    {
        if (_shutdownStarted)
        {
            return;
        }

        var services = _settingsStore.Current.Services.ToArray();
        _trayIcon.SetToolTip(_activityCoordinator.CreateTrayToolTip(services));
        _taskbarActivityIndicator.SetHasActivity(
            HasTaskbarActivity(services, _settingsStore.Current.MailAccounts));
    }

    internal static bool HasTaskbarActivity(
        IEnumerable<ServiceInstance> services,
        IEnumerable<MailAccount> mailAccounts)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mailAccounts);
        return services.Any(service => service.IsEnabled && service.HasUnreadActivity)
            || mailAccounts.Any(account => account.IsEnabled && account.HasNewMailActivity);
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _initialized = false;
        _trayIcon.OpenRequested -= OnOpenRequested;
        _trayIcon.SettingsRequested -= OnSettingsRequested;
        _trayIcon.DoNotDisturbToggleRequested -= OnDoNotDisturbToggleRequested;
        _trayIcon.ExitRequested -= OnExitRequested;
        _activityCoordinator.ActivityChanged -= OnActivityChanged;
        if (_mailActivityCoordinator is not null)
        {
            _mailActivityCoordinator.ActivityChanged -= OnActivityChanged;
        }
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _trayIcon.OpenRequested += OnOpenRequested;
        _trayIcon.SettingsRequested += OnSettingsRequested;
        _trayIcon.DoNotDisturbToggleRequested += OnDoNotDisturbToggleRequested;
        _trayIcon.ExitRequested += OnExitRequested;
        _activityCoordinator.ActivityChanged += OnActivityChanged;
        if (_mailActivityCoordinator is not null)
        {
            _mailActivityCoordinator.ActivityChanged += OnActivityChanged;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }
}
