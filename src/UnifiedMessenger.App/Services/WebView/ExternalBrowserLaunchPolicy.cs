using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class ExternalBrowserLaunchPolicy
{
    public bool CanLaunch(ServiceType serviceType, bool isUserInitiated) =>
        serviceType is not ServiceType.Telegram || isUserInitiated;
}
