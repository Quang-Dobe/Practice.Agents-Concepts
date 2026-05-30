namespace Application.Abstractions;

// Verification returns a structured result instead of throwing for the "invalid" path,
// because in the demo we want to *show* the failure, not have the process die.
public sealed record VerificationResult(
    bool IsValid,
    string? Subject,
    string? Role,
    string? FailureReason);

public interface ITokenVerifier
{
    VerificationResult Verify(string token);
}
