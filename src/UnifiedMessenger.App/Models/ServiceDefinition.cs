namespace UnifiedMessenger.App.Models;

public sealed record ServiceDefinition(
    ServiceType ServiceType,
    string DisplayName,
    string Glyph,
    Uri? StartUri,
    bool IsWebViewService,
    IReadOnlyList<AllowedHostRule> AllowedHosts);

public sealed record AllowedHostRule(string Host, HostMatchMode MatchMode);

public enum HostMatchMode
{
    ExactOrSubdomain,
    ExactOnly
}
