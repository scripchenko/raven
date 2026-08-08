using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Persistence;

public sealed class ApplicationSettingsStore(ISettingsService settingsService) : IApplicationSettingsStore, IDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings? _current;
    private bool _disposed;

    public AppSettings Current => _current
        ?? throw new InvalidOperationException("Application settings have not been initialized.");

    public bool IsInitialized => _current is not null;

    public void Initialize(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppSettings settings = Current;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await settingsService.SaveAsync(settings, cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveGate.Dispose();
    }
}
