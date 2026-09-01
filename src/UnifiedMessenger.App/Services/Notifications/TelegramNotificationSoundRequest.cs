using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed record TelegramNotificationSoundRequest(
    Guid ServiceInstanceId,
    ServiceType ServiceType,
    string? NotificationTagHash = null);
