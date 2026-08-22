namespace UnifiedMessenger.App.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public MemoryMode MemoryMode { get; set; } = MemoryMode.Economy;
    public int SuspendAfterMinutes { get; set; } = 10;
    public bool CloseToTray { get; set; } = true;
    public bool HasShownTrayHint { get; set; }
    public bool CloseCompletelyOnWindowClose { get; set; }
    public bool RestoreLastService { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public string Language { get; set; } = "ru-RU";
    public Guid? LastServiceId { get; set; }
    public Guid? LastNavigationAccountId { get; set; }
    public NotificationSettings Notifications { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public List<ServiceInstance> Services { get; set; } = [];
    public List<MailAccount> MailAccounts { get; set; } = [];
    public List<string> PendingProfileDeletions { get; set; } = [];

    public static AppSettings CreateDefault() => new();
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum MemoryMode
{
    Normal,
    Economy,
    Minimal
}

public sealed class NotificationSettings
{
    public bool IsEnabled { get; set; } = true;
    public bool ShowNotificationPreview { get; set; } = true;
    public bool ShowServiceName { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    public bool DoNotDisturb { get; set; }
}

public sealed class WindowSettings
{
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 760;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool IsMaximized { get; set; }
}
