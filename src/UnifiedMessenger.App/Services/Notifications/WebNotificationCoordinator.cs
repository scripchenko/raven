using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WebNotificationCoordinator : IWebNotificationCoordinator
{
    public const int MaximumVisiblePopups = 3;
    public const int MaximumPendingNotifications = 20;

    private readonly Queue<PendingNotification> _pending = new();
    private readonly Dictionary<Guid, ActiveNotification> _activePopups = [];
    private readonly INotificationPopupService _popupService;
    private readonly ITrayIconService _trayIcon;
    private readonly IWindowActivationService _windowActivation;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly NavigationPolicy _navigationPolicy;
    private readonly IServiceActivityCoordinator _activityCoordinator;
    private readonly IBuiltInServiceCatalog _serviceCatalog;
    private readonly ITelegramNotificationSoundCoordinator _telegramSoundCoordinator;
    private ActiveNotification? _activeBalloon;
    private bool _shutdown;
    private bool _disposed;

    public WebNotificationCoordinator(
        INotificationPopupService popupService,
        ITrayIconService trayIcon,
        IWindowActivationService windowActivation,
        IApplicationSettingsStore settingsStore,
        NavigationPolicy navigationPolicy,
        IServiceActivityCoordinator activityCoordinator,
        IBuiltInServiceCatalog serviceCatalog,
        ITelegramNotificationSoundCoordinator telegramSoundCoordinator)
    {
        _popupService = popupService;
        _trayIcon = trayIcon;
        _windowActivation = windowActivation;
        _settingsStore = settingsStore;
        _navigationPolicy = navigationPolicy;
        _activityCoordinator = activityCoordinator;
        _serviceCatalog = serviceCatalog;
        _telegramSoundCoordinator = telegramSoundCoordinator;
        _popupService.Clicked += OnPopupClicked;
        _popupService.Closed += OnPopupClosed;
        _trayIcon.BalloonClicked += OnBalloonClicked;
        _trayIcon.BalloonClosed += OnBalloonClosed;
    }

    public int PendingCount => _pending.Count;
    public bool HasActiveNotification => _activePopups.Count > 0 || _activeBalloon is not null;

    public void Handle(WebNotificationRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (_shutdown || !IsOfficialOrigin(request))
        {
            request.Lifecycle.CompleteSuppressed();
            return;
        }

        ServiceInstance? service = FindService(request.ServiceInstanceId);
        if (service is null)
        {
            request.Lifecycle.CompleteSuppressed();
            return;
        }

        bool isSelectedActive = IsSelectedAndActive(service);
        if (!isSelectedActive && service.IsEnabled)
        {
            _activityCoordinator.MarkNotificationReceived(service);
        }

        if (ShouldSuppress(service))
        {
            request.Lifecycle.CompleteSuppressed();
            return;
        }

        _telegramSoundCoordinator.RequestSound(
            new TelegramNotificationSoundRequest(
                service.Id,
                service.ServiceType,
                request.NotificationTagHash));

        bool showPreview = _settingsStore.Current.Notifications.ShowNotificationPreview;
        WebNotificationRequest inMemoryRequest = showPreview
            ? request
            : request with { Title = string.Empty, Body = string.Empty };
        Enqueue(new PendingNotification(inMemoryRequest, showPreview));
        TryShowAvailable();
    }

    public void DiscardPending(Guid serviceInstanceId)
    {
        int count = _pending.Count;
        for (int index = 0; index < count; index++)
        {
            PendingNotification pending = _pending.Dequeue();
            if (pending.Request.ServiceInstanceId == serviceInstanceId)
            {
                pending.Request.Lifecycle.CompleteSuppressed();
            }
            else
            {
                _pending.Enqueue(pending);
            }
        }

        foreach (Guid notificationId in _activePopups
                     .Where(pair => pair.Value.Request.ServiceInstanceId == serviceInstanceId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            CloseActivePopup(notificationId);
        }

        if (_activeBalloon?.Request.ServiceInstanceId == serviceInstanceId)
        {
            ActiveNotification active = _activeBalloon;
            _activeBalloon = null;
            active.Request.Lifecycle.ReportClosed();
        }

        TryShowAvailable();
    }

    public void OnDoNotDisturbChanged(bool enabled)
    {
        if (enabled)
        {
            ClearAll();
        }
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _telegramSoundCoordinator.Shutdown();
        ClearAll();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shutdown();
        _popupService.Clicked -= OnPopupClicked;
        _popupService.Closed -= OnPopupClosed;
        _trayIcon.BalloonClicked -= OnBalloonClicked;
        _trayIcon.BalloonClosed -= OnBalloonClosed;
    }

    private bool IsOfficialOrigin(WebNotificationRequest request) =>
        Uri.TryCreate(request.SenderOrigin, UriKind.Absolute, out Uri? origin)
        && FindService(request.ServiceInstanceId) is ServiceInstance service
        && _navigationPolicy.IsAllowedTopLevelNavigation(service.ServiceType, origin);

    private void Enqueue(PendingNotification pending)
    {
        if (_pending.Count >= MaximumPendingNotifications)
        {
            _pending.Dequeue().Request.Lifecycle.CompleteSuppressed();
        }

        _pending.Enqueue(pending);
    }

    private void TryShowAvailable()
    {
        if (_shutdown)
        {
            return;
        }

        while (_activePopups.Count < MaximumVisiblePopups
               && _pending.TryDequeue(out PendingNotification? pending))
        {
            WebNotificationRequest request = pending.Request;
            ServiceInstance? service = FindService(request.ServiceInstanceId);
            if (service is null || ShouldSuppress(service))
            {
                request.Lifecycle.CompleteSuppressed();
                continue;
            }

            string serviceName = _serviceCatalog.Get(service.ServiceType).DisplayName;
            Guid notificationId = Guid.NewGuid();
            NotificationPopupDisplayModel popup = CreatePopupModel(
                notificationId,
                service,
                serviceName,
                pending);
            if (_popupService.TryShow(popup))
            {
                _activePopups.Add(notificationId, new ActiveNotification(notificationId, request));
                request.Lifecycle.ReportShown();
                continue;
            }

            if (_activeBalloon is null
                && _trayIcon.TryShowBalloon(service.DisplayName, $"Новое событие в {serviceName}"))
            {
                _activeBalloon = new ActiveNotification(notificationId, request);
                request.Lifecycle.ReportShown();
                continue;
            }

            if (_activeBalloon is not null)
            {
                _pending.Enqueue(pending);
                return;
            }

            request.Lifecycle.CompleteSuppressed();
        }
    }

    private NotificationPopupDisplayModel CreatePopupModel(
        Guid notificationId,
        ServiceInstance service,
        string serviceName,
        PendingNotification pending)
    {
        string genericTitle = $"Новое сообщение в {serviceName}";
        string title = pending.ShowPreview && !string.IsNullOrWhiteSpace(pending.Request.Title)
            ? pending.Request.Title
            : genericTitle;
        string body = pending.ShowPreview ? pending.Request.Body : string.Empty;
        return new NotificationPopupDisplayModel(
            notificationId,
            service.Id,
            serviceName,
            title,
            body);
    }

    private bool ShouldSuppress(ServiceInstance service)
    {
        NotificationSettings notifications = _settingsStore.Current.Notifications;
        return !service.IsEnabled
            || !notifications.IsEnabled
            || notifications.DoNotDisturb
            || service.IsMuted
            || service.NotificationPermissionState is not NotificationPermissionState.Allowed
            || IsSelectedAndActive(service);
    }

    private bool IsSelectedAndActive(ServiceInstance service) =>
        _windowActivation.IsMainWindowActive
        && _windowActivation.SelectedServiceId == service.Id;

    private ServiceInstance? FindService(Guid serviceInstanceId) =>
        _settingsStore.Current.Services.FirstOrDefault(candidate => candidate.Id == serviceInstanceId);

    private void OnPopupClicked(object? sender, NotificationPopupEventArgs eventArgs)
    {
        if (!_activePopups.Remove(eventArgs.NotificationId, out ActiveNotification? active))
        {
            return;
        }

        active.Request.Lifecycle.ReportClicked();
        ServiceInstance? service = FindService(active.Request.ServiceInstanceId);
        if (service is not null)
        {
            _activityCoordinator.Clear(service);
        }

        _windowActivation.ShowAndActivate(active.Request.ServiceInstanceId);
        TryShowAvailable();
    }

    private void OnPopupClosed(object? sender, NotificationPopupEventArgs eventArgs)
    {
        if (!_activePopups.Remove(eventArgs.NotificationId, out ActiveNotification? active))
        {
            return;
        }

        active.Request.Lifecycle.ReportClosed();
        TryShowAvailable();
    }

    private void OnBalloonClicked(object? sender, EventArgs eventArgs)
    {
        ActiveNotification? active = TakeActiveBalloon();
        if (active is null)
        {
            return;
        }

        active.Request.Lifecycle.ReportClicked();
        ServiceInstance? service = FindService(active.Request.ServiceInstanceId);
        if (service is not null)
        {
            _activityCoordinator.Clear(service);
        }

        _windowActivation.ShowAndActivate(active.Request.ServiceInstanceId);
        TryShowAvailable();
    }

    private void OnBalloonClosed(object? sender, EventArgs eventArgs)
    {
        ActiveNotification? active = TakeActiveBalloon();
        if (active is null)
        {
            return;
        }

        active.Request.Lifecycle.ReportClosed();
        TryShowAvailable();
    }

    private ActiveNotification? TakeActiveBalloon()
    {
        ActiveNotification? active = _activeBalloon;
        _activeBalloon = null;
        return active;
    }

    private void CloseActivePopup(Guid notificationId)
    {
        if (!_activePopups.Remove(notificationId, out ActiveNotification? active))
        {
            return;
        }

        _popupService.Close(notificationId);
        active.Request.Lifecycle.ReportClosed();
    }

    private void ClearAll()
    {
        while (_pending.TryDequeue(out PendingNotification? pending))
        {
            pending.Request.Lifecycle.CompleteSuppressed();
        }

        ActiveNotification[] activePopups = _activePopups.Values.ToArray();
        _activePopups.Clear();
        _popupService.CloseAll();
        foreach (ActiveNotification active in activePopups)
        {
            active.Request.Lifecycle.ReportClosed();
        }

        ActiveNotification? balloon = TakeActiveBalloon();
        balloon?.Request.Lifecycle.ReportClosed();
    }

    private sealed record PendingNotification(WebNotificationRequest Request, bool ShowPreview);
    private sealed record ActiveNotification(Guid NotificationId, WebNotificationRequest Request);
}
