using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Localization;
using UnifiedMessenger.App.Services.Persistence;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.Tests;

[CollectionDefinition("Localization state", DisableParallelization = true)]
public sealed class LocalizationStateCollection { }

[Collection("Localization state")]
public sealed class ApplicationLocalizationTests
{
    [Theory]
    [InlineData("ru-RU")]
    [InlineData("ru")]
    [InlineData("en-US")]
    [InlineData("pl-PL")]
    [InlineData("ja-JP")]
    public void FreshProfileDefaultsToEnglishRegardlessOfWindowsCulture(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            Assert.Equal("en", AppSettings.CreateDefault().Language);
            Assert.Equal("en", AppLanguage.Normalize(null));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("  ", "en")]
    [InlineData("ru-RU", "en")]
    [InlineData("ru", "ru")]
    [InlineData("en", "en")]
    [InlineData("unexpected", "en")]
    public void NormalizationPreservesValidChoiceAndDefaultsInvalidValuesToEnglish(string? value, string expected) =>
        Assert.Equal(expected, AppLanguage.Normalize(value));

    [Fact]
    public void ResourcesHaveMatchingKeysWithoutDuplicates()
    {
        IReadOnlyDictionary<string, string> english = Localizer.ReadResources("en");
        IReadOnlyDictionary<string, string> russian = Localizer.ReadResources("ru");
        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(), russian.Keys.Order());
        Assert.All(english, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
        Assert.All(russian, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
        foreach (string key in english.Keys)
        {
            string[] englishSlots = Regex.Matches(english[key], @"\{\d+(?::[^}]+)?\}")
                .Select(match => match.Value).Order().ToArray();
            string[] russianSlots = Regex.Matches(russian[key], @"\{\d+(?::[^}]+)?\}")
                .Select(match => match.Value).Order().ToArray();
            Assert.Equal(englishSlots, russianSlots);
        }
        Assert.Equal("raven — messaging and email in one place.", english["raven — messaging and email in one place."]);
        Assert.Equal("raven — мессенджеры и почта в одном окне.", russian["raven — messaging and email in one place."]);
    }

    [Fact]
    public void EveryLocalizedXamlKeyExistsInBothLanguages()
    {
        IReadOnlyDictionary<string, string> english = Localizer.ReadResources("en");
        IReadOnlyDictionary<string, string> russian = Localizer.ReadResources("ru");
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UnifiedMessenger.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string views = Path.Combine(root.FullName, "src", "UnifiedMessenger.App", "Views");
        foreach (string file in Directory.EnumerateFiles(views, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{loc:Text Key='([^']+)'\}"))
            {
                string key = match.Groups[1].Value;
                Assert.True(english.ContainsKey(key), $"Missing English key {key} in {file}");
                Assert.True(russian.ContainsKey(key), $"Missing Russian key {key} in {file}");
            }
        }
    }

    [Fact]
    public async Task MissingLanguageMigratesWithoutTouchingAccounts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"raven-localization-{Guid.NewGuid():N}.json");
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            await File.WriteAllTextAsync(path, $$"""
                {"schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "services": [], "mailAccounts": [], "closeToTray": false}
                """);
            SettingsLoadResult result = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal("en", result.Settings.Language);
            Assert.False(result.Settings.CloseToTray);
            Assert.Empty(result.Settings.Services);
            Assert.Empty(result.Settings.MailAccounts);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"ru-RU\"")]
    [InlineData("\"unexpected\"")]
    public async Task ExistingProfileWithInvalidLanguageFallsBackToEnglish(string rawValue)
    {
        string path = Path.Combine(Path.GetTempPath(), $"raven-localization-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, $$"""
                {"schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "language": {{rawValue}}, "services": [], "mailAccounts": [], "closeToTray": false}
                """);
            SettingsLoadResult result = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal("en", result.Settings.Language);
            Assert.False(result.Settings.CloseToTray);
            Assert.True(result.WasMigrated);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("ru", "ru")]
    [InlineData("invalid", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public async Task PersistedLanguageSurvivesReloadOrFallsBackSafely(string? value, string expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"raven-localization-{Guid.NewGuid():N}.json");
        try
        {
            JsonSettingsService store = new(path);
            AppSettings settings = AppSettings.CreateDefault();
            settings.Language = value!;
            await store.SaveAsync(settings);
            Assert.Equal(expected, (await store.LoadAsync()).Settings.Language);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(expected, json.RootElement.GetProperty("language").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LiveSwitchUpdatesHomeSettingsAboutTrayAndErrorResources()
    {
        Localizer localizer = Localizer.Instance;
        string previousLanguage = localizer.Language;
        int notifications = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == "Item[]") notifications++;
        };
        localizer.PropertyChanged += handler;
        try
        {
            localizer.SetLanguage("ru");
            Assert.Equal("raven — мессенджеры и почта в одном окне.", localizer.Get("raven — messaging and email in one place."));
            Assert.Equal("Настройки", localizer.Get("Settings"));
            Assert.Equal("Проверить обновления", localizer.Get("Check for updates"));
            Assert.Equal("Открыть raven", localizer.Get("Open raven"));
            Assert.Equal("Ошибка запуска", localizer.Get("Startup error"));

            localizer.SetLanguage("en");
            Assert.Equal("raven — messaging and email in one place.", localizer.Get("raven — messaging and email in one place."));
            Assert.Equal("Settings", localizer.Get("Settings"));
            Assert.Equal("Check for updates", localizer.Get("Check for updates"));
            Assert.Equal("Open raven", localizer.Get("Open raven"));
            Assert.Equal("Startup error", localizer.Get("Startup error"));
            Assert.Equal("unlisted localization key", localizer.Get("unlisted localization key"));

            localizer.SetLanguage("ru");
            Assert.Equal("Настройки", localizer.Get("Settings"));
            Assert.True(notifications >= 2);
        }
        finally
        {
            localizer.PropertyChanged -= handler;
            localizer.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void CompiledWelcomeViewUpdatesBoundTextWithoutRecreation()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            Localizer localizer = Localizer.Instance;
            string previousLanguage = localizer.Language;
            try
            {
                localizer.SetLanguage("en");
                WelcomeView view = new();
                TextBlock heading = Descendants(view).OfType<TextBlock>().Single(block =>
                    block.Text.Contains("messaging and email", StringComparison.Ordinal));
                Assert.Equal("raven — messaging and email in one place.", heading.Text);

                localizer.SetLanguage("ru");
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.Equal("raven — мессенджеры и почта в одном окне.", heading.Text);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                localizer.SetLanguage(previousLanguage);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);

        static IEnumerable<object> Descendants(DependencyObject parent)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                yield return child;
                if (child is DependencyObject nested)
                {
                    foreach (object descendant in Descendants(nested))
                    {
                        yield return descendant;
                    }
                }
            }
        }
    }
}
