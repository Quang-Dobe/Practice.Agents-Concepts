using Application.Abstractions;
using Application.Mediator;
using Domain;

namespace Application.Store;

// The recovery path. Runs once at startup, before any Put/Delete is accepted.
// Returns the number of records replayed so the console can print it.
public sealed record RecoverCommand : IRequest<int>;

public sealed class RecoverHandler(IWriteAheadLog wal, KeyValueStore store)
    : IRequestHandler<RecoverCommand, int>
{
    public Task<int> Handle(RecoverCommand cmd, CancellationToken ct)
    {
        var replayed = 0;

        // ARIES-style REDO in miniature: replay every durable record in
        // append order, in the same way it was originally applied. Because
        // we set values (not increment them), replay is idempotent - running
        // it twice yields the same state.
        wal.Replay(record =>
        {
            switch (record.Op)
            {
                case WalOp.Put:
                    store.Set(record.Key, record.Value!);
                    break;
                case WalOp.Delete:
                    store.Remove(record.Key);
                    break;
            }
            replayed++;
        });

        return Task.FromResult(replayed);
    }
}
