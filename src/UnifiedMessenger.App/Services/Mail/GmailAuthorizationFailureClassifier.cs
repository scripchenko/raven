using System.Net;
using Google;
using Google.Apis.Auth.OAuth2.Responses;

namespace UnifiedMessenger.App.Services.Mail;

internal static class GmailAuthorizationFailureClassifier
{
    private static readonly HashSet<string> AuthorizationFailureReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "authError",
        "invalidCredentials"
    };

    public static bool RequiresReauthorization(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is TokenResponseException)
        {
            return true;
        }

        if (exception is not GoogleApiException apiException)
        {
            return false;
        }

        if (apiException.HttpStatusCode is HttpStatusCode.Unauthorized)
        {
            return true;
        }

        return apiException.HttpStatusCode is HttpStatusCode.Forbidden
            && apiException.Error?.Errors?.Any(error =>
                !string.IsNullOrWhiteSpace(error.Reason)
                && AuthorizationFailureReasons.Contains(error.Reason)) == true;
    }
}
