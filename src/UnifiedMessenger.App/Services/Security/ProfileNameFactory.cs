namespace UnifiedMessenger.App.Services.Security;

public static class ProfileNameFactory
{
    public static string Create(Guid serviceInstanceId) => $"service-{serviceInstanceId:N}";
}
