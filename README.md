# TwitterBot

A .NET 10 background service that monitors X/Twitter mentions and watched accounts in real-time via the filtered stream API, and automatically replies using OAuth 2.0.

---

## How it works

| Concern | Mechanism |
|---|---|
| Reading / streaming tweets | Bearer Token → TwitterSharp filtered stream |
| Posting replies | OAuth 2.0 PKCE access token (auto-refreshed) |
| Refresh token rotation | Automatically persisted to `.refresh_token` |
| Secrets management | .NET user-secrets (never committed to source control) |

---

## 1. X/Twitter Developer Portal setup

### 1.1 Create a project and app

1. Go to [developer.x.com](https://developer.x.com) and sign in.
2. Navigate to **Projects & Apps → Overview → New Project**.
3. Give the project a name, select a use case, and create it.
4. Inside the project, create a new **App**.

### 1.2 Configure User Authentication Settings

1. Open your app → **Settings** tab → **User authentication settings** → **Edit**.
2. Set **App permissions** to **Read and Write**.
3. Enable **OAuth 2.0**.
4. Set **Type of App** to **Web App, Automated App or Bot**.
5. Set **Callback URI** to:
   ```
   http://localhost:3000/callback
   ```
6. Set **Website URL** to any valid URL (e.g. `https://example.com`).
7. Save.

> ⚠️ If you change permissions on an existing app, you must **regenerate** the Access Token and Secret — old tokens retain the old permission level.

### 1.3 Collect your credentials

From your app's **Keys and tokens** tab, collect the following:

| Portal label | Maps to | Where used |
|---|---|---|
| **Bearer Token** | `BearerToken` | Reading / streaming tweets |
| **Client ID** | `OAuth2ClientId` | OAuth 2.0 PKCE token flow |
| **Client Secret** | `OAuth2ClientSecret` | OAuth 2.0 PKCE token flow |

> The **OAuth 2.0 Refresh Token** (`OAuth2RefreshToken`) is generated during the one-time setup step below — it does not appear in the portal.

---

## 2. Application setup

### 2.1 Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2026 or any editor

### 2.2 Clone and restore

```powershell
git clone https://github.com/SmartQPtyLtd/TwitterBot
cd TwitterBot
dotnet restore
```

### 2.3 Configure appsettings.json

`appsettings.json` defines the non-secret, non-sensitive settings. All secret fields are left empty here and provided via user-secrets instead.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "TwitterBot": {
    "BearerToken": "",
    "OAuth2ClientId": "",
    "OAuth2ClientSecret": "",
    "OAuth2RefreshToken": "",
    "BotHandle": "YourBotHandle",
    "WatchUsernames": [ "someuser", "anotheruser" ],
    "BaseBackoffSeconds": 5,
    "MaxBackoffSeconds": 300,
    "ReplyMinJitterMs": 500,
    "ReplyMaxJitterMs": 6000
  }
}
```

| Setting | Description |
|---|---|
| `BotHandle` | Your bot's X username (without `@`) |
| `WatchUsernames` | Accounts whose new tweets trigger an automated reply |
| `BaseBackoffSeconds` | Initial reconnect backoff delay (seconds) |
| `MaxBackoffSeconds` | Maximum reconnect backoff ceiling (seconds) |
| `ReplyMinJitterMs` | Minimum random delay before posting a reply (ms) |
| `ReplyMaxJitterMs` | Maximum random delay before posting a reply (ms) |

### 2.4 Set secrets via user-secrets

User-secrets are stored outside the repository and are never committed.

```powershell
# Initialize user-secrets (one-time, if not already done)
dotnet user-secrets init

# Set credentials from the X Developer Portal
dotnet user-secrets set "TwitterBot:BearerToken"        "<your-bearer-token>"
dotnet user-secrets set "TwitterBot:OAuth2ClientId"     "<your-client-id>"
dotnet user-secrets set "TwitterBot:OAuth2ClientSecret" "<your-client-secret>"
```

> `OAuth2RefreshToken` is set automatically in the next step — do not set it manually yet.

Verify all secrets are saved:

```powershell
dotnet user-secrets list
```

### 2.5 Authorize the app (one-time OAuth 2.0 setup)

This step opens your browser, walks through the OAuth 2.0 PKCE flow, and saves the refresh token automatically.

```powershell
dotnet run -- --setup-oauth
```

1. Your browser will open the X authorization page.
2. Sign in as the bot account and click **Authorize**.
3. The browser redirects to `http://localhost:3000/callback` — the page will display **"Authorization complete! You can close this tab."**
4. Back in the terminal you will see:

```
=== SUCCESS ===
Scope: tweet.read tweet.write users.read offline.access
Access token expires in: 7200s

Run these commands to save the credentials:
  dotnet user-secrets set "TwitterBot:OAuth2RefreshToken" "<token>"
```

5. Run the `dotnet user-secrets set` command printed in the terminal to save the refresh token.

### 2.6 Run the bot

```powershell
dotnet run
```

Expected startup output:

```
info: TwitterBot.TwitterBotService[0]  Twitter bot starting.
info: TwitterBot.TwitterBotService[0]  Watching user @elonmusk (id: 44196397)
info: TwitterBot.TwitterBotService[0]  Watching user @x (id: 783214)
info: TwitterBot.TwitterBotService[0]  Adding 2 stream rule(s).
info: TwitterBot.TwitterBotService[0]  Stream connected. Listening for tweets...
```

---

## 3. Token refresh and rotation

Twitter access tokens expire after **2 hours**. The bot refreshes them automatically using the stored refresh token — no action is needed.

Twitter also **rotates** refresh tokens: each time the access token is refreshed, a new refresh token is issued and the old one is invalidated. The bot handles this automatically by persisting the new token to a local `.refresh_token` file (listed in `.gitignore`).

If the `.refresh_token` file is lost (e.g. after a clean `bin/` wipe), the bot falls back to the `OAuth2RefreshToken` value in user-secrets. If that token has also been rotated and is no longer valid, re-run the one-time setup:

```powershell
dotnet run -- --setup-oauth
```

---

## 4. Environment variables (production / CI)

In production, provide secrets via environment variables instead of user-secrets:

```
TwitterBot__BearerToken=...
TwitterBot__OAuth2ClientId=...
TwitterBot__OAuth2ClientSecret=...
TwitterBot__OAuth2RefreshToken=...
```

> .NET configuration maps `__` (double underscore) to `:` for nested keys.

---

## 5. Project structure

| File | Purpose |
|---|---|
| `Program.cs` | Host setup and `--setup-oauth` entry point |
| `TwitterBotService.cs` | Background service — stream loop, routing, reply dispatch |
| `TwitterApiHelper.cs` | Posts tweets via OAuth 2.0 PKCE with auto token refresh |
| `RefreshTokenStore.cs` | Persists rotated refresh tokens to `.refresh_token` |
| `OAuth2Setup.cs` | One-time PKCE authorization flow |
| `TwitterBotOptions.cs` | Strongly-typed configuration options |
| `appsettings.json` | Non-secret configuration |
| `.gitignore` | Excludes `bin/`, `obj/`, `.vs/`, `.refresh_token` |
