using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Branding;
using UnifiedMessenger.App.Services.Mail;
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
    private CancellationTokenSource? _startupCancellation;
    private CancellationTokenSource? _startupMailUnreadCancellation;
    private StartupWindow? _startupWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _ = WindowsShellIdentity.TryInitializeProcess();
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
            WebViewRuntimeInfo runtimeInfo = window.DetectRuntimeForStartup();
            if (runtimeInfo.IsAvailable)
            {
                _startupCancellation = new CancellationTokenSource();
                _startupWindow = new StartupWindow();
                _startupWindow.ExitRequested += OnStartupExitRequested;
                _startupWindow.Show();

                Progress<StartupPrimeProgress> progress = new(_startupWindow.UpdateProgress);
                try
                {
                    await window.PrimeEnabledServicesBeforeShowAsync(
                        progress,
                        _startupCancellation.Token);
                }
                catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
                {
                    return;
                }

                if (_exitCoordinator.IsExiting)
                {
                    return;
                }

                window.CompleteStartupPrime();
                CloseStartupWindow();
            }

            window.Show();
            _trayCoordinator = _serviceProvider.GetRequiredService<IApplicationTrayCoordinator>();
            _trayCoordinator.Initialize();
            StartStartupMailUnreadRefresh(loadResult.Settings.MailAccounts);

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
                $"Не удалось запустить {BrandIdentity.DisplayName}.\n\n{exception.Message}",
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
        CloseStartupWindow();
        CancelStartupMailUnreadRefresh();
        _startupCancellation?.Dispose();
        _startupCancellation = null;
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exitCoordinator?.BeginSessionEnding();
        _trayCoordinator?.BeginShutdown();
        CancelStartupMailUnreadRefresh();
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

        _startupCancellation?.Cancel();
        CancelStartupMailUnreadRefresh();
        await ShutdownApplicationAsync();
    }

    private void OnStartupExitRequested(object? sender, EventArgs eventArgs) =>
        _exitCoordinator?.RequestExit();

    private async Task ShutdownApplicationAsync()
    {
        try
        {
            _trayCoordinator?.BeginShutdown();
            CloseStartupWindow();

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
        services.AddSingleton<IMailCredentialProtector, DpapiMailCredentialProtector>();
        services.AddSingleton<IMailCredentialStore, FileMailCredentialStore>();
        services.AddSingleton<IRemoteImageSenderTrustProtector, DpapiRemoteImageSenderTrustProtector>();
        services.AddSingleton<IRemoteImageSenderTrustStore, FileRemoteImageSenderTrustStore>();
        services.AddSingleton<IMailConnectionValidator, MailKitConnectionValidator>();
        services.AddSingleton(GmailOAuthOptions.Default);
        services.AddSingleton<IGoogleOAuthClientConfigurationSource, GoogleOAuthClientConfigurationSource>();
        services.AddSingleton<ISystemBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IOAuthStateGenerator, CryptographicOAuthStateGenerator>();
        services.AddSingleton<IOAuthLoopbackListenerFactory, OAuthLoopbackListenerFactory>();
        services.AddSingleton<IGoogleOAuthProtocolClient, GoogleOAuthProtocolClient>();
        services.AddSingleton<IGmailOAuthService, GmailOAuthService>();
        services.AddSingleton<IGmailScopeUpgradeService, GmailScopeUpgradeService>();
        services.AddSingleton<IMailProvider, GmailApiProvider>();
        services.AddSingleton<IMailProvider, YandexMailProvider>();
        services.AddSingleton<IMailProvider, MailRuMailProvider>();
        services.AddSingleton<IMailProvider, GenericImapMailProvider>();
        services.AddSingleton<IMailProviderFactory, MailProviderFactory>();
        services.AddSingleton<IMailAccountProvisioningService, MailAccountProvisioningService>();
        services.AddSingleton<IMailHtmlSanitizer, MailHtmlSanitizer>();
        services.AddSingleton<IMailContentExtractor, MailContentExtractor>();
        services.AddSingleton<IMailHtmlDocumentBuilder, MailHtmlDocumentBuilder>();
        services.AddSingleton<IRemoteMailImageHttpClient, RemoteMailImageHttpClient>();
        services.AddSingleton<IRemoteMailImageUriValidator, RemoteMailImageUriValidator>();
        services.AddSingleton<IRemoteMailImageLoader, RemoteMailImageLoader>();
        services.AddSingleton<IGmailApiReadClient, GmailApiReadClient>();
        services.AddSingleton<IImapInboxClient, MailKitImapInboxClient>();
        services.AddSingleton<MailMessageSourceCache>();
        services.AddSingleton<GmailMailReadProvider>();
        services.AddSingleton<ImapMailReadProvider>();
        services.AddSingleton<IMailReadProvider>(provider => provider.GetRequiredService<GmailMailReadProvider>());
        services.AddSingleton<IMailReadProvider>(provider => provider.GetRequiredService<ImapMailReadProvider>());
        services.AddSingleton<IMailAttachmentContentProvider>(provider => provider.GetRequiredService<GmailMailReadProvider>());
        services.AddSingleton<IMailAttachmentContentProvider>(provider => provider.GetRequiredService<ImapMailReadProvider>());
        services.AddSingleton<IMailAttachmentContentProviderFactory, MailAttachmentContentProviderFactory>();
        services.AddSingleton<IMailAttachmentDialogService, WpfMailAttachmentDialogService>();
        services.AddSingleton<IMailAttachmentSaveService, MailAttachmentSaveService>();
        services.AddSingleton<IMailOutgoingAttachmentMaterializer, MailOutgoingAttachmentMaterializer>();
        services.AddSingleton<IMailReadProviderFactory, MailReadProviderFactory>();
        services.AddSingleton<IStartupMailUnreadRefreshService, StartupMailUnreadRefreshService>();
        services.AddSingleton<IMailComposeRequestFactory, MailComposeRequestFactory>();
        services.AddSingleton<IMailComposePreparationService, MailComposePreparationService>();
        services.AddSingleton<IMailComposeConfirmationService, WpfMailComposeConfirmationService>();
        services.AddSingleton<IMailMimeMessageFactory, MailMimeMessageFactory>();
        services.AddSingleton<IGmailApiSendClient, GmailApiSendClient>();
        services.AddSingleton<ISmtpClientSessionFactory, MailKitSmtpClientSessionFactory>();
        services.AddSingleton<ISmtpSubmissionClient, MailKitSmtpSubmissionClient>();
        services.AddSingleton<IImapSentCopySessionFactory, MailKitImapSentCopySessionFactory>();
        services.AddSingleton<IImapSentCopyClient, MailKitImapSentCopyClient>();
        services.AddSingleton<IMailSendProvider, GmailMailSendProvider>();
        services.AddSingleton<IMailSendProvider, SmtpMailSendProvider>();
        services.AddSingleton<IMailSendProviderFactory, MailSendProviderFactory>();
        services.AddSingleton<INotificationSoundPlayer, WindowsNotificationSoundPlayer>();
        services.AddSingleton<INotificationPermissionPrompt, WpfNotificationPermissionPrompt>();
        services.AddSingleton<INotificationPermissionCoordinator, NotificationPermissionCoordinator>();
        services.AddSingleton<INotificationPopupService, WpfNotificationPopupService>();
        services.AddSingleton<ITrayIconService, WinFormsTrayIconService>();
        services.AddSingleton<ITaskbarActivityIndicator, WpfTaskbarActivityIndicator>();
        services.AddSingleton<IApplicationExitCoordinator, ApplicationExitCoordinator>();
        services.AddSingleton<IWebViewRuntimeService, WebViewRuntimeService>();
        services.AddSingleton<IExternalBrowserService, ExternalBrowserService>();
        services.AddSingleton<MailRendererNavigationPolicy>();
        services.AddSingleton<MailRendererNavigationCoordinator>();
        services.AddSingleton<IMailMessageHtmlRenderer, MailMessageHtmlRenderer>();
        services.AddSingleton<WebNavigationService>();
        services.AddSingleton<WebNewWindowNavigationService>();
        services.AddSingleton<IWebViewProfileCleaner, WebViewProfileCleaner>();
        services.AddSingleton<ICoreWebView2EnvironmentProvider, CoreWebView2EnvironmentProvider>();
        services.AddSingleton<WebViewSessionManager>();
        services.AddSingleton<IWebViewSessionManager>(provider =>
            provider.GetRequiredService<WebViewSessionManager>());
        services.AddSingleton<IWebViewStartupPrimeCoordinator, WebViewStartupPrimeCoordinator>();
        services.AddSingleton<ITelegramNotificationSoundCoordinator, TelegramNotificationSoundCoordinator>();
        services.AddSingleton<IWindowActivationService, WpfWindowActivationService>();
        services.AddSingleton<IWebNotificationCoordinator, WebNotificationCoordinator>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MailComposeViewModel>();
        services.AddSingleton<MailInboxViewModel>();
        services.AddSingleton<SettingsViewModel>();
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

    private void CloseStartupWindow()
    {
        if (_startupWindow is null)
        {
            return;
        }

        _startupWindow.ExitRequested -= OnStartupExitRequested;
        _startupWindow.CompleteAndClose();
        _startupWindow = null;
    }

    private void StartStartupMailUnreadRefresh(IEnumerable<MailAccount> accounts)
    {
        _startupMailUnreadCancellation = new CancellationTokenSource();
        IStartupMailUnreadRefreshService refreshService =
            _serviceProvider!.GetRequiredService<IStartupMailUnreadRefreshService>();
        _ = refreshService.RefreshAsync(accounts, _startupMailUnreadCancellation.Token);
    }

    private void CancelStartupMailUnreadRefresh()
    {
        _startupMailUnreadCancellation?.Cancel();
        _startupMailUnreadCancellation?.Dispose();
        _startupMailUnreadCancellation = null;
    }

}
