using System.ComponentModel;

namespace UnifiedMessenger.App.Models;

public sealed record MailMessageSummary(
    string MessageKey,
    string Subject,
    string FromDisplayName,
    string FromAddress,
    DateTimeOffset ReceivedAt,
    string Preview,
    bool IsUnread) : INotifyPropertyChanged
{
    private static readonly string[] SenderAvatarBackgroundPalette =
    [
        "#FFD7E9FF",
        "#FFE5DDF8",
        "#FFD7EFE5",
        "#FFFFE2C4",
        "#FFF7DCE6",
        "#FFD8ECEE"
    ];

    private bool _isSelected;

    public MailMessageAttachmentSummary AttachmentSummary { get; init; } = MailMessageAttachmentSummary.Empty;
    public bool IsStarred { get; init; }
    internal IReadOnlySet<string> ProviderLabelIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    internal string? ProviderDraftId { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasAttachments => AttachmentSummary.Count > 0;
    public string StarActionText => IsStarred ? "Снять пометку" : "Пометить";
    public int AttachmentCount => AttachmentSummary.Count;
    public IReadOnlyList<MailAttachmentPreviewItem> AttachmentPreviewItems => AttachmentSummary.PreviewItems;
    public bool HasMoreAttachments => AttachmentSummary.RemainingCount > 0;
    public string RemainingAttachmentText => $"+{AttachmentSummary.RemainingCount}";

    public string SenderDisplay => string.IsNullOrWhiteSpace(FromDisplayName)
        ? FromAddress
        : FromDisplayName;

    public string SenderInitials
    {
        get
        {
            string[] parts = SenderDisplay
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return "?";
            }

            string initials = parts.Length == 1
                ? parts[0][0].ToString()
                : string.Concat(parts[0][0], parts[^1][0]);
            return initials.ToUpperInvariant();
        }
    }

    public string SenderAvatarBackground
    {
        get
        {
            string identity = string.IsNullOrWhiteSpace(FromAddress)
                ? SenderDisplay.Trim()
                : FromAddress.Trim();
            uint hash = 2166136261;
            foreach (char symbol in identity)
            {
                hash ^= char.ToLowerInvariant(symbol);
                hash = unchecked(hash * 16777619u);
            }

            return SenderAvatarBackgroundPalette[hash % (uint)SenderAvatarBackgroundPalette.Length];
        }
    }

    public string DisplayDate
    {
        get
        {
            DateTime local = ReceivedAt.ToLocalTime().DateTime;
            DateTime today = DateTime.Today;
            if (local.Date == today)
            {
                return local.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
            }

            if (local.Date >= today.AddDays(-6))
            {
                return local.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);
            }

            return local.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.CurrentCulture);
        }
    }
}

public sealed record MailAttachmentPreviewItem(
    string DisplayFileName,
    string ContentType,
    long? Size)
{
    public MailAttachmentVisualType VisualType => MailAttachmentVisualCatalog.Resolve(ContentType);
    public string TypeLabel => MailAttachmentVisualCatalog.GetBadgeText(VisualType);
}

public enum MailAttachmentVisualType
{
    Pdf,
    Image,
    Document,
    Spreadsheet,
    Archive,
    Text,
    Generic
}

public static class MailAttachmentVisualCatalog
{
    private static readonly HashSet<string> DocumentContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/msword",
        "application/rtf",
        "application/vnd.ms-word",
        "application/vnd.oasis.opendocument.text",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "text/rtf"
    };

    private static readonly HashSet<string> SpreadsheetContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.ms-excel",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/csv"
    };

    private static readonly HashSet<string> ArchiveContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/gzip",
        "application/vnd.rar",
        "application/x-7z-compressed",
        "application/x-bzip2",
        "application/x-compressed",
        "application/x-rar-compressed",
        "application/x-tar",
        "application/zip"
    };

    public static MailAttachmentVisualType Resolve(string? contentType)
    {
        string normalized = Normalize(contentType);
        if (normalized.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return MailAttachmentVisualType.Pdf;
        }

        if (normalized.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return MailAttachmentVisualType.Image;
        }

        if (DocumentContentTypes.Contains(normalized))
        {
            return MailAttachmentVisualType.Document;
        }

        if (SpreadsheetContentTypes.Contains(normalized))
        {
            return MailAttachmentVisualType.Spreadsheet;
        }

        if (ArchiveContentTypes.Contains(normalized))
        {
            return MailAttachmentVisualType.Archive;
        }

        return normalized.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            ? MailAttachmentVisualType.Text
            : MailAttachmentVisualType.Generic;
    }

    public static string GetBadgeText(MailAttachmentVisualType visualType) => visualType switch
    {
        MailAttachmentVisualType.Pdf => "PDF",
        MailAttachmentVisualType.Image => "IMG",
        MailAttachmentVisualType.Document => "DOC",
        MailAttachmentVisualType.Spreadsheet => "XLS",
        MailAttachmentVisualType.Archive => "ZIP",
        MailAttachmentVisualType.Text => "TXT",
        _ => "FILE"
    };

    private static string Normalize(string? contentType)
    {
        string value = contentType?.Trim() ?? string.Empty;
        int parameter = value.IndexOf(';');
        return parameter < 0 ? value : value[..parameter].Trim();
    }
}

public sealed record MailMessageAttachmentSummary(
    int Count,
    IReadOnlyList<MailAttachmentPreviewItem> PreviewItems)
{
    public const int MaximumPreviewItems = 2;
    public static MailMessageAttachmentSummary Empty { get; } = new(0, []);
    public int RemainingCount => Math.Max(0, Count - PreviewItems.Count);

    public static MailMessageAttachmentSummary Create(IEnumerable<MailAttachmentPreviewItem> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        int count = 0;
        List<MailAttachmentPreviewItem> previews = new(MaximumPreviewItems);
        foreach (MailAttachmentPreviewItem attachment in attachments)
        {
            count++;
            if (previews.Count < MaximumPreviewItems)
            {
                previews.Add(attachment);
            }
        }

        return count == 0 ? Empty : new MailMessageAttachmentSummary(count, previews);
    }
}

public enum MailMessageBodyKind
{
    PlainText,
    SanitizedHtml
}

public sealed record MailRemoteImageReference(
    string ImageId,
    Uri SourceUri);

public sealed record MailImageContent(
    string ContentType,
    ReadOnlyMemory<byte> Bytes)
{
    public string ToDataUri() =>
        $"data:{ContentType};base64,{Convert.ToBase64String(Bytes.Span)}";
}

public sealed record MailMessageAddress(
    string DisplayName,
    string Address);

public sealed record MailReplyMetadata(
    string ReplyTo,
    string? MessageId,
    IReadOnlyList<string> References)
{
    public IReadOnlyList<MailMessageAddress> OriginalTo { get; init; } = [];
    public IReadOnlyList<MailMessageAddress> OriginalCc { get; init; } = [];
    internal string? ProviderThreadId { get; init; }
}

public sealed record MailAttachmentInfo(
    string AttachmentKey,
    string FileName,
    string ContentType,
    long Size,
    bool IsInline,
    bool IsDownloadable)
{
    public string DisplaySize => MailAttachmentSizeFormatter.Format(Size);
}

public static class MailAttachmentSizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        string[] units = ["Б", "КБ", "МБ", "ГБ"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}

public sealed record MailMessageContent(
    string MessageKey,
    string Subject,
    string FromDisplayName,
    string FromAddress,
    string To,
    DateTimeOffset ReceivedAt,
    MailMessageBodyKind BodyKind,
    string BodyContent,
    IReadOnlyList<MailRemoteImageReference> RemoteImages,
    bool IsUnread,
    bool HasAttachments,
    string SafePlainTextContent = "",
    MailReplyMetadata? ReplyMetadata = null)
{
    public DateTime ReceivedAtLocal => ReceivedAt.ToLocalTime().DateTime;

    public string SenderDisplay => string.IsNullOrWhiteSpace(FromDisplayName)
        ? FromAddress
        : FromDisplayName;

    public string PlainTextContent => BodyKind is MailMessageBodyKind.PlainText
        ? BodyContent
        : string.Empty;

    public string SanitizedHtmlContent => BodyKind is MailMessageBodyKind.SanitizedHtml
        ? BodyContent
        : string.Empty;

    public bool HasRemoteImages => RemoteImages.Count > 0;

    public IReadOnlyList<MailAttachmentInfo> Attachments { get; init; } = [];

    public bool HasUnambiguousFromAddress { get; init; }
}

public sealed record MailPage<T>(
    IReadOnlyList<T> Items,
    string? ContinuationToken,
    long? TotalCount = null)
{
    public bool HasMore => !string.IsNullOrWhiteSpace(ContinuationToken);
}
