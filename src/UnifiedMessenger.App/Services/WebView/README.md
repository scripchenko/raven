# WebView2 module

Stage 3 owns lazy, isolated Telegram, WhatsApp, MAX and VK Messenger sessions through `IWebViewSessionManager`.

- `UserDataFolder` is shared at `%LOCALAPPDATA%\UnifiedMessenger\WebView2`.
- every `ProfileName` is derived once from its persisted service GUID and keeps authentication isolated;
- a WebView2 control is created only when its account is first selected;
- top-level navigation is restricted to the per-service catalog allow-list;
- external HTTP(S) and mail links use the Windows default handler;
- popup windows are handled explicitly and never create an uncontrolled second WebView2;
- runtime absence, navigation failures and process failures produce explicit UI states;
- a failed WebView2 process receives at most one automatic recreation attempt per session;
- deleting an account clears only its profile; a locked directory is queued for retry on the next startup.

This module never reads cookies, page messages, contacts or credentials.
