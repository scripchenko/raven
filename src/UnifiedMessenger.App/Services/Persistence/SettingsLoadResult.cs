using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Persistence;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    string? WarningMessage = null,
    string? CorruptedBackupPath = null,
    bool WasMigrated = false)
{
    public bool RecoveredFromCorruption => CorruptedBackupPath is not null;
}
