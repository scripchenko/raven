# WebView2 sessions

The WebView layer hosts the official Telegram, WhatsApp, MAX and VK web applications. Each service account keeps a stable WebView2 profile under `%LOCALAPPDATA%\UnifiedMessenger\WebView2`; switching accounts does not recreate that profile. The internal storage name is retained for compatibility.

`WebViewSessionManager` manages controller lifetime, navigation, external browser handoff, permission requests and recovery from WebView process failures. Top-level navigation is checked against the selected service's allowed domains. Typed WebView2 notifications are used when supported by the installed Runtime; page titles provide a limited activity fallback. This is not guaranteed background push.

The module does not extract chat contents through DOM scripting. Notification title/body from WebView2 can be passed to the in-memory popup pipeline when preview is enabled. WebView2 itself maintains site cookies and local storage for signed-in sessions.

For the different data flow used by email, see [security.md](../../../../docs/security.md).
