namespace UnifiedMessenger.App.Services.Security;

public static class ProfileNameFactory
{
    public static string Create(Guid serviceInstanceId) => $"service-{serviceInstanceId:N}";

    public static bool IsValid(string? profileName)
    {
        const string prefix = "service-";
        return profileName is not null
            && profileName.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(profileName[prefix.Length..], "N", out _);
    }
}
