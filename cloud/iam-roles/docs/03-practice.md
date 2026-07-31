# IAM Roles — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical multi-account AWS org, roles are the seam between *who initiated an action* and *what happened in the account*. Every commit that flows through CI, every developer console session, every Lambda pulling a KMS key, every replicator writing to a cross-account S3 bucket — all of it flows through `sts:AssumeRole` and back out as SigV4 calls signed with `ASIA…` credentials. If you look at any CloudTrail dashboard for more than five minutes, `AssumeRole` is the highest-volume event you will see after `Describe*`.

You will meet IAM roles in four load-bearing places. First, in **Terraform / CDK modules** where a workload role is defined next to the workload it belongs to. Second, in **GitHub Actions workflows** where `aws-actions/configure-aws-credentials` swaps an OIDC token for a role session. Third, in **incident response**, when someone asks "which role did this?" and you have to walk a CloudTrail trail back through a session name to a human. Fourth, in **security review**, when Access Analyzer flags a role whose trust policy contains `"Principal": "*"` and someone has to explain how it got there.

The failure mode is almost never "the role doesn't work." It is "the role works, plus twelve other things nobody intended." This doc is about keeping that set tight.

## Best practices

### 1. One role per workload, not one god-role per account
**Do:** Create a distinct role per service, per environment (`orders-api-prod`, `orders-api-staging`, `orders-worker-prod`). Attach only the actions and resource ARNs that workload actually calls.
**Why:** Blast radius. When an SSRF hits your `orders-api` in prod, the attacker gets S3 read on `orders-invoices/*` — not DynamoDB write on `users`, not Secrets Manager on the whole account.
**Avoid:** A shared `AppRole` reused across services because "it's easier." That role becomes the account's implicit admin within six months.

### 2. Start policies from evidence, not imagination
**Do:** Ship the workload with a temporarily broad policy behind a `PermissionsBoundary`, run it for 1–2 weeks, then use **[IAM Access Analyzer's policy generation](https://docs.aws.amazon.com/IAM/latest/UserGuide/access-analyzer-policy-generation.html)** to synthesize a least-privilege policy from CloudTrail. Iterate.
**Why:** Hand-written policies are almost always wrong in one of two ways — missing an action the SDK actually calls (`s3:ListBucket` in addition to `s3:GetObject`), or granting five actions the code has never called since day one.
**Avoid:** Copy-pasting an AWS managed policy like `AmazonS3FullAccess` and shipping it. Managed policies are starting points, not endpoints.

### 3. Prefer OIDC federation over long-lived access keys — always
**Do:** For GitHub Actions, GitLab, CircleCI, Buildkite, and EKS pods, register the provider's OIDC issuer in IAM and let workloads call `AssumeRoleWithWebIdentity`. On EKS, use **IRSA** or Pod Identity; on GitHub, use `aws-actions/configure-aws-credentials@v4` with `role-to-assume`.
**Why:** Long-lived `AKIA…` keys in `GITHUB_SECRETS` or CI env vars are the modal cause of public-cloud breaches in 2023–2025 (CircleCI 2023, several Terraform Cloud incidents). OIDC removes the secret entirely.
**Avoid:** IAM users provisioned "just for CI." If it exists, it will be reused, copied to a laptop, and end up in a screenshot.

### 4. Scope OIDC trust policies down to `sub` — never trust `aud` alone
**Do:** Pin the `sub` claim to a specific repo, branch, and (ideally) environment: `repo:acme/backend:environment:production`. Combine with `StringEquals` on `aud`.
**Why:** A trust policy that only checks `aud: sts.amazonaws.com` will accept a token from *any* GitHub workflow on the planet — [Datadog Security Labs found this in the wild](https://securitylabs.datadoghq.com/articles/aws-iam-oidc-github-actions/) at scale.
**Avoid:** `StringLike` on `sub` with a wildcard like `repo:acme/*:*`. If a wildcard is unavoidable, at minimum require `environment:production` so a fork's PR workflow cannot assume the prod role.

Bad:
```json
"Condition": { "StringEquals": {
  "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
}}
```
Good:
```json
"Condition": {
  "StringEquals": {
    "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
    "token.actions.githubusercontent.com:sub": "repo:acme/backend:environment:production"
  }
}
```

### 5. For cross-account access to a third party, require `sts:ExternalId`
**Do:** When a SaaS vendor (Datadog, Snyk, Wiz) needs to read your account, generate a per-tenant random ExternalId, put it in the trust policy's `Condition`, and hand it to the vendor out-of-band.
**Why:** This is the **[confused-deputy defense](https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html)**. Without it, an attacker who knows your role ARN and the vendor's account ID can trick the vendor's multi-tenant service into assuming your role on their behalf.
**Avoid:** Copy-pasting a trust policy from another customer of the same vendor; you will inherit their ExternalId and the guarantee collapses.

### 6. Use permissions boundaries to delegate role creation safely
**Do:** Let developers create IAM roles in dev accounts, but require every role they create to carry a **[permissions boundary](https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies_boundaries.html)** that caps the intersection (e.g. no `iam:*`, no `organizations:*`, no `kms:ScheduleKeyDeletion`). Enforce with a condition on `iam:CreateRole` requiring `iam:PermissionsBoundary`.
**Why:** Boundaries let you say "developers may self-serve roles, but no role they create can ever be more powerful than X." Without them, the choice is bottleneck (all roles via central platform) or foot-gun (privilege escalation via `iam:PassRole`).
**Avoid:** Using SCPs where a boundary belongs. SCPs cap the *account*; boundaries cap the *identity*. Reach for whichever matches the blast radius.

### 7. Enforce SCPs as the org-wide non-bypassable floor
**Do:** In AWS Organizations, apply Service Control Policies at the OU level to deny things nothing should ever do — `iam:DeleteRole` on `*prod*` roles, disabling CloudTrail, leaving the org, weakening S3 public-access block. Add **Resource Control Policies** (RCPs) for resource-side denies.
**Why:** SCPs are the only control that cannot be bypassed by an account admin. Roles, IAM users, and root all bounce off them equally.
**Avoid:** Trying to use SCPs to *grant* anything — they only cap. Also avoid SCP sprawl; ten crisp denies beat 50 fuzzy ones.

### 8. Prefer SSO / IAM Identity Center for humans; kill IAM users
**Do:** Route human console access through **IAM Identity Center** (formerly AWS SSO) with permission sets. Humans authenticate to your IdP (Okta / Entra), pick an account+role from the SSO portal, and drop into a time-boxed session. Same on the CLI with `aws sso login`.
**Why:** Zero long-lived credentials on laptops, centralized offboarding (disable the IdP user, all cloud access dies within the session TTL), and CloudTrail entries carry the human identity as `sourceIdentity`.
**Avoid:** Per-human IAM users with console passwords and access keys. Every one is an offboarding checklist item someone will forget.

### 9. Force meaningful session context — `RoleSessionName` and `sourceIdentity`
**Do:** When assuming a role, set `--role-session-name` to a human-identifiable value (email, ticket ID, workflow run URL). For federated flows, require `sts:SetSourceIdentity` in the trust policy so the original human identity is stamped on every downstream call.
**Why:** Without this, CloudTrail shows `botocore-session-1732000000` for every action, and forensics can't answer "who deleted the bucket?" in less than an hour.
**Avoid:** Trusting default session names. Enforce with `Condition: { StringLike: { "sts:RoleSessionName": "..." } }` in the trust policy.

### 10. Rotation is automatic — never persist STS credentials
**Do:** Let the SDK's default credential provider chain (IMDSv2 on EC2, IRSA on EKS, execution role on Lambda) refresh credentials in memory. Enforce `HttpTokens=required` on every instance to kill IMDSv1.
**Why:** Every "we stored the assumed-role credentials in Redis for perf" story ends with those credentials being valid inside an attacker's shell for 55 more minutes. IMDSv1-only enables SSRF-to-credential-exfiltration in one hop, as [Capital One 2019](https://www.capitalone.com/digital/facts2019/) demonstrated.
**Avoid:** Writing `access_key_id` / `session_token` to disk, env files, or process arguments. If you find yourself parsing STS output, you're doing it wrong.

### 11. Review continuously with Access Analyzer, not annually
**Do:** Enable **Access Analyzer** for both external access and unused access in every account. Route findings to Security Hub or a Slack channel. Treat "unused role for 90 days" as a policy violation.
**Why:** Roles accrete permissions the way logs accrete size — silently. A quarterly review is a compliance ritual; continuous analysis is a control.
**Avoid:** Running Access Analyzer once at project setup and never again.

### 12. Scope `iam:PassRole` — the quiet privilege-escalation vector
**Do:** Any policy that grants `iam:PassRole` must scope `Resource` to specific role ARNs and add a `Condition` on `iam:PassedToService` (e.g. `lambda.amazonaws.com`).
**Why:** `iam:PassRole *` combined with `lambda:CreateFunction` lets a developer create a Lambda with the admin role attached and invoke it. That's game over.
**Avoid:** Wildcarded `PassRole` in developer or CI policies. This is the single most common escalation path in AWS pentests.

## Anti-patterns to recognize

- **Wildcarded actions and resources**: `"Action": "*", "Resource": "*"` in a workload role, usually as leftover debug scaffolding; ships to prod because "we'll tighten it later." Fix by generating the policy from CloudTrail with Access Analyzer.
- **`Principal: "*"` in a trust policy**: any AWS account in the world can attempt the assume, and if a weak condition slips (`"aws:PrincipalArn": "*"`), they succeed; makes the role account-public. Restrict `Principal` to specific account IDs, roles, or federated providers.
- **Missing `sub` on OIDC trust**: only `aud` is checked, so any GitHub workflow can mint a session in your account; several 2023–2024 supply-chain incidents rode this in. Always pin `sub` to the repo and (ideally) environment.
- **Cross-account trust without `ExternalId`**: the classic confused-deputy setup; a multi-tenant vendor can be tricked into calling your role on another tenant's behalf. Always add and rotate an ExternalId with third parties.
- **Role that can modify its own policy**: `iam:PutRolePolicy` on `Resource: <self>` — any RCE on the workload becomes account admin in one step. Never let a workload role hold `iam:*` on itself.
- **Role chaining ignored in batch jobs**: a role assumed from an already-assumed session is capped at **1 hour** regardless of `MaxSessionDuration`; jobs die at exactly 60 minutes with `ExpiredToken`. Refresh credentials or restructure to avoid the second hop.
- **IMDSv1 left enabled on EC2**: SSRF in the app becomes credential theft with no token round-trip. Enforce `HttpTokens=required` at launch and audit with Config.
- **Console user with an access key "for emergencies"**: the emergency key never rotates, gets embedded in a script, then leaks. Use SSO break-glass with permission-set escalation and a paging alarm on assume.

## Real-world usage patterns

**SaaS multi-tenant data ingestion.** A B2B analytics product ingests customer data by assuming a role in each customer's AWS account. Each customer gets a distinct **ExternalId** stored per-tenant in the ingestion service. The trust policy pins `Principal` to the ingestion account's role ARN plus the ExternalId condition. *Non-obvious lesson:* rotate ExternalIds on tenant offboarding — the trust policy in the customer account is under *their* control, and if they don't remove it, an old ExternalId that leaks is still valid.

**CI/CD with per-environment OIDC roles.** GitHub Actions deploys to dev, staging, and prod. Three roles exist per service; the prod trust policy requires `sub` to match `repo:acme/backend:environment:production`, and the production environment in GitHub is gated by required reviewers. *Non-obvious lesson:* the GitHub environment name is what makes the sub-claim tight; without an environment gate, any PR from a maintainer to any branch can deploy to prod.

**Cross-account log aggregation.** A central security account holds a role that trusts every workload account's CloudTrail service to write into a locked S3 bucket. Workload accounts have no ability to delete objects in the aggregation bucket — enforced at the bucket policy layer, not just the role layer. *Non-obvious lesson:* aggregation only survives compromise of a workload account if the *destination* controls write semantics; don't rely on the source's role permissions alone.

**EKS with IRSA and ABAC.** Each Kubernetes namespace maps to an IAM role via a service-account annotation. Session tags carry the namespace name, and permission policies use `aws:ResourceTag/team == aws:PrincipalTag/team` to gate S3 prefix access. *Non-obvious lesson:* ABAC scales linearly in tag definitions and quadratically in tag *combinations*; three tags with three values each already gives you 27 effective permission sets — audit them, don't just trust the model.

## Operational checklist

- **Monitoring:** Are `AssumeRole` failures per role graphed and alerted? A sudden spike almost always means a trust-policy misconfiguration or a probing attacker.
- **Monitoring:** Is `UnauthorizedOperation` in CloudTrail alarming to on-call? Silent 403s mean roles have drifted from what workloads expect.
- **Failure handling:** What happens when STS in one region is degraded? Do SDKs fall back to another regional endpoint, or do you have `sts.amazonaws.com` (global) hardcoded?
- **Failure handling:** Is there a break-glass permission set in Identity Center, and does using it page the security channel automatically?
- **Security:** Does every OIDC role's trust policy pin `sub`, not just `aud`? Grep your Terraform for `token.actions.githubusercontent.com:aud` and inspect each match.
- **Security:** Does any role in the account grant `iam:PassRole` on `Resource: "*"`? If yes, treat as P1.
- **Security:** Is IMDSv2 enforced on 100% of EC2 launches (Launch Template + SCP fallback)?
- **Cost:** Is CloudTrail data-events on for the S3 buckets that matter? (These are billed; scoping matters.) Is Access Analyzer set to the right regions?
- **Onboarding:** Can a new engineer, on day one, run `aws sso login` and reach a scoped read-only role in prod without a ticket, and *no* other credentials on their laptop?
- **Review discipline:** Are Access Analyzer unused-access findings triaged weekly? Roles idle for 90 days should be flagged for deletion.

## How this topic typically evolves in a codebase

Teams start with a single AWS account, one hand-written IAM policy per service, and a couple of IAM users for CI. This works for a year. The first inflection point is the second account — usually "we need a separate prod." Suddenly cross-account access matters, someone learns about `sts:AssumeRole` the hard way, and the CI user's key gets copy-pasted into a second account "temporarily." That temporary key lives for two years.

The second inflection point is either a security audit or an incident. Both push the team toward Organizations, SCPs, and Identity Center. Roles multiply from tens to hundreds. Terraform modules for "standard workload role + boundary + trust policy" appear. OIDC replaces the CI user; the old access key is (eventually) rotated out. Access Analyzer starts producing a backlog nobody has time for.

The third inflection point is scale — hundreds of accounts, dozens of teams, thousands of roles. Now the pain is *governance*: who can create roles, who reviews them, how do you keep policies from drifting. Teams that survive this phase have moved to policy-as-code with automated review (cfn-nag, Checkov, or in-house), permissions boundaries applied at the CreateRole level via SCP, and continuous unused-access cleanup. The teams that don't survive it end up with `AdministratorAccess` roles nobody wants to touch and a Terraform state file everyone is afraid to run.

## Further reading

- [AWS IAM Security best practices](https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html) — the canonical checklist; short enough to read in one sitting and revisit yearly.
- [IAM Access Analyzer policy generation](https://docs.aws.amazon.com/IAM/latest/UserGuide/access-analyzer-policy-generation.html) — how to synthesize least-privilege policies from real CloudTrail activity instead of guessing.
- [Configuring OIDC in AWS for GitHub Actions](https://docs.github.com/actions/security-for-github-actions/security-hardening-your-deployments/configuring-openid-connect-in-amazon-web-services) — the reference for trust-policy shape; read the "Configuring the role and trust policy" section twice.
- [Datadog Security Labs — Compromising AWS IAM Roles via GitHub Actions OIDC](https://securitylabs.datadoghq.com/articles/aws-iam-oidc-github-actions/) — real-world write-up of what happens when `sub` is missing or wildcarded.
- [AWS Security Blog — Confused deputy problem](https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html) — the exact reason `ExternalId` exists, with an unambiguous example.
- [GCP Workload Identity Federation overview](https://cloud.google.com/iam/docs/workload-identity-federation) — the same pattern in GCP terms; useful to see what generalizes and what is AWS-specific.
