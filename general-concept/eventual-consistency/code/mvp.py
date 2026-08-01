"""
Eventual Consistency — smallest runnable demo.

Three replicas (A, B, C) accept writes independently while "partitioned"
(no network here — we just don't call gossip). They diverge, then converge
after a single anti-entropy / merge round.

Two data types, side by side, to make the LWW-vs-CRDT trade-off concrete:

  1. LWWRegister — Last-Writer-Wins. Merges deterministically, but the
                   concurrent losers are SILENTLY DROPPED. This is the
                   classic eventual-consistency footgun (see 02-deep-dive,
                   "Common failure modes -> Clock skew under LWW").

  2. GCounter    — grow-only CRDT counter. Each replica owns its own slot
                   and can only increment that slot; the value is the sum
                   of all slots. Merge is element-wise max — commutative,
                   associative, idempotent. Every concurrent increment
                   survives (strong eventual consistency).

Run:   python mvp.py
"""

from dataclasses import dataclass, field
from itertools import count


# ---------- LWW Register ----------------------------------------------------
# A (value, timestamp, node) triple. Higher (ts, node) wins on merge; the
# node id is only a deterministic tiebreak so two replicas never disagree
# after seeing the same set of writes.
@dataclass
class LWWRegister:
    value: object = None
    ts: int = 0
    node: str = ""

    def write(self, value, ts, node):
        if (ts, node) > (self.ts, self.node):
            self.value, self.ts, self.node = value, ts, node

    def merge(self, other: "LWWRegister"):
        # Reuse write() so the ordering rule lives in exactly one place.
        self.write(other.value, other.ts, other.node)


# ---------- G-Counter CRDT --------------------------------------------------
# One slot per replica. `inc` only ever bumps the caller's own slot, so two
# replicas incrementing concurrently touch different keys and cannot clobber
# each other. `merge` takes the elementwise max — that's the whole trick.
@dataclass
class GCounter:
    slots: dict = field(default_factory=dict)

    def inc(self, node, n=1):
        self.slots[node] = self.slots.get(node, 0) + n

    def value(self) -> int:
        return sum(self.slots.values())

    def merge(self, other: "GCounter"):
        for node, v in other.slots.items():
            self.slots[node] = max(self.slots.get(node, 0), v)


# ---------- The three replicas ---------------------------------------------
# No sockets, no threads — a dict is enough to show the concept. The point
# is that writes hit ONE replica at a time; gossip is a separate step.
REPLICAS = ["A", "B", "C"]
clock = count(1)  # shared monotonic tick to stamp LWW writes (Lamport-lite)

state = {r: {"city": LWWRegister(), "likes": GCounter()} for r in REPLICAS}


def show(label: str) -> None:
    print(f"\n--- {label} ---")
    for r in REPLICAS:
        c, k = state[r]["city"], state[r]["likes"]
        print(f"  {r}: city={c.value!r:<10} @ts{c.ts}  "
              f"likes={k.value()}  slots={k.slots}")


def gossip() -> None:
    """Anti-entropy: every pair of replicas swaps state and merges.
    Real systems use Merkle-tree diffing over key ranges; here it's brute
    force. Merge is commutative + idempotent, so ordering and repeats are
    both safe — that's precisely what makes convergence guaranteed."""
    for a in REPLICAS:
        for b in REPLICAS:
            if a == b:
                continue
            state[a]["city"].merge(state[b]["city"])
            state[a]["likes"].merge(state[b]["likes"])


# ---------- Scene 1: concurrent writes on each replica ---------------------
show("initial (all replicas empty)")

# Three concurrent writes to the SAME logical key. Under LWW only the
# highest timestamp survives; the other two vanish with no error.
state["A"]["city"].write("Hanoi",  next(clock), "A")
state["B"]["city"].write("Tokyo",  next(clock), "B")
state["C"]["city"].write("Berlin", next(clock), "C")

# Concurrent increments on the counter. Every single one must survive.
state["A"]["likes"].inc("A"); state["A"]["likes"].inc("A")
state["B"]["likes"].inc("B")
state["C"]["likes"].inc("C"); state["C"]["likes"].inc("C"); state["C"]["likes"].inc("C")

show("after independent writes — DIVERGED (partition still up)")

# ---------- Scene 2: partition heals, gossip converges ---------------------
gossip()
show("after gossip round 1 — CONVERGED")

# Running the exact same anti-entropy pass again must not change anything.
# That's the idempotence property CRDTs and LWW-registers both rely on.
gossip()
show("after gossip round 2 — idempotent (no drift)")

# ---------- Scene 3: a new write after convergence -------------------------
# A single write on A. Until the next gossip, only A knows about it.
state["A"]["city"].write("Osaka", next(clock), "A")
state["A"]["likes"].inc("A")
show("A writes again — only A knows")

gossip()
show("after gossip round 3 — everyone knows")

print("\nTake-away:")
print("  * LWW converged, but 2 of the 3 concurrent city writes were SILENTLY DROPPED.")
print("  * G-Counter converged AND preserved every increment. That is why CRDTs matter")
print("    for anything you cannot afford to lose (see 03-practice, best-practice #4).")
