using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewProfileCleaner : IWebViewProfileCleaner
{
    private readonly IAppPaths _appPaths;
    private readonly IWebViewProfileFileSystem _fileSystem;

    public WebViewProfileCleaner(IAppPaths appPaths)
        : this(appPaths, new SystemWebViewProfileFileSystem())
    {
    }

    internal WebViewProfileCleaner(
        IAppPaths appPaths,
        IWebViewProfileFileSystem fileSystem)
    {
        _appPaths = appPaths;
        _fileSystem = fileSystem;
    }

    public Task<bool> TryDeleteProfileAsync(
        Guid serviceInstanceId,
        string profileName,
        CancellationToken cancellationToken = default)
    {
        WebViewProfileIdentity identity = WebViewProfileIdentity.Create(
            serviceInstanceId,
            profileName);
        string profileFolder = GetValidatedProfileFolder(identity);

        return Task.Run(
            () => TryDeleteProfile(profileFolder, cancellationToken),
            cancellationToken);
    }

    public async Task<bool> ProcessPendingDeletionsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool changed = false;
        HashSet<string> activeProfiles = settings.Services
            .Select(service =>
                WebViewProfileIdentity.TryCreateFromProfileName(
                    service.ProfileName,
                    out WebViewProfileIdentity identity)
                    ? identity.ProfileName
                    : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (string profileName in settings.PendingProfileDeletions.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!WebViewProfileIdentity.TryCreateFromProfileName(profileName, out WebViewProfileIdentity identity)
                || activeProfiles.Contains(identity.ProfileName))
            {
                settings.PendingProfileDeletions.Remove(profileName);
                changed = true;
                continue;
            }

            if (await TryDeleteProfileAsync(
                    identity.ServiceInstanceId,
                    identity.ProfileName,
                    cancellationToken))
            {
                settings.PendingProfileDeletions.Remove(profileName);
                changed = true;
            }
        }

        return changed;
    }

    private bool TryDeleteProfile(string profileFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!_fileSystem.DirectoryExists(profileFolder))
            {
                return true;
            }

            if (ContainsReparsePoint(profileFolder, cancellationToken))
            {
                return false;
            }

            DeleteDirectoryTree(profileFolder, cancellationToken);
            return true;
        }
        catch (UnsafeProfilePathException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool ContainsReparsePoint(string profileFolder, CancellationToken cancellationToken)
    {
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(profileFolder);

        while (pendingDirectories.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(_fileSystem.GetAttributes(directory)))
            {
                return true;
            }

            foreach (WebViewProfileFileSystemEntry entry in _fileSystem.EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(entry.Attributes))
                {
                    return true;
                }

                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    pendingDirectories.Push(entry.FullPath);
                }
            }
        }

        return false;
    }

    private void DeleteDirectoryTree(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsReparsePoint(_fileSystem.GetAttributes(directory)))
        {
            throw new UnsafeProfilePathException();
        }

        foreach (WebViewProfileFileSystemEntry entry in _fileSystem.EnumerateEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry.Attributes))
            {
                throw new UnsafeProfilePathException();
            }

            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteDirectoryTree(entry.FullPath, cancellationToken);
            }
            else
            {
                _fileSystem.DeleteFile(entry.FullPath);
            }
        }

        _fileSystem.DeleteDirectory(directory);
    }

    private string GetValidatedProfileFolder(WebViewProfileIdentity identity)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_appPaths.WebViewDataFolder));
        string profileFolder = Path.GetFullPath(Path.Combine(root, identity.ProfileName));
        DirectoryInfo? parent = Directory.GetParent(profileFolder);

        if (parent is null
            || !string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(profileFolder, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The WebView2 profile path is not an exact child of the shared user data folder.");
        }

        return profileFolder;
    }

    private static bool IsReparsePoint(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.ReparsePoint);

    private sealed class UnsafeProfilePathException : Exception;
}

internal readonly record struct WebViewProfileFileSystemEntry(
    string FullPath,
    FileAttributes Attributes);

internal interface IWebViewProfileFileSystem
{
    bool DirectoryExists(string path);
    FileAttributes GetAttributes(string path);
    IReadOnlyList<WebViewProfileFileSystemEntry> EnumerateEntries(string directory);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
}

internal sealed class SystemWebViewProfileFileSystem : IWebViewProfileFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public IReadOnlyList<WebViewProfileFileSystemEntry> EnumerateEntries(string directory) =>
        new DirectoryInfo(directory)
            .EnumerateFileSystemInfos()
            .Select(entry => new WebViewProfileFileSystemEntry(entry.FullName, entry.Attributes))
            .ToArray();

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);
}
