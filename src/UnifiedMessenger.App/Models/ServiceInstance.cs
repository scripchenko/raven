namespace UnifiedMessenger.App.Models;

public sealed class ServiceInstance
{
    public Guid Id { get; set; }
    public ServiceType ServiceType { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? StartUrl { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public int? UnreadCount { get; set; }
    public bool HasUnreadActivity { get; set; }
    public DateTimeOffset? LastOpenedAt { get; set; }
}
