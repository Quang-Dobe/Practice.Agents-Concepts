# MVCC — MVP Code

Smallest runnable demo of MVCC against a real engine: a flat .NET console app talking to Postgres via Npgsql. ~90 lines of C# excluding comments. No Clean Architecture — a two-transaction demo does not need it.

## What it demonstrates

- **Scenario 1 — snapshot stability under REPEATABLE READ.** T1 reads, T2 updates and commits, T1 re-reads and still sees its original value. The core MVCC behaviour from `02-deep-dive.md` "How → visibility rules" and the Google-Doc analogy in `01-overview.md`.
- **Scenario 2a — write skew is allowed under SI.** Two transactions both read the "at least one doctor on-call" invariant, each takes a *different* doctor off-call, both commit, invariant violated. Matches `03-practice.md` item #12.
- **Scenario 2b — write skew is rejected under SERIALIZABLE.** Same scenario, isolation bumped; Postgres SSI detects the dangerous rw-antidependency and aborts one transaction with SQLSTATE `40001`. `03-practice.md` item #7 in action.

## Prerequisites

- .NET SDK 8.0+.
- Docker, for a one-line Postgres on `127.0.0.1:5544`:

```bash
docker run --rm -d --name mvcc-pg -p 5544:5432 \
  -e POSTGRES_USER=mvcc -e POSTGRES_PASSWORD=mvcc -e POSTGRES_DB=mvcc \
  postgres:16
```

Stop it later with `docker rm -f mvcc-pg`.

## Run it

```bash
cd database/mvcc/code && dotnet run
```

## Expected output (abridged)

```
=== Scenario 1: REPEATABLE READ snapshot stability ===
T1 (REPEATABLE READ) sees balance = 100
T2 updated balance to 500 and COMMITTED.
T1 re-reads balance = 100  <-- still the snapshot value, not 500
Post-commit, a fresh read sees balance = 500

=== Scenario 2a: Write skew under REPEATABLE READ (anomaly allowed) ===
  Final on-call count = 0 <-- INVARIANT VIOLATED (write skew got through)

=== Scenario 2b: Write skew under SERIALIZABLE (SSI aborts one txn) ===
  Serialization failure caught: could not serialize access due to read/write dependencies among transactions
  Final on-call count = 1 <-- invariant preserved
```

## What to try next

- Switch Scenario 1 to `IsolationLevel.ReadCommitted` and watch T1's second read jump to 500.
- Point both `UPDATE` statements in Scenario 2 at the *same* doctor id and observe a write-write conflict instead of write skew.
- Run `SELECT xmin, xmax, balance FROM accounts;` in `psql` after Scenario 1 to see the version stamps.
- Wrap Scenario 2b in a retry loop (per `03-practice.md` #7) so both transactions eventually succeed.
