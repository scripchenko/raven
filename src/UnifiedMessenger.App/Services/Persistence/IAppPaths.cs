namespace UnifiedMessenger.App.Services.Persistence;

public interface IAppPaths
{
    string RoamingDataFolder { get; }
    string LocalDataFolder { get; }
    string SettingsFilePath { get; }
    string WebViewDataFolder { get; }
    string MailCredentialsFolder => System.IO.Path.Combine(LocalDataFolder, "Credentials", "Mail");
    string YandexDraftRecoveryFilePath =>
        System.IO.Path.Combine(LocalDataFolder, "Recovery", "YandexDrafts", "recovery.bin");
    string RemoteImageSenderTrustFolder =>
        System.IO.Path.Combine(LocalDataFolder, "Privacy", "RemoteImageSenderTrust");
    string NotificationSoundsFolder => System.IO.Path.Combine(LocalDataFolder, "Sounds");
    string GoogleOAuthClientConfigurationPath =>
        System.IO.Path.Combine(LocalDataFolder, "GoogleOAuth", "client_secret.json");
    string LogsFolder { get; }
}
