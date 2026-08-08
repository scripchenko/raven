namespace UnifiedMessenger.App.Services.Notifications;

public sealed record NotificationPopupDisplayModel(
    Guid NotificationId,
    Guid ServiceInstanceId,
    string ServiceName,
    string Title,
    string Body);
