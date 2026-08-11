# Infrastructure as Code — Overview

> Infrastructure as Code means your servers, networks, databases, and permissions are defined in text files that live in Git and get applied by a tool — so the cloud console stops being the source of truth.

## The 30-second version
Instead of clicking through the AWS or Azure portal to create a VM, a load balancer, and a firewall rule, you write a file that says "these things should exist, configured this way." A tool reads that file, compares it to what actually exists in your cloud account, and makes reality match. That file goes through pull requests, code review, and CI like any other code. The payoff is that spinning up an identical staging environment stops being a two-day archaeology project and becomes one command.

## The mental model
Think of the difference between **a recipe and a food order**.

An imperative script is a recipe: "create the VPC, then the subnet, then attach the gateway, then launch the instance." It works perfectly the first time. Run it a second time and you get two VPCs, or an error, because a recipe assumes you're starting with an empty kitchen.

Declarative IaC is a food order: "I want a table set for six, with three plates of pasta." You hand that to the kitchen. The kitchen walks over, sees four plates already on the table, and adjusts — removes one, adds two chairs. Run the same order again and nothing happens, because the table already matches. That property is **idempotency**, and it's the whole reason IaC scales.

To do this, the tool needs a memory of what it put there — a **state file**. State is how Terraform knows the `web-01` instance in your config is the same instance as `i-0abc123` in AWS. When someone changes that instance by hand in the console, config and reality diverge; that gap is called **drift**, and the tool's `plan` step is what surfaces it.

The mental model in one line: **config = what you want, cloud = what exists, state = the mapping between them, plan = the diff.**

## What it is NOT
- **Not configuration management.** Ansible, Chef, and Puppet configure software *inside* machines. IaC provisions the machines themselves. They overlap but solve different halves.
- **Not a deployment pipeline.** CI/CD ships your application code. IaC ships the platform it runs on. They usually run in the same pipeline, in that order.
- **Not containers.** A Dockerfile describes one image. IaC describes the cluster, network, and IAM that image needs.
- **Not just "scripts in Git."** A bash script full of `az cli` calls is automation, not IaC — it has no state, no plan, no idempotency.

## The tool landscape (as of 2026)

| Tool | Language | Scope | Reach for it when |
|---|---|---|---|
| Terraform | HCL | Multi-cloud | Default choice; largest provider ecosystem |
| OpenTofu | HCL | Multi-cloud | You want Terraform without the BSL license (Linux Foundation fork) |
| Pulumi | C#, TS, Python, Go | Multi-cloud | Your team wants loops, types, and real tests |
| Bicep / ARM | Bicep DSL | Azure only | Pure Azure shop, want day-one feature parity |
| CloudFormation / CDK | YAML / TS, C# | AWS only | Pure AWS shop, want native drift detection |

## When you would reach for it
- You need staging to actually match production, not approximately match it.
- More than one person changes infrastructure, and you want review and an audit trail.
- You're building the same stack per customer, per region, or per environment.
- Disaster recovery must be a rebuild, not a restore-from-memory.

## When you would NOT reach for it
- A one-off experiment you'll delete this afternoon — the console is faster.
- A genuine production outage — fix it by hand, then reconcile the config afterward.
- Fully managed platforms (Vercel, Heroku) where there's barely any infrastructure to describe.

## Key vocabulary (just enough to keep reading)
- **Declarative** — you describe the end state, not the steps.
- **Idempotent** — running it twice produces the same result as running it once.
- **State** — the tool's record mapping your config to real cloud resources.
- **Plan / diff** — a dry run showing what would change before it changes.
- **Apply** — executing the plan.
- **Drift** — real infrastructure no longer matching the config, usually from manual edits.
- **Provider** — the plugin that translates config into AWS/Azure/GCP API calls.
- **Module** — a reusable, parameterized bundle of resources.
- **Immutable infrastructure** — replace resources instead of mutating them in place.

## What's next
The next document (`02-deep-dive.md`) answers What / Where / When / How / Why in detail — the plan/apply lifecycle, how state locking and remote backends work, module design, secret handling, and where IaC fits alongside GitOps and policy-as-code.
