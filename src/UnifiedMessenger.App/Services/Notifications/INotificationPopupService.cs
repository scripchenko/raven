namespace UnifiedMessenger.App.Services.Notifications;

public interface INotificationPopupService : IDisposable
{
    event EventHandler<NotificationPopupEventArgs>? Clicked;
    event EventHandler<NotificationPopupEventArgs>? Closed;

    int VisibleCount { get; }

    bool TryShow(NotificationPopupDisplayModel notification);
    void Close(Guid notificationId);
    void CloseAll();
}
