# WebView2 module

Stage 4 keeps the lazy, isolated Telegram, WhatsApp, MAX and VK Messenger sessions and adds safe activity and notification events through `IWebViewSessionManager`.

- `UserDataFolder` is shared at `%LOCALAPPDATA%\UnifiedMessenger\WebView2`.
- every `ProfileName` is derived once from its persisted service GUID and keeps authentication isolated;
- a WebView2 control is created only when its account is first selected;
- top-level navigation is restricted to the per-service catalog allow-list;
- external HTTP(S) and mail links use the Windows default handler;
- popup windows are handled explicitly and never create an uncontrolled second WebView2;
- runtime absence, navigation failures and process failures produce explicit UI states;
- a failed WebView2 process receives at most one automatic recreation attempt per session;
- deleting an account clears only its profile; a locked directory is queued for retry on the next startup.
- `DocumentTitleChanged` publishes only the account identity and document title to the best-effort leading-count parser;
- notification permission is handled only for `Notifications`, after validating the requesting origin with the service allow-list, and the decision is persisted in the stable WebView2 profile;
- typed `NotificationReceived` events are handled when the installed Runtime supports them and all subscriptions are removed when a session is released;
- notification event models carry the account identity, sender origin, in-memory title/body and guarded lifecycle; title/body are never persisted or logged and are removed before UI dispatch when preview is disabled.

Telegram notification sounds are produced by UnifiedMessenger from the typed `NotificationReceived` event using a short Windows system sound and a one-second cooldown. To avoid duplicate notification audio, users should disable Telegram Web's built-in notification sound manually. UnifiedMessenger does not change that web setting and never mutes WebView2, so voice messages, calls, video and other media remain unaffected.

WebView2 web notifications are not guaranteed background push. They are available only for created, running sessions; an account that has never been opened may have no active session. If the installed Runtime does not expose `NotificationReceived`, the application continues with `DocumentTitleChanged` as a best-effort fallback. Services can change their title format at any time.

This module never injects JavaScript, analyzes the DOM, or reads cookies, page messages, contacts or credentials. Notification text is read only from the typed WebView2 notification event for the short-lived popup.
