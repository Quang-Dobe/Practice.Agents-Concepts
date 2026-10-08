// Transaction Isolation Levels — minimal runnable demo.
//
// What this file proves: the same five interleavings of two transactions
// produce an anomaly at a weak level and stop producing it at a stronger one.
// It prints the anomaly-by-level matrix from 02-deep-dive.md, computed live.
//
// The "database" is a tiny in-process key/value engine with row versions.
// It is modelled on a snapshot (MVCC) engine such as PostgreSQL:
//   READ UNCOMMITTED -> sees other transactions' uncommitted writes
//                       (PostgreSQL does not offer this; MySQL / SQL Server do)
//   READ COMMITTED   -> fresh snapshot for every statement
//   REPEATABLE READ  -> one snapshot per transaction + write-write conflict check
//                       = Snapshot Isolation, which is what PostgreSQL ships
//   SERIALIZABLE     -> Snapshot Isolation + a read-write conflict check
//                       (a simplified stand-in for PostgreSQL's SSI)
//
// Every interleaving is scripted step by step, so the output is deterministic.
// No threads: concurrency here means "two transactions are open at once".

string[] anomalies = ["dirty read", "non-repeatable read", "phantom read", "lost update", "write skew"];
Func<Db, Level, bool>[] scenarios = [DirtyRead, NonRepeatableRead, Phantom, LostUpdate, WriteSkew];

Console.WriteLine($"{"anomaly",-21}" + string.Concat(Enum.GetNames<Level>().Select(n => $"{n,-17}")));
for (var i = 0; i < scenarios.Length; i++)
{
    Console.Write($"{anomalies[i],-21}");
    foreach (var level in Enum.GetValues<Level>())
        Console.Write($"{Run(scenarios[i], level),-17}");
    Console.WriteLine();
}
Console.WriteLine("\nANOMALY = wrong result  ok = prevented  abort = prevented by a serialization failure (client must retry)");

// A serialization failure is how a stronger level says no: it does not block,
// it aborts one transaction. Catching it here is the "retry wrapper" moment.
static string Run(Func<Db, Level, bool> scenario, Level level)
{
    try { return scenario(new Db(), level) ? "ANOMALY" : "ok"; }
    catch (SerializationFailure) { return "abort"; }
}

// P1. T2 reads a value T1 wrote but then rolled back: a value that never existed.
static bool DirtyRead(Db db, Level level)
{
    db.Seed("x", 100);
    var t1 = db.Begin(level); var t2 = db.Begin(level);
    t1.Write("x", 999);
    var seen = t2.Read("x");
    t1.Rollback(); t2.Commit();
    return seen == 999;
}

// P2. Same row, same transaction, two different answers.
static bool NonRepeatableRead(Db db, Level level)
{
    db.Seed("x", 100);
    var t1 = db.Begin(level);
    var first = t1.Read("x");
    var t2 = db.Begin(level); t2.Write("x", 200); t2.Commit();
    var second = t1.Read("x");
    t1.Commit();
    return first != second;
}

// P3. Same predicate ("all doctors"), same transaction, a different row set.
static bool Phantom(Db db, Level level)
{
    db.Seed("doc:alice", 1); db.Seed("doc:bob", 1);
    var t1 = db.Begin(level);
    var before = t1.Scan("doc:").Count;
    var t2 = db.Begin(level); t2.Write("doc:carol", 1); t2.Commit(); // INSERT
    var after = t1.Scan("doc:").Count;
    t1.Commit();
    return before != after;
}

// P4. Both read 0, both write 0 + 1, both commit: one increment silently vanishes.
static bool LostUpdate(Db db, Level level)
{
    db.Seed("counter", 0);
    var t1 = db.Begin(level); var t2 = db.Begin(level);
    var v1 = t1.Read("counter"); var v2 = t2.Read("counter");
    t1.Write("counter", v1!.Value + 1); t1.Commit();
    t2.Write("counter", v2!.Value + 1); t2.Commit(); // SI: same row, concurrent writer -> abort
    return db.Latest("counter") != 2;
}

// A5B. The on-call doctors from 01-overview.md. Invariant: at least one doctor on call.
// Each transaction checks the invariant, then takes a DIFFERENT doctor off call.
static bool WriteSkew(Db db, Level level)
{
    db.Seed("doc:alice", 1); db.Seed("doc:bob", 1); // 1 = on call
    var t1 = db.Begin(level); var t2 = db.Begin(level);
    var aliceMayLeave = t1.Scan("doc:").Sum() >= 2; // both check BEFORE either writes:
    var bobMayLeave = t2.Scan("doc:").Sum() >= 2;   // that is the race window
    if (aliceMayLeave) t1.Write("doc:alice", 0);
    if (bobMayLeave) t2.Write("doc:bob", 0);
    t1.Commit();
    t2.Commit(); // disjoint rows: SI sees no write-write conflict, only the rw check catches it
    return db.Latest("doc:alice") + db.Latest("doc:bob") == 0;
}

enum Level { ReadUncommitted, ReadCommitted, RepeatableRead, Serializable }

sealed class SerializationFailure(string why) : Exception(why);

sealed class Db
{
    // Every committed write appends a version stamped with its commit time.
    // A null value would be a delete; this demo never deletes.
    internal readonly Dictionary<string, List<(long Ts, int? Value)>> Versions = [];
    internal readonly List<Tx> Active = [];
    internal long Clock;

    public void Seed(string key, int value) { var t = Begin(Level.ReadCommitted); t.Write(key, value); t.Commit(); }
    public Tx Begin(Level level) { var tx = new Tx(this, level, Clock); Active.Add(tx); return tx; }
    public int? Latest(string key) => AsOf(key, Clock);

    // The visibility rule: the newest version committed at or before the snapshot.
    internal int? AsOf(string key, long snapshot) =>
        Versions.TryGetValue(key, out var vs) ? vs.LastOrDefault(v => v.Ts <= snapshot).Value : null;

    // "Did anyone commit a write to this key after my snapshot?" Drives both conflict checks.
    internal bool ChangedSince(string key, long snapshot) =>
        Versions.TryGetValue(key, out var vs) && vs.Any(v => v.Ts > snapshot);
}

sealed class Tx(Db db, Level level, long start)
{
    readonly Dictionary<string, int> _writes = [];   // buffered, uncommitted
    readonly HashSet<string> _readKeys = [];
    readonly HashSet<string> _readPrefixes = [];     // a predicate read, for phantoms

    // THE knob. Read Committed re-snapshots on every statement; Repeatable Read
    // and Serializable keep the snapshot taken at Begin for the whole transaction.
    long Snapshot => level >= Level.RepeatableRead ? start : db.Clock;

    public void Write(string key, int value) => _writes[key] = value;

    public int? Read(string key)
    {
        _readKeys.Add(key);
        if (_writes.TryGetValue(key, out var own)) return own; // read your own writes
        if (level == Level.ReadUncommitted)                       // peek at other transactions' pencil marks
            foreach (var other in db.Active)
                if (other != this && other._writes.TryGetValue(key, out var dirty)) return dirty;
        return db.AsOf(key, Snapshot);
    }

    public List<int> Scan(string prefix)
    {
        _readPrefixes.Add(prefix);
        var keys = db.Versions.Keys.Concat(_writes.Keys);
        if (level == Level.ReadUncommitted) keys = keys.Concat(db.Active.SelectMany(t => t._writes.Keys));
        return keys.Where(k => k.StartsWith(prefix)).Distinct()
                   .Select(Read).OfType<int>().ToList();
    }

    public void Commit()
    {
        db.Active.Remove(this);
        if (level >= Level.RepeatableRead)
        {
            // Snapshot Isolation: two concurrent writers of the same row cannot both commit.
            // (PostgreSQL makes the 2nd writer wait, then aborts it: first-updater-wins.
            //  With no threads, checking at commit gives the same outcome.)
            if (_writes.Keys.Any(k => db.ChangedSince(k, start)))
                throw new SerializationFailure("could not serialize access due to concurrent update");
        }
        if (level == Level.Serializable && _writes.Count > 0)
        {
            // rw-antidependency: something I READ was overwritten by a transaction that
            // committed after my snapshot, and I am about to write based on it. Abort.
            // Simplified: real SSI waits for TWO consecutive rw edges (fewer false aborts),
            // and also tracks read-only transactions. This check skips read-only ones.
            var stale = _readKeys.Any(k => db.ChangedSince(k, start))
                     || db.Versions.Keys.Any(k => _readPrefixes.Any(k.StartsWith) && db.ChangedSince(k, start));
            if (stale) throw new SerializationFailure("could not serialize access due to read/write dependencies");
        }
        db.Clock++;
        foreach (var (k, v) in _writes)
            (db.Versions.TryGetValue(k, out var vs) ? vs : db.Versions[k] = []).Add((db.Clock, v));
    }

    public void Rollback() => db.Active.Remove(this); // buffered writes are simply dropped
}
