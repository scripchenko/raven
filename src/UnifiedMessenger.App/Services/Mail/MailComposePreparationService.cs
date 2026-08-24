using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailComposePreparationService : IMailComposePreparationService
{
    private static readonly Regex ReplyPrefix = new(
        @"^\s*(?:(?:re|ответ)\s*:\s*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ForwardPrefix = new(
        @"^\s*(?:(?:fwd?|пересл\.)\s*:\s*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public MailComposeTemplate CreateReply(MailMessageContent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string recipient = ResolveReplyRecipient(source);
        MailReplyMetadata? metadata = source.ReplyMetadata;
        List<string> references = metadata?.References
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(metadata?.MessageId)
            && !references.Contains(metadata.MessageId, StringComparer.Ordinal))
        {
            references.Add(metadata.MessageId);
        }

        string? originalMessageId = string.IsNullOrWhiteSpace(metadata?.MessageId)
            ? null
            : metadata.MessageId;
        MailReplyContext? context = originalMessageId is null
            ? null
            : new MailReplyContext(
                originalMessageId,
                references)
            {
                ProviderThreadId = metadata!.ProviderThreadId
            };
        return new MailComposeTemplate(
            recipient,
            string.Empty,
            string.Empty,
            NormalizeReplySubject(source.Subject),
            BuildReplyBody(source),
            context);
    }

    public MailComposeTemplate CreateForward(MailMessageContent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new MailComposeTemplate(
            string.Empty,
            string.Empty,
            string.Empty,
            NormalizeForwardSubject(source.Subject),
            BuildForwardBody(source))
        {
            ForwardAttachments = source.Attachments
                .Where(attachment => attachment.IsDownloadable && !attachment.IsInline)
                .Select(attachment => new MailForwardAttachmentOffer(source.MessageKey, attachment))
                .ToArray()
        };
    }

    internal static string NormalizeReplySubject(string? subject)
    {
        string normalized = NormalizeOriginalSubject(subject);
        normalized = ReplyPrefix.Replace(normalized, string.Empty).Trim();
        return "Re: " + (string.IsNullOrWhiteSpace(normalized) ? "(без темы)" : normalized);
    }

    internal static string NormalizeForwardSubject(string? subject)
    {
        string normalized = NormalizeOriginalSubject(subject);
        normalized = ForwardPrefix.Replace(normalized, string.Empty).Trim();
        return "Fwd: " + (string.IsNullOrWhiteSpace(normalized) ? "(без темы)" : normalized);
    }

    private static string ResolveReplyRecipient(MailMessageContent source)
    {
        string replyTo = source.ReplyMetadata?.ReplyTo ?? string.Empty;
        if (IsValidAddressList(replyTo))
        {
            return replyTo;
        }

        if (string.IsNullOrWhiteSpace(source.FromAddress))
        {
            return string.Empty;
        }

        try
        {
            return new MailboxAddress(source.FromDisplayName, source.FromAddress).ToString();
        }
        catch (ArgumentException)
        {
            return source.FromAddress;
        }
    }

    private static bool IsValidAddressList(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        try
        {
            MailboxAddress[] addresses = InternetAddressList.Parse(value).Mailboxes.ToArray();
            return addresses.Length > 0
                && addresses.All(address => MailComposeRequestFactory.IsValidMailboxAddress(address.Address));
        }
        catch (ParseException)
        {
            return false;
        }
    }

    private static string BuildReplyBody(MailMessageContent source)
    {
        StringBuilder body = new();
        body.AppendLine().AppendLine();
        body.Append(source.ReceivedAtLocal.ToString("g", CultureInfo.CurrentCulture));
        body.Append(", ").Append(source.SenderDisplay).AppendLine(":");
        AppendQuoted(body, GetSafeBody(source));
        return body.ToString();
    }

    private static string BuildForwardBody(MailMessageContent source)
    {
        StringBuilder body = new();
        body.AppendLine().AppendLine();
        body.AppendLine("---------- Пересланное сообщение ----------");
        body.Append("Отправитель — ").AppendLine(source.SenderDisplay);
        body.Append("Дата отправки — ").AppendLine(source.ReceivedAtLocal.ToString("g", CultureInfo.CurrentCulture));
        body.Append("Тема сообщения — ").AppendLine(source.Subject);
        body.Append("Получатель — ").AppendLine(source.To);
        body.AppendLine();
        AppendQuoted(body, GetSafeBody(source));
        return body.ToString();
    }

    private static string GetSafeBody(MailMessageContent source) =>
        string.IsNullOrWhiteSpace(source.SafePlainTextContent)
            ? source.BodyKind is MailMessageBodyKind.PlainText
                ? source.PlainTextContent
                : "(текст письма недоступен)"
            : source.SafePlainTextContent;

    private static void AppendQuoted(StringBuilder body, string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (string line in normalized.Split('\n'))
        {
            body.Append("> ").AppendLine(line);
        }
    }

    private static string NormalizeOriginalSubject(string? subject) =>
        string.IsNullOrWhiteSpace(subject) || string.Equals(subject.Trim(), "(без темы)", StringComparison.Ordinal)
            ? string.Empty
            : subject.Trim();
}
