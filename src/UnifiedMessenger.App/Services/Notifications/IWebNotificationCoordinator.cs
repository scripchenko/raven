namespace UnifiedMessenger.App.Services.Notifications;

public interface IWebNotificationCoordinator : IDisposable
{
    int PendingCount { get; }
    bool HasActiveNotification { get; }
    void Handle(WebNotificationRequest request);
    void DiscardPending(Guid serviceInstanceId);
    void OnDoNotDisturbChanged(bool enabled);
    void Shutdown();
}
