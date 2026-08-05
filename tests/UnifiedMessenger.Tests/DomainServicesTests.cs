using System.Text.RegularExpressions;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Security;

namespace UnifiedMessenger.Tests;

public sealed class DomainServicesTests
{
    private readonly BuiltInServiceCatalog _catalog = new();

    [Fact]
    public void ProfileNameFactory_CreatesStableSafeName()
    {
        Guid id = Guid.Parse("6A1A9C95-20F4-4B56-B623-D1797DE9A17B");

        string profileName = ProfileNameFactory.Create(id);

        Assert.Equal("service-6a1a9c9520f44b56b623d1797de9a17b", profileName);
        Assert.Matches(new Regex("^[a-z0-9-]+$", RegexOptions.CultureInvariant), profileName);
    }

    [Fact]
    public void BuiltInCatalog_ContainsEverySupportedServiceAndCentralUrls()
    {
        Assert.Equal(5, _catalog.All.Count);
        Assert.Equal("https://web.telegram.org/k/", _catalog.Get(ServiceType.Telegram).StartUri?.AbsoluteUri);
        Assert.Equal("https://web.whatsapp.com/", _catalog.Get(ServiceType.WhatsApp).StartUri?.AbsoluteUri);
        Assert.Equal("https://web.max.ru/", _catalog.Get(ServiceType.Max).StartUri?.AbsoluteUri);
        Assert.Equal("https://web.vk.me/", _catalog.Get(ServiceType.VkMessenger).StartUri?.AbsoluteUri);

        ServiceDefinition gmail = _catalog.Get(ServiceType.Gmail);
        Assert.Null(gmail.StartUri);
        Assert.False(gmail.IsWebViewService);
    }

    [Theory]
    [InlineData(ServiceType.Telegram, "https://web.telegram.org/k/", true)]
    [InlineData(ServiceType.Telegram, "https://web.telegram.org.evil.example/", false)]
    [InlineData(ServiceType.Telegram, "http://web.telegram.org/k/", false)]
    [InlineData(ServiceType.Max, "https://id.max.ru/login", true)]
    [InlineData(ServiceType.VkMessenger, "https://id.vk.com/auth", true)]
    [InlineData(ServiceType.VkMessenger, "https://example.com/", false)]
    [InlineData(ServiceType.Gmail, "https://mail.google.com/", false)]
    public void NavigationPolicy_AppliesHttpsHostAllowList(ServiceType serviceType, string target, bool expected)
    {
        NavigationPolicy policy = new(_catalog);

        bool actual = policy.IsAllowedTopLevelNavigation(serviceType, new Uri(target));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("(3) Telegram", 3)]
    [InlineData("  (104) WhatsApp", 104)]
    [InlineData("Telegram (3)", null)]
    [InlineData("(99999999999) Telegram", null)]
    [InlineData("", null)]
    public void PageTitleParser_RecognizesOnlyLeadingValidCount(string title, int? expected)
    {
        Assert.Equal(expected, PageTitleUnreadCountParser.TryParse(title));
    }

    [Fact]
    public void Sort_UsesSortOrderThenDisplayName()
    {
        ServiceInstance second = CreateService("Zulu", 2);
        ServiceInstance firstB = CreateService("Beta", 1);
        ServiceInstance firstA = CreateService("Alpha", 1);

        IReadOnlyList<ServiceInstance> sorted = ServiceInstanceManager.Sort([second, firstB, firstA]);

        Assert.Equal(["Alpha", "Beta", "Zulu"], sorted.Select(service => service.DisplayName));
    }

    [Fact]
    public void Remove_RemovesOnlyTargetAccountAndNormalizesOrder()
    {
        ServiceInstance telegramPersonal = CreateService("Telegram", 0);
        ServiceInstance telegramWork = CreateService("Telegram Работа", 1);
        ServiceInstance whatsapp = CreateService("WhatsApp", 2);
        List<ServiceInstance> services = [telegramPersonal, telegramWork, whatsapp];

        bool removed = ServiceInstanceManager.Remove(services, telegramWork.Id);

        Assert.True(removed);
        Assert.Equal([telegramPersonal.Id, whatsapp.Id], services.Select(service => service.Id));
        Assert.Equal([0, 1], services.Select(service => service.SortOrder));
    }

    [Fact]
    public void Rename_TrimsAndChangesOnlyDisplayName()
    {
        ServiceInstance service = CreateService("Telegram", 0);
        Guid originalId = service.Id;

        ServiceInstanceManager.Rename(service, "  Telegram Работа  ");

        Assert.Equal("Telegram Работа", service.DisplayName);
        Assert.Equal(originalId, service.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_RejectsEmptyName(string displayName)
    {
        Assert.Throws<ArgumentException>(() =>
            ServiceInstanceManager.Rename(CreateService("Telegram", 0), displayName));
    }

    [Fact]
    public void UpdatePerformance_ChangesModeAndSuspendDelay()
    {
        AppSettings settings = AppSettings.CreateDefault();

        ServiceInstanceManager.UpdatePerformance(settings, MemoryMode.Minimal, 30);

        Assert.Equal(MemoryMode.Minimal, settings.MemoryMode);
        Assert.Equal(30, settings.SuspendAfterMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void UpdatePerformance_RejectsUnsafeSuspendDelay(int minutes)
    {
        AppSettings settings = AppSettings.CreateDefault();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ServiceInstanceManager.UpdatePerformance(settings, MemoryMode.Economy, minutes));
    }

    private static ServiceInstance CreateService(string displayName, int sortOrder) =>
        new()
        {
            Id = Guid.NewGuid(),
            ServiceType = ServiceType.Telegram,
            DisplayName = displayName,
            ProfileName = ProfileNameFactory.Create(Guid.NewGuid()),
            IsEnabled = true,
            SortOrder = sortOrder
        };
}
