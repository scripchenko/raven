using System.Globalization;

namespace UnifiedMessenger.App.Models;

public static class SidebarBadgeFormatter
{
    public static string? Format(int count) => count switch
    {
        <= 0 => null,
        >= 100 => "99+",
        _ => count.ToString(CultureInfo.InvariantCulture)
    };
}
