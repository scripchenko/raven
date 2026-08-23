using System.IO;
using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.WebView;

public interface ICoreWebView2EnvironmentProvider
{
    Task<CoreWebView2Environment> GetAsync();
}

public sealed class CoreWebView2EnvironmentProvider(IAppPaths appPaths) : ICoreWebView2EnvironmentProvider
{
    private readonly object _sync = new();
    private Task<CoreWebView2Environment>? _environmentTask;

    public Task<CoreWebView2Environment> GetAsync()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(appPaths.WebViewDataFolder);
            return _environmentTask ??= CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: appPaths.WebViewDataFolder,
                options: null);
        }
    }
}
