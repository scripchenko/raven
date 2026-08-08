namespace UnifiedMessenger.App.ViewModels;

public enum SettingsSection
{
    General,
    Notifications,
    Accounts,
    About
}

public sealed record SettingsSectionItem(SettingsSection Section, string Title, string Glyph);
