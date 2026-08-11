# OAuth 2.0 — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

OAuth 2.0 is a **delegated authorization framework** defined by [RFC 6749](https://datatracker.ietf.org/doc/html/rfc6749) (October 2012) and [RFC 6750](https://datatracker.ietf.org/doc/html/rfc6750) (bearer token usage). It specifies how a *client* obtains a scoped, time-limited *access token* from an *authorization server* (AS), with the *resource owner's* consent, and presents it to a *resource server* (RS).

The word **framework** is doing real work. RFC 6749 leaves token format, client authentication method, scope semantics, and token lifetimes to the deployment. Two conforming OAuth servers can be mutually unusable. Everything since 2012 has been narrowing that space: [RFC 9700 / BCP 240](https://datatracker.ietf.org/doc/rfc9700/) (January 2025) is the security best current practice, and [OAuth 2.1](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-v2-1) — at `draft-ietf-oauth-v2-1-15`, published 2 March 2026 — folds RFC 6749, RFC 6750, PKCE and the BCP into one document and deletes the unsafe parts.

### The core building blocks

- **Four roles** — resource owner, client, authorization server, resource server. The client is *confidential* (can hold a secret) or *public* (cannot: SPA, mobile, desktop, CLI).
- **Two endpoints that matter** — `/authorize` on the **front channel** (browser redirects, attacker-visible, no client authentication possible) and `/token` on the **back channel** (server-to-server POST over TLS, client authenticates).
- **Grants** — OAuth 2.1 defines exactly three: `authorization_code`, `client_credentials`, `refresh_token`. Everything else is an extension: device grant ([RFC 8628](https://www.rfc-editor.org/rfc/rfc8628.html)), token exchange ([RFC 8693](https://datatracker.ietf.org/doc/html/rfc8693)), CIBA (OpenID).
- **PKCE** ([RFC 7636](https://datatracker.ietf.org/doc/html/rfc7636)) — the client generates a random `code_verifier` (43–128 unreserved characters) and sends `code_challenge = BASE64URL(SHA-256(verifier))` with `code_challenge_method=S256`. The `plain` method exists and should never be used.
- **Token formats** — opaque random string validated by introspection ([RFC 7662](https://datatracker.ietf.org/doc/html/rfc7662)), or JWT with `typ: at+jwt` per [RFC 9068](https://www.rfc-editor.org/rfc/rfc9068.html). OAuth itself is format-agnostic.
- **Discovery metadata** — `/.well-known/oauth-authorization-server` ([RFC 8414](https://datatracker.ietf.org/doc/html/rfc8414)) tells clients the endpoints and `jwks_uri`; `/.well-known/oauth-protected-resource` ([RFC 9728](https://datatracker.ietf.org/doc/html/rfc9728)) tells clients which AS guards a given API.
- **Hardening extensions** — PAR ([RFC 9126](https://datatracker.ietf.org/doc/html/rfc9126)), DPoP ([RFC 9449](https://datatracker.ietf.org/doc/html/rfc9449)), mTLS-bound tokens ([RFC 8705](https://datatracker.ietf.org/doc/html/rfc8705)), RAR ([RFC 9396](https://datatracker.ietf.org/doc/html/rfc9396)), issuer identification ([RFC 9207](https://datatracker.ietf.org/doc/html/rfc9207)).

### How it relates to the broader landscape

OAuth 2.0 belongs to the family of **browser-redirect-based delegation protocols**. Its predecessor OAuth 1.0a ([RFC 5849](https://datatracker.ietf.org/doc/html/rfc5849)) signed every request with HMAC; SAML 2.0 does the same job with XML assertions and browser form POSTs, and still dominates enterprise SSO. OpenID Connect sits *on top* of OAuth 2.0 and adds an `id_token` plus a `/userinfo` endpoint — it is not an alternative, it is a profile. GNAP ([RFC 9635](https://datatracker.ietf.org/doc/html/rfc9635)) is a clean-sheet redesign with negligible deployment; OAuth 2.1 won by being an incremental cleanup instead.

## Where

### Where it runs / lives in the stack

Application layer, over HTTPS, split across two channels with different threat models. Concretely you touch OAuth in four places:

1. **The authorization server** — a separate service (or SaaS), holding user credentials, sessions, consent records, client registrations and signing keys.
2. **The client's redirect handler** — a route in your app that receives `?code=` and exchanges it. In a BFF architecture this is server-side.
3. **The resource server's auth middleware** — `AddJwtBearer` in ASP.NET Core, `express-oauth2-jwt-bearer` in Node, Spring Security's resource server. Validates signature, `iss`, `aud`, `exp`, `scope`.
4. **The gateway** — Envoy `ext_authz`, Kong, Azure APIM and AWS API Gateway commonly validate tokens before the request ever reaches your service.

### Where you typically encounter it

- Every major API: Google, Microsoft Graph, GitHub, Slack, Stripe Connect, Salesforce connected apps.
- Mobile apps using AppAuth-iOS/Android or MSAL against Entra ID.
- SPAs talking to your own backend — increasingly through a BFF rather than as a public client.
- Kubernetes API server with `--oidc-issuer-url`, and CI systems using GitHub Actions OIDC to federate into AWS/Azure without static keys.
- **MCP servers**: the Model Context Protocol authorization spec requires the server to act as an OAuth 2.1 resource server and to publish RFC 9728 protected-resource metadata.

### Ecosystem and tooling

- **Self-hosted authorization servers** — Keycloak, Ory Hydra, Duende IdentityServer (commercial; IdentityServer4 is end-of-life), FusionAuth, Curity.
- **Managed** — Entra ID, Auth0, Okta, AWS Cognito, Google Identity Platform.
- **Client / RS libraries** — `Microsoft.AspNetCore.Authentication.JwtBearer` and `.OpenIdConnect` (.NET), `openid-client` (Node), Authlib (Python), Spring Security, AppAuth (mobile).
- **BFF and proxying** — Duende.BFF, `oauth2-proxy`, Auth.js, Envoy/Kong OIDC filters.
- **Profiles and conformance** — [FAPI 2.0 Security Profile](https://openid.net/specs/fapi-security-profile-2_0-final.html), final since February 2025, and the OpenID Foundation conformance suite.

## When

### When the topic emerged and why

Before 2007 the standard way to let Site B read your Site A data was to hand Site B your Site A password and let it scrape. Flickr Auth, Google AuthSub and Yahoo BBAuth were incompatible one-off fixes. OAuth 1.0 (2007, revised to 1.0a in 2009) unified them using per-request HMAC signatures — cryptographically sound, but implementers spent their lives debugging signature base-string canonicalization.

OAuth 2.0 traded that signature for **TLS plus a bearer token**: any party holding the token can use it, and transport security is assumed to protect it. That trade made adoption explode and prompted lead author Eran Hammer to resign and publish *"OAuth 2.0 and the Road to Hell"* in 2012, arguing the spec had become an unopinionated framework. The following 13 years proved both sides right: adoption is universal, and the spec needed continuous patching — PKCE (2015), device grant (2019), sender-constrained tokens (2020–2023), RFC 9700 (2025), OAuth 2.1 (in progress).

### When to use it in a project

Reach for it when:

- A party outside your trust boundary needs API access on a user's behalf.
- Your client is public (SPA, mobile, desktop, CLI) and cannot hold a secret.
- Multiple APIs share one identity provider and you want one login and one revocation point.
- You need per-integration revocation, scoped credentials, and a consent audit trail.
- Service-to-service calls need rotatable credentials — `client_credentials` with `private_key_jwt` or mTLS beats a shared static API key.
- A regulator or partner requires a profile (FAPI 2.0, open banking) — those are defined on top of OAuth.

### When NOT to use it

Avoid it when:

- You have one app, one user store, no third parties. A signed HTTP-only session cookie is fewer moving parts and fewer failure modes.
- Both ends are inside one trust boundary and you already run mTLS/SPIFFE.
- You only need identity. Use OpenID Connect; raw OAuth access tokens carry no reliable statement about who the user is.
- Your authorization is per-object ("can Alice edit document 42?"). Scope strings do not scale to that — pair OAuth with a policy engine (OpenFGA, Cedar, OPA).
- The device has no browser and you cannot use the device grant. There is no safe redirect-free flow left; the password grant is gone.

## How

### How it works under the hood

Authorization Code + PKCE, end to end:

```
Browser (front channel, untrusted)      Client backend (back channel, TLS)
   │  1. GET /authorize?...S256           │
   ▼                                      │
Authorization Server ── login + consent ──┤
   │  2. 302 ?code=&state=&iss=           │
   ▼                                      │
Redirect handler ─────────────────────────► 3. POST /token (code + code_verifier)
                                          ◄── access_token (+refresh, +id_token)
                                          │
                                          └─► 4. GET /api  Authorization: Bearer …
                                                    Resource Server validates
```

1. Client generates `code_verifier`, derives `code_challenge`, generates `state`, and stores both against the user's session.
2. Redirect to `/authorize` with `response_type=code`, `client_id`, exact `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method=S256`. With PAR, these parameters are POSTed to `/par` first and the redirect carries only a short-lived `request_uri`, keeping them off the front channel.
3. The AS authenticates the human (session cookie, password + MFA, passkey), renders consent, and binds the challenge to the issued code.
4. Redirect back with `code`, `state`, and `iss`. The code is single-use and short-lived — RFC 6749 caps the recommendation at 10 minutes, and real servers issue 30–60 seconds.
5. Client POSTs to `/token` with `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `code_verifier`, plus client authentication if confidential (`client_secret_basic`, `private_key_jwt`, or mTLS).
6. The AS checks `SHA-256(code_verifier) == stored challenge`, exact-string `redirect_uri` match, and code-not-yet-used. A second redemption of the same code must revoke the tokens already issued from it.
7. The client calls the RS with `Authorization: Bearer …`. The RS validates a JWT locally against the AS's JWKS (cached by `kid`), checking `iss`, `aud`, `exp`, `nbf` and `scope` — or calls `/introspect` for opaque tokens.
8. On expiry the client uses the refresh token. For public clients OAuth 2.1 requires refresh tokens to be either sender-constrained or **rotated** on each use. Presenting a superseded refresh token is treated as theft: the AS revokes the entire token family.

### Key trade-offs

| Choice | You gain | You give up |
|---|---|---|
| Bearer tokens | Trivial to issue and verify; works everywhere | Anyone who steals the token is you — no proof of possession |
| DPoP (RFC 9449) | Token bound to a client key; theft alone is useless | Per-request signing, key management, nonce/replay handling |
| mTLS-bound tokens (RFC 8705) | Strongest binding, transport layer | X.509 lifecycle, TLS termination you control; unusable in browsers |
| JWT access tokens | Stateless validation, no network hop per request | Cannot revoke before `exp`; claims go stale; larger headers |
| Opaque + introspection | Instant revocation, small tokens | One extra round trip per call unless you cache |
| Short access tokens (5–15 min) | Small blast radius | More refresh traffic and more load on the AS |
| Scopes | Simple, interoperable, cacheable in a JWT | Coarse; explodes combinatorially for per-object rules (RAR helps) |
| BFF for SPAs | Tokens never enter JavaScript | A stateful server component and a session store |
| Managed AS | Conformance, MFA, threat detection for free | Vendor lock-in on claims, extensibility limits, per-MAU cost |

### Common failure modes

- **Wildcard or loosely matched `redirect_uri`** — an open redirect on any allowed host exfiltrates the code. OAuth 2.1 mandates exact string matching for this reason.
- **Missing `state`** — CSRF: an attacker's code gets injected into the victim's session, linking the victim's client session to the attacker's account.
- **PKCE omitted on a public client** — an intercepted code (custom URL scheme hijack, referrer leak) is redeemable by anyone.
- **Mix-up attack** with more than one configured AS — the client sends the code to the wrong token endpoint. Fixed by validating the `iss` response parameter (RFC 9207).
- **Tokens in `localStorage`** — one XSS and every token leaves the browser. The browser-based apps BCP is blunt: no browser storage mechanism is safe from malicious JavaScript.
- **RS ignores `aud`** — a token minted for service A is accepted by service B. Classic confused deputy.
- **JWKS handling bugs** — no `kid`-based cache invalidation causes mass 401s on key rotation; fetching keys from an issuer-controlled URL taken out of the token itself lets an attacker sign their own tokens.
- **Over-broad consented scopes on a machine integration** — the Salesloft-Drift compromise (August 2025) turned stolen OAuth tokens into data theft across 700+ Salesforce tenants; refresh tokens survived password resets and never triggered an MFA prompt.

## Why

### Why it exists

Because credential sharing does not compose. A password is an unscoped, non-expiring, non-revocable, non-auditable capability over an entire account. OAuth replaces it with capabilities that are **scoped, expiring, revocable and attributable** — and it moves the authentication event to the only party that should ever see the credential, the authorization server. Everything else in the spec is machinery to move a capability across a browser redirect without letting an attacker steal it in transit.

### Why it looks the way it does

The redirect dance looks baroque until you name the alternative: the client collects the user's password and forwards it (the `password` grant). That is simpler, needs no browser, and reintroduces exactly the problem OAuth exists to solve — plus it cannot support MFA, passkeys, or step-up. RFC 9700 removed it, and OAuth 2.1 does not define it.

The deeper design call was OAuth 1.0's per-request signatures versus OAuth 2.0's bearer tokens. Signatures give proof of possession for free but made every client implementation a canonicalization exercise. OAuth 2.0 pushed that responsibility down to TLS and bought a decade of adoption with it. The bill arrived as token theft, and the answer — DPoP and mTLS binding — is proof of possession reintroduced as an *opt-in layer* rather than a mandatory tax. That is the pattern to internalise: OAuth 2.x optimises for a working baseline that any team can ship, then layers rigour for those who need it.

### Why it matters now

Three forces in 2026. First, token theft is the dominant identity attack vector — adversary-in-the-middle proxies harvest post-MFA session and refresh tokens, which is why FAPI 2.0 (final February 2025) mandates sender-constrained tokens and PAR, and why DPoP is moving from exotic to expected. Second, OAuth 2.1 is close enough to done that new systems should be built to it now; the removed flows are already gone from every serious provider. Third, AI agents made OAuth a front-line concern again: the MCP authorization spec puts every MCP server in the resource-server role with RFC 9728 discovery, and "which scopes did the user actually grant this agent" is now a design question, not a compliance checkbox.

## Open questions / things to verify in practice

- What are the actual default access-token and refresh-token lifetimes on the AS I use, and can I shorten them without breaking clients? Vendor defaults vary by an order of magnitude.
- Does my resource server really reject a token with the wrong `aud`? Mint one for a sibling API and try it.
- Does refresh-token rotation on my AS include **reuse detection** that revokes the whole family, or does it just issue a new token silently?
- How does my JWKS cache behave during a signing-key rotation — do I get a thundering herd on `jwks_uri`, or a wave of 401s?
- For my SPA: measure the cost of moving to a BFF versus staying a public client with in-memory tokens. What breaks — CORS, cookie `SameSite`, multi-tab, WebSockets?
- Can my AS issue DPoP-bound tokens today, and what does the per-request signing cost look like on mobile?
