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
            allowedHost => IsAllowedHost(target.IdnHost, allowedHost));
    }

    private static bool IsAllowedHost(string host, AllowedHostRule allowedHost) =>
        host.Equals(allowedHost.Host, StringComparison.OrdinalIgnoreCase)
        || allowedHost.MatchMode is HostMatchMode.ExactOrSubdomain
            && host.EndsWith($".{allowedHost.Host}", StringComparison.OrdinalIgnoreCase);
}
