# Infrastructure as Code — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Infrastructure as Code is the practice of specifying cloud/infrastructure resources as **machine-readable declarative configuration** that a reconciliation engine converts into API calls. The engine computes a diff between three things — the **desired state** (your config), the **prior state** (a recorded snapshot of what the tool created), and the **actual state** (what the provider API currently reports) — and emits a plan of create/update/replace/delete operations that closes the gap. Applying the same config to the same infrastructure twice produces no operations. That is idempotency, and it is a property of the *diff engine*, not of your files.

The formal model most tools implement is a **directed acyclic graph (DAG)** of resource nodes, where edges are data dependencies inferred from expression references (e.g. `subnet_id = aws_subnet.a.id`). The engine topologically sorts the DAG and walks it with bounded parallelism — Terraform's default is 10 concurrent node operations, tunable with `-parallelism=N`.

### The core building blocks

- **Resource** — one addressable object in a provider's API, with a type, a local name, and a schema-validated set of arguments. Address form: `aws_s3_bucket.logs` (Terraform), `Microsoft.Storage/storageAccounts@2023-05-01` (Bicep/ARM).
- **Provider / plugin** — the adapter that maps schema to REST/gRPC calls. In Terraform a provider is a **separate OS process** speaking gRPC over HashiCorp's `go-plugin` framework; protocol version 6 is current and works with CLI 1.0+ (protocol 5 covers 0.12+). Process isolation means a crashing provider does not crash the CLI, and providers can be written in any gRPC-capable language.
- **State** — the persisted mapping from config address → real resource ID plus last-known attribute values. Terraform/Pulumi/OpenTofu keep it as an explicit artifact you own; CloudFormation and ARM/Bicep keep it **server-side** inside the stack/deployment record.
- **Backend / state store** — where state lives and how it is locked. S3, Azure Blob, GCS, HCP Terraform, Pulumi Cloud.
- **Module** — a parameterised folder of resources with typed inputs and outputs. The unit of reuse and the unit of naming convention enforcement.
- **Plan / change set / what-if** — the dry-run artifact. Terraform `plan` (savable as a binary plan file), CloudFormation **change sets**, ARM/Bicep **what-if**, Pulumi `preview`.
- **Expressions and functions** — the config language's compute layer: `for_each`, `count`, `dynamic` blocks, `for` comprehensions, string templates.
- **Provisioners / escape hatches** — `local-exec`, `remote-exec`, `null_resource`, CloudFormation custom resources, Bicep deployment scripts. All are last resorts; they break idempotency because the engine cannot diff their effects.

### How it relates to the broader landscape

IaC sits in the **provisioning** half of infrastructure automation. Its sibling half is **configuration management** (Ansible, Chef, Puppet, DSC), which mutates the inside of an already-existing machine. Its cousin is **continuous reconciliation** — GitOps controllers like Argo CD and Flux, and Crossplane, which run the diff loop *continuously inside the cluster* instead of once per CI job. Container image builds (Docker, Packer) are a third neighbour: they produce the artifact that IaC then places. In practice a mature platform uses all four, in that order: build image → provision infrastructure → configure OS/app → reconcile continuously.

## Where

### Where it runs / lives in the stack

IaC is a **control-plane** tool, not a data-plane one. It runs as a CLI process — on a laptop, in a CI runner, or in a managed run environment (HCP Terraform, Pulumi Deployments, Azure DevOps, GitHub Actions) — and talks to cloud management APIs (`management.azure.com`, `*.amazonaws.com`) over HTTPS. It never sits in your request path. Its dependencies are: credentials, network egress to the API endpoints, and write access to the state backend. Nothing IaC does at 3 a.m. affects a user request unless it changes a resource that request touches.

For the Crossplane/GitOps variant, the control loop moves *into* a Kubernetes cluster as a set of controllers, which changes the failure profile: reconciliation is continuous rather than triggered, so drift is corrected automatically instead of being reported.

### Where you typically encounter it

- **AWS platform teams** — Terraform or CDK for VPC/EKS/RDS; CloudFormation StackSets for multi-account landing zones under AWS Control Tower.
- **Azure platform teams** — Bicep + **Azure Verified Modules** (the single Microsoft-standard module set in the public Bicep registry) and **deployment stacks** for lifecycle-managed resource groups.
- **Kubernetes platforms** — Terraform provisions the cluster; Argo CD or Flux then reconciles workloads; Crossplane sometimes provisions cloud resources from inside the cluster.
- **SaaS per-tenant provisioning** — one module instantiated per customer, driven by a tenant registry.
- **Developer environment tooling** — ephemeral preview environments spun up per pull request and destroyed on merge.
- **Compliance-heavy estates** — where the audit story is "every change is a reviewed commit," and policy-as-code gates the plan.

### Ecosystem and tooling

- **Engines:** Terraform (BUSL 1.1 since Aug 2023; 1.5.7 and earlier remain MPL 2.0; latest stable in the 1.15.x line as of mid-2026), OpenTofu (Linux Foundation, MPL 2.0, 1.11.x line), Pulumi, AWS CDK, CloudFormation, Bicep/ARM, Crossplane.
- **State and orchestration:** S3 + native lockfile, Azure Blob with lease-based locking, HCP Terraform (Stacks went GA at HashiConf 2025), Spacelift, env0, Scalr, Atlantis, Terragrunt / Terramate for multi-root orchestration.
- **Policy and security scanning:** Open Policy Agent + Conftest (Rego, portable across Terraform/OpenTofu/Kubernetes), HashiCorp Sentinel (HCP Terraform/TFE only, does not run on OpenTofu), Checkov, Trivy (absorbed tfsec), CloudFormation Guard, CloudFormation Hooks.
- **Testing:** `terraform test` (HCL-native test framework, since 1.6), Terratest, Pulumi's unit-test mocks, `Pester` for Bicep.
- **Cost and drift:** Infracost for plan-time cost deltas, driftctl-style scanners, CloudFormation native drift detection plus **drift-aware change sets** (launched Nov 2025).
- **Discovery / brownfield import:** Terraform `import` blocks (config-driven import, since 1.5), CloudFormation **IaC generator** (limited to resource types supported by Cloud Control API in the region), `az bicep decompile`.

## When

### When the topic emerged and why

The chain of pressure runs: hand-built "pet" servers → shell scripts → configuration management (CFEngine 1993, Puppet 2005, Chef 2009) → cloud APIs making infrastructure itself programmable. AWS CloudFormation shipped in **Feb 2011** and established the declarative-template model with server-side state. Terraform arrived in **July 2014** with the differentiating bet: keep state **client-side** so the tool is not coupled to any one cloud's stack service, and model everything as a provider plugin. Azure's ARM templates (2014) were JSON and painful to author, which is why Microsoft shipped **Bicep** (2020) as a transpiler to ARM. Pulumi (2018) attacked the other axis: use a real programming language and get types, loops, and unit tests. AWS CDK (2019) applied the same idea on top of CloudFormation.

Two recent forks in the road matter. HashiCorp relicensed Terraform to BUSL 1.1 in August 2023, which produced **OpenTofu** within a month; IBM completed its acquisition of HashiCorp on **27 February 2025**. And HashiCorp **archived CDK for Terraform on 10 December 2025**, citing lack of product-market fit — a data point worth weighing before adopting a general-purpose-language wrapper over a declarative engine.

### When to use it in a project

Reach for it when:

- More than one person changes infrastructure, and you need review, blame, and rollback.
- The same topology is deployed more than once — per environment, per region, per tenant.
- Disaster recovery must be a **rebuild** with a bounded RTO, not archaeology.
- Compliance requires that infrastructure change be auditable and pre-approved.
- The resource count is beyond what one person can hold in their head (roughly: past a few dozen resources).

### When NOT to use it

Avoid it when:

- The infrastructure is a single managed platform with nothing to describe (Vercel, Heroku, Render).
- You are in an active incident. Fix by hand, then reconcile the config and record the drift.
- The resource is genuinely stateful and hand-tended — a production database you would never let a tool replace. Manage its *surroundings* in IaC and mark it `prevent_destroy` / `lifecycle` protected, or leave it out.
- Nobody on the team will own the state backend and the CI apply path. Half-adopted IaC — where the console is still authoritative — is worse than none, because the config lies.
- You need a change applied in seconds and the tool's plan cycle takes minutes on a large state.

## How

### How it works under the hood

Terraform's `plan` is the clearest example. Per resource instance, the CLI drives the provider through a fixed RPC sequence:

1. **Init** — download provider binaries matching the version constraints, write the dependency lock file (`.terraform.lock.hcl`, with per-platform hashes), configure the backend.
2. **Load and parse** — read `.tf` files, build the resource graph from expression references, resolve variables and locals.
3. **Lock state** — acquire an exclusive lock. On S3 this is now `use_lockfile = true`, which uses an S3 **conditional write** to create a `.tflock` object; a conflict surfaces as HTTP 412 `PreconditionFailed`. This replaced the DynamoDB table approach (native locking arrived experimentally in 1.10, was promoted in 1.11, and `dynamodb_table` is deprecated).
4. **`UpgradeResourceState`** — if the provider's schema version advanced, migrate the stored state shape forward.
5. **`ReadResource` (refresh)** — ask the provider for the current remote object. This is the drift detection step, and it is one API call per resource instance. It is why plans on a 3000-resource state take minutes.
6. **`ValidateResourceConfig`** — provider-side validation beyond what the schema can express.
7. **`PlanResourceChange`** — the provider predicts the effect of an apply, marking values it cannot know yet as *unknown*. This is called **twice**: once at plan time with unknowns, once during apply when upstream outputs are concrete.
8. **Present the diff** — `+ create`, `~ update in-place`, `-/+ replace` (a schema attribute marked `ForceNew`), `- destroy`. Save with `-out=plan.bin` so the apply executes exactly what was reviewed.
9. **`ApplyResourceChange`** — mutate the remote system, return the final state object. State is written back **incrementally** as nodes complete, so a mid-apply crash still records what succeeded.
10. **Release lock**, write the new state version (S3 versioning gives you the rollback path).

```
config ──┐
         ├─► DAG ─► plan (diff) ─► [policy gate] ─► apply ─► provider API
state ───┤            ▲
cloud ───┘            │  refresh (ReadResource per instance)
```

CloudFormation inverts the ownership: you `PUT` a template, the service builds the change set server-side, executes it with automatic rollback on failure, and keeps state in the stack record. ARM/Bicep is closest to CloudFormation — Bicep compiles to an ARM JSON template, and Azure Resource Manager does the diffing. Pulumi and CDK differ at a different layer: both **generate** the desired state by executing a program, then hand it to an engine (Pulumi's own, or CloudFormation for CDK).

### Key trade-offs

| Design choice | You gain | You give up |
|---|---|---|
| Client-side state (Terraform, Pulumi) | Cloud-agnostic, one plan spans AWS+Azure+Datadog | You must secure, back up, lock, and split state yourself; state can contain secrets |
| Server-side state (CloudFormation, ARM) | No state file to lose, native drift API, automatic rollback | Single-cloud only; hard quotas (500 resources/template, 51,200-byte inline template body, 2500 resources per nested-stack operation) |
| DSL (HCL, Bicep) | Diffable, statically analysable, one obvious way to write it | Awkward conditional/loop logic; no real unit tests |
| General-purpose language (Pulumi, CDK) | Types, IDE support, loops, real tests, package managers | Turing-complete configs are hard to review; arbitrary code between "commit" and "desired state" |
| Small many-state layout | Fast plans, tight blast radius, per-team IAM | Cross-state data passing via remote-state lookups or SSM/Key Vault; ordering becomes your problem |
| One big state | Simple, one plan sees everything | Slow refresh, wide blast radius, one lock serialises the whole org |
| Immutable replace | Predictable, matches golden-image workflows | Downtime windows and data-migration work per replace |
| In-place update | Cheaper, no data movement | Providers' update paths are less tested than create paths |

### Common failure modes

- **State/reality divergence after a console fix** — someone edits in the portal; next apply proposes to undo it, or errors because the resource shape changed underneath.
- **Lost or corrupted state** — the backend was not versioned. Recovery is `import` for every resource, by hand.
- **Concurrent apply without locking** — two CI runs write state; the loser's resources become orphans nobody manages.
- **Refresh-time cliff** — plans go from 10 seconds to 4+ minutes as the state grows past a few thousand resources, because refresh is one API call per instance.
- **Accidental replace from a `ForceNew` attribute** — a tag or name change on an RDS instance or storage account silently plans a destroy/create. Always read `-/+` lines.
- **`for_each` keyed by index** — reordering a list re-indexes and destroys/recreates unrelated resources. Key by a stable string.
- **Secrets in state** — passwords passed as resource arguments are stored in plaintext state unless the provider marks them write-only or you use ephemeral values (OpenTofu 1.11; Terraform's `terraform_data` `store` block is arriving in the 1.16 line).
- **Provider version drift** — an unpinned provider upgrade changes defaults and proposes changes you never wrote. Commit the lock file.
- **What-if / plan short-circuiting** — ARM what-if gives up analysing nested templates that use runtime functions, and since every Bicep module becomes a nested template, the preview can silently report `Ignore` instead of the real change.
- **Provider bug on a partially-created resource** — apply fails halfway, state records a tainted object, and the only clean path is manual deletion plus `state rm`.

## Why

### Why it exists

Three first-principles pressures. **Reproducibility**: a manually built environment cannot be recreated, so staging never matches production and every incident has an untestable hypothesis. **Auditability**: cloud APIs let anyone with a role change anything; text in Git plus a review gate is the cheapest way to make change intentional. **Scale of cardinality**: once you have hundreds of resources across accounts and regions, human memory stops being a viable storage medium for configuration. IaC replaces institutional memory with a data structure a machine can diff.

### Why it looks the way it does

The non-obvious design decision is **why a state file exists at all**. The obvious alternative — pure discovery, where the tool reads the cloud on every run and needs no memory — was tried and does not work. Two reasons. First, cloud APIs are not lossless mirrors of your config: a resource you created with three arguments comes back with forty server-computed fields, and without prior state the tool cannot tell "you never set this" from "someone changed this." Second, **deletion is undetectable without memory**: if you remove a resource block from your config, a stateless tool sees only a resource in the cloud it does not recognise, and cannot distinguish "delete this" from "not mine." State makes both decidable, at the cost of being a critical, lockable, secret-bearing artifact you now have to operate. CloudFormation and ARM make the same bet but hide the artifact inside the service, trading portability for one less thing to break.

The second non-obvious choice is **declarative DSL over general-purpose language**. A Turing-complete config can express anything, which is exactly the problem: reviewing a pull request means predicting what the program will emit. HCL and Bicep are deliberately weaker so the diff is the review. That constraint is why Pulumi and CDK remain minorities despite better ergonomics — and CDKTF's archival in December 2025 is the strongest evidence that a general-purpose wrapper over a declarative engine is a hard sell at scale.

### Why it matters now

As of 2026 IaC is not emerging technology; it is the assumed baseline for any cloud role above junior. What is actually moving is the layer above it. State and run orchestration are commoditising — S3 native locking removed the DynamoDB table, Pulumi Cloud now speaks Terraform's backend API — while the differentiation shifts to **governance**: policy-as-code gates, cost preview in the pull request, and module registries as internal platforms (Azure Verified Modules is Microsoft's bet here). Terraform's BUSL relicense and the IBM acquisition gave the ecosystem a genuine second option in OpenTofu, so "which engine" is a live question again rather than a settled one. And LLM-generated infrastructure raises the value of the plan step specifically: a machine-checked diff plus a policy gate is the control that makes generated config safe to apply.

## Open questions / things to verify in practice

- Measure the refresh cost yourself: time `plan` versus `plan -refresh=false` on a state with ~200 resources, then again at ~2000. Find where your own cliff is.
- Break state deliberately in a sandbox: delete a resource in the console, then run plan; delete the state object, then recover via `import` blocks. Time the recovery.
- Test S3 `use_lockfile = true` under real contention — trigger two CI applies at once and confirm the 412 surfaces as a readable error, not a silent overwrite.
- On Azure, run `what-if` against a Bicep file with several modules and check whether any resource comes back as `Ignore`. If so, your preview is lying and deployment stacks with `denySettings` matter more.
- Verify how your provider treats secrets: apply a resource with a password argument, then grep the state file. Confirm whether write-only/ephemeral attributes actually keep it out.
- Pick one non-trivial module and try it under both Terraform and OpenTofu with the same state, to see how real the compatibility claim is for your provider set.
- Deliberately trigger a `ForceNew` replace on a throwaway resource and confirm your CI plan output makes it impossible to miss.
