using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;

namespace UnifiedMessenger.App.Services.Security;

public sealed class NavigationPolicy(IBuiltInServiceCatalog serviceCatalog)
{
    public bool IsAllowedTopLevelNavigation(ServiceType serviceType, Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!target.IsAbsoluteUri || !string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return serviceCatalog.Get(serviceType).AllowedHosts.Any(
            allowedHost => IsHostOrSubdomain(target.IdnHost, allowedHost));
    }

    private static bool IsHostOrSubdomain(string host, string allowedHost) =>
        host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith($".{allowedHost}", StringComparison.OrdinalIgnoreCase);
}
