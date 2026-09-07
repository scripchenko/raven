using System.IO;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.Tests;

public sealed class WebViewProfileSafetyTests
{
    [Fact]
    public async Task ExactServiceProfile_IsDeletedWithoutTouchingSibling()
    {
        using TempAppPaths paths = new();
        Guid targetId = Guid.NewGuid();
        Guid siblingId = Guid.NewGuid();
        string targetName = ProfileNameFactory.Create(targetId);
        string siblingName = ProfileNameFactory.Create(siblingId);
        string target = Directory.CreateDirectory(Path.Combine(paths.WebViewDataFolder, targetName)).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(target, "nested")).FullName;
        string sibling = Directory.CreateDirectory(Path.Combine(paths.WebViewDataFolder, siblingName)).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "target.marker"), "target");
        await File.WriteAllTextAsync(Path.Combine(sibling, "sibling.marker"), "sibling");
        WebViewProfileCleaner cleaner = new(paths);

        bool deleted = await cleaner.TryDeleteProfileAsync(targetId, targetName);

        Assert.True(deleted);
        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(sibling, "sibling.marker")));
    }

    [Fact]
    public async Task MismatchedServiceIdentity_IsRejected()
    {
        using TempAppPaths paths = new();
        WebViewProfileCleaner cleaner = new(paths);
        Guid requestedId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cleaner.TryDeleteProfileAsync(requestedId, ProfileNameFactory.Create(Guid.NewGuid())));
    }

    [Theory]
    [InlineData("service-not-a-guid")]
    [InlineData("..\\outside")]
    [InlineData("../outside")]
    [InlineData("")]
    public async Task MalformedOutsideOrRootProfile_IsRejected(string profileName)
    {
        using TempAppPaths paths = new();
        WebViewProfileCleaner cleaner = new(paths);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cleaner.TryDeleteProfileAsync(Guid.NewGuid(), profileName));
    }

    [Fact]
    public async Task AccountACannotDeleteAccountBProfile()
    {
        using TempAppPaths paths = new();
        Guid accountA = Guid.NewGuid();
        Guid accountB = Guid.NewGuid();
        string accountBProfile = ProfileNameFactory.Create(accountB);
        string accountBFolder = Directory.CreateDirectory(
            Path.Combine(paths.WebViewDataFolder, accountBProfile)).FullName;
        await File.WriteAllTextAsync(Path.Combine(accountBFolder, "keep.marker"), "keep");
        WebViewProfileCleaner cleaner = new(paths);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cleaner.TryDeleteProfileAsync(accountA, accountBProfile));

        Assert.True(File.Exists(Path.Combine(accountBFolder, "keep.marker")));
    }

    [Fact]
    public async Task TargetReparsePoint_IsRejected()
    {
        using TempAppPaths paths = new();
        Guid serviceId = Guid.NewGuid();
        string profileName = ProfileNameFactory.Create(serviceId);
        string target = Path.GetFullPath(Path.Combine(paths.WebViewDataFolder, profileName));
        FakeProfileFileSystem fileSystem = new();
        fileSystem.AddDirectory(target, isReparsePoint: true);
        WebViewProfileCleaner cleaner = new(paths, fileSystem);

        bool deleted = await cleaner.TryDeleteProfileAsync(serviceId, profileName);

        Assert.False(deleted);
        Assert.Empty(fileSystem.DeletedPaths);
        Assert.Empty(fileSystem.EnumeratedDirectories);
    }

    [Fact]
    public async Task ChildReparsePoint_IsNotFollowed()
    {
        using TempAppPaths paths = new();
        Guid serviceId = Guid.NewGuid();
        string profileName = ProfileNameFactory.Create(serviceId);
        string target = Path.GetFullPath(Path.Combine(paths.WebViewDataFolder, profileName));
        string childLink = Path.Combine(target, "linked-cache");
        FakeProfileFileSystem fileSystem = new();
        fileSystem.AddDirectory(target);
        fileSystem.AddDirectory(childLink, isReparsePoint: true);
        fileSystem.SetEntries(
            target,
            new WebViewProfileFileSystemEntry(
                childLink,
                FileAttributes.Directory | FileAttributes.ReparsePoint));
        WebViewProfileCleaner cleaner = new(paths, fileSystem);

        bool deleted = await cleaner.TryDeleteProfileAsync(serviceId, profileName);

        Assert.False(deleted);
        Assert.Contains(target, fileSystem.EnumeratedDirectories);
        Assert.DoesNotContain(childLink, fileSystem.EnumeratedDirectories);
        Assert.Empty(fileSystem.DeletedPaths);
    }

    private sealed class FakeProfileFileSystem : IWebViewProfileFileSystem
    {
        private readonly Dictionary<string, FileAttributes> _attributes =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<WebViewProfileFileSystemEntry>> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> EnumeratedDirectories { get; } = [];
        public List<string> DeletedPaths { get; } = [];

        public void AddDirectory(string path, bool isReparsePoint = false)
        {
            _attributes[path] = FileAttributes.Directory
                | (isReparsePoint ? FileAttributes.ReparsePoint : 0);
            _entries.TryAdd(path, []);
        }

        public void SetEntries(string directory, params WebViewProfileFileSystemEntry[] entries) =>
            _entries[directory] = entries;

        public bool DirectoryExists(string path) => _attributes.ContainsKey(path);

        public FileAttributes GetAttributes(string path) => _attributes[path];

        public IReadOnlyList<WebViewProfileFileSystemEntry> EnumerateEntries(string directory)
        {
            EnumeratedDirectories.Add(directory);
            return _entries[directory];
        }

        public void DeleteFile(string path) => DeletedPaths.Add(path);

        public void DeleteDirectory(string path) => DeletedPaths.Add(path);
    }

    private sealed class TempAppPaths : IAppPaths, IDisposable
    {
        public TempAppPaths()
        {
            LocalDataFolder = Path.Combine(
                Path.GetTempPath(),
                "UnifiedMessenger.Tests",
                Guid.NewGuid().ToString("N"));
            RoamingDataFolder = Path.Combine(LocalDataFolder, "Roaming");
            WebViewDataFolder = Path.Combine(LocalDataFolder, "WebView2");
            Directory.CreateDirectory(WebViewDataFolder);
        }

        public string RoamingDataFolder { get; }
        public string LocalDataFolder { get; }
        public string SettingsFilePath => Path.Combine(RoamingDataFolder, "settings.json");
        public string WebViewDataFolder { get; }
        public string LogsFolder => Path.Combine(LocalDataFolder, "Logs");

        public void Dispose()
        {
            if (Directory.Exists(LocalDataFolder))
            {
                Directory.Delete(LocalDataFolder, recursive: true);
            }
        }
    }
}
