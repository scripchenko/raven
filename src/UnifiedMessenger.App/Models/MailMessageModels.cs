namespace UnifiedMessenger.App.Models;

public sealed record MailMessageSummary(
    string MessageKey,
    string Subject,
    string FromDisplayName,
    string FromAddress,
    DateTimeOffset ReceivedAt,
    string Preview,
    bool IsUnread)
{
    public string SenderDisplay => string.IsNullOrWhiteSpace(FromDisplayName)
        ? FromAddress
        : FromDisplayName;

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

public sealed record MailReplyMetadata(
    string ReplyTo,
    string? MessageId,
    IReadOnlyList<string> References)
{
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
}

public sealed record MailPage<T>(
    IReadOnlyList<T> Items,
    string? ContinuationToken)
{
    public bool HasMore => !string.IsNullOrWhiteSpace(ContinuationToken);
}
