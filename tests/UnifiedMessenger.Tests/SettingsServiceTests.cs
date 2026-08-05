using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSettingsAndUsesReadableJson()
    {
        using TempSettingsFolder temp = new();
        JsonSettingsService service = new(temp.SettingsPath);
        Guid serviceId = Guid.NewGuid();
        AppSettings expected = new()
        {
            Theme = AppTheme.Dark,
            MemoryMode = MemoryMode.Minimal,
            SuspendAfterMinutes = 25,
            Services =
            [
                new ServiceInstance
                {
                    Id = serviceId,
                    ServiceType = ServiceType.Telegram,
                    DisplayName = "Telegram Работа",
                    StartUrl = "https://web.telegram.org/k/",
                    ProfileName = $"service-{serviceId:N}",
                    SortOrder = 0
                }
            ]
        };

        await service.SaveAsync(expected);
        SettingsLoadResult result = await service.LoadAsync();
        string json = await File.ReadAllTextAsync(temp.SettingsPath);

        Assert.Equal(AppTheme.Dark, result.Settings.Theme);
        Assert.Equal(MemoryMode.Minimal, result.Settings.MemoryMode);
        Assert.Equal(25, result.Settings.SuspendAfterMinutes);
        ServiceInstance savedService = Assert.Single(result.Settings.Services);
        Assert.Equal(serviceId, savedService.Id);
        Assert.Equal("Telegram Работа", savedService.DisplayName);
        Assert.Contains("\"memoryMode\": \"Minimal\"", json, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(temp.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task Load_WhenFileDoesNotExist_ReturnsDocumentedDefaults()
    {
        using TempSettingsFolder temp = new();
        JsonSettingsService service = new(temp.SettingsPath);

        SettingsLoadResult result = await service.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Equal(AppTheme.System, result.Settings.Theme);
        Assert.Equal(MemoryMode.Economy, result.Settings.MemoryMode);
        Assert.Equal(10, result.Settings.SuspendAfterMinutes);
        Assert.True(result.Settings.CloseToTray);
        Assert.True(result.Settings.RestoreLastService);
        Assert.Empty(result.Settings.Services);
        Assert.Null(result.WarningMessage);
    }

    [Fact]
    public async Task Load_WhenJsonIsCorrupted_MovesItAsideAndReturnsDefaults()
    {
        using TempSettingsFolder temp = new();
        await File.WriteAllTextAsync(temp.SettingsPath, "{ definitely-not-json");
        JsonSettingsService service = new(temp.SettingsPath);

        SettingsLoadResult result = await service.LoadAsync();

        Assert.True(result.RecoveredFromCorruption);
        Assert.NotNull(result.WarningMessage);
        Assert.NotNull(result.CorruptedBackupPath);
        Assert.True(File.Exists(result.CorruptedBackupPath));
        Assert.False(File.Exists(temp.SettingsPath));
        Assert.Equal(MemoryMode.Economy, result.Settings.MemoryMode);
    }

    [Fact]
    public async Task Save_NormalizesOutOfRangePerformanceValue()
    {
        using TempSettingsFolder temp = new();
        JsonSettingsService service = new(temp.SettingsPath);
        AppSettings settings = new() { SuspendAfterMinutes = 5000 };

        await service.SaveAsync(settings);
        SettingsLoadResult result = await service.LoadAsync();

        Assert.Equal(1440, result.Settings.SuspendAfterMinutes);
    }

    private sealed class TempSettingsFolder : IDisposable
    {
        public TempSettingsFolder()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "UnifiedMessenger.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        }

        public string DirectoryPath { get; }
        public string SettingsPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
