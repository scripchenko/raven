namespace UnifiedMessenger.App.Services.WebView;

public interface IExternalBrowserService
{
    bool TryOpen(Uri uri);
}
