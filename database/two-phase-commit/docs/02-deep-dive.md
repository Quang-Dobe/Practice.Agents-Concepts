# Two-Phase Commit — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Two-phase commit (2PC) is an **atomic commitment protocol**. It solves the problem of getting a set of independent resource managers to reach the same commit-or-abort outcome for one distributed transaction, under a crash-recovery failure model. The protocol guarantees **safety**: no two participants ever reach different outcomes, and a commit happens only if every participant voted yes. It does **not** guarantee **liveness**. If the coordinator fails after participants have voted yes, those participants block until the coordinator recovers. Formally, 2PC is consensus on a binary value (commit/abort) where the "proposal" needs unanimity, and the coordinator is a single, unreplicated decider.

### The core building blocks

- **Coordinator / Transaction Manager (TM).** Drives the protocol and owns the **decision log**. Its durable write of the COMMIT record is the commit point for the whole transaction.
- **Participants / Resource Managers (RM).** Databases, brokers, or queues that execute the work locally and vote. Examples: PostgreSQL, MySQL/InnoDB, IBM MQ, Kafka (as a participant via KIP-939).
- **Global transaction identifier (GTRID / XID / GID).** The name that ties together the local branches on each RM. PostgreSQL limits a GID to under 200 bytes.
- **Prepared state.** A participant that votes YES force-writes its redo/undo and its "prepared" record to stable storage, keeps its locks, and **gives up the right to abort on its own**. This state survives restarts.
- **Write-ahead logs on both sides.** Recovery depends entirely on what was force-written (fsync'd) before each message was sent.
- **Recovery protocol.** After a crash, the coordinator re-drives phase 2 from its log, and participants ask "what happened to XID x?" The XA spec exposes this as `xa_recover`. PostgreSQL exposes it as the `pg_prepared_xacts` view.
- **The X/Open XA specification (1991).** The standard TM↔RM interface: `xa_start`, `xa_end`, `xa_prepare`, `xa_commit`, `xa_rollback`, `xa_recover`, `xa_forget`. Java exposes it through JTA / Jakarta Transactions (`XAResource`). MySQL exposes it as SQL (`XA START … XA PREPARE … XA COMMIT`).

### How it relates to the broader landscape

2PC belongs to the **atomic commitment** family, next to three-phase commit (3PC, Skeen 1981) and Paxos Commit (Gray & Lamport, 2004/2006). It sits beside, but is distinct from, **consensus** protocols (Paxos, Raft), which replicate one value across replicas and need only a majority. It is also distinct from **application-level** alternatives like Sagas and the transactional outbox, which give up atomic visibility in exchange for availability. Modern distributed SQL engines combine the two families: they run 2PC *across* shards, and each shard is a consensus group.

## Where

### Where it runs / lives in the stack

2PC lives at the **transaction/storage layer**. It shows up in two places:

- **Inside a distributed database.** The engine runs it internally across shards or ranges. The application issues `COMMIT` and never sees the protocol.
- **In the application tier, across separate databases.** A TM library (Narayana, Atomikos) or an OS service (MSDTC) acts as coordinator. The RMs are separate database or broker processes reached over the network through XA-capable drivers.

### Where you typically encounter it

- **Google Spanner.** Transactions that touch more than one Paxos group run 2PC across the group leaders. One leader is the "coordinator leader." Commit timestamps come from TrueTime, followed by a commit wait.
- **CockroachDB.** Uses a 2PC-derived protocol with a transaction record and write intents. Since v19.2, **Parallel Commits** marks the record `STAGING` so a commit costs one round of consensus instead of two.
- **Citus (PostgreSQL extension).** Multi-node writes issue `PREPARE TRANSACTION` / `COMMIT PREPARED` to the workers. Recovery records are tracked in `pg_dist_transaction`.
- **Vitess.** `transaction_mode=TWOPC` gives atomic cross-shard commits. The feature was redesigned and reintroduced as experimental in v21 (October 2024).
- **Java EE / Jakarta EE application servers and Spring + JTA.** XA across a database and a JMS broker.
- **.NET `System.Transactions`.** `TransactionScope` escalates from the Lightweight Transaction Manager to MSDTC when a second durable resource enlists. .NET 7 restored this on **Windows only**.

### Ecosystem and tooling

- **Native database 2PC:** PostgreSQL `PREPARE TRANSACTION` / `COMMIT PREPARED` / `ROLLBACK PREPARED` (gated by `max_prepared_transactions`, default `0`). MySQL/InnoDB `XA` statements with `XA RECOVER`. SQL Server and Oracle through MSDTC or XA.
- **Standalone transaction managers:** Narayana (JBoss/WildFly, Quarkus), Atomikos (embedded in Spring Boot apps), MSDTC (Windows). Bitronix is historical and unmaintained.
- **Broker participation:** IBM MQ and ActiveMQ/Artemis support XA. Kafka has used its own internal 2PC-style protocol for exactly-once transactions since 0.11. **KIP-939** (accepted, targeted at 4.1) lets Kafka act as a *participant* under an external coordinator. Check your client and broker versions before you rely on it.
- **Formal references:** Gray, *Notes on Data Base Operating Systems* (1978). Mohan, Lindsay and Obermarck, *Transaction Management in the R\* Distributed DBMS*, ACM TODS 11(4), 1986, which defines presumed abort and presumed commit. [Gray & Lamport, *Consensus on Transaction Commit*](https://arxiv.org/abs/cs/0408036).

## When

### When the topic emerged and why

By the mid-1970s, transaction processing systems such as IBM's IMS and System R had made atomic, durable transactions on one machine routine. Distributed databases (SDD-1, R\*) broke that guarantee. A transaction updating two sites could commit at one site and fail at the other. Lampson and Sturgis (Xerox PARC, 1976) and Gray (1978) described the protocol now called 2PC. R\* (1986) refined it with presumed-abort and presumed-commit to cut log forces and messages. X/Open standardized the TM↔RM API as XA in 1991 so heterogeneous databases and TP monitors (CICS, Tuxedo, Encina) could interoperate. Before 2PC, people used manual reconciliation, batch jobs, and "commit and hope."

### When to use it in a project

Reach for it when:

- You need **atomic visibility**: no reader may ever see one side committed and the other not.
- All participants are **systems you operate** and that can actually prepare: databases and XA-capable brokers, not HTTP APIs.
- The participant count is **small (2 to a handful)** and they share a low-latency network, usually one datacenter or region.
- The coordinator's log can be made **durable and highly available**: a persistent volume, a replicated store, or a database engine that does it internally.
- You are using a distributed database that already runs 2PC internally. In that case you get it with no extra work.

### When NOT to use it

Avoid it when:

- Participants are **microservices owned by other teams**. A prepared transaction in their database holds their locks hostage to your coordinator's uptime.
- Any participant is a **third-party SaaS or HTTP endpoint**. These have no prepare/commit split.
- You run on **ephemeral compute** (stateless pods, serverless) with no durable place for the TM log. A lost coordinator log means permanently in-doubt transactions.
- The workload is **write-hot or latency-critical**. Locks are held across at least one network round trip plus two fsyncs, which caps throughput on contended rows.
- **Eventual consistency is acceptable** to the business. The outbox pattern plus idempotent consumers, or a Saga with compensations, removes the coordinator altogether.

## How

### How it works under the hood

Below is the classic protocol with **presumed abort**, the variant used by most XA implementations. "Force" means fsync before continuing.

1. **Execute.** The application does its work on each RM under one XID. Each RM takes locks and writes uncommitted changes as usual.
2. **Prepare request.** The coordinator sends `PREPARE(xid)` to every participant. Under presumed abort, the coordinator writes nothing yet.
3. **Participant vote.**
   - If the participant can commit, it **forces** a `PREPARED` log record (with enough redo/undo to finish either way), keeps its locks, and replies `YES`.
   - If it cannot, it aborts locally and replies `NO`. It is free to forget the transaction.
   - If it only read data, it replies `READ-ONLY`, releases its locks, and drops out of phase 2.
4. **Decision.** If every vote is YES, the coordinator **forces** a `COMMIT(xid, participants)` record. **This fsync is the commit point.** If any vote is NO or a timeout fires, the coordinator decides abort and needs no forced write.
5. **Phase 2.** The coordinator sends `COMMIT` or `ABORT` to every YES-voter.
6. **Participant completion.** The participant forces a `COMMIT` record, releases its locks, and sends an `ACK`. Under presumed abort, ABORT needs no ACK.
7. **Forget.** After all ACKs arrive, the coordinator writes a non-forced `END` record and can garbage-collect the transaction.

Cost in the commit case with *n* participants: **4n messages** (prepare, vote, decision, ack), **2 round trips**, and **2n + 1 forced writes** (n prepares, 1 decision, n commits). The application can be told "committed" right after step 4. Participants' locks, however, stay held until step 6 reaches each one.

**Participant state machine and what recovery does in each state:**

```
            vote NO / timeout
 ACTIVE ───────────────────────────────▶ ABORTED
   │                                         ▲
   │ force PREPARED, vote YES                │ coordinator says ABORT
   ▼                                         │ (or no COMMIT record: presumed abort)
 PREPARED (in-doubt, locks held) ────────────┘
   │
   │ coordinator says COMMIT
   ▼
 COMMITTED
```

| Crash point | Recovery outcome |
|---|---|
| Participant crashes before forcing PREPARED | It restarts with no record of the transaction and aborts. The coordinator times out and aborts. |
| Participant crashes after PREPARED | It restarts *in-doubt*. Its locks are reacquired and it asks the coordinator for the outcome. |
| Coordinator crashes before forcing COMMIT | On restart there is no decision record, so presumed abort applies. Participants that ask are told ABORT. |
| Coordinator crashes after forcing COMMIT | On restart it re-sends COMMIT until every participant ACKs. |
| Coordinator's log is lost permanently | Prepared participants are stuck forever. Only an operator **heuristic** decision clears them, and that decision can break atomicity. |

**Presumed commit** is the mirror image. The coordinator forces a "collecting" record listing the participants *before* prepare. Commits then need no ACKs, and aborts do. It is cheaper when commits dominate, but it costs an extra forced write up front.

### Key trade-offs

| Design choice | Gain | Cost |
|---|---|---|
| Unanimous vote (vs majority) | Every RM agrees on the outcome, which gives true atomicity. | Any single RM can veto or stall the transaction, so availability is the product of the participants' availabilities. |
| Single unreplicated coordinator | Simple, and only one decision record to force. | Blocking: a coordinator crash after votes leaves participants in-doubt. Paxos Commit / Spanner replicate the coordinator to remove this. |
| Prepared state holds locks | Participants can honor a commit decision no matter when it arrives. | Lock hold time grows by ≥ 1 RTT + 2 fsyncs. Contention and deadlocks rise with participant count. |
| Presumed abort vs presumed commit | PA: aborts and read-only branches are nearly free. PC: commits skip ACKs. | PA: commits still need ACKs. PC: an extra forced write before every transaction. |
| Synchronous phase 2 | Each participant shows the committed data as soon as the client gets success. | Higher commit latency. Asynchronous phase 2 returns sooner, but readers on a lagging participant can briefly see pre-commit state. |
| Heuristic completion (XA) | An operator can unblock a stuck RM. | Atomicity is no longer guaranteed. You get `XA_HEURMIX` / `XA_HEURHAZ` outcomes and manual cleanup. |
| 2PC vs Saga | Atomic, isolated, no compensation logic. | Sagas need no locks across services and tolerate participant outages, but they expose intermediate states and need hand-written compensations. |

### Common failure modes

- **Orphaned prepared transactions in PostgreSQL.** The TM crashed or lost its log, so rows in `pg_prepared_xacts` never resolve. They hold locks, block `VACUUM` from removing dead tuples, and in the extreme push toward XID-wraparound shutdown.
- **`max_prepared_transactions` left at 0, or set lower than `max_connections`.** `PREPARE TRANSACTION` errors out, or concurrent 2PC traffic fails under load.
- **Coordinator on ephemeral storage.** The pod is rescheduled and its TM log vanishes, so in-doubt branches pile up on every RM with nobody able to resolve them.
- **Heuristic mixed outcome.** A DBA manually commits a branch on one RM while the coordinator decides abort, and the data now diverges.
- **Lock amplification under contention.** A hot row is held across a cross-AZ round trip, so throughput collapses to roughly 1 / (lock hold time) per row.
- **MySQL XA crash during `XA PREPARE`/`XA COMMIT` on versions before 8.0.30.** The binary log and InnoDB could disagree after a crash because prepare was not atomic between the server and the engine. Before 5.7.7, prepared XA transactions were rolled back on client disconnect.
- **Timeouts treated as aborts after a YES vote.** A participant or broker that auto-aborts a prepared branch (Kafka before KIP-939) breaks atomicity silently.
- **GID collisions or reuse.** The TM reuses identifiers across restarts, and recovery commits or rolls back the wrong branch.

## Why

### Why it exists

A local transaction gets atomicity from one write-ahead log: one fsync of one commit record decides everything. Once state is split across machines, there is no single log, and the machines can crash independently. 2PC rebuilds that single decision point. It makes every participant reach a state where it can go *either way* (prepared), then records the outcome in exactly one durable place (the coordinator's log). The first principles at stake are **atomicity under partial failure** and the fact that a message cannot be un-sent. A participant that commits before it knows everyone else can commit cannot take that commit back.

### Why it looks the way it does

The non-obvious part is the **prepared state**: a participant voluntarily gives up its right to abort on its own. Without that promise, the coordinator's decision would be advisory, because a participant could still abort later. Atomic commit would then be impossible.

Alternatives and why they lost:

- **One phase ("commit everywhere, then fix up").** One fewer round trip, but no atomicity. This is the status quo 2PC replaced. Sagas formalize it with compensations.
- **Three-phase commit.** Adds a pre-commit phase so that surviving participants can finish without the coordinator. It is non-blocking only if the network is synchronous with bounded delays. Under a network partition, two halves can reach different decisions. It costs an extra round trip and gives up safety in exactly the failures that matter in practice. Production systems therefore rarely use it.
- **Replicate the coordinator (Paxos Commit).** Gray and Lamport show that classic 2PC is Paxos Commit with F = 0. With 2F + 1 coordinators the protocol tolerates F failures, keeps the same stable-storage write delay, and spends more messages. Spanner takes this route: each 2PC participant and the coordinator are Paxos groups. CockroachDB, YugabyteDB and TiDB do the same with Raft.

The modern answer to "2PC blocks" is therefore not a different commit protocol. It is a coordinator that cannot disappear.

### Why it matters now

As of 2026, 2PC is **moving downward, not dying**. Application-level XA across heterogeneous systems has lost ground to Sagas and the transactional outbox in microservice architectures. That happened because it couples availability across team boundaries and does not fit stateless, container-based deployment. At the same time, 2PC sits at the core of every distributed SQL engine (Spanner, CockroachDB, YugabyteDB, TiDB, Citus, Vitess), with consensus-replicated coordinators and latency optimizations such as Parallel Commits. Interest in letting streaming systems participate is also growing again: Kafka's KIP-939 targets the long-standing "write to the database and Kafka atomically" dual-write problem. If you work with any of these systems, 2PC explains their commit latency, their lock behavior, and their recovery tooling.

## Open questions / things to verify in practice

- With PostgreSQL, what does `pg_prepared_xacts` show after you `kill -9` the coordinator process between `PREPARE TRANSACTION` and `COMMIT PREPARED`? How long do the locks stay held, and does `VACUUM` visibly stall?
- What is the measured commit latency of 2PC versus a local commit on the same hardware? Is the difference closer to 1 RTT or 2 RTTs, given that `synchronous_commit` changes fsync behavior?
- Does a prepared transaction survive failover to a physical streaming replica, and who is responsible for resolving it after promotion?
- How does your chosen TM (Narayana, Atomikos, MSDTC) store its log, and what happens when that log's volume is lost?
- Under contention on one hot row, how does throughput degrade as you add a second and third participant?
- If you use Kafka, does your client/broker version actually ship KIP-939's `prepareTransaction()` / `completeTransaction()` APIs, and what timeout behavior applies in 2PC mode?
