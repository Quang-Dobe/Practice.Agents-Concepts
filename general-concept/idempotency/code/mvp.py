"""
Idempotency MVP — the smallest possible demonstration.

What this proves
----------------
A side-effectful operation (charging a balance) is wrapped with an
idempotency key. The lesson is in three calls inside main():

  1. First call with key K and request R   -> executes, debits balance, caches response.
  2. Retry  with key K and request R       -> returns the CACHED response, balance unchanged.
  3. Retry  with key K and DIFFERENT body  -> rejected as a fingerprint mismatch (the
                                              Stripe-style "key reuse trap" from the practice doc).

Concurrency is intentionally out of scope. A real implementation must serialise
replays of the in-flight state with an atomic acquisition primitive
(Postgres INSERT ... ON CONFLICT, Redis SET NX, DynamoDB attribute_not_exists).
The single-threaded dict here is *not* that primitive — it would race under
concurrent retries. The state machine and fingerprint check are what matter.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from typing import Literal


# The three terminal/intermediate states a key can occupy. Modelling this as
# a state machine (rather than a single "seen" boolean) is best-practice #7
# in 03-practice.md — you must be able to tell "still running" from "done"
# from "rejected".
KeyStatus = Literal["in_flight", "succeeded", "mismatch_rejected"]


@dataclass
class IdempotencyRecord:
    fingerprint: str          # SHA-256 of the canonical request body; detects key-reuse with different params.
    status: KeyStatus
    response: dict | None     # The cached response, replayed byte-identically on retry.


class PaymentService:
    """In-memory toy payment service. One account, one dedup table."""

    def __init__(self, opening_balance: int) -> None:
        self._balance: int = opening_balance
        # The dedup store. In production this is Postgres or Redis, not a dict.
        self._idempotency_store: dict[str, IdempotencyRecord] = {}

    @staticmethod
    def _fingerprint(amount: int, currency: str) -> str:
        # Canonicalise then hash. sort_keys ensures two clients producing the same
        # logical body get the same hash regardless of field ordering — see
        # best-practice #3 in 03-practice.md.
        canonical = json.dumps({"amount": amount, "currency": currency}, sort_keys=True)
        return hashlib.sha256(canonical.encode()).hexdigest()

    def charge(self, idempotency_key: str, amount: int, currency: str) -> dict:
        fp = self._fingerprint(amount, currency)
        existing = self._idempotency_store.get(idempotency_key)

        if existing is not None:
            # Key has been seen. The fingerprint check is what protects the
            # client from its own bug: "same key, different body" must NOT
            # silently replay the old response.
            if existing.fingerprint != fp:
                existing.status = "mismatch_rejected"
                # Stripe returns 400; the IETF draft prefers 422. We model the
                # rejection as a raised exception — the demo's "409-equivalent".
                raise FingerprintMismatch(
                    f"key {idempotency_key!r} was first used with a different request body"
                )
            # Same key, same body -> this is a genuine retry. Replay the cached
            # response. The handler does NOT run again, so balance is untouched.
            assert existing.response is not None
            return existing.response

        # First time we've seen this key. Mark it in_flight *before* doing the
        # side effect so a concurrent retry would find the row already taken.
        # (In a real system the acquisition must be atomic — that's the load-
        # bearing primitive, not the dict assignment here.)
        self._idempotency_store[idempotency_key] = IdempotencyRecord(
            fingerprint=fp, status="in_flight", response=None
        )

        # The actual side effect. This is the line that must not run twice.
        self._balance -= amount
        response = {
            "status": "ok",
            "charged": amount,
            "currency": currency,
            "balance_after": self._balance,
        }

        # Persist the response on the record so future retries replay it.
        # Best-practice #4: cache the full response, including failures (we'd
        # do the same on a 4xx if the handler had validation logic).
        record = self._idempotency_store[idempotency_key]
        record.status = "succeeded"
        record.response = response
        return response

    @property
    def balance(self) -> int:
        return self._balance


class FingerprintMismatch(Exception):
    """Raised when an idempotency key is reused with a different request body."""


def main() -> None:
    svc = PaymentService(opening_balance=1000)
    key = "client-uuid-abc-123"  # In real life: UUIDv4 generated on the client BEFORE the first send.

    print(f"opening balance: {svc.balance}")

    # --- Call 1: original request. Handler runs, balance drops by 50.
    r1 = svc.charge(idempotency_key=key, amount=50, currency="USD")
    print(f"call 1 (original): {r1}  | balance now {svc.balance}")

    # --- Call 2: client never received the ack and retried with the SAME key
    #             and SAME body. Must hit the cache. Balance must NOT move.
    r2 = svc.charge(idempotency_key=key, amount=50, currency="USD")
    print(f"call 2 (retry):    {r2}  | balance now {svc.balance}")
    assert r1 == r2, "retry must return the cached response byte-identically"
    assert svc.balance == 950, "retry must not double-charge"

    # --- Call 3: the key-reuse trap. Client bug: reused the same key but with
    #             a different amount. A naive 'just dedup on the key' design
    #             would silently return the cached $50 response and the client
    #             would never learn it lost $450. We reject instead.
    try:
        svc.charge(idempotency_key=key, amount=500, currency="USD")
    except FingerprintMismatch as e:
        print(f"call 3 (key reuse, different body): REJECTED -> {e}")

    print(f"final balance: {svc.balance} (charged exactly once, despite three calls)")


if __name__ == "__main__":
    main()
