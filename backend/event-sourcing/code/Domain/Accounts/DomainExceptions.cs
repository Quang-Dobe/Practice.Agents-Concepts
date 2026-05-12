namespace Domain.Accounts;

// Thrown when an aggregate invariant is violated — for example, withdrawing
// more than the current (folded) balance. This is *not* the same thing as a
// concurrency conflict; that lives in the Application layer because the
// "conflict" is a property of the store contract, not the domain.
public sealed class DomainException(string message) : Exception(message);
