# IAM Roles — Overview

> An IAM role is a named bundle of permissions that any trusted workload or user can temporarily "put on" to act in the cloud, without owning a password or long-lived key.

## The 30-second version
An IAM role is an identity you can *borrow*, not one you *are*. It has permissions attached to it (what it can do) and a trust policy (who is allowed to assume it). When something assumes a role, the cloud provider hands out **short-lived credentials** — typically valid for an hour — that carry those permissions. This is how AWS, GCP, and Azure let code, machines, and cross-account pipelines act on cloud APIs without anyone stapling a static access key into a config file. If you understand this one primitive, you understand 80% of cloud security posture.

## The mental model
Think of a movie studio lot. Employees have personal ID badges (that is an **IAM user**). But when a director needs to enter the vault where the master reels are stored, they don't get vault access permanently added to their ID. Instead, they walk to the front desk and check out a **temporary vault-access badge**. The badge:

- has a list of doors it opens (permissions policy),
- has a posted sign at the desk saying who is allowed to check it out (trust policy),
- expires at the end of the shift (session duration),
- is not tied to any single person — a producer could check out the same badge tomorrow.

That temporary badge is an IAM role. The front desk is AWS **STS** (Security Token Service). The act of walking up and requesting the badge is called **assume-role**. When the badge expires, you either request a fresh one or you're locked out. That expiry is the whole point — a stolen credential is a stolen hour, not a stolen forever.

## What it is NOT
- **Not an IAM user.** A user is a long-lived identity with its own credentials, usually meant for a human or a legacy script.
- **Not an access key.** Access keys are static secrets tied to a user. Roles hand out rotating short-lived credentials via STS.
- **Not a group.** Groups bundle users for permission management. Roles are assumable identities of their own.
- **Not an OS-level role or RBAC role.** Kubernetes RBAC and OS accounts are separate systems; IAM roles govern cloud provider APIs.

## When you would reach for it
- An **EC2 instance, ECS task, or Lambda function** needs to read from S3 or write to DynamoDB — attach a role; the SDK auto-discovers the credentials.
- A **CI/CD pipeline in GitHub Actions** needs to deploy to your AWS account — configure OIDC federation so the pipeline assumes a role, and never store an access key as a secret.
- **Cross-account access:** a central logging account needs to pull logs from ten workload accounts — each workload account exposes a role the logging account is trusted to assume.
- A **developer** needs temporary admin rights for a break-glass task — assume a role via SSO and get an audit trail of who did what, when.

## When you would NOT reach for it
- You need an identity for a **human end-user of your product** — that is a job for Cognito, Auth0, or your own auth system, not IAM.
- You need **fine-grained per-request authorization inside your app** — IAM controls the AWS API surface, not your business logic.
- Your workload runs **entirely outside any cloud** and never calls a cloud API — a role buys you nothing.

## Key vocabulary (just enough to keep reading)
- **Principal** — the thing doing the action (user, role, service, federated identity).
- **Trust policy** — who is allowed to assume this role.
- **Permissions policy** — what this role can do once assumed.
- **STS (Security Token Service)** — the API that hands out temporary credentials.
- **AssumeRole** — the STS call that trades your current identity for role credentials.
- **Session** — one issuance of temporary credentials; has an expiry (default 1 hour, max 12).
- **Instance profile** — the wrapper that attaches a role to an EC2 instance.
- **Service-linked role** — a role the cloud provider itself creates and manages for one of its services.
- **GCP equivalent** — service accounts with impersonation and workload identity federation.
- **Azure equivalent** — managed identities (system-assigned or user-assigned).

## What's next
The next document (`02-deep-dive.md`) answers What / Where / When / How / Why in detail — trust policy syntax, the assume-role handshake step by step, the STS credential lifecycle, cross-account and federated patterns, and how instance profiles and managed identities differ under the hood.

It clicks the moment you stop thinking of cloud identity as "who holds the key" and start thinking of it as "which workload is allowed to briefly become which role."
