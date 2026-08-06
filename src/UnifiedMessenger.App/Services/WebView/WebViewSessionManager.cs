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
    IBuiltInServiceCatalog serviceCatalog,
    NavigationPolicy navigationPolicy,
    WebNavigationService webNavigationService,
    WebNewWindowNavigationService newWindowNavigationService,
    IWebViewProfileCleaner profileCleaner) : IWebViewSessionManager
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly Dictionary<Guid, SessionEntry> _sessions = [];
    private SessionEntry? _activeSession;
    private bool _disposed;

    public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested;

    public WebViewSessionState State { get; private set; } = WebViewSessionState.Uninitialized;

    public WpfWebView2 CreateWebView(ServiceInstance serviceInstance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateServiceInstance(serviceInstance);

        if (_sessions.TryGetValue(serviceInstance.Id, out SessionEntry? existingSession))
        {
            EnsureSameProfile(existingSession.ServiceInstance, serviceInstance);
            existingSession.ServiceInstance = serviceInstance;
            return existingSession.WebView;
        }

        Directory.CreateDirectory(appPaths.WebViewDataFolder);
        WpfWebView2 webView = new()
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

        _sessions.Add(serviceInstance.Id, new SessionEntry(serviceInstance, webView));
        return webView;
    }

    public async Task<bool> InitializeAsync(
        WpfWebView2 webView,
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(webView);
        ValidateServiceInstance(serviceInstance);

        if (!_sessions.TryGetValue(serviceInstance.Id, out SessionEntry? session)
            || !ReferenceEquals(session.WebView, webView))
        {
            throw new InvalidOperationException("The WebView2 control does not belong to this service account.");
        }

        EnsureSameProfile(session.ServiceInstance, serviceInstance);
        session.ServiceInstance = serviceInstance;
        _activeSession = session;

        if (HasInitializedControl(session))
        {
            Publish(session, session.State.Status is WebViewSessionStatus.Uninitialized
                ? WebViewSessionStatus.Ready
                : session.State.Status,
                session.State.ErrorTitle,
                session.State.ErrorMessage,
                session.State.ErrorCode);
            return true;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasInitializedControl(session))
            {
                Publish(session, WebViewSessionStatus.Ready);
                return true;
            }

            session.ProcessFailureDetected = false;
            Publish(session, WebViewSessionStatus.Initializing);

            await webView.EnsureCoreWebView2Async();
            cancellationToken.ThrowIfCancellationRequested();

            ConfigureCoreWebView(session, webView.CoreWebView2);
            Uri startUri = GetValidatedStartUri(serviceInstance);
            webView.CoreWebView2.Navigate(startUri.AbsoluteUri);
            Publish(session, WebViewSessionStatus.Navigating);
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            PublishFailure(
                session,
                "Требуется WebView2 Runtime",
                "Microsoft Edge WebView2 Runtime не найден.",
                nameof(WebView2RuntimeNotFoundException));
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseSessionCore(session, updateState: true);
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException)
        {
            PublishFailure(
                session,
                $"Не удалось открыть {serviceInstance.DisplayName}",
                "Инициализация защищённого профиля WebView2 завершилась ошибкой. Повторите попытку.",
                exception.GetType().Name);
            return false;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public bool HasSession(Guid serviceInstanceId) => _sessions.ContainsKey(serviceInstanceId);

    public void DeactivateSession()
    {
        _activeSession = null;
        State = WebViewSessionState.Uninitialized;
        StateChanged?.Invoke(this, new WebViewSessionStateChangedEventArgs(State));
    }

    public void GoBack()
    {
        SessionEntry? session = _activeSession;
        try
        {
            if (session is not null && !session.ProcessFailureDetected && session.WebView.CanGoBack)
            {
                session.WebView.GoBack();
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(session, exception);
        }
    }

    public void GoForward()
    {
        SessionEntry? session = _activeSession;
        try
        {
            if (session is not null && !session.ProcessFailureDetected && session.WebView.CanGoForward)
            {
                session.WebView.GoForward();
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(session, exception);
        }
    }

    public void Reload()
    {
        SessionEntry? session = _activeSession;
        try
        {
            if (session?.WebView.CoreWebView2 is not null && !session.ProcessFailureDetected)
            {
                session.WebView.Reload();
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(session, exception);
        }
    }

    public void NavigateHome()
    {
        SessionEntry? session = _activeSession;
        try
        {
            if (session?.WebView.CoreWebView2 is null || session.ProcessFailureDetected)
            {
                return;
            }

            session.WebView.CoreWebView2.Navigate(GetValidatedStartUri(session.ServiceInstance).AbsoluteUri);
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            PublishUnavailableControl(session, exception);
        }
    }

    public void Retry()
    {
        SessionEntry? session = _activeSession;
        if (session is null)
        {
            return;
        }

        if (!HasInitializedControl(session) || session.ProcessFailureDetected)
        {
            SessionRecreationRequested?.Invoke(
                this,
                new WebViewSessionRecreationRequestedEventArgs(session.ServiceInstance.Id));
            return;
        }

        NavigateHome();
    }

    public void ReleaseSession(Guid serviceInstanceId)
    {
        if (_sessions.TryGetValue(serviceInstanceId, out SessionEntry? session))
        {
            ReleaseSessionCore(session, updateState: true);
        }
    }

    public async Task<bool> ClearProfileAsync(
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceInstance);
        ValidateProfile(serviceInstance);

        if (_sessions.TryGetValue(serviceInstance.Id, out SessionEntry? session))
        {
            try
            {
                if (session.WebView.CoreWebView2?.Profile is CoreWebView2Profile profile)
                {
                    await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                }
            }
            catch (Exception exception) when (IsUnavailableControlException(exception) || exception is IOException)
            {
                // Disposing the controller and deleting its isolated directory is the fallback.
            }

            ReleaseSessionCore(session, updateState: true);
        }

        return await profileCleaner.TryDeleteProfileAsync(serviceInstance.ProfileName, cancellationToken);
    }

    public void ReleaseAllSessions()
    {
        foreach (SessionEntry session in _sessions.Values.ToArray())
        {
            ReleaseSessionCore(session, updateState: false);
        }

        DeactivateSession();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseAllSessions();
        _initializationGate.Dispose();
    }

    private void ConfigureCoreWebView(SessionEntry session, CoreWebView2 coreWebView)
    {
#if DEBUG
        coreWebView.Settings.AreDevToolsEnabled = true;
#else
        coreWebView.Settings.AreDevToolsEnabled = false;
#endif
        session.NavigationStartingHandler = (_, eventArgs) => OnNavigationStarting(session, eventArgs);
        session.NavigationCompletedHandler = (_, eventArgs) => OnNavigationCompleted(session, eventArgs);
        session.HistoryChangedHandler = (_, _) => OnHistoryChanged(session);
        session.NewWindowRequestedHandler = (_, eventArgs) => OnNewWindowRequested(session, eventArgs);
        session.ProcessFailedHandler = (_, eventArgs) => OnProcessFailed(session, eventArgs);

        coreWebView.NavigationStarting += session.NavigationStartingHandler;
        coreWebView.NavigationCompleted += session.NavigationCompletedHandler;
        coreWebView.HistoryChanged += session.HistoryChangedHandler;
        coreWebView.NewWindowRequested += session.NewWindowRequestedHandler;
        coreWebView.ProcessFailed += session.ProcessFailedHandler;
    }

    private static void UnsubscribeCoreWebView(SessionEntry session, CoreWebView2 coreWebView)
    {
        coreWebView.NavigationStarting -= session.NavigationStartingHandler;
        coreWebView.NavigationCompleted -= session.NavigationCompletedHandler;
        coreWebView.HistoryChanged -= session.HistoryChangedHandler;
        coreWebView.NewWindowRequested -= session.NewWindowRequestedHandler;
        coreWebView.ProcessFailed -= session.ProcessFailedHandler;
    }

    private void OnNavigationStarting(SessionEntry session, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out Uri? target))
        {
            eventArgs.Cancel = true;
            PublishFailure(session, "Переход заблокирован", "WebView2 запросил некорректный адрес.", "InvalidUri");
            return;
        }

        WebNavigationDisposition disposition = webNavigationService.Route(session.ServiceInstance.ServiceType, target);
        if (disposition is WebNavigationDisposition.Internal)
        {
            Publish(session, WebViewSessionStatus.Navigating);
            return;
        }

        eventArgs.Cancel = true;
        session.CancelledExternalNavigations.Add(eventArgs.NavigationId);
        if (disposition is WebNavigationDisposition.ExternalOpened)
        {
            Publish(session, WebViewSessionStatus.Ready);
            return;
        }

        PublishFailure(
            session,
            "Внешняя ссылка заблокирована",
            "Адрес нельзя безопасно открыть в системном браузере.",
            "UnsupportedExternalUri");
    }

    private void OnNavigationCompleted(SessionEntry session, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (session.CancelledExternalNavigations.Remove(eventArgs.NavigationId))
        {
            Publish(session, WebViewSessionStatus.Ready);
            return;
        }

        if (eventArgs.IsSuccess)
        {
            Publish(session, WebViewSessionStatus.Ready);
            return;
        }

        bool isOffline = WebViewErrorClassifier.IsConnectivityFailure(eventArgs.WebErrorStatus);
        Publish(
            session,
            isOffline ? WebViewSessionStatus.Offline : WebViewSessionStatus.Failed,
            isOffline ? "Нет подключения к интернету" : $"Ошибка загрузки {session.ServiceInstance.DisplayName}",
            WebViewErrorClassifier.GetUserMessage(eventArgs.WebErrorStatus),
            eventArgs.WebErrorStatus.ToString());
    }

    private void OnHistoryChanged(SessionEntry session) =>
        Publish(session, session.State.Status, session.State.ErrorTitle, session.State.ErrorMessage, session.State.ErrorCode);

    private void OnNewWindowRequested(SessionEntry session, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;

        if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out Uri? target))
        {
            PublishFailure(session, "Окно заблокировано", "Сервис запросил некорректный адрес нового окна.", "InvalidPopupUri");
            return;
        }

        WebNavigationDisposition disposition = newWindowNavigationService.Route(
            session.ServiceInstance,
            target,
            internalTarget => session.WebView.CoreWebView2.Navigate(internalTarget.AbsoluteUri));

        if (disposition is WebNavigationDisposition.Blocked)
        {
            PublishFailure(
                session,
                "Внешняя ссылка заблокирована",
                "Адрес нельзя безопасно открыть в системном браузере.",
                "UnsupportedExternalUri");
        }
    }

    private void OnProcessFailed(SessionEntry session, CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        session.ProcessFailureDetected = true;
        PublishFailure(
            session,
            "Сбой процесса WebView2",
            session.AutomaticProcessRecoveryUsed
                ? "Автоматическое восстановление уже выполнялось. Нажмите «Повторить», чтобы попробовать вручную."
                : "WebView2 неожиданно завершил работу. Выполняется одна попытка восстановления.",
            eventArgs.ProcessFailedKind.ToString());

        if (session.AutomaticProcessRecoveryUsed || !ReferenceEquals(_activeSession, session))
        {
            return;
        }

        session.AutomaticProcessRecoveryUsed = true;
        SessionRecreationRequested?.Invoke(
            this,
            new WebViewSessionRecreationRequestedEventArgs(session.ServiceInstance.Id));
    }

    private void PublishFailure(SessionEntry session, string title, string message, string code) =>
        Publish(session, WebViewSessionStatus.Failed, title, message, code);

    private void Publish(
        SessionEntry session,
        WebViewSessionStatus status,
        string? errorTitle = null,
        string? errorMessage = null,
        string? errorCode = null)
    {
        (bool canGoBack, bool canGoForward) = ReadHistoryState(session);
        session.State = new WebViewSessionState(
            status,
            canGoBack,
            canGoForward,
            errorTitle,
            errorMessage,
            errorCode);

        if (!ReferenceEquals(_activeSession, session))
        {
            return;
        }

        State = session.State;
        StateChanged?.Invoke(this, new WebViewSessionStateChangedEventArgs(State));
    }

    private void ReleaseSessionCore(SessionEntry session, bool updateState)
    {
        try
        {
            if (session.WebView.CoreWebView2 is CoreWebView2 coreWebView)
            {
                UnsubscribeCoreWebView(session, coreWebView);
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            // A crashed or disposed controller has no usable events left to detach.
        }

        session.WebView.Dispose();
        _sessions.Remove(session.ServiceInstance.Id);
        session.CancelledExternalNavigations.Clear();

        if (ReferenceEquals(_activeSession, session))
        {
            _activeSession = null;
            if (updateState)
            {
                State = WebViewSessionState.Uninitialized;
                StateChanged?.Invoke(this, new WebViewSessionStateChangedEventArgs(State));
            }
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

    private void ValidateServiceInstance(ServiceInstance serviceInstance)
    {
        ArgumentNullException.ThrowIfNull(serviceInstance);
        ServiceDefinition definition = serviceCatalog.Get(serviceInstance.ServiceType);
        if (!definition.IsWebViewService || definition.StartUri is null || serviceInstance.ServiceType == ServiceType.Gmail)
        {
            throw new NotSupportedException("This account is not a Stage 3 WebView service.");
        }

        if (!serviceInstance.IsEnabled)
        {
            throw new InvalidOperationException("A disabled account cannot create a WebView2 session.");
        }

        ValidateProfile(serviceInstance);
    }

    private static void ValidateProfile(ServiceInstance serviceInstance)
    {
        if (serviceInstance.Id == Guid.Empty)
        {
            throw new InvalidOperationException("The service account must have a non-empty identifier.");
        }

        string expectedProfileName = ProfileNameFactory.Create(serviceInstance.Id);
        if (!string.Equals(serviceInstance.ProfileName, expectedProfileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The WebView2 profile name is not safe for this service account.");
        }
    }

    private static void EnsureSameProfile(ServiceInstance existing, ServiceInstance requested)
    {
        if (existing.ServiceType != requested.ServiceType
            || !string.Equals(existing.ProfileName, requested.ProfileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A service account cannot switch its WebView2 profile or service type.");
        }
    }

    private static bool HasInitializedControl(SessionEntry session)
    {
        try
        {
            return session.WebView.CoreWebView2 is not null;
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            return false;
        }
    }

    private static (bool CanGoBack, bool CanGoForward) ReadHistoryState(SessionEntry session)
    {
        try
        {
            return session.ProcessFailureDetected
                ? (false, false)
                : (session.WebView.CanGoBack, session.WebView.CanGoForward);
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            return (false, false);
        }
    }

    private void PublishUnavailableControl(SessionEntry? session, Exception exception)
    {
        if (session is null)
        {
            return;
        }

        session.ProcessFailureDetected = true;
        PublishFailure(
            session,
            "Сессия WebView2 недоступна",
            $"Контрол {session.ServiceInstance.DisplayName} больше не отвечает. Нажмите «Повторить», чтобы создать его заново.",
            exception.GetType().Name);
    }

    private static bool IsUnavailableControlException(Exception exception) =>
        exception is InvalidOperationException
            or System.Runtime.InteropServices.COMException;

    private sealed class SessionEntry(ServiceInstance serviceInstance, WpfWebView2 webView)
    {
        public ServiceInstance ServiceInstance { get; set; } = serviceInstance;
        public WpfWebView2 WebView { get; } = webView;
        public WebViewSessionState State { get; set; } = WebViewSessionState.Uninitialized;
        public HashSet<ulong> CancelledExternalNavigations { get; } = [];
        public bool ProcessFailureDetected { get; set; }
        public bool AutomaticProcessRecoveryUsed { get; set; }
        public EventHandler<CoreWebView2NavigationStartingEventArgs>? NavigationStartingHandler { get; set; }
        public EventHandler<CoreWebView2NavigationCompletedEventArgs>? NavigationCompletedHandler { get; set; }
        public EventHandler<object>? HistoryChangedHandler { get; set; }
        public EventHandler<CoreWebView2NewWindowRequestedEventArgs>? NewWindowRequestedHandler { get; set; }
        public EventHandler<CoreWebView2ProcessFailedEventArgs>? ProcessFailedHandler { get; set; }
    }
}
