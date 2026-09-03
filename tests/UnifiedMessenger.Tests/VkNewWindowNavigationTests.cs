using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class VkNewWindowNavigationTests
{
    private readonly BuiltInServiceCatalog _catalog = new();

    [Fact]
    public void VkCatalog_UsesOnlyMinimalOfficialHostAllowlist()
    {
        ServiceDefinition vk = _catalog.Get(ServiceType.VkMessenger);

        Assert.Equal(4, vk.AllowedHosts.Count);
        Assert.Contains(vk.AllowedHosts, rule => rule is { Host: "web.vk.me", MatchMode: HostMatchMode.ExactOrSubdomain });
        Assert.Contains(vk.AllowedHosts, rule => rule is { Host: "vk.com", MatchMode: HostMatchMode.ExactOrSubdomain });
        Assert.Contains(vk.AllowedHosts, rule => rule is { Host: "id.vk.com", MatchMode: HostMatchMode.ExactOrSubdomain });
        Assert.Contains(vk.AllowedHosts, rule => rule is { Host: "id.vk.ru", MatchMode: HostMatchMode.ExactOnly });
    }

    [Theory]
    [InlineData("https://id.vk.ru/auth", true)]
    [InlineData("https://evilid.vk.ru/auth", false)]
    [InlineData("https://sub.id.vk.ru/auth", false)]
    [InlineData("https://id.vk.ru.evil.example/auth", false)]
    [InlineData("https://example.vk.ru/auth", false)]
    public void NavigationPolicy_UsesExactOnlyForIdVkRu(string address, bool expected)
    {
        NavigationPolicy policy = new(_catalog);

        Assert.Equal(expected, policy.IsAllowedTopLevelNavigation(ServiceType.VkMessenger, new Uri(address)));
    }

    [Theory]
    [InlineData("https://vk.com/im")]
    [InlineData("https://id.vk.com/auth")]
    [InlineData("https://id.vk.ru/auth")]
    [InlineData("https://web.vk.me/")]
    [InlineData("https://subdomain.vk.com/path")]
    public void NewWindowRequested_OfficialVkHostNavigatesInsideCurrentWebView(string address)
    {
        RecordingExternalBrowserService browser = new();
        WebNewWindowNavigationService router = CreateRouter(browser);
        ServiceInstance vk = CreateAccount(ServiceType.VkMessenger);
        Uri? navigatedInside = null;

        WebNavigationDisposition result = router.Route(
            vk,
            new Uri(address),
            isUserInitiated: false,
            uri => navigatedInside = uri);

        Assert.Equal(WebNavigationDisposition.Internal, result);
        Assert.Equal(address, navigatedInside?.AbsoluteUri);
        Assert.Null(browser.LastOpenedUri);
    }

    [Theory]
    [InlineData("https://evilvk.com/path")]
    [InlineData("https://vk.com.evil.example/path")]
    [InlineData("https://evilid.vk.ru/path")]
    [InlineData("https://sub.id.vk.ru/path")]
    [InlineData("https://id.vk.ru.evil.example/path")]
    [InlineData("https://example.vk.ru/path")]
    public void NewWindowRequested_LookalikeVkHostIsExternal(string address)
    {
        RecordingExternalBrowserService browser = new();
        WebNewWindowNavigationService router = CreateRouter(browser);
        ServiceInstance vk = CreateAccount(ServiceType.VkMessenger);
        bool navigatedInside = false;
        Uri target = new(address);

        WebNavigationDisposition result = router.Route(
            vk,
            target,
            isUserInitiated: false,
            _ => navigatedInside = true);

        Assert.Equal(WebNavigationDisposition.ExternalOpened, result);
        Assert.False(navigatedInside);
        Assert.Equal(target, browser.LastOpenedUri);
    }

    [Fact]
    public void NewWindowRequested_ExternalVkLinkOpensSystemBrowser()
    {
        RecordingExternalBrowserService browser = new();
        WebNewWindowNavigationService router = CreateRouter(browser);
        ServiceInstance vk = CreateAccount(ServiceType.VkMessenger);
        Uri external = new("https://example.com/article");

        WebNavigationDisposition result = router.Route(
            vk,
            external,
            isUserInitiated: false,
            _ => Assert.Fail("External URI entered WebView2."));

        Assert.Equal(WebNavigationDisposition.ExternalOpened, result);
        Assert.Equal(external, browser.LastOpenedUri);
    }

    [Fact]
    public void NavigationStarting_IdVkRuStaysInsideCurrentVkWebView()
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = new(
            new NavigationPolicy(_catalog),
            browser,
            new ExternalBrowserLaunchPolicy());
        Uri target = new("https://id.vk.ru/auth");

        WebNavigationDisposition result = navigation.Route(
            ServiceType.VkMessenger,
            target,
            isUserInitiated: false);

        Assert.Equal(WebNavigationDisposition.Internal, result);
        Assert.Null(browser.LastOpenedUri);
    }

    [Theory]
    [InlineData(ServiceType.Telegram, "https://web.telegram.org/k/")]
    [InlineData(ServiceType.WhatsApp, "https://web.whatsapp.com/")]
    [InlineData(ServiceType.Max, "https://web.max.ru/")]
    public void NewWindowRequested_OtherServicesKeepOfficialPopupInside(
        ServiceType serviceType,
        string address)
    {
        RecordingExternalBrowserService browser = new();
        WebNewWindowNavigationService router = CreateRouter(browser);
        ServiceInstance account = CreateAccount(serviceType);
        Uri? navigatedInside = null;

        WebNavigationDisposition result = router.Route(
            account,
            new Uri(address),
            isUserInitiated: false,
            uri => navigatedInside = uri);

        Assert.Equal(WebNavigationDisposition.Internal, result);
        Assert.NotNull(navigatedInside);
        Assert.Null(browser.LastOpenedUri);
    }

    [Theory]
    [InlineData(ServiceType.WhatsApp)]
    [InlineData(ServiceType.Max)]
    public void NewWindowRequested_OtherServicesKeepExternalPopupInSystemBrowser(ServiceType serviceType)
    {
        RecordingExternalBrowserService browser = new();
        WebNewWindowNavigationService router = CreateRouter(browser);
        ServiceInstance account = CreateAccount(serviceType);
        Uri external = new("https://example.com/");

        WebNavigationDisposition result = router.Route(
            account,
            external,
            isUserInitiated: false,
            _ => Assert.Fail("External URI entered WebView2."));

        Assert.Equal(WebNavigationDisposition.ExternalOpened, result);
        Assert.Equal(external, browser.LastOpenedUri);
    }

    private WebNewWindowNavigationService CreateRouter(RecordingExternalBrowserService browser) =>
        new(new WebNavigationService(
            new NavigationPolicy(_catalog),
            browser,
            new ExternalBrowserLaunchPolicy()));

    private ServiceInstance CreateAccount(ServiceType serviceType)
    {
        Guid id = Guid.NewGuid();
        ServiceDefinition definition = _catalog.Get(serviceType);
        return new ServiceInstance
        {
            Id = id,
            ServiceType = serviceType,
            DisplayName = definition.DisplayName,
            StartUrl = definition.StartUri?.AbsoluteUri,
            ProfileName = ProfileNameFactory.Create(id),
            IsEnabled = true
        };
    }

    private sealed class RecordingExternalBrowserService : IExternalBrowserService
    {
        public Uri? LastOpenedUri { get; private set; }

        public bool TryOpen(Uri uri)
        {
            LastOpenedUri = uri;
            return true;
        }
    }

}
