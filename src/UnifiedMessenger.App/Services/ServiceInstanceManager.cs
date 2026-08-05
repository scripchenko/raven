using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services;

public static class ServiceInstanceManager
{
    public static IReadOnlyList<ServiceInstance> Sort(IEnumerable<ServiceInstance> services) =>
        services
            .OrderBy(service => service.SortOrder)
            .ThenBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(service => service.Id)
            .ToArray();

    public static bool Remove(IList<ServiceInstance> services, Guid serviceInstanceId)
    {
        ServiceInstance? target = services.FirstOrDefault(service => service.Id == serviceInstanceId);
        if (target is null)
        {
            return false;
        }

        services.Remove(target);
        NormalizeSortOrder(services);
        return true;
    }

    public static void Rename(ServiceInstance service, string displayName)
    {
        ArgumentNullException.ThrowIfNull(service);

        string normalizedName = displayName?.Trim()
            ?? throw new ArgumentNullException(nameof(displayName));

        if (normalizedName.Length is 0 or > 80)
        {
            throw new ArgumentException("Display name must contain between 1 and 80 characters.", nameof(displayName));
        }

        service.DisplayName = normalizedName;
    }

    public static void UpdatePerformance(AppSettings settings, MemoryMode memoryMode, int suspendAfterMinutes)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Enum.IsDefined(memoryMode))
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMode));
        }

        if (suspendAfterMinutes is < 1 or > 1440)
        {
            throw new ArgumentOutOfRangeException(nameof(suspendAfterMinutes));
        }

        settings.MemoryMode = memoryMode;
        settings.SuspendAfterMinutes = suspendAfterMinutes;
    }

    private static void NormalizeSortOrder(IList<ServiceInstance> services)
    {
        ServiceInstance[] sorted = Sort(services).ToArray();
        for (int index = 0; index < sorted.Length; index++)
        {
            sorted[index].SortOrder = index;
        }
    }
}
