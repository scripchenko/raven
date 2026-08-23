# Gmail OAuth development configuration

UnifiedMessenger uses the Google OAuth 2.0 authorization-code flow for a **Desktop app** client.
Authorization opens in the system browser and returns to a one-shot IPv4 loopback listener at
`http://127.0.0.1:<random-port>/oauth2/callback/`.

The downloaded Google Desktop client configuration is local-only and must be stored at:

`%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`

This file, authorization codes, tokens, and DPAPI credential blobs must never be committed. The
application requests only `https://www.googleapis.com/auth/gmail.readonly`. Refresh tokens and the
client metadata required to refresh them are stored as a typed credential protected with Windows
DPAPI `CurrentUser`; access tokens remain in memory.

For an External OAuth consent screen with publishing status **Testing**, Google normally issues a
refresh token that expires after seven days when Gmail scopes are requested. This is a Google
testing-mode limitation rather than an application credential-storage failure.
