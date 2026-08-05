using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Security;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebNavigationService(
    NavigationPolicy navigationPolicy,
    IExternalBrowserService externalBrowserService)
{
    public WebNavigationDisposition Route(ServiceType serviceType, Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (navigationPolicy.IsAllowedTopLevelNavigation(serviceType, target))
        {
            return WebNavigationDisposition.Internal;
        }

        return externalBrowserService.TryOpen(target)
            ? WebNavigationDisposition.ExternalOpened
            : WebNavigationDisposition.Blocked;
    }
}
