namespace Application.Abstractions;

// The read side of CQRS. The projection is rebuilt from the same events
// the write side produces, NOT shared state. If we deleted this entirely
// and replayed from event position 0, the read model would be reconstructed
// bit-for-bit — that is the deep-dive §How step 6 invariant.
public interface IBalanceProjection
{
    // Snapshot of the balance for one account as the projection has seen
    // it so far. Returns null if the projection hasn't observed an
    // AccountOpened event yet for this account.
    decimal? GetBalance(Guid accountId);
}
