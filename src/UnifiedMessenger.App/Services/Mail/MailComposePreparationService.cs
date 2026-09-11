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
        return CreateReplyTemplate(source, ResolveReplyRecipient(source), string.Empty);
    }

    public MailComposeTemplate CreateReplyAll(MailMessageContent source, MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(account);

        string primaryRecipient = ResolveReplyRecipient(source);
        List<MailMessageAddress> to = [];
        List<MailMessageAddress> cc = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string ownAddress = account.EmailAddress.Trim();
        bool primaryIsValid = TryParseValidAddresses(primaryRecipient, out IReadOnlyList<MailMessageAddress> primary);
        if (primaryIsValid)
        {
            AddUniqueValidAddresses(to, primary, ownAddress, seen);
        }

        AddUniqueValidAddresses(to, source.ReplyMetadata?.OriginalTo ?? [], ownAddress, seen);
        AddUniqueValidAddresses(cc, source.ReplyMetadata?.OriginalCc ?? [], ownAddress, seen);

        string formattedTo = FormatAddresses(to);
        if (!primaryIsValid && !string.IsNullOrWhiteSpace(primaryRecipient))
        {
            formattedTo = string.IsNullOrWhiteSpace(formattedTo)
                ? primaryRecipient
                : $"{primaryRecipient}, {formattedTo}";
        }

        return CreateReplyTemplate(source, formattedTo, FormatAddresses(cc));
    }

    private static MailComposeTemplate CreateReplyTemplate(
        MailMessageContent source,
        string to,
        string cc)
    {
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
            to,
            cc,
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

    private static void AddUniqueValidAddresses(
        ICollection<MailMessageAddress> destination,
        IEnumerable<MailMessageAddress> candidates,
        string ownAddress,
        ISet<string> seen)
    {
        foreach (MailMessageAddress candidate in candidates)
        {
            string address = candidate.Address.Trim();
            if (!MailComposeRequestFactory.IsValidMailboxAddress(address)
                || string.Equals(address, ownAddress, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(address))
            {
                continue;
            }

            destination.Add(new MailMessageAddress(candidate.DisplayName.Trim(), address));
        }
    }

    private static string FormatAddresses(IEnumerable<MailMessageAddress> addresses) =>
        string.Join(
            ", ",
            addresses.Select(address => new MailboxAddress(address.DisplayName, address.Address).ToString()));

    private static bool IsValidAddressList(string value) =>
        TryParseValidAddresses(value, out _);

    private static bool TryParseValidAddresses(
        string value,
        out IReadOnlyList<MailMessageAddress> addresses)
    {
        addresses = [];
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return false;
        }

        try
        {
            MailMessageAddress[] parsed = InternetAddressList.Parse(value).Mailboxes
                .Select(address => new MailMessageAddress(
                    address.Name?.Trim() ?? string.Empty,
                    address.Address?.Trim() ?? string.Empty))
                .ToArray();
            if (parsed.Length == 0
                || parsed.Any(address => !MailComposeRequestFactory.IsValidMailboxAddress(address.Address)))
            {
                return false;
            }

            addresses = parsed;
            return true;
        }
        catch (Exception exception) when (exception is ParseException or ArgumentException)
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
