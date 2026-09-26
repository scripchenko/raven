using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Notifications;
using UnifiedMessenger.App.Services.Localization;

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

            bool wasMigrated = Normalize(settings, resetRuntimeActivity: true);
            return new SettingsLoadResult(settings, WasMigrated: wasMigrated);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
        {
            string? backupPath = MoveCorruptedFileAside();
            string warning = backupPath is null
                ? L.Instance.Get("Settings file is damaged. Defaults were loaded; no backup could be created.")
                : L.Instance.Format("Settings file is damaged. Defaults were loaded; the original file was saved as {0}.", Path.GetFileName(backupPath));

            return new SettingsLoadResult(AppSettings.CreateDefault(), warning, backupPath);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = Normalize(settings, resetRuntimeActivity: false);

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
        options.Converters.Add(new SafeStringEnumJsonConverter<NotificationSoundMode>(NotificationSoundMode.Lantern));
        options.Converters.Add(new SafeStringEnumJsonConverter<LanternSoundSource>(LanternSoundSource.Default));
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static bool Normalize(AppSettings settings, bool resetRuntimeActivity)
    {
        int sourceSchemaVersion = settings.SchemaVersion;
        bool changed = sourceSchemaVersion != AppSettings.CurrentSchemaVersion;
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        string normalizedLanguage = AppLanguage.Normalize(settings.Language);
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

        if (settings.MailAccounts is null)
        {
            settings.MailAccounts = [];
            changed = true;
        }

        if (sourceSchemaVersion < 4 && settings.LastNavigationAccountId is null)
        {
            settings.LastNavigationAccountId = settings.LastServiceId;
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

        changed |= NormalizeNotificationSettings(settings.Notifications);

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

            if (!Enum.IsDefined(service.NotificationPermissionState))
            {
                service.NotificationPermissionState = NotificationPermissionState.Unknown;
                changed = true;
            }

            if (resetRuntimeActivity)
            {
                service.UnreadCount = null;
                service.LanternUnviewedActivityCount = 0;
                service.HasUnreadActivity = false;
            }
        }

        HashSet<Guid> mailAccountIds = [];
        HashSet<string> credentialKeys = new(StringComparer.OrdinalIgnoreCase);
        for (int index = settings.MailAccounts.Count - 1; index >= 0; index--)
        {
            MailAccount account = settings.MailAccounts[index];
            if (account.Id == Guid.Empty
                || !mailAccountIds.Add(account.Id)
                || string.IsNullOrWhiteSpace(account.EmailAddress)
                || string.IsNullOrWhiteSpace(account.CredentialKey)
                || !Guid.TryParseExact(account.CredentialKey, "N", out _)
                || !credentialKeys.Add(account.CredentialKey))
            {
                settings.MailAccounts.RemoveAt(index);
                changed = true;
                continue;
            }

            string normalizedEmail = account.EmailAddress.Trim();
            string? normalizedDisplayName = string.IsNullOrWhiteSpace(account.DisplayName)
                ? null
                : account.DisplayName.Trim();
            changed |= !string.Equals(account.EmailAddress, normalizedEmail, StringComparison.Ordinal)
                || !string.Equals(account.DisplayName, normalizedDisplayName, StringComparison.Ordinal);
            account.EmailAddress = normalizedEmail;
            account.DisplayName = normalizedDisplayName;
            account.SortOrder = Math.Max(0, account.SortOrder);

            if (!Enum.IsDefined(account.Provider))
            {
                settings.MailAccounts.RemoveAt(index);
                changed = true;
                continue;
            }

            MailAuthenticationKind expectedAuthentication = account.Provider == MailProviderType.Gmail
                ? MailAuthenticationKind.OAuth
                : MailAuthenticationKind.Password;
            if (account.AuthenticationKind != expectedAuthentication)
            {
                account.AuthenticationKind = expectedAuthentication;
                changed = true;
            }

            if (account.Provider != MailProviderType.GenericImap && account.GenericConnectionSettings is not null)
            {
                account.GenericConnectionSettings = null;
                changed = true;
            }
        }

        int mailOrder = 0;
        foreach (MailAccount account in settings.MailAccounts.OrderBy(account => account.SortOrder))
        {
            changed |= account.SortOrder != mailOrder;
            account.SortOrder = mailOrder++;
        }

        bool navigationTargetExists = settings.LastNavigationAccountId is Guid navigationId
            && (settings.Services.Any(service => service.Id == navigationId)
                || settings.MailAccounts.Any(account => account.Id == navigationId));
        if (!navigationTargetExists && settings.LastNavigationAccountId is not null)
        {
            settings.LastNavigationAccountId = settings.LastServiceId;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeNotificationSettings(NotificationSettings settings)
    {
        bool changed = false;
        if (!Enum.IsDefined(settings.TelegramSoundMode))
        {
            settings.TelegramSoundMode = NotificationSoundMode.Lantern;
            changed = true;
        }

        if (!Enum.IsDefined(settings.WhatsAppSoundMode))
        {
            settings.WhatsAppSoundMode = NotificationSoundMode.Lantern;
            changed = true;
        }

        if (!Enum.IsDefined(settings.MaxSoundMode))
        {
            settings.MaxSoundMode = NotificationSoundMode.Lantern;
            changed = true;
        }

        if (!Enum.IsDefined(settings.LanternSoundSource))
        {
            settings.LanternSoundSource = LanternSoundSource.Default;
            changed = true;
        }

        string? internalFileName = string.IsNullOrWhiteSpace(settings.CustomSoundInternalFileName)
            ? null
            : settings.CustomSoundInternalFileName.Trim();
        string? displayName = string.IsNullOrWhiteSpace(settings.CustomSoundDisplayName)
            ? null
            : LanternSoundFilePolicy.CreateSafeDisplayName(settings.CustomSoundDisplayName.Trim());
        if (internalFileName is not null
            && !LanternSoundFilePolicy.IsSafeInternalFileName(internalFileName))
        {
            internalFileName = null;
        }

        if (!string.Equals(settings.CustomSoundInternalFileName, internalFileName, StringComparison.Ordinal)
            || !string.Equals(settings.CustomSoundDisplayName, displayName, StringComparison.Ordinal))
        {
            settings.CustomSoundInternalFileName = internalFileName;
            settings.CustomSoundDisplayName = displayName;
            changed = true;
        }

        if (settings.LanternSoundSource is LanternSoundSource.Custom && internalFileName is null)
        {
            settings.LanternSoundSource = LanternSoundSource.Default;
            changed = true;
        }

        return changed;
    }

    private sealed class SafeStringEnumJsonConverter<TEnum>(TEnum fallback) : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.String
                && Enum.TryParse(reader.GetString(), ignoreCase: true, out TEnum parsed)
                && Enum.IsDefined(parsed))
            {
                return parsed;
            }

            if (reader.TokenType is JsonTokenType.Number
                && reader.TryGetInt32(out int numeric)
                && Enum.IsDefined(typeof(TEnum), numeric))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            return fallback;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : fallback.ToString());
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
