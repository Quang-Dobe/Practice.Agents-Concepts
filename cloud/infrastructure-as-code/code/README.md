# Infrastructure as Code — MVP Code

The smallest runnable demo of declarative IaC. About 25 lines of HCL, comments excluded. No cloud account, no credentials — the `local` and `random` providers make your filesystem the "cloud API".

## What it demonstrates

- **Desired state + DAG**: `local_file` references `random_pet.id`, so the engine derives the ordering itself (`02-deep-dive.md` → "What").
- **Plan / apply lifecycle**: the diff is reviewed before anything is written; a second apply produces zero operations (idempotency).
- **State as memory**: `terraform.tfstate` holds the generated pet name — nothing in `mvp.tf` can recompute it, and deleting a resource block only becomes decidable because of it.
- **Drift**: hand-edit the generated file and `plan` reports the object changed outside Terraform.

## Prerequisites

- Terraform 1.5+ (`winget install HashiCorp.Terraform`) or OpenTofu 1.6+ (`winget install OpenTofu.OpenTofu`, then swap `terraform` for `tofu` in every command).
- Internet access on the first `init` only, to download the two provider plugins.

## Run it

```powershell
cd cloud/infrastructure-as-code/code
terraform init                       # download providers, write .terraform.lock.hcl
terraform plan                       # the diff: 2 to add
terraform apply -auto-approve        # writes out/staging.conf + terraform.tfstate
terraform apply -auto-approve        # idempotency: "No changes."

Add-Content out\staging.conf "rogue = true"   # simulate a console fix
terraform plan -detailed-exitcode    # drift: exit code 2, the CI drift-alarm signal
terraform apply -auto-approve        # reconcile back to desired state
terraform destroy -auto-approve      # the file is deleted, state is emptied
```

## Expected output

```
random_pet.server_name: Creating...
local_file.server_config: Creating...
Apply complete! Resources: 2 added, 0 changed, 0 destroyed.
server_name = "smashing-mudfish"

# second apply
No changes. Your infrastructure matches the configuration.

# after the hand edit
Note: Objects have changed outside of Terraform
  # local_file.server_config has been deleted
Plan: 1 to add, 0 to change, 0 to destroy.
```

## What to try next

- Run `terraform apply -var environment=prod` and read the `-/+ replace` line — `keepers` is a `ForceNew` attribute.
- Open `terraform.tfstate` and find the file content in plaintext; that is why real state never goes in Git.
- Delete `out/staging.conf` entirely, then `plan` — the same drift path handles "someone deleted it in the console".
- Delete `terraform.tfstate` and `plan` — the engine now wants to create resources that already exist, which is the lost-state failure mode.
