# Transaction Isolation Levels — MVP Code

The smallest runnable demo of isolation levels. Five scripted two-transaction interleavings run against a tiny in-process versioned key/value engine at all four ANSI levels, and the program prints the anomaly-by-level matrix. About 97 lines of C#, not counting comments or brace-only lines. You don't need a database server or Docker. Like the other database demos, it is one flat console app.

## What it demonstrates
- **The snapshot knob.** Read Committed takes a new snapshot for every statement, while Repeatable Read keeps one per transaction (`Tx.Snapshot`). That single line is what separates non-repeatable reads and phantoms from stable reads ("How it works under the hood", B in `02-deep-dive.md`).
- **Snapshot Isolation's write-write rule.** Repeatable Read aborts the second writer of the same row, so the lost update turns into `abort` instead of a silent overwrite.
- **Write skew gets past Snapshot Isolation.** In the on-call doctors case, each transaction writes a different row, so only Serializable's read-write conflict check catches it.
- **Stronger levels abort instead of blocking.** `abort` in the matrix is the serialization failure that your retry wrapper has to handle (`03-practice.md` #4).

## Prerequisites
- .NET SDK 8.0+ (it also runs on 9/10 through `RollForward`). There are no NuGet packages.

## Run it

```bash
cd database/transaction-isolation-levels/code && dotnet run
```

## Expected output
```
anomaly              ReadUncommitted  ReadCommitted    RepeatableRead   Serializable
dirty read           ANOMALY          ok               ok               ok
non-repeatable read  ANOMALY          ANOMALY          ok               ok
phantom read         ANOMALY          ANOMALY          ok               ok
lost update          ANOMALY          ANOMALY          abort            abort
write skew           ANOMALY          ANOMALY          ANOMALY          abort
```

The engine follows PostgreSQL's snapshot model, so phantoms are already gone at Repeatable Read. A lock-based engine such as SQL Server would still show phantoms at that level.

## What to try next
- Delete the write-write check in `Commit()` to get MySQL InnoDB Repeatable Read behaviour: lost update becomes `ANOMALY` in the Repeatable Read column.
- In `WriteSkew`, have Bob take `doc:alice` off call too (the same row). Repeatable Read now aborts it as a write-write conflict.
- Wrap `t2` in `WriteSkew` in a retry loop that re-runs the scan. On attempt 2 Bob sees one doctor on call and stays.
- Remove the `_writes.Count > 0` guard and watch Serializable abort the read-only phantom transaction. This is a false positive, which real SSI avoids.
