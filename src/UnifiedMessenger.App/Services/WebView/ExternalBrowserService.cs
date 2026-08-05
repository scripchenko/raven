using System.ComponentModel;
using System.Diagnostics;

namespace UnifiedMessenger.App.Services.WebView;

public sealed class ExternalBrowserService : IExternalBrowserService
{
    private static readonly IReadOnlySet<string> SupportedSchemes = new HashSet<string>(
        [Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeMailto],
        StringComparer.OrdinalIgnoreCase);

    public bool TryOpen(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!CanOpen(uri))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool CanOpen(Uri uri) =>
        uri.IsAbsoluteUri && SupportedSchemes.Contains(uri.Scheme);
}
