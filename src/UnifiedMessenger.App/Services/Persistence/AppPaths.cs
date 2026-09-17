using System.IO;

namespace UnifiedMessenger.App.Services.Persistence;

public sealed class AppPaths : IAppPaths
{
    private const string ApplicationFolderName = "UnifiedMessenger";

    public AppPaths()
    {
        RoamingDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ApplicationFolderName);
        LocalDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName);
    }

    public string RoamingDataFolder { get; }
    public string LocalDataFolder { get; }
    public string SettingsFilePath => Path.Combine(RoamingDataFolder, "settings.json");
    public string WebViewDataFolder => Path.Combine(LocalDataFolder, "WebView2");
    public string MailCredentialsFolder => Path.Combine(LocalDataFolder, "Credentials", "Mail");
    public string YandexDraftRecoveryFilePath =>
        Path.Combine(LocalDataFolder, "Recovery", "YandexDrafts", "recovery.bin");
    // Stable compatibility path: the managed-IMAP recovery store intentionally
    // continues to read the original Yandex v1.0 recovery file.
    public string ManagedImapDraftRecoveryFilePath => YandexDraftRecoveryFilePath;
    public string RemoteImageSenderTrustFolder =>
        Path.Combine(LocalDataFolder, "Privacy", "RemoteImageSenderTrust");
    public string NotificationSoundsFolder => Path.Combine(LocalDataFolder, "Sounds");
    public string GoogleOAuthClientConfigurationPath =>
        Path.Combine(LocalDataFolder, "GoogleOAuth", "client_secret.json");
    public string LogsFolder => Path.Combine(LocalDataFolder, "Logs");
}
