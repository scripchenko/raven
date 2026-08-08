namespace UnifiedMessenger.App.Services.Tray;

public sealed class ApplicationExitCoordinator : IApplicationExitCoordinator
{
    private readonly object _gate = new();

    public event EventHandler? ExitRequested;

    public ApplicationShutdownState ShutdownState { get; private set; }
    public bool IsExiting => ShutdownState is not ApplicationShutdownState.Running;
    public bool IsExplicitExitRequested { get; private set; }
    public bool IsSessionEnding { get; private set; }

    public void RequestExit()
    {
        EventHandler? handler;
        lock (_gate)
        {
            if (ShutdownState is not ApplicationShutdownState.Running)
            {
                return;
            }

            IsExplicitExitRequested = true;
            ShutdownState = ApplicationShutdownState.ExitRequested;
            handler = ExitRequested;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    public bool TryBeginShutdown()
    {
        lock (_gate)
        {
            if (ShutdownState is not ApplicationShutdownState.ExitRequested)
            {
                return false;
            }

            ShutdownState = ApplicationShutdownState.ShuttingDown;
            return true;
        }
    }

    public void CompleteShutdown()
    {
        lock (_gate)
        {
            if (ShutdownState is ApplicationShutdownState.ShuttingDown)
            {
                ShutdownState = ApplicationShutdownState.Completed;
            }
        }
    }

    public void BeginSessionEnding()
    {
        lock (_gate)
        {
            IsSessionEnding = true;
            if (ShutdownState is ApplicationShutdownState.Running)
            {
                ShutdownState = ApplicationShutdownState.ShuttingDown;
            }
        }
    }

    public bool ShouldHideToTray(bool closeToTray, bool applicationShutdownStarted) =>
        closeToTray
        && !IsExiting
        && !IsSessionEnding
        && !applicationShutdownStarted;

    public bool ShouldRequestExitFromWindowClose(bool closeToTray, bool applicationShutdownStarted) =>
        !closeToTray
        && !IsExiting
        && !IsSessionEnding
        && !applicationShutdownStarted;
}
