using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.WebView;

public interface IWebViewProfileCleaner
{
    Task<bool> TryDeleteProfileAsync(string profileName, CancellationToken cancellationToken = default);
    Task<bool> ProcessPendingDeletionsAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
