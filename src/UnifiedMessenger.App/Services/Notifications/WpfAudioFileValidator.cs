using System.IO;
using System.Windows.Media;
using UnifiedMessenger.App.Services.Tray;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WpfAudioFileValidator(IUiDispatcher uiDispatcher) : IAudioFileValidator
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> CanOpenAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        TaskCompletionSource<bool> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer? player = null;
        EventHandler? opened = null;
        EventHandler<ExceptionEventArgs>? failed = null;

        try
        {
            await uiDispatcher.InvokeAsync(
                () =>
                {
                    player = new MediaPlayer();
                    opened = (_, _) => completion.TrySetResult(true);
                    failed = (_, _) => completion.TrySetResult(false);
                    player.MediaOpened += opened;
                    player.MediaFailed += failed;
                    player.Open(new Uri(filePath, UriKind.Absolute));
                    return true;
                });

            return await completion.Task.WaitAsync(OpenTimeout, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or TimeoutException)
        {
            return false;
        }
        finally
        {
            if (player is not null)
            {
                await uiDispatcher.InvokeAsync(
                    () =>
                    {
                        if (opened is not null)
                        {
                            player.MediaOpened -= opened;
                        }

                        if (failed is not null)
                        {
                            player.MediaFailed -= failed;
                        }

                        player.Close();
                        return true;
                    });
            }
        }
    }
}
