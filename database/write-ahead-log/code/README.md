# Write-Ahead Log — MVP Code

Minimal runnable demo of a WAL-backed key-value store.

## What it demonstrates

- The WAL rule: every mutation is appended and `fsync`'d to `wal.log` *before* memory is updated.
- Crash recovery: startup replays `wal.log` on top of `snapshot.txt` to rebuild state.
- `crash-test`: mutates memory without writing the log, so the change is lost on the next run.
- `checkpoint`: snapshots state to disk, then truncates the log.

## Prerequisites

.NET SDK 8.0+. No DB, no Docker, no packages.

## Run it — crash-and-recover walkthrough

```bash
cd code

dotnet run -- put name alice
dotnet run -- put role admin
dotnet run -- dump
#   name = alice
#   role = admin
#   -- 2 key(s), wal=30 B, snap=0 B

# Simulate a crash: mutate memory only, exit without appending.
dotnet run -- crash-test
dotnet run -- get ghost
#   (not found)   <-- never reached the log; recovery never sees it

dotnet run -- checkpoint
dotnet run -- dump
#   -- 2 key(s), wal=0 B, snap=22 B
```

## Try next

- Delete `snapshot.txt`, run `dump` — recovery works from `wal.log` alone.
- Comment out `fs.Flush(flushToDisk: true)` and reason about new crash windows.
