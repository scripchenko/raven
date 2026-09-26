using UnifiedMessenger.App.Models;
using HtmlAgilityPack;
using System.Globalization;
using System.Net;
using System.Text;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailHtmlDocumentBuilder : IMailHtmlDocumentBuilder
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'; " +
        "form-action 'none'; base-uri 'none'; worker-src 'none'; child-src 'none'; manifest-src 'none'; " +
        "media-src 'none'; font-src 'none'; img-src data:; style-src 'unsafe-inline'";

    public string Build(
        MailMessageContent content,
        IReadOnlyDictionary<string, MailImageContent>? remoteImages = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.BodyKind is not MailMessageBodyKind.SanitizedHtml)
        {
            throw new ArgumentException("Only sanitized HTML can be rendered as a mail document.", nameof(content));
        }

        string renderedBody = ApplyRemoteImages(content.SanitizedHtmlContent, remoteImages);
        string printHeader = BuildPrintHeader(content);
        return $$"""
            <!doctype html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="{{ContentSecurityPolicy}}">
              <meta name="referrer" content="no-referrer">
              <meta name="color-scheme" content="light">
              <style>
                html { width: 100%; max-width: 100%; box-sizing: border-box; background: #ffffff; color: #202124; }
                body { width: 100%; max-width: 100%; box-sizing: border-box; margin: 0; padding: 26px clamp(12px, 3vw, 30px) 44px; font-family: "Segoe UI", Arial, sans-serif; font-size: 15px; line-height: 1.55; }
                .um-mail-viewport { width: 100%; max-width: 100%; box-sizing: border-box; overflow-x: auto; overflow-y: visible; }
                .um-mail-content { display: block; width: 100%; min-width: 0; overflow: visible; }
                .um-mail-content > :first-child { margin-top: 0 !important; }
                .um-mail-content img { max-width: 100% !important; height: auto !important; }
                .um-print-header { display: none; }
                img[{{MailHtmlSanitizer.RemoteImageIdAttribute}}] { display: none !important; }
                table { border-collapse: collapse; }
                pre { white-space: pre-wrap; }
                a { color: #1769aa; text-decoration: underline; cursor: pointer; }
                @media print {
                  @page { margin: 16mm; }
                  html, body { width: auto; max-width: none; background: #ffffff !important; color: #111111 !important; }
                  body { margin: 0; padding: 0; font-size: 11pt; line-height: 1.45; }
                  .um-print-header { display: block; margin: 0 0 18pt; padding: 0 0 12pt; border-bottom: 1px solid #c8c8c8; }
                  .um-print-subject { margin: 0 0 10pt; font-size: 18pt; line-height: 1.25; }
                  .um-print-meta { display: grid; grid-template-columns: max-content 1fr; gap: 3pt 8pt; margin: 0; }
                  .um-print-meta dt { font-weight: 600; }
                  .um-print-meta dd { margin: 0; overflow-wrap: anywhere; }
                  .um-print-attachments { margin-top: 9pt; }
                  .um-print-attachments ul { margin: 4pt 0 0; padding-left: 18pt; }
                  .um-mail-viewport { width: auto; max-width: none; overflow: visible; }
                  .um-mail-content { width: auto; max-width: none; overflow: visible; }
                  .um-mail-content img { max-width: 100% !important; height: auto !important; }
                }
              </style>
            </head>
            <body>{{printHeader}}<div class="um-mail-viewport"><div class="um-mail-content">{{renderedBody}}</div></div></body>
            </html>
            """;
    }

    private static string BuildPrintHeader(MailMessageContent content)
    {
        static string Escape(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

        string sender = string.IsNullOrWhiteSpace(content.FromAddress)
            || string.Equals(content.FromDisplayName, content.FromAddress, StringComparison.OrdinalIgnoreCase)
                ? content.SenderDisplay
                : $"{content.SenderDisplay} <{content.FromAddress}>";
        string date = content.ReceivedAt.ToLocalTime().ToString(
            "ddd, d MMM, HH:mm",
            CultureInfo.CurrentCulture);
        StringBuilder header = new();
        header.Append("<section class=\"um-print-header\" aria-label=\"Mail header\">")
            .Append("<h1 class=\"um-print-subject\">")
            .Append(Escape(content.Subject))
            .Append("</h1><dl class=\"um-print-meta\">")
            .Append("<dt>").Append(Escape(L.Instance.Get("From:"))).Append("</dt><dd>")
            .Append(Escape(sender))
            .Append("</dd><dt>").Append(Escape(L.Instance.Get("To:"))).Append("</dt><dd>")
            .Append(Escape(content.To))
            .Append("</dd><dt>").Append(Escape(L.Instance.Get("Date:"))).Append("</dt><dd>")
            .Append(Escape(date))
            .Append("</dd></dl>");
        if (content.Attachments.Count > 0)
        {
            header.Append("<div class=\"um-print-attachments\"><strong>")
                .Append(Escape(L.Instance.Get("Attachments:"))).Append("</strong><ul>");
            foreach (MailAttachmentInfo attachment in content.Attachments)
            {
                header.Append("<li>")
                    .Append(Escape(attachment.FileName))
                    .Append("</li>");
            }

            header.Append("</ul></div>");
        }

        return header.Append("</section>").ToString();
    }

    private static string ApplyRemoteImages(
        string sanitizedHtml,
        IReadOnlyDictionary<string, MailImageContent>? remoteImages)
    {
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(sanitizedHtml);
        HtmlNodeCollection? images = document.DocumentNode.SelectNodes(
            $"//img[@{MailHtmlSanitizer.RemoteImageIdAttribute}]");
        if (images is null)
        {
            return document.DocumentNode.InnerHtml;
        }

        foreach (HtmlNode image in images)
        {
            string imageId = image.GetAttributeValue(MailHtmlSanitizer.RemoteImageIdAttribute, string.Empty);
            if (remoteImages is null
                || !remoteImages.TryGetValue(imageId, out MailImageContent? content)
                || content.Bytes.Length > RemoteMailImageLoader.MaximumImageBytes
                || !MailHtmlSanitizer.IsSupportedImage(content.ContentType, content.Bytes.Span))
            {
                image.Attributes.Remove("src");
                continue;
            }

            image.SetAttributeValue("src", content.ToDataUri());
            image.Attributes.Remove(MailHtmlSanitizer.RemoteImageIdAttribute);
        }

        return document.DocumentNode.InnerHtml;
    }
}
