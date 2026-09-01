using System.Drawing;
using System.Media;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Services.Security;
using UnifiedMessenger.App.Services.WebView;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class NotificationSoundSettingsTests
{
    [Fact]
    public void Defaults_UseBundledLanternSoundForSupportedMessengers()
    {
        NotificationSettings settings = new();

        Assert.Equal(NotificationSoundMode.Lantern, settings.GetSoundMode(ServiceType.Telegram));
        Assert.Equal(NotificationSoundMode.Lantern, settings.GetSoundMode(ServiceType.WhatsApp));
        Assert.Equal(NotificationSoundMode.Lantern, settings.GetSoundMode(ServiceType.Max));
        Assert.Equal(NotificationSoundMode.Native, settings.GetSoundMode(ServiceType.VkMessenger));
        Assert.Equal(LanternSoundSource.Default, settings.LanternSoundSource);
    }

    [Fact]
    public void BundledSound_IsValidWavAndCopiedToBuildOutput()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            LanternSoundFilePolicy.DefaultSoundRelativePath);

        Assert.True(File.Exists(path));
        using SoundPlayer player = new(path);
        player.Load();
        Assert.True(new FileInfo(path).Length > 44);
    }

    [Fact]
    public async Task SoundModesAndCustomMetadata_PersistWithoutAbsoluteSourcePath()
    {
        using TemporaryDirectory temp = new();
        string settingsPath = Path.Combine(temp.Path, "settings.json");
        JsonSettingsService persistence = new(settingsPath);
        AppSettings settings = AppSettings.CreateDefault();
        settings.Notifications.TelegramSoundMode = NotificationSoundMode.Native;
        settings.Notifications.WhatsAppSoundMode = NotificationSoundMode.Lantern;
        settings.Notifications.MaxSoundMode = NotificationSoundMode.Native;
        settings.Notifications.LanternSoundSource = LanternSoundSource.Custom;
        settings.Notifications.CustomSoundInternalFileName = "custom-notification.mp3";
        settings.Notifications.CustomSoundDisplayName = "my-sound.mp3";

        await persistence.SaveAsync(settings);
        SettingsLoadResult loaded = await persistence.LoadAsync();
        string json = await File.ReadAllTextAsync(settingsPath);

        Assert.Equal(NotificationSoundMode.Native, loaded.Settings.Notifications.TelegramSoundMode);
        Assert.Equal(NotificationSoundMode.Lantern, loaded.Settings.Notifications.WhatsAppSoundMode);
        Assert.Equal(NotificationSoundMode.Native, loaded.Settings.Notifications.MaxSoundMode);
        Assert.Equal(LanternSoundSource.Custom, loaded.Settings.Notifications.LanternSoundSource);
        Assert.Equal("custom-notification.mp3", loaded.Settings.Notifications.CustomSoundInternalFileName);
        Assert.Equal("my-sound.mp3", loaded.Settings.Notifications.CustomSoundDisplayName);
        Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidNewEnumValues_FallBackWithoutLosingOtherSettings()
    {
        using TemporaryDirectory temp = new();
        string path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 5,
              "closeToTray": false,
              "notifications": {
                "telegramSoundMode": "NotARealMode",
                "lanternSoundSource": "NotARealSource"
              }
            }
            """);

        SettingsLoadResult loaded = await new JsonSettingsService(path).LoadAsync();

        Assert.False(loaded.Settings.CloseToTray);
        Assert.Equal(NotificationSoundMode.Lantern, loaded.Settings.Notifications.TelegramSoundMode);
        Assert.Equal(LanternSoundSource.Default, loaded.Settings.Notifications.LanternSoundSource);
    }

    [Fact]
    public void MissingCustomInternalFile_FallsBackToBundledSound()
    {
        using TemporaryDirectory temp = new();
        TestAppPaths paths = new(temp.Path);
        LanternSoundFileService service = new(paths, new FakeAudioFileValidator(true));
        NotificationSettings settings = new()
        {
            LanternSoundSource = LanternSoundSource.Custom,
            CustomSoundInternalFileName = "custom-notification.wav",
            CustomSoundDisplayName = "missing.wav"
        };

        Assert.Equal(service.DefaultSoundPath, service.ResolvePlaybackPath(settings));
    }

    [Fact]
    public async Task ValidCustomSound_IsCopiedToInternalAppDataLocation()
    {
        using TemporaryDirectory temp = new();
        string source = Path.Combine(temp.Path, "source.wav");
        await File.WriteAllBytesAsync(source, [0x52, 0x49, 0x46, 0x46]);
        TestAppPaths paths = new(Path.Combine(temp.Path, "appdata"));
        LanternSoundFileService service = new(paths, new FakeAudioFileValidator(true));

        LanternSoundImportResult result = await service.ImportAsync(source);

        Assert.True(result.Success);
        Assert.Equal("custom-notification.wav", result.InternalFileName);
        Assert.Equal("source.wav", result.DisplayName);
        Assert.True(File.Exists(Path.Combine(paths.NotificationSoundsFolder, result.InternalFileName!)));
        Assert.NotEqual(Path.GetFullPath(source), Path.GetFullPath(
            Path.Combine(paths.NotificationSoundsFolder, result.InternalFileName!)));
    }

    [Fact]
    public async Task CorruptUnsupportedAndOversizedCustomSounds_AreRejected()
    {
        using TemporaryDirectory temp = new();
        TestAppPaths paths = new(Path.Combine(temp.Path, "appdata"));
        string corrupt = Path.Combine(temp.Path, "corrupt.wav");
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3]);
        LanternSoundFileService corruptService = new(paths, new FakeAudioFileValidator(false));
        Assert.False((await corruptService.ImportAsync(corrupt)).Success);

        string unsupported = Path.Combine(temp.Path, "sound.flac");
        await File.WriteAllBytesAsync(unsupported, [1, 2, 3]);
        LanternSoundFileService service = new(paths, new FakeAudioFileValidator(true));
        Assert.False((await service.ImportAsync(unsupported)).Success);

        string oversized = Path.Combine(temp.Path, "large.mp3");
        await using (FileStream stream = File.Create(oversized))
        {
            stream.SetLength(LanternSoundFilePolicy.MaximumFileSizeBytes + 1);
        }

        Assert.False((await service.ImportAsync(oversized)).Success);
    }

    [Fact]
    public async Task RestoreDefault_ClearsCustomMetadataAndDeletesInternalCopy()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        AppSettings settings = new() { Services = [service] };
        settings.Notifications.LanternSoundSource = LanternSoundSource.Custom;
        settings.Notifications.CustomSoundInternalFileName = "custom-notification.wav";
        settings.Notifications.CustomSoundDisplayName = "chosen.wav";
        FakeSettingsStore store = new(settings);
        FakeLanternSoundFileService soundFiles = new();
        using MainWindowViewModel viewModel = new(
            new BuiltInServiceCatalog(),
            new StubSessionManager(),
            store,
            new ServiceActivityCoordinator(),
            new StubWebNotificationCoordinator(),
            mailActivityCoordinator: null,
            soundFiles,
            new FakeNotificationSoundPlayer());
        viewModel.Initialize(settings);

        await viewModel.RestoreDefaultLanternSoundAsync();

        Assert.Equal(LanternSoundSource.Default, settings.Notifications.LanternSoundSource);
        Assert.Null(settings.Notifications.CustomSoundInternalFileName);
        Assert.Null(settings.Notifications.CustomSoundDisplayName);
        Assert.Equal("custom-notification.wav", soundFiles.DeletedFileName);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task CustomSelectionAndPreview_UpdateSharedLanternSoundSetting()
    {
        ServiceInstance service = CreateService(ServiceType.Telegram);
        AppSettings settings = new() { Services = [service] };
        FakeSettingsStore store = new(settings);
        FakeLanternSoundFileService soundFiles = new()
        {
            ImportResult = new LanternSoundImportResult(
                true,
                "custom-notification.mp3",
                "chosen.mp3")
        };
        FakeNotificationSoundPlayer player = new();
        using MainWindowViewModel viewModel = new(
            new BuiltInServiceCatalog(),
            new StubSessionManager(),
            store,
            new ServiceActivityCoordinator(),
            new StubWebNotificationCoordinator(),
            mailActivityCoordinator: null,
            soundFiles,
            player);
        viewModel.Initialize(settings);

        LanternSoundImportResult result = await viewModel.ImportCustomLanternSoundAsync("source.mp3");
        bool previewStarted = viewModel.PreviewLanternSound();
        await viewModel.SetNotificationSoundModeAsync(ServiceType.Telegram, NotificationSoundMode.Native);

        Assert.True(result.Success);
        Assert.Equal(LanternSoundSource.Custom, settings.Notifications.LanternSoundSource);
        Assert.Equal("custom-notification.mp3", settings.Notifications.CustomSoundInternalFileName);
        Assert.Equal("chosen.mp3", settings.Notifications.CustomSoundDisplayName);
        Assert.True(previewStarted);
        Assert.Equal(1, player.PreviewCount);
        Assert.Equal(NotificationSoundMode.Native, settings.Notifications.TelegramSoundMode);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void SupportedPickerFormats_MatchProductionPolicy()
    {
        Assert.Contains(".wav", LanternSoundFilePolicy.Extensions);
        Assert.Contains(".mp3", LanternSoundFilePolicy.Extensions);
        Assert.Contains(".wma", LanternSoundFilePolicy.Extensions);
        Assert.DoesNotContain(".ogg", LanternSoundFilePolicy.Extensions);
        Assert.DoesNotContain(".flac", LanternSoundFilePolicy.Extensions);
    }

    [Fact]
    public void NotificationTag_IsHashedInMemoryAndRawValueIsNotForwarded()
    {
        const string rawTag = "private-provider-tag";

        string? hash = WebViewSessionManager.HashNotificationTag(rawTag);

        Assert.NotNull(hash);
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(rawTag, hash, StringComparison.Ordinal);
        Assert.Equal(hash, WebViewSessionManager.HashNotificationTag(rawTag));
        Assert.Null(WebViewSessionManager.HashNotificationTag(string.Empty));
    }

    private static ServiceInstance CreateService(ServiceType type)
    {
        Guid id = Guid.NewGuid();
        return new ServiceInstance
        {
            Id = id,
            ServiceType = type,
            DisplayName = type.ToString(),
            StartUrl = new BuiltInServiceCatalog().Get(type).StartUri?.AbsoluteUri,
            ProfileName = ProfileNameFactory.Create(id),
            IsEnabled = true,
            NotificationPermissionState = NotificationPermissionState.Allowed
        };
    }

    private sealed class FakeAudioFileValidator(bool result) : IAudioFileValidator
    {
        public Task<bool> CanOpenAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string RoamingDataFolder => Path.Combine(root, "roaming");
        public string LocalDataFolder => Path.Combine(root, "local");
        public string SettingsFilePath => Path.Combine(RoamingDataFolder, "settings.json");
        public string WebViewDataFolder => Path.Combine(LocalDataFolder, "WebView2");
        public string NotificationSoundsFolder => Path.Combine(LocalDataFolder, "Sounds");
        public string LogsFolder => Path.Combine(LocalDataFolder, "Logs");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LanternSoundTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FakeSettingsStore(AppSettings settings) : IApplicationSettingsStore
    {
        public AppSettings Current { get; private set; } = settings;
        public bool IsInitialized => true;
        public int SaveCount { get; private set; }
        public void Initialize(AppSettings value) => Current = value;
        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLanternSoundFileService : ILanternSoundFileService
    {
        public string DefaultSoundPath => "default.wav";
        public string? DeletedFileName { get; private set; }
        public LanternSoundImportResult ImportResult { get; set; } =
            LanternSoundImportResult.Failed("not used");
        public Task<LanternSoundImportResult> ImportAsync(string sourceFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(ImportResult);
        public string ResolvePlaybackPath(NotificationSettings settings) => DefaultSoundPath;
        public void DeleteInternalCopy(string? internalFileName) => DeletedFileName = internalFileName;
    }

    private sealed class FakeNotificationSoundPlayer : INotificationSoundPlayer
    {
        public int PreviewCount { get; private set; }
        public bool TryPlay(ServiceType serviceType) => true;
        public bool TryPreviewLanternSound()
        {
            PreviewCount++;
            return true;
        }
    }

    private sealed class StubWebNotificationCoordinator : IWebNotificationCoordinator
    {
        public int PendingCount => 0;
        public bool HasActiveNotification => false;
        public void Handle(WebNotificationRequest request) { }
        public void DiscardPending(Guid serviceInstanceId) { }
        public void OnDoNotDisturbChanged(bool enabled) { }
        public void Shutdown() { }
        public void Dispose() { }
    }

    private sealed class StubSessionManager : IWebViewSessionManager
    {
        public event EventHandler<WebViewSessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<WebViewSessionRecreationRequestedEventArgs>? SessionRecreationRequested { add { } remove { } }
        public event EventHandler<ServiceDocumentTitleChangedEventArgs>? DocumentTitleChanged { add { } remove { } }
        public event EventHandler<WebNotificationReceivedEventArgs>? NotificationReceived { add { } remove { } }
        public event EventHandler<BackgroundNotificationActivityReceivedEventArgs>? BackgroundNotificationActivityReceived { add { } remove { } }
        public WebViewSessionState State => WebViewSessionState.Uninitialized;
        public bool IsShutdownStarted => false;
        public int InitializedSessionCount => 0;
        public int InitialNavigationCount => 0;
        public Task<bool> InitializeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, bool activate, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> PrimeAsync(IntPtr parentWindow, Rectangle bounds, ServiceInstance serviceInstance, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public bool IsSessionInitialized(Guid serviceInstanceId) => false;
        public void ActivateSession(Guid serviceInstanceId, Rectangle bounds, bool isVisible, bool moveFocus = false) { }
        public void UpdateActiveSessionLayout(Rectangle bounds, bool isVisible) { }
        public void NotifyParentWindowPositionChanged() { }
        public bool HasSession(Guid serviceInstanceId) => false;
        public void DeactivateSession() { }
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void NavigateHome() { }
        public void Retry() { }
        public void ReleaseSession(Guid serviceInstanceId) { }
        public Task<bool> ClearProfileAsync(ServiceInstance serviceInstance, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void ReleaseAllSessions() { }
        public void BeginShutdown() { }
        public void Dispose() { }
    }
}
