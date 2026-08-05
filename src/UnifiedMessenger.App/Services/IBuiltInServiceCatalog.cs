using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services;

public interface IBuiltInServiceCatalog
{
    IReadOnlyList<ServiceDefinition> All { get; }
    ServiceDefinition Get(ServiceType serviceType);
}
