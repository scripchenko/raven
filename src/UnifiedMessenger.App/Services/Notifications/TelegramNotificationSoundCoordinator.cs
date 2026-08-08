using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class TelegramNotificationSoundCoordinator : ITelegramNotificationSoundCoordinator
{
    public static readonly TimeSpan SoundCooldown = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly INotificationSoundPlayer _soundPlayer;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _lastSoundAt;
    private long _pendingGeneration;
    private bool _soundPending;
    private bool _shutdown;
    private bool _disposed;

    public TelegramNotificationSoundCoordinator(
        IApplicationSettingsStore settingsStore,
        INotificationSoundPlayer soundPlayer,
        IUiDispatcher uiDispatcher,
        TimeProvider timeProvider)
    {
        _settingsStore = settingsStore;
        _soundPlayer = soundPlayer;
        _uiDispatcher = uiDispatcher;
        _timeProvider = timeProvider;
    }

    public bool IsShutdownStarted
    {
        get
        {
            lock (_gate)
            {
                return _shutdown;
            }
        }
    }

    public bool RequestSound(TelegramNotificationSoundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ServiceType is not ServiceType.Telegram || !CanPlaySound(request.ServiceInstanceId))
        {
            return false;
        }

        long generation;
        lock (_gate)
        {
            if (_shutdown || _soundPending || IsInsideCooldown())
            {
                return false;
            }

            _soundPending = true;
            generation = ++_pendingGeneration;
        }

        _uiDispatcher.Post(() => PlayPendingSound(request.ServiceInstanceId, generation));
        return true;
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _soundPending = false;
            _pendingGeneration++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Shutdown();
    }

    private void PlayPendingSound(Guid serviceInstanceId, long generation)
    {
        lock (_gate)
        {
            if (_shutdown || !_soundPending || generation != _pendingGeneration)
            {
                return;
            }

            _soundPending = false;
            if (IsInsideCooldown() || !CanPlaySound(serviceInstanceId))
            {
                return;
            }

            _lastSoundAt = _timeProvider.GetUtcNow();
            _soundPlayer.Play();
        }
    }

    private bool CanPlaySound(Guid serviceInstanceId)
    {
        NotificationSettings notifications = _settingsStore.Current.Notifications;
        ServiceInstance? service = _settingsStore.Current.Services.FirstOrDefault(
            candidate => candidate.Id == serviceInstanceId);
        return service is
            {
                ServiceType: ServiceType.Telegram,
                IsEnabled: true,
                IsMuted: false,
                NotificationPermissionState: NotificationPermissionState.Allowed
            }
            && notifications.IsEnabled
            && notifications.PlaySound
            && !notifications.DoNotDisturb;
    }

    private bool IsInsideCooldown() =>
        _lastSoundAt is DateTimeOffset lastSoundAt
        && _timeProvider.GetUtcNow() - lastSoundAt < SoundCooldown;
}
