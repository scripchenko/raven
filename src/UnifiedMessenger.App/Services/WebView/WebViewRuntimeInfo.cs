namespace UnifiedMessenger.App.Services.WebView;

public sealed record WebViewRuntimeInfo(bool IsAvailable, string? Version)
{
    public static WebViewRuntimeInfo Missing { get; } = new(false, null);
}
