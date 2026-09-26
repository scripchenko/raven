using System.IO;

namespace UnifiedMessenger.App.Services.Notifications;

public static class LanternSoundFilePolicy
{
    public const long MaximumFileSizeBytes = 10 * 1024 * 1024;
    public const string DefaultSoundRelativePath = "Assets/Sounds/lantern_notification.wav";

    private static readonly HashSet<string> SupportedExtensions = new(
        [".wav", ".mp3", ".wma"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Extensions => SupportedExtensions;

    public static bool IsSupportedExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(extension);

    public static bool IsSafeInternalFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || !fileName.StartsWith("custom-notification", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSupportedExtension(Path.GetExtension(fileName));
    }

    public static string CreateInternalFileName(string extension) =>
        $"custom-notification{extension.ToLowerInvariant()}";

    public static string CreateSafeDisplayName(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        string safe = new(fileName.Where(character => !char.IsControl(character)).ToArray());
        safe = safe.Trim();
        if (safe.Length > 128)
        {
            safe = safe[..128];
        }

        return string.IsNullOrWhiteSpace(safe) ? L.Instance.Get("Custom sound") : safe;
    }
}
