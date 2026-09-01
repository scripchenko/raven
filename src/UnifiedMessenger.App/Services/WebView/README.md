# WebView2 module

Stage 6 hosts isolated Telegram, WhatsApp, MAX and VK Messenger sessions with direct `CoreWebView2Controller` instances and exposes safe activity and notification events through `IWebViewSessionManager`.

- `UserDataFolder` is shared at `%LOCALAPPDATA%\UnifiedMessenger\WebView2`.
- every `ProfileName` is derived once from its persisted service GUID and keeps authentication isolated;
- one shared `CoreWebView2Environment` creates exactly one direct controller per enabled account;
- enabled accounts are primed sequentially while the real `MainWindow` is hidden, selected account first and then the remaining user order;
- startup priming uses the existing `MainWindow` HWND and never creates a technical WPF host window, `HwndHost`, composition control or off-screen surface;
- switching changes controller bounds, visibility and focus without navigation, reload or profile recreation;
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

Telegram, WhatsApp and MAX can use the shared bundled Lantern sound from the typed `NotificationReceived` event or leave audio to the native web application. Sound deduplication uses only the service-instance id plus an in-memory hash of a non-empty notification tag; tag-less notifications use a short per-session debounce. The raw tag and notification content are never used by the sound decision. Lantern does not change web sound settings and never mutes WebView2, so voice messages, calls, video and other media remain unaffected.

WebView2 web notifications are not guaranteed background push. Stage 6 primes every enabled session before showing the main window so supported services can establish their notification pipeline without a visible service switch. If the installed Runtime does not expose `NotificationReceived`, the application continues with `DocumentTitleChanged` as a best-effort fallback. Services can change their title format at any time.

This module never injects JavaScript, analyzes the DOM, or reads cookies, page messages, contacts or credentials. Notification text is read only from the typed WebView2 notification event for the short-lived popup.
