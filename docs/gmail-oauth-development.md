# Gmail OAuth development configuration

raven uses the Google OAuth 2.0 authorization-code flow for a Desktop application. Sign-in opens in the system browser, and a one-time local listener receives the response at `http://127.0.0.1:<random-port>/oauth2/callback/`.

The client configuration is read from `%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`. This local Google JSON must contain an `installed` object with `client_id` and `client_secret`. Never commit it, authorization codes, or tokens. The historical `credentials.example.json` in Git history was only a placeholder and cannot be used for real sign-in.

New Gmail connections in the current code request `https://www.googleapis.com/auth/gmail.modify`. Older credentials with `gmail.readonly` are prompted for broader access when an operation requires it. The current flow does not request `gmail.send` separately. Check the actual request, current code, and Google consent screen before shipping a new version.

The refresh credential and metadata needed to renew it are stored as a typed record protected by Windows DPAPI `CurrentUser`. The access token remains in memory. The Desktop OAuth client configuration is not included in the installer and is not encrypted by the application.

User-facing setup steps are in [gmail-setup.md](gmail-setup.md).
