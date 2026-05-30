# JWT (JSON Web Token) — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, JWT is the thing the auth service hands out after login and that every downstream API, gateway, and sidecar verifies locally instead of calling the auth DB on every request. If you've ever added `[Authorize]` to a controller and configured `AddJwtBearer`, you've been a JWT verifier whether you realized it or not.

In a microservice mesh, JWT is the identity envelope. An API gateway (Kong, Envoy, AWS API Gateway) validates the token at the edge, then forwards it — or a re-signed internal version of it — to downstream services so each one can read `sub`, `tenant_id`, and `scope` without re-authenticating.

In federated and workload identity (OIDC logins, GitHub Actions deploying to AWS, Kubernetes service accounts), JWT is the *interchange format* between trust domains. The issuer publishes a public JWKS, the verifier caches it, and the token crosses an organizational boundary with nothing but a signature for trust.

The common thread: JWT is almost never the *interesting* part of a feature. It's the load-bearing piece underneath that quietly breaks audits, scaling, and on-call shifts when someone gets a detail wrong.

## Best practices

### 1. Use short-lived access tokens + opaque refresh tokens
**Do:** Issue access JWTs with `exp` of 5–15 minutes and pair them with a long-lived (days–weeks) refresh token that is opaque, random, and stored server-side in a `refresh_tokens` table with `user_id`, `family_id`, `expires_at`, and `revoked_at`. Rotate the refresh token on every use; if a previously-used one is presented again, revoke the whole family (probable theft).
**Why:** JWT has no native revocation. Keeping access tokens short means a stolen one is useful for minutes, not weeks. The refresh token *can* be revoked because it lives in your database.
**Avoid:** Long-lived (hours or days) access JWTs with no refresh flow — that's a bearer credential you cannot recall.

### 2. Pick the algorithm based on issuer/verifier trust
**Do:** Use `HS256` only when the same service that signs the token also verifies it (single binary, shared secret, no third party). Use `RS256` or `ES256` whenever issuer and verifier are different services, processes, or organizations. Prefer `ES256` for new systems — smaller signatures (~64 bytes vs ~256 for RS256), faster verification, equivalent security at smaller key sizes.
**Why:** With HS256, every verifier holds the signing secret. One compromised verifier forges tokens for the whole system. Asymmetric signing lets verifiers hold only the public key.
**Avoid:** HS256 with a secret shared across teams or environments — that secret will leak via a config file, a Docker image, or a Slack message.

### 3. Pin the `alg` to an allow-list on verify
**Do:** Hard-code the accepted algorithms in your verifier configuration and never let the token's header decide.

```js
jwt.verify(token, key, {
  algorithms: ['RS256'],   // allow-list, not "whatever the header says"
  issuer: 'https://auth.example.com',
  audience: 'api.example.com',
});
```

**Why:** The `alg: none` bypass and the classic RS256→HS256 confusion attack both depend on the verifier trusting the header. An allow-list shuts both down.
**Avoid:** Calling `verify(token, key)` with no `algorithms` option — many libraries used to default to "any", and some still do.

### 4. Always verify signature, `exp`, `iss`, and `aud` — in that order
**Do:** Run the full check on every request, even on internal services. Signature first (reject forgeries before parsing claims), then `exp` with 30–60 s leeway for clock skew, then `iss` exact-match against your expected issuer string, then `aud` contains your service's identifier.
**Why:** Skipping `aud` lets a token issued for service A be replayed against service B. Skipping `iss` lets a rogue IdP whose key you happen to trust mint valid tokens for you. Both are real CVE patterns.
**Avoid:** "It came through the gateway, so it's fine." Defense in depth: every hop verifies.

### 5. Fetch verification keys from a JWKS endpoint with `kid`-aware caching
**Do:** Resolve keys via the issuer's `/.well-known/jwks.json` (or the URL in its OIDC discovery document at `/.well-known/openid-configuration`). Use a library that caches the JWKS in memory and refreshes on an unknown `kid` (rate-limited, e.g. minimum 5 minutes between refetches). Set the `kid` header on every token you sign.
**Why:** Key rotation is when systems break. JWKS + `kid` is how rotation happens with zero downtime — the issuer publishes the new key alongside the old one, signs with the new one, verifiers pick it up on the next `kid` miss.
**Avoid:** Hard-coding the public key in config (someone forgets to rotate it), or refetching JWKS on every request (you'll DoS your own IdP).

### 6. Pick client-side storage with eyes open about XSS vs CSRF
**Do:** For browser apps, the safest default is **`httpOnly; Secure; SameSite=Lax` (or `Strict`) cookies** for the token, plus CSRF protection (double-submit cookie or per-request token) on state-changing endpoints. For SPAs that need to attach the token to cross-origin API calls, **in-memory storage of the access token + httpOnly refresh-token cookie** is the modern compromise.
**Why:** `localStorage` is readable by any JS that runs on the page; one XSS and every token is exfiltrated. Cookies are immune to that but reintroduce CSRF. There is no option without a trade-off — pick consciously.
**Avoid:** Putting the JWT in `localStorage` because "it's easier" without an XSS budget. And never put a JWT in a URL query string — it ends up in browser history, Referer headers, proxy logs, and CDN access logs.

### 7. Keep payloads small and free of PII
**Do:** Put `sub`, `iss`, `aud`, `exp`, `iat`, `jti`, and a *handful* of authorization-relevant claims (`scope`, `roles`, `tenant_id`). Anything bigger — full user profile, permission matrix — belongs behind a lookup the resource server does once and caches.
**Why:** Every request carries the token. Tokens past ~4 KB start hitting default header limits in Nginx, ALB, CloudFront (commonly 8 KB total request-header budget). PII in payload means a token leak is also a data-protection incident.
**Avoid:** Stuffing every permission into the token so "downstream doesn't have to look anything up." That decision is how you discover the 8 KB header limit at 2 AM.

### 8. Design revocation before you need it
**Do:** Decide upfront: short `exp` is your *primary* revocation strategy. For "user clicked logout / admin disabled account / token suspected stolen," add a `jti` deny-list in Redis with a TTL equal to the token's remaining lifetime — verifiers check it after signature. For "we rotated keys because something bad happened," rotating the signing key invalidates every token in flight.
**Why:** "We'll add revocation later" becomes "we have no way to log a user out" during an incident. Decide which scenarios you actually need to handle and which the short `exp` covers.
**Avoid:** A deny-list that every request hits unconditionally — you've reinvented sessions, more slowly, with worse semantics.

### 9. Separate secrets per environment, rotate signing keys on a schedule
**Do:** Different signing keys per environment (dev, staging, prod). Rotate signing keys on a schedule (quarterly is common) and immediately on suspected compromise. Keep the previous key in the JWKS during the overlap window so in-flight tokens still verify.
**Why:** A leaked staging key that also signs prod tokens is an instant production breach. Scheduled rotation forces the rotation machinery to actually work — keys you've never rotated are keys you can't rotate.
**Avoid:** One `.env` value reused across environments. One key signing tokens since 2019.

### 10. Know the standardized profiles you're actually implementing
**Do:** If you're issuing OAuth 2.0 access tokens as JWTs, follow [RFC 9068](https://www.rfc-editor.org/rfc/rfc9068.html) (`typ: at+jwt`, required claims, audience binding). If you're doing OIDC, the `id_token` rules from the [OIDC Core spec](https://openid.net/specs/openid-connect-core-1_0.html) apply — and the `id_token` is for the client, not for authorizing API calls. For sender-constrained tokens (token theft is in your threat model), look at [DPoP, RFC 9449](https://www.rfc-editor.org/rfc/rfc9449.html) or mTLS-bound tokens ([RFC 8705](https://www.rfc-editor.org/rfc/rfc8705.html)).
**Why:** Reinventing these profiles guarantees subtle interop bugs with Auth0, Okta, Entra ID, and every SDK that expects them.
**Avoid:** Using an OIDC `id_token` as an API access token. It's a common confusion that breaks audience semantics.

## Anti-patterns to recognize

- **"Just decode it to read the user id"**: Code calls `jwt.decode()` (no signature check) to extract `sub` and never adds the verify step later. Any attacker can mint a token with any `sub`. Always verify, then read claims — never the other way around.
- **Trusting the header `alg`**: Verifier picks the algorithm dynamically from `token.header.alg`. Enables `alg: none` and RS256→HS256 confusion. Fix: explicit allow-list in the verify call.
- **Storing passwords or full PII in claims**: Developer assumes "signed" means "encrypted." Anyone who captures the token reads it on jwt.io. Fix: claims hold IDs and authorization data only; lookups happen server-side.
- **Infinite-lived tokens**: `exp` set to a year, or no `exp` at all, "because refresh is annoying." A single stolen token = a year of access. Fix: short `exp` + refresh tokens, always.
- **Same signing key across environments**: A dev laptop's `.env` leaks the key that also signs production tokens. Fix: per-environment keys, rotated on a schedule.
- **JWTs in URLs**: Magic links, OAuth implicit-flow fragments, `?token=...` query parameters. Tokens leak via browser history, Referer headers, CDN access logs, and screenshot tooling. Fix: POST bodies or `Authorization` header; for magic links, use a short-lived single-use code that exchanges for a token over POST.
- **No `aud` enforcement in a multi-API system**: Same IdP issues tokens for multiple APIs; one API skips `aud` and accepts tokens minted for any of them. Fix: every resource server pins its own audience identifier and rejects mismatches.
- **Refresh tokens that never rotate**: Refresh token is reused indefinitely; theft is undetectable. Fix: rotate on every refresh and revoke the family if an old one reappears (refresh-token-rotation, OAuth 2.0 BCP, [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html)).

## Real-world usage patterns

**B2B SaaS API with tenant isolation.** Auth service issues `RS256` JWTs containing `sub`, `tenant_id`, `roles`, `scope`, with 15-minute `exp`. Every API service validates against the auth service's JWKS, enforces `aud=api.example.com`, and uses `tenant_id` to scope every database query. Refresh tokens are stored in Postgres with rotation on use.
*Non-obvious lesson:* the `tenant_id` claim is convenient, but role changes don't propagate until the access token expires. The product team needs to know "removing a user takes up to 15 minutes" or you need a deny-list path for instant revocation.

**Microservice mesh behind an API gateway.** Edge gateway (Envoy) validates the external customer JWT, then mints a short-lived *internal* JWT signed by the mesh CA and forwarded to downstream services. Internal services trust only the mesh CA's JWKS.
*Non-obvious lesson:* token re-minting at the edge is what lets you put `internal-only` claims (request ID, trace context, downstream-only roles) in the internal token without exposing them to the customer. The cost is one extra signing operation per request — measure it.

**CI/CD workload identity (GitHub Actions to AWS).** GitHub publishes a JWKS at `https://token.actions.githubusercontent.com/.well-known/jwks`. AWS STS verifies the OIDC token's signature, `iss`, `aud`, and a `sub` pattern like `repo:org/repo:ref:refs/heads/main` before assuming a role. No long-lived AWS keys involved.
*Non-obvious lesson:* the entire security model rests on the `sub` claim trust-policy condition. Writing `"StringLike": {"sub": "repo:org/*"}` instead of an exact match silently grants any repo in the org access to your prod role. JWT validation is correct; the *authorization rule on top of it* is what gets people.

**Mobile app with biometric re-auth.** Access JWT lives in memory only. Refresh token is stored in the OS keychain (iOS Keychain, Android Keystore), protected by biometrics. On app foreground, the app exchanges the refresh token for a fresh access JWT; biometric prompt gates refresh-token access.
*Non-obvious lesson:* keychain-backed refresh tokens are the closest thing to "device-bound" you get without DPoP. They survive app reinstalls badly and require care in the logout flow — clearing the keychain entry is the actual logout.

## Operational checklist

- **Monitoring:** Metrics for token-verification failures broken down by reason (`expired`, `bad_signature`, `wrong_aud`, `unknown_kid`). A spike in `bad_signature` is either a key rotation gone wrong or an attack.
- **Failure handling:** What happens if the JWKS endpoint is down? Is the cache stale-while-revalidate or fail-closed? Tested in a chaos drill?
- **Algorithm pinning:** Code review confirms every `verify` call passes an explicit `algorithms` allow-list. Grep your codebase for `jwt.decode(` without a verify nearby.
- **Audience and issuer pinning:** Every resource server has its own `aud` identifier and its issuer URL is configured, not inferred.
- **Token size:** p99 access-token size is below 4 KB, well under proxy header limits. Tracked as a metric, not just measured once.
- **Refresh-token storage:** Refresh tokens are hashed at rest (not stored plaintext), rotated on use, and family-revoked on reuse detection.
- **Key management:** Signing keys differ per environment. Rotation runbook exists and has been executed at least once in a non-emergency.
- **Logging hygiene:** No raw `Authorization` headers in logs. Grep request-logger middleware for `Bearer` redaction.
- **Onboarding:** A new engineer can find the issuer URL, the JWKS URL, the expected `aud`, and the token TTL in one place (README or `appsettings.json`).

## How this topic typically evolves in a codebase

Teams almost always start with HS256 and a shared secret because every tutorial does. It works fine until the second service needs to verify tokens and someone copies the secret into a second `.env` file. The migration to RS256/ES256 + JWKS usually happens after the first "we leaked a config file" incident or when the team adopts a hosted IdP (Auth0, Cognito, Entra ID) that issues asymmetric tokens by default.

The next inflection point is revocation. Early systems ship without it — short `exp` is "good enough" until support gets a ticket asking to immediately log out a compromised account and the answer is "wait 15 minutes." That's when teams either add a Redis deny-list keyed on `jti` or, more often, accept the window and document it. The painful migration is when long-lived access tokens (1 hour, 24 hours) need to be shortened — every client integration that doesn't handle refresh properly breaks at once.

Mature systems converge on: hosted IdP (or a hardened internal one), ES256 signing, JWKS with `kid` rotation on a schedule, 5–15 minute access tokens, rotating refresh tokens with family revocation, and either DPoP or mTLS-bound tokens for high-value APIs. The remaining work is mostly about *what claims* go in the token and *who* is allowed to add new ones — claim bloat is the long-term threat to a JWT system, not crypto.

## Further reading

- [RFC 9700 — Best Current Practice for OAuth 2.0 Security](https://www.rfc-editor.org/rfc/rfc9700.html) — the IETF's consolidated advice; the refresh-token rotation and sender-constraint sections are essential.
- [RFC 9068 — JWT Profile for OAuth 2.0 Access Tokens](https://www.rfc-editor.org/rfc/rfc9068.html) — the spec that turns "JWTs as access tokens" from convention into requirement; tells you exactly which claims must appear.
- [RFC 9449 — DPoP (Demonstrating Proof of Possession)](https://www.rfc-editor.org/rfc/rfc9449.html) — the modern answer to bearer-token theft; worth reading even if you don't implement it yet.
- [Auth0 — Refresh Token Rotation](https://auth0.com/docs/secure/tokens/refresh-tokens/refresh-token-rotation) — clean, concrete writeup of the rotation-with-family-revocation pattern.
- [OWASP — JSON Web Token Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/JSON_Web_Token_for_Java_Cheat_Sheet.html) — the attack-surface counterpart to the RFCs; covers the alg-confusion and `none` attacks at implementation level.
- [PortSwigger Web Security Academy — JWT Attacks](https://portswigger.net/web-security/jwt) — interactive labs for every classic JWT vulnerability; do them once and you will never write a vulnerable verifier.
