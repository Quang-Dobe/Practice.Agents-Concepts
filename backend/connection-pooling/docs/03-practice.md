# Connection Pooling — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, the connection pool sits at the exact seam between "stateless request handlers" and "one shared, stateful, expensive Postgres cluster." It is the shock absorber that decides whether a Black Friday traffic spike shows up as slower responses or as a database that stops accepting new sessions.

In a Kubernetes-hosted service, it is the thing whose configuration silently multiplies with every replica: `MaxPoolSize=20` looks fine until the HPA scales you from 4 pods to 40 and Postgres suddenly sees 800 connections. In serverless, it is the thing you *don't* have — which is why RDS Proxy or Supavisor exists as a substitute.

In a data-heavy internal tool (analytics dashboards, admin panels, batch reporters), the pool is what stops a runaway CSV export from starving the transactional workload of connections. The knobs — `acquireTimeout`, per-connection statement timeout, separate pools per workload — are how you keep the two apart.

If you have ever seen a P99 latency graph that is flat for a week and then vertical for 90 seconds, there is a good chance a saturated pool was the cause.

## Best practices

### 1. Size the pool from Little's Law, not from your thread count
**Do:** Compute `MaxPoolSize ≈ peak_QPS × avg_query_seconds`, then sanity-check against the DB's real capacity — for Postgres, HikariCP's rule `connections = ((core_count × 2) + effective_spindle_count)` per DB instance is a good ceiling, not a target.
**Why:** Sizing to thread count (200 threads → 200 connections) turns your app pool into an unbounded amplifier that kills the DB before back-pressure ever reaches the client.
**Avoid:** Copy-pasting `MaxPoolSize=100` from a Stack Overflow answer and never revisiting it.

### 2. Budget for *all* your app replicas, not one
**Do:** Track `total_connections = replicas × MaxPoolSize + external_pooler_reserve` against the DB's `max_connections` (minus superuser and replication slots) as a first-class SLO. Recompute it whenever the HPA max changes.
**Why:** In production, the failure mode is not "my pool is too big" — it is "my pool times HPA-max is too big," and it only fires during the incident that already scaled you out.
**Avoid:** Letting the HPA and the pool config live in different repos owned by different teams with no shared budget.

### 3. Always set an acquire timeout — and make it shorter than upstream
**Do:** Set `connectionTimeout` (HikariCP) / `Timeout` (Npgsql) / `pool_timeout` (SQLAlchemy) to something like 2–5 seconds, strictly less than your HTTP request timeout and any upstream retry budget.
**Why:** Without it, a slow query drains the pool and every subsequent request hangs indefinitely, holding threads, memory, and load-balancer slots. With it, you fail fast, shed load, and the LB routes around you.
**Avoid:** The library default of "wait forever." It is the single most common cause of "the app is up but nothing works" incidents.

### 4. Set a statement timeout on the connection itself
**Do:** Configure `SET statement_timeout` (Postgres) or `command_timeout` (Npgsql/asyncpg) at pool init, and a shorter one for read-only paths. Make it tighter than your acquire timeout.
**Why:** A pool exhaustion incident almost always starts with one query that never returns. The statement timeout is what caps blast radius from a bad plan or missing index.
**Avoid:** Trusting the ORM's default (usually none) and hoping the app-level HTTP timeout will save you — it won't; the connection is still held.

### 5. Keep `maxLifetime` shorter than any network idle timeout in the path
**Do:** Set `maxLifetime` to 20–30 minutes on cloud environments (HikariCP default is 30 min; Npgsql's `Connection Lifetime` defaults to 0 — set it explicitly). It must be less than the DB's `idle_in_transaction_session_timeout`, AWS NLB's 350s idle timeout, and any firewall's connection reaper.
**Why:** Long-lived TCP sockets get silently reset by NAT gateways and load balancers. Without lifetime rotation, the first query after a quiet period returns `Connection reset by peer` and blows up a request.
**Avoid:** `maxLifetime=0` ("forever"). That worked on a bare-metal LAN in 2010. It does not work behind three layers of AWS networking.

### 6. Warm the pool at startup — don't cold-start into a stampede
**Do:** Set `MinimumIdle` / `min_size` to a non-zero value (often 25–50% of `MaxPoolSize`) so cold pods finish their TLS handshakes *before* traffic arrives. Combine with a readiness probe that runs one `SELECT 1` through the pool.
**Why:** After a rolling deploy or an HPA scale-up, the first burst of traffic hits an empty pool and every request pays the 30–100 ms handshake cost simultaneously — P99 latency triples for 60 seconds and PagerDuty wakes up.
**Avoid:** `MinimumIdle=0` on any service that ever autoscales. It optimizes for idle cost at the expense of every scaling event.

### 7. Understand your PgBouncer / RDS Proxy mode before you deploy behind it
**Do:** If you are in PgBouncer **transaction mode**, verify: (a) driver uses protocol-level extended-query prepared statements, not `PREPARE ... EXECUTE`; (b) app does not rely on `LISTEN/NOTIFY`, session advisory locks, `SET` (use `SET LOCAL`), temp tables outside a transaction, or `WITH HOLD` cursors. On RDS Proxy, watch `DatabaseConnectionsCurrentlySessionPinned` — non-zero in steady state means multiplexing is being defeated.
**Why:** Transaction pooling silently breaks the assumption "the connection I used last statement is the same one I'm using now." You get bugs that only appear under load and never in staging.
**Avoid:** Turning on PgBouncer as a "just add it, what could go wrong" migration. It is a semantic change, not a transparent proxy.

### 8. Right-size the app pool *smaller* when using an external pooler
**Do:** If you have PgBouncer/RDS Proxy in the path, your in-app pool sizes to app concurrency, not DB capacity — often `MaxPoolSize=10–20` per pod is enough. The external pooler owns the DB-side ceiling.
**Why:** Doubling up big pools defeats the whole point of the external pooler and turns the pooler into an idle connection warehouse.
**Avoid:** Leaving the app pool at 100 "just in case" while PgBouncer sits in front with `default_pool_size=25`. You are paying for both and getting the smaller of the two.

### 9. Instrument acquire latency, saturation, and wait queue depth
**Do:** Export `pool.active`, `pool.idle`, `pool.pending` (waiters), and a histogram of acquire time. Alert on `acquire_p99 > 50ms` for more than 5 minutes and on `pending > 0` sustained. Micrometer's `hikaricp.*` metrics or Npgsql's `pool.*` counters give you this for free.
**Why:** Query latency alone hides pool contention — a fast query that waited 900 ms for a connection looks like a slow query. Separating "time waiting for a connection" from "time running the query" is the difference between fixing the DB and fixing the pool.
**Avoid:** Monitoring only DB-side `pg_stat_activity`. It tells you what happened once queries reached the DB; it says nothing about what queued in the app.

### 10. Use separate pools for separate workloads
**Do:** Run a small dedicated pool for background jobs, migrations, or long analytical queries, distinct from the request-serving pool. In .NET, use a second connection string (different `Application Name`, different pool). In Java, register a second `DataSource`.
**Why:** A single 30-second analytical query on the shared pool holds one of your precious request-serving connections. With separate pools, the worker's saturation is invisible to the API.
**Avoid:** "One pool for everything" — it is simpler on day one and destroys you the first time a report job runs at 9am.

### 11. Retire connections on rotating credentials and failovers
**Do:** When using IAM DB auth, rotate the auth token, and set `maxLifetime` well under the token TTL (15 min for RDS). On failover, either rely on `maxLifetime` churn or explicitly drain-and-reopen — most drivers do not detect failover without a query round-trip failure first.
**Why:** After a failover, half the pool is holding sockets to a promoted-then-demoted node. Without lifetime rotation, error rates hover at 5% for hours "for no reason."
**Avoid:** Assuming the load balancer or DNS TTL will save you. The pool holds the socket, not the hostname.

## Anti-patterns to recognize

- **Pool per request:** Constructing a new `HikariDataSource` or `NpgsqlConnection` pool inside a request handler. Every request pays the pool-init cost plus a fresh TLS handshake; you have "connection pooling" that pools nothing. Build the pool once at app startup and inject it.
- **Sharing a pool across forked processes:** Creating the pool before `fork()` (Gunicorn preload mode, `uwsgi --lazy-apps=false`). Child processes inherit sockets and race on them, producing "unexpected message" or protocol de-sync errors. Initialize the pool *after* fork, in a post-fork hook.
- **Pool size equals thread count:** Sizing so every worker thread can hold a connection simultaneously. You lose all back-pressure and turn the DB into the bottleneck. Size the pool to DB capacity; let threads queue on it.
- **Ignoring TLS handshake cost in the metric budget:** Reporting "P50 = 8ms" from a benchmark where the pool was pre-warmed, then being surprised when production P99 is 120 ms. The handshake cost lives in the tail; measure with cold pods too.
- **Wrapping `using`/`Dispose` around the wrong object:** In .NET, `using var conn = pool.CreateConnection()` but forgetting the `using` on the `NpgsqlDataReader`. The reader holds the connection; leak the reader, leak the connection. Same story with JDBC `ResultSet`.
- **`SET` at session scope through a transaction pooler:** `SET application_name = 'reports'` on every acquire, behind PgBouncer transaction mode. The setting either leaks to the next tenant or (on RDS Proxy) pins the connection, silently collapsing multiplexing. Use `SET LOCAL` inside a transaction, or set it via connection-string options.
- **Health check that hits the DB on every acquire:** `SELECT 1` on every checkout doubles round-trips for sub-ms queries. Use JDBC4 `isValid()` or Npgsql's built-in keepalives, and reserve validation for connections idle longer than `N` seconds.
- **Retry-on-failure that re-acquires without releasing:** A retry loop that catches the exception, calls `acquire()` again, but the failing connection is still in the caller's local variable. Two acquires per failed request drains the pool in seconds under load.

## Real-world usage patterns

- **High-traffic B2C API on Kubernetes (Postgres RDS):** 40 pods, HikariCP `MaxPoolSize=15`, RDS Proxy in front with `MaxConnectionsPercent=80`. Total app-visible = 600, DB-side multiplexed to ~120. Lesson: the app pool sizes to *per-pod* concurrency (roughly cores × 2), not to the DB. RDS Proxy is what makes the total safe.
- **Multi-tenant SaaS with per-tenant schemas:** Uses `SET search_path` per request. Migrated to PgBouncer transaction mode and immediately saw cross-tenant data leaks in staging. Fix: switch to schema-qualified queries, or wrap every request in a transaction with `SET LOCAL`. Lesson: session-scoped state and transaction pooling are actively hostile to each other.
- **Analytics ingest worker (Java, Kafka consumer):** Long-running consumer, small pool (`MaxPoolSize=4`), `maxLifetime=15min`. Deliberately undersized to avoid overwhelming the shared warehouse. Batches 500 rows per transaction. Lesson: pool size for workers is a throttle, not a throughput dial — sometimes smaller is the right answer.
- **Serverless image-processing Lambdas:** Cannot hold a pool. Every function uses a single connection through RDS Proxy. Under a burst of 2,000 concurrent invocations, RDS Proxy borrows ~50 backend connections. Lesson: without the proxy, 2,000 concurrent Lambdas would open 2,000 direct connections and instantly exhaust the DB.
- **Multi-region read replica routing:** Two DataSources per app — primary for writes, read-replica for reads — each with its own pool, its own `maxLifetime`, and its own health check. Separate acquire-time histograms in Grafana. Lesson: never share a pool across endpoints; a replica failure should not poison the primary pool.

## Operational checklist

- [ ] `MaxPoolSize × replicas` (plus any external pooler reserve) is under the DB's `max_connections`, with headroom for admin and replication.
- [ ] Every pool has a finite `connectionTimeout` / `acquire_timeout`, tighter than the caller's HTTP timeout.
- [ ] A `statement_timeout` is set on every connection at pool init, per workload.
- [ ] `maxLifetime` is set and shorter than the shortest idle timeout in the network path (LB, NAT, firewall) and any credential TTL.
- [ ] Pool metrics exported: active, idle, waiters, acquire-time histogram (p50/p95/p99).
- [ ] Alerts on sustained waiter count > 0 and acquire p99 > 50 ms.
- [ ] A load test has actually driven the pool to saturation in staging, and the failure mode was verified (fast reject, not hang).
- [ ] If behind PgBouncer/RDS Proxy: confirmed pooling mode, driver's prepared-statement mode, and pinned-connections metric is ~0 at steady state.
- [ ] Pool is constructed once at app startup (or post-fork), never per-request.
- [ ] Runbook exists for "the DB is at connection limit" — includes commands to identify which app is holding sessions (`pg_stat_activity.application_name`).

## How this topic typically evolves in a codebase

Most projects start with library defaults: `Data Source=…` in the connection string, no explicit pool config, everything pooled per connection string by accident. This is fine to about 50 RPS and one pod. The first migration point comes when someone deploys a second replica and Postgres suddenly sees 200 connections — the team learns that pool size is per-process, not global.

Stage two is a config sprawl phase: `MaxPoolSize`, `MinimumIdle`, timeouts, health checks all get added ad-hoc, usually during incidents, often copied from unrelated services. The pool config file grows a comment block explaining what a past on-call learned the hard way. At around 20–100 replicas, teams hit the "add PgBouncer or RDS Proxy" cliff — usually because `max_connections` on the DB is being raised for the third time and someone realizes RAM is the bottleneck.

Stage three is workload separation: read replicas get their own pool, background jobs get a smaller dedicated pool, migrations run through a direct connection that bypasses the pooler entirely. This is also where teams discover that transaction-mode pooling broke `LISTEN/NOTIFY` two years ago and nobody noticed. The painful migration point is not adding the pooler — it is auditing every `SET`, prepared statement, and long-lived cursor in the codebase to make it pool-safe.

## Further reading

- [About Pool Sizing — HikariCP wiki](https://github.com/brettwooldridge/HikariCP/wiki/About-Pool-Sizing) — the canonical short essay on why small pools beat large ones. Read once, remember forever.
- [PgBouncer FAQ](https://www.pgbouncer.org/faq.html) — the "what breaks in transaction mode" list you should re-read before any migration.
- [Amazon RDS Proxy — Managing pinning](https://docs.aws.amazon.com/AmazonRDS/User/rds-proxy.html#rds-proxy-pinning) — the definitive list of SQL patterns that defeat multiplexing on RDS Proxy.
- [Npgsql connection string parameters](https://www.npgsql.org/doc/connection-string-parameters.html) — the actual defaults for .NET pooling, including the subtle ones (`Maximum Pool Size` defaults to 100, `Connection Lifetime` to 0).
- [How Little's Law applies to database performance — Percona blog](https://www.percona.com/blog/) — practical framing of `concurrency = throughput × latency` for DBAs sizing pools.
- [The Ultimate Guide to Connection Pooling — Vlad Mihalcea](https://vladmihalcea.com/the-anatomy-of-connection-pooling/) — long-form, JVM-flavored but universally applicable; includes the "Flexy Pool" adaptive-sizing case study.
