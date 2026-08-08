using System.Media;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WindowsNotificationSoundPlayer : INotificationSoundPlayer
{
    public void Play() => SystemSounds.Asterisk.Play();
}
