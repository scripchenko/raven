using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Branding;

public static class BrandIdentity
{
    public const string DisplayName = "Lantern";
    public const string SystemIconPackUri =
        "pack://application:,,,/UnifiedMessenger.App;component/Assets/Branding/lantern_system.ico";
    public static string CreateWindowTitle(string? _) => DisplayName;
}

public enum BrandIconKind
{
    Lantern,
    Telegram,
    WhatsApp,
    Max,
    Vk,
    Gmail,
    Yandex,
    MailRu
}

public static class BrandIconCatalog
{
    public static BrandIconKind Resolve(NavigationAccountItem? item) => item switch
    {
        { Service: ServiceInstance service } => Resolve(service.ServiceType),
        { MailAccount: MailAccount account } => Resolve(account.Provider),
        _ => BrandIconKind.Lantern
    };

    public static BrandIconKind Resolve(ServiceType serviceType) => serviceType switch
    {
        ServiceType.Telegram => BrandIconKind.Telegram,
        ServiceType.WhatsApp => BrandIconKind.WhatsApp,
        ServiceType.Max => BrandIconKind.Max,
        ServiceType.VkMessenger => BrandIconKind.Vk,
        ServiceType.Gmail => BrandIconKind.Gmail,
        _ => BrandIconKind.Lantern
    };

    public static BrandIconKind Resolve(MailProviderType provider) => provider switch
    {
        MailProviderType.Gmail => BrandIconKind.Gmail,
        MailProviderType.Yandex => BrandIconKind.Yandex,
        MailProviderType.MailRu => BrandIconKind.MailRu,
        _ => BrandIconKind.Lantern
    };

    public static string GetAssetFileName(BrandIconKind iconKind) => iconKind switch
    {
        BrandIconKind.Telegram => "telegram.png",
        BrandIconKind.WhatsApp => "whatsapp.png",
        BrandIconKind.Max => "max.png",
        BrandIconKind.Vk => "vk.png",
        BrandIconKind.Gmail => "gmail.png",
        BrandIconKind.Yandex => "yandex_mail.png",
        BrandIconKind.MailRu => "mailru.png",
        _ => "lantern_icon.png"
    };

    public static string GetPackUri(BrandIconKind iconKind) => iconKind == BrandIconKind.Lantern
        ? "pack://application:,,,/UnifiedMessenger.App;component/Assets/Branding/lantern_icon.png"
        : $"pack://application:,,,/UnifiedMessenger.App;component/Assets/Services/{GetAssetFileName(iconKind)}";

    public static double GetSidebarPresentationSize(BrandIconKind iconKind) => iconKind switch
    {
        BrandIconKind.Telegram => 38,
        BrandIconKind.WhatsApp => 38,
        BrandIconKind.Max => 34,
        BrandIconKind.Vk => 37,
        BrandIconKind.Gmail => 38,
        BrandIconKind.Yandex => 37,
        BrandIconKind.MailRu => 37,
        _ => 34
    };
}
