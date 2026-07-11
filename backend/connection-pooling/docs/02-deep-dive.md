# Connection Pooling — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

A connection pool is a bounded, thread-safe cache of already-established client-server sessions (typically database sessions over TCP, with TLS and authentication completed) that the application borrows via an `acquire()` call and returns via `release()`. The pool guarantees an upper bound on concurrent physical connections, arbitrates contention between callers via a queue or semaphore, and takes responsibility for connection health, lifetime, and eviction.

At the protocol level a "connection" here is not a raw TCP socket — it is a fully warmed database session: TCP established, TLS negotiated (TLS 1.3 = 1-RTT, TLS 1.2 = 2-RTT plus asymmetric-crypto CPU cost), authentication completed (SCRAM-SHA-256, MD5, IAM token, etc.), and session defaults (search_path, timezone, application_name) applied. Reusing that session is the whole point.

### The core building blocks

- **Idle queue / free list** — the data structure holding released connections available for reuse (typically a lock-free stack in HikariCP's `ConcurrentBag`, an unbounded channel in Npgsql, an `asyncio.Queue` in asyncpg).
- **Concurrency limiter (semaphore)** — bounds outstanding acquisitions to `MaxPoolSize`; blocks or times out callers past that limit.
- **Physical connector** — the driver-specific object that owns the socket, the protocol state machine, and the parser.
- **Housekeeper / eviction thread** — periodic task that closes connections past `idleTimeout`, `maxLifetime`, or failing health checks. HikariCP runs this every 30 seconds by default.
- **Health-check hook** — a lightweight validation query (`SELECT 1`, `SELECT 1 FROM DUAL`, `;` for PgBouncer's `server_check_query`) or a protocol-level `Sync` used to prove the connection is still alive before handing it out.
- **Wait queue / acquire timeout** — governs the fairness and failure behavior when the pool is saturated. Missing this correctly is how "the app just hangs forever."

### How it relates to the broader landscape

Connection pools sit inside a wider family of **resource-lending patterns**: HTTP keep-alive, thread pools, gRPC channel pools, Redis client pools, S3 SDK connection reuse. All of them exist because setup dominates cost. Within database access specifically, an in-app pool is one option; the sibling technology is an **external / server-side pooler** (PgBouncer, RDS Proxy, ProxySQL, Pgpool-II) that concentrates thousands of client connections into a much smaller set of backend sessions. The two coexist — an app has its own pool of connections to PgBouncer, and PgBouncer has its own pool to Postgres.

## Where

### Where it runs / lives in the stack

- **Driver / client library** in the application process (JDBC + HikariCP, Npgsql, `psycopg`, `asyncpg`, `sqlx`, `pgx`, node-postgres). This is the default for stateful services (containers, VMs, JVM, .NET, long-lived Python/Go workers).
- **A separate process on the same host or a nearby node** (PgBouncer as a sidecar, ProxySQL as a DaemonSet).
- **A managed service at the network edge of the database** (Amazon RDS Proxy, Azure Database for PostgreSQL built-in PgBouncer, Google Cloud SQL Auth Proxy — though the last is auth, not pooling, and this trips people up).
- **Inside the database engine itself** for a small number of systems (SQL Server has a form of internal connection multiplexing; MySQL's thread pool plugin is adjacent but not the same as connection pooling).

### Where you typically encounter it

- Spring Boot / Quarkus JVM services — HikariCP is the default since Spring Boot 2.0.
- ASP.NET Core apps — Npgsql and `Microsoft.Data.SqlClient` pool by default per connection string.
- Django/FastAPI with SQLAlchemy — `QueuePool` is the default engine pool.
- Node.js with `pg`, `mysql2`, `mongodb` — all ship built-in pooling.
- Ruby on Rails — ActiveRecord's connection pool is per-process, keyed by thread.
- Serverless functions in front of RDS Proxy — because a Lambda cannot hold a warm pool across invocations, the pooling is delegated to the proxy.

### Ecosystem and tooling

- **In-process pools (JVM):** HikariCP (default), Apache DBCP2, c3p0 (legacy).
- **In-process pools (.NET):** Npgsql's built-in pool with optional multiplexing, `Microsoft.Data.SqlClient` pool.
- **In-process pools (Python):** SQLAlchemy `QueuePool` / `AsyncAdaptedQueuePool`, `psycopg_pool`, `asyncpg`'s `Pool`.
- **In-process pools (Node.js):** `pg.Pool`, `mysql2`'s `createPool`, `generic-pool`.
- **External poolers (Postgres):** PgBouncer, Pgpool-II, Odyssey (Yandex), Supavisor (Supabase), pgcat (Rust).
- **External poolers (MySQL):** ProxySQL, MaxScale, MySQL Router.
- **Managed:** AWS RDS Proxy, Azure Database for PostgreSQL Flexible Server built-in pooler, Neon's built-in pooler, PlanetScale's Vitess-based pooling.
- **Observability:** HikariCP's `HikariPoolMXBean`, Micrometer's `hikaricp.*` metrics, `pg_stat_activity`, PgBouncer's `SHOW POOLS`, RDS Proxy CloudWatch `DatabaseConnectionsBorrowed` and `PinnedConnectionsClosed`.

## When

### When the topic emerged and why

Pooling as a pattern predates the web — object pools were in mainstream OO literature by the mid-1990s (Grand's *Patterns in Java*, 1998). Database connection pools arrived with the first serious server-side web platforms: JDBC 2.0 Optional Package (1999) formalized `javax.sql.DataSource` and connection pooling contracts; Apache DBCP appeared in 2001; c3p0 shortly after. The motivating problem was that early CGI-style architectures opened a fresh connection per HTTP request, and Postgres/Oracle simply could not sustain the resulting churn — the per-connection cost is not just latency, it is a full backend process in Postgres's fork-per-connection model.

External poolers followed a second wave, once PaaS and serverless made "thousands of tiny clients, one database" the dominant shape: PgBouncer (2007), ProxySQL (2015), RDS Proxy (2020).

### When to use it in a project

Reach for it when:

- You have any long-lived process handling more than a handful of DB-touching requests per second.
- Connection setup shows up in your latency budget (typically anything over ~1% of P50 latency is worth removing).
- Your database is Postgres, which pays a heavy per-connection RAM cost (~10 MB per backend process).
- You have a fleet of stateless workers (Lambdas, ECS tasks that scale to thousands) — reach for an **external pooler** in addition to any in-process pool.

### When NOT to use it

Avoid it (or avoid non-default tuning) when:

- The workload is a one-shot script, cron job, or migration tool — one connection, run, exit.
- Each caller genuinely needs isolated session state (temp tables, `SET LOCAL TIMEZONE`, session advisory locks) *and* you cannot scope it to a transaction — a pool will hand your session to the next unsuspecting caller.
- You are behind a transaction-mode PgBouncer and the app relies on named prepared statements, `LISTEN/NOTIFY`, `WITH HOLD` cursors, or session advisory locks — those are broken by transaction pooling unless you route them through a separate direct connection.

## How

### How it works under the hood

An `acquire()` on a modern in-process pool (HikariCP, Npgsql) follows roughly this lifecycle:

1. **Fast path.** Try to grab an idle connection from the free list. In HikariCP this is a thread-local scan of `ConcurrentBag` before touching shared state; typical uncontended cost is sub-microsecond.
2. **Semaphore check.** If nothing is idle, decrement a permit against `MaxPoolSize`. If no permit is available, the caller enters the wait queue.
3. **Grow.** If a permit is available and the pool is below its ceiling, open a new physical connection: TCP SYN → TLS handshake → auth exchange → connection-init SQL. This is the expensive path — 20–100ms on a healthy LAN, more across regions, and 5–10x more CPU on the DB than a resumed session.
4. **Validation.** Optionally run a liveness check. HikariCP short-circuits this using JDBC4 `Connection.isValid(timeoutSec)`, which for Postgres becomes a lightweight `Sync` round-trip; PgBouncer runs `server_check_query` (defaults to `SELECT 1;` if `server_check_delay` is exceeded).
5. **Hand off.** Caller uses the connection. When they call `close()` (or `Dispose()` in .NET, or exit the `with` block in Python), the wrapper returns the physical connection to the pool rather than actually closing it.
6. **Reset.** Before reuse, session state must be scrubbed — HikariCP calls `Connection.setAutoCommit`, `setTransactionIsolation`, `setReadOnly`, `setCatalog` back to defaults; PgBouncer runs `server_reset_query` (default `DISCARD ALL`) between transactions in session mode.
7. **Eviction.** The housekeeper thread periodically closes connections past `maxLifetime` (HikariCP default 30 minutes) or `idleTimeout` (default 10 minutes) so that DNS changes, credential rotations, and failovers eventually propagate.

External poolers add a step in front of steps 1–7: PgBouncer keeps a pool of *server* connections to Postgres, and multiplexes many *client* connections onto them. In **transaction pooling mode** (the common production mode), a client gets a server connection only for the duration of a `BEGIN..COMMIT` and is otherwise detached; `PREPARE`, `SET`, `LISTEN`, and `WITH HOLD` cursors will silently misbehave. Since PgBouncer 1.21 (Oct 2023), protocol-level prepared statements are tracked with `max_prepared_statements` and can survive transaction pooling — but SQL-level `PREPARE` still cannot.

RDS Proxy behaves similarly but calls the exception **pinning**: when it sees behavior it cannot multiplex safely (temp tables, session variables, `SET`, prepared statements on some engines), it pins the client to that backend connection for the rest of the client's session, defeating pooling. `DatabaseConnectionsCurrentlySessionPinned` in CloudWatch is the metric to watch.

### Key trade-offs

| Choice | Gained | Given up |
|---|---|---|
| Bigger `MaxPoolSize` | More app-side concurrency before queuing | DB memory + context-switch overhead; can cause worse throughput past a threshold (~`2 * cores + spindles` per HikariCP guidance) |
| Aggressive validation query | Fewer stale-connection errors | Extra RTT on every acquire — kills sub-ms use cases |
| Long `maxLifetime` | Fewer reconnect storms | Slower propagation of DB failover, DNS changes, and rotated IAM tokens |
| In-app pool only | Simple, no extra hop | Scales badly past ~thousands of app instances hitting one DB |
| External pooler in transaction mode | 10-100x more app clients per DB | Breaks prepared statements, session state, `LISTEN/NOTIFY`, some cursors |
| Multiplexing (Npgsql, RDS Proxy) | Uses far fewer physical connections | Pinning gotchas; not compatible with everything (e.g. large `COPY`) |

### Common failure modes

- **Pool exhaustion under a slow query.** One slow query holds a connection; incoming traffic drains the pool; every subsequent request times out at `acquireTimeout`. Cause: no query timeout, and `MaxPoolSize` too low relative to `Throughput × Latency` (Little's Law: `concurrency = λ × W`).
- **Connection leak.** A code path throws before `release()` / `Dispose()`; the connection is never returned. Cause: missing `using`/`try-with-resources`/context manager.
- **Stale connection after failover.** DB fails over to a replica; the pool still holds sockets pointing to the old primary. Next query returns `ECONNRESET` or hangs until TCP timeout. Cause: no server-side `tcp_keepalives_idle`, no client `maxLifetime`, or too-long OS `net.ipv4.tcp_keepalive_time` (default 7200 seconds on Linux).
- **Prepared-statement collision through PgBouncer transaction mode.** `prepared statement "s0" already exists`. Cause: driver-managed prepared statements assumed a stable backend that transaction pooling took away.
- **Pinning storm on RDS Proxy.** `SET application_name` in every connection pins every client; effective pool ratio collapses to 1:1. Cause: session-level `SET` instead of `SET LOCAL`, or unavoidable prepared statements pre-multiplexing support.
- **Thundering herd on cold start.** App starts, `MinIdle=0`, first N requests all try to open connections simultaneously. Cause: no warmup, or misconfigured `MinimumIdle`.
- **Pool-size / thread-pool mismatch.** App has 200 worker threads, pool has 20 connections; the 180 threads waiting on the pool are not doing work but are counted as "busy" upstream. Cause: treating pool sizing as "match your threads" instead of "match your DB's actual concurrency capacity."

## Why

### Why it exists

Three first-principles reasons:

1. **Setup cost dominates for short queries.** A `SELECT id FROM users WHERE id=?` on an indexed column is ~0.2 ms of DB work. Paying 30 ms for TCP+TLS+auth to run it is 150x overhead. Amortizing that setup across thousands of queries is the whole game.
2. **Databases have hard concurrency ceilings.** Postgres forks a full backend process per connection (~10 MB RSS). At 500 connections you have spent 5 GB on stack space, and only ~`2 × cores` of them can run CPU-bound work simultaneously anyway. The pool exists to *cap* concurrency, not just to save handshakes.
3. **Applications need bounded, back-pressured access to a shared, scarce resource.** A pool is a semaphore-with-objects; the queueing behavior is what gives operators a knob for graceful degradation instead of a runaway that kills the DB.

### Why it looks the way it does

The obvious alternative is "one connection per thread, forever" — the Apache prefork model. That fails on two fronts: (a) modern async frameworks have tens of thousands of logical threads over a small OS thread pool, so per-thread ownership is meaningless; and (b) it makes the pool size an emergent property of load, not a policy, which lets one buggy client kill a database shared with everyone else.

A less obvious alternative is **multiplexing at the driver level** — one physical connection serves many concurrent commands via pipelining. That is exactly what Npgsql's experimental multiplexing and RDS Proxy do. It works, but it inherits the same limitations: any per-session state (temp tables, `SET`, prepared statements, cursors, transactions) forces the driver to *pin* one physical connection to one logical caller — which is a pool, just with more indirection. Pools survived because "one caller owns one connection for the duration of a transaction" is the coarsest granularity at which most session state can be safely reasoned about.

The queue-plus-semaphore design won because it maps cleanly onto Little's Law (`L = λ W`): given a target request rate and observed DB latency, you can compute the *minimum* pool size that avoids queueing, and cap it there. Any larger and you are trading DB CPU for nothing.

### Why it matters now

In 2026 the shape of DB traffic has bimodalized: on one end, always-on Kubernetes workloads with well-behaved in-process pools; on the other, serverless and edge functions that cannot hold state across invocations at all. Both patterns are growing, and both are why external poolers (RDS Proxy, Supavisor, Neon's built-in pooler, PlanetScale's Vitess) became first-class Postgres/MySQL infrastructure rather than a niche tool. Understanding pool internals is also the difference between "our DB just fell over" and "our DB is fine, the app's `MaxPoolSize` was too high" — a distinction that decides whether the on-call fix is a config change or a hardware upgrade.

## Open questions / things to verify in practice

- What is my DB's real concurrency ceiling? Run a `pgbench` or equivalent load test and find the point where P99 latency inflects — that is your true `MaxPoolSize`, not `2 × cores + 1`.
- Does my app leak connections under failure paths? Force exceptions in handlers and watch pool active/idle counts.
- What happens on a controlled failover? Trigger one in staging and measure how long the pool serves stale sockets before recovery — this is where `maxLifetime` and TCP keepalive settings earn their keep.
- Am I accidentally pinning connections through my external pooler? Check `PinnedConnectionsClosed` on RDS Proxy or `SHOW POOLS` on PgBouncer during peak load.
- Is my prepared-statement behavior compatible with the pooler mode? Turn on statement logging and confirm the driver is using protocol-level prepares that survive `DISCARD ALL`.
- Is the pool actually the bottleneck? Instrument `acquire()` latency separately from query latency; if acquire time is >1 ms in steady state, the pool is undersized *or* something upstream is holding connections too long.
