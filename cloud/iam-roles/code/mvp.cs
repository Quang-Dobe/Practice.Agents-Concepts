// mvp.cs -- Self-contained simulation of AWS IAM roles and the STS AssumeRole flow.
// Runs entirely in memory; no AWS account, no NuGet packages, no network.
//
// What this file proves (see docs/02-deep-dive.md for the full theory):
//   1. A Role bundles TWO independent policy documents -- a trust policy (WHO may assume)
//      and a permissions policy (WHAT the assumed session may do).
//   2. AssumeRole hands back SHORT-LIVED session credentials only if the caller passes the
//      trust check; the credentials themselves carry no permissions -- the role does.
//   3. Policy evaluation is: explicit Deny wins over any Allow; unmatched requests are
//      implicitly denied. This is the same rule real IAM enforces.
//   4. Sessions expire. Even the "correct" call fails with ExpiredToken until re-assume.

namespace IamRolesMvp;

// ===== Records that model the IAM primitives =====

// A Principal is anything that can act -- a workload identity, another role's session,
// a federated JWT subject. We identify it by an ARN string, same as real AWS.
public sealed record Principal(string Arn);

// A single policy statement: Effect x Action-pattern x Resource-pattern. Trailing '*'
// wildcard is supported (e.g. "s3:*" or ".../invoices/*"), which is enough for the demo.
public sealed record Statement(string Effect, string Action, string Resource);

public sealed record PermissionsPolicy(List<Statement> Statements);

// TrustPolicy answers "who may call AssumeRole on this role?"
// RequiredExternalId defends against the confused-deputy pattern used by multi-tenant SaaS
// vendors -- a third party must present the shared secret out-of-band. See 03-practice.md #5.
public sealed record TrustPolicy(HashSet<string> TrustedPrincipals, string? RequiredExternalId = null);

public sealed record Role(string Arn, TrustPolicy Trust, PermissionsPolicy Permissions);

// Session credentials returned by STS. Real AWS also returns AccessKeyId/SecretAccessKey/
// SessionToken; here the RoleArn is all we need to look up permissions on subsequent calls.
public sealed record SessionCredentials(string RoleArn, string SessionName, DateTime ExpiresAt);

// ===== The mini STS service =====

public sealed class Sts(IReadOnlyDictionary<string, Role> catalog, TimeSpan sessionDuration)
{
    // The AssumeRole handshake, stripped to its two load-bearing checks.
    // A real STS also enforces SCPs, MaxSessionDuration, aws:SourceIp conditions, etc.
    public SessionCredentials AssumeRole(
        Principal caller,
        string roleArn,
        string sessionName,
        string? externalId = null)
    {
        var role = catalog[roleArn];

        // (a) Trust check -- the caller's ARN must appear in the role's trusted-principal set.
        if (!role.Trust.TrustedPrincipals.Contains(caller.Arn))
            throw new UnauthorizedAccessException(
                $"AccessDenied: {caller.Arn} is not trusted to assume {roleArn}");

        // (b) ExternalId check -- confused-deputy defense for cross-account trust.
        if (role.Trust.RequiredExternalId is not null && role.Trust.RequiredExternalId != externalId)
            throw new UnauthorizedAccessException(
                $"AccessDenied: ExternalId mismatch on {roleArn}");

        return new SessionCredentials(roleArn, sessionName, DateTime.UtcNow.Add(sessionDuration));
    }
}

// ===== A tiny S3-like resource that enforces the role's permissions policy on every call =====

public sealed class ObjectStore(Func<string, Role> roleLookup)
{
    private readonly Dictionary<string, string> _objects = new();

    public void Seed(string bucket, string key, string value) => _objects[$"{bucket}/{key}"] = value;

    public string GetObject(SessionCredentials creds, string bucket, string key)
    {
        Authorize(creds, "s3:GetObject", $"arn:aws:s3:::{bucket}/{key}");
        return _objects[$"{bucket}/{key}"];
    }

    public void PutObject(SessionCredentials creds, string bucket, string key, string value)
    {
        Authorize(creds, "s3:PutObject", $"arn:aws:s3:::{bucket}/{key}");
        _objects[$"{bucket}/{key}"] = value;
    }

    public void DeleteObject(SessionCredentials creds, string bucket, string key)
    {
        Authorize(creds, "s3:DeleteObject", $"arn:aws:s3:::{bucket}/{key}");
        _objects.Remove($"{bucket}/{key}");
    }

    // Policy evaluation, mirroring the order documented in 02-deep-dive.md:
    //   0) if the session has expired, fail with ExpiredToken -- do not evaluate further.
    //   1) any explicit Deny that matches -> deny (short-circuit).
    //   2) at least one Allow that matches -> allow.
    //   3) otherwise -> implicit deny.
    private void Authorize(SessionCredentials creds, string action, string resource)
    {
        if (DateTime.UtcNow >= creds.ExpiresAt)
            throw new UnauthorizedAccessException(
                $"ExpiredToken: session '{creds.SessionName}' expired at {creds.ExpiresAt:O}");

        var matches = roleLookup(creds.RoleArn).Permissions.Statements
            .Where(s => Matches(s.Action, action) && Matches(s.Resource, resource))
            .ToList();

        if (matches.Any(s => s.Effect == "Deny"))
            throw new UnauthorizedAccessException($"AccessDenied (explicit deny): {action} on {resource}");
        if (!matches.Any(s => s.Effect == "Allow"))
            throw new UnauthorizedAccessException($"AccessDenied (implicit deny): {action} on {resource}");
    }

    // Trailing '*' only -- covers "s3:*" and "arn:aws:s3:::orders-invoices/*". Real IAM
    // supports richer patterns and Condition blocks; that machinery is not the point here.
    private static bool Matches(string pattern, string value) =>
        pattern.EndsWith('*')
            ? value.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : pattern == value;
}

// ===== The demo runner =====

public static class Program
{
    public static void Main()
    {
        // Two principals: one the role trusts, one it does not.
        var appPrincipal      = new Principal("arn:aws:iam::111122223333:role/orders-api");
        var attackerPrincipal = new Principal("arn:aws:iam::999999999999:role/random-lambda");

        // The role: read/write invoices in one bucket, with an EXPLICIT deny on DeleteObject.
        // The explicit deny is there to show it wins even if a future Allow were added.
        var invoicesRole = new Role(
            Arn: "arn:aws:iam::111122223333:role/invoices-reader",
            Trust: new TrustPolicy(TrustedPrincipals: [appPrincipal.Arn]),
            Permissions: new PermissionsPolicy([
                new Statement("Allow", "s3:GetObject",    "arn:aws:s3:::orders-invoices/*"),
                new Statement("Allow", "s3:PutObject",    "arn:aws:s3:::orders-invoices/*"),
                new Statement("Deny",  "s3:DeleteObject", "arn:aws:s3:::orders-invoices/*"),
            ])
        );

        var catalog = new Dictionary<string, Role> { [invoicesRole.Arn] = invoicesRole };
        // 2-second sessions so scenario 4 completes in a couple of seconds. Real AWS defaults to 3600s.
        var sts = new Sts(catalog, sessionDuration: TimeSpan.FromSeconds(2));
        var s3  = new ObjectStore(arn => catalog[arn]);
        s3.Seed("orders-invoices", "2026-01.pdf", "PDF-BYTES");

        // --- [1] Happy path: trusted principal assumes the role and reads an object.
        Console.WriteLine("[1] orders-api assumes invoices-reader and reads an object");
        var session = sts.AssumeRole(appPrincipal, invoicesRole.Arn, sessionName: "orders-api-worker-42");
        Console.WriteLine($"    minted session for {session.RoleArn} (expires {session.ExpiresAt:HH:mm:ss.fff}Z)");
        Console.WriteLine($"    GetObject -> {s3.GetObject(session, "orders-invoices", "2026-01.pdf")}");

        // --- [2] Untrusted principal is stopped at the STS door -- no credentials are ever minted.
        Console.WriteLine("\n[2] random-lambda from another account attempts AssumeRole");
        Try("AssumeRole", () => sts.AssumeRole(attackerPrincipal, invoicesRole.Arn, "attacker-session"));

        // --- [3] The valid session tries three actions: allowed, implicit-deny, explicit-deny.
        Console.WriteLine("\n[3] valid session invokes allowed / implicit-deny / explicit-deny actions");
        Try("PutObject orders-invoices/receipt.pdf", () => s3.PutObject(session, "orders-invoices", "receipt.pdf", "OK"));
        Try("GetObject customer-pii/ssn.csv       ", () => s3.GetObject(session, "customer-pii", "ssn.csv"));
        Try("DeleteObject orders-invoices/2026-01 ", () => s3.DeleteObject(session, "orders-invoices", "2026-01.pdf"));

        // --- [4] After expiry, even the previously-successful call fails until we re-assume.
        Console.WriteLine("\n[4] wait past session TTL, observe ExpiredToken, then re-assume");
        Thread.Sleep(TimeSpan.FromMilliseconds(2200));
        Try("GetObject with expired session       ", () => s3.GetObject(session, "orders-invoices", "2026-01.pdf"));
        var fresh = sts.AssumeRole(appPrincipal, invoicesRole.Arn, sessionName: "orders-api-worker-43");
        Console.WriteLine($"    re-assumed; GetObject -> {s3.GetObject(fresh, "orders-invoices", "2026-01.pdf")}");
    }

    // Demo helper: run an action, print OK or the denial reason. Never do this in production --
    // swallowing an UnauthorizedAccessException in real code is how audit trails go dark.
    private static void Try(string label, Action a)
    {
        try { a(); Console.WriteLine($"    {label}: OK"); }
        catch (Exception ex) { Console.WriteLine($"    {label}: DENIED -> {ex.Message}"); }
    }
}
