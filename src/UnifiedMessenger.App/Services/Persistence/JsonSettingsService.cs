using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Persistence;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _settingsFilePath;

    public JsonSettingsService(IAppPaths appPaths)
        : this(appPaths?.SettingsFilePath ?? throw new ArgumentNullException(nameof(appPaths)))
    {
    }

    public JsonSettingsService(string settingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        _settingsFilePath = Path.GetFullPath(settingsFilePath);
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new SettingsLoadResult(AppSettings.CreateDefault());
        }

        try
        {
            await using FileStream stream = new(
                _settingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                SerializerOptions,
                cancellationToken);

            if (settings is null || settings.SchemaVersion <= 0)
            {
                throw new InvalidDataException("The settings document is empty or has an invalid schema version.");
            }

            bool wasMigrated = Normalize(settings);
            return new SettingsLoadResult(settings, WasMigrated: wasMigrated);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
        {
            string? backupPath = MoveCorruptedFileAside();
            string warning = backupPath is null
                ? "Файл настроек повреждён. Загружены настройки по умолчанию; резервную копию создать не удалось."
                : $"Файл настроек повреждён. Загружены настройки по умолчанию, исходный файл сохранён как {Path.GetFileName(backupPath)}.";

            return new SettingsLoadResult(AppSettings.CreateDefault(), warning, backupPath);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = Normalize(settings);

        string directory = Path.GetDirectoryName(_settingsFilePath)
            ?? throw new InvalidOperationException("The settings path must include a directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsFilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json, Utf8WithoutBom, cancellationToken);

            await using (FileStream verificationStream = File.OpenRead(temporaryPath))
            {
                _ = await JsonSerializer.DeserializeAsync<AppSettings>(
                    verificationStream,
                    SerializerOptions,
                    cancellationToken)
                    ?? throw new InvalidDataException("Serialized settings could not be verified.");
            }

            File.Move(temporaryPath, _settingsFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static bool Normalize(AppSettings settings)
    {
        bool changed = settings.SchemaVersion != AppSettings.CurrentSchemaVersion;
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        string normalizedLanguage = string.IsNullOrWhiteSpace(settings.Language) ? "ru-RU" : settings.Language.Trim();
        changed |= !string.Equals(settings.Language, normalizedLanguage, StringComparison.Ordinal);
        settings.Language = normalizedLanguage;

        int normalizedSuspendDelay = Math.Clamp(settings.SuspendAfterMinutes, 1, 1440);
        changed |= settings.SuspendAfterMinutes != normalizedSuspendDelay;
        settings.SuspendAfterMinutes = normalizedSuspendDelay;

        if (settings.Services is null)
        {
            settings.Services = [];
            changed = true;
        }

        if (settings.PendingProfileDeletions is null)
        {
            settings.PendingProfileDeletions = [];
            changed = true;
        }

        if (settings.Notifications is null)
        {
            settings.Notifications = new NotificationSettings();
            changed = true;
        }

        if (settings.Window is null)
        {
            settings.Window = new WindowSettings();
            changed = true;
        }

        foreach (ServiceInstance service in settings.Services)
        {
            if (service.DisplayName is null)
            {
                service.DisplayName = string.Empty;
                changed = true;
            }

            if (service.ProfileName is null)
            {
                service.ProfileName = string.Empty;
                changed = true;
            }
        }

        return changed;
    }

    private string? MoveCorruptedFileAside()
    {
        try
        {
            string directory = Path.GetDirectoryName(_settingsFilePath)!;
            string fileName = Path.GetFileNameWithoutExtension(_settingsFilePath);
            string extension = Path.GetExtension(_settingsFilePath);
            string backupPath = Path.Combine(
                directory,
                $"{fileName}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
            File.Move(_settingsFilePath, backupPath);
            return backupPath;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
