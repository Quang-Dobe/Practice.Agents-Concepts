namespace Infrastructure.Tokens;

// One spot for issuer/audience/secret/lifetime — registered as a singleton from Program.cs.
// The secret must be >= 32 bytes for HS256; the constructor enforces that loudly so a
// dev who shortens it doesn't get a silent runtime crash later.
public sealed class JwtOptions
{
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string Secret { get; init; }
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (System.Text.Encoding.UTF8.GetByteCount(Secret) < 32)
            throw new InvalidOperationException(
                "JWT_SECRET must be at least 32 bytes (256 bits) for HS256.");
    }
}
