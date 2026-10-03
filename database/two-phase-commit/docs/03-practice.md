# Two-Phase Commit — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

Most engineers meet 2PC without knowing it. You run a sharded Postgres cluster on Citus, a Vitess keyspace, or a CockroachDB or Spanner database, and some `UPDATE` touches two shards. The engine quietly runs prepare and commit across the nodes. You only notice when commit latency jumps for multi-shard writes, or when an alert fires because `pg_prepared_xacts` has a row that is three hours old.

The second place is the enterprise Java or .NET tier. A Spring Boot service consumes a message from IBM MQ, writes to Oracle, and acknowledges the message, all inside one JTA transaction run by Atomikos or Narayana. A .NET service opens two SQL connections inside one `TransactionScope` and escalates to MSDTC. In these systems, 2PC is a library plus a log file on disk, and the log file is the part that bites.

The third place is hand-rolled "intent then commit" protocols: write a pending marker, write the record, flip the marker. These are 2PC in disguise and inherit all of its recovery duties.

## Best practices

### 1. Let the database run 2PC for you when you can
**Do:** Prefer a distributed SQL engine (CockroachDB, Spanner, YugabyteDB, Citus, Vitess) over application-level XA across separate databases.
**Why:** These engines replicate the coordinator's state with Raft or Paxos, so a single node crash does not leave in-doubt branches. They also ship their own recovery loop. With XA, you own the coordinator's uptime and its log.
**Avoid:** Stitching two independent Postgres instances together with a TM library because "it's just one more datasource."

### 2. Design the schema so 2PC is the slow path
**Do:** Co-locate data that is written together on one participant. In Citus, distribute every tenant-scoped table by `tenant_id` so a tenant's writes hit one worker and commit locally.
**Why:** A single-participant transaction skips both phases. If 80% of your writes are cross-shard, every one pays ≥ 2 round trips and 2n + 1 fsyncs, and the lock hold time on hot rows grows to match.
**Avoid:** Picking a shard key for read fan-out and finding out later that every write is a distributed transaction.

### 3. Put the coordinator log on durable storage with a stable identity
**Do:** Run XA coordinators as a Kubernetes StatefulSet with one PersistentVolume per pod, or use a TM that stores its log in a replicated database.
**Why:** If a pod is rescheduled and its log disappears, every branch it prepared stays in-doubt forever. Nobody else knows whether that GID committed.
**Avoid:** A Deployment with `emptyDir` for `tmlog/`, which works perfectly until the first node drain.

### 4. Give every TM instance a unique, stable node identifier
**Do:** Set Narayana's `node-identifier` or Atomikos's `com.atomikos.icatch.tm_unique_name` per instance (for example, the StatefulSet pod name), and keep it stable across restarts.
**Why:** Recovery scans `xa_recover` on every RM and claims branches whose XID matches its node ID. Two instances sharing an ID will roll back each other's in-flight branches. An ID that changes on restart orphans the old branches.
**Avoid:** Leaving the default identifier on every replica because the first deployment only had one.

### 5. Size `max_prepared_transactions` on purpose
**Do:** On Postgres participants, set it to at least `max_connections` (the official recommendation). Keep it at `0` on every database that should never see 2PC. Set standbys equal to or higher than the primary.
**Why:** Too low and `PREPARE TRANSACTION` fails under load. A standby set lower than the primary refuses queries. Leaving it at `0` on "non-2PC" databases makes accidental 2PC fail loudly. It requires a restart, so get it right before launch.
**Avoid:** Bumping it to 100 on every database "just in case."

### 6. Alert on in-doubt age, not just count
**Do:** Page when any branch has been prepared longer than a few minutes: `SELECT gid, prepared FROM pg_prepared_xacts WHERE prepared < now() - interval '5 minutes'`. Run the `XA RECOVER` equivalent on MySQL.
**Why:** An orphaned prepared transaction holds locks and pins the vacuum horizon. Dead tuples pile up, tables bloat, and in the worst case Postgres stops accepting writes to prevent XID wraparound. That incident shows up days after the cause.
**Avoid:** Monitoring only "TM is up," which stays green while branches rot.

### 7. Keep the transaction short and remote-call-free
**Do:** Do the expensive reads and computation before opening the distributed transaction. Inside it, only write.
**Why:** Locks are held for execute time + prepare RTT + 2 fsyncs + commit RTT. An HTTP call or a slow query inside the scope multiplies that. A hot row then caps throughput at roughly 1 / (lock hold time).
**Avoid:** Calling a pricing service between the debit and the credit "because it's all one unit of work."

### 8. Make heuristic resolution a runbook, not a reflex
**Do:** Before running `ROLLBACK PREPARED` or `COMMIT PREPARED` by hand, look up the GID in the coordinator's log. If a COMMIT record exists, commit. Otherwise abort. Record who did it.
**Why:** A DBA who rolls back a branch that the coordinator already committed creates a heuristic mixed outcome: money left one account and never arrived in the other. Nothing detects it automatically.
**Avoid:** "It's been stuck for an hour, just roll it back."

### 9. Encode traceability into the GID
**Do:** Build GIDs from TM node ID + timestamp + sequence, kept under Postgres's 200-byte limit, and log them with the business request ID.
**Why:** At 3 a.m., the on-call engineer looking at `pg_prepared_xacts` needs to know which service, which pod, and which request produced the row. A UUID with no context gives them nothing.
**Avoid:** Random opaque GIDs, or GIDs reused across TM restarts.

### 10. Kill the coordinator in a test environment before production does
**Do:** Run a chaos test that `kill -9`s the coordinator between prepare and commit, then assert that every participant converges to the same outcome within the recovery interval.
**Why:** Recovery code runs only during failures, so it is the least-exercised path in the system. Misconfigured node IDs, missing log volumes, and RM credentials missing from the recovery module only show up here.
**Avoid:** Treating "commit works on the happy path" as proof that 2PC works.

## Anti-patterns to recognize

- **Distributed monolith via XA**: Service A enlists Service B's database in its global transaction. B's locks now depend on A's coordinator uptime, and B's on-call gets paged for A's outages. Give each service its own database and use the transactional outbox or a Saga across the boundary.
- **The janitor cron**: A script runs `ROLLBACK PREPARED` on anything in `pg_prepared_xacts` older than N minutes. It "fixes" the vacuum alerts and silently breaks atomicity whenever the coordinator had already decided COMMIT. Fix the coordinator's recovery instead, and escalate stuck branches to a human with the coordinator log in hand.
- **"Prepare" over HTTP**: A team adds `/reserve` and `/confirm` endpoints and calls it 2PC. The reservation expires on a timer, so the participant can abort on its own after voting yes, which is exactly what the prepared state forbids. This is Try-Confirm-Cancel, a Saga variant; design it with compensations and idempotency, not 2PC guarantees.
- **Assuming 2PC gives a global snapshot**: Phase 2 does not land on every participant at the same instant. A reader that queries participant A, then B, can see A's commit and not yet B's. If readers need a consistent cross-node view, you need a database with global timestamps (Spanner, CockroachDB), not plain XA.
- **Retrying an unknown outcome as if it failed**: The client times out after the commit point, assumes failure, and re-runs the transfer. The first attempt commits during recovery, and the money moves twice. Attach an idempotency key to the business operation so the retry is a no-op.
- **Silent escalation**: In .NET, a second connection inside `TransactionScope` escalates to MSDTC. Since .NET 7 this throws unless `TransactionManager.ImplicitDistributedTransactions = true`. Teams flip the flag to make the exception go away and gain a Windows-only distributed transaction they never designed for. Treat the exception as a design review trigger.

## Real-world usage patterns

**Multi-tenant SaaS on Citus.** A B2B product with thousands of tenants distributes tables by `tenant_id`. Normal requests commit on one worker; cross-tenant admin jobs and reference-table updates go through 2PC, with the coordinator recording each prepared branch in `pg_dist_transaction` and a background process resolving leftovers. *Lesson:* the share of multi-shard transactions is a schema-design metric. Track it per endpoint, because one new feature that joins across tenants can turn the fast path into the slow path.

**Bank message processing on JTA.** A payments service reads from IBM MQ, writes to Oracle, and acknowledges the message in one XA transaction, so a crash never loses or double-applies a payment instruction. It ran fine on VMs for a decade. *Lesson:* the move to Kubernetes is where it breaks. Scaling a StatefulSet down removes a pod whose log still holds in-doubt branches. The WildFly operator handles this by refusing to terminate the pod (marking it `SCALING_DOWN_RECOVERY_DIRTY`) until recovery drains it. If your platform has nothing equivalent, scale-down is a data-integrity event.

**Globally distributed ledger on CockroachDB or Spanner.** A fintech keeps accounts in a multi-region cluster. Every transfer that touches two ranges runs the engine's 2PC variant over consensus groups. *Lesson:* latency is set by geography, not CPU. The commit pays a consensus round in the region holding the transaction record or coordinator leader, so pinning leaseholders or leaders near the writers matters more than any query tuning. CockroachDB's Parallel Commits cut this to one consensus round, but contention still surfaces as `40001` retry errors, so clients need a retry loop.

**Hand-rolled 2PC for strongly consistent indexes.** Uber's LedgerStore writes an index *intent*, then the record, then commits the intent asynchronously so the index never points at a record that does not exist. *Lesson:* once you build 2PC yourself, you own recovery. A record write that fails after the intent write leaves an intent that must be rolled back, or unused intents accumulate. Plan the sweeper before shipping the write path.

## Operational checklist

- Is there an alert on the age of the oldest prepared branch (`pg_prepared_xacts.prepared`, `XA RECOVER`), and does it page someone?
- Is the TM log on a persistent, backed-up volume, and has someone restored it on a fresh pod and watched recovery complete?
- Does every TM instance have a unique node ID that survives restarts, and is the recovery module configured with credentials for every RM?
- Is `max_prepared_transactions` ≥ `max_connections` on 2PC participants, `0` everywhere else, and ≥ primary on standbys?
- Has a `kill -9` between prepare and commit been run in staging, with all participants converging to the same outcome?
- Is there a written runbook for heuristic resolution that requires checking the coordinator log before any manual `COMMIT PREPARED` / `ROLLBACK PREPARED`?
- Are p99 commit latency and lock wait time tracked separately for single-participant and multi-participant transactions?
- Do business operations that can time out after the commit point carry an idempotency key?
- Cost: in a multi-region database, are multi-region writes priced into the latency budget and the cross-region transfer bill?
- Onboarding: does a new engineer know which code paths open a distributed transaction, and that "stuck prepared transaction" is never fixed by a blind rollback?

## How this topic typically evolves in a codebase

Teams start with one database and local transactions. Then a second store arrives (a broker, a second shard), and the first dual-write bug appears: the database committed and the message never went out. The quick fix is XA through `TransactionScope` or JTA. On long-lived VMs with local disks, this can run quietly for years.

The painful migration point is the platform shift. Containers break what XA relies on: stable host identity, a disk that outlives the process, and deliberate scale-down. Around the same time the organization splits into teams, and the shared global transaction couples their deploys and on-call. Teams then go one of two ways. Some replace cross-boundary XA with the transactional outbox plus idempotent consumers and accept eventual consistency. Others move the data that must be atomic into a distributed SQL engine with a replicated coordinator.

In most mature systems, application code rarely talks to a TM directly. 2PC still runs on every multi-shard commit, but inside the database. The team's remaining job is to keep most transactions single-shard and watch the in-doubt dashboard.

## Further reading

- [PostgreSQL: `PREPARE TRANSACTION`](https://www.postgresql.org/docs/current/sql-prepare-transaction.html) — short, and states plainly that the feature is for transaction managers only and why long-lived prepared transactions hurt `VACUUM`.
- [Gray & Lamport, *Consensus on Transaction Commit*](https://arxiv.org/abs/cs/0408036) — shows classic 2PC as Paxos Commit with zero fault tolerance, which explains why every modern engine replicates the coordinator.
- [CockroachDB: Parallel Commits](https://www.cockroachlabs.com/blog/parallel-commits/) — a production redesign of the commit path that halves consensus latency, with clear reasoning about the recovery cases.
- [Uber: How LedgerStore Supports Trillions of Indexes](https://www.uber.com/us/en/blog/how-ledgerstore-supports-trillions-of-indexes/) — a 2PC-style intent protocol at very large scale, including the cleanup it requires.
- [Citus: How Citus Executes Distributed Transactions](https://www.citusdata.com/blog/2017/11/22/how-citus-executes-distributed-transactions/) — how a Postgres extension drives `PREPARE TRANSACTION` across workers, plus distributed deadlock detection.
- [WildFly proposal: transactions during graceful shutdown](https://docs.wildfly.org/wildfly-proposals/transactions/WFLY-17742_SupportTransactionsDuringGracefulShutdown.html) — concrete detail on what XA needs from a container platform at shutdown and scale-down.
