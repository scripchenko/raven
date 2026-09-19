using UnifiedMessenger.App.Services;

namespace UnifiedMessenger.Tests;

public sealed class ApplicationSingleInstanceTests
{
    [Fact]
    public async Task SecondInstanceSignalsPrimaryWithoutAcquiringOwnership()
    {
        string instanceName = CreateInstanceName();
        using ApplicationSingleInstanceCoordinator primary = new(instanceName);
        using ApplicationSingleInstanceCoordinator secondary = new(instanceName);
        TaskCompletionSource activation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(primary.TryAcquirePrimaryInstance());
        primary.ActivationRequested += (_, _) => activation.TrySetResult();
        primary.StartActivationListener();
        Assert.False(secondary.TryAcquirePrimaryInstance());

        bool signaled = await secondary.TrySignalPrimaryInstanceAsync(TimeSpan.FromSeconds(5));

        Assert.True(signaled);
        await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);
    }

    [Fact]
    public void ActivationDuringStartupIsDeliveredWhenUiTargetBecomesReady()
    {
        ApplicationActivationRequestBuffer requests = new();
        int activationCount = 0;

        requests.RequestActivation();
        Assert.Equal(0, activationCount);

        requests.SetTarget(() => activationCount++);

        Assert.Equal(1, activationCount);
    }

    [Fact]
    public void HiddenWindowActivationUsesCurrentTargetWithoutReselectingState()
    {
        ApplicationActivationRequestBuffer requests = new();
        int activationCount = 0;
        requests.SetTarget(() => activationCount++);

        requests.RequestActivation();
        requests.RequestActivation();

        Assert.Equal(2, activationCount);
    }

    [Fact]
    public void CleanExitReleasesSingletonForNextLaunch()
    {
        string instanceName = CreateInstanceName();
        ApplicationSingleInstanceCoordinator first = new(instanceName);
        Assert.True(first.TryAcquirePrimaryInstance());
        first.Dispose();

        using ApplicationSingleInstanceCoordinator next = new(instanceName);

        Assert.True(next.TryAcquirePrimaryInstance());
    }

    [Fact]
    public void AppClaimsSingletonAndStartsActivationListenerBeforeFullInitialization()
    {
        string app = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "App.xaml.cs"));

        int acquire = app.IndexOf("TryAcquirePrimaryInstance()", StringComparison.Ordinal);
        int listener = app.IndexOf("StartActivationListener()", StringComparison.Ordinal);
        int services = app.IndexOf("ConfigureServices()", StringComparison.Ordinal);

        Assert.True(acquire >= 0 && acquire < services);
        Assert.True(listener >= 0 && listener < services);
        Assert.Contains("TrySignalPrimaryInstanceAsync", app, StringComparison.Ordinal);
        Assert.Contains("_activationRequests.SetTarget(DispatchActivationRequest)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowActivationUsesNativeRestoreForHiddenOrIconicHwnd()
    {
        string activation = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Tray", "WpfWindowActivationService.cs"));
        string nativeActivation = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Tray", "NativeWindowActivation.cs"));

        Assert.Contains("NativeWindowActivation.IsMinimized(windowHandle)", activation, StringComparison.Ordinal);
        Assert.Contains("NativeWindowActivation.RestoreAndActivate(windowHandle)", activation, StringComparison.Ordinal);
        Assert.Contains("ShowWindowAsync(windowHandle, RestoreWindow)", nativeActivation, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow(windowHandle)", nativeActivation, StringComparison.Ordinal);
    }

    private static string CreateInstanceName() =>
        $"Scripchenko.Raven.Tests.{Guid.NewGuid():N}";

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
