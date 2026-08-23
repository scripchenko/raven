using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using Ganss.Xss;
using HtmlAgilityPack;
using MimeKit;
using System.IO;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailHtmlSanitizer : IMailHtmlSanitizer
{
    private delegate bool ImageSignatureValidator(ReadOnlySpan<byte> bytes);

    public const int MaximumInlineImageBytes = 5 * 1024 * 1024;
    public const int MaximumInlineImageCount = 32;
    internal const string RemoteImageIdAttribute = "data-um-remote-image-id";

    private static readonly IReadOnlyDictionary<string, ImageSignatureValidator> SupportedImageTypes =
        new Dictionary<string, ImageSignatureValidator>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = bytes => bytes.Length >= 8
                && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ["image/jpeg"] = bytes => bytes.Length >= 3
                && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            ["image/gif"] = bytes => bytes.Length >= 6
                && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)),
            ["image/webp"] = bytes => bytes.Length >= 12
                && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)
        };

    private static readonly string[] SafeTags =
    [
        "a", "abbr", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br",
        "caption", "center", "cite", "code", "col", "colgroup", "dd", "del", "div", "dl", "dt", "em",
        "figcaption", "figure", "font", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr",
        "i", "img", "ins", "kbd", "li", "main", "mark", "nav", "ol", "p", "pre", "q", "s", "samp",
        "section", "small", "span", "strike", "strong", "sub", "sup", "table", "tbody", "td", "tfoot",
        "th", "thead", "time", "tr", "tt", "u", "ul", "var", "wbr"
    ];

    private static readonly string[] SafeAttributes =
    [
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "char", "charoff", "cite", "color",
        "colspan", "datetime", "dir", "face", "headers", "height", "href", "hspace", "lang", "nowrap", "rel",
        "reversed", "rowspan", "rules", "scope", "size", "span", "src", "start", "style", "summary", "title",
        "type", "valign", "vspace", "width", RemoteImageIdAttribute
    ];

    private static readonly string[] SafeCssProperties =
    [
        "background-color", "border", "border-bottom", "border-bottom-color", "border-bottom-left-radius",
        "border-bottom-right-radius", "border-bottom-style",
        "border-bottom-width", "border-collapse", "border-color", "border-left", "border-left-color",
        "border-left-style", "border-left-width", "border-right", "border-right-color", "border-right-style",
        "border-radius", "border-right-width", "border-spacing", "border-style", "border-top", "border-top-color",
        "border-top-left-radius", "border-top-right-radius", "border-top-style", "border-top-width", "border-width",
        "box-sizing", "clear", "color", "direction", "display", "float", "font-family", "font-size", "font-style",
        "font-weight", "height", "letter-spacing", "line-height", "list-style-type", "margin", "margin-bottom",
        "margin-left", "margin-right", "margin-top", "max-height", "max-width", "min-height", "min-width", "overflow-wrap",
        "padding", "padding-bottom", "padding-left", "padding-right", "padding-top", "table-layout", "text-align",
        "text-decoration", "text-indent", "text-overflow", "text-transform", "vertical-align", "white-space", "width",
        "word-break", "word-spacing", "word-wrap"
    ];

    private static readonly CssParser StyleParser = new();
    private readonly HtmlSanitizer _sanitizer = CreateSanitizer();

    public MailHtmlSanitizationResult Sanitize(string html, MimeMessage message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        ArgumentNullException.ThrowIfNull(message);

        HtmlAgilityPack.HtmlDocument source = new();
        source.LoadHtml(html);
        NormalizeResponsiveImages(source);
        NormalizePresentationStyles(source);
        List<MailRemoteImageReference> remoteImages = [];
        int inlineImageCount = PrepareImageSources(source, message, remoteImages);

        string sanitized = _sanitizer.Sanitize(source.DocumentNode.InnerHtml);
        HtmlAgilityPack.HtmlDocument document = new();
        document.LoadHtml(sanitized);
        NormalizeLinks(document);
        FinalizeImages(document);
        return new MailHtmlSanitizationResult(
            document.DocumentNode.InnerHtml.Trim(),
            remoteImages.AsReadOnly(),
            inlineImageCount);
    }

    internal static bool IsSupportedImage(string contentType, ReadOnlySpan<byte> bytes) =>
        SupportedImageTypes.TryGetValue(contentType, out ImageSignatureValidator? validator)
        && validator(bytes);

    private static HtmlSanitizer CreateSanitizer()
    {
        HtmlSanitizer sanitizer = new()
        {
            AllowCssCustomProperties = false,
            AllowDataAttributes = false,
            KeepChildNodes = false
        };
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(SafeTags);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(SafeAttributes);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedCssProperties.UnionWith(SafeCssProperties);
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith([Uri.UriSchemeHttp, Uri.UriSchemeHttps, "data"]);
        return sanitizer;
    }

    private static void NormalizeResponsiveImages(HtmlAgilityPack.HtmlDocument document)
    {
        foreach (HtmlNode picture in document.DocumentNode.SelectNodes("//picture")?.ToArray() ?? [])
        {
            HtmlNode? image = picture.SelectSingleNode(".//img");
            if (image is null || picture.ParentNode is not HtmlNode parent)
            {
                picture.Remove();
                continue;
            }

            string selectedSource = picture.SelectNodes(".//source")?
                .Select(SelectImageSource)
                .FirstOrDefault(source => !string.IsNullOrWhiteSpace(source))
                ?? SelectImageSource(image);
            if (!string.IsNullOrWhiteSpace(selectedSource))
            {
                image.SetAttributeValue("src", selectedSource);
            }

            image.ParentNode?.RemoveChild(image);
            parent.ReplaceChild(image, picture);
        }
    }

    private static void NormalizePresentationStyles(HtmlAgilityPack.HtmlDocument document)
    {
        foreach (HtmlNode node in document.DocumentNode.SelectNodes("//*[@style]")?.ToArray() ?? [])
        {
            string originalStyle = HtmlEntity.DeEntitize(node.GetAttributeValue("style", string.Empty));
            if (StyleParser.ParseDeclaration(originalStyle) is not ICssStyleDeclaration declaration)
            {
                continue;
            }

            ICssProperty? backgroundProperty = declaration.GetProperty("background");
            if (backgroundProperty is null)
            {
                continue;
            }

            string backgroundColor = ExtractBackgroundColor(declaration.GetPropertyValue("background"));
            string priority = declaration.GetPropertyPriority("background") ?? string.Empty;
            declaration.RemoveProperty("background");
            string normalizedStyle = declaration.CssText;
            if (!string.IsNullOrWhiteSpace(backgroundColor))
            {
                normalizedStyle = $"{normalizedStyle};background-color:{backgroundColor}" +
                    (string.Equals(priority, "important", StringComparison.OrdinalIgnoreCase)
                        ? " !important"
                        : string.Empty);
            }

            node.SetAttributeValue("style", normalizedStyle);
        }
    }

    private static string ExtractBackgroundColor(string parsedBackground)
    {
        ICssStyleDeclaration? colorDeclaration = StyleParser.ParseDeclaration($"color:{parsedBackground};");
        return colorDeclaration?.GetPropertyValue("color") ?? string.Empty;
    }

    private static string SelectImageSource(HtmlNode node)
    {
        string source = HtmlEntity.DeEntitize(node.GetAttributeValue("src", string.Empty)).Trim();
        if (IsSupportedImageReference(source))
        {
            return source;
        }

        string srcset = HtmlEntity.DeEntitize(node.GetAttributeValue("srcset", string.Empty));
        return EnumerateSrcsetCandidates(srcset).FirstOrDefault(IsSupportedImageReference) ?? string.Empty;
    }

    private static IEnumerable<string> EnumerateSrcsetCandidates(string srcset)
    {
        int index = 0;
        while (index < srcset.Length)
        {
            while (index < srcset.Length && (char.IsWhiteSpace(srcset[index]) || srcset[index] == ','))
            {
                index++;
            }

            int start = index;
            while (index < srcset.Length && !char.IsWhiteSpace(srcset[index]) && srcset[index] != ',')
            {
                index++;
            }

            if (index > start)
            {
                yield return srcset[start..index].Trim();
            }

            while (index < srcset.Length && srcset[index] != ',')
            {
                index++;
            }
        }
    }

    private static bool IsSupportedImageReference(string source)
    {
        if (source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            return source.Length > 4;
        }

        return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static int PrepareImageSources(
        HtmlAgilityPack.HtmlDocument document,
        MimeMessage message,
        ICollection<MailRemoteImageReference> remoteImages)
    {
        HtmlNodeCollection? images = document.DocumentNode.SelectNodes("//img");
        if (images is null)
        {
            return 0;
        }

        int inlineImageCount = 0;
        int remoteOrdinal = 0;
        foreach (HtmlNode image in images)
        {
            string source = SelectImageSource(image);
            image.Attributes.Remove("src");
            image.Attributes.Remove("srcset");
            image.Attributes.Remove(RemoteImageIdAttribute);
            if (TryCreateInlineDataUri(message, source, inlineImageCount, out string dataUri))
            {
                image.SetAttributeValue("src", dataUri);
                inlineImageCount++;
                continue;
            }

            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? remoteUri)
                || (!string.Equals(remoteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(remoteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string imageId = $"remote-{++remoteOrdinal}";
            image.SetAttributeValue(RemoteImageIdAttribute, imageId);
            remoteImages.Add(new MailRemoteImageReference(imageId, remoteUri));
        }

        return inlineImageCount;
    }

    private static bool TryCreateInlineDataUri(
        MimeMessage message,
        string source,
        int inlineImageCount,
        out string dataUri)
    {
        dataUri = string.Empty;
        if (inlineImageCount >= MaximumInlineImageCount
            || !source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string contentId = Uri.UnescapeDataString(source[4..]).Trim().Trim('<', '>');
        MimePart? part = message.BodyParts
            .OfType<MimePart>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.ContentId?.Trim('<', '>'), contentId, StringComparison.OrdinalIgnoreCase));
        string contentType = part?.ContentType.MimeType?.ToLowerInvariant() ?? string.Empty;
        if (part?.Content is null || !SupportedImageTypes.ContainsKey(contentType))
        {
            return false;
        }

        try
        {
            using SizeLimitedMemoryStream decoded = new(MaximumInlineImageBytes);
            part.Content.DecodeTo(decoded);
            byte[] bytes = decoded.ToArray();
            if (!IsSupportedImage(contentType, bytes))
            {
                return false;
            }

            dataUri = new MailImageContent(contentType, bytes).ToDataUri();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void NormalizeLinks(HtmlAgilityPack.HtmlDocument document)
    {
        HtmlNodeCollection? links = document.DocumentNode.SelectNodes("//a[@href]");
        if (links is null)
        {
            return;
        }

        foreach (HtmlNode link in links)
        {
            string href = HtmlEntity.DeEntitize(link.GetAttributeValue("href", string.Empty)).Trim();
            if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? target)
                || (!string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                link.Attributes.Remove("href");
                continue;
            }

            link.SetAttributeValue("href", target.AbsoluteUri);
            link.SetAttributeValue("rel", "noopener noreferrer");
            if (!NormalizeVisibleText(HtmlEntity.DeEntitize(link.InnerText)).Any(char.IsLetterOrDigit))
            {
                if (link.Descendants("img").Any())
                {
                    continue;
                }

                link.InnerHtml = HtmlEntity.Entitize(target.AbsoluteUri);
            }
            else
            {
                RemoveDuplicatedHrefEcho(link, target.AbsoluteUri);
            }
        }
    }

    private static void FinalizeImages(HtmlAgilityPack.HtmlDocument document)
    {
        HtmlNodeCollection? images = document.DocumentNode.SelectNodes("//img");
        if (images is null)
        {
            return;
        }

        foreach (HtmlNode image in images.ToArray())
        {
            string remoteId = image.GetAttributeValue(RemoteImageIdAttribute, string.Empty);
            string source = image.GetAttributeValue("src", string.Empty);
            bool isGeneratedInlineImage = SupportedImageTypes.Keys.Any(contentType =>
                source.StartsWith($"data:{contentType};base64,", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(remoteId))
            {
                image.Attributes.Remove("src");
            }
            else if (!isGeneratedInlineImage)
            {
                image.Remove();
            }
        }
    }

    private static void RemoveDuplicatedHrefEcho(HtmlNode link, string href)
    {
        string visible = NormalizeVisibleText(HtmlEntity.DeEntitize(link.InnerText));
        string descriptive = NormalizeVisibleText(
            visible
                .Replace($"<{href}>", string.Empty, StringComparison.Ordinal)
                .Replace($"({href})", string.Empty, StringComparison.Ordinal)
                .Replace(href, string.Empty, StringComparison.Ordinal));
        if (!descriptive.Any(char.IsLetterOrDigit))
        {
            return;
        }

        foreach (HtmlNode textNode in link.DescendantsAndSelf().Where(node => node.NodeType == HtmlNodeType.Text).ToArray())
        {
            string value = HtmlEntity.DeEntitize(textNode.InnerText);
            string cleaned = value
                .Replace($"<{href}>", string.Empty, StringComparison.Ordinal)
                .Replace($"({href})", string.Empty, StringComparison.Ordinal)
                .Replace(href, string.Empty, StringComparison.Ordinal);
            if (string.Equals(value, cleaned, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(NormalizeVisibleText(cleaned))
                && textNode.ParentNode is HtmlNode parent
                && !ReferenceEquals(parent, link)
                && parent.ChildNodes.Count == 1)
            {
                parent.Remove();
            }
            else
            {
                textNode.InnerHtml = HtmlEntity.Entitize(cleaned);
            }
        }
    }

    private static string NormalizeVisibleText(string value) =>
        string.Join(
            ' ',
            value
                .Replace("\u200B", string.Empty, StringComparison.Ordinal)
                .Replace("\u200C", string.Empty, StringComparison.Ordinal)
                .Replace("\u200D", string.Empty, StringComparison.Ordinal)
                .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class SizeLimitedMemoryStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityWithinLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityWithinLimit(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityWithinLimit(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityWithinLimit(int additionalBytes)
        {
            if (Length + additionalBytes > maximumBytes)
            {
                throw new InvalidDataException("Inline image exceeds the in-memory size limit.");
            }
        }
    }

}
