namespace UnifiedMessenger.App.Services.Notifications;

public sealed class NotificationPopupEventArgs(Guid notificationId) : EventArgs
{
    public Guid NotificationId { get; } = notificationId;
}
