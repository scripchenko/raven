using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WebViewEventCoordinator : IWebViewEventCoordinator
{
    private readonly IWebViewSessionManager _sessionManager;
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly IServiceActivityCoordinator _activityCoordinator;
    private readonly IWebNotificationCoordinator _notificationCoordinator;
    private readonly IWindowActivationService _windowActivation;
    private bool _disposed;

    public WebViewEventCoordinator(
        IWebViewSessionManager sessionManager,
        IApplicationSettingsStore settingsStore,
        IServiceActivityCoordinator activityCoordinator,
        IWebNotificationCoordinator notificationCoordinator,
        IWindowActivationService windowActivation)
    {
        _sessionManager = sessionManager;
        _settingsStore = settingsStore;
        _activityCoordinator = activityCoordinator;
        _notificationCoordinator = notificationCoordinator;
        _windowActivation = windowActivation;
        _sessionManager.DocumentTitleChanged += OnDocumentTitleChanged;
        _sessionManager.NotificationReceived += OnNotificationReceived;
        _sessionManager.BackgroundNotificationActivityReceived += OnBackgroundNotificationActivityReceived;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionManager.DocumentTitleChanged -= OnDocumentTitleChanged;
        _sessionManager.NotificationReceived -= OnNotificationReceived;
        _sessionManager.BackgroundNotificationActivityReceived -= OnBackgroundNotificationActivityReceived;
    }

    private void OnDocumentTitleChanged(object? sender, ServiceDocumentTitleChangedEventArgs eventArgs)
    {
        ServiceInstance? service = FindEnabledService(eventArgs.ServiceInstanceId, eventArgs.ServiceType);
        if (service is not null && service.ServiceType is not ServiceType.VkMessenger)
        {
            bool isBeingViewed = _windowActivation.IsMainWindowActive
                && _windowActivation.SelectedServiceId == service.Id;
            _activityCoordinator.UpdateFromDocumentTitle(service, eventArgs.DocumentTitle, !isBeingViewed);
        }
    }

    private void OnNotificationReceived(object? sender, WebNotificationReceivedEventArgs eventArgs)
    {
        if (FindEnabledService(eventArgs.ServiceInstanceId, eventArgs.ServiceType) is null)
        {
            eventArgs.Lifecycle.CompleteSuppressed();
            return;
        }

        _notificationCoordinator.Handle(
            new WebNotificationRequest(
                eventArgs.ServiceInstanceId,
                eventArgs.SenderOrigin,
                eventArgs.Title,
                eventArgs.Body,
                eventArgs.Lifecycle,
                eventArgs.NotificationTagHash));
    }

    private void OnBackgroundNotificationActivityReceived(
        object? sender,
        BackgroundNotificationActivityReceivedEventArgs eventArgs)
    {
        ServiceInstance? service = FindEnabledService(eventArgs.ServiceInstanceId, eventArgs.ServiceType);
        if (service is null)
        {
            return;
        }

        bool isBeingViewed = _windowActivation.IsMainWindowActive
            && _windowActivation.SelectedServiceId == service.Id;
        if (!isBeingViewed)
        {
            _activityCoordinator.MarkNotificationReceived(service);
        }
    }

    private ServiceInstance? FindEnabledService(Guid serviceInstanceId, ServiceType serviceType) =>
        _settingsStore.Current.Services.FirstOrDefault(
            service => service.Id == serviceInstanceId
                && service.ServiceType == serviceType
                && service.IsEnabled);
}
