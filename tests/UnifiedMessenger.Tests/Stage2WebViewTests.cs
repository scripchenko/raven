using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class Stage2WebViewTests
{
    [Fact]
    public void Provisioner_AddsOneTelegramWithCentralUrlAndSafeProfile()
    {
        AppSettings settings = AppSettings.CreateDefault();
        TelegramServiceProvisioner provisioner = new(new BuiltInServiceCatalog());

        bool changed = provisioner.EnsureTelegramInstance(settings);

        Assert.True(changed);
        ServiceInstance telegram = Assert.Single(settings.Services);
        Assert.Equal(ServiceType.Telegram, telegram.ServiceType);
        Assert.Equal("Telegram", telegram.DisplayName);
        Assert.Equal("https://web.telegram.org/k/", telegram.StartUrl);
        Assert.Equal(ProfileNameFactory.Create(telegram.Id), telegram.ProfileName);
        Assert.True(telegram.IsEnabled);
        Assert.Equal(telegram.Id, settings.LastServiceId);
    }

    [Fact]
    public void Provisioner_IsIdempotentForValidTelegramInstance()
    {
        AppSettings settings = AppSettings.CreateDefault();
        TelegramServiceProvisioner provisioner = new(new BuiltInServiceCatalog());
        Assert.True(provisioner.EnsureTelegramInstance(settings));

        bool changed = provisioner.EnsureTelegramInstance(settings);

        Assert.False(changed);
        Assert.Single(settings.Services);
    }

    [Fact]
    public void Provisioner_RepairsUnsafePersistedProfileWithoutAddingDuplicate()
    {
        Guid id = Guid.NewGuid();
        AppSettings settings = new()
        {
            Services =
            [
                new ServiceInstance
                {
                    Id = id,
                    ServiceType = ServiceType.Telegram,
                    DisplayName = string.Empty,
                    StartUrl = "https://example.com/",
                    ProfileName = "..\\unsafe",
                    IsEnabled = false
                }
            ]
        };
        TelegramServiceProvisioner provisioner = new(new BuiltInServiceCatalog());

        bool changed = provisioner.EnsureTelegramInstance(settings);

        Assert.True(changed);
        ServiceInstance telegram = Assert.Single(settings.Services);
        Assert.Equal(ProfileNameFactory.Create(id), telegram.ProfileName);
        Assert.Equal("https://web.telegram.org/k/", telegram.StartUrl);
        Assert.Equal("Telegram", telegram.DisplayName);
        Assert.True(telegram.IsEnabled);
    }

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("mailto:user@example.com", true)]
    [InlineData("file:///C:/Windows/System32/notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    public void ExternalBrowserPolicy_AllowsOnlyExpectedSchemes(string address, bool expected)
    {
        Assert.Equal(expected, ExternalBrowserService.CanOpen(new Uri(address)));
    }

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.HostNameNotResolved)]
    [InlineData(CoreWebView2WebErrorStatus.Timeout)]
    [InlineData(CoreWebView2WebErrorStatus.ServerUnreachable)]
    [InlineData(CoreWebView2WebErrorStatus.ConnectionReset)]
    [InlineData(CoreWebView2WebErrorStatus.Disconnected)]
    public void ErrorClassifier_RecognizesConnectivityFailures(CoreWebView2WebErrorStatus status)
    {
        Assert.True(WebViewErrorClassifier.IsConnectivityFailure(status));
        Assert.False(string.IsNullOrWhiteSpace(WebViewErrorClassifier.GetUserMessage(status, "Telegram")));
    }

    [Fact]
    public void ErrorClassifier_DoesNotReportCertificateFailureAsOffline()
    {
        Assert.False(WebViewErrorClassifier.IsConnectivityFailure(
            CoreWebView2WebErrorStatus.CertificateIsInvalid));
    }

    [Theory]
    [InlineData("Telegram")]
    [InlineData("WhatsApp")]
    [InlineData("MAX")]
    [InlineData("VK Мессенджер")]
    public void ErrorClassifier_AttributesTimeoutToOriginatingService(string serviceName)
    {
        string message = WebViewErrorClassifier.GetUserMessage(
            CoreWebView2WebErrorStatus.Timeout,
            serviceName);

        Assert.Contains(serviceName, message, StringComparison.Ordinal);
        if (!serviceName.Equals("Telegram", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("Telegram", message, StringComparison.Ordinal);
        }
    }
}
