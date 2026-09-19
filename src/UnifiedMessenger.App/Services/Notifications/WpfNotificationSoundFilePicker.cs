using Microsoft.Win32;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class WpfNotificationSoundFilePicker : INotificationSoundFilePicker
{
    public string? SelectSoundFile()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "Поддерживаемые звуки (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma|WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|Windows Media Audio (*.wma)|*.wma",
            Multiselect = false,
            Title = "Выбрать звук raven"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
