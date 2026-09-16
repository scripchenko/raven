using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public enum YandexDraftRecoveryAttachmentKind
{
    LocalFile,
    SourceMessage,
    Memory
}

public sealed record YandexDraftRecoveryAttachment(
    string AttachmentId,
    string FileName,
    string ContentType,
    long Size,
    YandexDraftRecoveryAttachmentKind Kind,
    string? LocalPath = null,
    long? OriginalSize = null,
    DateTime? OriginalLastWriteTimeUtc = null,
    Guid? SourceAccountId = null,
    string? SourceMessageKey = null,
    string? SourceAttachmentKey = null,
    byte[]? Bytes = null)
{
    internal static YandexDraftRecoveryAttachment Capture(OutgoingMailAttachment attachment) =>
        attachment.Source switch
        {
            LocalFileMailAttachmentSource local => new(
                attachment.AttachmentId,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                YandexDraftRecoveryAttachmentKind.LocalFile,
                LocalPath: local.Path,
                OriginalSize: local.OriginalSize,
                OriginalLastWriteTimeUtc: local.OriginalLastWriteTimeUtc),
            SourceMessageMailAttachmentSource source => new(
                attachment.AttachmentId,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                YandexDraftRecoveryAttachmentKind.SourceMessage,
                SourceAccountId: source.AccountId,
                SourceMessageKey: source.MessageKey,
                SourceAttachmentKey: source.AttachmentKey),
            MemoryMailAttachmentSource memory => new(
                attachment.AttachmentId,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                YandexDraftRecoveryAttachmentKind.Memory,
                Bytes: memory.Bytes.ToArray()),
            _ => throw new InvalidOperationException("Unsupported draft attachment source.")
        };

    internal OutgoingMailAttachment Restore(Guid accountId)
    {
        MailAttachmentSource source = Kind switch
        {
            YandexDraftRecoveryAttachmentKind.LocalFile
                when !string.IsNullOrWhiteSpace(LocalPath)
                    && OriginalSize is long originalSize
                    && OriginalLastWriteTimeUtc is DateTime originalWriteTime =>
                new LocalFileMailAttachmentSource(LocalPath, originalSize, originalWriteTime),
            YandexDraftRecoveryAttachmentKind.SourceMessage
                when SourceAccountId == accountId
                    && !string.IsNullOrWhiteSpace(SourceMessageKey)
                    && !string.IsNullOrWhiteSpace(SourceAttachmentKey) =>
                new SourceMessageMailAttachmentSource(
                    accountId,
                    SourceMessageKey,
                    SourceAttachmentKey),
            YandexDraftRecoveryAttachmentKind.Memory when Bytes is not null =>
                new MemoryMailAttachmentSource(Bytes.ToArray()),
            _ => throw new InvalidDataException("Invalid recovered draft attachment.")
        };
        return new OutgoingMailAttachment(
            AttachmentId,
            MailAttachmentFileName.Sanitize(FileName),
            MailAttachmentContentType.Normalize(ContentType),
            Size)
        {
            Source = source
        };
    }
}

public sealed record YandexDraftRecoverySnapshot(
    Guid AccountId,
    DateTimeOffset CapturedAtUtc,
    YandexDraftIdentity? Identity,
    string? LogicalId,
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string TextBody,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyList<YandexDraftRecoveryAttachment> Attachments)
{
    internal MailComposeTemplate RestoreTemplate() => new(
        To,
        Cc,
        Bcc,
        Subject,
        TextBody,
        string.IsNullOrWhiteSpace(InReplyTo) && References.Count == 0
            ? null
            : new MailReplyContext(InReplyTo, References))
    {
        ExistingAttachments = Attachments.Select(item => item.Restore(AccountId)).ToArray()
    };
}

public interface IYandexDraftRecoveryStore
{
    IReadOnlyList<YandexDraftRecoverySnapshot> Load();
    bool Upsert(IReadOnlyCollection<YandexDraftRecoverySnapshot> snapshots);
    bool Remove(Guid accountId);
}

internal sealed class FileYandexDraftRecoveryStore : IYandexDraftRecoveryStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IMailCredentialProtector _protector;

    public FileYandexDraftRecoveryStore(IAppPaths paths, IMailCredentialProtector protector)
    {
        _path = Path.GetFullPath(paths.YandexDraftRecoveryFilePath);
        _protector = protector;
    }

    public IReadOnlyList<YandexDraftRecoverySnapshot> Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public bool Upsert(IReadOnlyCollection<YandexDraftRecoverySnapshot> snapshots)
    {
        lock (_gate)
        {
            try
            {
                Dictionary<Guid, YandexDraftRecoverySnapshot> merged = LoadCore()
                    .ToDictionary(item => item.AccountId);
                foreach (YandexDraftRecoverySnapshot snapshot in snapshots)
                {
                    merged[snapshot.AccountId] = snapshot;
                }
                return SaveCore(merged.Values.ToArray());
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or CryptographicException
                or JsonException
                or InvalidDataException)
            {
                return false;
            }
        }
    }

    public bool Remove(Guid accountId)
    {
        lock (_gate)
        {
            try
            {
                YandexDraftRecoverySnapshot[] remaining = LoadCore()
                    .Where(item => item.AccountId != accountId)
                    .ToArray();
                return SaveCore(remaining);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or CryptographicException
                or JsonException
                or InvalidDataException)
            {
                return false;
            }
        }
    }

    private IReadOnlyList<YandexDraftRecoverySnapshot> LoadCore()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        byte[] protectedBytes = File.ReadAllBytes(_path);
        byte[]? plaintext = null;
        try
        {
            plaintext = _protector.Unprotect(protectedBytes);
            RecoveryEnvelope? envelope = JsonSerializer.Deserialize<RecoveryEnvelope>(plaintext, SerializerOptions);
            return envelope is { SchemaVersion: SchemaVersion }
                ? envelope.Drafts.Where(item => item.AccountId != Guid.Empty).ToArray()
                : [];
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

    private bool SaveCore(IReadOnlyCollection<YandexDraftRecoverySnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            return true;
        }

        string? folder = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }
        Directory.CreateDirectory(folder);
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new RecoveryEnvelope(SchemaVersion, snapshots.ToArray()),
            SerializerOptions);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = _protector.Protect(plaintext);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
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

    private sealed record RecoveryEnvelope(
        int SchemaVersion,
        IReadOnlyList<YandexDraftRecoverySnapshot> Drafts);
}
