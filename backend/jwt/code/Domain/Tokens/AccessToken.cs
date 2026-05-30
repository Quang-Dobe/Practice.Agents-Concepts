namespace Domain.Tokens;

// Value object: the compact-serialized JWT string plus the moment it stops being valid.
// Lives in Domain because "what is a token" is a domain concept; *how* it's signed is not.
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);
