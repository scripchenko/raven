using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private IApplicationSettingsStore? _settingsStore;
    private MainWindowViewModel? _mainWindowViewModel;
    private IApplicationExitCoordinator? _exitCoordinator;
    private IApplicationTrayCoordinator? _trayCoordinator;
    private IWebViewEventCoordinator? _webViewEventCoordinator;
    private IWebViewSessionManager? _webViewSessionManager;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _serviceProvider = ConfigureServices();
            ISettingsService settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            _settingsStore = _serviceProvider.GetRequiredService<IApplicationSettingsStore>();
            _mainWindowViewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            _exitCoordinator = _serviceProvider.GetRequiredService<IApplicationExitCoordinator>();
            _exitCoordinator.ExitRequested += OnExplicitExitRequested;

            SettingsLoadResult loadResult = await settingsService.LoadAsync();
            _settingsStore.Initialize(loadResult.Settings);
            IWebViewProfileCleaner profileCleaner = _serviceProvider.GetRequiredService<IWebViewProfileCleaner>();
            bool pendingProfilesChanged = await profileCleaner.ProcessPendingDeletionsAsync(loadResult.Settings);
            if (loadResult.WasMigrated || pendingProfilesChanged)
            {
                await _settingsStore.SaveAsync();
            }

            _mainWindowViewModel.Initialize(loadResult.Settings);
            _webViewSessionManager = _serviceProvider.GetRequiredService<IWebViewSessionManager>();
            _webViewEventCoordinator = _serviceProvider.GetRequiredService<IWebViewEventCoordinator>();

            MainWindow window = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            _trayCoordinator = _serviceProvider.GetRequiredService<IApplicationTrayCoordinator>();
            _trayCoordinator.Initialize();

            if (!string.IsNullOrWhiteSpace(loadResult.WarningMessage))
            {
                System.Windows.MessageBox.Show(
                    window,
                    loadResult.WarningMessage,
                    "Настройки восстановлены",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"Не удалось запустить UnifiedMessenger.\n\n{exception.Message}",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_exitCoordinator is not null)
        {
            _exitCoordinator.ExitRequested -= OnExplicitExitRequested;
        }

        _webViewEventCoordinator = null;
        _webViewSessionManager = null;
        _trayCoordinator = null;
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exitCoordinator?.BeginSessionEnding();
        _trayCoordinator?.BeginShutdown();
        _webViewEventCoordinator?.Dispose();
        _webViewEventCoordinator = null;
        _webViewSessionManager?.BeginShutdown();
        base.OnSessionEnding(e);
    }

    private async void OnExplicitExitRequested(object? sender, EventArgs eventArgs)
    {
        if (_exitCoordinator is null || !_exitCoordinator.TryBeginShutdown())
        {
            return;
        }

        await ShutdownApplicationAsync();
    }

    private async Task ShutdownApplicationAsync()
    {
        try
        {
            _trayCoordinator?.BeginShutdown();

            if (MainWindow is MainWindow mainWindow)
            {
                mainWindow.Hide();
            }

            _webViewEventCoordinator?.Dispose();
            _webViewEventCoordinator = null;
            _webViewSessionManager?.BeginShutdown();

            if (MainWindow is MainWindow loadedWindow && loadedWindow.IsLoaded)
            {
                loadedWindow.Close();
            }

            if (_settingsStore is not null && _mainWindowViewModel is not null)
            {
                _ = _mainWindowViewModel.CreateSettingsSnapshot();
                await _settingsStore.SaveAsync();
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
        {
            // The UI is already gone. Startup recovery keeps the next launch usable.
        }
        finally
        {
            _exitCoordinator?.CompleteShutdown();
            Shutdown();
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IApplicationSettingsStore, ApplicationSettingsStore>();
        services.AddSingleton<IBuiltInServiceCatalog, BuiltInServiceCatalog>();
        services.AddSingleton<NavigationPolicy>();
        services.AddSingleton<IServiceActivityCoordinator, ServiceActivityCoordinator>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<INotificationSoundPlayer, WindowsNotificationSoundPlayer>();
        services.AddSingleton<INotificationPermissionPrompt, WpfNotificationPermissionPrompt>();
        services.AddSingleton<INotificationPermissionCoordinator, NotificationPermissionCoordinator>();
        services.AddSingleton<INotificationPopupService, WpfNotificationPopupService>();
        services.AddSingleton<ITrayIconService, WinFormsTrayIconService>();
        services.AddSingleton<ITaskbarActivityIndicator, WpfTaskbarActivityIndicator>();
        services.AddSingleton<IApplicationExitCoordinator, ApplicationExitCoordinator>();
        services.AddSingleton<IWebViewRuntimeService, WebViewRuntimeService>();
        services.AddSingleton<IExternalBrowserService, ExternalBrowserService>();
        services.AddSingleton<WebNavigationService>();
        services.AddSingleton<WebNewWindowNavigationService>();
        services.AddSingleton<IWebViewProfileCleaner, WebViewProfileCleaner>();
        services.AddSingleton<WebViewSessionManager>();
        services.AddSingleton<IWebViewSessionManager>(provider =>
            provider.GetRequiredService<WebViewSessionManager>());
        services.AddSingleton<ITelegramNotificationSoundCoordinator, TelegramNotificationSoundCoordinator>();
        services.AddSingleton<IWindowActivationService, WpfWindowActivationService>();
        services.AddSingleton<IWebNotificationCoordinator, WebNotificationCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<IWebViewEventCoordinator, WebViewEventCoordinator>();
        services.AddSingleton<IApplicationTrayCoordinator, ApplicationTrayCoordinator>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }
}
