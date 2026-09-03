using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class TelegramExternalNavigationTests
{
    private readonly BuiltInServiceCatalog _catalog = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NavigationStarting_AllowedTelegramUrl_RemainsInternal(bool isUserInitiated)
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = CreateNavigationService(browser);

        WebNavigationDisposition result = navigation.Route(
            ServiceType.Telegram,
            new Uri("https://web.telegram.org/k/"),
            isUserInitiated);

        Assert.Equal(WebNavigationDisposition.Internal, result);
        Assert.Empty(browser.AttemptedUris);
        Assert.Empty(browser.OpenedUris);
    }

    [Fact]
    public void NavigationStarting_UserInitiatedExternalHttps_OpensSystemBrowserExactlyOnce()
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = CreateNavigationService(browser);
        Uri target = new("https://example.com/article");

        WebNavigationDisposition result = navigation.Route(
            ServiceType.Telegram,
            target,
            isUserInitiated: true);

        Assert.Equal(WebNavigationDisposition.ExternalOpened, result);
        Assert.Equal([target], browser.AttemptedUris);
        Assert.Equal([target], browser.OpenedUris);
    }

    [Fact]
    public void NavigationStarting_AutomaticExternalHttps_DoesNotCallSystemBrowser()
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = CreateNavigationService(browser);

        WebNavigationDisposition result = navigation.Route(
            ServiceType.Telegram,
            new Uri("https://example.com/automatic-redirect"),
            isUserInitiated: false);

        Assert.Equal(WebNavigationDisposition.External, result);
        Assert.Empty(browser.AttemptedUris);
        Assert.Empty(browser.OpenedUris);
    }

    [Theory]
    [InlineData(true, WebNavigationDisposition.ExternalOpened, 1)]
    [InlineData(false, WebNavigationDisposition.External, 0)]
    public void NewWindowRequested_ExternalHttps_RequiresTelegramUserGesture(
        bool isUserInitiated,
        WebNavigationDisposition expected,
        int expectedLaunchCount)
    {
        RecordingExternalBrowserService browser = new();
        WebNewWindowNavigationService navigation = CreateNewWindowService(browser);
        ServiceInstance telegram = CreateAccount(ServiceType.Telegram);
        Uri target = new("https://example.com/popup");

        WebNavigationDisposition result = navigation.Route(
            telegram,
            target,
            isUserInitiated,
            _ => Assert.Fail("External URI entered the Telegram WebView."));

        Assert.Equal(expected, result);
        Assert.Equal(expectedLaunchCount, browser.AttemptedUris.Count);
        Assert.Equal(expectedLaunchCount, browser.OpenedUris.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewWindowRequested_AllowedTelegramUrl_RemainsInternal(bool isUserInitiated)
    {
        RecordingExternalBrowserService browser = new();
        WebNewWindowNavigationService navigation = CreateNewWindowService(browser);
        ServiceInstance telegram = CreateAccount(ServiceType.Telegram);
        Uri target = new("https://web.telegram.org/k/#channel");
        Uri? navigatedInside = null;

        WebNavigationDisposition result = navigation.Route(
            telegram,
            target,
            isUserInitiated,
            uri => navigatedInside = uri);

        Assert.Equal(WebNavigationDisposition.Internal, result);
        Assert.Equal(target, navigatedInside);
        Assert.Empty(browser.AttemptedUris);
    }

    [Theory]
    [InlineData("https://t.me/lantern_test", true, WebNavigationDisposition.ExternalOpened, 1)]
    [InlineData("https://t.me/lantern_test", false, WebNavigationDisposition.External, 0)]
    [InlineData("https://telegram.me/lantern_test", true, WebNavigationDisposition.ExternalOpened, 1)]
    [InlineData("https://telegram.me/lantern_test", false, WebNavigationDisposition.External, 0)]
    public void TelegramLinkDomains_FollowExternalUserGesturePolicy(
        string address,
        bool isUserInitiated,
        WebNavigationDisposition expected,
        int expectedLaunchCount)
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = CreateNavigationService(browser);

        WebNavigationDisposition result = navigation.Route(
            ServiceType.Telegram,
            new Uri(address),
            isUserInitiated);

        Assert.Equal(expected, result);
        Assert.Equal(expectedLaunchCount, browser.AttemptedUris.Count);
        Assert.Equal(expectedLaunchCount, browser.OpenedUris.Count);
    }

    [Theory]
    [InlineData(true, WebNavigationDisposition.Blocked, 1)]
    [InlineData(false, WebNavigationDisposition.External, 0)]
    public void TelegramScheme_RemainsBlockedAndNeverOpensSystemBrowser(
        bool isUserInitiated,
        WebNavigationDisposition expected,
        int expectedAttemptCount)
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = CreateNavigationService(browser);

        WebNavigationDisposition result = navigation.Route(
            ServiceType.Telegram,
            new Uri("tg://resolve?domain=lantern_test"),
            isUserInitiated);

        Assert.Equal(expected, result);
        Assert.Equal(expectedAttemptCount, browser.AttemptedUris.Count);
        Assert.Empty(browser.OpenedUris);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,blocked")]
    [InlineData("file:///C:/Windows/System32/notepad.exe")]
    public void UnsupportedSchemes_RemainBlocked(string address)
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = CreateNavigationService(browser);

        WebNavigationDisposition result = navigation.Route(
            ServiceType.Telegram,
            new Uri(address),
            isUserInitiated: true);

        Assert.Equal(WebNavigationDisposition.Blocked, result);
        Assert.Single(browser.AttemptedUris);
        Assert.Empty(browser.OpenedUris);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a URI")]
    [InlineData("://missing-scheme")]
    public void MalformedTarget_IsRejectedBeforeNavigationRouting(string address)
    {
        Assert.False(WebViewSessionManager.TryParseNavigationTarget(address, out Uri? target));
        Assert.Null(target);
    }

    [Theory]
    [InlineData(ServiceType.WhatsApp)]
    [InlineData(ServiceType.Max)]
    [InlineData(ServiceType.VkMessenger)]
    [InlineData(ServiceType.Gmail)]
    public void OtherServices_PreserveAutomaticExternalLaunchBehavior(ServiceType serviceType)
    {
        RecordingExternalBrowserService browser = new();
        WebNavigationService navigation = CreateNavigationService(browser);
        Uri target = new("https://example.com/unchanged");

        WebNavigationDisposition result = navigation.Route(
            serviceType,
            target,
            isUserInitiated: false);

        Assert.Equal(WebNavigationDisposition.ExternalOpened, result);
        Assert.Equal([target], browser.AttemptedUris);
        Assert.Equal([target], browser.OpenedUris);
    }

    private WebNavigationService CreateNavigationService(RecordingExternalBrowserService browser) =>
        new(
            new NavigationPolicy(_catalog),
            browser,
            new ExternalBrowserLaunchPolicy());

    private WebNewWindowNavigationService CreateNewWindowService(RecordingExternalBrowserService browser) =>
        new(CreateNavigationService(browser));

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
        public List<Uri> AttemptedUris { get; } = [];
        public List<Uri> OpenedUris { get; } = [];

        public bool TryOpen(Uri uri)
        {
            AttemptedUris.Add(uri);
            if (!ExternalBrowserService.CanOpen(uri))
            {
                return false;
            }

            OpenedUris.Add(uri);
            return true;
        }
    }
}
