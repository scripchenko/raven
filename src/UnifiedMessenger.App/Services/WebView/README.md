# WebView2 module

Stage 2 owns one Telegram WebView2 session through `IWebViewSessionManager`.

- `UserDataFolder` is shared at `%LOCALAPPDATA%\UnifiedMessenger\WebView2`.
- `ProfileName` is derived from the service GUID and keeps authentication isolated.
- top-level navigation is restricted to the catalog allow-list;
- external HTTP(S) and mail links use the Windows default handler;
- popup windows are handled explicitly and never create an uncontrolled second WebView2;
- runtime absence, navigation failures and process failures produce explicit UI states;
- a failed WebView2 process receives at most one automatic recreation attempt per app run.

This module never reads cookies, page messages, contacts or credentials.
