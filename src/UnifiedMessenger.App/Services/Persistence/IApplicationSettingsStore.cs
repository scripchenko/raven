using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Persistence;

public interface IApplicationSettingsStore
{
    AppSettings Current { get; }
    bool IsInitialized { get; }
    void Initialize(AppSettings settings);
    Task SaveAsync(CancellationToken cancellationToken = default);
}
