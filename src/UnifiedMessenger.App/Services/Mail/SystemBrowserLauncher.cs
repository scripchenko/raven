using System.Security.Cryptography;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class SystemBrowserLauncher(IExternalBrowserService externalBrowserService)
    : ISystemBrowserLauncher
{
    public bool TryOpen(Uri uri) => externalBrowserService.TryOpen(uri);
}

public sealed class CryptographicOAuthStateGenerator : IOAuthStateGenerator
{
    public string CreateState()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
