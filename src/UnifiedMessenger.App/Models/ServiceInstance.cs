using CommunityToolkit.Mvvm.ComponentModel;

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
    private int? _unreadCount;

    [ObservableProperty]
    private bool _hasUnreadActivity;

    [ObservableProperty]
    private DateTimeOffset? _lastOpenedAt;
}
