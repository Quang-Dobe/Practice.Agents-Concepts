# JWT (JSON Web Token) — MVP Code

The smallest runnable demo of issuing and verifying a signed JWT. ~120 lines of code across four projects.

## What it demonstrates
- **Issue**: build the registered claims (`sub`, `iss`, `aud`, `iat`, `exp`, `jti`) and HMAC-SHA-256 sign them — see `Infrastructure/Tokens/HmacTokenIssuer.cs`.
- **Verify the four mandatory checks** (signature, `exp`, `iss`, `aud`) with the algorithm **pinned to an allow-list** so neither `alg: none` nor RS256→HS256 confusion can sneak past — see `Infrastructure/Tokens/HmacTokenVerifier.cs`.
- **Tamper failure**: flip one character in the payload segment and watch verification fail with `SecurityTokenSignatureKeyNotFoundException` / `SecurityTokenInvalidSignatureException`.
- The Clean Architecture seam — `Application` owns the `ITokenIssuer` / `ITokenVerifier` ports; `Infrastructure` owns `Microsoft.IdentityModel.Tokens`.

## Prerequisites
**.NET 8.0 SDK** (or newer). Verify with `dotnet --version`. Everything runs in-process — no IdP, no JWKS, no database.

## Run it
```bash
cd code && dotnet run --project Console
```

## Overriding the secret
The demo reads `JWT_SECRET` from the environment and falls back to a hard-coded 34-byte string so `dotnet run` works out of the box. To override (recommended any time you change the code):
```bash
JWT_SECRET="$(openssl rand -base64 48)" dotnet run --project Console
```
The fallback exists only so the demo is one command. Production code must never ship a hard-coded secret — see `../docs/03-practice.md` §9.

## Expected output
```
Issued JWT (expires 2026-05-30T...):
eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyLTQyIi...<signature>

Verify (untouched) -> valid=True, sub=user-42, role=admin
Verify (tampered) -> valid=False, reason=SecurityTokenSignatureKeyNotFoundException: IDX10503: Signature validation failed...
```

## What to try next
- Drop `Lifetime` in `Program.cs` to `TimeSpan.FromSeconds(1)`, add a `Task.Delay(2000)` before verify, and see `exp` fail instead of the signature.
- Change `ValidAudience` in `HmacTokenVerifier.cs` to `"other-api"` — same token, audience mismatch, fails with `SecurityTokenInvalidAudienceException`.
- Remove `ValidAlgorithms = [...]` from the verifier and re-run — the demo still passes, but you've just opened the `alg: none` door (see `../docs/02-deep-dive.md` §Common failure modes).
- Shorten the fallback secret in `Program.cs` to `"too-short"` — `JwtOptions.Validate()` should fail loudly at startup.
