using System.Media;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WindowsNotificationSoundPlayer : INotificationSoundPlayer
{
    public bool TryPlay(ServiceType serviceType, NotificationSoundMode mode)
    {
        if (mode is not NotificationSoundMode.System)
        {
            return false;
        }

        SystemSounds.Asterisk.Play();
        return true;
    }
}
