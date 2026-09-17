using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Mail;

public enum ManagedImapDraftRecoveryAttachmentKind
{
    LocalFile,
    SourceMessage,
    Memory
}

public sealed record ManagedImapDraftRecoveryAttachment(
    string AttachmentId,
    string FileName,
    string ContentType,
    long Size,
    ManagedImapDraftRecoveryAttachmentKind Kind,
    string? LocalPath = null,
    long? OriginalSize = null,
    DateTime? OriginalLastWriteTimeUtc = null,
    Guid? SourceAccountId = null,
    string? SourceMessageKey = null,
    string? SourceAttachmentKey = null,
    byte[]? Bytes = null)
{
    internal static ManagedImapDraftRecoveryAttachment Capture(OutgoingMailAttachment attachment) =>
        attachment.Source switch
        {
            LocalFileMailAttachmentSource local => new(
                attachment.AttachmentId,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                ManagedImapDraftRecoveryAttachmentKind.LocalFile,
                LocalPath: local.Path,
                OriginalSize: local.OriginalSize,
                OriginalLastWriteTimeUtc: local.OriginalLastWriteTimeUtc),
            SourceMessageMailAttachmentSource source => new(
                attachment.AttachmentId,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                ManagedImapDraftRecoveryAttachmentKind.SourceMessage,
                SourceAccountId: source.AccountId,
                SourceMessageKey: source.MessageKey,
                SourceAttachmentKey: source.AttachmentKey),
            MemoryMailAttachmentSource memory => new(
                attachment.AttachmentId,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                ManagedImapDraftRecoveryAttachmentKind.Memory,
                Bytes: memory.Bytes.ToArray()),
            _ => throw new InvalidOperationException("Unsupported draft attachment source.")
        };

    internal OutgoingMailAttachment Restore(Guid accountId)
    {
        MailAttachmentSource source = Kind switch
        {
            ManagedImapDraftRecoveryAttachmentKind.LocalFile
                when !string.IsNullOrWhiteSpace(LocalPath)
                    && OriginalSize is long originalSize
                    && OriginalLastWriteTimeUtc is DateTime originalWriteTime =>
                new LocalFileMailAttachmentSource(LocalPath, originalSize, originalWriteTime),
            ManagedImapDraftRecoveryAttachmentKind.SourceMessage
                when SourceAccountId == accountId
                    && !string.IsNullOrWhiteSpace(SourceMessageKey)
                    && !string.IsNullOrWhiteSpace(SourceAttachmentKey) =>
                new SourceMessageMailAttachmentSource(
                    accountId,
                    SourceMessageKey,
                    SourceAttachmentKey),
            ManagedImapDraftRecoveryAttachmentKind.Memory when Bytes is not null =>
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

public sealed record ManagedImapDraftRecoverySnapshot(
    Guid AccountId,
    DateTimeOffset CapturedAtUtc,
    ManagedImapDraftIdentity? Identity,
    string? LogicalId,
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string TextBody,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyList<ManagedImapDraftRecoveryAttachment> Attachments)
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

public interface IManagedImapDraftRecoveryStore
{
    IReadOnlyList<ManagedImapDraftRecoverySnapshot> Load();
    bool Upsert(IReadOnlyCollection<ManagedImapDraftRecoverySnapshot> snapshots);
    bool Remove(Guid accountId);
}

internal sealed class FileManagedImapDraftRecoveryStore : IManagedImapDraftRecoveryStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IMailCredentialProtector _protector;

    public FileManagedImapDraftRecoveryStore(IAppPaths paths, IMailCredentialProtector protector)
    {
        _path = Path.GetFullPath(paths.ManagedImapDraftRecoveryFilePath);
        _protector = protector;
    }

    public IReadOnlyList<ManagedImapDraftRecoverySnapshot> Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public bool Upsert(IReadOnlyCollection<ManagedImapDraftRecoverySnapshot> snapshots)
    {
        lock (_gate)
        {
            try
            {
                Dictionary<Guid, ManagedImapDraftRecoverySnapshot> merged = LoadCore()
                    .ToDictionary(item => item.AccountId);
                foreach (ManagedImapDraftRecoverySnapshot snapshot in snapshots)
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
                ManagedImapDraftRecoverySnapshot[] remaining = LoadCore()
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

    private IReadOnlyList<ManagedImapDraftRecoverySnapshot> LoadCore()
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

    private bool SaveCore(IReadOnlyCollection<ManagedImapDraftRecoverySnapshot> snapshots)
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
        IReadOnlyList<ManagedImapDraftRecoverySnapshot> Drafts);
}
