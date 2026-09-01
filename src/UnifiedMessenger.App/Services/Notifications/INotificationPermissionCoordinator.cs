using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public interface INotificationPermissionCoordinator
{
    Task<NotificationPermissionState> DecideAsync(
        ServiceInstance service,
        string? senderOrigin,
        CancellationToken cancellationToken = default);

    Task<NotificationPermissionState> SynchronizeFromProfileAsync(
        ServiceInstance service,
        string? permissionOrigin,
        NotificationPermissionState profileState,
        CancellationToken cancellationToken = default);
}
