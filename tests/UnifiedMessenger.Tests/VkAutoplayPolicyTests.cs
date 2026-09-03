using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class VkAutoplayPolicyTests
{
    [Fact]
    public async Task VkProfile_AppliesAutoplayDenyForExactOrigin()
    {
        PermissionRecorder recorder = new();

        await VkAutoplayPolicy.ApplyAsync(ServiceType.VkMessenger, recorder.SetAsync);

        PermissionChange change = Assert.Single(recorder.Changes);
        Assert.Equal(CoreWebView2PermissionKind.Autoplay, change.Kind);
        Assert.Equal("https://web.vk.me", change.Origin);
        Assert.Equal(CoreWebView2PermissionState.Deny, change.State);
    }

    [Fact]
    public async Task Telegram_DoesNotChangeAutoplay()
    {
        PermissionRecorder recorder = new();

        await VkAutoplayPolicy.ApplyAsync(ServiceType.Telegram, recorder.SetAsync);

        Assert.Empty(recorder.Changes);
    }

    [Fact]
    public async Task WhatsApp_DoesNotChangeAutoplay()
    {
        PermissionRecorder recorder = new();

        await VkAutoplayPolicy.ApplyAsync(ServiceType.WhatsApp, recorder.SetAsync);

        Assert.Empty(recorder.Changes);
    }

    [Fact]
    public async Task Max_DoesNotChangeAutoplay()
    {
        PermissionRecorder recorder = new();

        await VkAutoplayPolicy.ApplyAsync(ServiceType.Max, recorder.SetAsync);

        Assert.Empty(recorder.Changes);
    }

    [Fact]
    public async Task Gmail_DoesNotChangeAutoplay()
    {
        PermissionRecorder recorder = new();

        await VkAutoplayPolicy.ApplyAsync(ServiceType.Gmail, recorder.SetAsync);

        Assert.Empty(recorder.Changes);
    }

    [Fact]
    public async Task MultipleVkAccounts_ApplyPolicyIndependentlyToEveryProfile()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        Dictionary<string, PermissionRecorder> profiles = new()
        {
            [ProfileNameFactory.Create(firstId)] = new PermissionRecorder(),
            [ProfileNameFactory.Create(secondId)] = new PermissionRecorder()
        };

        foreach (PermissionRecorder profile in profiles.Values)
        {
            await VkAutoplayPolicy.ApplyAsync(ServiceType.VkMessenger, profile.SetAsync);
        }

        Assert.Equal(2, profiles.Count);
        Assert.All(profiles.Values, profile =>
        {
            PermissionChange change = Assert.Single(profile.Changes);
            Assert.Equal(CoreWebView2PermissionKind.Autoplay, change.Kind);
            Assert.Equal(VkAutoplayPolicy.Origin, change.Origin);
            Assert.Equal(CoreWebView2PermissionState.Deny, change.State);
        });
    }

    [Fact]
    public async Task VkPolicy_DoesNotChangeNotificationsPermission()
    {
        PermissionRecorder recorder = new();

        await VkAutoplayPolicy.ApplyAsync(ServiceType.VkMessenger, recorder.SetAsync);

        Assert.DoesNotContain(
            recorder.Changes,
            change => change.Kind is CoreWebView2PermissionKind.Notifications);
    }

    [Fact]
    public void ProductionPolicy_HasNoCoreWebViewDependencyForMuting()
    {
        Type[] parameterTypes = typeof(VkAutoplayPolicy)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(CoreWebView2), parameterTypes);
        Assert.Contains(typeof(CoreWebView2Profile), parameterTypes);
    }

    private sealed class PermissionRecorder
    {
        public List<PermissionChange> Changes { get; } = [];

        public Task SetAsync(
            CoreWebView2PermissionKind kind,
            string origin,
            CoreWebView2PermissionState state)
        {
            Changes.Add(new PermissionChange(kind, origin, state));
            return Task.CompletedTask;
        }
    }

    private sealed record PermissionChange(
        CoreWebView2PermissionKind Kind,
        string Origin,
        CoreWebView2PermissionState State);
}
