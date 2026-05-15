// =============================================================================
// Write-Ahead Log MVP
// -----------------------------------------------------------------------------
// A toy key-value store that demonstrates THE write-ahead invariant from the
// deep-dive doc:
//
//   "For every page modification P whose effect must survive a crash, the log
//    record describing P must reach stable storage BEFORE the modified page
//    itself does, and before the transaction's commit is acknowledged."
//
// We implement that invariant at its bare minimum:
//   1. Every mutation is appended to wal.log AND fsync'd to disk *before*
//      the in-memory dictionary is updated.
//   2. On startup we replay wal.log (plus the latest snapshot, if any) to
//      rebuild the in-memory state. This is "redo" recovery.
//   3. `crash-test` mutates state in memory and exits WITHOUT writing the
//      log, proving that anything not yet in the WAL is gone forever.
//   4. `checkpoint` writes a snapshot file and truncates the log — the same
//      job real engines do to keep recovery time bounded.
//
// Two files on disk live next to the executable:
//   wal.log       append-only newline-delimited records: "PUT k v" / "DEL k"
//   snapshot.txt  the latest checkpoint (k\tv per line)
// =============================================================================

using System.Globalization;
using System.Text;

// Paths are hard-coded next to cwd — this is a demo, not a daemon.
const string WalPath = "wal.log";
const string SnapshotPath = "snapshot.txt";

// -----------------------------------------------------------------------------
// Recovery: snapshot first, then replay every WAL record on top of it.
// This is the same ordering ARIES uses: start from the latest checkpoint,
// then redo all log records after it. Idempotency of PUT/DEL means we can
// safely replay even records that were already reflected in the snapshot.
// -----------------------------------------------------------------------------
var store = new Dictionary<string, string>(StringComparer.Ordinal);

if (File.Exists(SnapshotPath))
{
    foreach (var line in File.ReadAllLines(SnapshotPath))
    {
        // tab-separated so values may contain spaces
        var tab = line.IndexOf('\t');
        if (tab < 0) continue;
        store[line[..tab]] = line[(tab + 1)..];
    }
}

if (File.Exists(WalPath))
{
    foreach (var record in File.ReadAllLines(WalPath))
        Apply(store, record);
}

// -----------------------------------------------------------------------------
// Dispatch. One subcommand per invocation — keeps the demo about the WAL,
// not about CLI parsing.
// -----------------------------------------------------------------------------
var cmd = args.Length > 0 ? args[0] : "dump";

switch (cmd)
{
    case "put" when args.Length == 3:
        // The order matters: log first (durable), THEN mutate memory.
        // If the machine dies between AppendAndFsync and Apply, recovery still
        // sees the record and converges to the same state. If we did it the
        // other way around, a crash here would leave acknowledged state in
        // memory only — exactly what WAL exists to prevent.
        AppendAndFsync($"PUT {args[1]} {args[2]}");
        Apply(store, $"PUT {args[1]} {args[2]}");
        Console.WriteLine($"ok: {args[1]} = {args[2]}");
        break;

    case "del" when args.Length == 2:
        AppendAndFsync($"DEL {args[1]}");
        Apply(store, $"DEL {args[1]}");
        Console.WriteLine($"ok: deleted {args[1]}");
        break;

    case "get" when args.Length == 2:
        Console.WriteLine(store.TryGetValue(args[1], out var v) ? v : "(not found)");
        break;

    case "dump":
        // Show the recovered state. Useful after a crash to prove the log
        // was enough to rebuild memory.
        foreach (var kv in store.OrderBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine($"{kv.Key} = {kv.Value}");
        Console.WriteLine($"-- {store.Count} key(s), wal={Size(WalPath)} B, snap={Size(SnapshotPath)} B");
        break;

    case "checkpoint":
        // Write a fresh snapshot, fsync it, THEN truncate the log.
        // The order here is also load-bearing: if we truncated first and
        // then crashed before the snapshot landed, we'd lose history.
        Checkpoint(store);
        Console.WriteLine($"checkpoint ok: {store.Count} key(s) snapshotted, wal truncated");
        break;

    case "crash-test":
        // Deliberately violate durability: mutate memory and exit WITHOUT
        // writing the log. Run `dump` afterwards and you'll see the change
        // is gone — because the log never saw it. This is the negative
        // image of the WAL rule.
        store["ghost"] = "this-will-vanish";
        Console.WriteLine("crash-test: set ghost=this-will-vanish in MEMORY ONLY, exiting without WAL append");
        Environment.Exit(0);
        break;

    default:
        Console.Error.WriteLine("usage: wal <put k v | del k | get k | dump | checkpoint | crash-test>");
        Environment.Exit(2);
        break;
}

return;

// -----------------------------------------------------------------------------
// The whole point of the demo lives in these eight lines.
// -----------------------------------------------------------------------------
static void AppendAndFsync(string record)
{
    // FileShare.Read so a concurrent reader could tail; not load-bearing here.
    using var fs = new FileStream(
        WalPath,
        FileMode.Append,
        FileAccess.Write,
        FileShare.Read,
        bufferSize: 4096,
        // WriteThrough asks the OS to bypass its write cache for this handle.
        // Combined with the explicit Flush(true) below, that's our "fsync".
        options: FileOptions.WriteThrough);

    var bytes = Encoding.UTF8.GetBytes(record + "\n");
    fs.Write(bytes, 0, bytes.Length);

    // Flush(true) == FlushToDisk == fsync. This is the syscall the entire
    // durability guarantee of every WAL-backed database rests on. If your
    // storage lies about fsync (consumer SSDs, some virtualized disks),
    // this whole architecture silently degrades. See fsyncgate, 2018.
    fs.Flush(flushToDisk: true);
}

// Pure function: applies one log record to an in-memory map. Used both at
// runtime (after a successful append) and during recovery (replay). Same code
// path both times — that's how we know recovery and live writes can't diverge.
static void Apply(Dictionary<string, string> s, string record)
{
    var space = record.IndexOf(' ');
    var op = space < 0 ? record : record[..space];
    var rest = space < 0 ? "" : record[(space + 1)..];

    switch (op)
    {
        case "PUT":
            var sp2 = rest.IndexOf(' ');
            s[rest[..sp2]] = rest[(sp2 + 1)..];
            break;
        case "DEL":
            s.Remove(rest);
            break;
    }
}

static void Checkpoint(Dictionary<string, string> s)
{
    // Write to a temp file and atomically rename, so the snapshot is either
    // fully there or not there at all. Same pattern engines use for control
    // files.
    var tmp = SnapshotPath + ".tmp";
    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                   FileShare.None, 4096, FileOptions.WriteThrough))
    using (var w = new StreamWriter(fs, Encoding.UTF8))
    {
        foreach (var kv in s)
            w.Write($"{kv.Key}\t{kv.Value}\n");
        w.Flush();
        fs.Flush(flushToDisk: true);
    }
    File.Move(tmp, SnapshotPath, overwrite: true);

    // Only NOW is it safe to drop the log. A crash before this point still
    // has the old snapshot + the full WAL to replay from.
    if (File.Exists(WalPath))
        File.Delete(WalPath);
}

static string Size(string path) =>
    File.Exists(path)
        ? new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture)
        : "0";
