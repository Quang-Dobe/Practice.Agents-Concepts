# JWT (JSON Web Token) — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A JWT is a compact, URL-safe representation of a set of *claims* — a JSON object — that has been cryptographically protected by either a JSON Web Signature (JWS) or a JSON Web Encryption (JWE). The format and processing rules are defined in [RFC 7519](https://www.rfc-editor.org/rfc/rfc7519.html); the signing layer is [RFC 7515](https://www.rfc-editor.org/rfc/rfc7515.html); the algorithm registry is [RFC 7518](https://datatracker.ietf.org/doc/html/rfc7518) (JWA); the key format is [RFC 7517](https://datatracker.ietf.org/doc/html/rfc7517) (JWK). Together those four specs are usually called **JOSE** (JavaScript Object Signing and Encryption).

In practice, "JWT" almost always means a **JWS in Compact Serialization** form: three base64url-encoded segments separated by dots.

### The core building blocks

- **JOSE header** — a JSON object describing how the token is protected. Required member: `alg` (algorithm). Common optional members: `typ` ("JWT"), `kid` (key identifier), `jku` (JWKS URL), `cty` (content type for nested JWTs).
- **Claims set (payload)** — a JSON object of name/value pairs about the subject. RFC 7519 splits claim names into three buckets:
  - **Registered** (IANA-reserved, three letters): `iss` (issuer), `sub` (subject), `aud` (audience), `exp` (expiration, NumericDate seconds since epoch), `nbf` (not before), `iat` (issued at), `jti` (unique JWT ID).
  - **Public** — names registered in the IANA "JSON Web Token Claims" registry or collision-resistant (e.g. a URI you control).
  - **Private** — any other name agreed between parties (`role`, `tenant_id`, …). No collision guarantee.
- **Signature** — bytes produced by running the chosen `alg` over the *signing input*. For JWS Compact:

  ```
  signing_input = BASE64URL(UTF8(header)) || "." || BASE64URL(payload)
  signature     = SIGN(alg, key, ASCII(signing_input))
  jwt           = signing_input || "." || BASE64URL(signature)
  ```

  Base64url is base64 with `+`→`-`, `/`→`_`, and no `=` padding (RFC 4648 §5).

- **JWE (rarely used)** — encrypts the payload using key-wrapping + AEAD. Compact form has *five* segments (header, encrypted key, IV, ciphertext, auth tag). If confidentiality matters, you need JWE, not JWS.

### How it relates to the broader landscape

JWT is one concrete format inside the JOSE family, which competes with PASETO (versioned, "no algorithm choice" tokens), Macaroons (caveat-based, third-party-attenuable), and classic opaque bearer tokens backed by a server-side store. Inside the auth-protocol layer, JWT is *one possible token format* used by OAuth 2.0 access tokens (formalized by [RFC 9068](https://www.rfc-editor.org/rfc/rfc9068.html), "JWT Profile for OAuth 2.0 Access Tokens") and the *mandatory* format for OpenID Connect ID tokens.

## Where

### Where it runs / lives in the stack

JWT sits at the **application / auth layer**, transported over HTTP (or gRPC metadata, or message-queue headers). Issuance happens at an identity provider or auth service; verification happens at every resource server / API gateway / sidecar that needs to authorize a request. The token itself is stateless data — it lives wherever the client puts it.

### Where you typically encounter it

- **OAuth 2.0 access tokens** issued by Auth0, Okta, Keycloak, AWS Cognito, Azure AD / Entra ID, Google Identity. With the RFC 9068 profile they are JWTs by spec.
- **OIDC `id_token`** — always a JWT, used to communicate authenticated user identity to the relying party.
- **Service-to-service auth in microservices** — sidecar meshes (Istio, Linkerd) and API gateways (Kong, Envoy) validate JWTs at the edge so downstream services trust pre-authenticated requests.
- **Kubernetes service account tokens** — projected service-account tokens are JWTs signed by the API server, verifiable via its JWKS.
- **GitHub Actions OIDC**, **AWS IAM Roles for Service Accounts (IRSA)**, **HashiCorp Vault JWT auth** — workload identity federation, all JWT under the hood.
- **Magic-link and password-reset flows** — short-lived single-use JWTs in URLs (with caveats — see *Failure modes*).

### Ecosystem and tooling

- **For issuing / verifying in code**: `Microsoft.IdentityModel.Tokens` and `System.IdentityModel.Tokens.Jwt` (.NET), `jjwt` and Nimbus JOSE+JWT (Java), `jsonwebtoken` and `jose` (Node.js), `PyJWT` and `authlib` (Python), `golang-jwt/jwt` (Go).
- **For hosted identity**: Auth0, Okta, Clerk, WorkOS, AWS Cognito, Azure Entra ID, Keycloak (self-hosted).
- **For inspection / debugging**: jwt.io (paste-and-decode), `jwt-cli`, Burp Suite JWT Editor.
- **For attacking (know your enemy)**: `jwt_tool`, PortSwigger Web Security Academy labs.

## When

### When the topic emerged and why

JWT was first drafted in late 2010 and standardized as RFC 7519 in May 2015, alongside the rest of the JOSE suite. It grew out of two pressures: SAML assertions were XML-heavy and painful for browsers/mobile, and OAuth 2.0 (RFC 6749, 2012) deliberately left token format unspecified, creating a vacuum. JWT filled it with a compact, JSON-native, URL-safe alternative that fit naturally into `Authorization: Bearer` headers and OIDC's redirect-based flows.

### When to use it in a project

Reach for it when:
- You have a **horizontally scaled, stateless API** and want to avoid a shared session store.
- You operate a **federated identity** boundary — issuer and verifier are different services, possibly different organizations.
- Tokens are **short-lived** (minutes, not days), so the inability to revoke instantly is acceptable.
- You need a **workload identity** primitive (Kubernetes, CI/CD OIDC, mTLS-adjacent auth).
- Downstream services need to read identity *claims* (roles, tenant, scopes) without calling back to the IdP.

### When NOT to use it

Avoid it when:
- You need **immediate revocation on logout** (banking, admin consoles, anything compliance-driven). A server-side session with a one-line `DELETE FROM sessions WHERE id = ?` is simpler and correct.
- The token must live **for days or weeks**. Long-lived JWTs are essentially un-revocable bearer credentials.
- Your app is a **classic monolith** with a single web tier. Cookies + a session table is less code, fewer footguns, and easier to debug.
- You want to put **confidential data** in the token. JWS is signed, not encrypted; reach for JWE or just don't put the data in.

## How

### How it works under the hood

A signed JWT round-trip:

1. **Client authenticates** to the authorization server (password, refresh token, authorization code, client credentials, …).
2. **Server constructs claims**: `{ "iss": "https://auth.example.com", "sub": "user_123", "aud": "api.example.com", "exp": 1735689600, "iat": 1735686000, "jti": "8f3...", "scope": "read:orders" }`.
3. **Server constructs header**: `{ "alg": "RS256", "typ": "JWT", "kid": "2026-01-key" }`. The `kid` lets verifiers pick the right key during rotation.
4. **Server base64url-encodes** the header and payload, joins with `.`, signs the result with the private key.
5. **Server returns** the three-segment JWT to the client (typically as an OAuth 2.0 access token).
6. **Client stores** the token and sends it on every API call as `Authorization: Bearer <jwt>`.
7. **Resource server validates** the token. The validation order matters:
   1. Parse and split on `.`; reject if not exactly three segments.
   2. Read the header. **Enforce an allow-list of `alg` values** locally — never trust the header's `alg` alone.
   3. Look up the verification key by `kid` from a cached JWKS (`GET https://auth.example.com/.well-known/jwks.json`). On `kid` miss, refetch JWKS once (rate-limited, typically 5–10 min minimum interval).
   4. Verify the signature over `header_b64 + "." + payload_b64`.
   5. Check `exp` > now (with a small clock skew, e.g. 30–60 s).
   6. Check `nbf` ≤ now, if present.
   7. Check `iss` matches the expected issuer string exactly.
   8. Check `aud` contains this server's audience identifier.
   9. Apply application-level authorization on `scope` / `roles` / custom claims.

   Any failure → 401. Skipping any one of these checks is a CVE waiting to happen.

8. **Token expires.** The client uses a long-lived **refresh token** (opaque, stored server-side, revocable) to obtain a new access JWT. This is the standard mitigation for JWT's weak revocation story.

### Key trade-offs

| Choice | Gain | Give up |
|---|---|---|
| HS256 (HMAC-SHA-256, symmetric) | Fast, simple, one secret | Every verifier holds the signing secret; one compromise forges any token. Only viable when issuer == verifier. |
| RS256 / ES256 (asymmetric) | Verifiers only need the public key; safe to publish via JWKS; clean issuer/verifier separation | Slower signing, larger signatures, more key management. ES256 is smaller and faster than RS256 at equivalent security. |
| Stateless JWT | No session store, no shared DB, horizontal scale trivial | Revocation is hard; token bloat in every request; payload visible to client. |
| Short `exp` (5–15 min) + refresh token | Limits the blast radius of a stolen token | Extra round trip to refresh; refresh token itself becomes the long-lived secret. |
| Putting roles/claims in the token | Downstream services skip an IdP call per request | Stale data — role changes don't propagate until the token expires. |

### Common failure modes

- **`alg: none` accepted** — the spec lists "none" as a valid algorithm; a naive verifier that honors the header skips signature verification entirely. Cause: trusting `header.alg` instead of an allow-list.
- **HS256/RS256 algorithm confusion** — server is configured for RS256 but its verifier picks the algorithm from the header. Attacker sends `alg: HS256` and signs the forged token using the server's **public** key as the HMAC secret. The server, holding the same public key, verifies it as a valid HMAC. Full auth bypass. Cause: dynamic algorithm selection from header.
- **Weak HMAC secret** — `HS256` with a short or human-chosen secret is brute-forceable offline once you have any valid token. Cause: secret < 256 bits of entropy.
- **Missing `aud` check** — a token issued for service A is replayed against service B that shares the issuer. Cause: verifier skipped the audience claim.
- **Missing `iss` check** — token from a rogue issuer with a key the verifier happens to trust is accepted. Cause: JWKS configured for multiple issuers without binding.
- **Sensitive data in payload** — SSN, email, internal IDs leaked because the developer assumed "signed" meant "encrypted." Cause: misunderstanding JWS vs JWE.
- **JWT in URL** — magic links and OAuth implicit-flow fragments end up in browser history, Referer headers, and server access logs. Cause: convenience over hygiene.
- **Oversized tokens** — packing every permission as a claim → 4–8 KB tokens → blown HTTP header limits and cache poisoning. Cause: using JWT as a permissions database.
- **Clock skew rejection** — verifier with a fast clock rejects freshly issued tokens. Cause: no `leeway` configured.
- **No `kid` → wrong key tried** — during rotation, verifier picks the old key for a new token. Cause: missing `kid` or no JWKS refresh on miss.

## Why

### Why it exists

JWT exists because **distributed systems need a way for one service to trust an assertion made by another service without calling back to the source on every request**. The pre-JWT options were (a) shared session stores (couples services, becomes the bottleneck), (b) SAML (XML, heavyweight, browser-hostile), or (c) opaque tokens with introspection (network round trip per request). A signed self-describing token collapses identity propagation to a local cryptographic check — that is the entire value proposition.

### Why it looks the way it does

Three design choices are worth understanding:

- **Why three base64url segments instead of a binary format like CBOR/COSE?** JOSE optimized for the HTTP/browser world circa 2013: URL-safe, header-safe, copy-pasteable, JSON-debuggable. CBOR Web Tokens (CWT, RFC 8392) exist for constrained environments like IoT — same idea, binary encoding.
- **Why asymmetric signing as a first-class option?** Because the most important deployment is "one issuer, many verifiers in different trust domains." With RS256/ES256, the issuer publishes a JWKS at `/.well-known/jwks.json`; anyone can verify without ever holding a secret. This is what makes OIDC and workload identity federation work.
- **Why short expiry + refresh tokens instead of a real revocation list?** Because a real revocation list re-introduces the stateful lookup JWT was designed to avoid. The compromise: tokens are short enough that the revocation window is acceptable, and the refresh token (which *is* stateful and revocable) carries the long-lived trust. It is an explicit, named trade-off, not an accident.

### Why it matters now

As of 2026, JWT is the dominant access-token format across OAuth 2.0 and OIDC deployments, and the substrate for workload identity federation (GitHub Actions OIDC, AWS IRSA, GCP Workload Identity, SPIFFE). The interesting movement is *around* JWT, not away from it: sender-constrained tokens (DPoP, RFC 9449) and mTLS-bound tokens (RFC 8705) are tightening the bearer-token model; PASETO and Biscuit exist as opinionated alternatives but have not displaced JWT in the enterprise stack. Knowing JWT well — including its sharp edges — is table stakes for backend work.

## Open questions / things to verify in practice

- Does my framework's JWT library default to a fixed `alg` allow-list, or does it accept whatever the header says? (Test by sending `alg: none` and `alg: HS256` against an RS256-configured endpoint.)
- What is the actual clock skew tolerance configured in my verifier? Does it match the issuer's clock-sync guarantees?
- How does my JWKS client behave on `kid` miss — refetch, fail, or DoS the IdP?
- What is the p99 size of my access tokens in production, and is it within the proxy/header limits of every hop?
- If a user's role is revoked, what is the worst-case window before their token stops working? Is that acceptable to the security team in writing?
- Do my logs or error responses ever echo back the raw JWT? (`Bearer` tokens in logs are a frequent finding in audits.)
