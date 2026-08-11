# OAuth 2.0 — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, OAuth is the thing sitting between your users' browsers and every API you own. There is an authorization server (usually Entra ID, Auth0, Okta, Cognito or Keycloak), a redirect-handling route in your web app, and a line of middleware in each service — `AddJwtBearer` in ASP.NET Core, `express-oauth2-jwt-bearer` in Node. Most of the OAuth code in a healthy codebase is configuration, not logic. When you find hand-written token parsing, that is usually where the bug is.

In a product with an integrations marketplace, OAuth is the load-bearing piece of your partner surface. Every "Connect to Slack / Salesforce / Google Drive" button is an authorization-code flow, and every one of those grants leaves a long-lived refresh token in someone's database. The Salesloft–Drift compromise of August 2025 was exactly this: stolen integration tokens gave attackers read access across [700+ downstream tenants](https://cloudsecurityalliance.org/blog/2025/09/25/the-salesloft-drift-oauth-supply-chain-attack-cross-industry-lessons-in-third-party-access-visibility), survived password resets, and never triggered an MFA prompt.

On the platform side, OAuth shows up as machine identity: `client_credentials` between services, GitHub Actions OIDC federating into AWS without static keys, and — new since 2025 — MCP servers acting as OAuth 2.1 resource servers for AI agents. Here the resource owner is absent, consent is a config file, and the failure mode is scope sprawl rather than phishing.

## Best practices

### 1. Use Authorization Code + PKCE for every interactive client
**Do:** One flow for SPAs, mobile, desktop, CLIs and confidential web apps. `code_challenge_method=S256`, never `plain`. Confidential clients still authenticate at `/token` — PKCE is additive, not a substitute.
**Why:** An intercepted code (custom URL-scheme hijack, referrer leak, browser history, proxy log) is worthless without the verifier. Without PKCE, the same leak is a full account takeover.
**Avoid:** Keeping an implicit-flow or `password`-grant code path alive "for the legacy mobile app" — both are removed in [OAuth 2.1](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-v2-1).

### 2. Register exact redirect URIs, and treat every open redirect on those hosts as a P1
**Do:** Full absolute strings, no wildcards, no path prefixes. Separate client registrations per environment instead of `https://*.dev.example.com/**`.
**Why:** An [academic study of 16 major IdPs](https://dl.acm.org/doi/fullHtml/10.1145/3627106.3627140) found 6 vulnerable to path confusion and 10 to parameter pollution in redirect matching. An open redirect anywhere on an allowed host converts into code exfiltration.
**Avoid:** Adding `http://localhost:3000` to the production client so developers can debug against prod.

### 3. Keep tokens out of the browser: use a BFF for SPAs
**Do:** Terminate the flow server-side; the browser gets an `HttpOnly; Secure; SameSite=Lax` session cookie, the BFF holds the tokens and proxies API calls. `oauth2-proxy`, Duende.BFF and Auth.js all implement this.
**Why:** The browser-based apps BCP is blunt: no browser storage survives malicious JavaScript. `localStorage` means one XSS in one npm dependency exfiltrates every token, including refresh tokens.
**Avoid:** "We store tokens in memory only, so we're fine" — in-memory tokens are still readable by any script in the page.

### 4. Rotate refresh tokens and detect reuse
**Do:** For public clients, rotate on every use and revoke the entire token family when a superseded token is presented. Verify your AS actually does the revocation, not just the reissue.
**Why:** Refresh tokens outlive sessions by days-to-months and bypass MFA. Reuse detection is the only signal that turns a silent theft into an alert.
**Avoid:** Non-expiring refresh tokens on integrations, then discovering during incident response that resetting passwords did nothing.

### 5. Validate `aud`, `iss` and `exp` on every resource server — separately per API
**Do:** Give each API its own audience identifier and reject anything else. Cache JWKS keyed by `kid` with a bounded negative-cache on miss.
**Why:** An RS that ignores `aud` accepts a token minted for a lower-trust sibling service. That is the confused deputy, and it converts one compromised low-value integration into access to your billing API.
**Avoid:** A single audience such as `api.example.com` shared by twelve services — you have no blast-radius boundary left.

### 6. Design scopes around business capabilities, not endpoints
**Do:** Roughly 5–20 stable scopes grouped by resource and action (`invoices.read`, `invoices.write`). Push per-object decisions ("can Alice edit document 42?") into a policy engine (OpenFGA, Cedar, OPA).
**Why:** Endpoint-shaped scopes make every new route a client-visible breaking change and blow past header size limits. Curity's [scope guidance](https://curity.io/resources/learn/scope-best-practices/) makes stability the primary criterion.
**Avoid:** Requesting the union of all scopes at first login "so we never have to re-prompt" — you have just made every stolen token maximally valuable.

### 7. Set access-token lifetimes deliberately and know your revocation story
**Do:** 5–15 minutes for JWT access tokens. If you need instant revocation, use opaque tokens plus introspection with a short cache, or accept a bounded revocation delay and document it.
**Why:** A JWT is valid until `exp` no matter what your admin console says. Teams routinely discover this during an incident, when "disable the user" does not stop the traffic.
**Avoid:** 24-hour access tokens because refresh handling was annoying to implement.

### 8. Authenticate confidential clients with keys, not shared secrets
**Do:** `private_key_jwt` ([RFC 7523](https://datatracker.ietf.org/doc/html/rfc7523)) or mTLS ([RFC 8705](https://datatracker.ietf.org/doc/html/rfc8705)) for server-side clients and `client_credentials`. Secrets that must exist go in a vault with a rotation runbook that has actually been rehearsed.
**Why:** A `client_secret` is a bearer credential in CI logs, container env vars and Slack threads. Asymmetric auth means nothing worth stealing ever leaves your service.
**Avoid:** One shared `client_id`/`client_secret` pair reused across all environments and services.

### 9. Send `state`, and check `iss` on the response
**Do:** `state` bound to the user's session (not a global nonce), single-use, validated before the code is redeemed. Validate the `iss` response parameter ([RFC 9207](https://datatracker.ietf.org/doc/html/rfc9207)) if you support more than one authorization server.
**Why:** Missing `state` is CSRF — an attacker's code lands in the victim's session, silently linking accounts. Missing `iss` with multiple ASes is the mix-up attack: your client posts a code to the wrong token endpoint and hands it to the attacker.
**Avoid:** Using `state` to carry the post-login return URL without also carrying CSRF entropy.

### 10. Treat third-party grants as inventory, with an expiry policy
**Do:** Store every grant with client, scopes, issue time, last use, and source IP range. Expire unused grants after a set window and alert on scope escalation at re-consent.
**Why:** This is the Salesloft–Drift lesson. Nobody knew which tokens existed or which had gone quiet, so ten days of exfiltration looked like normal integration traffic.
**Avoid:** Treating "user approved it once in 2023" as a permanent authorization.

## Anti-patterns to recognize

- **Tokens in `localStorage` with a CSP as the mitigation**: the SPA stores access and refresh tokens in web storage and the team points at a strict CSP. CSPs get relaxed for analytics, and a compromised dependency runs inside the origin regardless — move to a BFF or accept that XSS equals full account compromise.
- **The homegrown JWT validator**: someone decodes the token, reads `iss` from inside it, fetches that issuer's JWKS, and verifies. An attacker signs their own token and points `iss` at their own JWKS — pin the issuer and audience from configuration, never from the token.
- **OAuth-as-login**: the client calls `/userinfo` (or worse, `/api/me`) with an access token and treats the response as proof of identity. An access token minted for a different client is accepted, which is the [original confused-deputy login bug](https://oauth.net/articles/authentication/) — use OpenID Connect and validate the `id_token`'s `aud` and `nonce`.
- **Scope creep by re-consent**: each feature adds a scope to the login request, so version 12 asks for the union of everything. Users click through it, and the value of a stolen token grows monotonically — request scopes incrementally, in context.
- **The shared service identity**: one `client_credentials` client for the whole platform because per-service registration was tedious. Any compromised service can call any API as any other, and audit logs cannot attribute anything — one client per service, per environment.
- **Refresh-token rotation without reuse detection**: the AS issues a new refresh token each time but silently accepts the old one too. You get the operational cost of rotation with none of the theft detection — verify reuse triggers family revocation by testing it.
- **Revoking sessions but not tokens during incident response**: the runbook says "reset password, kill sessions." Refresh tokens and OAuth grants survive both, so the attacker keeps access — the runbook needs an explicit grant-revocation step.

## Real-world usage patterns

**Multi-tenant B2B SaaS with an integrations directory.** A mid-size product exposes 40+ inbound integrations, each holding a refresh token per tenant. The pattern that works: one client registration per integration partner, tenant-scoped tokens, and a per-partner kill switch that revokes every grant for that `client_id` in one call. The non-obvious lesson is that the kill switch must be tested during a quiet week — the first time you run it should not be during an incident, because it usually turns out the revocation endpoint is rate-limited.

**Mobile app plus API, 10k+ RPS.** AppAuth or MSAL performs Authorization Code + PKCE in the system browser (never a `WebView`), tokens live in Keychain / Keystore, the API gateway validates JWTs and forwards claims. Lesson: JWKS caching at the gateway is what keeps signing-key rotation from becoming an outage. Give the cache a stale-while-revalidate window; a hard TTL plus a rotated key produces a synchronized wave of 401s across every pod.

**Regulated fintech under FAPI 2.0.** PAR keeps request parameters off the front channel, sender-constrained tokens (DPoP or mTLS) make stolen tokens useless, and every client is registered with a public key. Lesson: DPoP's real cost is not signing, it is nonce handling and clock skew — budget for the retry loop when the AS issues `use_dpop_nonce`.

**Internal platform with service-to-service auth.** `client_credentials` with `private_key_jwt`, one client per service, audience-scoped tokens, and short lifetimes. Lesson: the hard part is not the OAuth, it is that services cache tokens badly — without a shared token cache with jittered pre-expiry refresh, you get a thundering herd on the token endpoint every 15 minutes, on the minute.

## Operational checklist

- **Monitoring**: are you alerting on token-endpoint error rate by `error` code, refresh-token reuse detections, consent grants with new scopes, and JWKS fetch failures?
- **Failure handling**: when the authorization server is down or its JWKS is unreachable, does the resource server fail closed, and has that been tested with a chaos run?
- **Failure handling**: does a second redemption of the same authorization code revoke the tokens already issued from it? Try it.
- **Security**: are redirect URIs exact-match, wildcard-free, and reviewed whenever a new one is added?
- **Security**: does each API reject a token minted for a sibling API? Mint one and confirm the 401/403.
- **Security**: is there a documented, rehearsed path to revoke a single integration's grants across all tenants?
- **Cost**: managed authorization servers price per monthly active user or per token; do short access-token lifetimes push you into a higher tier, and is `/introspect` cached?
- **Cost**: does the client cache access tokens with jittered refresh, or does every request hit `/token`?
- **Onboarding**: can a new engineer name your authorization server, the client registration for their service, the audience value, and where secrets live — on day one, from the README?

## How this topic typically evolves in a codebase

Teams almost always start with a managed provider's SDK and a single client registration. Login works in an afternoon, scopes are `openid profile email`, and the access token is treated as a session. This is fine, and it stays fine until the second API appears.

The first painful migration is audience separation. Once two services exist, that shared token becomes a confused deputy waiting to happen, and splitting audiences means touching every client, every gateway rule and every integration test at once. The second is the SPA token-storage migration: moving from `localStorage` to a BFF changes your deployment topology (you now need a stateful session store), your CORS and cookie configuration, and your multi-tab and WebSocket behaviour. Teams postpone it until a pen test forces the issue, which is the most expensive possible time.

The end state for a mature system looks like this: one authorization server, one client registration per application per environment, audience-scoped short-lived access tokens, rotated refresh tokens held server-side, scopes as coarse capabilities with a policy engine underneath for per-object rules, and a grant inventory with expiry. Getting there incrementally is possible — audience separation first, then token storage, then sender-constrained tokens if your threat model needs them.

## Further reading

- [RFC 9700 — OAuth 2.0 Security Best Current Practice](https://datatracker.ietf.org/doc/rfc9700/) — the single highest-value document here. Read the attack sections; they are the source of half this doc.
- [OAuth 2.0 for Browser-Based Applications](https://datatracker.ietf.org/doc/draft-ietf-oauth-browser-based-apps/) — the working group's reasoning on why SPAs should not hold tokens, with the BFF pattern spelled out.
- [Securing SPAs using the BFF Pattern](https://duendesoftware.com/blog/20210326-bff) — Duende's practical walkthrough of the same idea with the trade-offs stated honestly.
- [OAuth 2.0 Scope Best Practices](https://curity.io/resources/learn/scope-best-practices/) — Curity on granularity, naming, and where scopes stop scaling.
- [The Salesloft–Drift OAuth supply-chain attack](https://cloudsecurityalliance.org/blog/2025/09/25/the-salesloft-drift-oauth-supply-chain-attack-cross-industry-lessons-in-third-party-access-visibility) — the current canonical case study in third-party grant risk.
- [FAPI 2.0 Security Profile](https://openid.net/specs/fapi-security-profile-2_0-final.html) — worth reading even outside finance; it shows what "OAuth with the safeties on" looks like.
