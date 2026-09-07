using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Security;

namespace UnifiedMessenger.App.Services.WebView;

internal readonly record struct WebViewProfileIdentity(Guid ServiceInstanceId, string ProfileName)
{
    public static WebViewProfileIdentity Create(ServiceInstance serviceInstance)
    {
        ArgumentNullException.ThrowIfNull(serviceInstance);
        return Create(serviceInstance.Id, serviceInstance.ProfileName);
    }

    public static WebViewProfileIdentity Create(Guid serviceInstanceId, string profileName)
    {
        if (serviceInstanceId == Guid.Empty)
        {
            throw new InvalidOperationException("The service account must have a non-empty identifier.");
        }

        string expectedProfileName = ProfileNameFactory.Create(serviceInstanceId);
        if (!string.Equals(profileName, expectedProfileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The WebView2 profile name does not match the service account.");
        }

        return new WebViewProfileIdentity(serviceInstanceId, expectedProfileName);
    }

    public static bool TryCreateFromProfileName(
        string? profileName,
        out WebViewProfileIdentity identity)
    {
        const string prefix = "service-";
        if (profileName is null
            || !profileName.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(profileName[prefix.Length..], "N", out Guid serviceInstanceId))
        {
            identity = default;
            return false;
        }

        string expectedProfileName = ProfileNameFactory.Create(serviceInstanceId);
        if (!string.Equals(profileName, expectedProfileName, StringComparison.Ordinal))
        {
            identity = default;
            return false;
        }

        identity = new WebViewProfileIdentity(serviceInstanceId, expectedProfileName);
        return true;
    }
}
