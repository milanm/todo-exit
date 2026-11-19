# Register todo-exit in Microsoft Entra

The web app and the command-line tool sign users in with the Microsoft identity platform through one app registration. The official build uses the todo-exit registration (client ID `a9d144de-2371-43eb-b285-c98db87814cb`). A fork, or anyone who hosts the web app somewhere else, needs their own registration and client ID: Microsoft only redirects back to addresses registered on that app. This takes about five minutes and is free.

## 1. Create the registration

1. Sign in to the Microsoft Entra admin center at https://entra.microsoft.com (or the Azure portal, "Microsoft Entra ID").
2. Go to **Identity > Applications > App registrations** and choose **New registration**.
3. Name: `todo-exit` (any name works; users see it on the consent screen).
4. Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**. Personal accounts are the main audience, so this option is required.
5. Redirect URI: choose the platform **Single-page application (SPA)** and enter
   `https://localhost:<port>/authentication/login-callback`
   where `<port>` is the HTTPS port from `src/TodoExit.Web/Properties/launchSettings.json`.
6. Choose **Register**.

## 2. Add the production redirect URI

When the app is hosted, open **Authentication** on the registration and add a second SPA redirect URI:

`https://<your-domain>/authentication/login-callback`

If the app is hosted under a sub-path, include it. For the GitHub Pages site of this repository that is:

`https://milanm.github.io/todo-exit/authentication/login-callback`

Signing out returns to `.../authentication/logout-callback` under the same address. You can add that as a front-channel logout URL or a redirect URI too; sign-in works without it.

On the same page, leave **Implicit grant and hybrid flows** unchecked (no access tokens, no ID tokens). The SPA platform uses the authorization code flow with PKCE.

## 3. Allow public client flows (for the command-line tool)

The command-line tool signs in with the device code flow: it shows a web address and a code, and the user signs in in any browser. Microsoft only allows that for registrations that permit public client flows.

1. Open **Authentication** on the registration.
2. Under **Advanced settings**, set **Allow public client flows** to **Yes**.
3. Choose **Save**.

Without this, the tool stops with "This app registration does not allow sign-in from a command-line tool" (Microsoft answers `invalid_client`, AADSTS70002 or AADSTS7000218). The web app does not need this setting.

## 4. Add the permission

1. Open **API permissions** and choose **Add a permission**.
2. Choose **Microsoft Graph > Delegated permissions**.
3. Select **Tasks.Read** and choose **Add permissions**.

`User.Read` is usually added by default. You can keep it or remove it; todo-exit does not need it. Do not add `Tasks.ReadWrite`. todo-exit only reads.

No admin consent is needed for personal accounts. Each user consents to `Tasks.Read` when they sign in for the first time. Work or school tenants may require an administrator to approve the app.

## 5. Put the client ID into the app

1. Open **Overview** and copy the **Application (client) ID**.
2. Edit `src/TodoExit.Web/wwwroot/appsettings.json` and set your client ID:

```json
{
  "AzureAd": {
    "Authority": "https://login.microsoftonline.com/common",
    "ClientId": "<your application (client) id>",
    "ValidateAuthority": true
  }
}
```

Keep the authority on `common` so both personal and work accounts can sign in.

3. Run `dotnet run --project src/TodoExit.Web` and open the HTTPS address. The **Sign in with Microsoft** button appears once the client ID is no longer the placeholder.

For the command-line tool, pass `--client-id <your client id>` or set `TODOEXIT_CLIENT_ID`.

The client ID is not a secret. A single-page application has no client secret, so do not create one.
