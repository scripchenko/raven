using System.Runtime.InteropServices;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WebNotificationLifecycle(
    Action reportShown,
    Action reportClicked,
    Action reportClosed) : IWebNotificationLifecycle
{
    private int _state;

    public bool WasShown => Volatile.Read(ref _state) >= 1;
    public bool IsCompleted => Volatile.Read(ref _state) >= 2;

    public void ReportShown()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
        {
            TryReport(reportShown);
        }
    }

    public void ReportClicked()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
        {
            TryReport(reportClicked);
        }
    }

    public void ReportClosed()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
        {
            TryReport(reportClosed);
        }
    }

    public void CompleteSuppressed()
    {
        ReportShown();
        ReportClosed();
    }

    private static void TryReport(Action report)
    {
        try
        {
            report();
        }
        catch (COMException)
        {
            // The WebView may have closed between receiving and completing the notification.
        }
        catch (InvalidOperationException)
        {
            // Duplicate or late WebView notification completion must not crash the app.
        }
    }
}
