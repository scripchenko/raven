namespace UnifiedMessenger.App.Services.Localization;

public static class AppLanguage
{
    public const string English = "en";
    public const string Russian = "ru";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return English;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            English => English,
            Russian => Russian,
            _ => English
        };
    }
}
