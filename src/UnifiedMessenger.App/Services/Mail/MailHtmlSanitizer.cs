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
        InlineSafeEmbeddedPresentationColors(source);
        NormalizeLegacyPresentationColors(source);
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

    internal static bool TryDetectSupportedImageContentType(
        ReadOnlySpan<byte> bytes,
        out string contentType)
    {
        foreach ((string candidate, ImageSignatureValidator validator) in SupportedImageTypes)
        {
            if (validator(bytes))
            {
                contentType = candidate;
                return true;
            }
        }

        contentType = string.Empty;
        return false;
    }

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

    private static void InlineSafeEmbeddedPresentationColors(HtmlAgilityPack.HtmlDocument document)
    {
        HtmlNode[] styleNodes = document.DocumentNode.SelectNodes("//style")?.ToArray() ?? [];
        if (styleNodes.Length == 0)
        {
            return;
        }

        HtmlNode[] elements = document.DocumentNode
            .Descendants()
            .Where(node => node.NodeType is HtmlNodeType.Element && node.Name is not "style")
            .ToArray();
        Dictionary<HtmlNode, Dictionary<string, AppliedStaticColor>> safeStyles = [];
        int sourceOrder = 0;
        foreach (HtmlNode styleNode in styleNodes)
        {
            string css = HtmlEntity.DeEntitize(styleNode.InnerText);
            ICssStyleSheet? styleSheet;
            try
            {
                styleSheet = StyleParser.ParseStyleSheet(css);
            }
            catch
            {
                continue;
            }

            foreach (ICssStyleRule rule in EnumerateApplicableStyleRules(styleSheet.Rules))
            {
                IReadOnlyList<StaticColorDeclaration> safeDeclarations =
                    ExtractSafeStaticColorDeclarations(rule.Style);
                if (safeDeclarations.Count == 0)
                {
                    continue;
                }

                foreach (string selectorText in rule.SelectorText.Split(','))
                {
                    if (!TryParsePresentationSelector(selectorText, out PresentationSelector selector))
                    {
                        continue;
                    }

                    foreach (HtmlNode element in elements.Where(selector.Matches))
                    {
                        if (!safeStyles.TryGetValue(
                            element,
                            out Dictionary<string, AppliedStaticColor>? declarations))
                        {
                            declarations = new Dictionary<string, AppliedStaticColor>(StringComparer.OrdinalIgnoreCase);
                            safeStyles[element] = declarations;
                        }

                        foreach (StaticColorDeclaration declaration in safeDeclarations)
                        {
                            AppliedStaticColor candidate = new(
                                declaration.Value,
                                declaration.Important,
                                selector.Specificity,
                                sourceOrder);
                            if (!declarations.TryGetValue(declaration.PropertyName, out AppliedStaticColor current)
                                || candidate.HasPrecedenceOver(current))
                            {
                                declarations[declaration.PropertyName] = candidate;
                            }
                        }
                    }
                }

                sourceOrder++;
            }
        }

        foreach ((HtmlNode node, Dictionary<string, AppliedStaticColor> declarations) in safeStyles)
        {
            string inlineStyle = node.GetAttributeValue("style", string.Empty);
            string embeddedStyle = string.Join(
                ';',
                declarations.Select(pair =>
                    $"{pair.Key}:{pair.Value.Value}" + (pair.Value.Important ? " !important" : string.Empty)));
            node.SetAttributeValue(
                "style",
                string.IsNullOrWhiteSpace(inlineStyle)
                    ? embeddedStyle
                    : $"{embeddedStyle};{inlineStyle}");
        }
    }

    private static IEnumerable<ICssStyleRule> EnumerateApplicableStyleRules(ICssRuleList rules)
    {
        foreach (ICssRule rule in rules)
        {
            if (rule is ICssStyleRule styleRule)
            {
                yield return styleRule;
                continue;
            }

            if (rule is ICssMediaRule mediaRule
                && IsUnconditionalScreenMedia(mediaRule.Media.MediaText))
            {
                foreach (ICssStyleRule nested in EnumerateApplicableStyleRules(mediaRule.Rules))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool IsUnconditionalScreenMedia(string mediaText)
    {
        string normalized = mediaText.Trim();
        return string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "screen", StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeLegacyPresentationColors(HtmlAgilityPack.HtmlDocument document)
    {
        foreach (HtmlNode node in document.DocumentNode.SelectNodes("//*[@bgcolor]")?.ToArray() ?? [])
        {
            string legacyColor = HtmlEntity.DeEntitize(node.GetAttributeValue("bgcolor", string.Empty)).Trim();
            string safeColor = NormalizeStaticCssValue("background-color", legacyColor);
            if (!string.IsNullOrWhiteSpace(safeColor))
            {
                string inlineStyle = node.GetAttributeValue("style", string.Empty);
                node.SetAttributeValue(
                    "style",
                    string.IsNullOrWhiteSpace(inlineStyle)
                        ? $"background-color:{safeColor}"
                        : $"background-color:{safeColor};{inlineStyle}");
            }

            node.Attributes.Remove("bgcolor");
        }
    }

    private static IReadOnlyList<StaticColorDeclaration> ExtractSafeStaticColorDeclarations(
        ICssStyleDeclaration source)
    {
        List<StaticColorDeclaration> declarations = [];
        AddSafeStaticColor(source, declarations, "background-color");
        AddSafeStaticColor(source, declarations, "color");
        AddSafeStaticColor(source, declarations, "border-color");
        AddSafeStaticColor(source, declarations, "border-top-color");
        AddSafeStaticColor(source, declarations, "border-right-color");
        AddSafeStaticColor(source, declarations, "border-bottom-color");
        AddSafeStaticColor(source, declarations, "border-left-color");
        return declarations;
    }

    private static void AddSafeStaticColor(
        ICssStyleDeclaration source,
        ICollection<StaticColorDeclaration> target,
        string propertyName)
    {
        if (source.GetProperty(propertyName) is null)
        {
            return;
        }

        string value = NormalizeStaticCssValue(propertyName, source.GetPropertyValue(propertyName));
        if (!string.IsNullOrWhiteSpace(value))
        {
            target.Add(new StaticColorDeclaration(propertyName, value, IsImportant(source, propertyName)));
        }
    }

    private static bool IsImportant(ICssStyleDeclaration declaration, string propertyName) =>
        string.Equals(
            declaration.GetPropertyPriority(propertyName),
            "important",
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeStaticCssValue(string propertyName, string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || ContainsDynamicOrExternalCss(value))
        {
            return string.Empty;
        }

        ICssStyleDeclaration? declaration = StyleParser.ParseDeclaration($"{propertyName}:{value};");
        return declaration?.GetPropertyValue(propertyName)?.Trim() ?? string.Empty;
    }

    private static bool ContainsDynamicOrExternalCss(string value) =>
        value.Contains("url(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("image-set(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("expression(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
        || value.Contains("var(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("@import", StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePresentationSelector(string selectorText, out PresentationSelector selector)
    {
        selector = default;
        string value = selectorText.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(character => character is '+' or '~' or '[' or ']' or ':' or '*'))
        {
            return false;
        }

        List<SimplePresentationSelector> segments = [];
        List<PresentationCombinator> combinators = [];
        int index = 0;
        while (index < value.Length)
        {
            int segmentStart = index;
            while (index < value.Length && !char.IsWhiteSpace(value[index]) && value[index] != '>')
            {
                index++;
            }

            if (segmentStart == index
                || !TryParseSimpleSelectorSegment(
                    value[segmentStart..index],
                    out SimplePresentationSelector segment))
            {
                return false;
            }

            segments.Add(segment);
            bool hadWhitespace = false;
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                hadWhitespace = true;
                index++;
            }

            if (index >= value.Length)
            {
                break;
            }

            if (value[index] == '>')
            {
                combinators.Add(PresentationCombinator.Child);
                index++;
                while (index < value.Length && char.IsWhiteSpace(value[index]))
                {
                    index++;
                }
            }
            else if (hadWhitespace)
            {
                combinators.Add(PresentationCombinator.Descendant);
            }
            else
            {
                return false;
            }
        }

        if (segments.Count == 0 || combinators.Count != segments.Count - 1)
        {
            return false;
        }

        selector = new PresentationSelector(segments.ToArray(), combinators.ToArray());
        return true;
    }

    private static bool TryParseSimpleSelectorSegment(
        string value,
        out SimplePresentationSelector selector)
    {
        selector = default;
        int index = 0;
        string? tagName = null;
        string? id = null;
        List<string> classes = [];
        if (value[0] is not '.' and not '#')
        {
            tagName = ReadCssIdentifier(value, ref index);
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return false;
            }
        }

        while (index < value.Length)
        {
            char prefix = value[index++];
            if (prefix is not '.' and not '#')
            {
                return false;
            }

            string identifier = ReadCssIdentifier(value, ref index);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return false;
            }

            if (prefix == '#')
            {
                if (id is not null)
                {
                    return false;
                }

                id = identifier;
            }
            else
            {
                classes.Add(identifier);
            }
        }

        selector = new SimplePresentationSelector(tagName, id, classes.ToArray());
        return tagName is not null || id is not null || classes.Count > 0;
    }

    private static string ReadCssIdentifier(string value, ref int index)
    {
        int start = index;
        while (index < value.Length
            && (char.IsLetterOrDigit(value[index]) || value[index] is '-' or '_'))
        {
            index++;
        }

        return value[start..index];
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

            if (!IsExpandedBackgroundShorthand(declaration))
            {
                continue;
            }

            string backgroundColor = NormalizeStaticCssValue(
                "background-color",
                declaration.GetPropertyValue("background-color"));
            string priority = declaration.GetPropertyPriority("background-color") ?? string.Empty;
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

    private static bool IsExpandedBackgroundShorthand(ICssStyleDeclaration declaration) =>
        declaration.GetProperty("background-color") is not null
        && declaration.GetProperty("background-position-x") is not null
        && declaration.GetProperty("background-position-y") is not null;

    private readonly record struct PresentationSelector(
        IReadOnlyList<SimplePresentationSelector> Segments,
        IReadOnlyList<PresentationCombinator> Combinators)
    {
        public int Specificity => Segments.Sum(segment => segment.Specificity);

        public bool Matches(HtmlNode node)
        {
            int segmentIndex = Segments.Count - 1;
            if (!Segments[segmentIndex].Matches(node))
            {
                return false;
            }

            HtmlNode? current = node;
            while (segmentIndex > 0)
            {
                PresentationCombinator combinator = Combinators[segmentIndex - 1];
                segmentIndex--;
                if (combinator is PresentationCombinator.Child)
                {
                    current = current?.ParentNode;
                    if (current is null || !Segments[segmentIndex].Matches(current))
                    {
                        return false;
                    }

                    continue;
                }

                current = current?.ParentNode;
                while (current is not null && !Segments[segmentIndex].Matches(current))
                {
                    current = current.ParentNode;
                }

                if (current is null)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly record struct SimplePresentationSelector(
        string? TagName,
        string? Id,
        IReadOnlyList<string> Classes)
    {
        public int Specificity => (Id is null ? 0 : 100) + (Classes.Count * 10) + (TagName is null ? 0 : 1);

        public bool Matches(HtmlNode node)
        {
            if (TagName is not null && !string.Equals(node.Name, TagName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Id is not null
                && !string.Equals(node.GetAttributeValue("id", string.Empty), Id, StringComparison.Ordinal))
            {
                return false;
            }

            if (Classes.Count == 0)
            {
                return true;
            }

            HashSet<string> elementClasses = node
                .GetAttributeValue("class", string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            return Classes.All(elementClasses.Contains);
        }
    }

    private readonly record struct StaticColorDeclaration(
        string PropertyName,
        string Value,
        bool Important);

    private readonly record struct AppliedStaticColor(
        string Value,
        bool Important,
        int Specificity,
        int SourceOrder)
    {
        public bool HasPrecedenceOver(AppliedStaticColor other) =>
            Important != other.Important
                ? Important
                : Specificity != other.Specificity
                    ? Specificity > other.Specificity
                    : SourceOrder >= other.SourceOrder;
    }

    private enum PresentationCombinator
    {
        Descendant,
        Child
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
