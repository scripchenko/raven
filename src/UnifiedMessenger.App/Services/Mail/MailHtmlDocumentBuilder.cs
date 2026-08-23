using UnifiedMessenger.App.Models;
using HtmlAgilityPack;

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
                img[{{MailHtmlSanitizer.RemoteImageIdAttribute}}] { display: none !important; }
                table { border-collapse: collapse; }
                pre { white-space: pre-wrap; }
                a { color: #1769aa; text-decoration: underline; cursor: pointer; }
              </style>
            </head>
            <body><div class="um-mail-viewport"><div class="um-mail-content">{{renderedBody}}</div></div></body>
            </html>
            """;
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
