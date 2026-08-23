using System.Text;
using HtmlAgilityPack;
using MimeKit;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailContentExtractor(IMailHtmlSanitizer htmlSanitizer) : IMailContentExtractor
{
    private const string EmptyBodyText = "В письме нет текстового содержимого.";

    public MailMessageContent Extract(string messageKey, MimeMessage message, bool isUnread)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        ArgumentNullException.ThrowIfNull(message);

        string? htmlBody = message.HtmlBody;
        MailMessageBodyKind bodyKind;
        string bodyContent;
        IReadOnlyList<MailRemoteImageReference> remoteImages;
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            bodyKind = MailMessageBodyKind.SanitizedHtml;
            MailHtmlSanitizationResult sanitized = htmlSanitizer.Sanitize(htmlBody, message);
            bodyContent = sanitized.SanitizedHtml;
            remoteImages = sanitized.RemoteImages;
            if (string.IsNullOrWhiteSpace(bodyContent))
            {
                bodyContent = $"<p>{EmptyBodyText}</p>";
            }
        }
        else
        {
            bodyKind = MailMessageBodyKind.PlainText;
            bodyContent = NormalizePlainText(message.TextBody);
            remoteImages = [];
            if (string.IsNullOrWhiteSpace(bodyContent))
            {
                bodyContent = EmptyBodyText;
            }
        }

        (string displayName, string address) = GetPrimaryMailbox(message.From);
        return new MailMessageContent(
            messageKey,
            NormalizeSubject(message.Subject),
            displayName,
            address,
            FormatAddresses(message.To),
            message.Date,
            bodyKind,
            bodyContent,
            remoteImages,
            isUnread,
            message.Attachments.Any());
    }

    internal static (string DisplayName, string Address) GetPrimaryMailbox(InternetAddressList? addresses)
    {
        MailboxAddress? mailbox = addresses?.Mailboxes.FirstOrDefault();
        if (mailbox is null)
        {
            return ("Неизвестный отправитель", string.Empty);
        }

        string address = mailbox.Address?.Trim() ?? string.Empty;
        string name = mailbox.Name?.Trim() ?? string.Empty;
        return (string.IsNullOrWhiteSpace(name) ? address : name, address);
    }

    internal static string FormatAddresses(InternetAddressList? addresses)
    {
        if (addresses is null)
        {
            return string.Empty;
        }

        return string.Join(
            "; ",
            addresses.Mailboxes.Select(mailbox =>
                string.IsNullOrWhiteSpace(mailbox.Name)
                    ? mailbox.Address
                    : $"{mailbox.Name} <{mailbox.Address}>")
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    internal static string NormalizeSubject(string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? "(без темы)" : subject.Trim();

    internal static string NormalizePreview(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return string.Empty;
        }

        StringBuilder result = new(preview.Length);
        bool previousWasWhitespace = false;
        foreach (char character in HtmlEntity.DeEntitize(preview))
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    result.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                result.Append(character);
                previousWasWhitespace = false;
            }
        }

        return result.ToString().Trim();
    }

    internal static string NormalizePlainText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string normalizedLines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        StringBuilder output = new(normalizedLines.Length);
        int emptyLineCount = 0;
        foreach (string line in normalizedLines.Split('\n'))
        {
            string trimmedEnd = line.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmedEnd))
            {
                emptyLineCount++;
                continue;
            }

            if (output.Length > 0)
            {
                output.AppendLine();
                if (emptyLineCount > 0)
                {
                    output.AppendLine();
                }
            }

            output.Append(trimmedEnd.TrimStart());
            emptyLineCount = 0;
        }

        return output.ToString().Trim();
    }
}
