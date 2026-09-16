using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Branding;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfMessageBox = System.Windows.MessageBox;

namespace UnifiedMessenger.App.Views;

public partial class MainWindow : Window
{
    internal const int RemoteImageSessionCacheCapacity = 20;
    private readonly MainWindowViewModel _viewModel;
    private readonly MailInboxViewModel _mailInboxViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IWebViewSessionManager _webViewSessionManager;
    private readonly IWebViewStartupPrimeCoordinator _startupPrimeCoordinator;
    private readonly IWebViewRuntimeService _webViewRuntimeService;
    private readonly IExternalBrowserService _externalBrowserService;
    private readonly IApplicationExitCoordinator _exitCoordinator;
    private readonly IApplicationTrayCoordinator _trayCoordinator;
    private readonly IWindowActivationService _windowActivationService;
    private readonly ITaskbarActivityIndicator _taskbarActivityIndicator;
    private readonly IMailProviderFactory _mailProviderFactory;
    private readonly IMailAccountProvisioningService _mailAccountProvisioningService;
    private readonly IMailMessageHtmlRenderer _mailMessageHtmlRenderer;
    private readonly MailRendererWindowLifecycleCoordinator _mailRendererWindowLifecycle;
    private readonly IRemoteMailImageLoader _remoteMailImageLoader;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _deferredPrimeGate = new(1, 1);
    private readonly HashSet<Guid> _deferredPrimeServiceIds = [];
    private CancellationTokenSource? _selectionCancellation;
    private CancellationTokenSource? _mailRendererCancellation;
    private CancellationTokenSource? _remoteImageLoadCancellation;
    private IReadOnlyDictionary<string, MailImageContent> _loadedRemoteImages =
        new Dictionary<string, MailImageContent>(StringComparer.Ordinal);
    private readonly BoundedLruCache<RemoteImageCacheKey, IReadOnlyDictionary<string, MailImageContent>>
        _sessionRemoteImages = new(RemoteImageSessionCacheCapacity);
    private Guid? _remoteImageAccountId;
    private string? _remoteImageMessageKey;
    private HwndSource? _windowSource;
    private IntPtr _mainWindowHandle;
    private bool _isRuntimeAvailable;
    private bool _startupPrimeCompleted;
    private bool _restoreMailRendererAfterInteractiveMove;
    private long _mailRendererVersion;

    public MainWindow(
        MainWindowViewModel viewModel,
        MailInboxViewModel mailInboxViewModel,
        SettingsViewModel settingsViewModel,
        IWebViewSessionManager webViewSessionManager,
        IWebViewStartupPrimeCoordinator startupPrimeCoordinator,
        IWebViewRuntimeService webViewRuntimeService,
        IExternalBrowserService externalBrowserService,
        IApplicationExitCoordinator exitCoordinator,
        IApplicationTrayCoordinator trayCoordinator,
        IWindowActivationService windowActivationService,
        ITaskbarActivityIndicator taskbarActivityIndicator,
        IMailProviderFactory mailProviderFactory,
        IMailAccountProvisioningService mailAccountProvisioningService,
        IMailMessageHtmlRenderer mailMessageHtmlRenderer,
        IRemoteMailImageLoader remoteMailImageLoader)
    {
        _viewModel = viewModel;
        _mailInboxViewModel = mailInboxViewModel;
        _settingsViewModel = settingsViewModel;
        _webViewSessionManager = webViewSessionManager;
        _startupPrimeCoordinator = startupPrimeCoordinator;
        _webViewRuntimeService = webViewRuntimeService;
        _externalBrowserService = externalBrowserService;
        _exitCoordinator = exitCoordinator;
        _trayCoordinator = trayCoordinator;
        _windowActivationService = windowActivationService;
        _taskbarActivityIndicator = taskbarActivityIndicator;
        _mailProviderFactory = mailProviderFactory;
        _mailAccountProvisioningService = mailAccountProvisioningService;
        _mailMessageHtmlRenderer = mailMessageHtmlRenderer;
        _mailRendererWindowLifecycle = new MailRendererWindowLifecycleCoordinator(mailMessageHtmlRenderer);
        _remoteMailImageLoader = remoteMailImageLoader;
        _mailInboxViewModel.SetAutomaticallyShowRemoteImages(_viewModel.AutomaticallyShowRemoteImages);
        DataContext = viewModel;

        InitializeComponent();
        SettingsContent.DataContext = settingsViewModel;
        MailInboxContent.DataContext = mailInboxViewModel;
        _windowActivationService.Attach(
            this,
            () => _viewModel.IsSettingsOpen ? null : _viewModel.SelectedService?.Id,
            _viewModel.SelectService);
        _taskbarActivityIndicator.Attach(this);
        ApplySavedWindowSettings(viewModel.WindowSettings);
        Loaded += OnLoaded;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += OnWindowLocationChanged;
        StateChanged += OnWindowStateChanged;
        IsVisibleChanged += OnWindowVisibilityChanged;
        WebViewContainer.SizeChanged += OnWebViewContainerSizeChanged;
        MailInboxContent.HtmlRendererSurface.SizeChanged += OnMailRendererSurfaceSizeChanged;
        MailInboxContent.ShowRemoteImagesRequested += OnShowRemoteImagesRequested;
        MailInboxContent.AlwaysShowRemoteImagesFromSenderRequested += OnAlwaysShowRemoteImagesFromSenderRequested;
        MailInboxContent.RevokeRemoteImagesFromSenderRequested += OnRevokeRemoteImagesFromSenderRequested;
        MailInboxContent.PrintRequested += OnPrintRequested;
        _mailMessageHtmlRenderer.PrintAvailabilityChanged += OnMailRendererPrintAvailabilityChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _mailInboxViewModel.PropertyChanged += OnMailInboxViewModelPropertyChanged;
        _viewModel.SelectedServiceChanged += OnSelectedServiceChanged;
        _settingsViewModel.RenameAccountRequested += OnSettingsRenameAccountRequested;
        _settingsViewModel.AccountEnabledChangeRequested += OnSettingsAccountEnabledChangeRequested;
        _settingsViewModel.DeleteAccountRequested += OnSettingsDeleteAccountRequested;
        _settingsViewModel.AddMailAccountRequested += OnSettingsAddMailAccountRequested;
        _settingsViewModel.RenameMailAccountRequested += OnSettingsRenameMailAccountRequested;
        _settingsViewModel.ChangeMailAccountPasswordRequested += OnSettingsChangeMailAccountPasswordRequested;
        _settingsViewModel.MailAccountEnabledChangeRequested += OnSettingsMailAccountEnabledChangeRequested;
        _settingsViewModel.DeleteMailAccountRequested += OnSettingsDeleteMailAccountRequested;
        _webViewSessionManager.SessionRecreationRequested += OnSessionRecreationRequested;
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        Activated -= OnActivated;
        Deactivated -= OnDeactivated;
        SourceInitialized -= OnSourceInitialized;
        LocationChanged -= OnWindowLocationChanged;
        StateChanged -= OnWindowStateChanged;
        IsVisibleChanged -= OnWindowVisibilityChanged;
        WebViewContainer.SizeChanged -= OnWebViewContainerSizeChanged;
        MailInboxContent.HtmlRendererSurface.SizeChanged -= OnMailRendererSurfaceSizeChanged;
        MailInboxContent.ShowRemoteImagesRequested -= OnShowRemoteImagesRequested;
        MailInboxContent.AlwaysShowRemoteImagesFromSenderRequested -= OnAlwaysShowRemoteImagesFromSenderRequested;
        MailInboxContent.RevokeRemoteImagesFromSenderRequested -= OnRevokeRemoteImagesFromSenderRequested;
        MailInboxContent.PrintRequested -= OnPrintRequested;
        _mailMessageHtmlRenderer.PrintAvailabilityChanged -= OnMailRendererPrintAvailabilityChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _mailInboxViewModel.PropertyChanged -= OnMailInboxViewModelPropertyChanged;
        _viewModel.SelectedServiceChanged -= OnSelectedServiceChanged;
        _settingsViewModel.RenameAccountRequested -= OnSettingsRenameAccountRequested;
        _settingsViewModel.AccountEnabledChangeRequested -= OnSettingsAccountEnabledChangeRequested;
        _settingsViewModel.DeleteAccountRequested -= OnSettingsDeleteAccountRequested;
        _settingsViewModel.AddMailAccountRequested -= OnSettingsAddMailAccountRequested;
        _settingsViewModel.RenameMailAccountRequested -= OnSettingsRenameMailAccountRequested;
        _settingsViewModel.ChangeMailAccountPasswordRequested -= OnSettingsChangeMailAccountPasswordRequested;
        _settingsViewModel.MailAccountEnabledChangeRequested -= OnSettingsMailAccountEnabledChangeRequested;
        _settingsViewModel.DeleteMailAccountRequested -= OnSettingsDeleteMailAccountRequested;
        _webViewSessionManager.SessionRecreationRequested -= OnSessionRecreationRequested;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        _mailRendererCancellation?.Cancel();
        _mailRendererCancellation?.Dispose();
        _mailRendererCancellation = null;
        _remoteImageLoadCancellation?.Cancel();
        _remoteImageLoadCancellation?.Dispose();
        _remoteImageLoadCancellation = null;
        _mailInboxViewModel.SetDetailHostActive(false);
        _loadedRemoteImages = new Dictionary<string, MailImageContent>(StringComparer.Ordinal);
        _sessionRemoteImages.Clear();
        _lifetimeCancellation.Cancel();

        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _webViewSessionManager.ReleaseAllSessions();
        _mailMessageHtmlRenderer.BeginShutdown();
        _mailInboxViewModel.Dispose();
        _lifetimeCancellation.Dispose();
        _windowActivationService.Detach(this);
        _taskbarActivityIndicator.Detach(this);

        Rect bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        _viewModel.UpdateWindowSettings(
            bounds.Width,
            bounds.Height,
            bounds.Left,
            bounds.Top,
            WindowState == WindowState.Maximized);
        base.OnClosed(eventArgs);
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        _mailInboxViewModel.SetDetailHostActive(false);
        bool shutdownStarted = Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished;
        if (_exitCoordinator.ShouldHideToTray(_viewModel.CloseToTray, shutdownStarted))
        {
            eventArgs.Cancel = true;
            Hide();
            _ = ShowTrayHintOnceAsync();
        }
        else if (_exitCoordinator.ShouldRequestExitFromWindowClose(_viewModel.CloseToTray, shutdownStarted))
        {
            eventArgs.Cancel = true;
            _exitCoordinator.RequestExit();
        }

        base.OnClosing(eventArgs);
    }

    private async Task ShowTrayHintOnceAsync()
    {
        try
        {
            if (!_viewModel.HasShownTrayHint && _trayCoordinator.TryShowCloseToTrayHint())
            {
                await _viewModel.MarkTrayHintShownAsync();
            }
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            // Closing to tray must remain available even if the hint state could not be saved.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        _mainWindowHandle = new WindowInteropHelper(this).Handle;

        if (_startupPrimeCompleted)
        {
            UpdateDirectSurface(moveFocus: false);
            await _mailInboxViewModel.ActivateAsync(
                _viewModel.SelectedMailAccount,
                _lifetimeCancellation.Token);
            UpdateMailDetailActivity();
            return;
        }

        WebViewRuntimeInfo runtimeInfo = _webViewRuntimeService.DetectRuntime();
        _viewModel.SetRuntimeInfo(runtimeInfo);
        if (!runtimeInfo.IsAvailable)
        {
            ShowMissingRuntimeDialog();
            await _mailInboxViewModel.ActivateAsync(
                _viewModel.SelectedMailAccount,
                _lifetimeCancellation.Token);
            UpdateMailDetailActivity();
            return;
        }

        _isRuntimeAvailable = true;
        await ShowSelectedServiceAsync();
        await _mailInboxViewModel.ActivateAsync(
            _viewModel.SelectedMailAccount,
            _lifetimeCancellation.Token);
        UpdateMailDetailActivity();
    }

    private async void OnSelectedServiceChanged(object? sender, EventArgs eventArgs)
    {
        UpdateMailDetailActivity();
        try
        {
            Task mailActivation = _mailInboxViewModel.ActivateAsync(
                _viewModel.SelectedMailAccount,
                _lifetimeCancellation.Token);
            if (_viewModel.SelectedService is not null)
            {
                _mailRendererWindowLifecycle.Deactivate(clearContent: true);
            }
            else
            {
                UpdateMailRendererSurface();
            }

            _viewModel.MarkSelectedServiceViewed(IsVisible, IsActive);
            await _viewModel.PersistSelectionAsync();
            await ShowSelectedServiceAsync();
            await mailActivation;
            UpdateMailDetailActivity();
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось переключить аккаунт", exception);
        }
    }

    private void OnActivated(object? sender, EventArgs eventArgs) =>
        HandleWindowActivated();

    private void HandleWindowActivated()
    {
        _viewModel.MarkSelectedServiceViewed(IsVisible, IsActive);
        UpdateDirectSurface(moveFocus: true);
        UpdateMailDetailActivity();
    }

    private void OnDeactivated(object? sender, EventArgs eventArgs) =>
        UpdateMailDetailActivity();

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.AutomaticallyShowRemoteImages))
        {
            _mailInboxViewModel.SetAutomaticallyShowRemoteImages(
                _viewModel.AutomaticallyShowRemoteImages);
            if (_viewModel.AutomaticallyShowRemoteImages)
            {
                await LoadCurrentRemoteImagesAsync();
            }

            return;
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen)
            || eventArgs.PropertyName == nameof(MainWindowViewModel.WebViewStatus))
        {
            UpdateDirectSurface(moveFocus: false);
            UpdateMailRendererSurface();
            UpdateMailDetailActivity();
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen)
                && !_viewModel.IsSettingsOpen)
            {
                _viewModel.MarkSelectedServiceViewed(IsVisible, IsActive);
                if (_viewModel.SelectedService is ServiceInstance selected
                    && _deferredPrimeServiceIds.Contains(selected.Id))
                {
                    _ = InitializeSelectedServiceAfterSettingsAsync();
                }
            }
        }
    }

    private async void OnMailInboxViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        UpdateMailDetailActivity();
        if (eventArgs.PropertyName == nameof(MailInboxViewModel.IsComposeOpen))
        {
            _mailRendererWindowLifecycle.SetComposeActive(_mailInboxViewModel.IsComposeOpen);
        }

        if (ShouldLoadTrustedRemoteImages(_mailInboxViewModel, eventArgs.PropertyName))
        {
            await LoadCurrentRemoteImagesAsync();
            return;
        }

        bool shouldLoadAutomaticRemoteImages = ShouldLoadAutomaticRemoteImages(
            _mailInboxViewModel,
            eventArgs.PropertyName);

        if (!ShouldRefreshMailRendererContent(_mailInboxViewModel, eventArgs.PropertyName))
        {
            if (shouldLoadAutomaticRemoteImages)
            {
                await LoadCurrentRemoteImagesAsync();
            }

            return;
        }

        await RefreshMailRendererContentAsync();
        if (shouldLoadAutomaticRemoteImages)
        {
            await LoadCurrentRemoteImagesAsync();
        }
    }

    internal static bool ShouldRefreshMailRendererContent(
        MailInboxViewModel viewModel,
        string? propertyName) =>
        propertyName switch
        {
            nameof(MailInboxViewModel.SelectedMessageContent) => !viewModel.IsReadStateMetadataUpdate,
            nameof(MailInboxViewModel.ActiveAccount)
                or nameof(MailInboxViewModel.IsMessageLoading)
                or nameof(MailInboxViewModel.IsComposeOpen)
                or nameof(MailInboxViewModel.IsMessageDetailVisible) => true,
            _ => false
        };

    internal static bool ShouldLoadTrustedRemoteImages(
        MailInboxViewModel viewModel,
        string? propertyName) =>
        propertyName == nameof(MailInboxViewModel.IsCurrentRemoteImageSenderTrusted)
        && !viewModel.AutomaticallyShowRemoteImages
        && viewModel.IsCurrentRemoteImageSenderTrusted
        && !viewModel.AreRemoteImagesShown;

    internal static bool ShouldLoadAutomaticRemoteImages(
        MailInboxViewModel viewModel,
        string? propertyName) =>
        propertyName == nameof(MailInboxViewModel.SelectedMessageContent)
        && viewModel.AutomaticallyShowRemoteImages
        && viewModel.IsMessageDetailVisible
        && viewModel.SelectedMessageContent?.HasRemoteImages == true
        && !viewModel.AreRemoteImagesShown;

    private async Task InitializeSelectedServiceAfterSettingsAsync()
    {
        try
        {
            await ShowSelectedServiceAsync();
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось инициализировать аккаунт", exception);
        }
    }

    internal WebViewRuntimeInfo DetectRuntimeForStartup()
    {
        WebViewRuntimeInfo runtimeInfo = _webViewRuntimeService.DetectRuntime();
        _viewModel.SetRuntimeInfo(runtimeInfo);
        _isRuntimeAvailable = runtimeInfo.IsAvailable;
        return runtimeInfo;
    }

    internal async Task<StartupPrimeResult> PrimeEnabledServicesBeforeShowAsync(
        IProgress<StartupPrimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_isRuntimeAvailable)
        {
            return StartupPrimeResult.Empty;
        }

        _mainWindowHandle = new WindowInteropHelper(this).EnsureHandle();
        Rectangle bounds = GetPreShowDirectWebViewBounds();
        return await _startupPrimeCoordinator.PrimeAsync(
            _mainWindowHandle,
            bounds,
            _viewModel.Services,
            _viewModel.SelectedService?.Id,
            progress,
            cancellationToken);
    }

    internal void CompleteStartupPrime()
    {
        if (_viewModel.SelectedService is ServiceInstance selected && selected.IsEnabled)
        {
            _webViewSessionManager.ActivateSession(
                selected.Id,
                GetPreShowDirectWebViewBounds(),
                isVisible: true,
                moveFocus: false);
        }

        _startupPrimeCompleted = true;
    }

    internal int InitializedControllerCount => _webViewSessionManager.InitializedSessionCount;
    internal int InitialNavigationCount => _webViewSessionManager.InitialNavigationCount;

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        _mainWindowHandle = new WindowInteropHelper(this).Handle;
        _ = WindowsShellIdentity.TryApplyToWindow(_mainWindowHandle);
        _windowSource = HwndSource.FromHwnd(_mainWindowHandle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private void OnWindowLocationChanged(object? sender, EventArgs eventArgs)
    {
        _webViewSessionManager.NotifyParentWindowPositionChanged();
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs)
    {
        UpdateDirectSurface(moveFocus: false);
        UpdateMailRendererSurface();
        UpdateMailDetailActivity();
    }

    private async void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        UpdateDirectSurface(moveFocus: false);
        UpdateMailRendererSurface();
        UpdateMailDetailActivity();
        if (!IsVisible && !_lifetimeCancellation.IsCancellationRequested)
        {
            await PrimeDeferredServicesWhileHiddenAsync(_lifetimeCancellation.Token);
        }
    }

    private void OnWebViewContainerSizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        UpdateDirectSurface(moveFocus: false);

    private void OnMailRendererSurfaceSizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        UpdateMailRendererSurface();

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WindowPositionChanged = 0x0047;
        const int DpiChanged = 0x02E0;
        const int EnterSizeMove = 0x0231;
        const int ExitSizeMove = 0x0232;
        if (message == EnterSizeMove)
        {
            _restoreMailRendererAfterInteractiveMove =
                _mailRendererWindowLifecycle.ReleaseForInteractiveMove();
        }
        else if (message == ExitSizeMove && _restoreMailRendererAfterInteractiveMove)
        {
            _restoreMailRendererAfterInteractiveMove = false;
            _ = RefreshMailRendererContentAsync();
        }

        if (message is WindowPositionChanged or DpiChanged)
        {
            _webViewSessionManager.NotifyParentWindowPositionChanged();
            _mailRendererWindowLifecycle.NotifyParentWindowPositionChanged();
            Dispatcher.BeginInvoke(() =>
            {
                UpdateDirectSurface(moveFocus: false);
                if (message == DpiChanged)
                {
                    UpdateMailRendererSurface();
                }
            });
        }

        return IntPtr.Zero;
    }

    private async void OnSessionRecreationRequested(
        object? sender,
        WebViewSessionRecreationRequestedEventArgs eventArgs)
    {
        if (_viewModel.SelectedService?.Id != eventArgs.ServiceInstanceId)
        {
            return;
        }

        await ShowSelectedServiceAsync(recreate: true);
    }

    private async Task ShowSelectedServiceAsync(bool recreate = false)
    {
        if (!_isRuntimeAvailable || _lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        CancellationTokenSource selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _selectionCancellation = selectionCancellation;
        CancellationToken cancellationToken = selectionCancellation.Token;

        ServiceInstance? service = _viewModel.SelectedService;
        _webViewSessionManager.DeactivateSession();

        if (service is null || !service.IsEnabled)
        {
            CompleteSelectionOperation(selectionCancellation);
            return;
        }

        try
        {
            if (recreate)
            {
                _webViewSessionManager.ReleaseSession(service.Id);
            }

            _deferredPrimeServiceIds.Remove(service.Id);
            Rectangle bounds = await WebViewSurfaceBoundsReadiness.GetReadyBoundsAsync(
                () => WebViewContainer.ActualWidth > 0 && WebViewContainer.ActualHeight > 0,
                WaitForWebViewSurfaceLayoutAsync,
                GetDirectWebViewBounds,
                cancellationToken);
            await _webViewSessionManager.InitializeAsync(
                _mainWindowHandle,
                bounds,
                service,
                activate: true,
                cancellationToken);
            UpdateDirectSurface(moveFocus: true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowMissingRuntimeDialog();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Another account was selected or the window is closing.
        }
        finally
        {
            CompleteSelectionOperation(selectionCancellation);
        }
    }

    private async void AddService_Click(object sender, RoutedEventArgs eventArgs)
    {
        AddServiceWindow dialog = new(_viewModel.AvailableServices) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _viewModel.AddServiceAsync(dialog.SelectedServiceType, dialog.AccountName);
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось добавить сервис", exception);
        }
    }

    private void OnSettingsAddMailAccountRequested(object? sender, EventArgs eventArgs)
    {
        AddMailAccountWindow dialog = new(_mailProviderFactory, _mailAccountProvisioningService)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.ConnectedAccount is MailAccount account)
        {
            _viewModel.AddConnectedMailAccount(account);
        }
    }

    private async void RenameAccount_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetNavigationItem(sender, out NavigationAccountItem item))
        {
            return;
        }

        if (item.Service is ServiceInstance service)
        {
            await RenameAccountAsync(service);
        }
        else if (item.MailAccount is MailAccount mailAccount)
        {
            await RenameMailAccountAsync(mailAccount);
        }
    }

    private async void OnSettingsRenameAccountRequested(object? sender, SettingsAccountEventArgs eventArgs) =>
        await RenameAccountAsync(eventArgs.Service);

    private async Task RenameAccountAsync(ServiceInstance service)
    {
        RenameAccountWindow dialog = new(service.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _viewModel.RenameServiceAsync(service, dialog.AccountName);
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось переименовать аккаунт", exception);
        }
    }

    private async void OnSettingsRenameMailAccountRequested(
        object? sender,
        SettingsMailAccountEventArgs eventArgs) =>
        await RenameMailAccountAsync(eventArgs.Account);

    private async Task RenameMailAccountAsync(MailAccount account)
    {
        RenameAccountWindow dialog = new(account.DisplayLabel) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _viewModel.RenameMailAccountAsync(account, dialog.AccountName);
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось переименовать почтовый аккаунт", exception);
        }
    }

    private async void OnSettingsChangeMailAccountPasswordRequested(
        object? sender,
        SettingsMailAccountEventArgs eventArgs)
    {
        MailAccount account = eventArgs.Account;
        ChangeMailAppPasswordWindow dialog = new(account, _mailAccountProvisioningService)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _mailInboxViewModel.RefreshAfterPasswordReplacementAsync(
                account,
                _lifetimeCancellation.Token);
            WpfMessageBox.Show(
                this,
                "Новый пароль приложения проверен и сохранён в защищённом хранилище Windows.",
                "Пароль приложения обновлён",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The application is closing after the credential was already replaced.
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Пароль сохранён, но почту не удалось обновить", exception);
        }
    }

    private async void ToggleAccount_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetNavigationItem(sender, out NavigationAccountItem item))
        {
            return;
        }

        if (item.Service is ServiceInstance service)
        {
            await SetAccountEnabledAsync(service, !service.IsEnabled);
        }
        else if (item.MailAccount is MailAccount mailAccount)
        {
            await SetMailAccountEnabledAsync(mailAccount, !mailAccount.IsEnabled);
        }
    }

    private async void OnSettingsAccountEnabledChangeRequested(
        object? sender,
        SettingsAccountEnabledEventArgs eventArgs) =>
        await SetAccountEnabledAsync(eventArgs.Service, eventArgs.IsEnabled);

    private async void OnSettingsMailAccountEnabledChangeRequested(
        object? sender,
        SettingsMailAccountEnabledEventArgs eventArgs) =>
        await SetMailAccountEnabledAsync(eventArgs.Account, eventArgs.IsEnabled);

    private async Task SetMailAccountEnabledAsync(MailAccount account, bool isEnabled)
    {
        try
        {
            await _viewModel.SetMailAccountEnabledAsync(account, isEnabled);
            if (_viewModel.SelectedMailAccount?.Id == account.Id)
            {
                await _mailInboxViewModel.ActivateAsync(account, _lifetimeCancellation.Token);
            }
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError(
                isEnabled ? "Не удалось включить почтовый аккаунт" : "Не удалось отключить почтовый аккаунт",
                exception);
        }
    }

    private async void EnableSelectedAccount_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_viewModel.SelectedService is ServiceInstance service)
        {
            await SetAccountEnabledAsync(service, isEnabled: true);
        }
    }

    private async Task SetAccountEnabledAsync(ServiceInstance service, bool isEnabled)
    {
        try
        {
            if (!isEnabled)
            {
                _deferredPrimeServiceIds.Remove(service.Id);
            }

            await _viewModel.SetServiceEnabledAsync(service, isEnabled);
            if (_viewModel.SelectedService?.Id == service.Id)
            {
                if (isEnabled
                    && IsVisible
                    && _viewModel.IsSettingsOpen
                    && !_webViewSessionManager.IsSessionInitialized(service.Id))
                {
                    _deferredPrimeServiceIds.Add(service.Id);
                }
                else
                {
                    await ShowSelectedServiceAsync();
                }
            }
            else if (isEnabled)
            {
                EnabledAccountInitializationAction action = EnabledAccountInitializationPolicy.Decide(
                    service.IsEnabled,
                    _webViewSessionManager.IsSessionInitialized(service.Id),
                    IsVisible);
                if (action is EnabledAccountInitializationAction.PrimeWhileHidden)
                {
                    _deferredPrimeServiceIds.Add(service.Id);
                    await PrimeDeferredServicesWhileHiddenAsync(_lifetimeCancellation.Token);
                }
                else if (action is EnabledAccountInitializationAction.WaitForSelectionOrHiddenState)
                {
                    _deferredPrimeServiceIds.Add(service.Id);
                }
            }
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError(isEnabled ? "Не удалось включить аккаунт" : "Не удалось отключить аккаунт", exception);
        }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetNavigationItem(sender, out NavigationAccountItem item))
        {
            return;
        }

        if (item.Service is ServiceInstance service)
        {
            await DeleteAccountAsync(service);
        }
        else if (item.MailAccount is MailAccount mailAccount)
        {
            await DeleteMailAccountAsync(mailAccount);
        }
    }

    private async void OnSettingsDeleteAccountRequested(object? sender, SettingsAccountEventArgs eventArgs) =>
        await DeleteAccountAsync(eventArgs.Service);

    private async void OnSettingsDeleteMailAccountRequested(
        object? sender,
        SettingsMailAccountEventArgs eventArgs) =>
        await DeleteMailAccountAsync(eventArgs.Account);

    private async Task DeleteMailAccountAsync(MailAccount account)
    {
        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            $"Удалить почтовый аккаунт «{account.DisplayLabel}»?\n\nСохранённые учётные данные этого почтового аккаунта будут удалены из защищённого хранилища Windows.",
            "Удаление почтового аккаунта",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _mailInboxViewModel.DeleteRemoteImageSenderTrustAsync(
                account.Id,
                _lifetimeCancellation.Token);
            await _mailAccountProvisioningService.DeleteAsync(account.Id, _lifetimeCancellation.Token);
            _mailInboxViewModel.RemoveAccount(account.Id);
            _sessionRemoteImages.RemoveWhere(key => key.AccountId == account.Id);

            _viewModel.RemoveMailAccountFromNavigation(account);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось удалить почтовый аккаунт", exception);
        }
    }

    private async Task DeleteAccountAsync(ServiceInstance service)
    {
        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            $"Удалить аккаунт «{service.DisplayName}»?\n\nДанные только этого профиля будут очищены. При повторном добавлении потребуется новая авторизация.",
            "Удаление аккаунта",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _deferredPrimeServiceIds.Remove(service.Id);
            bool profileWasDeleted = await _webViewSessionManager.ClearProfileAsync(
                service,
                _lifetimeCancellation.Token);
            await _viewModel.RemoveServiceAsync(service, profileWasDeleted);

            if (!profileWasDeleted)
            {
                WpfMessageBox.Show(
                    this,
                    "Профиль занят процессом WebView2 и будет безопасно удалён при следующем запуске.",
                    "Удаление отложено",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось удалить аккаунт", exception);
        }
    }

    private async void MoveAccountUp_Click(object sender, RoutedEventArgs eventArgs) =>
        await MoveAccountAsync(sender, -1);

    private async void MoveAccountDown_Click(object sender, RoutedEventArgs eventArgs) =>
        await MoveAccountAsync(sender, 1);

    private async Task MoveAccountAsync(object sender, int offset)
    {
        if (!TryGetMenuService(sender, out ServiceInstance service))
        {
            return;
        }

        try
        {
            await _viewModel.MoveServiceAsync(service, offset);
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось изменить порядок аккаунтов", exception);
        }
    }

    private static bool TryGetMenuService(object sender, out ServiceInstance service)
    {
        service = (sender as WpfMenuItem)?.CommandParameter switch
        {
            ServiceInstance direct => direct,
            NavigationAccountItem item => item.Service ?? null!,
            _ => null!
        };
        return service is not null;
    }

    private static bool TryGetNavigationItem(object sender, out NavigationAccountItem item)
    {
        item = (sender as WpfMenuItem)?.CommandParameter as NavigationAccountItem ?? null!;
        return item is not null;
    }

    private async Task PrimeDeferredServicesWhileHiddenAsync(CancellationToken cancellationToken)
    {
        if (IsVisible
            || !_isRuntimeAvailable
            || _mainWindowHandle == IntPtr.Zero
            || _deferredPrimeServiceIds.Count == 0)
        {
            return;
        }

        await _deferredPrimeGate.WaitAsync(cancellationToken);
        try
        {
            if (IsVisible || _deferredPrimeServiceIds.Count == 0)
            {
                return;
            }

            ServiceInstance[] services = _viewModel.Services
                .Where(service => service.IsEnabled && _deferredPrimeServiceIds.Contains(service.Id))
                .ToArray();
            StartupPrimeResult result = await _startupPrimeCoordinator.PrimeAsync(
                _mainWindowHandle,
                GetDirectWebViewBounds(),
                services,
                selectedServiceId: null,
                progress: null,
                cancellationToken);

            foreach (Guid serviceId in result.AttemptedServiceIds)
            {
                _deferredPrimeServiceIds.Remove(serviceId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown cancels deferred initialization without creating more UI.
        }
        finally
        {
            _deferredPrimeGate.Release();
        }
    }

    private void UpdateDirectSurface(bool moveFocus)
    {
        if (!_isRuntimeAvailable
            || _mainWindowHandle == IntPtr.Zero
            || WebViewContainer.ActualWidth <= 0
            || WebViewContainer.ActualHeight <= 0)
        {
            return;
        }

        Rectangle bounds = GetDirectWebViewBounds();
        bool isVisible = IsVisible
            && WindowState != WindowState.Minimized
            && !_viewModel.IsSettingsOpen
            && !_viewModel.HasWebViewError
            && _viewModel.SelectedService?.IsEnabled == true;

        if (_viewModel.SelectedService is ServiceInstance selected
            && _webViewSessionManager.IsSessionInitialized(selected.Id))
        {
            _webViewSessionManager.ActivateSession(selected.Id, bounds, isVisible, moveFocus);
        }
        else
        {
            _webViewSessionManager.UpdateActiveSessionLayout(bounds, isVisible: false);
        }
    }

    private async Task RefreshMailRendererContentAsync()
    {
        long version = ++_mailRendererVersion;
        _mailRendererCancellation?.Cancel();
        _mailRendererCancellation?.Dispose();
        _mailRendererCancellation = null;

        MailMessageContent? content = _mailInboxViewModel.SelectedMessageContent;
        MailAccount? account = _mailInboxViewModel.ActiveAccount;
        ResetRemoteImagesWhenSelectionChanges(account?.Id, content?.MessageKey);
        if (!_isRuntimeAvailable
            || _mailInboxViewModel.IsMessageLoading
            || _mailInboxViewModel.IsComposeOpen
            || !_mailInboxViewModel.IsMessageDetailVisible
            || content?.BodyKind is not MailMessageBodyKind.SanitizedHtml
            || _mailInboxViewModel.ActiveAccount is null
            || _viewModel.SelectedMailAccount?.Id != _mailInboxViewModel.ActiveAccount.Id)
        {
            _mailMessageHtmlRenderer.Hide(clearContent: true);
            return;
        }

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _mailRendererCancellation = cancellation;
        try
        {
            await Dispatcher.InvokeAsync(
                () => ServiceWorkspace.UpdateLayout(),
                DispatcherPriority.Loaded,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (MailInboxContent.HtmlRendererSurface.ActualWidth <= 0
                || MailInboxContent.HtmlRendererSurface.ActualHeight <= 0)
            {
                return;
            }

            Rectangle bounds = GetElementClientBounds(MailInboxContent.HtmlRendererSurface);
            await _mailMessageHtmlRenderer.ShowAsync(
                _mainWindowHandle,
                bounds,
                content,
                _loadedRemoteImages,
                ShouldShowMailRenderer(),
                cancellation.Token);

            if (version != _mailRendererVersion
                || !ReferenceEquals(content, _mailInboxViewModel.SelectedMessageContent))
            {
                _mailMessageHtmlRenderer.Hide(clearContent: true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer account/message selection or shutdown owns the renderer.
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            _mailMessageHtmlRenderer.Hide(clearContent: true);
        }
        finally
        {
            if (ReferenceEquals(_mailRendererCancellation, cancellation))
            {
                _mailRendererCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async void OnShowRemoteImagesRequested(object? sender, EventArgs eventArgs) =>
        await LoadCurrentRemoteImagesAsync();

    private async void OnAlwaysShowRemoteImagesFromSenderRequested(object? sender, EventArgs eventArgs)
    {
        try
        {
            await _mailInboxViewModel.TrustCurrentRemoteImageSenderAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось сохранить доверие к отправителю", exception);
        }
    }

    private async void OnRevokeRemoteImagesFromSenderRequested(object? sender, EventArgs eventArgs)
    {
        try
        {
            await _mailInboxViewModel.RevokeCurrentRemoteImageSenderTrustAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The application is closing.
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось отменить доверие к отправителю", exception);
        }
    }

    private void OnMailRendererPrintAvailabilityChanged(object? sender, EventArgs eventArgs) =>
        _mailInboxViewModel.SetPrintAvailable(
            _mailMessageHtmlRenderer.CanPrint && ShouldShowMailRenderer());

    private void OnPrintRequested(object? sender, EventArgs eventArgs)
    {
        if (!_mailInboxViewModel.CanPrintMessage
            || !_mailMessageHtmlRenderer.TryShowPrintPreview())
        {
            WpfMessageBox.Show(
                this,
                "Не удалось открыть окно печати.",
                "Печать",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task LoadCurrentRemoteImagesAsync()
    {
        MailAccount? account = _mailInboxViewModel.ActiveAccount;
        MailMessageContent? content = _mailInboxViewModel.SelectedMessageContent;
        if (account is null
            || content?.BodyKind is not MailMessageBodyKind.SanitizedHtml
            || !content.HasRemoteImages
            || _viewModel.SelectedMailAccount?.Id != account.Id)
        {
            return;
        }

        ResetRemoteImagesWhenSelectionChanges(account.Id, content.MessageKey);
        _remoteImageLoadCancellation?.Cancel();
        _remoteImageLoadCancellation?.Dispose();
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _remoteImageLoadCancellation = cancellation;
        _mailInboxViewModel.SetRemoteImageLoading(true);

        try
        {
            IReadOnlyDictionary<string, MailImageContent> images = await _remoteMailImageLoader.LoadAsync(
                content.RemoteImages,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRemoteImageMessage(account.Id, content.MessageKey)
                || !ReferenceEquals(content, _mailInboxViewModel.SelectedMessageContent))
            {
                return;
            }

            _loadedRemoteImages = images;
            _sessionRemoteImages.Set(new RemoteImageCacheKey(account.Id, content.MessageKey), images);
            _mailInboxViewModel.MarkRemoteImagesShown();
            await RefreshMailRendererContentAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Another message selection or shutdown owns remote-image consent.
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            // Keep the banner available so the user can explicitly retry.
        }
        finally
        {
            if (ReferenceEquals(_remoteImageLoadCancellation, cancellation))
            {
                _remoteImageLoadCancellation = null;
                cancellation.Dispose();
                if (IsCurrentRemoteImageMessage(account.Id, content.MessageKey))
                {
                    _mailInboxViewModel.SetRemoteImageLoading(false);
                }
            }
        }
    }

    private void ResetRemoteImagesWhenSelectionChanges(Guid? accountId, string? messageKey)
    {
        if (_remoteImageAccountId == accountId
            && string.Equals(_remoteImageMessageKey, messageKey, StringComparison.Ordinal))
        {
            return;
        }

        _remoteImageLoadCancellation?.Cancel();
        _remoteImageLoadCancellation?.Dispose();
        _remoteImageLoadCancellation = null;
        IReadOnlyDictionary<string, MailImageContent>? cachedImages = null;
        bool hasCachedImages = accountId is Guid currentAccountId
            && !string.IsNullOrWhiteSpace(messageKey)
            && _sessionRemoteImages.TryGet(
                new RemoteImageCacheKey(currentAccountId, messageKey),
                out cachedImages);
        _loadedRemoteImages = hasCachedImages
            ? cachedImages!
            : new Dictionary<string, MailImageContent>(StringComparer.Ordinal);
        if (!hasCachedImages
            && accountId is Guid missingAccountId
            && !string.IsNullOrWhiteSpace(messageKey))
        {
            _mailInboxViewModel.ForgetRemoteImagesShown(missingAccountId, messageKey);
        }
        _remoteImageAccountId = accountId;
        _remoteImageMessageKey = messageKey;
    }

    private bool IsCurrentRemoteImageMessage(Guid accountId, string messageKey) =>
        _remoteImageAccountId == accountId
        && string.Equals(_remoteImageMessageKey, messageKey, StringComparison.Ordinal);

    private readonly record struct RemoteImageCacheKey(Guid AccountId, string MessageKey);

    private void UpdateMailRendererSurface()
    {
        _mailRendererWindowLifecycle.UpdateSurface(
            ShouldShowMailRenderer(),
            () => MailInboxContent.HtmlRendererSurface.ActualWidth <= 0
                || MailInboxContent.HtmlRendererSurface.ActualHeight <= 0
                    ? null
                    : GetElementClientBounds(MailInboxContent.HtmlRendererSurface));
    }

    private bool ShouldShowMailRenderer() =>
        MailRendererVisibilityPolicy.ShouldShow(
            IsVisible,
            WindowState == WindowState.Minimized,
            _viewModel.IsSettingsOpen,
            _viewModel.SelectedService is not null,
            _mailInboxViewModel.ActiveAccount is { IsEnabled: true }
                && _viewModel.SelectedMailAccount?.Id == _mailInboxViewModel.ActiveAccount.Id,
            _mailInboxViewModel.IsComposeOpen || !_mailInboxViewModel.IsMessageDetailVisible
                ? null
                : _mailInboxViewModel.SelectedMessageContent?.BodyKind);

    private void UpdateMailDetailActivity()
    {
        bool isActive = IsVisible
            && IsActive
            && WindowState != WindowState.Minimized
            && !_viewModel.IsSettingsOpen
            && _viewModel.SelectedService is null
            && _viewModel.SelectedMailAccount?.Id == _mailInboxViewModel.ActiveAccount?.Id;
        _mailInboxViewModel.SetDetailHostActive(isActive);
    }

    private Rectangle GetDirectWebViewBounds()
        => GetElementClientBounds(WebViewContainer);

    private Rectangle GetElementClientBounds(FrameworkElement element)
    {
        System.Windows.Point screenTopLeft = element.PointToScreen(new System.Windows.Point(0, 0));
        System.Windows.Point screenBottomRight = element.PointToScreen(
            new System.Windows.Point(element.ActualWidth, element.ActualHeight));
        NativePoint topLeft = new((int)Math.Round(screenTopLeft.X), (int)Math.Round(screenTopLeft.Y));
        NativePoint bottomRight = new((int)Math.Round(screenBottomRight.X), (int)Math.Round(screenBottomRight.Y));
        if (!ScreenToClient(_mainWindowHandle, ref topLeft)
            || !ScreenToClient(_mainWindowHandle, ref bottomRight))
        {
            throw new InvalidOperationException("Unable to map the WebView2 bounds to MainWindow client coordinates.");
        }

        return Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    private async Task WaitForWebViewSurfaceLayoutAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(
            () => ServiceWorkspace.UpdateLayout(),
            DispatcherPriority.Loaded,
            cancellationToken);
    }

    private Rectangle GetPreShowDirectWebViewBounds()
    {
        if (!GetClientRect(_mainWindowHandle, out NativeRect clientRect))
        {
            throw new InvalidOperationException("Unable to read the hidden MainWindow client bounds.");
        }

        uint dpi = GetDpiForWindow(_mainWindowHandle);
        double scale = dpi == 0 ? 1d : dpi / 96d;
        int left = (int)Math.Round(84d * scale);
        int top = (int)Math.Round(64d * scale);
        return Rectangle.FromLTRB(left, top, clientRect.Right, clientRect.Bottom);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void CompleteSelectionOperation(CancellationTokenSource selectionCancellation)
    {
        if (!ReferenceEquals(_selectionCancellation, selectionCancellation))
        {
            return;
        }

        _selectionCancellation = null;
        selectionCancellation.Dispose();
    }

    private void ShowMissingRuntimeDialog()
    {
        WebViewRuntimeRequiredWindow dialog = new() { Owner = this };
        bool openInstallerPage = dialog.ShowDialog() == true;

        if (openInstallerPage && !_externalBrowserService.TryOpen(_webViewRuntimeService.InstallerPageUri))
        {
            WpfMessageBox.Show(
                this,
                "Не удалось открыть официальную страницу WebView2 Runtime в системном браузере.",
                "Не удалось открыть браузер",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        System.Windows.Application.Current.Shutdown();
    }

    private void ShowOperationError(string title, Exception exception) =>
        WpfMessageBox.Show(
            this,
            $"{exception.Message}",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static bool IsRecoverableOperationException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or System.Security.Cryptography.CryptographicException
            or System.Runtime.InteropServices.COMException;

    private void ApplySavedWindowSettings(WindowSettings settings)
    {
        Width = Math.Max(MinWidth, settings.Width);
        Height = Math.Max(MinHeight, settings.Height);

        if (settings.Left is double left
            && settings.Top is double top
            && IsPositionVisible(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (settings.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsPositionVisible(double left, double top, double width, double height)
    {
        Rect savedBounds = new(left, top, width, height);
        Rect virtualScreen = new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        savedBounds.Intersect(virtualScreen);
        return savedBounds.Width >= 120 && savedBounds.Height >= 80;
    }
}
