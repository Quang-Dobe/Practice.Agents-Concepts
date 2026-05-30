namespace Domain.Tokens;

// The identity the token is being minted for. Kept tiny on purpose:
// claim bloat is the long-term threat to a JWT system (see ../docs/03-practice.md).
public sealed record TokenSubject(string UserId, string Role);
