# Write-Ahead Log — MVP Code

Smallest runnable demo of a WAL-backed key-value store: every mutation is fsync'd to `wal.log` **before** the in-memory dictionary is updated, and startup replays the log to recover committed state. ~120 lines across four Clean Architecture projects.

## What it demonstrates

- The **WAL invariant**: `Append + fsync` runs before the in-memory `Set/Remove` in every handler (`Application/Store/PutCommand.cs`).
- **Crash recovery via replay**: `RecoverCommand` rebuilds state from the log at startup — see `../docs/02-deep-dive.md § How` for the full ARIES-style REDO pass this miniatures.
- **Idempotent replay**: PUTs overwrite, so replaying the same log twice yields the same state.
- **Clean Architecture + hand-rolled Mediator** across `Domain / Application / Infrastructure / Console`.

## Prerequisites

.NET SDK **8.0+** (`dotnet --version`). No external services — `wal.log` is created next to the executable.

## Run it

```bash
dotnet run --project Console                    # fresh: 5 ops complete cleanly
rm Console/bin/Debug/net8.0/wal.log             # start over
dotnet run --project Console -- --crash         # kills itself after 2 committed ops
dotnet run --project Console                    # replays 2 records, then finishes the rest
```

## Expected output (recovery run after `--crash`)

```
Recovered 2 record(s) from .../wal.log
State after recovery: { user:1=alice, user:2=bob }
  applied op 1: PUT user:1 alice
  ...
Final state: { user:1=alice-v2, user:3=carol }
```

## What to try next

- Comment out `_stream.Flush(flushToDisk: true)` in `FileWriteAheadLog.Append` and re-run with `--crash`. Recovery may lose records — that is the missing fsync.
- Swap the order in `PutHandler.Handle` (mutate first, then log) and reason about what a crash between the two lines now costs.
- Delete only the last line of `wal.log`, then run recovery. That is what a mid-write crash looks like.
