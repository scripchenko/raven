using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class TelegramNotificationSoundCoordinator : ITelegramNotificationSoundCoordinator
{
    public static readonly TimeSpan EmptyTagDebounce = TimeSpan.FromSeconds(1);
    internal const int MaximumRememberedTagHashesPerSession = 256;

    private readonly object _gate = new();
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly INotificationSoundPlayer _soundPlayer;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, SessionDedupeState> _dedupeBySession = [];
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
        if (!SupportsNotificationSound(request.ServiceType) || !CanPlaySound(request))
        {
            return false;
        }

        lock (_gate)
        {
            if (_shutdown || !TryReserveDedupeSlot(request))
            {
                return false;
            }
        }

        _uiDispatcher.Post(() => PlayPendingSound(request));
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
            _dedupeBySession.Clear();
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

    private void PlayPendingSound(TelegramNotificationSoundRequest request)
    {
        lock (_gate)
        {
            if (_shutdown)
            {
                return;
            }

            if (!CanPlaySound(request))
            {
                return;
            }

            _ = _soundPlayer.TryPlay(request.ServiceType);
        }
    }

    private bool CanPlaySound(TelegramNotificationSoundRequest request)
    {
        NotificationSettings notifications = _settingsStore.Current.Notifications;
        ServiceInstance? service = _settingsStore.Current.Services.FirstOrDefault(
            candidate => candidate.Id == request.ServiceInstanceId);
        return service is
            {
                IsEnabled: true,
                IsMuted: false,
                NotificationPermissionState: NotificationPermissionState.Allowed
            }
            && service.ServiceType == request.ServiceType
            && SupportsNotificationSound(service.ServiceType)
            && notifications.GetSoundMode(service.ServiceType) is NotificationSoundMode.Lantern
            && notifications.IsEnabled
            && notifications.PlaySound
            && !notifications.DoNotDisturb;
    }

    private static bool SupportsNotificationSound(ServiceType serviceType) =>
        serviceType is ServiceType.Telegram or ServiceType.WhatsApp or ServiceType.Max;

    private bool TryReserveDedupeSlot(TelegramNotificationSoundRequest request)
    {
        if (!_dedupeBySession.TryGetValue(request.ServiceInstanceId, out SessionDedupeState? state))
        {
            state = new SessionDedupeState();
            _dedupeBySession.Add(request.ServiceInstanceId, state);
        }

        if (!string.IsNullOrWhiteSpace(request.NotificationTagHash))
        {
            if (!state.RememberedTagHashes.Add(request.NotificationTagHash))
            {
                return false;
            }

            state.TagHashOrder.Enqueue(request.NotificationTagHash);
            while (state.TagHashOrder.Count > MaximumRememberedTagHashesPerSession)
            {
                _ = state.RememberedTagHashes.Remove(state.TagHashOrder.Dequeue());
            }

            return true;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (state.LastEmptyTagSoundAt is DateTimeOffset previous
            && now - previous < EmptyTagDebounce)
        {
            return false;
        }

        state.LastEmptyTagSoundAt = now;
        return true;
    }

    private sealed class SessionDedupeState
    {
        public HashSet<string> RememberedTagHashes { get; } = new(StringComparer.Ordinal);
        public Queue<string> TagHashOrder { get; } = [];
        public DateTimeOffset? LastEmptyTagSoundAt { get; set; }
    }
}
