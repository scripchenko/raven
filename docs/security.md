# Security and privacy

This document describes how the current raven application handles data. It is not an independent security audit.

## Web messengers

Telegram, WhatsApp, MAX, and VK open at their official web addresses in Microsoft Edge WebView2. Their websites handle sign-in, QR codes, and conversations within WebView2. raven does not extract chat contents from the DOM or read cookies directly. WebView2 nevertheless keeps cookies and other site data in separate account profiles so sessions survive a restart.

raven receives technical events, page titles, and, when supported by the Runtime, web-notification titles and bodies from WebView2. These can drive activity indicators and popups. Notifications depend on the website and an active WebView2 session; they are not guaranteed background push notifications.

Top-level web-messenger navigation is restricted to the corresponding service's HTTPS domains. External links open in the system browser. Origin checks also apply to web-notification permission requests. These rules do not mean that third-party websites or the browser itself cannot process user data.

## Mail

Unlike web messengers, mail features are performed by the application itself. Gmail connects through the Gmail API and Google OAuth; Yandex Mail, Mail.ru, and other supported accounts connect through IMAP/SMTP. To list, search, read, attach, draft, send, and notify, raven retrieves and processes the necessary addresses, subjects, snippets, and message contents. The mail provider continues to store messages and server drafts under its own policies.

Mail HTML is sanitized before display. A restrictive Content Security Policy is applied and JavaScript is disabled. A setting controls remote image loading; requests to remote hosts can reveal that a message was opened and disclose the user's IP address. The image loader checks URLs, redirects, response types, and sizes, and does not send browser cookies or Windows default credentials.

## Local data and secrets

- `%APPDATA%\UnifiedMessenger\settings.json` contains ordinary settings and account details, including mail addresses. Passwords and OAuth refresh tokens should not be stored there in plaintext.
- `%LOCALAPPDATA%\UnifiedMessenger\WebView2` contains web-messenger profiles and their session data.
- Mail app passwords and the Gmail OAuth credential are stored locally under Windows DPAPI `CurrentUser` protection. Separate recovery for an unsaved IMAP draft also uses DPAPI.
- For Gmail, the user provides a local Google Desktop OAuth client JSON at `%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`. It is not in the installer, is not encrypted by the application, and must not be committed.
- Attachments explicitly saved by a user are written to the chosen location. Ordinary message data may reside in memory and cache while the application runs.

The historical `UnifiedMessenger` directory names are retained for compatibility with existing accounts and profiles. Uninstalling the application does not automatically remove these user-data directories.

## Notifications and diagnostics

Desktop notifications may display a sender, subject, and short preview. Preview, sound, and account notifications are configurable; Do Not Disturb suppresses popups and sound. Notification text is not intended to be stored in settings or technical logs. Diagnostics should exclude passwords, tokens, cookies, mail and chat text, recipients, and sensitive URL parameters.

Review diagnostic files and screenshots before sharing them: WebView2 and mail data may contain personal information.
