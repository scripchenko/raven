using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class DpapiMailCredentialProtector : IMailCredentialProtector
{
    private static readonly byte[] OptionalEntropy = "UnifiedMessenger.MailCredentials.v1"u8.ToArray();

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        byte[] copy = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(copy, OptionalEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
        ProtectedData.Unprotect(protectedData.ToArray(), OptionalEntropy, DataProtectionScope.CurrentUser);
}

public sealed class FileMailCredentialStore : IMailCredentialStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _credentialsFolder;
    private readonly IMailCredentialProtector _protector;

    public FileMailCredentialStore(IAppPaths appPaths, IMailCredentialProtector protector)
        : this(appPaths?.MailCredentialsFolder ?? throw new ArgumentNullException(nameof(appPaths)), protector)
    {
    }

    public FileMailCredentialStore(string credentialsFolder, IMailCredentialProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsFolder);
        _credentialsFolder = Path.GetFullPath(credentialsFolder);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public async Task SaveAsync(
        string credentialKey,
        MailCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!credential.IsValid())
        {
            throw new ArgumentException("The mail credential payload is invalid.", nameof(credential));
        }

        string targetPath = GetCredentialPath(credentialKey);
        Directory.CreateDirectory(_credentialsFolder);
        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, SerializerOptions);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = _protector.Protect(plaintext);
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<MailCredential?> LoadAsync(
        string credentialKey,
        CancellationToken cancellationToken = default)
    {
        string path = GetCredentialPath(credentialKey);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(protectedBytes);
            return DeserializeCredential(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static MailCredential? DeserializeCredential(byte[] plaintext)
    {
        try
        {
            MailCredential? credential = JsonSerializer.Deserialize<MailCredential>(plaintext, SerializerOptions);
            return credential?.IsValid() == true ? credential : null;
        }
        catch (JsonException)
        {
            // Stage 7 stored password credentials as protected UTF-8 text. Reading that format keeps
            // existing IMAP accounts compatible while all new writes use the typed envelope.
            string legacyPassword = Encoding.UTF8.GetString(plaintext);
            return string.IsNullOrWhiteSpace(legacyPassword)
                ? null
                : MailCredential.CreatePassword(legacyPassword);
        }
    }

    public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetCredentialPath(credentialKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetCredentialPath(string credentialKey)
    {
        if (!Guid.TryParseExact(credentialKey, "N", out _))
        {
            throw new ArgumentException("Credential keys must be GUIDs in N format.", nameof(credentialKey));
        }

        string path = Path.GetFullPath(Path.Combine(_credentialsFolder, credentialKey + ".credential.bin"));
        string folderPrefix = _credentialsFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Credential path escaped its storage folder.");
        }

        return path;
    }
}
