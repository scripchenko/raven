using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace UnifiedMessenger.App.Services.Localization;

public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly Lazy<Localizer> Shared = new(() => new Localizer());
    private readonly IReadOnlyDictionary<string, string> _english;
    private readonly IReadOnlyDictionary<string, string> _russian;
    private readonly IReadOnlyDictionary<string, string> _englishKeyByRussianText;
    private string _language;

    private Localizer()
    {
        _english = Load("en");
        _russian = Load("ru");
        _englishKeyByRussianText = _russian
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
        _language = AppLanguage.English;
    }

    public static Localizer Instance => Shared.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language => _language;
    public string this[string key] => Get(key);

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_language == AppLanguage.Russian && _russian.TryGetValue(key, out string? translation))
        {
            return translation;
        }

        return _english.TryGetValue(key, out string? english) ? english : key;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(_language), Get(key), arguments);

    // Used at presentation boundaries for legacy structured status/result text.
    // Provider identity, user content, and message bodies must never pass through this method.
    public string? TranslateKnown(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (_english.ContainsKey(text))
        {
            return Get(text);
        }

        return _englishKeyByRussianText.TryGetValue(text, out string? key) ? Get(key) : text;
    }

    public string? TranslateError(string? text)
    {
        string? translated = TranslateKnown(text);
        return _language == AppLanguage.English
            && translated is not null
            && translated.Any(character => character is >= '\u0400' and <= '\u04FF')
            ? Get("Unable to complete this action. Check your connection and try again.")
            : translated;
    }

    public void SetLanguage(string? language)
    {
        string normalized = AppLanguage.Normalize(language);
        if (_language == normalized)
        {
            return;
        }

        _language = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public static IReadOnlyDictionary<string, string> ReadResources(string language) => Load(language);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        string name = $"UnifiedMessenger.App.Resources.Strings.{language}.json";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing localization resource: {name}");
        using JsonDocument document = JsonDocument.Parse(stream);
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!entries.TryAdd(property.Name, property.Value.GetString() ?? string.Empty))
            {
                throw new InvalidOperationException($"Duplicate localization key: {property.Name}");
            }
        }

        return entries;
    }
}
