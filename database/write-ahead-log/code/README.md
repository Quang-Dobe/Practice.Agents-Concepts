# Write-Ahead Log — MVP Code

The smallest runnable demo of a WAL-backed key-value store. About 90 lines of actual Python, comments excluded, standard library only.

## What it demonstrates

- **The log-before-page invariant** — every mutation is appended to the WAL and `fsync`'d BEFORE the in-memory `pages` dict is updated (`KVStore._append`).
- **Force-log-at-commit** — the `os.fsync(f.fileno())` call is the exact instant a write becomes durable; the next line only runs once that returns.
- **Checkpoint bounds recovery** — `checkpoint()` snapshots current pages to a separate file, then truncates the WAL. Recovery only replays records with `lsn > checkpoint.lsn`.
- **REDO recovery** — `_recover()` loads the snapshot, then reads WAL records in order, verifying each per-record CRC and stopping at the first torn/corrupt tail. This is ARIES' "repeat history" step, minimal-form.
- **Crash simulation** — the child subprocess calls `os._exit(137)` after a few writes; no atexit hooks, no `finally`, no stdio flush. Only what `fsync`'d to the WAL survives.

## Prerequisites

- Python 3.11+
- Standard library only. No `pip install` needed.

The demo writes two files under `/tmp/`: `/tmp/wal_demo.log` and `/tmp/wal_demo.snap`. Each run wipes them at the top.

## Run it

```bash
python3 mvp.py
```

## Expected output

```
[parent] launching writer that will crash mid-workload...
[parent] writer exited with code 137 (simulated crash)

[parent] on disk after crash:  WAL=95B  snapshot=49B

[parent] reopening store (triggers snapshot load + WAL redo pass)...
[parent] recovered state:
    account:alice = 80
    account:bob = 70
    account:carol = 999
[parent] highest LSN replayed: 5
```

The invariant this proves: the child was killed with `os._exit` after writing three post-checkpoint records. On reopen, the store rebuilds `alice=80, bob=70, carol=999` exactly — not the pre-checkpoint values `alice=100, bob=50` from the snapshot, and not an empty state. Durability held across a hard crash because each `put` fsync'd its record to the WAL before returning.

## What to try next

- Comment out `os.fsync(f.fileno())` in `_append` and re-run — durability becomes best-effort; a real power loss (not `os._exit`) could now lose committed writes.
- Move `self.pages[key] = value` in `put()` to run BEFORE `_append(...)` and reason about the crash window it opens.
- Remove the `store.checkpoint()` line in `child_writer()` — the snapshot stays empty and every replay walks the entire WAL.
- Truncate `/tmp/wal_demo.log` with `truncate -s 90 /tmp/wal_demo.log` before reopening — watch the CRC check stop replay at the torn record instead of loading garbage.
