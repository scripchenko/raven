using System.Globalization;
using System.Text.RegularExpressions;

namespace UnifiedMessenger.App.Services.Notifications;

public static partial class PageTitleUnreadCountParser
{
    public static int? TryParse(string? documentTitle)
    {
        if (string.IsNullOrWhiteSpace(documentTitle))
        {
            return null;
        }

        Match match = LeadingUnreadCount().Match(documentTitle);
        return match.Success
            && int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                ? count
                : null;
    }

    [GeneratedRegex(@"^\s*\((?<count>\d{1,10})\)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingUnreadCount();
}
