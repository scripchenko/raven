using CommunityToolkit.Mvvm.ComponentModel;
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
    private int _lanternUnviewedActivityCount;

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
                OnPropertyChanged(nameof(SidebarBadgeCount));
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
                OnPropertyChanged(nameof(UnreadBadgeText));
                OnPropertyChanged(nameof(SidebarBadgeCount));
            }
        }
    }

    [JsonIgnore]
    public int LanternUnviewedActivityCount
    {
        get => _lanternUnviewedActivityCount;
        set
        {
            int normalized = Math.Max(0, value);
            if (SetProperty(ref _lanternUnviewedActivityCount, normalized))
            {
                OnPropertyChanged(nameof(UnreadBadgeText));
                OnPropertyChanged(nameof(ShowUnreadBadge));
                OnPropertyChanged(nameof(SidebarBadgeCount));
            }
        }
    }

    [JsonIgnore]
    public int SidebarBadgeCount => UnreadCount is > 0
        ? UnreadCount.Value
        : LanternUnviewedActivityCount > 0
            ? LanternUnviewedActivityCount
            : HasUnreadActivity
                ? 1
                : 0;

    [JsonIgnore]
    public string? UnreadBadgeText => SidebarBadgeFormatter.Format(SidebarBadgeCount);

    [JsonIgnore]
    public bool ShowUnreadBadge => IsEnabled && SidebarBadgeCount > 0;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUnreadBadge));
    }
}

public enum NotificationPermissionState
{
    Unknown,
    Allowed,
    Denied
}
