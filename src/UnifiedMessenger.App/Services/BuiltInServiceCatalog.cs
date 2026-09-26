using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services;

public sealed class BuiltInServiceCatalog : IBuiltInServiceCatalog
{
    private static readonly IReadOnlyList<ServiceDefinition> Definitions =
    [
        new(ServiceType.Telegram, "Telegram", "T", new Uri("https://web.telegram.org/k/"), true, CreateHosts("web.telegram.org")),
        new(ServiceType.WhatsApp, "WhatsApp", "W", new Uri("https://web.whatsapp.com/"), true, CreateHosts("web.whatsapp.com")),
        new(ServiceType.Max, "MAX", "M", new Uri("https://web.max.ru/"), true, CreateHosts("web.max.ru", "max.ru")),
        new(
            ServiceType.VkMessenger,
            "VK",
            "VK",
            new Uri("https://web.vk.me/"),
            true,
            CreateHosts(
                ["web.vk.me", "vk.com", "id.vk.com"],
                ["id.vk.ru"])),
        new(ServiceType.Gmail, "Gmail", "G", null, false, CreateHosts())
    ];

    public IReadOnlyList<ServiceDefinition> All => Definitions;

    public ServiceDefinition Get(ServiceType serviceType) =>
        Definitions.FirstOrDefault(definition => definition.ServiceType == serviceType)
        ?? throw new ArgumentOutOfRangeException(nameof(serviceType), serviceType, "Unknown service type.");

    private static IReadOnlyList<AllowedHostRule> CreateHosts(params string[] hosts) =>
        CreateHosts(hosts, []);

    private static IReadOnlyList<AllowedHostRule> CreateHosts(
        IReadOnlyList<string> exactOrSubdomainHosts,
        IReadOnlyList<string> exactOnlyHosts) =>
        [
            .. exactOrSubdomainHosts.Select(host => new AllowedHostRule(host, HostMatchMode.ExactOrSubdomain)),
            .. exactOnlyHosts.Select(host => new AllowedHostRule(host, HostMatchMode.ExactOnly))
        ];
}
