using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public interface INotificationSoundPlayer
{
    bool TryPlay(ServiceType serviceType);
    bool TryPreviewLanternSound();
}
