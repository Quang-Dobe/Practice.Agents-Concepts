using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Application.Abstractions;
using Domain.Tokens;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Tokens;

// HS256 because the demo's issuer and verifier are the same process.
// Real federated systems (issuer != verifier) should use RS256/ES256 + JWKS — see
// ../docs/03-practice.md §2.
public sealed class HmacTokenIssuer(JwtOptions options) : ITokenIssuer
{
    public AccessToken Issue(TokenSubject subject)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(options.Lifetime);

        // Registered claims (RFC 7519): sub identifies the user, iss/aud bind the
        // token to one issuer and one consumer, iat/exp scope its lifetime, jti
        // gives it a unique ID for future deny-list revocation if needed.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.UserId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat,
                now.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new("role", subject.Role),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        var serialized = new JwtSecurityTokenHandler().WriteToken(jwt);
        return new AccessToken(serialized, expiresAt);
    }
}
