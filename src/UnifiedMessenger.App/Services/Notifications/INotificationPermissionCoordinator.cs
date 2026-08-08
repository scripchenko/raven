using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public interface INotificationPermissionCoordinator
{
    Task<NotificationPermissionState> DecideAsync(
        ServiceInstance service,
        string? senderOrigin,
        CancellationToken cancellationToken = default);
}
