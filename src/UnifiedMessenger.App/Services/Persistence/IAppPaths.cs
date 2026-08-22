namespace UnifiedMessenger.App.Services.Persistence;

public interface IAppPaths
{
    string RoamingDataFolder { get; }
    string LocalDataFolder { get; }
    string SettingsFilePath { get; }
    string WebViewDataFolder { get; }
    string MailCredentialsFolder => System.IO.Path.Combine(LocalDataFolder, "Credentials", "Mail");
    string LogsFolder { get; }
}
