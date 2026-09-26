using System.IO;
using System.Globalization;
using System.Text;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public static class MailAttachmentLimits
{
    public const long GmailMaximumRawMessageBytes = 35L * 1024 * 1024;
    public const long YandexMaximumAttachmentBytes = 25L * 1024 * 1024;
    public const long MailRuMaximumSingleAttachmentBytes = 25L * 1024 * 1024;
    public const long ClientMaximumSingleAttachmentBytes = 64L * 1024 * 1024;
    public const long ClientMaximumAggregateAttachmentBytes = 64L * 1024 * 1024;
    public const int SourceCacheCapacity = 2;
    public const long SourceCacheMaximumBytes = 35L * 1024 * 1024;

    public static void ValidateMetadata(
        MailProviderType provider,
        IReadOnlyList<OutgoingMailAttachment> attachments)
    {
        long aggregate = 0;
        foreach (OutgoingMailAttachment attachment in attachments)
        {
            if (attachment.Size < 0 || attachment.Size > ClientMaximumSingleAttachmentBytes)
            {
                throw MailAttachmentException.MessageTooLarge();
            }

            aggregate = checked(aggregate + attachment.Size);
            if (aggregate > ClientMaximumAggregateAttachmentBytes)
            {
                throw MailAttachmentException.MessageTooLarge();
            }

            if (provider is MailProviderType.MailRu
                && attachment.Size > MailRuMaximumSingleAttachmentBytes)
            {
                throw MailAttachmentException.MessageTooLarge();
            }
        }

        if (provider is MailProviderType.Yandex && aggregate > YandexMaximumAttachmentBytes)
        {
            throw MailAttachmentException.MessageTooLarge();
        }
    }

    public static void ValidateGmailRawMessageSize(long rawMessageBytes)
    {
        if (rawMessageBytes > GmailMaximumRawMessageBytes)
        {
            throw MailAttachmentException.MessageTooLarge();
        }
    }
}

public static class MailAttachmentFileName
{
    private const string FallbackName = "attachment.bin";
    private static readonly char[] InvalidCharacters = Path.GetInvalidFileNameChars()
        .Concat(['/', '\\', ':'])
        .Distinct()
        .ToArray();
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Sanitize(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        int separator = Math.Max(candidate.LastIndexOf('/'), candidate.LastIndexOf('\\'));
        if (separator >= 0)
        {
            candidate = candidate[(separator + 1)..];
        }

        StringBuilder safe = new(candidate.Length);
        foreach (char character in candidate)
        {
            safe.Append(character == '\0' || char.IsControl(character) || InvalidCharacters.Contains(character)
                ? '_'
                : character);
        }

        candidate = safe.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "..")
        {
            candidate = FallbackName;
        }

        string stem = Path.GetFileNameWithoutExtension(candidate);
        if (ReservedNames.Contains(stem))
        {
            candidate = "_" + candidate;
        }

        return candidate.Length <= 180 ? candidate : candidate[..180].TrimEnd('.', ' ');
    }
}

public enum MailAttachmentFailureKind
{
    Unavailable,
    ProviderFailure,
    LocalReadFailure,
    LocalWriteFailure,
    MessageTooLarge
}

public sealed class MailAttachmentException(
    MailAttachmentFailureKind failureKind,
    string userMessage,
    Exception? innerException = null) : Exception(userMessage, innerException)
{
    public MailAttachmentFailureKind FailureKind { get; } = failureKind;
    public string UserMessage { get; } = userMessage;

    public static MailAttachmentException MessageTooLarge() =>
        new(MailAttachmentFailureKind.MessageTooLarge, L.Instance.Get("Message exceeds the allowed size."));
}

public sealed record MailAttachmentContent(
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Bytes);

internal sealed class MailSizeLimitedMemoryStream(long maximumBytes) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        Validate(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Validate(buffer.Length);
        base.Write(buffer);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Validate(buffer.Length);
        await base.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        Validate(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override void WriteByte(byte value)
    {
        Validate(1);
        base.WriteByte(value);
    }

    private void Validate(int count)
    {
        if (Length + count > maximumBytes)
        {
            throw MailAttachmentException.MessageTooLarge();
        }
    }
}

internal static class MailMimeAttachmentCatalog
{
    private const string KeyPrefix = "mime:";

    public static IReadOnlyList<MailAttachmentInfo> Extract(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        List<(string Key, MimeEntity Entity)> parts = [];
        Walk(message.Body, "0", parts);
        string html = message.HtmlBody ?? string.Empty;
        return parts
            .Select(item => ToInfo(item.Key, item.Entity, html))
            .Where(info => info is not null)
            .Select(info => info!)
            .ToArray();
    }

    public static MailAttachmentContent GetContent(
        MimeMessage message,
        string attachmentKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(attachmentKey))
        {
            throw Unavailable();
        }

        List<(string Key, MimeEntity Entity)> parts = [];
        Walk(message.Body, "0", parts);
        (string Key, MimeEntity Entity) match = parts.FirstOrDefault(item =>
            string.Equals(item.Key, attachmentKey, StringComparison.Ordinal));
        MailAttachmentInfo? info = match.Entity is null
            ? null
            : ToInfo(match.Key, match.Entity, message.HtmlBody ?? string.Empty);
        if (info is not { IsDownloadable: true })
        {
            throw Unavailable();
        }

        using MailSizeLimitedMemoryStream output = new(MailAttachmentLimits.ClientMaximumSingleAttachmentBytes);
        try
        {
            switch (match.Entity)
            {
                case MimePart part when part.Content is not null:
                    part.Content.DecodeTo(output, cancellationToken);
                    break;
                case MessagePart messagePart when messagePart.Message is not null:
                    messagePart.Message.WriteTo(output, cancellationToken);
                    break;
                default:
                    throw Unavailable();
            }
        }
        catch (MailAttachmentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or FormatException or InvalidOperationException)
        {
            throw new MailAttachmentException(
                MailAttachmentFailureKind.Unavailable,
                L.Instance.Get("Attachment no longer available."),
                exception);
        }

        return new MailAttachmentContent(info.FileName, info.ContentType, output.ToArray());
    }

    public static long EstimateEncodedSize(MimeMessage message)
    {
        using CountingStream output = new(MailAttachmentLimits.SourceCacheMaximumBytes);
        try
        {
            message.WriteTo(output);
            return output.Length;
        }
        catch (MailAttachmentException)
        {
            return MailAttachmentLimits.SourceCacheMaximumBytes + 1;
        }
    }

    private static void Walk(MimeEntity? entity, string path, List<(string Key, MimeEntity Entity)> output)
    {
        if (entity is null)
        {
            return;
        }

        output.Add((KeyPrefix + path, entity));
        if (entity is Multipart multipart)
        {
            for (int index = 0; index < multipart.Count; index++)
            {
                Walk(multipart[index], $"{path}.{index.ToString(CultureInfo.InvariantCulture)}", output);
            }
        }
    }

    private static MailAttachmentInfo? ToInfo(string key, MimeEntity entity, string html)
    {
        bool explicitAttachment = entity.ContentDisposition?.IsAttachment == true;
        string rawFileName = entity.ContentDisposition?.FileName
            ?? entity.ContentType.Name
            ?? string.Empty;
        bool cidUsed = !string.IsNullOrWhiteSpace(entity.ContentId)
            && html.Contains("cid:" + entity.ContentId.Trim('<', '>'), StringComparison.OrdinalIgnoreCase);
        bool inline = string.Equals(
                entity.ContentDisposition?.Disposition,
                ContentDisposition.Inline,
                StringComparison.OrdinalIgnoreCase)
            || cidUsed;
        bool technicalBody = entity is TextPart && !explicitAttachment;
        bool downloadable = explicitAttachment
            || !inline && !technicalBody && !string.IsNullOrWhiteSpace(rawFileName)
            || entity is MessagePart && explicitAttachment;
        if (!downloadable || inline)
        {
            return null;
        }

        long size = GetDecodedSize(entity);
        string contentType = entity.ContentType.MimeType;
        string fallback = entity is MessagePart ? "attachment.eml" : "attachment.bin";
        string fileName = MailAttachmentFileName.Sanitize(
            string.IsNullOrWhiteSpace(rawFileName) ? fallback : rawFileName);
        return new MailAttachmentInfo(key, fileName, contentType, size, inline, true);
    }

    private static long GetDecodedSize(MimeEntity entity)
    {
        if (entity.ContentDisposition?.Size is long declaredSize && declaredSize >= 0)
        {
            return declaredSize;
        }

        using CountingStream output = new(MailAttachmentLimits.ClientMaximumSingleAttachmentBytes);
        try
        {
            switch (entity)
            {
                case MimePart part when part.Content is not null:
                    part.Content.DecodeTo(output);
                    return output.Length;
                case MessagePart messagePart when messagePart.Message is not null:
                    messagePart.Message.WriteTo(output);
                    return output.Length;
                default:
                    return 0;
            }
        }
        catch (MailAttachmentException)
        {
            Stream? source = (entity as MimePart)?.Content?.Stream;
            return source?.CanSeek == true
                ? source.Length
                : MailAttachmentLimits.ClientMaximumSingleAttachmentBytes + 1;
        }
    }

    private static MailAttachmentException Unavailable() =>
        new(MailAttachmentFailureKind.Unavailable, L.Instance.Get("Attachment no longer available."));

    private sealed class CountingStream(long maximumBytes) : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Add(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);
        private void Add(int count)
        {
            _length = checked(_length + count);
            if (_length > maximumBytes)
            {
                throw MailAttachmentException.MessageTooLarge();
            }
        }
    }

}

public sealed class MailMessageSourceCache
{
    private readonly object _sync = new();
    private readonly Dictionary<SourceKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _lru = [];
    private long _totalBytes;

    internal int Count
    {
        get { lock (_sync) { return _entries.Count; } }
    }

    public void Set(Guid accountId, string messageKey, MimeMessage message, long encodedSize)
    {
        if (encodedSize < 0 || encodedSize > MailAttachmentLimits.SourceCacheMaximumBytes)
        {
            return;
        }

        SourceKey key = new(accountId, messageKey);
        lock (_sync)
        {
            RemoveCore(key);
            LinkedListNode<CacheEntry> node = _lru.AddFirst(new CacheEntry(key, message, encodedSize));
            _entries[key] = node;
            _totalBytes += encodedSize;
            while (_entries.Count > MailAttachmentLimits.SourceCacheCapacity
                || _totalBytes > MailAttachmentLimits.SourceCacheMaximumBytes)
            {
                LinkedListNode<CacheEntry>? last = _lru.Last;
                if (last is null)
                {
                    break;
                }

                RemoveCore(last.Value.Key);
            }
        }
    }

    public bool TryGet(Guid accountId, string messageKey, out MimeMessage? message)
    {
        SourceKey key = new(accountId, messageKey);
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                message = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            message = node.Value.Message;
            return true;
        }
    }

    public void RemoveAccount(Guid accountId)
    {
        lock (_sync)
        {
            foreach (SourceKey key in _entries.Keys.Where(key => key.AccountId == accountId).ToArray())
            {
                RemoveCore(key);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
            _totalBytes = 0;
        }
    }

    private void RemoveCore(SourceKey key)
    {
        if (_entries.Remove(key, out LinkedListNode<CacheEntry>? node))
        {
            _lru.Remove(node);
            _totalBytes -= node.Value.EncodedSize;
        }
    }

    private readonly record struct SourceKey(Guid AccountId, string MessageKey);
    private sealed record CacheEntry(SourceKey Key, MimeMessage Message, long EncodedSize);
}

public interface IMailAttachmentContentProvider
{
    bool Supports(MailProviderType providerType);

    Task<MailAttachmentContent> GetAsync(
        MailAccount account,
        string messageKey,
        string attachmentKey,
        CancellationToken cancellationToken = default);
}

public interface IMailAttachmentContentProviderFactory
{
    IMailAttachmentContentProvider Get(MailProviderType providerType);
}

public sealed class MailAttachmentContentProviderFactory(
    IEnumerable<IMailAttachmentContentProvider> providers) : IMailAttachmentContentProviderFactory
{
    private readonly IReadOnlyList<IMailAttachmentContentProvider> _providers = providers.ToArray();

    public IMailAttachmentContentProvider Get(MailProviderType providerType)
    {
        IMailAttachmentContentProvider[] matches = _providers.Where(provider => provider.Supports(providerType)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new KeyNotFoundException("Attachment content provider is not registered uniquely.");
    }
}

internal abstract record MailAttachmentSource;
internal sealed record LocalFileMailAttachmentSource(
    string Path,
    long OriginalSize,
    DateTime OriginalLastWriteTimeUtc) : MailAttachmentSource;
internal sealed record SourceMessageMailAttachmentSource(
    Guid AccountId,
    string MessageKey,
    string AttachmentKey) : MailAttachmentSource;
internal sealed record MemoryMailAttachmentSource(
    ReadOnlyMemory<byte> Bytes) : MailAttachmentSource;

public sealed record OutgoingMailAttachment(
    string AttachmentId,
    string FileName,
    string ContentType,
    long Size)
{
    internal MailAttachmentSource? Source { get; init; }

    internal static OutgoingMailAttachment FromLocalFile(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                throw new MailAttachmentException(
                    MailAttachmentFailureKind.LocalReadFailure,
                    L.Instance.Get("Could not read the attachment."));
            }

            string fileName = MailAttachmentFileName.Sanitize(file.Name);
            return new OutgoingMailAttachment(
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                fileName,
                MailAttachmentContentType.Resolve(fileName),
                file.Length)
            {
                Source = new LocalFileMailAttachmentSource(
                    file.FullName,
                    file.Length,
                    file.LastWriteTimeUtc)
            };
        }
        catch (MailAttachmentException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            throw new MailAttachmentException(
                MailAttachmentFailureKind.LocalReadFailure,
                L.Instance.Get("Could not read the attachment."),
                exception);
        }
    }

    internal static OutgoingMailAttachment FromSource(
        Guid accountId,
        string messageKey,
        MailAttachmentInfo attachment) =>
        new(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            attachment.FileName,
            attachment.ContentType,
            attachment.Size)
        {
            Source = new SourceMessageMailAttachmentSource(
                accountId,
                messageKey,
                attachment.AttachmentKey)
        };

    internal static OutgoingMailAttachment FromMemory(MailAttachmentContent content) =>
        new(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            MailAttachmentFileName.Sanitize(content.FileName),
            MailAttachmentContentType.Normalize(content.ContentType),
            content.Bytes.Length)
        {
            Source = new MemoryMailAttachmentSource(content.Bytes.ToArray())
        };
}

public sealed record MaterializedMailAttachment(
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Bytes);

public interface IMailOutgoingAttachmentMaterializer
{
    Task<IReadOnlyList<MaterializedMailAttachment>> MaterializeAsync(
        MailAccount account,
        IReadOnlyList<OutgoingMailAttachment> attachments,
        CancellationToken cancellationToken = default);
}

public sealed class MailOutgoingAttachmentMaterializer(
    IMailAttachmentContentProviderFactory providerFactory) : IMailOutgoingAttachmentMaterializer
{
    public async Task<IReadOnlyList<MaterializedMailAttachment>> MaterializeAsync(
        MailAccount account,
        IReadOnlyList<OutgoingMailAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        MailAttachmentLimits.ValidateMetadata(account.Provider, attachments);
        List<MaterializedMailAttachment> result = new(attachments.Count);
        long aggregate = 0;
        foreach (OutgoingMailAttachment attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MailAttachmentContent content = attachment.Source switch
            {
                LocalFileMailAttachmentSource local => await ReadLocalAsync(attachment, local, cancellationToken),
                SourceMessageMailAttachmentSource source => await ReadSourceAsync(account, attachment, source, cancellationToken),
                MemoryMailAttachmentSource memory => new MailAttachmentContent(
                    attachment.FileName,
                    attachment.ContentType,
                    memory.Bytes),
                _ => throw new MailAttachmentException(
                    MailAttachmentFailureKind.Unavailable,
                    L.Instance.Get("Attachment no longer available."))
            };
            aggregate = checked(aggregate + content.Bytes.Length);
            if (aggregate > MailAttachmentLimits.ClientMaximumAggregateAttachmentBytes)
            {
                throw MailAttachmentException.MessageTooLarge();
            }

            result.Add(new MaterializedMailAttachment(
                MailAttachmentFileName.Sanitize(content.FileName),
                MailAttachmentContentType.Normalize(content.ContentType),
                content.Bytes));
        }

        return result;
    }

    private async Task<MailAttachmentContent> ReadSourceAsync(
        MailAccount account,
        OutgoingMailAttachment attachment,
        SourceMessageMailAttachmentSource source,
        CancellationToken cancellationToken)
    {
        if (source.AccountId != account.Id)
        {
            throw new MailAttachmentException(
                MailAttachmentFailureKind.Unavailable,
                L.Instance.Get("Attachment no longer available."));
        }

        try
        {
            MailAttachmentContent content = await providerFactory.Get(account.Provider).GetAsync(
                account,
                source.MessageKey,
                source.AttachmentKey,
                cancellationToken);
            return content;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailAttachmentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or KeyNotFoundException or MailReadException)
        {
            throw new MailAttachmentException(
                MailAttachmentFailureKind.ProviderFailure,
                L.Instance.Get("Could not load the attachment."),
                exception);
        }
    }

    private static async Task<MailAttachmentContent> ReadLocalAsync(
        OutgoingMailAttachment attachment,
        LocalFileMailAttachmentSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            FileInfo file = new(source.Path);
            if (!file.Exists
                || file.Length != source.OriginalSize
                || file.LastWriteTimeUtc != source.OriginalLastWriteTimeUtc
                || file.Length > MailAttachmentLimits.ClientMaximumSingleAttachmentBytes)
            {
                throw new IOException();
            }

            byte[] bytes = new byte[file.Length];
            await using FileStream stream = new(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return new MailAttachmentContent(attachment.FileName, attachment.ContentType, bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            throw new MailAttachmentException(
                MailAttachmentFailureKind.LocalReadFailure,
                L.Instance.Format("Could not read attachment “{0}”.", attachment.FileName),
                exception);
        }
    }
}

internal static class MailAttachmentContentType
{
    public static string Resolve(string fileName) => Normalize(MimeTypes.GetMimeType(fileName));

    public static string Normalize(string? value)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value)
                ? "application/octet-stream"
                : ContentType.Parse(value).MimeType;
        }
        catch (ParseException)
        {
            return "application/octet-stream";
        }
    }
}

public interface IMailAttachmentDialogService
{
    IReadOnlyList<OutgoingMailAttachment> SelectOutgoingAttachments();
    string? SelectSaveDestination(MailAttachmentInfo attachment);
}

public enum MailAttachmentSaveOutcome
{
    Canceled,
    Saved,
    Failed
}

public sealed record MailAttachmentSaveResult(
    MailAttachmentSaveOutcome Outcome,
    string? UserMessage = null);

public interface IMailAttachmentSaveService
{
    Task<MailAttachmentSaveResult> SaveAsync(
        MailAccount account,
        string messageKey,
        MailAttachmentInfo attachment,
        CancellationToken cancellationToken = default);
}

public sealed class MailAttachmentSaveService(
    IMailAttachmentDialogService dialogService,
    IMailAttachmentContentProviderFactory providerFactory) : IMailAttachmentSaveService
{
    public async Task<MailAttachmentSaveResult> SaveAsync(
        MailAccount account,
        string messageKey,
        MailAttachmentInfo attachment,
        CancellationToken cancellationToken = default)
    {
        string? destination = dialogService.SelectSaveDestination(attachment);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return new MailAttachmentSaveResult(MailAttachmentSaveOutcome.Canceled);
        }

        string temporary = destination + ".um-part-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            MailAttachmentContent content = await providerFactory.Get(account.Provider).GetAsync(
                account,
                messageKey,
                attachment.AttachmentKey,
                cancellationToken);
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content.Bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
            return new MailAttachmentSaveResult(MailAttachmentSaveOutcome.Saved, L.Instance.Get("File saved"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeletePartial(temporary);
            return new MailAttachmentSaveResult(MailAttachmentSaveOutcome.Canceled);
        }
        catch (MailAttachmentException exception)
        {
            DeletePartial(temporary);
            return new MailAttachmentSaveResult(MailAttachmentSaveOutcome.Failed, exception.UserMessage);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            DeletePartial(temporary);
            return new MailAttachmentSaveResult(
                MailAttachmentSaveOutcome.Failed,
                L.Instance.Get("Could not save the file to the selected location."));
        }
    }

    private static void DeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // A failed save remains a failure; cleanup errors are intentionally not logged.
        }
    }
}
