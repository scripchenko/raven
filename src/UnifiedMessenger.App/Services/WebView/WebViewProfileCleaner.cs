using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewProfileCleaner(IAppPaths appPaths) : IWebViewProfileCleaner
{
    public Task<bool> TryDeleteProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        string profileFolder = GetValidatedProfileFolder(profileName);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(profileFolder))
                    {
                        Directory.Delete(profileFolder, recursive: true);
                    }

                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            },
            cancellationToken);
    }

    public async Task<bool> ProcessPendingDeletionsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool changed = false;
        HashSet<string> activeProfiles = settings.Services
            .Select(service => service.ProfileName)
            .Where(ProfileNameFactory.IsValid)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string profileName in settings.PendingProfileDeletions.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ProfileNameFactory.IsValid(profileName) || activeProfiles.Contains(profileName))
            {
                settings.PendingProfileDeletions.Remove(profileName);
                changed = true;
                continue;
            }

            if (await TryDeleteProfileAsync(profileName, cancellationToken))
            {
                settings.PendingProfileDeletions.Remove(profileName);
                changed = true;
            }
        }

        return changed;
    }

    private string GetValidatedProfileFolder(string profileName)
    {
        if (!ProfileNameFactory.IsValid(profileName))
        {
            throw new ArgumentException("The WebView2 profile name is not safe.", nameof(profileName));
        }

        string root = Path.GetFullPath(appPaths.WebViewDataFolder);
        string profileFolder = Path.GetFullPath(Path.Combine(root, profileName));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!profileFolder.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The WebView2 profile path escaped the shared user data folder.");
        }

        return profileFolder;
    }
}
