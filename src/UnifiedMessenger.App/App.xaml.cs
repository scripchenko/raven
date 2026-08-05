using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private ISettingsService? _settingsService;
    private MainWindowViewModel? _mainWindowViewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _serviceProvider = ConfigureServices();
            _settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            _mainWindowViewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();

            SettingsLoadResult loadResult = await _settingsService.LoadAsync();
            IWebViewProfileCleaner profileCleaner = _serviceProvider.GetRequiredService<IWebViewProfileCleaner>();
            bool pendingProfilesChanged = await profileCleaner.ProcessPendingDeletionsAsync(loadResult.Settings);
            if (loadResult.WasMigrated || pendingProfilesChanged)
            {
                await _settingsService.SaveAsync(loadResult.Settings);
            }

            _mainWindowViewModel.Initialize(loadResult.Settings);

            MainWindow window = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

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
        if (_settingsService is not null && _mainWindowViewModel is not null)
        {
            try
            {
                _settingsService.SaveAsync(_mainWindowViewModel.CreateSettingsSnapshot())
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The application is already exiting. Startup recovery will keep it usable next time.
            }
        }

        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        ServiceCollection services = new();
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IBuiltInServiceCatalog, BuiltInServiceCatalog>();
        services.AddSingleton<NavigationPolicy>();
        services.AddSingleton<IWebViewRuntimeService, WebViewRuntimeService>();
        services.AddSingleton<IExternalBrowserService, ExternalBrowserService>();
        services.AddSingleton<WebNavigationService>();
        services.AddSingleton<IWebViewProfileCleaner, WebViewProfileCleaner>();
        services.AddSingleton<IWebViewSessionManager, WebViewSessionManager>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }
}
