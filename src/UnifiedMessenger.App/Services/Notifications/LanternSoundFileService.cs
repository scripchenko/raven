using System.IO;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Persistence;

namespace UnifiedMessenger.App.Services.Notifications;

public sealed class LanternSoundFileService(
    IAppPaths appPaths,
    IAudioFileValidator audioFileValidator) : ILanternSoundFileService
{
    public string DefaultSoundPath { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, LanternSoundFilePolicy.DefaultSoundRelativePath));

    public async Task<LanternSoundImportResult> ImportAsync(
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            return LanternSoundImportResult.Failed("Файл звука не выбран.");
        }

        string fullSourcePath;
        try
        {
            fullSourcePath = Path.GetFullPath(sourceFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return LanternSoundImportResult.Failed("Путь к файлу звука некорректен.");
        }

        string extension = Path.GetExtension(fullSourcePath);
        if (!LanternSoundFilePolicy.IsSupportedExtension(extension))
        {
            return LanternSoundImportResult.Failed("Этот формат звука не поддерживается.");
        }

        try
        {
            FileInfo source = new(fullSourcePath);
            if (!source.Exists)
            {
                return LanternSoundImportResult.Failed("Выбранный файл не найден.");
            }

            if (source.Length <= 0)
            {
                return LanternSoundImportResult.Failed("Выбранный файл пуст.");
            }

            if (source.Length > LanternSoundFilePolicy.MaximumFileSizeBytes)
            {
                return LanternSoundImportResult.Failed("Файл звука превышает допустимый размер 10 МБ.");
            }

            await using (FileStream readable = new(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous))
            {
                _ = readable.Length;
            }

            if (!await audioFileValidator.CanOpenAsync(fullSourcePath, cancellationToken))
            {
                return LanternSoundImportResult.Failed("Windows не смог открыть выбранный аудиофайл.");
            }

            Directory.CreateDirectory(appPaths.NotificationSoundsFolder);
            string internalFileName = LanternSoundFilePolicy.CreateInternalFileName(extension);
            string destinationPath = Path.Combine(appPaths.NotificationSoundsFolder, internalFileName);
            string temporaryPath = Path.Combine(
                appPaths.NotificationSoundsFolder,
                $".custom-notification-{Guid.NewGuid():N}{extension}");
            try
            {
                File.Copy(fullSourcePath, temporaryPath, overwrite: false);
                if (!await audioFileValidator.CanOpenAsync(temporaryPath, cancellationToken))
                {
                    return LanternSoundImportResult.Failed("Внутренняя копия аудиофайла не прошла проверку.");
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            DeleteOtherInternalCopies(internalFileName);
            return new LanternSoundImportResult(
                true,
                internalFileName,
                LanternSoundFilePolicy.CreateSafeDisplayName(fullSourcePath));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException)
        {
            return LanternSoundImportResult.Failed("Не удалось безопасно сохранить выбранный звук.");
        }
    }

    public string ResolvePlaybackPath(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.LanternSoundSource is LanternSoundSource.Custom
            && LanternSoundFilePolicy.IsSafeInternalFileName(settings.CustomSoundInternalFileName))
        {
            string customPath = Path.Combine(
                appPaths.NotificationSoundsFolder,
                settings.CustomSoundInternalFileName!);
            if (File.Exists(customPath))
            {
                return customPath;
            }
        }

        return DefaultSoundPath;
    }

    public void DeleteInternalCopy(string? internalFileName)
    {
        if (!LanternSoundFilePolicy.IsSafeInternalFileName(internalFileName))
        {
            return;
        }

        string path = Path.Combine(appPaths.NotificationSoundsFolder, internalFileName!);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked custom sound is harmless; the setting already points to the bundled fallback.
        }
    }

    private void DeleteOtherInternalCopies(string retainedFileName)
    {
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(
                         appPaths.NotificationSoundsFolder,
                         "custom-notification.*",
                         SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(candidate), retainedFileName, StringComparison.OrdinalIgnoreCase)
                    || !LanternSoundFilePolicy.IsSafeInternalFileName(Path.GetFileName(candidate)))
                {
                    continue;
                }

                File.Delete(candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Obsolete internal copies can be retried on the next successful import.
        }
    }
}
