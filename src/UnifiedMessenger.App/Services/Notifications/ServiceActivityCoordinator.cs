using System.Globalization;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Branding;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class ServiceActivityCoordinator : IServiceActivityCoordinator
{
    public event EventHandler? ActivityChanged;

    public void UpdateFromDocumentTitle(ServiceInstance service, string? documentTitle, bool markActivity)
    {
        ArgumentNullException.ThrowIfNull(service);
        int? count = PageTitleUnreadCountParser.TryParse(documentTitle);
        if (count is null)
        {
            return;
        }

        service.UnreadCount = count > 0 ? count : null;
        if (markActivity && count > 0)
        {
            service.HasUnreadActivity = true;
        }

        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkNotificationReceived(ServiceInstance service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.LanternUnviewedActivityCount = service.LanternUnviewedActivityCount == int.MaxValue
            ? int.MaxValue
            : service.LanternUnviewedActivityCount + 1;
        service.HasUnreadActivity = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(ServiceInstance service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.UnreadCount = null;
        service.LanternUnviewedActivityCount = 0;
        service.HasUnreadActivity = false;
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset(IEnumerable<ServiceInstance> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (ServiceInstance service in services)
        {
            service.UnreadCount = null;
            service.LanternUnviewedActivityCount = 0;
            service.HasUnreadActivity = false;
        }

        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public string CreateTrayToolTip(IEnumerable<ServiceInstance> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ServiceInstance[] enabled = services.Where(service => service.IsEnabled).ToArray();
        bool hasUnknownActivity = enabled.Any(service =>
            service.HasUnreadActivity && service.UnreadCount is not > 0);
        if (hasUnknownActivity)
        {
            return $"{BrandIdentity.DisplayName} — есть новые события";
        }

        long knownTotal = enabled
            .Where(service => service.UnreadCount is > 0)
            .Sum(service => (long)service.UnreadCount!.Value);
        return knownTotal > 0
            ? $"{BrandIdentity.DisplayName} — {knownTotal.ToString(CultureInfo.InvariantCulture)} непрочитанных"
            : BrandIdentity.DisplayName;
    }
}
