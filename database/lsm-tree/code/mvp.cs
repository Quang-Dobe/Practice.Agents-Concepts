// LSM Tree (Log-Structured Merge Tree) — smallest runnable demo.
//
// What this file proves, mapped to docs/02-deep-dive.md:
//   - The MEMTABLE absorbs all writes in memory, kept sorted by key.
//   - A FLUSH turns a full memtable into an immutable, sorted SSTABLE.
//   - The READ PATH checks the memtable first, then SSTables newest -> oldest,
//     stopping at the first hit ("newest wins").
//   - A DELETE is a TOMBSTONE entry that shadows older live values; it is not
//     an in-place erase.
//   - COMPACTION k-way-merges all SSTables into one, keeping only the newest
//     entry per key and physically dropping tombstoned/shadowed keys.
//
// SSTables are simulated as in-memory sorted dictionaries so the demo runs with
// only `dotnet run` — no disk, no dependencies. The algorithm is identical to a
// real engine; only the persistence medium is faked.

namespace LsmTree;

// A stored value carries a sequence number so newer writes always win, and an
// IsTombstone flag so a delete is a real entry rather than a missing key.
// `record` because it is an immutable value, per the .NET conventions.
internal readonly record struct Entry(long Seq, string? Value, bool IsTombstone);

// The whole engine is one small class — no abstraction earns its keep at this size.
internal sealed class LsmEngine(int memtableLimit)
{
    // Active in-memory buffer. SortedDictionary stands in for the skip list /
    // balanced tree a real engine uses; the point is "sorted by key".
    private readonly SortedDictionary<string, Entry> _memtable = new(StringComparer.Ordinal);

    // On-disk segments, oldest first. Index 0 is the oldest SSTable, so we scan
    // this list in REVERSE on reads to honour newest-wins.
    private readonly List<SortedDictionary<string, Entry>> _ssTables = [];

    // Monotonic clock for ordering writes across memtable + every SSTable.
    private long _seq;

    public void Put(string key, string value) => Write(key, new Entry(++_seq, value, IsTombstone: false));

    // A delete writes a tombstone — it does NOT remove the key. Older copies in
    // older SSTables still exist; the tombstone just shadows them on reads.
    public void Delete(string key) => Write(key, new Entry(++_seq, Value: null, IsTombstone: true));

    private void Write(string key, Entry entry)
    {
        _memtable[key] = entry; // overwrite in the active buffer is free; it's RAM

        // When the memtable fills, flush it verbatim to a new immutable SSTable
        // and start a fresh empty memtable. This is the only moment data "hits disk".
        if (_memtable.Count >= memtableLimit)
            Flush();
    }

    private void Flush()
    {
        // Copy the sorted memtable into a new sealed segment, then clear the buffer.
        _ssTables.Add(new SortedDictionary<string, Entry>(_memtable, StringComparer.Ordinal));
        _memtable.Clear();
        Console.WriteLine($"  [flush] memtable -> SSTable #{_ssTables.Count - 1} ({_ssTables.Count} on disk)");
    }

    // Returns the live value, or null if the key is absent OR deleted.
    public string? Get(string key)
    {
        // 1. Freshest data lives in the memtable — check it first.
        if (_memtable.TryGetValue(key, out var hit))
            return Resolve(hit);

        // 2. Then SSTables newest -> oldest. First match wins; we stop immediately,
        //    because anything older is shadowed by what we already found.
        for (var i = _ssTables.Count - 1; i >= 0; i--)
            if (_ssTables[i].TryGetValue(key, out hit))
                return Resolve(hit);

        return null; // key was never written
    }

    // A tombstone counts as "found, but deleted" -> the caller sees null.
    private static string? Resolve(Entry e) => e.IsTombstone ? null : e.Value;

    // Compaction: k-way merge of every SSTable into ONE. We walk segments
    // newest -> oldest and keep the FIRST entry seen per key (the newest), then
    // physically drop tombstones since there is no older segment left to shadow.
    public void Compact()
    {
        var merged = new SortedDictionary<string, Entry>(StringComparer.Ordinal);

        for (var i = _ssTables.Count - 1; i >= 0; i--)
            foreach (var (key, entry) in _ssTables[i])
                merged.TryAdd(key, entry); // TryAdd ignores older duplicates -> newest wins

        // Drop tombstoned keys: after a full merge nothing older survives, so the
        // delete marker has done its job and can be reclaimed.
        var tombstonedKeys = merged.Where(kv => kv.Value.IsTombstone).Select(kv => kv.Key).ToList();
        foreach (var key in tombstonedKeys)
            merged.Remove(key);

        var dropped = _ssTables.Sum(t => t.Count) - merged.Count;
        _ssTables.Clear();
        _ssTables.Add(merged);
        Console.WriteLine($"  [compact] {_ssTables.Count} SSTable now; {dropped} shadowed/tombstoned entries reclaimed");
    }

    public int SsTableCount => _ssTables.Count;
}

internal static class Program
{
    private static void Main()
    {
        // Tiny limit so a flush happens every 3 writes and we can watch segments pile up.
        var db = new LsmEngine(memtableLimit: 3);

        Console.WriteLine("Writing keys (flush fires every 3 writes):");
        db.Put("apple", "red");
        db.Put("banana", "yellow");
        db.Put("cherry", "dark-red"); // <- triggers flush #0
        db.Put("apple", "green");     // update: shadows the old "red" in SSTable #0
        db.Delete("banana");          // tombstone: shadows "yellow" in SSTable #0
        db.Put("date", "brown");      // <- triggers flush #1 (memtable now has 3)

        Console.WriteLine($"\nReads across {db.SsTableCount} SSTables (newest wins):");
        Print(db, "apple");  // green  — newer SSTable beats older
        Print(db, "banana"); // (deleted) — tombstone shadows the live value
        Print(db, "cherry"); // dark-red — only one copy, found in oldest SSTable
        Print(db, "grape");  // (absent) — never written

        Console.WriteLine("\nCompacting all SSTables into one:");
        db.Compact();

        Console.WriteLine($"\nReads after compaction (same answers, fewer files):");
        Print(db, "apple");  // green
        Print(db, "banana"); // (deleted) -> now physically gone, still reads as deleted
        Print(db, "cherry"); // dark-red
        Print(db, "date");   // brown
    }

    private static void Print(LsmEngine db, string key)
    {
        var value = db.Get(key);
        Console.WriteLine($"  get({key,-7}) = {value ?? "(deleted/absent)"}");
    }
}
