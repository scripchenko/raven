using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public interface IServiceActivityCoordinator
{
    event EventHandler? ActivityChanged;
    void UpdateFromDocumentTitle(ServiceInstance service, string? documentTitle, bool markActivity);
    void MarkNotificationReceived(ServiceInstance service);
    void Clear(ServiceInstance service);
    void Reset(IEnumerable<ServiceInstance> services);
    string CreateTrayToolTip(IEnumerable<ServiceInstance> services);
}
