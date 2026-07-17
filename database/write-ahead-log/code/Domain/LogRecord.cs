namespace Domain;

// A single WAL record. In a real engine this would carry an LSN, a prev-LSN,
// a transaction id, a page id, and a CRC. For the demo we keep only the two
// bits of information every replay strictly needs: the operation kind and the
// key/value payload. Everything else is bookkeeping around this shape.
public enum WalOp : byte
{
    Put = 1,
    Delete = 2,
}

// A record is a `record` (naturally immutable, structural equality) so the
// replay loop can treat it as a value. The Value is nullable because a Delete
// record has no payload beyond its Key.
public sealed record LogRecord(WalOp Op, string Key, string? Value);
