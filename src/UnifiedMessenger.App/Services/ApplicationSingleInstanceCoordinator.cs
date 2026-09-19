using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace UnifiedMessenger.App.Services;

internal sealed class ApplicationSingleInstanceCoordinator : IDisposable
{
    internal const string DefaultInstanceName = "Scripchenko.Raven.v1";
    internal static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Mutex? _mutex;
    private Task? _listenerTask;
    private bool _ownsMutex;
    private bool _acquisitionAttempted;
    private bool _disposed;

    internal ApplicationSingleInstanceCoordinator(string instanceName = DefaultInstanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        _mutexName = $@"Local\{instanceName}.Mutex";
        _pipeName = $"{instanceName}.Activation.Session{Process.GetCurrentProcess().SessionId}";
    }

    internal event EventHandler? ActivationRequested;

    internal bool IsPrimaryInstance => _ownsMutex;

    internal bool TryAcquirePrimaryInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acquisitionAttempted)
        {
            throw new InvalidOperationException("Single-instance ownership has already been evaluated.");
        }

        _acquisitionAttempted = true;
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        _ownsMutex = createdNew;
        return _ownsMutex;
    }

    internal void StartActivationListener()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation.");
        }

        _listenerTask ??= Task.Run(() => ListenForActivationAsync(_listenerCancellation.Token));
    }

    internal async Task<bool> TrySignalPrimaryInstanceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsMutex)
        {
            return false;
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            int connectionTimeoutMilliseconds = (int)Math.Clamp(
                remaining.TotalMilliseconds,
                1,
                400);

            try
            {
                using NamedPipeClientStream client = new(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(connectionTimeoutMilliseconds, cancellationToken);
                await client.WriteAsync(new byte[] { 1 }, cancellationToken);
                await client.FlushAsync(cancellationToken);
                return true;
            }
            catch (Exception exception) when (
                exception is TimeoutException or IOException
                && stopwatch.Elapsed < timeout)
            {
                TimeSpan retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(100, Math.Max(1, (timeout - stopwatch.Elapsed).TotalMilliseconds)));
                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation.Cancel();
        ActivationRequested = null;
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The owning process is already shutting down; closing the handle is sufficient.
            }
        }

        _ownsMutex = false;
        _mutex?.Dispose();
        _mutex = null;
        _listenerCancellation.Dispose();
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream server = new(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                byte[] signal = new byte[1];
                int bytesRead = await server.ReadAsync(signal, cancellationToken);
                if (bytesRead == 1 && signal[0] == 1)
                {
                    try
                    {
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                    }
                    catch
                    {
                        // A UI activation failure must not terminate the IPC listener.
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
    }
}
