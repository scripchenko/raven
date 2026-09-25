using System.Text;
using System.IO;

namespace UnifiedMessenger.App.Services;

internal static class StartupDiagnostics
{
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnifiedMessenger",
        "Diagnostics",
        "startup-error.log");

    public static void TryWriteFailure(string stage, Exception exception)
    {
        try
        {
            string? directory = Path.GetDirectoryName(LogPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            StringBuilder text = new();
            text.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
            text.AppendLine($"Startup stage: {stage}");
            text.AppendLine($"AppContext.BaseDirectory: {AppContext.BaseDirectory}");
            text.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
            int depth = 0;
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                text.AppendLine($"Exception[{depth}].Type: {current.GetType().FullName}");
                text.AppendLine($"Exception[{depth}].Message: {current.Message}");
                text.AppendLine($"Exception[{depth}].StackTrace:");
                text.AppendLine(current.StackTrace ?? "<none>");
                depth++;
            }

            File.WriteAllText(LogPath, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception)
        {
            // Diagnostics must never replace or amplify the original startup failure.
        }
    }
}
