using System.Windows;
using UnifiedMessenger.App.Views;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WpfNotificationPopupService : INotificationPopupService
{
    private const int MaximumVisiblePopups = 3;
    private const double ScreenMargin = 16;
    private const double PopupGap = 10;

    private readonly Dictionary<Guid, NotificationPopupWindow> _windows = [];
    private readonly List<Guid> _displayOrder = [];
    private bool _disposed;

    public event EventHandler<NotificationPopupEventArgs>? Clicked;
    public event EventHandler<NotificationPopupEventArgs>? Closed;

    public int VisibleCount => _windows.Count;

    public bool TryShow(NotificationPopupDisplayModel notification)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(notification);

        if (_windows.Count >= MaximumVisiblePopups || _windows.ContainsKey(notification.NotificationId))
        {
            return false;
        }

        try
        {
            NotificationPopupWindow window = new(notification);
            window.PopupClicked += OnPopupClicked;
            window.PopupClosed += OnPopupClosed;
            _windows.Add(notification.NotificationId, window);
            _displayOrder.Add(notification.NotificationId);
            window.Show();
            window.UpdateLayout();
            RepositionPopups();
            return true;
        }
        catch (InvalidOperationException)
        {
            RemoveWindow(notification.NotificationId);
            return false;
        }
    }

    public void Close(Guid notificationId)
    {
        if (_windows.TryGetValue(notificationId, out NotificationPopupWindow? window))
        {
            window.Close();
        }
    }

    public void CloseAll()
    {
        foreach (NotificationPopupWindow window in _windows.Values.ToArray())
        {
            window.Close();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseAll();
        _windows.Clear();
        _displayOrder.Clear();
    }

    private void OnPopupClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not NotificationPopupWindow window)
        {
            return;
        }

        Guid notificationId = window.NotificationId;
        RemoveWindow(notificationId);
        Clicked?.Invoke(this, new NotificationPopupEventArgs(notificationId));
        RepositionPopups();
    }

    private void OnPopupClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is not NotificationPopupWindow window)
        {
            return;
        }

        Guid notificationId = window.NotificationId;
        RemoveWindow(notificationId);
        Closed?.Invoke(this, new NotificationPopupEventArgs(notificationId));
        RepositionPopups();
    }

    private void RemoveWindow(Guid notificationId)
    {
        if (!_windows.Remove(notificationId, out NotificationPopupWindow? window))
        {
            return;
        }

        window.PopupClicked -= OnPopupClicked;
        window.PopupClosed -= OnPopupClosed;
        _displayOrder.Remove(notificationId);
    }

    private void RepositionPopups()
    {
        Rect workArea = SystemParameters.WorkArea;
        double bottomOffset = ScreenMargin;
        foreach (Guid notificationId in _displayOrder.AsEnumerable().Reverse())
        {
            if (!_windows.TryGetValue(notificationId, out NotificationPopupWindow? window))
            {
                continue;
            }

            double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            double height = window.ActualHeight > 0 ? window.ActualHeight : 120;
            window.Left = workArea.Right - width - ScreenMargin;
            window.Top = workArea.Bottom - height - bottomOffset;
            bottomOffset += height + PopupGap;
        }
    }
}
