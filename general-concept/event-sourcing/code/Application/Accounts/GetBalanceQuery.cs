using Application.Abstractions;
using Application.Mediator;

namespace Application.Accounts;

// Read side. Note we do NOT load the aggregate to answer this — we read
// the projection. That's the CQRS pairing (deep-dive §What, §How step 6):
// the write side appends events; a subscription updates a read model;
// queries read the read model.
public sealed record GetBalanceQuery(Guid AccountId) : IRequest<decimal?>;

public sealed class GetBalanceHandler(IBalanceProjection projection)
    : IRequestHandler<GetBalanceQuery, decimal?>
{
    public Task<decimal?> Handle(GetBalanceQuery query, CancellationToken ct)
        => Task.FromResult(projection.GetBalance(query.AccountId));
}
