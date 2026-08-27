using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Branding;

namespace UnifiedMessenger.Tests;

public sealed class LanternBrandingTests
{
    [Fact]
    public void DefaultBrand_IsLantern()
    {
        Assert.Equal("Lantern", BrandIdentity.DisplayName);
        Assert.Equal("Lantern", BrandIdentity.CreateWindowTitle(null));
        Assert.Equal("Lantern", BrandIdentity.CreateWindowTitle("Telegram"));
        Assert.Equal("Lantern", BrandIdentity.CreateWindowTitle("WhatsApp"));
        Assert.Equal("Lantern", BrandIdentity.CreateWindowTitle("Почта Mail.Ru"));
    }

    [Fact]
    public void SystemSurfacesUseCompactIcon_WhileExeKeepsFullLanternIcon()
    {
        Assert.EndsWith("/Assets/Branding/lantern_system.ico", BrandIdentity.SystemIconPackUri, StringComparison.Ordinal);

        string mainWindow = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MainWindow.xaml"));
        string project = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "UnifiedMessenger.App.csproj"));
        string trayService = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Tray", "WinFormsTrayIconService.cs"));

        Assert.Contains(
            "Icon=\"/UnifiedMessenger.App;component/Assets/Branding/lantern_system.ico\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<Window.Icon>", mainWindow, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>Assets\\Branding\\lantern.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Assets\\Branding\\lantern_system.ico\" />", project, StringComparison.Ordinal);
        Assert.Contains("BrandIconResources.LoadSystemIcon()", trayService, StringComparison.Ordinal);
        Assert.DoesNotContain("lantern_taskbar.ico", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("lantern_taskbar.ico", project, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactSystemIcon_ContainsSmallSurfaceFramesWithoutClipping()
    {
        string path = FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Assets", "Branding", "lantern_system.ico");
        byte[] icon = File.ReadAllBytes(path);

        using MemoryStream stream = new(icon, writable: false);
        using BinaryReader reader = new(stream);
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        ushort frameCount = reader.ReadUInt16();
        Assert.True(frameCount >= 5);

        HashSet<int> sizes = [];
        for (int i = 0; i < frameCount; i++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
            uint byteCount = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            sizes.Add(width == 0 ? 256 : width);
            Assert.Equal(width, height);
            Assert.True(offset + byteCount <= icon.Length);
        }

        Assert.True(sizes.IsSupersetOf([16, 20, 24, 32, 40]));
    }

    [Theory]
    [InlineData(ServiceType.Telegram, BrandIconKind.Telegram)]
    [InlineData(ServiceType.WhatsApp, BrandIconKind.WhatsApp)]
    [InlineData(ServiceType.Max, BrandIconKind.Max)]
    [InlineData(ServiceType.VkMessenger, BrandIconKind.Vk)]
    [InlineData(ServiceType.Gmail, BrandIconKind.Gmail)]
    public void WebService_ResolvesExpectedIconKind(ServiceType serviceType, BrandIconKind expected)
    {
        Assert.Equal(expected, BrandIconCatalog.Resolve(serviceType));
    }

    [Theory]
    [InlineData(MailProviderType.Gmail, BrandIconKind.Gmail)]
    [InlineData(MailProviderType.Yandex, BrandIconKind.Yandex)]
    [InlineData(MailProviderType.MailRu, BrandIconKind.MailRu)]
    public void MailProvider_ResolvesExpectedIconKind(MailProviderType provider, BrandIconKind expected)
    {
        Assert.Equal(expected, BrandIconCatalog.Resolve(provider));
    }

    [Fact]
    public void MissingSelection_UsesLanternFallback()
    {
        Assert.Equal(BrandIconKind.Lantern, BrandIconCatalog.Resolve((NavigationAccountItem?)null));
        Assert.Equal(BrandIconKind.Lantern, BrandIconCatalog.Resolve((ServiceType)int.MaxValue));
        Assert.Equal(BrandIconKind.Lantern, BrandIconCatalog.Resolve(MailProviderType.GenericImap));
    }

    [Theory]
    [InlineData(BrandIconKind.Telegram, "telegram.png")]
    [InlineData(BrandIconKind.WhatsApp, "whatsapp.png")]
    [InlineData(BrandIconKind.Max, "max.png")]
    [InlineData(BrandIconKind.Vk, "vk.png")]
    [InlineData(BrandIconKind.Gmail, "gmail.png")]
    [InlineData(BrandIconKind.Yandex, "yandex_mail.png")]
    [InlineData(BrandIconKind.MailRu, "mailru.png")]
    public void IconKind_MapsToPackagedServiceAsset(BrandIconKind iconKind, string expectedFileName)
    {
        Assert.Equal(expectedFileName, BrandIconCatalog.GetAssetFileName(iconKind));
        Assert.EndsWith($"/Assets/Services/{expectedFileName}", BrandIconCatalog.GetPackUri(iconKind));
    }

    [Fact]
    public void SelectionChange_UpdatesIconMapping()
    {
        using NavigationAccountItem telegram = NavigationAccountItem.FromService(
            new ServiceInstance { ServiceType = ServiceType.Telegram });
        using NavigationAccountItem vk = NavigationAccountItem.FromService(
            new ServiceInstance { ServiceType = ServiceType.VkMessenger });

        Assert.Equal(BrandIconKind.Telegram, BrandIconCatalog.Resolve(telegram));
        Assert.Equal(BrandIconKind.Vk, BrandIconCatalog.Resolve(vk));
        Assert.NotEqual(
            BrandIconCatalog.GetPackUri(BrandIconCatalog.Resolve(telegram)),
            BrandIconCatalog.GetPackUri(BrandIconCatalog.Resolve(vk)));
    }

    [Theory]
    [InlineData(BrandIconKind.Telegram, 38)]
    [InlineData(BrandIconKind.WhatsApp, 38)]
    [InlineData(BrandIconKind.Max, 34)]
    [InlineData(BrandIconKind.Vk, 37)]
    [InlineData(BrandIconKind.Gmail, 38)]
    [InlineData(BrandIconKind.Yandex, 37)]
    [InlineData(BrandIconKind.MailRu, 37)]
    public void SidebarIcon_UsesNormalizedPresentationSize(BrandIconKind iconKind, double expectedSize)
    {
        Assert.Equal(expectedSize, BrandIconCatalog.GetSidebarPresentationSize(iconKind));
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

}
