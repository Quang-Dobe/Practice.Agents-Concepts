namespace Domain.Prices;

// One price update. Id is its position in an ordered log, which is what makes it
// usable as an SSE `id:` field: the client can hand it back later and say "after this one".
public sealed record PriceTick(long Id, string Symbol, decimal Price)
{
    public static PriceTick First(string symbol) => new(1, symbol, 100m);

    // Ids only ever go up by one, so "everything after N" is well defined.
    // The price walk is deterministic on purpose: the demo output stays readable.
    public PriceTick Next() => this with { Id = Id + 1, Price = Price + (Id % 3 == 0 ? -0.25m : 0.50m) };
}
