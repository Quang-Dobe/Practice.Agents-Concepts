# OAuth 2.0 — MVP Code

The smallest runnable demo of Authorization Code + PKCE. About 90 lines of actual code, comments excluded.
One process plays all four roles, so the whole redirect dance is visible without a browser.

## What it demonstrates

- The full Authorization Code + PKCE message flow from `../docs/02-deep-dive.md § How`: `code_verifier` → `S256` challenge → `/authorize` → code → `/token` → access token → API call.
- **PKCE binding**: a stolen authorization code redeemed with the wrong verifier is rejected (step 7).
- **Single-use codes**: replaying a spent code fails even with the correct verifier (step 6).
- Front-channel defences: exact `redirect_uri` matching, `state` for CSRF, `iss` for the mix-up attack; plus `aud` + `scope` checks on the resource server.

## Prerequisites

- .NET SDK **10.0+** (uses file-based apps — `dotnet run mvp.cs` with no `.csproj`).
- No packages, no database, no external authorization server. Binds `http://localhost:5099`.

## Run it

```bash
dotnet run mvp.cs
```

## Expected output

```
1. verifier WXZrEvYH_f...  ->  S256 challenge kanRwIasg4...
2. GET /authorize -> 302 redirect with code 7MAiDl3Lsd...
3. state + iss verified
4. POST /token -> access_token AepWWwD0nn... scope invoices.read
5. GET /api/invoices -> 200 [{"id":42,"total":199.00}]
6. replay same code -> 400 {"error":"invalid_grant","detail":"unknown or already-used code"}
7. stolen code, wrong verifier -> 400 {"error":"invalid_grant","detail":"PKCE verifier or redirect_uri mismatch"}
```

## What to try next

- Delete the `proof != grant.Challenge` check and watch step 7 succeed — that is a public client without PKCE.
- Change the requested `scope` to `invoices.write` and see the resource server return 403.
- Change `Audience` on the issued token to `"https://other.api"` and watch the same 403 — the confused-deputy guard.
- Replace `codes.TryRemove` with `codes.TryGetValue` and see step 6 hand out a second token for one code.
