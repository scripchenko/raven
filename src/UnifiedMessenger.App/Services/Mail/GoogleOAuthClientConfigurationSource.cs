using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class GoogleOAuthClientConfigurationSource(IAppPaths appPaths)
    : IGoogleOAuthClientConfigurationSource
{
    public string ConfigurationPath { get; } =
        appPaths?.GoogleOAuthClientConfigurationPath
        ?? throw new ArgumentNullException(nameof(appPaths));

    public async Task<GoogleOAuthClientConfiguration?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ConfigurationPath))
        {
            return null;
        }

        await using FileStream stream = new(
            ConfigurationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        GoogleClientSecretsDocument? document = await JsonSerializer.DeserializeAsync<GoogleClientSecretsDocument>(
            stream,
            cancellationToken: cancellationToken);
        GoogleInstalledClient? installed = document?.Installed;
        if (installed is null
            || string.IsNullOrWhiteSpace(installed.ClientId)
            || string.IsNullOrWhiteSpace(installed.ClientSecret))
        {
            throw new InvalidDataException("The Google OAuth Desktop client configuration is invalid.");
        }

        return new GoogleOAuthClientConfiguration(
            installed.ClientId.Trim(),
            installed.ClientSecret.Trim());
    }

    private sealed class GoogleClientSecretsDocument
    {
        [JsonPropertyName("installed")]
        public GoogleInstalledClient? Installed { get; init; }
    }

    private sealed class GoogleInstalledClient
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; init; }

        [JsonPropertyName("client_secret")]
        public string? ClientSecret { get; init; }
    }
}
