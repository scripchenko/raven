using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Persistence;

public interface ISettingsService
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
