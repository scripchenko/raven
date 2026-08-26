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
        string safePlainText;
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

            safePlainText = NormalizePlainText(message.TextBody);
            if (string.IsNullOrWhiteSpace(safePlainText))
            {
                safePlainText = ExtractSafePlainText(bodyContent);
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

            safePlainText = bodyContent;
        }

        IReadOnlyList<MailAttachmentInfo> attachments = MailMimeAttachmentCatalog.Extract(message);
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
            attachments.Count > 0,
            safePlainText,
            new MailReplyMetadata(
                FormatAddresses(message.ReplyTo),
                NormalizeMessageId(message.MessageId),
                message.References
                    .Select(NormalizeMessageId)
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
        {
            Attachments = attachments,
            HasUnambiguousFromAddress = message.From.Count == 1 && message.From[0] is MailboxAddress
        };
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

    internal static string ExtractSafePlainText(string sanitizedHtml)
    {
        if (string.IsNullOrWhiteSpace(sanitizedHtml))
        {
            return string.Empty;
        }

        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(sanitizedHtml);
        StringBuilder output = new(sanitizedHtml.Length);
        AppendVisibleText(document.DocumentNode, output);
        return NormalizePlainText(HtmlEntity.DeEntitize(output.ToString()));
    }

    private static void AppendVisibleText(HtmlNode node, StringBuilder output)
    {
        if (node.NodeType is HtmlNodeType.Comment
            || node.Name is "script" or "style" or "head" or "noscript")
        {
            return;
        }

        if (node.NodeType is HtmlNodeType.Text)
        {
            output.Append(node.InnerText);
            return;
        }

        bool isListItem = node.Name is "li";
        bool isBreak = node.Name is "br";
        bool isBlock = node.Name is "p" or "div" or "section" or "article" or "header" or "footer"
            or "table" or "tr" or "ul" or "ol" or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
        if (isBreak)
        {
            output.AppendLine();
            return;
        }

        if (isListItem)
        {
            output.Append("• ");
        }

        foreach (HtmlNode child in node.ChildNodes)
        {
            AppendVisibleText(child, output);
        }

        if (isListItem)
        {
            output.AppendLine();
        }
        else if (isBlock)
        {
            output.AppendLine().AppendLine();
        }
    }

    private static string? NormalizeMessageId(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
