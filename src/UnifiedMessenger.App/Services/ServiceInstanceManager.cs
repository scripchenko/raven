using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services;

public static class ServiceInstanceManager
{
    public static ServiceInstance Add(
        AppSettings settings,
        ServiceDefinition definition,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(definition);

        if (!definition.IsWebViewService || definition.StartUri is null || definition.ServiceType == ServiceType.Gmail)
        {
            throw new NotSupportedException("Only the Stage 3 WebView services can be added as accounts.");
        }

        Guid id = Guid.NewGuid();
        ServiceInstance service = new()
        {
            Id = id,
            ServiceType = definition.ServiceType,
            DisplayName = definition.DisplayName,
            StartUrl = definition.StartUri.AbsoluteUri,
            ProfileName = Security.ProfileNameFactory.Create(id),
            IsEnabled = true,
            SortOrder = settings.Services.Count == 0
                ? 0
                : settings.Services.Max(existing => existing.SortOrder) + 1
        };

        Rename(service, string.IsNullOrWhiteSpace(displayName) ? definition.DisplayName : displayName);
        settings.Services.Add(service);
        settings.LastServiceId = service.Id;
        return service;
    }

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

    public static void SetEnabled(ServiceInstance service, bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.IsEnabled = isEnabled;
    }

    public static bool Move(IList<ServiceInstance> services, Guid serviceInstanceId, int offset)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (offset is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        ServiceInstance[] sorted = Sort(services).ToArray();
        int currentIndex = Array.FindIndex(sorted, service => service.Id == serviceInstanceId);
        int targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= sorted.Length)
        {
            return false;
        }

        (sorted[currentIndex], sorted[targetIndex]) = (sorted[targetIndex], sorted[currentIndex]);
        for (int index = 0; index < sorted.Length; index++)
        {
            sorted[index].SortOrder = index;
        }

        return true;
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

    public static void NormalizeSortOrder(IList<ServiceInstance> services)
    {
        ServiceInstance[] sorted = Sort(services).ToArray();
        for (int index = 0; index < sorted.Length; index++)
        {
            sorted[index].SortOrder = index;
        }
    }
}
