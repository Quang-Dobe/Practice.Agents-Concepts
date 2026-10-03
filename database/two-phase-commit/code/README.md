# Two-Phase Commit — MVP Code

The smallest runnable demo of 2PC (presumed abort): one coordinator, two simulated databases, all three with fsync'd write-ahead logs. About 105 lines of C#, not counting comments or brace-only lines. No database server is needed. It is one flat console app, like `database/mvcc`, because the lesson here is the protocol, not the layering.

## What it demonstrates
- **Prepared state is a promise.** A participant forces `PREPARED` to disk before it votes YES. After that it can't decide alone, and its row stays locked (`02-deep-dive.md`, "Why it looks the way it does").
- **The commit point** is the coordinator's forced `COMMIT` record. Aborts write nothing (step 4 of "How it works under the hood").
- **The crash-point table**: a crash after the commit point gets fixed by re-sending COMMIT. A crash before it gets fixed by presumed abort.
- **Blocking**: while the coordinator is down, the in-doubt participant votes NO on any other transaction that wants the same row.

## Prerequisites
- .NET SDK 8.0+ (it also runs on 9/10 through `RollForward`). There are no NuGet packages.

## Run it

```bash
cd database/two-phase-commit/code && dotnet run
```

## Expected output (abridged)
```
== 2. A votes NO: move 500 from A to B ==
  A: vote NO on tx2 (insufficient funds)
  B: vote YES on tx2 (PREPARED forced to disk)
  coordinator: ABORT tx2 (nothing logged, presumed abort)
== 3. Coordinator crashes AFTER forcing COMMIT ==
  state: A=70 [in-doubt tx3, locked]   B=30 [in-doubt tx3, locked]
  B: vote NO on tx-other (row locked by in-doubt tx3)
  coordinator: found COMMIT tx3 without END, re-sending COMMIT
  state: A=50   B=50
== 4. Coordinator crashes BEFORE the commit point ==
  A: in-doubt tx4, coordinator says ABORT
```

## What to try next
- Open the `.log` files in the `Logs:` folder printed at the end and match each line to a protocol step.
- Delete `coordinator.log` between the crash and `Boot()` in scenario 3 (a lost TM log). Presumed abort now rolls back a transaction that had already committed.
- Change `Crash.AfterDecision` to `Crash.None` in scenario 3. The `tx-other` probe now votes YES because no lock is held, and on restart B asks about it and gets ABORT.
- Insert `a.Abort("tx3")` before `Recover()` in scenario 3 (the "janitor cron"). This produces a heuristic mixed outcome: B commits, and A never applies the debit.
