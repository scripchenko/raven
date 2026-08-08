namespace UnifiedMessenger.App.Services.Notifications;

public sealed record WebNotificationRequest(
    Guid ServiceInstanceId,
    string SenderOrigin,
    string Title,
    string Body,
    IWebNotificationLifecycle Lifecycle);
