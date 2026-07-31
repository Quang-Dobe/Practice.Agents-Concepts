# IAM Roles -- MVP Code

The smallest runnable demo of IAM roles: a self-contained simulation of the AWS `AssumeRole` handshake, session credentials, and policy evaluation. About 100 lines of actual code, comments excluded. No AWS account required.

## What it demonstrates

- A **Role** bundles two independent documents -- a **trust policy** (who may assume) and a **permissions policy** (what the session may do).
- `AssumeRole` mints **short-lived session credentials** only if the caller passes the trust check; the credentials themselves carry no permissions -- the role does.
- Policy evaluation follows the real IAM rule: **explicit `Deny` wins**, otherwise at least one `Allow` must match, otherwise implicit deny.
- **Sessions expire.** Even the correct call fails with `ExpiredToken` until the caller re-assumes.

## Prerequisites

- .NET SDK 8.0 or newer (`dotnet --version` should print `8.x` or higher).
- No packages, no services, no cloud credentials.

## Run it

```bash
cd cloud/iam-roles/code
dotnet run
```

## Expected output

```
[1] orders-api assumes invoices-reader and reads an object
    minted session for arn:aws:iam::111122223333:role/invoices-reader (expires HH:mm:ss.fffZ)
    GetObject -> PDF-BYTES

[2] random-lambda from another account attempts AssumeRole
    AssumeRole: DENIED -> AccessDenied: arn:aws:iam::999999999999:role/random-lambda is not trusted to assume ...

[3] valid session invokes allowed / implicit-deny / explicit-deny actions
    PutObject orders-invoices/receipt.pdf: OK
    GetObject customer-pii/ssn.csv       : DENIED -> AccessDenied (implicit deny): s3:GetObject on arn:aws:s3:::customer-pii/ssn.csv
    DeleteObject orders-invoices/2026-01 : DENIED -> AccessDenied (explicit deny): s3:DeleteObject on arn:aws:s3:::orders-invoices/2026-01.pdf

[4] wait past session TTL, observe ExpiredToken, then re-assume
    GetObject with expired session       : DENIED -> ExpiredToken: session 'orders-api-worker-42' expired at ...
    re-assumed; GetObject -> PDF-BYTES
```

## What to try next

- Change `TrustedPrincipals: [appPrincipal.Arn]` to include `attackerPrincipal.Arn` and rerun scenario 2.
- Set `RequiredExternalId: "vendor-secret-abc"` on the `TrustPolicy`, then pass a wrong `externalId` to `AssumeRole` to see the confused-deputy defense fire.
- Remove the explicit `Deny` on `s3:DeleteObject` and observe scenario 3 flipping from explicit-deny to implicit-deny.
- Raise `sessionDuration` to `TimeSpan.FromMinutes(1)` and watch scenario 4 hang for a minute -- that is why real batch jobs care about `MaxSessionDuration`.
