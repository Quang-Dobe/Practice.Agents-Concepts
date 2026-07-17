using Application.Abstractions;
using Application.Mediator;
using Domain;

namespace Application.Store;

public sealed record DeleteCommand(string Key) : IRequest<object?>;

public sealed class DeleteHandler(IWriteAheadLog wal, KeyValueStore store)
    : IRequestHandler<DeleteCommand, object?>
{
    public Task<object?> Handle(DeleteCommand cmd, CancellationToken ct)
    {
        // Same ordering as PutHandler: log first, then mutate. A Delete
        // record has no Value; Replay will treat it as a Remove.
        wal.Append(new LogRecord(WalOp.Delete, cmd.Key, null));
        store.Remove(cmd.Key);
        return Task.FromResult<object?>(null);
    }
}
