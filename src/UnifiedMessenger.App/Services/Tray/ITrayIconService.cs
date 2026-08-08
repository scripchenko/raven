namespace UnifiedMessenger.App.Services.Tray;

public interface ITrayIconService : IDisposable
{
    event EventHandler? OpenRequested;
    event EventHandler? DoNotDisturbToggleRequested;
    event EventHandler? ExitRequested;
    event EventHandler? BalloonClicked;
    event EventHandler? BalloonClosed;

    void Show(bool doNotDisturb, string toolTipText);
    void BeginShutdown();
    void SetDoNotDisturb(bool enabled);
    void SetToolTip(string text);
    bool TryShowBalloon(string title, string text, int timeoutMilliseconds = 5000);
}
