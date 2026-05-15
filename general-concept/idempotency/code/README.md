# Idempotency — MVP Code

The smallest runnable demo of idempotency. About 60 lines of actual code, comments excluded. A tiny in-memory payment service charges a balance, dedups retries by `Idempotency-Key`, and rejects the Stripe-style key-reuse trap (same key, different body).

## What it demonstrates
- A side-effectful operation made retry-safe with a client-supplied idempotency key.
- The three-state lifecycle from `02-deep-dive.md` (`in_flight` -> `succeeded` / `mismatch_rejected`).
- The request-fingerprint check from best-practice #3 in `03-practice.md` — catches "same key, different params" instead of silently replaying the wrong cached response.
- Replays return the cached response byte-identically; the underlying handler does not run again.

## Prerequisites
- Python 3.11+
- No dependencies. Standard library only.

## Run it

```bash
python mvp.py
```

## Expected output

```
opening balance: 1000
call 1 (original): {'status': 'ok', 'charged': 50, 'currency': 'USD', 'balance_after': 950}  | balance now 950
call 2 (retry):    {'status': 'ok', 'charged': 50, 'currency': 'USD', 'balance_after': 950}  | balance now 950
call 3 (key reuse, different body): REJECTED -> key 'client-uuid-abc-123' was first used with a different request body
final balance: 950 (charged exactly once, despite three calls)
```

The key observation: three `charge()` calls, but the balance only moves once.

## What to try next
- Remove the fingerprint check in `charge()` and watch call 3 silently return the cached $50 response while the client thinks $500 was debited.
- Change call 2's `amount` to `51` and observe the mismatch rejection fires on the retry instead.
- Add a fourth call with a fresh key (`"client-uuid-xyz-999"`) and confirm it debits a second time — the dedup is per-key, not global.
- Wrap the `self._balance -= amount` line in a `raise RuntimeError("crash!")` to see the record stuck in `in_flight` — the bricked-row failure mode from the practice doc.
