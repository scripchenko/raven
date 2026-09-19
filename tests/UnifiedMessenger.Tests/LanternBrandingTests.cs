using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Branding;

namespace UnifiedMessenger.Tests;

public sealed class LanternBrandingTests
{
    [Fact]
    public void PublicBrand_IsRaven()
    {
        Assert.Equal("raven", BrandIdentity.DisplayName);
        Assert.Equal("raven", BrandIdentity.CreateWindowTitle(null));
        Assert.Equal("raven", BrandIdentity.CreateWindowTitle("Telegram"));
        Assert.Equal("raven", BrandIdentity.CreateWindowTitle("WhatsApp"));
        Assert.Equal("raven", BrandIdentity.CreateWindowTitle("Почта Mail.Ru"));
    }

    [Fact]
    public void HybridMapping_UsesRavenBrandWithHistoricalBracketForIconOnlySurfaces()
    {
        Assert.EndsWith("/Assets/Branding/lantern_system.ico", BrandIdentity.SystemIconPackUri, StringComparison.Ordinal);

        string mainWindow = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MainWindow.xaml"));
        string project = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "UnifiedMessenger.App.csproj"));
        string trayService = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Tray", "WinFormsTrayIconService.cs"));
        string startupWindow = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "StartupWindow.xaml"));
        string welcomeView = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "WelcomeView.xaml"));
        string notificationPopup = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "NotificationPopupWindow.xaml"));
        string taskbarIndicator = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Tray", "WpfTaskbarActivityIndicator.cs"));
        string mainWindowCode = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MainWindow.xaml.cs"));

        Assert.Contains(
            "Icon=\"/UnifiedMessenger.App;component/Assets/Branding/lantern_system.ico\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<Window.Icon>", mainWindow, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>Assets\\Branding\\lantern_system.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains("<Resource Include=\"Assets\\Branding\\lantern_system.ico\" />", project, StringComparison.Ordinal);
        Assert.Contains("BrandIconResources.LoadSystemIcon()", trayService, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Assets/Branding/raven_logo.png", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Assets/Branding/lantern_sidebar.png", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Width=\"40\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Height=\"40\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RenderOptions.BitmapScalingMode=\"HighQuality\"", mainWindow, StringComparison.Ordinal);
        Assert.Equal(
            1,
            mainWindow.Split("Assets/Branding/lantern_system.ico", StringSplitOptions.None).Length - 1);
        Assert.Contains("Assets/Branding/raven_logo.png", startupWindow, StringComparison.Ordinal);
        Assert.Contains("Assets/Branding/raven_tile.png", welcomeView, StringComparison.Ordinal);
        Assert.Contains("Assets/Branding/raven_icon.png", notificationPopup, StringComparison.Ordinal);
        Assert.Contains("taskbarItem.Overlay = null", taskbarIndicator, StringComparison.Ordinal);
        Assert.Contains("Title=\"\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"raven\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Title=\"{Binding WindowTitle}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("NativeWindowCaption.TryHideBranding(_mainWindowHandle)", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivityOverlay", taskbarIndicator, StringComparison.Ordinal);
        Assert.DoesNotContain("lantern_taskbar.ico", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("lantern_taskbar.ico", project, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactSystemIcon_ContainsSmallSurfaceFramesWithoutClipping()
    {
        string path = FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Assets", "Branding", "lantern_system.ico");
        HashSet<int> sizes = ReadIconSizes(path);

        Assert.True(sizes.IsSupersetOf([16, 20, 24, 32, 40]));
    }

    [Theory]
    [InlineData(16, 14, 14, 12, 12, 0)]
    [InlineData(20, 18, 18, 14, 14, 0)]
    [InlineData(24, 22, 22, 16, 16, 0)]
    public void CompactSystemIcon_UsesMostOfTrayCanvasWithoutOpaqueEdgeClipping(
        int frameSize,
        int minimumWidth,
        int maximumWidth,
        int minimumHeight,
        int maximumHeight,
        byte maximumEdgeAlpha)
    {
        string path = FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Assets", "Branding", "lantern_system.ico");
        using System.Drawing.Icon icon = new(path, frameSize, frameSize);
        using System.Drawing.Bitmap bitmap = icon.ToBitmap();
        System.Drawing.Rectangle visibleBounds = GetVisibleBounds(bitmap, alphaThreshold: 8);

        Assert.InRange(visibleBounds.Width, minimumWidth, maximumWidth);
        Assert.InRange(visibleBounds.Height, minimumHeight, maximumHeight);
        byte maximumObservedEdgeAlpha = GetMaximumEdgeAlpha(bitmap);
        Assert.True(maximumObservedEdgeAlpha < byte.MaxValue);
        Assert.InRange(maximumObservedEdgeAlpha, (byte)0, maximumEdgeAlpha);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void HistoricalBracketSystemIcon_HasNoBakedRedStatusDot(int frameSize)
    {
        string path = FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Assets", "Branding", "lantern_system.ico");
        using System.Drawing.Icon icon = new(path, frameSize, frameSize);
        using System.Drawing.Bitmap bitmap = icon.ToBitmap();

        Assert.Equal(0, CountRedStatusPixels(bitmap));
    }

    [Fact]
    public void SidebarBracket_UsesLargeHistoricalSourceForSharpWpfDownscaling()
    {
        string path = FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Assets", "Branding", "lantern_sidebar.png");
        using System.Drawing.Bitmap bitmap = new(path);

        Assert.Equal(256, bitmap.Width);
        Assert.Equal(256, bitmap.Height);
        Assert.Equal(0, CountRedStatusPixels(bitmap));
    }

    [Fact]
    public void RavenDesktopShortcutIcon_ContainsRequiredMultiResolutionFrames()
    {
        string path = FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Assets", "Branding", "raven.ico");

        Assert.True(ReadIconSizes(path).IsSupersetOf([16, 24, 32, 48, 64, 128, 256]));
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

    private static HashSet<int> ReadIconSizes(string path)
    {
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

        return sizes;
    }

    private static System.Drawing.Rectangle GetVisibleBounds(
        System.Drawing.Bitmap bitmap,
        byte alphaThreshold)
    {
        int left = bitmap.Width;
        int top = bitmap.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A <= alphaThreshold)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= left && bottom >= top);
        return System.Drawing.Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static byte GetMaximumEdgeAlpha(System.Drawing.Bitmap bitmap)
    {
        byte maximum = 0;
        for (int index = 0; index < bitmap.Width; index++)
        {
            maximum = Math.Max(maximum, bitmap.GetPixel(index, 0).A);
            maximum = Math.Max(maximum, bitmap.GetPixel(index, bitmap.Height - 1).A);
        }

        for (int index = 0; index < bitmap.Height; index++)
        {
            maximum = Math.Max(maximum, bitmap.GetPixel(0, index).A);
            maximum = Math.Max(maximum, bitmap.GetPixel(bitmap.Width - 1, index).A);
        }

        return maximum;
    }

    private static int CountRedStatusPixels(System.Drawing.Bitmap bitmap)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                System.Drawing.Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 20
                    && pixel.R > 170
                    && pixel.R > pixel.G * 1.5
                    && pixel.R > pixel.B * 1.5)
                {
                    count++;
                }
            }
        }

        return count;
    }

}
