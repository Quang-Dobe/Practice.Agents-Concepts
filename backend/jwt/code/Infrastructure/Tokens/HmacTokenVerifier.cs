using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Application.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Tokens;

// The four checks every JWT verifier must do, in order:
//   1. Signature (re-compute with our key, compare).
//   2. exp (with a small leeway for clock skew).
//   3. iss exact-match.
//   4. aud must contain our audience.
// Skipping any one of these is a known CVE class — see ../docs/02-deep-dive.md §How.
public sealed class HmacTokenVerifier(JwtOptions options) : ITokenVerifier
{
    private readonly TokenValidationParameters _parameters = new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret)),

        ValidateIssuer = true,
        ValidIssuer = options.Issuer,

        ValidateAudience = true,
        ValidAudience = options.Audience,

        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),

        // Pin the algorithm locally — never trust the header's "alg" field.
        // This single line blocks both "alg: none" and the classic RS256→HS256
        // confusion attack (see ../docs/02-deep-dive.md §Common failure modes).
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
    };

    public VerificationResult Verify(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(token, _parameters, out _);
            return new VerificationResult(
                IsValid: true,
                Subject: principal.FindFirstValue(JwtRegisteredClaimNames.Sub),
                Role: principal.FindFirstValue("role"),
                FailureReason: null);
        }
        catch (SecurityTokenException ex)
        {
            // Catching the library's own base exception is intentional: it covers
            // expired, bad-signature, wrong-issuer, wrong-audience, and wrong-alg
            // in one branch so the demo can show *which* one fired.
            return new VerificationResult(false, null, null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
