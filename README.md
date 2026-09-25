# raven

[Русская версия](README.ru.md)

raven is a Windows desktop app that brings messaging services and email accounts into one window.

## Supported services

| Messaging through the official web app | Email |
| --- | --- |
| Telegram, WhatsApp, MAX, VK | Gmail, Yandex Mail, Mail.ru, other IMAP/SMTP accounts |

Messaging services run in Microsoft Edge WebView2. Gmail uses the Gmail API and Google OAuth in the system browser. Yandex Mail, Mail.ru and other supported mail servers use IMAP and SMTP. Available folders and mailbox actions depend on the mail server.

## What raven offers

- Multiple accounts, including multiple accounts for one service. Each web messenger account has its own persistent WebView2 profile.
- Mail folders, message reading, attachments, compose, replies and forwarding. Supported providers also offer server search, mailbox actions and saved drafts.
- Desktop notifications, account notification controls, a global Do Not Disturb mode and sound settings. Supported messenger notifications can use the bundled or a custom sound.
- A system tray icon, Home page and Settings. Closing the window can keep raven running in the tray.
- A check for newer stable GitHub releases. Updates are downloaded and installed by the user.
- External links opened in the system browser, subject to the app's navigation policy.

## Install raven v0.1.0

Download `raven-Setup-0.1.0-win-x64.exe` from the [official v0.1.0 release](https://github.com/scripchenko/raven/releases/tag/v0.1.0).

User requirements:

- Windows 10 version 1809 or newer, or Windows 11, on x64 hardware;
- Microsoft Edge WebView2 Evergreen Runtime.

The installer contains a self-contained .NET 10 build. Users do **not** need the .NET SDK or a separately installed .NET runtime. WebView2 Evergreen is a separate requirement and is not bundled.

The v0.1.0 installer is unsigned. Windows SmartScreen may warn on first launch. Check that the installer came from the official repository release; do not disable SmartScreen globally.

Gmail additionally requires a local Google Desktop OAuth client configuration supplied by the user; it is not bundled with the installer. See [Gmail setup](docs/gmail-setup.md) before adding a Gmail account. Other mail providers may require an app password and provider-specific IMAP/SMTP access.

## Local data and privacy

Settings and account identifiers live under `%APPDATA%\UnifiedMessenger`. WebView2 profiles, protected mail credentials and other local app data live under `%LOCALAPPDATA%\UnifiedMessenger`. These historical directory names are retained for compatibility with existing accounts and sessions.

Web messenger pages are displayed by WebView2. To provide email features, raven retrieves and processes mail content through Gmail API or IMAP/SMTP. Mail credentials are protected with Windows DPAPI for the current user; WebView2 maintains its own session data. See [Security and privacy](docs/security.md) for details.

## Support

[Contact @dscripchenko on Telegram](https://t.me/dscripchenko).

## License

This project is licensed under the [MIT License](LICENSE).

## Development

Building from source requires the .NET 10 SDK. Building the Windows installer also requires Inno Setup 6. From the repository root:

```powershell
dotnet build UnifiedMessenger.sln --configuration Release
dotnet test UnifiedMessenger.sln --configuration Release
./scripts/build-windows-package.ps1
```

The internal executable remains `UnifiedMessenger.App.exe`. See [architecture](docs/architecture.md), [Windows distribution](docs/windows-distribution.md) and [Gmail OAuth development setup](docs/gmail-oauth-development.md).
