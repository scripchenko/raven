namespace UnifiedMessenger.App.Services.WebView;

internal sealed class WebViewSessionSettlementTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<Task>> _settlements = [];

    public void Track(Guid serviceInstanceId, Task settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        if (settlement.IsCompleted)
        {
            ObserveFailure(settlement);
            return;
        }

        lock (_sync)
        {
            if (!_settlements.TryGetValue(serviceInstanceId, out List<Task>? serviceSettlements))
            {
                serviceSettlements = [];
                _settlements.Add(serviceInstanceId, serviceSettlements);
            }

            serviceSettlements.Add(settlement);
        }

        _ = settlement.ContinueWith(
            static completed => ObserveFailure(completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task WaitAsync(Guid serviceInstanceId, CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_sync)
        {
            if (!_settlements.TryGetValue(serviceInstanceId, out List<Task>? serviceSettlements))
            {
                return;
            }

            pending = serviceSettlements.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0)
            {
                _settlements.Remove(serviceInstanceId);
                return;
            }
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Initialization failure does not make a released controller live again.
            // Destructive cleanup only needs the work to have reached settlement.
        }
        finally
        {
            lock (_sync)
            {
                if (_settlements.TryGetValue(serviceInstanceId, out List<Task>? serviceSettlements))
                {
                    serviceSettlements.RemoveAll(task => task.IsCompleted);
                    if (serviceSettlements.Count == 0)
                    {
                        _settlements.Remove(serviceInstanceId);
                    }
                }
            }
        }
    }

    private static void ObserveFailure(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }
}
