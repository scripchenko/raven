using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewSessionManager(
    IAppPaths appPaths,
    NavigationPolicy navigationPolicy,
    IExternalBrowserService externalBrowserService) : IWebViewSessionManager
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly HashSet<ulong> _cancelledExternalNavigations = [];
    private WpfWebView2? _webView;
    private ServiceInstance? _serviceInstance;
    private bool _processFailureDetected;
    private bool _automaticProcessRecoveryUsed;
    private bool _disposed;

    public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged;
    public event EventHandler? SessionRecreationRequested;

    public WebViewSessionState State { get; private set; } = WebViewSessionState.Uninitialized;

    public WpfWebView2 CreateWebView(ServiceInstance serviceInstance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateServiceInstance(serviceInstance);
        Directory.CreateDirectory(appPaths.WebViewDataFolder);

        return new WpfWebView2
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = appPaths.WebViewDataFolder,
                ProfileName = serviceInstance.ProfileName,
                IsInPrivateModeEnabled = false
            }
        };
    }

    public async Task<bool> InitializeAsync(
        WpfWebView2 webView,
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(webView);
        ValidateServiceInstance(serviceInstance);

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseSessionCore(updateState: false);
            _webView = webView;
            _serviceInstance = serviceInstance;
            _processFailureDetected = false;
            Publish(WebViewSessionStatus.Initializing);

            await webView.EnsureCoreWebView2Async();
            cancellationToken.ThrowIfCancellationRequested();

            ConfigureCoreWebView(webView.CoreWebView2);

            Uri startUri = GetValidatedStartUri(serviceInstance);
            webView.CoreWebView2.Navigate(startUri.AbsoluteUri);
            Publish(WebViewSessionStatus.Navigating);
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            PublishFailure(
                "Требуется WebView2 Runtime",
                "Microsoft Edge WebView2 Runtime не найден.",
                nameof(WebView2RuntimeNotFoundException));
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseSessionCore(updateState: true);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            PublishFailure(
                "Не удалось открыть Telegram",
                "Инициализация защищённого профиля WebView2 завершилась ошибкой. Повторите попытку.",
                exception.GetType().Name);
            return false;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public void GoBack()
    {
        try
        {
            if (!_processFailureDetected && _webView?.CanGoBack == true)
            {
                _webView.GoBack();
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(exception);
        }
    }

    public void GoForward()
    {
        try
        {
            if (!_processFailureDetected && _webView?.CanGoForward == true)
            {
                _webView.GoForward();
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(exception);
        }
    }

    public void Reload()
    {
        try
        {
            if (_webView?.CoreWebView2 is not null && !_processFailureDetected)
            {
                _webView.Reload();
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(exception);
        }
    }

    public void NavigateHome()
    {
        try
        {
            if (_webView?.CoreWebView2 is null || _serviceInstance is null || _processFailureDetected)
            {
                return;
            }

            _webView.CoreWebView2.Navigate(GetValidatedStartUri(_serviceInstance).AbsoluteUri);
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(exception);
        }
    }

    public void Retry()
    {
        if (!HasInitializedControl() || _processFailureDetected)
        {
            SessionRecreationRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        NavigateHome();
    }

    public void ReleaseSession()
    {
        ReleaseSessionCore(updateState: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseSessionCore(updateState: false);
        _initializationGate.Dispose();
    }

    private void ConfigureCoreWebView(CoreWebView2 coreWebView)
    {
#if DEBUG
        coreWebView.Settings.AreDevToolsEnabled = true;
#else
        coreWebView.Settings.AreDevToolsEnabled = false;
#endif
        coreWebView.NavigationStarting += OnNavigationStarting;
        coreWebView.NavigationCompleted += OnNavigationCompleted;
        coreWebView.HistoryChanged += OnHistoryChanged;
        coreWebView.NewWindowRequested += OnNewWindowRequested;
        coreWebView.ProcessFailed += OnProcessFailed;
    }

    private void UnsubscribeCoreWebView(CoreWebView2 coreWebView)
    {
        coreWebView.NavigationStarting -= OnNavigationStarting;
        coreWebView.NavigationCompleted -= OnNavigationCompleted;
        coreWebView.HistoryChanged -= OnHistoryChanged;
        coreWebView.NewWindowRequested -= OnNewWindowRequested;
        coreWebView.ProcessFailed -= OnProcessFailed;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_serviceInstance is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? target))
        {
            e.Cancel = true;
            PublishFailure("Переход заблокирован", "WebView2 запросил некорректный адрес.", "InvalidUri");
            return;
        }

        if (!navigationPolicy.IsAllowedTopLevelNavigation(_serviceInstance.ServiceType, target))
        {
            e.Cancel = true;
            _cancelledExternalNavigations.Add(e.NavigationId);
            OpenExternalOrReport(target);
            return;
        }

        Publish(WebViewSessionStatus.Navigating);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_cancelledExternalNavigations.Remove(e.NavigationId))
        {
            Publish(WebViewSessionStatus.Ready);
            return;
        }

        if (e.IsSuccess)
        {
            Publish(WebViewSessionStatus.Ready);
            return;
        }

        bool isOffline = WebViewErrorClassifier.IsConnectivityFailure(e.WebErrorStatus);
        Publish(
            isOffline ? WebViewSessionStatus.Offline : WebViewSessionStatus.Failed,
            isOffline ? "Нет подключения к интернету" : "Ошибка загрузки Telegram",
            WebViewErrorClassifier.GetUserMessage(e.WebErrorStatus),
            e.WebErrorStatus.ToString());
    }

    private void OnHistoryChanged(object? sender, object e)
    {
        Publish(State.Status, State.ErrorTitle, State.ErrorMessage, State.ErrorCode);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (_serviceInstance is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? target))
        {
            PublishFailure("Окно заблокировано", "Сервис запросил некорректный адрес нового окна.", "InvalidPopupUri");
            return;
        }

        if (navigationPolicy.IsAllowedTopLevelNavigation(_serviceInstance.ServiceType, target))
        {
            _webView?.CoreWebView2.Navigate(target.AbsoluteUri);
            return;
        }

        OpenExternalOrReport(target);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _processFailureDetected = true;
        PublishFailure(
            "Сбой процесса WebView2",
            _automaticProcessRecoveryUsed
                ? "Автоматическое восстановление уже выполнялось. Нажмите «Повторить», чтобы попробовать вручную."
                : "WebView2 неожиданно завершил работу. Выполняется одна попытка восстановления.",
            e.ProcessFailedKind.ToString());

        if (_automaticProcessRecoveryUsed)
        {
            return;
        }

        _automaticProcessRecoveryUsed = true;
        SessionRecreationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenExternalOrReport(Uri target)
    {
        if (externalBrowserService.TryOpen(target))
        {
            Publish(WebViewSessionStatus.Ready);
            return;
        }

        PublishFailure(
            "Внешняя ссылка заблокирована",
            "Адрес нельзя безопасно открыть в системном браузере.",
            "UnsupportedExternalUri");
    }

    private void PublishFailure(string title, string message, string code) =>
        Publish(WebViewSessionStatus.Failed, title, message, code);

    private void Publish(
        WebViewSessionStatus status,
        string? errorTitle = null,
        string? errorMessage = null,
        string? errorCode = null)
    {
        (bool canGoBack, bool canGoForward) = ReadHistoryState();
        State = new WebViewSessionState(
            status,
            canGoBack,
            canGoForward,
            errorTitle,
            errorMessage,
            errorCode);
        StateChanged?.Invoke(this, new WebViewSessionStateChangedEventArgs(State));
    }

    private void ReleaseSessionCore(bool updateState)
    {
        if (_webView is not null)
        {
            try
            {
                if (_webView.CoreWebView2 is CoreWebView2 coreWebView)
                {
                    UnsubscribeCoreWebView(coreWebView);
                }
            }
            catch (Exception exception) when (IsUnavailableControlException(exception))
            {
                // A crashed or disposed controller has no usable events left to detach.
            }

            _webView.Dispose();
        }

        _webView = null;
        _serviceInstance = null;
        _processFailureDetected = false;
        _cancelledExternalNavigations.Clear();

        if (updateState)
        {
            Publish(WebViewSessionStatus.Uninitialized);
        }
    }

    private Uri GetValidatedStartUri(ServiceInstance serviceInstance)
    {
        if (!Uri.TryCreate(serviceInstance.StartUrl, UriKind.Absolute, out Uri? startUri)
            || !navigationPolicy.IsAllowedTopLevelNavigation(serviceInstance.ServiceType, startUri))
        {
            throw new InvalidOperationException("The service start URI is missing or not allowed.");
        }

        return startUri;
    }

    private static void ValidateServiceInstance(ServiceInstance serviceInstance)
    {
        ArgumentNullException.ThrowIfNull(serviceInstance);

        if (serviceInstance.ServiceType != ServiceType.Telegram)
        {
            throw new NotSupportedException("Stage 2 supports only one Telegram instance.");
        }

        if (serviceInstance.Id == Guid.Empty)
        {
            throw new InvalidOperationException("The Telegram service instance must have a non-empty identifier.");
        }

        string expectedProfileName = ProfileNameFactory.Create(serviceInstance.Id);
        if (!string.Equals(serviceInstance.ProfileName, expectedProfileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The WebView2 profile name is not safe for this service instance.");
        }
    }

    private bool HasInitializedControl()
    {
        try
        {
            return _webView?.CoreWebView2 is not null;
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            return false;
        }
    }

    private (bool CanGoBack, bool CanGoForward) ReadHistoryState()
    {
        try
        {
            return _processFailureDetected || _webView is null
                ? (false, false)
                : (_webView.CanGoBack, _webView.CanGoForward);
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            return (false, false);
        }
    }

    private void PublishUnavailableControl(Exception exception)
    {
        _processFailureDetected = true;
        PublishFailure(
            "Сессия WebView2 недоступна",
            "Контрол Telegram больше не отвечает. Нажмите «Повторить», чтобы создать его заново.",
            exception.GetType().Name);
    }

    private static bool IsUnavailableControlException(Exception exception) =>
        exception is InvalidOperationException
            or System.Runtime.InteropServices.COMException;
}
