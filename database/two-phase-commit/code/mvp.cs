// Two-Phase Commit (presumed abort) — smallest runnable demo.
//
// What this file proves:
//   Two "databases" (participants A and B, one bank account each) and one coordinator
//   move money atomically. Every node keeps a write-ahead log on disk, and a "restart"
//   throws away all in-memory objects and rebuilds them from those logs alone.
//
//   1. Happy path      — both vote YES, the coordinator forces COMMIT, both commit.
//   2. One votes NO    — A can't cover the debit, so B (which voted YES) is rolled back.
//   3. Crash AFTER the commit point — participants are stuck in-doubt holding their lock
//      (the blocking problem). On restart the coordinator finds COMMIT without END and
//      re-sends COMMIT.
//   4. Crash BEFORE the commit point — no COMMIT record exists, so participants that ask
//      are told ABORT. That rule is "presumed abort".

using System.Text;

var dir = Path.Combine(Path.GetTempPath(), "2pc-demo");
if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); // fresh logs every run
Directory.CreateDirectory(dir);

var (coord, a, b) = Boot();

Console.WriteLine("== 1. Happy path: move 30 from A to B ==");
coord.Run("tx1", [(a, -30), (b, +30)]);
Show();

Console.WriteLine("\n== 2. A votes NO: move 500 from A to B ==");
coord.Run("tx2", [(a, -500), (b, +500)]);
Show();

Console.WriteLine("\n== 3. Coordinator crashes AFTER forcing COMMIT ==");
try { coord.Run("tx3", [(a, -20), (b, +20)], Crash.AfterDecision); }
catch (CrashException e) { Console.WriteLine($"  !! {e.Message}"); }
Show();
// B already voted YES, so it gave up the right to decide alone. Its row stays locked.
b.Prepare("tx-other", +1);

// Power-cycle everything. Only what was fsync'd survives.
(coord, a, b) = Boot();
Console.WriteLine("  -- restarted from logs --");
Show(); // still in-doubt: the PREPARED record was durable
Recover();
Show();

Console.WriteLine("\n== 4. Coordinator crashes BEFORE the commit point ==");
try { coord.Run("tx4", [(a, -10), (b, +10)], Crash.BeforeDecision); }
catch (CrashException e) { Console.WriteLine($"  !! {e.Message}"); }
(coord, a, b) = Boot();
Console.WriteLine("  -- restarted from logs --");
Recover();
Show();

Console.WriteLine($"\nLogs: {dir}");

(Coordinator, Participant, Participant) Boot() =>
    (new Coordinator(dir), new Participant("A", 100, dir), new Participant("B", 0, dir));

void Recover()
{
    coord.Recover([a, b]);              // coordinator pushes decisions it already logged
    a.AskOutcome(coord);                // participants pull anything still in-doubt
    b.AskOutcome(coord);
}

void Show() => Console.WriteLine($"  state: {a}   {b}");

enum Crash { None, BeforeDecision, AfterDecision }

sealed class CrashException(string message) : Exception(message);

static class Wal
{
    // force = fsync before returning. 2PC correctness rests on which writes are forced
    // BEFORE the next message is sent.
    public static void Append(string path, string record, bool force)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        fs.Write(Encoding.UTF8.GetBytes(record + "\n"));
        fs.Flush(flushToDisk: force);
    }

    public static List<string[]> Read(string path) =>
        File.Exists(path) ? File.ReadLines(path).Select(l => l.Split(' ')).ToList() : [];
}

sealed class Participant
{
    public string Name { get; }
    int _balance;
    (string Xid, int Delta)? _prepared; // non-null = in-doubt, and the row lock is held
    readonly string _log;

    public Participant(string name, int openingBalance, string dir)
    {
        Name = name;
        _balance = openingBalance;
        _log = Path.Combine(dir, $"participant-{name}.log");
        // Restart = replay the WAL. A PREPARED with no COMMIT/ABORT after it comes back
        // as in-doubt, lock re-acquired. That is the "prepared state survives restarts" rule.
        foreach (var r in Wal.Read(_log))
        {
            if (r[0] == "PREPARED") _prepared = (r[1], int.Parse(r[2]));
            else if (r[0] == "COMMIT") { _balance += _prepared!.Value.Delta; _prepared = null; }
            else if (r[0] == "ABORT") _prepared = null;
        }
    }

    // Phase 1. YES is a promise: from now on only the coordinator decides.
    public bool Prepare(string xid, int delta)
    {
        if (_prepared is { } p) return Vote(xid, false, $"row locked by in-doubt {p.Xid}");
        if (_balance + delta < 0) return Vote(xid, false, "insufficient funds");
        Wal.Append(_log, $"PREPARED {xid} {delta}", force: true); // promise hits disk BEFORE "YES"
        _prepared = (xid, delta);
        return Vote(xid, true, "PREPARED forced to disk");
    }

    // Phase 2. Idempotent, because recovery may re-send the same COMMIT.
    public void Commit(string xid)
    {
        if (_prepared?.Xid != xid) return;
        Wal.Append(_log, $"COMMIT {xid}", force: true);
        _balance += _prepared.Value.Delta;
        _prepared = null;                                         // lock released only now
        Console.WriteLine($"  {Name}: COMMIT {xid}, ack");
    }

    public void Abort(string xid)
    {
        if (_prepared?.Xid != xid) return;
        Wal.Append(_log, $"ABORT {xid}", force: false); // not forced: if lost, asking again yields ABORT anyway
        _prepared = null;
        Console.WriteLine($"  {Name}: ABORT {xid}");
    }

    public void AskOutcome(Coordinator c)
    {
        if (_prepared is not { } p) return;
        var commit = c.Outcome(p.Xid);
        Console.WriteLine($"  {Name}: in-doubt {p.Xid}, coordinator says {(commit ? "COMMIT" : "ABORT")}");
        if (commit) Commit(p.Xid); else Abort(p.Xid);
    }

    bool Vote(string xid, bool yes, string why)
    {
        Console.WriteLine($"  {Name}: vote {(yes ? "YES" : "NO")} on {xid} ({why})");
        return yes;
    }

    public override string ToString() =>
        $"{Name}={_balance}" + (_prepared is { } p ? $" [in-doubt {p.Xid}, locked]" : "");
}

sealed class Coordinator(string dir)
{
    readonly string _log = Path.Combine(dir, "coordinator.log");

    public void Run(string xid, (Participant P, int Delta)[] work, Crash crash = Crash.None)
    {
        // Phase 1. Presumed abort: the coordinator writes NOTHING before sending PREPARE.
        // Ask everyone (no early exit) so a YES-voter can be seen getting rolled back.
        var yes = work.Where(w => w.P.Prepare(xid, w.Delta)).Select(w => w.P).ToList();

        if (crash == Crash.BeforeDecision) throw new CrashException("coordinator died before logging a decision");

        if (yes.Count < work.Length)
        {
            Console.WriteLine($"  coordinator: ABORT {xid} (nothing logged, presumed abort)");
            yes.ForEach(p => p.Abort(xid)); // only YES-voters hold anything to undo
            return;
        }

        // THE COMMIT POINT. Once this fsync returns, the outcome can never change.
        Wal.Append(_log, $"COMMIT {xid} {string.Join(',', yes.Select(p => p.Name))}", force: true);
        Console.WriteLine($"  coordinator: COMMIT {xid} forced to disk (commit point)");

        if (crash == Crash.AfterDecision) throw new CrashException("coordinator died before sending COMMIT");

        yes.ForEach(p => p.Commit(xid));
        Wal.Append(_log, $"END {xid}", force: false); // all acked; safe to forget
    }

    // Presumed abort in one line: no COMMIT record means the answer is ABORT.
    public bool Outcome(string xid) => Wal.Read(_log).Any(r => r[0] == "COMMIT" && r[1] == xid);

    // Re-drive phase 2 for every decision that was logged but never finished.
    public void Recover(Participant[] all)
    {
        var records = Wal.Read(_log);
        foreach (var c in records.Where(r => r[0] == "COMMIT"))
        {
            if (records.Any(r => r[0] == "END" && r[1] == c[1])) continue;
            Console.WriteLine($"  coordinator: found COMMIT {c[1]} without END, re-sending COMMIT");
            foreach (var name in c[2].Split(',')) all.Single(p => p.Name == name).Commit(c[1]);
            Wal.Append(_log, $"END {c[1]}", force: false);
        }
    }
}
