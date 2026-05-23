# LSM Tree (Log-Structured Merge Tree) — MVP Code

The smallest runnable demo of an LSM tree. About 70 lines of actual code, comments excluded. SSTables are simulated in memory so it runs with no disk and no dependencies.

## What it demonstrates
- A **memtable** absorbs writes in a sorted buffer and **flushes** to an immutable, sorted **SSTable** when it fills (`Put` / `Flush`).
- The **read path** checks the memtable first, then SSTables newest-to-oldest, stopping at the first hit — "newest wins" (`Get`).
- **Delete is a tombstone**, not an in-place erase; it shadows older live values (`Delete`).
- **Compaction** k-way-merges all SSTables into one, keeping only the newest entry per key and reclaiming shadowed/tombstoned entries (`Compact`).

## Prerequisites
- .NET SDK 8.0+ (`dotnet --version` should print 8.x or newer).
- No packages, no database, no `docker run` — SSTables are in-memory.

## Run it

```bash
dotnet run --project mvp.csproj
```

## Expected output

```
Writing keys (flush fires every 3 writes):
  [flush] memtable -> SSTable #0 (1 on disk)
  [flush] memtable -> SSTable #1 (2 on disk)

Reads across 2 SSTables (newest wins):
  get(apple  ) = green
  get(banana ) = (deleted/absent)
  get(cherry ) = dark-red
  get(grape  ) = (deleted/absent)

Compacting all SSTables into one:
  [compact] 1 SSTable now; 3 shadowed/tombstoned entries reclaimed

Reads after compaction (same answers, fewer files):
  get(apple  ) = green
  get(banana ) = (deleted/absent)
  get(cherry ) = dark-red
  get(date   ) = brown
```

## What to try next
- Change `memtableLimit: 3` to `1` and watch a flush fire on every single write (more SSTables, more read fan-out).
- Comment out the `db.Compact()` call and confirm reads still return the same answers — compaction is about reclaim, not correctness.
- Add `db.Put("banana", "back");` after the delete and observe the tombstone is itself shadowed by the new value.
- Reverse the read loop to scan SSTables oldest-first and watch `apple` return the stale `red` — the scan direction *is* the newest-wins rule.
