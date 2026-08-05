using System.Windows;
using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace UnifiedMessenger.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IWebViewSessionManager _webViewSessionManager;
    private readonly IWebViewRuntimeService _webViewRuntimeService;
    private readonly IExternalBrowserService _externalBrowserService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private WpfWebView2? _telegramWebView;
    private bool _isRecreatingSession;

    public MainWindow(
        MainWindowViewModel viewModel,
        IWebViewSessionManager webViewSessionManager,
        IWebViewRuntimeService webViewRuntimeService,
        IExternalBrowserService externalBrowserService)
    {
        _viewModel = viewModel;
        _webViewSessionManager = webViewSessionManager;
        _webViewRuntimeService = webViewRuntimeService;
        _externalBrowserService = externalBrowserService;
        DataContext = viewModel;

        InitializeComponent();
        ApplySavedWindowSettings(viewModel.WindowSettings);
        Loaded += OnLoaded;
        _webViewSessionManager.SessionRecreationRequested += OnSessionRecreationRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        _webViewSessionManager.SessionRecreationRequested -= OnSessionRecreationRequested;
        _lifetimeCancellation.Cancel();

        WebViewContainer.Children.Clear();
        _webViewSessionManager.ReleaseSession();
        _telegramWebView = null;
        _lifetimeCancellation.Dispose();

        Rect bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        _viewModel.UpdateWindowSettings(
            bounds.Width,
            bounds.Height,
            bounds.Left,
            bounds.Top,
            WindowState == WindowState.Maximized);
        base.OnClosed(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        WebViewRuntimeInfo runtimeInfo = _webViewRuntimeService.DetectRuntime();
        _viewModel.SetRuntimeInfo(runtimeInfo);
        if (!runtimeInfo.IsAvailable)
        {
            ShowMissingRuntimeDialog();
            return;
        }

        await CreateTelegramSessionAsync();
    }

    private async void OnSessionRecreationRequested(object? sender, EventArgs e)
    {
        await Task.Yield();
        await CreateTelegramSessionAsync();
    }

    private async Task CreateTelegramSessionAsync()
    {
        if (_isRecreatingSession
            || _lifetimeCancellation.IsCancellationRequested
            || _viewModel.SelectedService is not ServiceInstance telegram)
        {
            return;
        }

        _isRecreatingSession = true;
        try
        {
            WebViewContainer.Children.Clear();
            _webViewSessionManager.ReleaseSession();
            _telegramWebView = _webViewSessionManager.CreateWebView(telegram);
            WebViewContainer.Children.Add(_telegramWebView);

            await _webViewSessionManager.InitializeAsync(
                _telegramWebView,
                telegram,
                _lifetimeCancellation.Token);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowMissingRuntimeDialog();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // The window is closing while WebView2 is initializing.
        }
        finally
        {
            _isRecreatingSession = false;
        }
    }

    private void ShowMissingRuntimeDialog()
    {
        WebViewRuntimeRequiredWindow dialog = new() { Owner = this };
        bool openInstallerPage = dialog.ShowDialog() == true;

        if (openInstallerPage && !_externalBrowserService.TryOpen(_webViewRuntimeService.InstallerPageUri))
        {
            System.Windows.MessageBox.Show(
                this,
                "Не удалось открыть официальную страницу WebView2 Runtime в системном браузере.",
                "Не удалось открыть браузер",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        System.Windows.Application.Current.Shutdown();
    }

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
