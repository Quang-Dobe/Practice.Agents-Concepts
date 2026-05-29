# CAP Theorem

The CAP theorem is a result about distributed databases that says when the network breaks between your nodes, you cannot simultaneously keep every client seeing the latest correct data and keep every node answering requests. During a network partition you have to pick one or the other — refuse some requests to stay correct, or answer everything and accept that some answers will be stale.

Engineers reach for it whenever they design or choose a system that runs across more than one node and has to survive real-world networks, where dropped messages and isolated regions are a regular Tuesday rather than a rare emergency. It frames the choice between systems that prioritize correctness under partition — ZooKeeper, etcd, Spanner, leader-elected stores used for payments and configuration — and systems that prioritize staying up — Cassandra, DynamoDB in default mode, shopping carts and social feeds where stale data beats an outage. Modern systems are rarely statically one or the other; they expose the choice per request, so the trade-off becomes an API design decision rather than a vendor selection.

Picture a bank with two branches that share one ledger by phone, and the phone line cuts out. A customer in one branch asks to withdraw five hundred dollars. The teller can refuse until the line is back — the ledger stays correct across both branches, but the customer leaves angry. Or the teller can pay out and write it down locally to reconcile later — the customer is happy, but the two branches will disagree about the balance until the line returns and may discover an overdraft. There is no third option where the teller magically knows what is happening at the other branch without a working phone. That is CAP.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/general-concept/cap-theorem/
