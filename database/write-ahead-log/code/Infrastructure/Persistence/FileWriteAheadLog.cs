using Application.Abstractions;
using Domain;

namespace Infrastructure.Persistence;

// The infrastructure adapter for IWriteAheadLog. One file, append-only,
// fsync'd on every Append. That is the entire pattern - everything else
// (segment rotation, LSNs, CRCs, group commit) is an optimisation on top
// of these ten lines.
public sealed class FileWriteAheadLog : IWriteAheadLog, IDisposable
{
    private readonly string _path;
    private readonly FileStream _stream;

    public FileWriteAheadLog(string path)
    {
        _path = path;

        // Open in Append mode so every write goes to end-of-file, and share
        // Read so a curious reader can `cat wal.log` while the demo runs.
        _stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
    }

    public void Append(LogRecord record)
    {
        // Trivial line-per-record encoding: OP<TAB>KEY<TAB>VALUE\n. Keys and
        // values are assumed tab-free for the demo. A real WAL would use a
        // length-prefixed binary format with a CRC per record so torn writes
        // are detectable at replay time.
        var line = record.Op switch
        {
            WalOp.Put    => $"PUT\t{record.Key}\t{record.Value}\n",
            WalOp.Delete => $"DEL\t{record.Key}\t\n",
            _ => throw new InvalidOperationException($"Unknown op {record.Op}")
        };

        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        _stream.Write(bytes, 0, bytes.Length);

        // Flush the .NET buffer AND ask the OS to push to physical media.
        // The second argument (`flushToDisk: true`) is the fsync equivalent.
        // Without it, a kernel crash between now and the OS's own writeback
        // would lose the "committed" record - the WAL invariant would break.
        _stream.Flush(flushToDisk: true);
    }

    public void Replay(Action<LogRecord> apply)
    {
        // If the file does not exist yet, there is nothing to replay - this
        // is a fresh store. Silently return.
        if (!File.Exists(_path)) return;

        // Read from a fresh handle so we do not disturb the append cursor.
        // File.ReadLines streams line-by-line, so the memory cost is O(1)
        // in the log size even if the log is huge.
        foreach (var line in File.ReadLines(_path))
        {
            if (line.Length == 0) continue;

            // A partial trailing line means a crash mid-write. A production
            // WAL detects this via CRC; here we just skip it, which is safe
            // because the client was never acknowledged for that record.
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            LogRecord record = parts[0] switch
            {
                "PUT" => new LogRecord(WalOp.Put, parts[1], parts[2]),
                "DEL" => new LogRecord(WalOp.Delete, parts[1], null),
                _ => throw new InvalidOperationException($"Unknown op tag {parts[0]}")
            };

            apply(record);
        }
    }

    public void Dispose() => _stream.Dispose();
}
