"""Tiny simulated 3-node replicated KV store demonstrating the CAP trade-off.

Run: python3 mvp.py

CP mode: quorum write (W=2) + quorum read (R=2) over N=3, so R+W>N.
AP mode: any-node write/read with last-write-wins reconciliation.
A partition is simulated by dropping in-process "messages" between node pairs.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from itertools import count
from typing import Optional


# ---------- data + network model -------------------------------------------------

@dataclass(frozen=True)
class Versioned:
    """A value tagged with a logical timestamp for last-write-wins reconciliation."""
    value: str
    ts: int


class Network:
    """Tracks which directed node pairs can exchange messages.

    A partition is just a set of (src, dst) pairs that are currently blocked.
    """

    def __init__(self, node_ids: list[str]) -> None:
        self._blocked: set[tuple[str, str]] = set()
        self._node_ids = node_ids

    def isolate(self, node_id: str) -> None:
        # Drop every message into or out of this node — full isolation.
        for other in self._node_ids:
            if other == node_id:
                continue
            self._blocked.add((node_id, other))
            self._blocked.add((other, node_id))

    def heal(self) -> None:
        self._blocked.clear()

    def can_deliver(self, src: str, dst: str) -> bool:
        return (src, dst) not in self._blocked


# ---------- the replica ----------------------------------------------------------

@dataclass
class Node:
    """One replica. Holds a local copy of the KV store and a reference to the network."""
    node_id: str
    network: "Network"
    peers: list["Node"] = field(default_factory=list)
    store: dict[str, Versioned] = field(default_factory=dict)

    def local_put(self, key: str, value: Versioned) -> None:
        # Last-write-wins at the local level too: a stale message must not overwrite.
        current = self.store.get(key)
        if current is None or value.ts > current.ts:
            self.store[key] = value

    def local_get(self, key: str) -> Optional[Versioned]:
        return self.store.get(key)

    def reachable_peers(self) -> list["Node"]:
        return [p for p in self.peers if self.network.can_deliver(self.node_id, p.node_id)
                and self.network.can_deliver(p.node_id, self.node_id)]


# ---------- the two protocols ----------------------------------------------------

class PartitionedError(RuntimeError):
    """Raised by CP operations when a quorum cannot be reached."""


_clock = count(1)  # monotonic logical timestamps for the whole cluster


def cp_put(coordinator: Node, key: str, value: str) -> Versioned:
    """Quorum write: only succeeds if a majority (incl. coordinator) accept the value."""
    ts = next(_clock)
    versioned = Versioned(value=value, ts=ts)
    acks = 1  # the coordinator counts itself
    for peer in coordinator.peers:
        if coordinator.network.can_deliver(coordinator.node_id, peer.node_id):
            peer.local_put(key, versioned)
            acks += 1
    # N=3 so a majority is 2. The minority side cannot reach this threshold.
    if acks < 2:
        raise PartitionedError(
            f"CP put via {coordinator.node_id}: only {acks}/3 acks — no quorum, refusing write"
        )
    coordinator.local_put(key, versioned)
    return versioned


def cp_get(coordinator: Node, key: str) -> Optional[Versioned]:
    """Quorum read: collect from a majority and return the highest-timestamp value."""
    seen: list[Optional[Versioned]] = [coordinator.local_get(key)]
    for peer in coordinator.peers:
        if coordinator.network.can_deliver(coordinator.node_id, peer.node_id):
            seen.append(peer.local_get(key))
    if len(seen) < 2:
        raise PartitionedError(
            f"CP get via {coordinator.node_id}: only {len(seen)}/3 replies — no quorum, refusing read"
        )
    # With R+W>N the highest-ts reply is the most recently committed value.
    fresh = [v for v in seen if v is not None]
    return max(fresh, key=lambda v: v.ts) if fresh else None


def ap_put(coordinator: Node, key: str, value: str) -> Versioned:
    """Any-node write: ack immediately, gossip best-effort. Never refuses."""
    ts = next(_clock)
    versioned = Versioned(value=value, ts=ts)
    coordinator.local_put(key, versioned)
    for peer in coordinator.peers:
        # Best-effort gossip — silently drop on a partition; the heal step will repair.
        if coordinator.network.can_deliver(coordinator.node_id, peer.node_id):
            peer.local_put(key, versioned)
    return versioned


def ap_get(coordinator: Node, key: str) -> Optional[Versioned]:
    """Any-node read: just return whatever this node has locally. May be stale."""
    return coordinator.local_get(key)


def anti_entropy(nodes: list[Node]) -> None:
    """Post-heal sweep: every node merges every other node's view of every key.

    Last-write-wins by timestamp. This is the "reconciliation" half of AP — without
    it, divergence after a partition would persist forever.
    """
    keys = {k for n in nodes for k in n.store}
    for key in keys:
        best: Optional[Versioned] = None
        for n in nodes:
            v = n.local_get(key)
            if v is not None and (best is None or v.ts > best.ts):
                best = v
        if best is not None:
            for n in nodes:
                n.local_put(key, best)


# ---------- scripted scenario ----------------------------------------------------

def build_cluster() -> tuple[list[Node], Network]:
    net = Network(["n1", "n2", "n3"])
    nodes = [Node(node_id=nid, network=net) for nid in ("n1", "n2", "n3")]
    for n in nodes:
        n.peers = [other for other in nodes if other is not n]
    return nodes, net


def show(label: str, msg: str) -> None:
    print(f"[{label}] {msg}")


def main() -> None:
    nodes, net = build_cluster()
    n1, n2, n3 = nodes

    show("step 1", "healthy 3-node cluster: n1, n2, n3")

    # Step 2 — CP on a healthy network: quorum easily reached.
    v = cp_put(n1, "balance", "100")
    show("step 2", f"CP put via n1: ok, value={v.value} ts={v.ts}")
    r = cp_get(n2, "balance")
    show("step 2", f"CP get via n2: value={r.value if r else None}")

    # Step 3 — AP on a healthy network: same outcome, different mechanism.
    v = ap_put(n1, "cart", "[apple]")
    show("step 3", f"AP put via n1: ok, value={v.value} ts={v.ts}")
    r = ap_get(n3, "cart")
    show("step 3", f"AP get via n3: value={r.value if r else None}")

    # Step 4 — isolate n3. Majority side is {n1, n2}; minority side is {n3}.
    net.isolate("n3")
    show("step 4", "partition injected: n3 cut off from n1 and n2")

    # Step 5a — CP write on majority side: still gets 2 acks (n1 + n2), succeeds.
    v = cp_put(n1, "balance", "200")
    show("step 5", f"CP put via n1 (majority): ok, value={v.value} ts={v.ts}")
    r = cp_get(n2, "balance")
    show("step 5", f"CP get via n2 (majority): value={r.value if r else None}")

    # Step 5b — CP from n3 cannot reach a majority. This is C-over-A made concrete.
    try:
        cp_put(n3, "balance", "999")
    except PartitionedError as e:
        show("step 5", f"CP put via n3 (minority): REFUSED — {e}")
    try:
        cp_get(n3, "balance")
    except PartitionedError as e:
        show("step 5", f"CP get via n3 (minority): REFUSED — {e}")

    # Step 6 — AP keeps accepting writes on BOTH sides. Divergence is the visible cost.
    ap_put(n1, "cart", "[apple, banana]")        # majority-side write
    ap_put(n3, "cart", "[apple, durian]")        # minority-side write, same key
    maj = ap_get(n1, "cart")
    minr = ap_get(n3, "cart")
    show("step 6", f"AP get via n1 (majority): value={maj.value} ts={maj.ts}")
    show("step 6", f"AP get via n3 (minority): value={minr.value} ts={minr.ts}")
    show("step 6", "two sides disagree — this is the A-over-C choice in code")

    # Step 7 — heal the partition and run anti-entropy. Highest timestamp wins.
    net.heal()
    show("step 7", "partition healed")
    anti_entropy(nodes)
    show("step 7", "anti-entropy sweep complete (last-write-wins by timestamp)")
    for n in nodes:
        v = n.local_get("cart")
        show("step 7", f"AP get via {n.node_id}: value={v.value} ts={v.ts}")
    show("step 7", "all nodes converged — note: the loser's write was silently dropped (LWW)")


if __name__ == "__main__":
    main()
