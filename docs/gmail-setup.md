# Connect Gmail to raven

raven accesses Gmail through the Gmail API. Google sign-in opens in the system browser and returns to the application through a local loopback address. You do not enter your Google password in raven.

The Google Desktop OAuth client configuration is **not bundled** with the v0.1.0 installer. To connect Gmail, provide your own client configuration:

1. Configure a Google Cloud project with the Gmail API enabled and an OAuth consent screen.
2. Create an OAuth client of type **Desktop application** and download its JSON configuration.
3. Save the JSON as `%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`. It must contain an `installed` object with `client_id` and `client_secret`. Do not add the real file to Git or public reports.
4. In raven, choose to add a Gmail mail account and complete sign-in and consent in the system browser.

For a new connection, the current code requests the Gmail `gmail.modify` scope for supported mail and draft operations. An existing account with read-only access may need renewed consent. Do not grant access unless you agree to the permissions shown by Google.

The refresh credential is stored locally under Windows DPAPI `CurrentUser` protection; the access token is used while the app runs. raven does not separately encrypt the local OAuth client JSON, which remains necessary for later authorization. For technical details, see [gmail-oauth-development.md](gmail-oauth-development.md).
