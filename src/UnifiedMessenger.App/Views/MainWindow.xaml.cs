using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfMessageBox = System.Windows.MessageBox;
using WpfPanel = System.Windows.Controls.Panel;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace UnifiedMessenger.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IWebViewSessionManager _webViewSessionManager;
    private readonly IWebViewRuntimeService _webViewRuntimeService;
    private readonly IExternalBrowserService _externalBrowserService;
    private readonly IApplicationExitCoordinator _exitCoordinator;
    private readonly IApplicationTrayCoordinator _trayCoordinator;
    private readonly IWindowActivationService _windowActivationService;
    private readonly ITaskbarActivityIndicator _taskbarActivityIndicator;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _selectionCancellation;
    private bool _isRuntimeAvailable;

    public MainWindow(
        MainWindowViewModel viewModel,
        IWebViewSessionManager webViewSessionManager,
        IWebViewRuntimeService webViewRuntimeService,
        IExternalBrowserService externalBrowserService,
        IApplicationExitCoordinator exitCoordinator,
        IApplicationTrayCoordinator trayCoordinator,
        IWindowActivationService windowActivationService,
        ITaskbarActivityIndicator taskbarActivityIndicator)
    {
        _viewModel = viewModel;
        _webViewSessionManager = webViewSessionManager;
        _webViewRuntimeService = webViewRuntimeService;
        _externalBrowserService = externalBrowserService;
        _exitCoordinator = exitCoordinator;
        _trayCoordinator = trayCoordinator;
        _windowActivationService = windowActivationService;
        _taskbarActivityIndicator = taskbarActivityIndicator;
        DataContext = viewModel;

        InitializeComponent();
        _windowActivationService.Attach(
            this,
            () => _viewModel.SelectedService?.Id,
            _viewModel.SelectService);
        _taskbarActivityIndicator.Attach(this);
        ApplySavedWindowSettings(viewModel.WindowSettings);
        Loaded += OnLoaded;
        Activated += OnActivated;
        _viewModel.SelectedServiceChanged += OnSelectedServiceChanged;
        _webViewSessionManager.SessionRecreationRequested += OnSessionRecreationRequested;
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        Activated -= OnActivated;
        _viewModel.SelectedServiceChanged -= OnSelectedServiceChanged;
        _webViewSessionManager.SessionRecreationRequested -= OnSessionRecreationRequested;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        _lifetimeCancellation.Cancel();

        WebViewContainer.Children.Clear();
        _webViewSessionManager.ReleaseAllSessions();
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
        bool shutdownStarted = Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished;
        if (_exitCoordinator.ShouldHideToTray(_viewModel.CloseToTray, shutdownStarted))
        {
            eventArgs.Cancel = true;
            Hide();
            _ = ShowTrayHintOnceAsync();
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

        WebViewRuntimeInfo runtimeInfo = _webViewRuntimeService.DetectRuntime();
        _viewModel.SetRuntimeInfo(runtimeInfo);
        if (!runtimeInfo.IsAvailable)
        {
            ShowMissingRuntimeDialog();
            return;
        }

        _isRuntimeAvailable = true;
        await ShowSelectedServiceAsync();
    }

    private async void OnSelectedServiceChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            _viewModel.MarkSelectedServiceViewed(IsVisible, IsActive);
            await _viewModel.PersistSelectionAsync();
            await ShowSelectedServiceAsync();
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError("Не удалось переключить аккаунт", exception);
        }
    }

    private void OnActivated(object? sender, EventArgs eventArgs) =>
        _viewModel.MarkSelectedServiceViewed(IsVisible, IsActive);

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
        WebViewContainer.Children.Clear();
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

            WpfWebView2 webView = _webViewSessionManager.CreateWebView(service);
            if (webView.Parent is WpfPanel previousParent)
            {
                previousParent.Children.Remove(webView);
            }

            WebViewContainer.Children.Add(webView);
            await _webViewSessionManager.InitializeAsync(webView, service, cancellationToken);
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

    private async void RenameAccount_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetMenuService(sender, out ServiceInstance service))
        {
            return;
        }

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

    private async void ToggleAccount_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetMenuService(sender, out ServiceInstance service))
        {
            await SetAccountEnabledAsync(service, !service.IsEnabled);
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
                if (_viewModel.SelectedService?.Id == service.Id)
                {
                    WebViewContainer.Children.Clear();
                }

                _webViewSessionManager.ReleaseSession(service.Id);
            }

            await _viewModel.SetServiceEnabledAsync(service, isEnabled);
            if (_viewModel.SelectedService?.Id == service.Id)
            {
                await ShowSelectedServiceAsync();
            }
        }
        catch (Exception exception) when (IsRecoverableOperationException(exception))
        {
            ShowOperationError(isEnabled ? "Не удалось включить аккаунт" : "Не удалось отключить аккаунт", exception);
        }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetMenuService(sender, out ServiceInstance service))
        {
            return;
        }

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
            if (_viewModel.SelectedService?.Id == service.Id)
            {
                WebViewContainer.Children.Clear();
            }

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
        service = (sender as WpfMenuItem)?.CommandParameter as ServiceInstance ?? null!;
        return service is not null;
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
