using Application.Abstractions;
using Application.Mediator;
using Application.Tokens;
using Infrastructure.Tokens;
using Microsoft.Extensions.DependencyInjection;

namespace ConsoleHost;

// Composition root + demo runner. Three things happen here:
//   (1) Issue a signed token for a fake user.
//   (2) Verify it round-trips successfully.
//   (3) Flip one character in the payload and watch the signature check fail.
internal static class Program
{
    private static async Task Main()
    {
        var options = new JwtOptions
        {
            Issuer = "https://demo.local",
            Audience = "demo-api",
            // Read from env so a curious reader can override without editing code.
            // The 32-byte fallback is a *demo* default — never ship a hard-coded secret.
            Secret = Environment.GetEnvironmentVariable("JWT_SECRET")
                     ?? "demo-secret-please-rotate-me-now!!",
            Lifetime = TimeSpan.FromMinutes(15),
        };
        options.Validate();

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<ITokenIssuer, HmacTokenIssuer>();
        services.AddSingleton<ITokenVerifier, HmacTokenVerifier>();
        services.AddCustomMediator(typeof(IssueTokenCommand).Assembly);

        await using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        // --- (1) Issue ---
        var token = await mediator.Send(new IssueTokenCommand(UserId: "user-42", Role: "admin"));
        Console.WriteLine($"Issued JWT (expires {token.ExpiresAt:O}):");
        Console.WriteLine(token.Value);
        Console.WriteLine();

        // --- (2) Verify the genuine token ---
        var ok = await mediator.Send(new VerifyTokenQuery(token.Value));
        Console.WriteLine($"Verify (untouched) → valid={ok.IsValid}, sub={ok.Subject}, role={ok.Role}");

        // --- (3) Tamper: flip the last char of the payload segment ---
        // A signed JWT is three base64url segments separated by dots. We mutate the
        // payload, leave the signature alone, and watch the verifier reject it.
        var tampered = Tamper(token.Value);
        var bad = await mediator.Send(new VerifyTokenQuery(tampered));
        Console.WriteLine($"Verify (tampered) → valid={bad.IsValid}, reason={bad.FailureReason}");
    }

    private static string Tamper(string jwt)
    {
        var parts = jwt.Split('.');
        var payload = parts[1].ToCharArray();
        // Flip one character deterministically so the demo is reproducible.
        payload[^1] = payload[^1] == 'A' ? 'B' : 'A';
        parts[1] = new string(payload);
        return string.Join('.', parts);
    }
}
