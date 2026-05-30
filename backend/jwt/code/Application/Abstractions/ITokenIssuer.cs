using Domain.Tokens;

namespace Application.Abstractions;

// Port — Application owns the contract, Infrastructure owns the crypto.
// This is what lets the handler stay free of System.IdentityModel imports.
public interface ITokenIssuer
{
    AccessToken Issue(TokenSubject subject);
}
