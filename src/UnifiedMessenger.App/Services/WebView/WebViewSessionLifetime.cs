namespace UnifiedMessenger.App.Services.WebView;

internal sealed class WebViewSessionLifetime
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task<bool>? _initializationTask;
    private bool _released;

    public bool IsReleased
    {
        get
        {
            lock (_sync)
            {
                return _released;
            }
        }
    }

    public Task<bool> GetOrStartInitialization(
        Func<CancellationToken, Task<bool>> startInitialization)
    {
        ArgumentNullException.ThrowIfNull(startInitialization);

        lock (_sync)
        {
            if (_released)
            {
                return Task.FromResult(false);
            }

            return _initializationTask ??= startInitialization(_cancellation.Token);
        }
    }

    public bool TryAttachResource<T>(
        T resource,
        Action<T> attach,
        Action<T> closeRejectedResource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(closeRejectedResource);

        lock (_sync)
        {
            if (!_released)
            {
                attach(resource);
                return true;
            }
        }

        closeRejectedResource(resource);
        return false;
    }

    public bool TryRunIfActive(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_sync)
        {
            if (_released)
            {
                return false;
            }

            action();
            return true;
        }
    }

    public Task BeginRelease()
    {
        Task settlement;
        bool cancel;
        lock (_sync)
        {
            cancel = !_released;
            _released = true;
            settlement = _initializationTask ?? Task.CompletedTask;
        }

        if (cancel)
        {
            _cancellation.Cancel();
        }

        return settlement;
    }
}
