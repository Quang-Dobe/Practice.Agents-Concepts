using Domain;

namespace Application.Abstractions;

// The port. The Application layer only knows: "I can append a record durably
// and I can replay every record that was ever appended." How that maps to a
// file, an fsync, or an S3 object is an Infrastructure concern.
public interface IWriteAheadLog
{
    // Serialize the record, write it to the log, and force it to durable
    // storage. Must not return until the OS has acknowledged the flush -
    // that is the whole invariant this class exists to enforce.
    void Append(LogRecord record);

    // Read every record from the log in append order and hand each to the
    // caller. Used exactly once, at startup, to rebuild the in-memory state.
    void Replay(Action<LogRecord> apply);
}
