using Application.Abstractions;
using Application.Mediator;
using Domain;

namespace Application.Store;

// A write command. Returns Unit (a nullable object here - the demo does not
// need a real Unit type) because the caller only cares that it completed.
public sealed record PutCommand(string Key, string Value) : IRequest<object?>;

public sealed class PutHandler(IWriteAheadLog wal, KeyValueStore store)
    : IRequestHandler<PutCommand, object?>
{
    public Task<object?> Handle(PutCommand cmd, CancellationToken ct)
    {
        // The WAL invariant, one line each:
        //   1. Log the intent, durably. If the process dies after this line
        //      but before line 2, recovery will still apply the change - the
        //      commit is safe the moment Append returns.
        wal.Append(new LogRecord(WalOp.Put, cmd.Key, cmd.Value));

        //   2. Only now update the in-memory state. This ordering is what
        //      makes "write-ahead" a truthful name.
        store.Set(cmd.Key, cmd.Value);

        return Task.FromResult<object?>(null);
    }
}
