namespace UnifiedMessenger.App.Models;

public sealed record ServiceDefinition(
    ServiceType ServiceType,
    string DisplayName,
    string Glyph,
    Uri? StartUri,
    bool IsWebViewService,
    IReadOnlySet<string> AllowedHosts);
