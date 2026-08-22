using System.Drawing;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public interface IWebViewStartupPrimeCoordinator
{
    Task<StartupPrimeResult> PrimeAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        IReadOnlyList<ServiceInstance> services,
        Guid? selectedServiceId,
        IProgress<StartupPrimeProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record StartupPrimeProgress(
    Guid ServiceInstanceId,
    string DisplayName,
    int Current,
    int Total);

public sealed record StartupPrimeResult(
    IReadOnlyList<Guid> AttemptedServiceIds,
    IReadOnlyList<Guid> InitializedServiceIds,
    IReadOnlyList<Guid> FailedServiceIds)
{
    public static StartupPrimeResult Empty { get; } = new([], [], []);
}

public enum EnabledAccountInitializationAction
{
    None,
    PrimeWhileHidden,
    WaitForSelectionOrHiddenState
}

public static class EnabledAccountInitializationPolicy
{
    public static EnabledAccountInitializationAction Decide(
        bool isEnabled,
        bool isSessionInitialized,
        bool isMainWindowVisible) =>
        !isEnabled || isSessionInitialized
            ? EnabledAccountInitializationAction.None
            : isMainWindowVisible
                ? EnabledAccountInitializationAction.WaitForSelectionOrHiddenState
                : EnabledAccountInitializationAction.PrimeWhileHidden;
}
