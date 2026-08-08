namespace UnifiedMessenger.App.Services.Tray;

public interface IApplicationExitCoordinator
{
    event EventHandler? ExitRequested;
    ApplicationShutdownState ShutdownState { get; }
    bool IsExiting { get; }
    bool IsExplicitExitRequested { get; }
    bool IsSessionEnding { get; }
    void RequestExit();
    bool TryBeginShutdown();
    void CompleteShutdown();
    void BeginSessionEnding();
    bool ShouldHideToTray(bool closeToTray, bool applicationShutdownStarted);
    bool ShouldRequestExitFromWindowClose(bool closeToTray, bool applicationShutdownStarted);
}

public enum ApplicationShutdownState
{
    Running,
    ExitRequested,
    ShuttingDown,
    Completed
}
