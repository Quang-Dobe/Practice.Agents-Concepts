"""SOLID Principles — MVP.

Proves the refactor walked through in docs/02-deep-dive.md § How:
the god-class OrderService becomes a small policy class that depends only on
narrow, domain-owned abstractions. Then it shows what an LSP break looks like
and how a shared contract check catches it.

Standard library only. Run: python mvp.py
"""
from dataclasses import dataclass
from decimal import Decimal
from typing import Protocol


# ---------------------------------------------------------------- domain ----
# Everything in this section is the high-level "policy". It never imports or
# names Stripe/PayPal. In a real repo this would be its own package.

@dataclass(frozen=True)
class Item:
    price: Decimal
    qty: int


@dataclass(frozen=True)
class Order:
    id: str
    items: list[Item]


class PaymentGateway(Protocol):
    # DIP: the domain OWNS this interface. Infrastructure conforms to it,
    # so the source-code arrow points infra -> domain (the "inversion").
    # Contract: accepts ANY amount > 0, returns a receipt id (a domain str,
    # never an SDK object — returning StripeCharge would leak details).
    def charge(self, order_id: str, amount: Decimal) -> str: ...


class Refunder(Protocol):
    # ISP: refunds live in their own interface. Only the back-office tool
    # needs them, so OrderService is not coupled to refund signatures.
    def refund(self, receipt_id: str) -> None: ...


class PricingPolicy:
    # SRP: the finance team is the only actor that changes this class.
    def total(self, order: Order) -> Decimal:
        subtotal = sum((i.price * i.qty for i in order.items), Decimal("0"))
        return subtotal * Decimal("1.10")  # 10% tax, hard-coded for the demo


class OrderService:
    # OCP: adding a provider never edits this class — no `if method == ...`.
    # It receives collaborators instead of constructing them (DI).
    def __init__(self, pricing: PricingPolicy, gateway: PaymentGateway) -> None:
        self.pricing = pricing
        self.gateway = gateway

    def checkout(self, order: Order) -> str:
        amount = self.pricing.total(order)
        return self.gateway.charge(order.id, amount)


# -------------------------------------------------------- infrastructure ----
# Low-level details. Each class depends on the domain's Protocol, not the
# other way around. Structural typing: no inheritance needed to conform.

class StripeGateway:
    def charge(self, order_id: str, amount: Decimal) -> str:
        return f"stripe_ch_{order_id}_{amount:.2f}"

    def refund(self, receipt_id: str) -> None:  # also satisfies Refunder
        print(f"  refunded {receipt_id}")


class PayPalGateway:
    # Added "later": a brand-new class, zero edits to OrderService (OCP).
    def charge(self, order_id: str, amount: Decimal) -> str:
        return f"paypal_{order_id}_{amount:.2f}"


class CappedGateway:
    # LSP VIOLATION: strengthens the precondition ("amount must be < 100").
    # It type-checks as a PaymentGateway, but callers relying on the
    # contract break at runtime. Type checkers cannot see behavioural rules.
    def charge(self, order_id: str, amount: Decimal) -> str:
        if amount >= 100:
            raise ValueError(f"CappedGateway refuses {amount:.2f}")
        return f"capped_{order_id}_{amount:.2f}"


def contract_check(gateway: PaymentGateway) -> str:
    # LSP in practice: ONE shared check run against EVERY implementation.
    # It exercises inputs the contract allows, including a large amount.
    try:
        for amount in (Decimal("0.01"), Decimal("250.00")):
            assert isinstance(gateway.charge("probe", amount), str)
        return "PASS"
    except Exception as exc:
        return f"FAIL ({exc})"


# ------------------------------------------------------ composition root ----
# The ONLY place that names concrete classes. Swap wiring here, nowhere else.

if __name__ == "__main__":
    order = Order("A42", [Item(Decimal("40.00"), 2), Item(Decimal("15.00"), 1)])
    pricing = PricingPolicy()

    print("Same OrderService, different gateways (OCP + DIP):")
    for gw in (StripeGateway(), PayPalGateway()):
        print(f"  {type(gw).__name__:<14} -> {OrderService(pricing, gw).checkout(order)}")

    print("Back-office uses only the Refunder role (ISP):")
    refunder: Refunder = StripeGateway()
    refunder.refund("stripe_ch_A42_104.50")

    print("Contract check against every implementation (LSP):")
    for gw in (StripeGateway(), PayPalGateway(), CappedGateway()):
        print(f"  {type(gw).__name__:<14} {contract_check(gw)}")
