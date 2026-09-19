using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewSessionManager(
    ICoreWebView2EnvironmentProvider environmentProvider,
    IBuiltInServiceCatalog serviceCatalog,
    NavigationPolicy navigationPolicy,
    WebNavigationService webNavigationService,
    WebNewWindowNavigationService newWindowNavigationService,
    INotificationPermissionCoordinator permissionCoordinator,
    IWebViewProfileCleaner profileCleaner) : IWebViewSessionManager
{
    public const bool SaveNotificationPermissionsInProfile = true;
    internal const string MaxNotificationOrigin = "https://web.max.ru";
    internal static readonly TimeSpan StartupPrimeTimeout = TimeSpan.FromSeconds(45);

    // Empirical cold-start validation showed that Telegram registers its notification
    // pipeline just after the first successful visible navigation. Keep the only
    // post-navigation settle here so it is bounded, cancellable and easy to remove
    // when the WebView2/Telegram readiness contract becomes explicit.
    internal static readonly TimeSpan StartupPrimeSettleDelay = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Dictionary<Guid, SessionEntry> _sessions = [];
    private readonly WebViewSessionSettlementTracker _settlementTracker = new();
    private readonly HashSet<Guid> _profilesBeingCleared = [];
    private SessionEntry? _activeSession;
    private int _initialNavigationCount;
    private bool _shutdownStarted;
    private bool _disposed;

    public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested;
    public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged;
    public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived;
    public event EventHandler<BackgroundNotificationActivityReceivedEventArgs>? BackgroundNotificationActivityReceived;

    public WebViewSessionState State { get; private set; } = WebViewSessionState.Uninitialized;
    public bool IsShutdownStarted => _shutdownStarted;
    public int InitializedSessionCount => _sessions.Values.Count(HasInitializedControl);
    public int InitialNavigationCount => _initialNavigationCount;

    public Task<bool> InitializeAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        ServiceInstance serviceInstance,
        bool activate,
        CancellationToken cancellationToken = default) =>
        InitializeInternalAsync(
            parentWindow,
            bounds,
            serviceInstance,
            activate,
            primeVisibleWhileParentHidden: false,
            cancellationToken);

    public Task<bool> PrimeAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default) =>
        InitializeInternalAsync(
            parentWindow,
            bounds,
            serviceInstance,
            activate: false,
            primeVisibleWhileParentHidden: true,
            cancellationToken);

    private async Task<bool> InitializeInternalAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        ServiceInstance serviceInstance,
        bool activate,
        bool primeVisibleWhileParentHidden,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_shutdownStarted)
        {
            throw new InvalidOperationException("WebView2 shutdown has already started.");
        }

        if (parentWindow == IntPtr.Zero)
        {
            throw new ArgumentException("A real MainWindow HWND is required.", nameof(parentWindow));
        }

        ValidateBounds(bounds);
        ValidateServiceInstance(serviceInstance);

        if (_profilesBeingCleared.Contains(serviceInstance.Id))
        {
            throw new InvalidOperationException("The WebView2 profile is being cleared.");
        }

        if (!_sessions.TryGetValue(serviceInstance.Id, out SessionEntry? session))
        {
            session = new SessionEntry(serviceInstance, parentWindow, bounds);
            _sessions.Add(serviceInstance.Id, session);
        }

        EnsureSameProfile(session.ServiceInstance, serviceInstance);
        if (session.ParentWindow != parentWindow)
        {
            throw new InvalidOperationException("A WebView2 controller cannot switch its MainWindow parent.");
        }

        session.ServiceInstance = serviceInstance;
        session.Bounds = bounds;
        session.PrimeVisibleWhileParentHidden |= primeVisibleWhileParentHidden;

        if (primeVisibleWhileParentHidden)
        {
            HideAllExcept(session);
        }

        if (activate)
        {
            SetActiveSession(session, bounds, isVisible: true, moveFocus: false);
        }

        if (HasInitializedControl(session))
        {
            if (activate && ReferenceEquals(_activeSession, session))
            {
                ApplyControllerLayout(session, isVisible: true, moveFocus: false);
                PublishCurrentState(session);
            }

            return true;
        }

        Task<bool> initializationTask = session.Lifetime.GetOrStartInitialization(
            lifetimeToken => InitializeCoreAsync(session, lifetimeToken));

        bool initialized = await initializationTask.WaitAsync(cancellationToken);
        if (initialized
            && activate
            && IsCurrentSession(session)
            && ReferenceEquals(_activeSession, session))
        {
            ApplyControllerLayout(session, isVisible: true, moveFocus: true);
            PublishCurrentState(session);
        }

        return initialized;
    }

    public bool IsSessionInitialized(Guid serviceInstanceId) =>
        _sessions.TryGetValue(serviceInstanceId, out SessionEntry? session)
        && HasInitializedControl(session);

    public void ActivateSession(Guid serviceInstanceId, Rectangle bounds, bool isVisible, bool moveFocus = false)
    {
        ValidateBounds(bounds);
        if (!_sessions.TryGetValue(serviceInstanceId, out SessionEntry? session))
        {
            return;
        }

        SetActiveSession(session, bounds, isVisible, moveFocus);
        PublishCurrentState(session);
    }

    public void UpdateActiveSessionLayout(Rectangle bounds, bool isVisible)
    {
        ValidateBounds(bounds);
        if (_activeSession is SessionEntry session && session.Controller is not null)
        {
            session.Bounds = bounds;
            ApplyControllerLayout(session, isVisible, moveFocus: false);
        }
    }

    public void NotifyParentWindowPositionChanged()
    {
        foreach (SessionEntry session in _sessions.Values)
        {
            try
            {
                session.Controller?.NotifyParentWindowPositionChanged();
            }
            catch (Exception exception) when (IsUnavailableControlException(exception))
            {
                PublishUnavailableControl(session, exception);
            }
        }
    }

    private async Task<bool> InitializeCoreAsync(
        SessionEntry session,
        CancellationToken lifetimeToken)
    {
        using CancellationTokenSource initializationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCancellation.Token,
                lifetimeToken);
        CancellationToken cancellationToken = initializationCancellation.Token;
        bool gateEntered = false;
        try
        {
            await _initializationGate.WaitAsync(cancellationToken);
            gateEntered = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSession(session))
            {
                return false;
            }

            if (HasInitializedControl(session))
            {
                return true;
            }

            session.ProcessFailureDetected = false;
            Publish(session, WebViewSessionStatus.Initializing);

            CoreWebView2Environment environment = await environmentProvider.GetAsync();
            cancellationToken.ThrowIfCancellationRequested();
            CoreWebView2ControllerOptions options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = session.ServiceInstance.ProfileName;
            options.IsInPrivateModeEnabled = false;

            CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(
                session.ParentWindow,
                options);
            if (!session.Lifetime.TryAttachResource(
                    controller,
                    attachedController =>
                    {
                        session.Controller = attachedController;
                        session.CoreWebView = attachedController.CoreWebView2;
                    },
                    rejectedController => CloseController(session, rejectedController)))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await VkAutoplayPolicy.ApplyAsync(
                session.ServiceInstance.ServiceType,
                session.CoreWebView!.Profile,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            controller.Bounds = session.Bounds;
            controller.IsVisible = session.PrimeVisibleWhileParentHidden;
            if (session.ServiceInstance.ServiceType is ServiceType.Max)
            {
                await SynchronizeMaxNotificationPermissionAsync(
                    session.ServiceInstance,
                    session.CoreWebView.Profile,
                    cancellationToken);
            }
            else if (session.ServiceInstance.ServiceType is ServiceType.VkMessenger)
            {
                session.VkBackgroundNotificationMonitor =
                    await VkBackgroundNotificationMonitor.TryStartAsync(
                        session.CoreWebView,
                        () => OnBackgroundNotificationActivityReceived(session),
                        cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ConfigureCoreWebView(session, session.CoreWebView!);

            cancellationToken.ThrowIfCancellationRequested();
            session.CoreWebView.Navigate(GetValidatedStartUri(session.ServiceInstance).AbsoluteUri);
            Interlocked.Increment(ref _initialNavigationCount);
            Publish(session, WebViewSessionStatus.Navigating);

            if (session.PrimeVisibleWhileParentHidden)
            {
                bool navigationReady = await session.FirstNavigationCompleted.Task
                    .WaitAsync(StartupPrimeTimeout, cancellationToken);
                if (!navigationReady)
                {
                    controller.IsVisible = false;
                    return false;
                }

                await Task.Delay(StartupPrimeSettleDelay, cancellationToken);
                controller.IsVisible = false;
            }

            if (ReferenceEquals(_activeSession, session))
            {
                ApplyControllerLayout(session, isVisible: true, moveFocus: false);
            }
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
            return false;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or IOException
                or System.Runtime.InteropServices.COMException
                or TimeoutException)
        {
            PublishFailure(
                session,
                $"Не удалось открыть {session.ServiceInstance.DisplayName}",
                "Инициализация защищённого профиля WebView2 завершилась ошибкой. Повторите попытку.",
                exception.GetType().Name);
            return false;
        }
        finally
        {
            if (gateEntered)
            {
                _initializationGate.Release();
            }
        }
    }

    private void SetActiveSession(SessionEntry session, Rectangle bounds, bool isVisible, bool moveFocus)
    {
        HideAllExcept(session);
        _activeSession = session;
        session.Bounds = bounds;
        ApplyControllerLayout(session, isVisible, moveFocus);
    }

    private void HideAllExcept(SessionEntry session)
    {
        foreach (SessionEntry candidate in _sessions.Values)
        {
            if (!ReferenceEquals(candidate, session) && candidate.Controller is not null)
            {
                candidate.Controller.IsVisible = false;
            }
        }
    }

    private static void ApplyControllerLayout(SessionEntry session, bool isVisible, bool moveFocus)
    {
        CoreWebView2Controller? controller = session.Controller;
        if (controller is null)
        {
            return;
        }

        bool canShow = isVisible
            && session.State.Status is not WebViewSessionStatus.Failed
            && session.State.Status is not WebViewSessionStatus.Offline;
        controller.Bounds = session.Bounds;
        controller.IsVisible = canShow;
        if (canShow && moveFocus)
        {
            controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
    }

    private void PublishCurrentState(SessionEntry session) =>
        Publish(
            session,
            session.State.Status is WebViewSessionStatus.Uninitialized
                ? WebViewSessionStatus.Ready
                : session.State.Status,
            session.State.ErrorTitle,
            session.State.ErrorMessage,
            session.State.ErrorCode);

    private static void ValidateBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "The WebView2 bounds must be positive.");
        }
    }

    public bool HasSession(Guid serviceInstanceId) => _sessions.ContainsKey(serviceInstanceId);

    public void DeactivateSession()
    {
        foreach (SessionEntry session in _sessions.Values)
        {
            if (session.Controller is not null)
            {
                session.Controller.IsVisible = false;
            }
        }

        _activeSession = null;
        State = WebViewSessionState.Uninitialized;
        StateChanged?.Invoke(this, new WebViewSessionStateChangedEventArgs(null, State));
    }

    public void GoBack()
    {
        SessionEntry? session = _activeSession;
        try
        {
            if (session?.CoreWebView is not null && !session.ProcessFailureDetected && session.CoreWebView.CanGoBack)
            {
                session.CoreWebView.GoBack();
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
            if (session?.CoreWebView is not null && !session.ProcessFailureDetected && session.CoreWebView.CanGoForward)
            {
                session.CoreWebView.GoForward();
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
            if (session?.CoreWebView is not null && !session.ProcessFailureDetected)
            {
                session.CoreWebView.Reload();
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
            if (session?.CoreWebView is null || session.ProcessFailureDetected)
            {
                return;
            }

            session.CoreWebView.Navigate(GetValidatedStartUri(session.ServiceInstance).AbsoluteUri);
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
            _ = ReleaseSessionCore(session, updateState: true);
        }
    }

    public async Task ReleaseSessionAsync(
        Guid serviceInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(serviceInstanceId, out SessionEntry? session))
        {
            _ = ReleaseSessionCore(session, updateState: true);
        }

        await _settlementTracker.WaitAsync(serviceInstanceId, cancellationToken);
    }

    public async Task<bool> ClearProfileAsync(
        ServiceInstance serviceInstance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceInstance);
        WebViewProfileIdentity identity = WebViewProfileIdentity.Create(serviceInstance);
        if (!_profilesBeingCleared.Add(identity.ServiceInstanceId))
        {
            throw new InvalidOperationException("The WebView2 profile is already being cleared.");
        }

        try
        {
            CoreWebView2Profile? profile = null;
            if (_sessions.TryGetValue(identity.ServiceInstanceId, out SessionEntry? session))
            {
                try
                {
                    profile = session.CoreWebView?.Profile;
                }
                catch (Exception exception) when (IsUnavailableControlException(exception))
                {
                    // Filesystem deletion after settlement remains available.
                }
            }

            await ReleaseSessionAsync(identity.ServiceInstanceId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (profile is not null)
                {
                    await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                }
            }
            catch (Exception exception) when (IsUnavailableControlException(exception) || exception is IOException)
            {
                // Deleting the isolated directory after settlement is the fallback.
            }

            return await profileCleaner.TryDeleteProfileAsync(
                identity.ServiceInstanceId,
                identity.ProfileName,
                cancellationToken);
        }
        finally
        {
            _profilesBeingCleared.Remove(identity.ServiceInstanceId);
        }
    }

    public void ReleaseAllSessions()
    {
        foreach (SessionEntry session in _sessions.Values.ToArray())
        {
            _ = ReleaseSessionCore(session, updateState: false);
        }

        DeactivateSession();
    }

    public void BeginShutdown()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _shutdownCancellation.Cancel();
        ReleaseAllSessions();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        BeginShutdown();
        _disposed = true;
        // Controller creation is an async COM operation that cannot be synchronously
        // interrupted. The shutdown token makes the continuation close a late
        // controller; disposing these primitives here would race that continuation.
    }

    private void ConfigureCoreWebView(SessionEntry session, CoreWebView2 coreWebView)
    {
        if (!session.SubscriptionGuard.TrySubscribe())
        {
            return;
        }

#if DEBUG
        coreWebView.Settings.AreDevToolsEnabled = true;
#else
        coreWebView.Settings.AreDevToolsEnabled = false;
#endif
        session.NavigationStartingHandler = (_, eventArgs) => OnNavigationStarting(session, eventArgs);
        session.NavigationCompletedHandler = (_, eventArgs) => OnNavigationCompleted(session, eventArgs);
        session.DocumentTitleChangedHandler = (_, _) => OnDocumentTitleChanged(session, coreWebView);
        session.HistoryChangedHandler = (_, _) => OnHistoryChanged(session);
        session.NewWindowRequestedHandler = (_, eventArgs) => OnNewWindowRequested(session, eventArgs);
        session.PermissionRequestedHandler = (_, eventArgs) => OnPermissionRequested(session, eventArgs);
        session.ProcessFailedHandler = (_, eventArgs) => OnProcessFailed(session, eventArgs);

        coreWebView.NavigationStarting += session.NavigationStartingHandler;
        coreWebView.NavigationCompleted += session.NavigationCompletedHandler;
        coreWebView.DocumentTitleChanged += session.DocumentTitleChangedHandler;
        coreWebView.HistoryChanged += session.HistoryChangedHandler;
        coreWebView.NewWindowRequested += session.NewWindowRequestedHandler;
        coreWebView.PermissionRequested += session.PermissionRequestedHandler;
        coreWebView.ProcessFailed += session.ProcessFailedHandler;

        session.NotificationReceivedHandler = (_, eventArgs) => OnNotificationReceived(session, eventArgs);
        try
        {
            coreWebView.NotificationReceived += session.NotificationReceivedHandler;
        }
        catch (Exception exception) when (exception is NotImplementedException or System.Runtime.InteropServices.COMException)
        {
            session.NotificationReceivedHandler = null;
        }
    }

    private static void UnsubscribeCoreWebView(SessionEntry session, CoreWebView2 coreWebView)
    {
        if (!session.SubscriptionGuard.TryUnsubscribe())
        {
            return;
        }

        coreWebView.NavigationStarting -= session.NavigationStartingHandler;
        coreWebView.NavigationCompleted -= session.NavigationCompletedHandler;
        coreWebView.DocumentTitleChanged -= session.DocumentTitleChangedHandler;
        coreWebView.HistoryChanged -= session.HistoryChangedHandler;
        coreWebView.NewWindowRequested -= session.NewWindowRequestedHandler;
        coreWebView.PermissionRequested -= session.PermissionRequestedHandler;
        coreWebView.ProcessFailed -= session.ProcessFailedHandler;
        if (session.NotificationReceivedHandler is not null)
        {
            try
            {
                coreWebView.NotificationReceived -= session.NotificationReceivedHandler;
            }
            catch (Exception exception) when (exception is NotImplementedException or System.Runtime.InteropServices.COMException)
            {
                // Older runtimes may not expose the optional notification event.
            }
        }
    }

    private void OnNavigationStarting(SessionEntry session, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!TryParseNavigationTarget(eventArgs.Uri, out Uri? target))
        {
            eventArgs.Cancel = true;
            PublishFailure(session, "Переход заблокирован", "WebView2 запросил некорректный адрес.", "InvalidUri");
            return;
        }

        WebNavigationDisposition disposition = webNavigationService.Route(
            session.ServiceInstance.ServiceType,
            target,
            eventArgs.IsUserInitiated);
        if (disposition is WebNavigationDisposition.Internal)
        {
            Publish(session, WebViewSessionStatus.Navigating);
            return;
        }

        eventArgs.Cancel = true;
        session.CancelledExternalNavigations.Add(eventArgs.NavigationId);
        if (disposition is WebNavigationDisposition.ExternalOpened or WebNavigationDisposition.External)
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
        session.FirstNavigationCompleted.TrySetResult(eventArgs.IsSuccess);

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
            WebViewErrorClassifier.GetUserMessage(
                eventArgs.WebErrorStatus,
                serviceCatalog.Get(session.ServiceInstance.ServiceType).DisplayName),
            eventArgs.WebErrorStatus.ToString());
    }

    private void OnHistoryChanged(SessionEntry session) =>
        Publish(session, session.State.Status, session.State.ErrorTitle, session.State.ErrorMessage, session.State.ErrorCode);

    private void OnDocumentTitleChanged(SessionEntry session, CoreWebView2 coreWebView)
    {
        if (!IsCurrentSession(session))
        {
            return;
        }

        string documentTitle;
        try
        {
            documentTitle = coreWebView.DocumentTitle;
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            return;
        }

        DocumentTitleChanged?.Invoke(
            this,
            new ServiceDocumentTitleChangedEventArgs(
                session.ServiceInstance.Id,
                session.ServiceInstance.ServiceType,
                documentTitle));
    }

    private void OnNewWindowRequested(SessionEntry session, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;

        if (!TryParseNavigationTarget(eventArgs.Uri, out Uri? target))
        {
            PublishFailure(session, "Окно заблокировано", "Сервис запросил некорректный адрес нового окна.", "InvalidPopupUri");
            return;
        }

        WebNavigationDisposition disposition = newWindowNavigationService.Route(
            session.ServiceInstance,
            target,
            eventArgs.IsUserInitiated,
            internalTarget => session.CoreWebView?.Navigate(internalTarget.AbsoluteUri));

        if (disposition is WebNavigationDisposition.Blocked)
        {
            PublishFailure(
                session,
                "Внешняя ссылка заблокирована",
                "Адрес нельзя безопасно открыть в системном браузере.",
                "UnsupportedExternalUri");
        }
    }

    private async void OnPermissionRequested(
        SessionEntry session,
        CoreWebView2PermissionRequestedEventArgs eventArgs)
    {
        if (eventArgs.PermissionKind is not CoreWebView2PermissionKind.Notifications)
        {
            return;
        }

        CoreWebView2Deferral? deferral = null;
        try
        {
            deferral = eventArgs.GetDeferral();
            eventArgs.Handled = true;
            eventArgs.SavesInProfile = SaveNotificationPermissionsInProfile;
            NotificationPermissionState decision = await permissionCoordinator.DecideAsync(
                session.ServiceInstance,
                eventArgs.Uri);
            eventArgs.State = decision is NotificationPermissionState.Allowed
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or OperationCanceledException
                or System.Runtime.InteropServices.COMException)
        {
            try
            {
                eventArgs.Handled = true;
                eventArgs.State = CoreWebView2PermissionState.Deny;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The WebView was disposed while the permission prompt was open.
            }
        }
        finally
        {
            try
            {
                deferral?.Complete();
                deferral?.Dispose();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // A disposed WebView no longer accepts a completed deferral.
            }
        }
    }

    private void OnNotificationReceived(
        SessionEntry session,
        CoreWebView2NotificationReceivedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        WebNotificationLifecycle lifecycle = new(
            eventArgs.Notification.ReportShown,
            eventArgs.Notification.ReportClicked,
            eventArgs.Notification.ReportClosed);
        if (!IsCurrentSession(session))
        {
            lifecycle.CompleteSuppressed();
            return;
        }

        EventHandler<WebNotificationReceivedEventArgs>? handler = NotificationReceived;
        if (handler is null)
        {
            lifecycle.CompleteSuppressed();
            return;
        }

        handler.Invoke(
            this,
            new WebNotificationReceivedEventArgs(
                session.ServiceInstance.Id,
                session.ServiceInstance.ServiceType,
                eventArgs.SenderOrigin,
                eventArgs.Notification.Title ?? string.Empty,
                eventArgs.Notification.Body ?? string.Empty,
                lifecycle,
                HashNotificationTag(eventArgs.Notification.Tag)));
    }

    internal static string? HashNotificationTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        return Convert.ToHexString(hash);
    }

    private void OnBackgroundNotificationActivityReceived(SessionEntry session)
    {
        if (_shutdownStarted || !IsCurrentSession(session))
        {
            return;
        }

        BackgroundNotificationActivityReceived?.Invoke(
            this,
            new BackgroundNotificationActivityReceivedEventArgs(
                session.ServiceInstance.Id,
                session.ServiceInstance.ServiceType));
    }

    private async Task SynchronizeMaxNotificationPermissionAsync(
        ServiceInstance service,
        CoreWebView2Profile profile,
        CancellationToken cancellationToken)
    {
        CoreWebView2PermissionState profileState = await ReadExactMaxNotificationPermissionAsync(profile);
        cancellationToken.ThrowIfCancellationRequested();
        await permissionCoordinator.SynchronizeFromProfileAsync(
            service,
            MaxNotificationOrigin,
            MapProfileNotificationPermission(profileState),
            cancellationToken);
    }

    private static async Task<CoreWebView2PermissionState> ReadExactMaxNotificationPermissionAsync(
        CoreWebView2Profile profile)
    {
        IReadOnlyList<CoreWebView2PermissionSetting> settings =
            await profile.GetNonDefaultPermissionSettingsAsync();
        CoreWebView2PermissionSetting? setting = settings.FirstOrDefault(candidate =>
            candidate.PermissionKind is CoreWebView2PermissionKind.Notifications
            && IsExactMaxOrigin(candidate.PermissionOrigin));
        return setting?.PermissionState ?? CoreWebView2PermissionState.Default;
    }

    internal static NotificationPermissionState MapProfileNotificationPermission(
        CoreWebView2PermissionState profileState) =>
        profileState switch
        {
            CoreWebView2PermissionState.Allow => NotificationPermissionState.Allowed,
            CoreWebView2PermissionState.Deny => NotificationPermissionState.Denied,
            _ => NotificationPermissionState.Unknown
        };

    internal static bool IsExactMaxOrigin(string permissionOrigin) =>
        Uri.TryCreate(permissionOrigin, UriKind.Absolute, out Uri? origin)
        && origin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && origin.Host.Equals("web.max.ru", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(origin.UserInfo)
        && origin.IsDefaultPort;

    private void OnProcessFailed(SessionEntry session, CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        if (!IsCurrentSession(session))
        {
            return;
        }

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

    internal static bool TryParseNavigationTarget(
        string? address,
        [NotNullWhen(true)] out Uri? target) =>
        Uri.TryCreate(address, UriKind.Absolute, out target);

    private void Publish(
        SessionEntry session,
        WebViewSessionStatus status,
        string? errorTitle = null,
        string? errorMessage = null,
        string? errorCode = null)
    {
        if (!IsCurrentSession(session))
        {
            return;
        }

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
        StateChanged?.Invoke(
            this,
            new WebViewSessionStateChangedEventArgs(session.ServiceInstance.Id, State));
    }

    private Task ReleaseSessionCore(SessionEntry session, bool updateState)
    {
        Task settlement = session.Lifetime.BeginRelease();
        _settlementTracker.Track(session.ServiceInstance.Id, settlement);

        session.VkBackgroundNotificationMonitor?.Dispose();
        session.VkBackgroundNotificationMonitor = null;

        try
        {
            if (session.CoreWebView is CoreWebView2 coreWebView)
            {
                UnsubscribeCoreWebView(session, coreWebView);
            }
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            // A crashed or disposed controller has no usable events left to detach.
        }

        CoreWebView2Controller? controller = session.Controller;
        if (controller is not null)
        {
            CloseController(session, controller);
        }

        session.Controller = null;
        session.CoreWebView = null;
        if (_sessions.TryGetValue(session.ServiceInstance.Id, out SessionEntry? current)
            && ReferenceEquals(current, session))
        {
            _sessions.Remove(session.ServiceInstance.Id);
        }
        session.CancelledExternalNavigations.Clear();

        if (ReferenceEquals(_activeSession, session))
        {
            _activeSession = null;
            if (updateState)
            {
                State = WebViewSessionState.Uninitialized;
                StateChanged?.Invoke(this, new WebViewSessionStateChangedEventArgs(null, State));
            }
        }

        return settlement;
    }

    private static void CloseController(SessionEntry session, CoreWebView2Controller controller)
    {
        if (!session.CloseGuard.TryBeginClose())
        {
            return;
        }

        try
        {
            controller.Close();
        }
        catch (Exception exception) when (IsUnavailableControlException(exception))
        {
            // The controller is already unavailable; the close contract is still settled.
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
        _ = WebViewProfileIdentity.Create(serviceInstance);
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
        return !session.Lifetime.IsReleased
            && session.Controller is not null
            && session.CoreWebView is not null;
    }

    private bool IsCurrentSession(SessionEntry session) =>
        !session.Lifetime.IsReleased
        && _sessions.TryGetValue(session.ServiceInstance.Id, out SessionEntry? current)
        && ReferenceEquals(current, session);

    private static (bool CanGoBack, bool CanGoForward) ReadHistoryState(SessionEntry session)
    {
        try
        {
            return session.ProcessFailureDetected
                ? (false, false)
                : (session.CoreWebView?.CanGoBack == true, session.CoreWebView?.CanGoForward == true);
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

    private sealed class SessionEntry(ServiceInstance serviceInstance, IntPtr parentWindow, Rectangle bounds)
    {
        public ServiceInstance ServiceInstance { get; set; } = serviceInstance;
        public IntPtr ParentWindow { get; } = parentWindow;
        public Rectangle Bounds { get; set; } = bounds;
        public CoreWebView2Controller? Controller { get; set; }
        public CoreWebView2? CoreWebView { get; set; }
        public WebViewSessionLifetime Lifetime { get; } = new();
        public TaskCompletionSource<bool> FirstNavigationCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PrimeVisibleWhileParentHidden { get; set; }
        public WebViewControllerCloseGuard CloseGuard { get; } = new();
        public WebViewSessionState State { get; set; } = WebViewSessionState.Uninitialized;
        public HashSet<ulong> CancelledExternalNavigations { get; } = [];
        public bool ProcessFailureDetected { get; set; }
        public bool AutomaticProcessRecoveryUsed { get; set; }
        public WebViewEventSubscriptionGuard SubscriptionGuard { get; } = new();
        public EventHandler<CoreWebView2NavigationStartingEventArgs>? NavigationStartingHandler { get; set; }
        public EventHandler<CoreWebView2NavigationCompletedEventArgs>? NavigationCompletedHandler { get; set; }
        public EventHandler<object>? DocumentTitleChangedHandler { get; set; }
        public EventHandler<object>? HistoryChangedHandler { get; set; }
        public EventHandler<CoreWebView2NewWindowRequestedEventArgs>? NewWindowRequestedHandler { get; set; }
        public EventHandler<CoreWebView2PermissionRequestedEventArgs>? PermissionRequestedHandler { get; set; }
        public EventHandler<CoreWebView2NotificationReceivedEventArgs>? NotificationReceivedHandler { get; set; }
        public EventHandler<CoreWebView2ProcessFailedEventArgs>? ProcessFailedHandler { get; set; }
        public VkBackgroundNotificationMonitor? VkBackgroundNotificationMonitor { get; set; }
    }
}
