# OAuth 2.0 — Overview

> OAuth 2.0 is a protocol for letting one app use another app's API *on your behalf*, without you ever handing over your password.

## The 30-second version

You want a payroll app to read your Google Calendar. The dumb solution is to give the payroll app your Google password. Now it can read your email, delete your Drive, and change your password. OAuth 2.0 replaces that with a redirect dance: Google asks *you* directly whether the payroll app may read your calendar, and if you say yes, it hands the payroll app a short-lived **access token** scoped to calendar-read only. Engineers care because this is how nearly every "Sign in with…", every third-party API integration, and every mobile-app-to-your-own-backend call is authorized today.

## The mental model

Think of an apartment building with a front desk.

You live in the building. A cleaning company wants access to your unit's gym storage locker. You do **not** give them your house key.

Instead you send them to the front desk. The desk clerk knows you — they ask you in person, "the cleaning company wants access to your gym locker, approve?" You say yes. The clerk prints a **keycard** that opens the gym locker, nothing else, and expires at 6pm today. The cleaning company holds the keycard. It never learns your key.

Map it:

| Building | OAuth |
|---|---|
| You | Resource owner |
| Cleaning company | Client (the app asking) |
| Front desk clerk | Authorization server |
| The locker | Resource server (the API) |
| Keycard | Access token |
| "Gym locker only" | Scope |
| Expires at 6pm | Token lifetime |
| Renew card without re-asking you | Refresh token |

```
User ──approves──> Authorization Server
  ▲                     │ issues token
  │ redirect            ▼
Client App ──token──> Resource API
```

The core insight: **the app never sees your credentials, and the token it does see is deliberately weak** — narrow scope, short life, revocable.

## What it is NOT

- **Not authentication.** OAuth answers "may this app do X?", not "who is this person?" For login, you want OpenID Connect, a thin identity layer built on top of OAuth that adds an `id_token`.
- **Not JWT.** JWT is a *token format*; OAuth is a *protocol for getting tokens*. An access token may be a JWT or an opaque random string — OAuth does not care. See `backend/jwt` for the format side.
- **Not SAML.** Same goal, older XML-based protocol, still common in enterprise SSO.
- **Not authorization logic.** OAuth delivers scopes; deciding whether `calendar.read` lets you see a specific event is still your API's job.

## When you would reach for it

- A third-party app needs to call an API on a user's behalf (the original use case).
- Your React SPA or mobile app calls your own backend and you want short-lived, revocable credentials.
- Service-to-service calls with no user involved — the `client_credentials` grant.
- You need per-integration revocation: kill one app's access without resetting anyone's password.

## When you would NOT reach for it

- A single monolith with its own users and no third parties — a session cookie is simpler and safer.
- Machine-to-machine inside one trust boundary where mTLS or a signed internal token already works.
- You actually need "who is the user" — reach for OpenID Connect instead of bolting identity onto raw OAuth.

## Key vocabulary

- **Resource owner** — the human who owns the data.
- **Client** — the app requesting access. *Confidential* if it can keep a secret (server), *public* if it cannot (SPA, mobile).
- **Authorization server** — issues tokens (Entra ID, Auth0, Cognito, Keycloak).
- **Resource server** — the API that validates tokens.
- **Access token** — the keycard. Short-lived, sent as `Authorization: Bearer <token>`.
- **Refresh token** — longer-lived, exchanged for new access tokens.
- **Scope** — the permission label, e.g. `calendar.read`.
- **Grant / flow** — the recipe for getting a token. Authorization Code + PKCE is the default for everything now.
- **PKCE** — a proof-of-possession trick that stops an intercepted authorization code from being redeemed by an attacker. Required for all clients in [OAuth 2.1](https://oauth.net/2.1/).
- **Redirect URI** — where the authorization server sends the user back. Must match exactly.

## What's next

`02-deep-dive.md` covers What / Where / When / How / Why in detail: each grant type step by step, the full Authorization Code + PKCE message flow, token validation on the resource server, and why the Implicit and Password grants were removed by [RFC 9700](https://datatracker.ietf.org/doc/rfc9700/).
