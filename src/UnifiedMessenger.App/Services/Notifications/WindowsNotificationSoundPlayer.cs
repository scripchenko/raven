using System.Media;
using System.IO;
using System.Windows.Media;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WindowsNotificationSoundPlayer(
    IApplicationSettingsStore settingsStore,
    ILanternSoundFileService soundFileService,
    IUiDispatcher uiDispatcher) : INotificationSoundPlayer, IDisposable
{
    private readonly HashSet<MediaPlayer> _activePlayers = [];
    private bool _disposed;

    public bool TryPlay(ServiceType serviceType)
    {
        if (_disposed)
        {
            return false;
        }

        return QueueLanternSound();
    }

    public bool TryPreviewLanternSound() => !_disposed && QueueLanternSound();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        uiDispatcher.Post(
            () =>
            {
                foreach (MediaPlayer player in _activePlayers.ToArray())
                {
                    ClosePlayer(player);
                }
            });
    }

    private bool QueueLanternSound()
    {
        string playbackPath = soundFileService.ResolvePlaybackPath(settingsStore.Current.Notifications);
        if (!File.Exists(playbackPath))
        {
            return false;
        }

        string defaultPath = soundFileService.DefaultSoundPath;
        bool canFallback = !string.Equals(playbackPath, defaultPath, StringComparison.OrdinalIgnoreCase);
        uiDispatcher.Post(() => StartPlayback(playbackPath, defaultPath, canFallback));
        return true;
    }

    private void StartPlayback(string playbackPath, string defaultPath, bool canFallback)
    {
        if (_disposed)
        {
            return;
        }

        MediaPlayer player = new() { Volume = 1.0 };
        _activePlayers.Add(player);
        player.MediaOpened += OnMediaOpened;
        player.MediaEnded += OnMediaEnded;
        player.MediaFailed += OnMediaFailed;
        try
        {
            player.Open(new Uri(playbackPath, UriKind.Absolute));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or NotSupportedException)
        {
            ClosePlayer(player);
            TryFallback(defaultPath, canFallback);
        }

        void OnMediaOpened(object? sender, EventArgs eventArgs)
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }

        void OnMediaEnded(object? sender, EventArgs eventArgs) => ClosePlayer(player);

        void OnMediaFailed(object? sender, ExceptionEventArgs eventArgs)
        {
            ClosePlayer(player);
            TryFallback(defaultPath, canFallback);
        }
    }

    private void TryFallback(string defaultPath, bool canFallback)
    {
        if (canFallback && File.Exists(defaultPath))
        {
            StartPlayback(defaultPath, defaultPath, canFallback: false);
            return;
        }

        SystemSounds.Asterisk.Play();
    }

    private void ClosePlayer(MediaPlayer player)
    {
        if (_activePlayers.Remove(player))
        {
            player.Close();
        }
    }
}
