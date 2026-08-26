using System.Drawing;
using UnifiedMessenger.App.Services.Branding;
using Forms = System.Windows.Forms;

namespace UnifiedMessenger.App.Services.Tray;

public sealed class WinFormsTrayIconService : ITrayIconService
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _contextMenu;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _settingsItem;
    private readonly Forms.ToolStripMenuItem _doNotDisturbItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Icon _applicationIcon;
    private bool _disposed;

    public WinFormsTrayIconService()
    {
        _applicationIcon = BrandIconResources.LoadApplicationIcon();
        _openItem = new Forms.ToolStripMenuItem("Открыть");
        _settingsItem = new Forms.ToolStripMenuItem("Настройки");
        _doNotDisturbItem = new Forms.ToolStripMenuItem("Не беспокоить") { CheckOnClick = false };
        _exitItem = new Forms.ToolStripMenuItem("Выход");
        _contextMenu = new Forms.ContextMenuStrip();
        _contextMenu.Items.AddRange(
            [_openItem, _settingsItem, _doNotDisturbItem, new Forms.ToolStripSeparator(), _exitItem]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = BrandIdentity.DisplayName,
            ContextMenuStrip = _contextMenu,
            Visible = false
        };

        _openItem.Click += OnOpenClicked;
        _settingsItem.Click += OnSettingsClicked;
        _doNotDisturbItem.Click += OnDoNotDisturbClicked;
        _exitItem.Click += OnExitClicked;
        _notifyIcon.DoubleClick += OnOpenClicked;
        _notifyIcon.BalloonTipClicked += OnBalloonClicked;
        _notifyIcon.BalloonTipClosed += OnBalloonClosed;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? DoNotDisturbToggleRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? BalloonClicked;
    public event EventHandler? BalloonClosed;

    public void Show(bool doNotDisturb, string toolTipText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetDoNotDisturb(doNotDisturb);
        SetToolTip(toolTipText);
        _notifyIcon.Visible = true;
    }

    public void SetDoNotDisturb(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _doNotDisturbItem.Checked = enabled;
    }

    public void SetToolTip(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    public bool TryShowBalloon(string title, string text, int timeoutMilliseconds = 5000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_notifyIcon.Visible || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        _notifyIcon.ShowBalloonTip(
            Math.Clamp(timeoutMilliseconds, 1000, 30000),
            title,
            text,
            Forms.ToolTipIcon.Info);
        return true;
    }

    public void BeginShutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _exitItem.Enabled = false;
        _contextMenu.Close(Forms.ToolStripDropDownCloseReason.CloseCalled);
        _notifyIcon.Visible = false;
        _openItem.Click -= OnOpenClicked;
        _settingsItem.Click -= OnSettingsClicked;
        _doNotDisturbItem.Click -= OnDoNotDisturbClicked;
        _exitItem.Click -= OnExitClicked;
        _notifyIcon.DoubleClick -= OnOpenClicked;
        _notifyIcon.BalloonTipClicked -= OnBalloonClicked;
        _notifyIcon.BalloonTipClosed -= OnBalloonClosed;
        _notifyIcon.Icon = null;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _applicationIcon.Dispose();
    }

    public void Dispose() => BeginShutdown();

    private void OnOpenClicked(object? sender, EventArgs eventArgs) => OpenRequested?.Invoke(this, EventArgs.Empty);
    private void OnSettingsClicked(object? sender, EventArgs eventArgs) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnDoNotDisturbClicked(object? sender, EventArgs eventArgs) => DoNotDisturbToggleRequested?.Invoke(this, EventArgs.Empty);
    private void OnExitClicked(object? sender, EventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        EventHandler? handler = ExitRequested;
        BeginShutdown();
        handler?.Invoke(this, EventArgs.Empty);
    }
    private void OnBalloonClicked(object? sender, EventArgs eventArgs) => BalloonClicked?.Invoke(this, EventArgs.Empty);
    private void OnBalloonClosed(object? sender, EventArgs eventArgs) => BalloonClosed?.Invoke(this, EventArgs.Empty);
}
