# SOLID Principles — MVP Code

The smallest runnable demo of SOLID. About 60 lines of actual code, comments excluded.

## What it demonstrates
- **SRP + OCP:** `PricingPolicy` answers to finance only; `OrderService` takes new providers (`PayPalGateway`) with zero edits (see `../docs/02-deep-dive.md` § How, steps 2–3).
- **DIP:** the domain owns the `PaymentGateway` Protocol and gets it injected at one composition root (steps 4 and 7).
- **ISP:** refunds sit in a separate `Refunder` role, so checkout code never depends on them (step 5).
- **LSP:** `CappedGateway` type-checks but strengthens a precondition; one shared `contract_check` catches it (step 6).

## Prerequisites
- Python 3.11+
- No third-party dependencies (standard library only).

## Run it

```bash
python mvp.py
```

## Expected output

```
Same OrderService, different gateways (OCP + DIP):
  StripeGateway  -> stripe_ch_A42_104.50
  PayPalGateway  -> paypal_A42_104.50
Back-office uses only the Refunder role (ISP):
  refunded stripe_ch_A42_104.50
Contract check against every implementation (LSP):
  StripeGateway  PASS
  PayPalGateway  PASS
  CappedGateway  FAIL (CappedGateway refuses 250.00)
```

## What to try next
- Add a `FakeGateway` that records charges in a list and pass it to `OrderService`; note that no domain code changes.
- Pass `CappedGateway()` to `OrderService` in the first loop and watch checkout crash on a 104.50 order.
- Move `refund` into `PaymentGateway`, and note that `PayPalGateway` must now implement a method checkout never calls (the fat-interface cost).
- Replace the `PaymentGateway` Protocol with a plain `Callable[[str, Decimal], str]` parameter and compare line counts.
