using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace UnifiedMessenger.App.Models;

public sealed partial class ServiceInstance : ObservableObject
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private ServiceType _serviceType;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string? _startUrl;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private int _sortOrder;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private NotificationPermissionState _notificationPermissionState;

    private int? _unreadCount;
    private bool _hasUnreadActivity;

    [ObservableProperty]
    private DateTimeOffset? _lastOpenedAt;

    [JsonIgnore]
    public int? UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (SetProperty(ref _unreadCount, value))
            {
                OnPropertyChanged(nameof(UnreadBadgeText));
                OnPropertyChanged(nameof(ShowUnreadBadge));
                OnPropertyChanged(nameof(ShowUnreadDot));
            }
        }
    }

    [JsonIgnore]
    public bool HasUnreadActivity
    {
        get => _hasUnreadActivity;
        set
        {
            if (SetProperty(ref _hasUnreadActivity, value))
            {
                OnPropertyChanged(nameof(ShowUnreadBadge));
                OnPropertyChanged(nameof(ShowUnreadDot));
            }
        }
    }

    [JsonIgnore]
    public string? UnreadBadgeText => UnreadCount is > 99
        ? "99+"
        : UnreadCount is > 0
            ? UnreadCount.Value.ToString(CultureInfo.InvariantCulture)
            : null;

    [JsonIgnore]
    public bool ShowUnreadBadge => IsEnabled && UnreadCount is > 0;

    [JsonIgnore]
    public bool ShowUnreadDot => IsEnabled && HasUnreadActivity && UnreadCount is not > 0;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUnreadBadge));
        OnPropertyChanged(nameof(ShowUnreadDot));
    }
}

public enum NotificationPermissionState
{
    Unknown,
    Allowed,
    Denied
}
