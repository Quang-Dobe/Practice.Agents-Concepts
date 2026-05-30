# JWT (JSON Web Token) — Overview

> A JWT is a small, signed, base64url-encoded string the server hands a client to prove "yes, I already checked who you are" — without the server having to remember it.

## The 30-second version

A JWT is a self-contained authentication token. The server signs a tiny JSON document, gives it to the client, and the client sends it back on every subsequent request (usually in an `Authorization: Bearer <token>` header). Because the token is signed, the server can trust its contents just by verifying the signature — no database lookup, no in-memory session table. That property is why JWT became the default for stateless APIs, microservices, and single sign-on.

## The mental model

Think of a JWT as a **tamper-evident wristband** at a music festival. At the gate, security checks your ID once, then snaps a wristband on you with your access tier printed on it. For the rest of the night, no booth re-checks your ID — they just glance at the wristband. The wristband has a holographic seal that's impossible to fake without the festival's special machine. If anyone tries to scratch out "general admission" and write "VIP," the seal breaks and the next bouncer notices instantly.

The festival doesn't keep a list of who got a wristband. The wristband itself carries the proof.

That's a JWT. The "wristband" is a string with three parts separated by dots:

```
eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMiLCJuYW1lIjoiQWxpY2UifQ.dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
   header            .            payload                  .            signature
```

- **Header** — base64url-encoded JSON saying which signing algorithm was used, e.g. `{"alg":"HS256","typ":"JWT"}`.
- **Payload** — base64url-encoded JSON of *claims*: who the user is, when the token expires, what they're allowed to do, e.g. `{"sub":"123","name":"Alice","exp":1735689600}`.
- **Signature** — the cryptographic seal. Computed over the first two parts plus a secret (or private key) known only to the server.

Verifying a JWT means re-computing the signature and checking it matches. If it does, the payload is trustworthy. If anyone flipped a single character in the payload, the signature breaks.

Crucially, **the payload is encoded, not encrypted.** Anyone can paste a JWT into [jwt.io](https://jwt.io) and read it. The signature stops tampering, not snooping.

## What it is NOT

- **Not encryption.** The payload is readable by anyone who has the token. Use JWE (a separate spec) if you need confidentiality.
- **Not a session.** A session lives in the server's memory or database; a JWT lives in the client's storage. That difference is the whole point.
- **Not an OAuth replacement.** OAuth 2.0 is the *protocol* for issuing tokens. JWT is just one *format* a token can take.
- **Not a long-term credential.** It's a short-lived proof, not a password.

## When you would reach for it

- Stateless REST or gRPC APIs that should scale horizontally without sticky sessions.
- Service-to-service auth inside a microservice mesh.
- Single sign-on flows (OpenID Connect — the ID token is a JWT).
- Short-lived access tokens issued by an OAuth 2.0 authorization server.

## When you would NOT reach for it

- A classic monolithic web app where a server-side session cookie is simpler and easier to revoke.
- Anywhere you need to invalidate a token *immediately* on logout — JWTs are valid until they expire unless you build a denylist, which kills the "stateless" benefit.
- Storing anything sensitive in the payload (passwords, PII, secrets). It's readable.

## Key vocabulary (just enough to keep reading)

- **Claim** — a key/value pair in the payload (`sub`, `exp`, `iat`, `aud`, `iss`, plus custom ones).
- **Signature** — the cryptographic proof of integrity.
- **HS256** — symmetric signing with a shared secret (HMAC-SHA256).
- **RS256 / ES256** — asymmetric signing; the server signs with a private key, anyone verifies with the public key.
- **Bearer token** — "whoever holds it, gets in" — how JWTs are transmitted in the `Authorization` header.
- **`exp` claim** — expiration timestamp; the single most important field in the payload.
- **`alg: none`** — a footgun: the spec allows an unsigned JWT. A naive verifier that accepts it is trivially exploitable. Always pin the allowed algorithm.

## What's next

The next document (`02-deep-dive.md`) answers What / Where / When / How / Why in detail — the exact signing algorithms, the full claim set, the verification flow step by step, and where JWTs sit inside OAuth 2.0 and OIDC.
