# Privacy policy

**Applies to:** raven for Windows

**Last updated:** 2026-09-26

Raven is a desktop client. It does not operate the messaging, mail, or WebView2 services that users connect to. Those providers process data under their own terms and privacy policies.

## Network connections and information

- **Messaging services:** Telegram, WhatsApp, MAX, and VK pages run in Microsoft Edge WebView2. When an account is opened, the relevant service receives the network requests and information that the user submits or that the service page requires. WebView2 keeps that service's session data in a local account profile.
- **Gmail:** When the user configures Gmail, raven uses Google OAuth in the system browser and connects to Google APIs for the requested mail operations. Google receives the authentication and API requests needed to provide those operations.
- **IMAP/SMTP mail:** raven connects to the IMAP and SMTP servers configured for the user's mail account. Credentials and mail data are sent to that selected provider as required to authenticate, read, manage, and send mail.
- **Update checks:** raven automatically requests public release metadata from `https://api.github.com/repos/scripchenko/raven/releases/latest` at startup, subject to a 24-hour local throttle. The request identifies raven and its version through the HTTP User-Agent. As with any Internet request, GitHub can see the connecting IP address and ordinary request metadata. Raven does not send mail-account identifiers, credentials, message contents, or settings in this request. The check does not download or install an update; the user chooses whether to open and run a release installer. A manual check is also available in Settings.
- **Remote images in mail:** automatic loading of remote mail images is enabled by default and can be turned off in Settings. When enabled, raven requests image URLs referenced by a message through its protected image loader. The remote host can see the request and IP address and may infer that the message was opened. The loader does not send WebView cookies or Windows default credentials. With automatic loading disabled, images require a user action where the UI offers that option.
- **External and support links:** links are opened in the system browser only after the user activates them.

## Local storage

- Application settings and account details are stored locally under `%APPDATA%\UnifiedMessenger`.
- WebView2 profiles and other application data are stored locally under `%LOCALAPPDATA%\UnifiedMessenger`.
- Mail passwords and Gmail OAuth credentials are protected with Windows DPAPI for the current user. Credentials are sent to the corresponding provider only when needed to authenticate.
- Gmail's Desktop OAuth client configuration is supplied by the user and stored locally; raven does not include it in the installer.
- Mail content is processed locally to provide mail features. Server-side messages and drafts remain with the selected mail provider. A protected local recovery copy may be used for a dirty IMAP draft that cannot be saved during shutdown.
- A startup failure may produce a local diagnostic file under `%LOCALAPPDATA%\UnifiedMessenger\Diagnostics`. It contains startup stage, local paths, exception messages, and stack traces. It is not automatically uploaded; review it for personal data before sharing it.

## Installation and system changes

The Windows installer runs for the current user, places raven in the user's local program directory, creates Start Menu and Desktop shortcuts, and registers the normal Windows uninstall entry. It does not change PATH or file associations. The installer checks for Microsoft Edge WebView2 Evergreen Runtime but does not install it; if the Runtime is missing, it offers to open Microsoft's download page for the user.

## Advertising, analytics, and sale of data

Raven contains no advertising or analytics telemetry service. The project does not sell user data. Network requests described above are made to the selected service providers, GitHub for update metadata, or remote image hosts when that feature is enabled.

## Third-party services

Each provider and the Microsoft Edge WebView2 platform has its own terms and privacy policy. Those providers control how they process information sent through their websites, OAuth endpoints, mail protocols, and runtimes. Review the policies of the services you choose before signing in or connecting an account:

- [Telegram](https://telegram.org/privacy)
- [WhatsApp](https://www.whatsapp.com/legal/privacy-policies)
- [MAX](https://legal.max.ru/pp)
- [VK ID and VK services](https://id.vk.com/privacy)
- [Google](https://policies.google.com/privacy)
- [Yandex](https://yandex.ru/legal/confidential/)
- [Mail](https://help.mail.ru/legal/terms/mail/privacy/)
- [Microsoft](https://www.microsoft.com/en-us/privacy/privacystatement)

## Contact

For privacy questions, contact [@dscripchenko on Telegram](https://t.me/dscripchenko).
