using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services;

public sealed class BuiltInServiceCatalog : IBuiltInServiceCatalog
{
    private static readonly IReadOnlyList<ServiceDefinition> Definitions =
    [
        new(ServiceType.Telegram, "Telegram", "T", new Uri("https://web.telegram.org/k/"), true, CreateHosts("web.telegram.org")),
        new(ServiceType.WhatsApp, "WhatsApp", "W", new Uri("https://web.whatsapp.com/"), true, CreateHosts("web.whatsapp.com")),
        new(ServiceType.Max, "MAX", "M", new Uri("https://web.max.ru/"), true, CreateHosts("web.max.ru", "max.ru")),
        new(ServiceType.VkMessenger, "VK Мессенджер", "VK", new Uri("https://web.vk.me/"), true, CreateHosts("web.vk.me", "vk.com", "id.vk.com")),
        new(ServiceType.Gmail, "Gmail", "G", null, false, CreateHosts())
    ];

    public IReadOnlyList<ServiceDefinition> All => Definitions;

    public ServiceDefinition Get(ServiceType serviceType) =>
        Definitions.FirstOrDefault(definition => definition.ServiceType == serviceType)
        ?? throw new ArgumentOutOfRangeException(nameof(serviceType), serviceType, "Unknown service type.");

    private static IReadOnlySet<string> CreateHosts(params string[] hosts) =>
        new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
}
