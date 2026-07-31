# IAM Roles — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition
An IAM role is a **named IAM identity that has no long-lived credentials of its own**. Instead, it defines two policies — one describing *who may assume it* (the trust policy, also called the assume-role policy document) and one describing *what may be done once assumed* (the permissions policy). When a principal successfully assumes the role, AWS Security Token Service (STS) mints a set of short-lived credentials (access key ID, secret access key, session token) that carry the role's permissions until they expire. Every subsequent API call signed with those credentials appears in CloudTrail under the role's ARN, decorated with a session name.

Roles are the concrete implementation of the general cloud pattern "identity by trust relationship, credentials by exchange" — the same shape you will see as GCP **service accounts + Workload Identity Federation** and Azure **managed identities**.

### The core building blocks
- **Role ARN** — stable identifier of the form `arn:aws:iam::<account-id>:role/<role-name>`. Everything else references this.
- **Trust policy** — a JSON document with an `sts:AssumeRole*` action, a `Principal`, and optional `Condition` blocks. This is a *resource-based* policy attached to the role; it decides who can even attempt to assume the role.
- **Permissions policies** — one or more identity-based policies attached to the role (managed or inline). These decide what the assumed session can do.
- **Permissions boundary** (optional) — a managed policy attached to the role that caps the role's effective permissions to the *intersection* of boundary and permissions policy.
- **Session** — the result of a successful `AssumeRole`. Has credentials, a duration (900s to `MaxSessionDuration`, default 3600s), a role session name, and optionally an inline *session policy* that further narrows the session.
- **Instance profile** — the container object required to attach a role to an EC2 instance. Same name as the role in practice, but a distinct IAM resource type.
- **Service-linked role (SLR)** — a role a specific AWS service creates and controls on your behalf (e.g. `AWSServiceRoleForAutoScaling`). You cannot rewrite its trust policy.

The formal reference is the [IAM User Guide, policies chapter](https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies.html) and the [STS API reference](https://docs.aws.amazon.com/STS/latest/APIReference/welcome.html).

### How it relates to the broader landscape
Roles belong to the **cloud identity and access management** family: primitives whose job is to translate "who is calling" into "what may be called." The sibling in GCP is a **service account** — with the twist that GCP service accounts *can* still have keys, and modern practice is to disable that and use **Workload Identity Federation** or **impersonation** to hand out short-lived tokens instead. Azure's equivalent is **managed identity** (system-assigned or user-assigned), which is essentially a role permanently bolted to an Azure resource so no assume-role call is needed from inside the VM. All three converge on the same principle: **no static secrets in the workload**.

## Where

### Where it runs / lives in the stack
Roles are metadata inside the cloud provider's IAM control plane; they don't run anywhere. Two runtime touchpoints matter:

1. **STS endpoint** (`sts.amazonaws.com`, regional endpoints `sts.<region>.amazonaws.com`) — the API you call to mint session credentials.
2. **The credential provider chain** inside AWS SDKs, which knows how to locate credentials in environment variables, shared config files, IMDSv2 (for EC2), the ECS task metadata endpoint, EKS Pod Identity, or Lambda's execution environment. This is what lets application code call `s3.getObject(...)` without ever seeing the role ARN.

### Where you typically encounter it
- **EC2 instance profiles** — the SDK reads credentials from IMDSv2 at `169.254.169.254`. Two-step: `PUT /latest/api/token` for a session token, then `GET /latest/meta-data/iam/security-credentials/<role-name>` with that token as a header. Details in the [IMDSv2 docs](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/configuring-instance-metadata-service.html).
- **Lambda execution role** — credentials are injected as `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` env vars, refreshed by the runtime.
- **ECS task role** — SDK fetches credentials from `169.254.170.2` (the ECS agent's credential provider endpoint) using a per-task URI in `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI`.
- **Cross-account access** — account A's role trusts account B; a principal in B calls `sts:AssumeRole` and gets credentials that act *inside* account A.
- **OIDC federation for CI/CD** — GitHub Actions, GitLab, CircleCI, Buildkite each publish an OIDC provider; you register the provider in IAM and let workflows assume roles with `AssumeRoleWithWebIdentity`.
- **SAML federation for humans** — enterprise IdPs like Okta or Entra ID sign a SAML assertion; the browser POSTs it to STS via `AssumeRoleWithSAML` and the user drops into an AWS console session.

### Ecosystem and tooling
- **For assuming roles from a laptop:** `aws sts assume-role`, `aws sso login`, `granted` (`assume` CLI), Leapp, `saml2aws`.
- **For CI:** `aws-actions/configure-aws-credentials` (GitHub), `hashicorp/vault-action`, HashiCorp Vault's AWS secrets engine.
- **For defining roles as code:** Terraform (`aws_iam_role`), AWS CDK (`iam.Role`), Pulumi, CloudFormation.
- **For guardrails:** AWS Organizations SCPs, Resource Control Policies (RCPs), Access Analyzer, IAM Access Advisor.
- **For auditing:** CloudTrail (every `AssumeRole` and every API call under the session is logged), Athena queries over CloudTrail, CloudTrail Lake.

## When

### When the topic emerged and why
IAM shipped with AWS in 2010; roles landed in 2011, initially for cross-account delegation. **Roles for EC2** (instance profiles) followed in mid-2012 and were the killer feature — before then, teams routinely baked long-lived access keys into AMIs or user-data scripts, which leaked into GitHub with predictable frequency. STS opened up in 2011 for cross-account, then expanded with `AssumeRoleWithSAML` (federation for humans, 2012) and `AssumeRoleWithWebIdentity` (OIDC federation for machines, later extended to cover GitHub Actions and generic OIDC providers). Every step was a response to a specific pattern of leaked static credentials.

### When to use it in a project
Reach for a role when:
- A workload runs on AWS compute (EC2/ECS/EKS/Lambda) and needs to call AWS APIs. Always. There is no legitimate reason to give a Lambda a static access key.
- One AWS account needs to call APIs in another AWS account.
- A CI/CD system outside AWS needs to deploy into AWS — use OIDC federation and stop storing keys in GitHub Secrets.
- Humans need scoped, time-bounded elevated access (break-glass, `AdministratorAccess` via IAM Identity Center).
- You need per-caller auditability that survives credential rotation.

### When NOT to use it
Avoid a role (or add layers around it) when:
- You need to authenticate **end users of your product** — that is Cognito, Auth0, or your own identity system. Roles are for AWS API access, not application login.
- You need per-request authorization inside your app's business logic — IAM only gates AWS API calls.
- The workload never talks to AWS APIs. Attaching an empty role to it is theatre.
- You are tempted to create a role per user for humans. Prefer IAM Identity Center (SSO) with a small set of permission sets; role-per-user does not scale and defeats auditability.

## How

### How it works under the hood
The assume-role lifecycle is a request/response protocol against STS, followed by SigV4-signed API calls. For a typical `sts:AssumeRole`:

1. **Caller authenticates to STS** using its *current* credentials (an IAM user's long-lived key, another role's session, or a federated token via `AssumeRoleWithWebIdentity` / `AssumeRoleWithSAML`).
2. **STS resolves the target role's trust policy**, evaluating the `Principal`, `Action`, and every `Condition` block. If nothing matches, or an explicit `Deny` matches, the call fails with `AccessDenied`.
3. **STS applies any SCPs and RCPs** on the target account. SCPs never grant permissions; they cap them. An SCP denying `sts:AssumeRole` will block the mint even if the trust policy allows it.
4. **STS mints credentials.** The response contains `AccessKeyId` (starts with `ASIA…`, versus `AKIA…` for long-lived user keys), `SecretAccessKey`, `SessionToken`, and `Expiration`. Default duration 3600 seconds, capped by the role's `MaxSessionDuration` (settable 1h–12h). See [`AssumeRole` API reference](https://docs.aws.amazon.com/STS/latest/APIReference/API_AssumeRole.html).
5. **Client signs subsequent API calls** with SigV4, including the `X-Amz-Security-Token` header. Every service validates the session on each request; no server-side session state to invalidate on expiry — the credentials just stop verifying.
6. **CloudTrail records both events**: the `AssumeRole` on the caller's account, and the downstream API calls on the target account, correlated via the `sessionContext` and `sourceIdentity`.

**Role chaining** — assuming a role from a session that was itself obtained by assuming a role — is capped at **1 hour**, regardless of `MaxSessionDuration`. This is documented and non-negotiable ([re:Post note](https://repost.aws/knowledge-center/iam-role-chaining-limit)).

**OIDC federation** (the GitHub Actions pattern):
1. GitHub's OIDC provider signs a short-lived JWT whose `iss` is `https://token.actions.githubusercontent.com`, `aud` defaults to `sts.amazonaws.com`, and `sub` encodes repo/branch/environment (e.g. `repo:acme/backend:ref:refs/heads/main`).
2. The workflow calls `sts:AssumeRoleWithWebIdentity`, passing the JWT.
3. STS validates the JWT signature against the OIDC provider's JWKS, checks the audience, then evaluates the role's trust policy — which typically pins `token.actions.githubusercontent.com:sub` to specific repos/branches.
4. On success, session credentials come back and the rest is identical to `AssumeRole`.

Missing the `sub` condition is the canonical mistake — without it, *any* GitHub workflow anywhere can assume the role. See [Wiz's write-up](https://www.wiz.io/blog/avoiding-mistakes-with-aws-oidc-integration-conditions) for the 2023 vulnerability wave this caused.

**A minimal trust policy for a GitHub OIDC role:**

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::111122223333:oidc-provider/token.actions.githubusercontent.com" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {
        "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
      },
      "StringLike": {
        "token.actions.githubusercontent.com:sub": "repo:acme/backend:ref:refs/heads/main"
      }
    }
  }]
}
```

**A minimal permissions policy** attached to that same role:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["s3:GetObject", "s3:PutObject"],
    "Resource": "arn:aws:s3:::acme-artifacts/*"
  }]
}
```

**Policy evaluation order** for any request under a role session ([official docs](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_evaluation-logic.html)):

1. Start from *implicit deny*.
2. Evaluate every applicable policy: SCPs, RCPs, resource-based policies, identity-based policies, permissions boundaries, session policies.
3. **Any explicit `Deny` in any policy short-circuits the request.** This is the one absolute rule.
4. Otherwise, the request is allowed only if *every* applicable policy scope grants it. The effective permission set is the intersection of the identity policy, the permissions boundary (if set), and the SCPs on the account.

### Key trade-offs
| Design choice | Gained | Given up |
|---|---|---|
| Short-lived credentials (default 1h) | Blast radius of leak is minutes to hours, not forever | Clients must refresh; long-running batch jobs need careful design or `MaxSessionDuration` bumps |
| Trust policy as a separate document | Delegation and federation are first-class, auditable | Extra concept to grok; misconfigurations are silent until exploited |
| Explicit-deny-wins evaluation | Security teams can enforce non-bypassable guardrails via SCPs | Debugging "why is this denied" requires walking every policy layer |
| Permissions boundaries per identity | Safe delegation of role creation to developers | Must be applied per identity; SCPs do org-wide caps more scalably |
| Instance profile abstraction on EC2 | Zero credential handling in application code | Instance profile ≠ role at the API layer; extra confusion when scripting |

### Common failure modes
- **Wildcard trust policy** — `"Principal": { "AWS": "*" }` without conditions lets any AWS account assume the role. Publicly known incident pattern.
- **Missing `sub` condition on OIDC roles** — any GitHub workflow can mint credentials in your account.
- **`iam:PassRole` too broad** — a principal with `iam:PassRole *` on a Lambda create action can hand any role to Lambda and effectively escalate.
- **Role chaining silently truncates to 1 hour** — long CI jobs fail two hours in with `ExpiredToken`.
- **IMDSv1 left enabled** — server-side request forgery (SSRF) can read instance credentials without a token; enforce IMDSv2 with `HttpTokens=required`.
- **Session name not enforced** — CloudTrail shows sessions but with useless names like `botocore-session-1700000000`; use `sts:RoleSessionName` conditions to force meaningful names.
- **Confused-deputy across accounts** — external vendor role trusted without `sts:ExternalId` condition. AWS documents this specifically.

## Why

### Why it exists
The one problem IAM roles solve is **static-credential sprawl**. Any long-lived secret embedded in an artifact — an AMI, a container image, a `.env` file, a GitHub Secret — eventually leaks. Historically the industry response was rotation policy and secret-scanning; neither actually removes the failure mode. Roles remove it structurally: the workload never holds a secret capable of authenticating tomorrow.

Everything else (auditability via CloudTrail, cross-account delegation, federation with external IdPs, blast-radius containment) falls out of the same idea: authenticate the *workload's context* (which VM, which pod, which pipeline run) and hand back a token scoped to *that* context for a short window.

### Why it looks the way it does
The obvious alternative was **API keys per workload with tight rotation**, which is what many pre-cloud systems used. That fails for three reasons: rotation without downtime is hard, workloads can't safely bootstrap their first key without another key, and revocation on suspicion is expensive. Roles sidestep all three because the "key" is a token minted on demand from a proof of identity the platform already has (an EC2 instance's signed identity document, a Lambda's execution context, an OIDC JWT from GitHub).

The split into *trust policy* and *permissions policy* mirrors the same separation you see in Kerberos and OAuth 2.0 — authenticate first, authorize second — and lets a single role safely serve multiple principals (GitHub Actions across many repos, users across many teams) without duplicating permission definitions.

### Why it matters now
As of 2026, three trends make roles more central, not less:

1. **Multi-account is the default.** AWS Organizations, Control Tower, and landing-zone patterns push teams toward tens or hundreds of accounts. Cross-account role assumption is how anything reaches across them.
2. **CI/CD is the new attack surface.** Post-SolarWinds and post-CircleCI-breach, storing long-lived cloud keys in a build system is being treated as a policy violation. OIDC federation to IAM roles is the accepted replacement.
3. **GCP and Azure have converged on the same pattern.** Workload Identity Federation on GCP and managed identities on Azure both use exchange-a-proof-for-a-token semantics. Learning IAM roles teaches the general shape.

## Open questions / things to verify in practice
- What does the trust policy for the role I'm about to create actually pin — repo, branch, environment? Test with a workflow from an unrelated branch and confirm it's rejected.
- What is the role's `MaxSessionDuration`, and does any of my batch tooling silently role-chain into a 1-hour ceiling?
- Is IMDSv2 enforced on every EC2 instance profile I use (`HttpTokens=required`)? What happens if I try IMDSv1?
- What does a CloudTrail entry look like for a cross-account assume-role, end to end — can I trace a downstream S3 read back to the original human or workflow?
- If I attach a permissions boundary, does my role still work? Do I understand the intersection semantics, or am I guessing?
- How does GCP Workload Identity Federation exchange a token for a service account impersonation, and where does that flow differ from `AssumeRoleWithWebIdentity`?
