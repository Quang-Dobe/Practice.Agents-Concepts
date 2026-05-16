// MVCC (Multi-Version Concurrency Control) — minimal runnable demo.
//
// What this file proves, in order:
//   Scenario 1 (REPEATABLE READ snapshot stability):
//     T1 begins, reads accounts.balance for id=1. T2 begins, updates the same
//     balance, commits. T1 re-reads -- and still sees its original snapshot
//     value, even though T2's new version is live in the heap. That is MVCC:
//     T1's snapshot picks the old tuple version off the version chain because
//     T2's xid was not committed at T1's snapshot moment.
//
//   Scenario 2 (write skew under SI, blocked under SERIALIZABLE):
//     The classic "two doctors on call" anomaly from docs/02-deep-dive.md
//     and 03-practice.md item #12. Invariant: at least one doctor is on_call.
//     Two transactions concurrently check "are there >= 2 on call?", each
//     sees the answer yes, each takes a DIFFERENT doctor off-call. Under
//     REPEATABLE READ (snapshot isolation) both commit and the invariant is
//     violated. Under SERIALIZABLE (Postgres SSI) Postgres detects the
//     dangerous rw-antidependency structure and aborts one with SQLSTATE
//     40001 -- the application must retry.
//
// Run prerequisites are in code/README.md. Briefly: a local Postgres listening
// on 127.0.0.1:5544 with the credentials below, started via the docker command
// in the README. Schema is created/reset by Setup() on each run so the demo
// is idempotent.

using System.Data;
using Npgsql;

namespace MvccMvp;

internal static class Program
{
    // Hard-coded because they are not part of the concept being demonstrated.
    // Real apps read these from configuration.
    private const string ConnString =
        "Host=127.0.0.1;Port=5544;Username=mvcc;Password=mvcc;Database=mvcc;Include Error Detail=true";

    private static async Task Main()
    {
        await Setup();

        Console.WriteLine("=== Scenario 1: REPEATABLE READ snapshot stability ===");
        await SnapshotStabilityDemo();

        Console.WriteLine();
        Console.WriteLine("=== Scenario 2a: Write skew under REPEATABLE READ (anomaly allowed) ===");
        await WriteSkewDemo(IsolationLevel.RepeatableRead);

        Console.WriteLine();
        Console.WriteLine("=== Scenario 2b: Write skew under SERIALIZABLE (SSI aborts one txn) ===");
        await WriteSkewDemo(IsolationLevel.Serializable);
    }

    // -------------------------------------------------------------------------
    // Schema reset. Two tables, one row each used per scenario.
    // -------------------------------------------------------------------------
    private static async Task Setup()
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();

        // DROP-then-CREATE on every run keeps the demo idempotent. Real
        // migrations would never do this -- this is a learning script.
        const string sql = """
            DROP TABLE IF EXISTS accounts;
            DROP TABLE IF EXISTS doctors;

            CREATE TABLE accounts (id INT PRIMARY KEY, balance INT NOT NULL);
            INSERT INTO accounts (id, balance) VALUES (1, 100);

            CREATE TABLE doctors (id INT PRIMARY KEY, name TEXT, on_call BOOLEAN);
            INSERT INTO doctors VALUES (1, 'Alice', TRUE), (2, 'Bob', TRUE);
        """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // -------------------------------------------------------------------------
    // Scenario 1: snapshot stability under REPEATABLE READ.
    //
    // Postgres REPEATABLE READ takes one snapshot at the first SELECT of the
    // transaction and reuses it for every subsequent read in that transaction.
    // That is the definition of snapshot isolation. Note: under READ COMMITTED
    // T1's second SELECT WOULD see T2's new value -- try changing the isolation
    // level below to confirm.
    // -------------------------------------------------------------------------
    private static async Task SnapshotStabilityDemo()
    {
        await using var conn1 = new NpgsqlConnection(ConnString);
        await using var conn2 = new NpgsqlConnection(ConnString);
        await conn1.OpenAsync();
        await conn2.OpenAsync();

        // T1: open a REPEATABLE READ transaction and take a snapshot by reading.
        await using var t1 = await conn1.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var balanceBefore = (int)(await new NpgsqlCommand(
            "SELECT balance FROM accounts WHERE id = 1", conn1, t1).ExecuteScalarAsync())!;
        Console.WriteLine($"T1 (REPEATABLE READ) sees balance = {balanceBefore}");

        // T2: concurrent transaction updates and commits while T1 is still open.
        await using (var t2 = await conn2.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await new NpgsqlCommand(
                "UPDATE accounts SET balance = 500 WHERE id = 1", conn2, t2).ExecuteNonQueryAsync();
            await t2.CommitAsync();
            Console.WriteLine("T2 updated balance to 500 and COMMITTED.");
        }

        // T1 re-reads. Even though T2's new tuple version is now the "current"
        // one in the heap, T1's snapshot was taken before T2 committed, so the
        // visibility check walks the version chain and returns the older tuple.
        var balanceAfter = (int)(await new NpgsqlCommand(
            "SELECT balance FROM accounts WHERE id = 1", conn1, t1).ExecuteScalarAsync())!;
        Console.WriteLine($"T1 re-reads balance = {balanceAfter}  <-- still the snapshot value, not 500");

        await t1.CommitAsync();

        // After T1 commits its snapshot is gone; any new transaction sees 500.
        await using var verify = new NpgsqlCommand(
            "SELECT balance FROM accounts WHERE id = 1", conn1);
        Console.WriteLine($"Post-commit, a fresh read sees balance = {await verify.ExecuteScalarAsync()}");
    }

    // -------------------------------------------------------------------------
    // Scenario 2: write skew. Same code, two isolation levels, two outcomes.
    //
    // Invariant: at least one doctor must remain on_call. Both T1 and T2 read
    // "count of on-call doctors" -- both see 2 -- each then takes a DIFFERENT
    // doctor off call. Under SI both commit (different rows = no write-write
    // conflict the engine can detect from the writes alone). Under SSI Postgres
    // tracks the read predicates with SIReadLocks and aborts one transaction
    // because the rw-antidependency cycle would violate serializability.
    // -------------------------------------------------------------------------
    private static async Task WriteSkewDemo(IsolationLevel level)
    {
        // Reset doctors to a known state so the scenario is repeatable.
        await using (var reset = new NpgsqlConnection(ConnString))
        {
            await reset.OpenAsync();
            await new NpgsqlCommand(
                "UPDATE doctors SET on_call = TRUE", reset).ExecuteNonQueryAsync();
        }

        await using var conn1 = new NpgsqlConnection(ConnString);
        await using var conn2 = new NpgsqlConnection(ConnString);
        await conn1.OpenAsync();
        await conn2.OpenAsync();

        await using var t1 = await conn1.BeginTransactionAsync(level);
        await using var t2 = await conn2.BeginTransactionAsync(level);

        // Both transactions read the predicate. Both see 2.
        var t1Count = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM doctors WHERE on_call = TRUE", conn1, t1).ExecuteScalarAsync())!;
        var t2Count = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM doctors WHERE on_call = TRUE", conn2, t2).ExecuteScalarAsync())!;
        Console.WriteLine($"  T1 sees {t1Count} on-call, T2 sees {t2Count} on-call.");

        // Each transaction reasons "there are 2, I can safely go off-call" and
        // updates a DIFFERENT row. No row-level write conflict.
        await new NpgsqlCommand(
            "UPDATE doctors SET on_call = FALSE WHERE id = 1", conn1, t1).ExecuteNonQueryAsync();
        await new NpgsqlCommand(
            "UPDATE doctors SET on_call = FALSE WHERE id = 2", conn2, t2).ExecuteNonQueryAsync();

        // The interesting moment is COMMIT, where SSI runs its check.
        try
        {
            await t1.CommitAsync();
            Console.WriteLine("  T1 committed.");
            await t2.CommitAsync();
            Console.WriteLine("  T2 committed.");
        }
        catch (PostgresException ex) when (ex.SqlState == "40001")
        {
            // 40001 = serialization_failure. Per docs/03-practice.md #7, this
            // is expected control flow, not an error -- the app should retry.
            Console.WriteLine($"  Serialization failure caught: {ex.MessageText}");
            Console.WriteLine("  (Postgres SSI detected the dangerous rw-antidependency.)");
        }

        // Verify the post-state from a fresh connection so visibility is
        // unambiguous (no leftover snapshot from the demo transactions).
        await using var verifyConn = new NpgsqlConnection(ConnString);
        await verifyConn.OpenAsync();
        var finalOnCall = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM doctors WHERE on_call = TRUE", verifyConn).ExecuteScalarAsync())!;

        Console.WriteLine($"  Final on-call count = {finalOnCall} " +
            (finalOnCall == 0
                ? "<-- INVARIANT VIOLATED (write skew got through)"
                : "<-- invariant preserved"));
    }
}
