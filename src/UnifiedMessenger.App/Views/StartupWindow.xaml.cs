using System.ComponentModel;
using System.Windows;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.Views;

public partial class StartupWindow : Window
{
    private bool _completed;
    private bool _exitRequested;

    public StartupWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? ExitRequested;

    public void UpdateProgress(StartupPrimeProgress progress)
    {
        ServiceNameText.Text = progress.DisplayName;
        ProgressText.Text = $"{progress.Current} из {progress.Total}";
        StartupProgressBar.Maximum = Math.Max(1, progress.Total);
        StartupProgressBar.Value = progress.Current;
    }

    public void CompleteAndClose()
    {
        _completed = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!_completed)
        {
            eventArgs.Cancel = true;
            RequestExit();
        }

        base.OnClosing(eventArgs);
    }

    private void Exit_Click(object sender, RoutedEventArgs eventArgs) => RequestExit();

    private void RequestExit()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
