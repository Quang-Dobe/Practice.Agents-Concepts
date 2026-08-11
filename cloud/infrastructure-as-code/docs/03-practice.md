# Infrastructure as Code — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In a typical SaaS backend, IaC is the layer that owns everything your application assumes already exists: the VPC/VNet, the managed Postgres, the container platform, the queue, the secret store, the DNS record, and the IAM roles that let all of it talk. Your app deploy pipeline ships a container image; the IaC pipeline shipped the cluster that image lands on. The two pipelines are usually separate repos with separate approval rules, and the seam between them — "who creates the DB and who runs the migration" — is where most of the arguments happen.

The second place you meet it is the **landing zone**: multi-account AWS Organizations or Azure management groups, with baseline guardrails (logging, CloudTrail/Activity Log, tag policy, SCPs, network hub) applied to every account by the same code. Here IaC is not a productivity tool, it is the compliance story. "Prove nobody can create a public S3 bucket" is answered by a policy-as-code check on the plan plus a service control policy, both in Git.

Third: **environment multiplication**. The moment someone asks for "a staging that actually matches prod" or "a preview environment per pull request" or "one isolated stack per enterprise customer," you are in module-and-parameterisation territory, and the quality of your module boundaries decides whether that request takes a day or a quarter.

You will also meet it in its worst form: a half-adopted repo where 60% of prod is in Terraform, 40% was clicked in the console, and nobody knows which is which. That is the most common real-world state of IaC, and most of the practices below exist to prevent it.

## Best practices

### 1. Remote, versioned, locked state before your second commit
**Do:** S3 with `use_lockfile = true`, bucket versioning and SSE-KMS on; or Azure Blob with lease locking and soft delete. Separate the state account/subscription from the workload it describes.
**Why:** Local state means one laptop is your source of truth, and the first concurrent apply silently orphans resources. Versioning is your only rollback when a bad `state mv` corrupts the file.
**Avoid:** `terraform.tfstate` committed to Git — it leaks secrets and merge-conflicts unrecoverably.

### 2. Split state by blast radius and change frequency
**Do:** Layer it — network/identity (changes monthly), data stores (quarterly), app platform (weekly), app-adjacent resources (daily) — each with its own state and its own CI job. Cross-layer values pass through remote-state reads or SSM/Key Vault.
**Why:** One state means one lock: a 6-minute plan on the network layer blocks the team that just wanted to add a queue. It also means anyone with apply rights can destroy the database.
**Avoid:** The single root module holding 2,000 resources "so everything is in one plan."

### 3. Directories per environment, not workspaces
**Do:** `envs/prod/`, `envs/staging/`, each a thin root module calling shared versioned modules with its own backend config and its own `.tfvars`.
**Why:** Workspaces share one config and one backend key prefix, so the only thing separating prod from dev is a CLI flag someone will forget. Directories let prod pin an older module version and get a stricter CI job.
**Avoid:** `terraform workspace select prod && apply` as your production deployment procedure.

### 4. Pin provider and module versions, commit the lock file
**Do:** `~> 5.40` style constraints on providers, exact tags (`ref=v2.3.1`) or registry version pins on modules, and `.terraform.lock.hcl` in Git with hashes for every platform your CI and laptops use.
**Why:** An unpinned provider minor bump changes computed defaults, and your next plan proposes 40 changes you didn't write — usually discovered at 5pm on a Friday.
**Avoid:** `source = "git::...//modules/vpc"` with no ref, then wondering why staging and prod diverged.

### 5. Make the saved plan the review artifact
**Do:** CI runs `plan -out=plan.bin`, posts the human-readable diff on the PR, gates on approval, then applies **that exact file**. Never re-plan at apply time.
**Why:** Between plan and apply, someone else's merge or a console change can alter reality; re-planning means you applied something nobody reviewed.
**Avoid:** `apply -auto-approve` on merge to main with a fresh plan.

### 6. Keep secrets out of the IaC data path
**Do:** Have IaC create the *container* (Key Vault, Secrets Manager entry, IAM binding) and let the app read the value at runtime. Where a password argument is unavoidable, use ephemeral/write-only attributes or a provider-generated random value the app never sees in config.
**Why:** Any secret passed as a resource argument lands in plaintext in state and often in plan output posted to a PR comment. State is then a credential store with Git-level access control.
**Avoid:** `db_password = var.db_password` fed from a CI secret — that value is now permanently in state history.

### 7. OIDC-federated, short-lived, scoped credentials for CI
**Do:** GitHub Actions / Azure DevOps federated identity assuming a per-environment role. The prod role is assumable only from the protected branch, with a permission boundary that excludes IAM-write unless that layer needs it.
**Why:** Long-lived access keys in CI variables are the single most common cloud breach vector, and a leaked one has your full deploy blast radius.
**Avoid:** One `AWS_ACCESS_KEY_ID` with AdministratorAccess shared by every pipeline.

### 8. Gate plans with policy-as-code and cost preview
**Do:** Run OPA/Conftest or Checkov against the JSON plan, plus Infracost for the delta. Hard-fail on public ingress, unencrypted storage, missing owner tag; soft-warn on cost above a threshold.
**Why:** Human reviewers approve `~ 180 changes` diffs without reading them. A machine check on the plan is the only reviewer that reads every line — and it is the control that makes LLM-generated config safe to apply.
**Avoid:** Scanning only the source files; the plan is where computed values and module expansion become visible.

### 9. Protect stateful resources from the tool itself
**Do:** `lifecycle { prevent_destroy = true }` on databases and stateful stores, provider-level deletion protection on, Azure deployment stacks with `denySettings`, CloudFormation stack policies. Add `ignore_changes` for fields a platform mutates.
**Why:** A tag rename on an RDS instance can plan as `-/+ replace`. `prevent_destroy` turns that from a data-loss incident into a failed plan.
**Avoid:** Trusting reviewers to always spot `-/+` in a long diff.

### 10. Schedule drift detection and give drift an owner
**Do:** Nightly `plan -detailed-exitcode` per state, exit code 2 opens a ticket routed to the owning team. Treat "console fix during an incident" as a required follow-up reconcile task in the incident template.
**Why:** Undetected drift means your DR rebuild produces something that has never worked. Drift found six months late is archaeology; found overnight it is a two-line PR.
**Avoid:** Discovering drift only when an unrelated change proposes to undo someone's emergency fix.

### 11. Own the import path deliberately
**Do:** Use config-driven `import` blocks (reviewable in a PR) for brownfield adoption, and keep a documented runbook for "resource exists in cloud, not in state."
**Why:** Every real adoption is brownfield. Without an import practice, teams write parallel resources instead and end up with two VPCs.
**Avoid:** `state rm` as a routine debugging move — it silently converts managed resources into unmanaged ones nobody bills or patches.

### 12. Keep modules thin, opinionated and few
**Do:** Modules should encode a decision ("our standard private-subnet Postgres"), not wrap a single resource. Version them, publish to a private registry, and document inputs.
**Why:** A module per resource adds a release cycle to every provider feature, and your team ends up patching passthrough variables instead of building.
**Avoid:** The 90-input mega-module that configures anything and is understood by one person.

## Anti-patterns to recognize

- **The god state**: one root module for the whole estate. Plan times cross the refresh cliff (one API call per resource) and a single lock serialises the org; split by layer and environment instead. One documented case: a 180k-line monorepo with 68-minute plans, refactored into isolated states with sub-2-minute plans.
- **Workspaces as environments**: `prod` and `dev` differ only by workspace selector. Identical config means you cannot roll out risky changes to staging first, and a mis-selected workspace is a prod incident; use separate directories and backends.
- **Console-first, IaC-later**: emergency fixes are made in the portal and never reconciled. The config becomes fiction, so `plan` output stops being trusted and people stop reading it; make reconcile a mandatory incident follow-up.
- **`-target` as normal workflow**: using `-target` to skip the slow bits. It applies a partial graph, so dependent resources hold stale values and the next full plan is a surprise; fix the state split instead.
- **Copy-pasted environments**: staging was copied from prod once and has drifted for two years. Every "works in staging" claim becomes worthless; force sharing through versioned modules so differences are explicit variables.
- **Terragrunt/wrapper sprawl before it's needed**: layers of generated backend blocks and `include`s for a three-environment estate. New engineers cannot answer "what does this actually deploy"; adopt orchestration when you have >10 root modules, not on day one.
- **Plan output in a public PR**: convenient, until a plan prints a connection string or a private IP plan for an unreleased product. Redact, or keep plan output in the CI job log with restricted access.
- **Provisioners as glue**: `local-exec` running an `aws cli` command to finish a resource. The engine cannot diff it, so it is neither idempotent nor visible in the plan; use a real provider resource or move the step into the app boot path.

## Real-world usage patterns

**Multi-account SaaS landing zone (AWS, ~40 accounts).** A platform team owns an `account-baseline` module: logging, GuardDuty, tag policy, network hub attachment, break-glass role. New accounts are vended by adding a row to a registry and running one pipeline. Product teams get their own state files inside their own accounts and cannot touch the baseline. *Non-obvious lesson:* the baseline module's version bump is the riskiest change in the company — roll it out account-by-account with a canary account first, never `for_each` over all 40 in a single apply.

**Azure enterprise, Bicep + deployment stacks.** A regulated financial platform uses Azure Verified Modules with deployment stacks per resource group and `denySettings` so even a subscription Owner cannot delete stack-managed resources from the portal. *Non-obvious lesson:* `what-if` returns `Ignore` for nested templates that use runtime functions — and every Bicep module compiles to a nested template — so the preview can quietly under-report. The team's real safety net is `denySettings`, not the preview.

**Per-PR ephemeral environments (B2B product, ~30 engineers).** Opening a PR provisions a namespace, a database schema, and a DNS name; merging destroys them. *Non-obvious lesson:* the destroy path is the part that breaks, not the create path — orphaned load balancers and unreleased public IPs became the biggest line item on the bill. A nightly reaper that deletes anything tagged `ephemeral` older than 48 hours is mandatory, because failed destroys are normal.

**Per-tenant provisioning (vertical SaaS, ~600 tenants).** One module instantiated per customer, driven by a tenant table. Started as `for_each` over all tenants in one state; a single tenant's provider error blocked onboarding for everyone. *Non-obvious lesson:* past a few dozen instances, move to one state per tenant (or per shard of tenants) — the operational win is that failures are isolated, not that plans get faster.

## Operational checklist

- **State**: Is the backend versioned, encrypted, locked, and in a different blast-radius boundary than the resources it manages? Has restore-from-previous-version been tested?
- **Concurrency**: Have you triggered two applies at once and confirmed the second fails loudly (HTTP 412 / lease conflict) rather than overwriting?
- **Recovery**: Is there a written runbook for "state object deleted" and has someone timed the import-based recovery?
- **Monitoring**: Do you alert on drift (nightly `-detailed-exitcode`), apply failures, apply duration trend, and plan-time cost delta? Who is paged?
- **Blast radius**: Can a single apply destroy your production database? If yes, why is there no `prevent_destroy` plus provider deletion protection?
- **Security**: Are CI credentials OIDC-federated and scoped per environment? Grep your state for secrets — is anything in there you would rotate if leaked?
- **Cost**: Are `for_each` counts bounded, is Infracost gating large deltas, and does a nightly reaper clean up ephemeral resources whose destroy failed?
- **Review integrity**: Does apply consume the saved plan file, and is `-auto-approve` on a fresh plan impossible in the prod pipeline?
- **Onboarding**: Can a new engineer answer, on day one, which directory owns which environment, where state lives, and how to make a change without console access?

## How this topic typically evolves in a codebase

Teams start with one root module and local state, because it works and the whole estate is 30 resources. First forced migration: remote state with locking, triggered by the second engineer or the first orphaned resource. Second: splitting environments, usually triggered by an incident where a dev change hit prod. Third and most painful: **breaking the god state apart**, which means `state mv` surgery or import-based re-adoption, and it happens when plan times cross roughly 3–5 minutes and people start reaching for `-target`. Plan the split before you need it — the cost of moving 300 resources across state boundaries scales with how long you waited.

The later evolution is organisational rather than technical. Copy-pasted directories become versioned modules; versioned modules become an internal registry; the registry becomes a platform team with a release process and a deprecation policy. At that point the interesting problems are governance — policy-as-code coverage, cost attribution by tag, and who is allowed to bump the baseline module — not HCL syntax.

Two live debates worth knowing. **Monorepo vs polyrepo**: monorepo wins for 2–15 engineers (atomic cross-layer changes, one CI config), polyrepo wins past ~50 (ownership, independent velocity), and the tie-breaker is usually whether you have a platform team to maintain shared CI. **DSL vs general-purpose language**: HCL/Bicep advocates argue the diff *is* the review; Pulumi/CDK advocates argue types and tests catch more. The tie-breaker is who reviews changes — if a non-author must approve, the weaker language wins, and HashiCorp archiving CDK for Terraform in December 2025 is evidence in that direction.

## Further reading

- [Terraform: Up & Running](https://www.terraformupandrunning.com/) (Brikman) — the only book that treats state layout, module versioning, and team workflow as first-class problems rather than syntax appendices.
- [Google Cloud: Best practices for using Terraform](https://cloud.google.com/docs/terraform/best-practices-for-terraform) — the most opinionated vendor-neutral-in-practice guide on repo layout, module boundaries, and root-module sizing.
- [HashiCorp: Terraform mono-repo vs multi-repo](https://www.hashicorp.com/en/blog/terraform-mono-repo-vs-multi-repo-the-great-debate) — lays out both sides honestly, including the monolithic-configuration anti-pattern.
- [Azure Verified Modules](https://azure.github.io/Azure-Verified-Modules/) — worth reading even on AWS as a reference for what a governed internal module registry looks like (contracts, versioning, telemetry, deprecation).
- [Open Policy Agent + Conftest for Terraform plans](https://www.openpolicyagent.org/docs/terraform) — the portable way to gate plans; read it before buying a commercial policy engine.
- [AWS Prescriptive Guidance: Terraform on AWS](https://docs.aws.amazon.com/prescriptive-guidance/latest/terraform-aws-provider-best-practices/introduction.html) — concrete multi-account state, OIDC, and provider-configuration patterns.
