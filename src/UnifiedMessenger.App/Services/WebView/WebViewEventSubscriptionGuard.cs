namespace UnifiedMessenger.App.Services.WebView;

public sealed class WebViewEventSubscriptionGuard
{
    public bool IsSubscribed { get; private set; }

    public bool TrySubscribe()
    {
        if (IsSubscribed)
        {
            return false;
        }

        IsSubscribed = true;
        return true;
    }

    public bool TryUnsubscribe()
    {
        if (!IsSubscribed)
        {
            return false;
        }

        IsSubscribed = false;
        return true;
    }
}
