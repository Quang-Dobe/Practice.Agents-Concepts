# Infrastructure as Code — the whole lifecycle, with zero cloud account.
#
# This file proves four things from 02-deep-dive.md:
#   1. Desired state    — you describe the end result, never the steps.
#   2. Plan / apply     — the diff is computed before anything is touched.
#   3. State            — terraform.tfstate is the memory that makes deletion decidable.
#   4. Drift            — edit the output file by hand, then `plan` reports it.
#
# The only providers used are `local` and `random`, so the "cloud API" here is
# your own filesystem. Everything else about the engine is identical to AWS/Azure.

terraform {
  required_version = ">= 1.5"

  # Pinned on purpose: an unpinned provider bump silently changes computed
  # defaults and your next plan proposes changes you never wrote.
  required_providers {
    local  = { source = "hashicorp/local", version = "~> 2.5" }
    random = { source = "hashicorp/random", version = "~> 3.6" }
  }
}

# The one knob. In a real repo this comes from envs/prod/terraform.tfvars,
# not from a workspace selector.
variable "environment" {
  type    = string
  default = "staging"
}

# A provider-generated value. It lives in state, not in the config — which is
# exactly why the tool needs state: nothing in this file can recompute it.
resource "random_pet" "server_name" {
  length = 2

  # `keepers` marks these inputs as ForceNew. Change `environment` and the plan
  # shows `-/+ replace` instead of an in-place update. This is the same
  # mechanism that quietly replaces an RDS instance when you rename a tag.
  keepers = {
    environment = var.environment
  }
}

resource "local_file" "server_config" {
  filename = "${path.module}/out/${var.environment}.conf"

  # Referencing random_pet.server_name.id creates an implicit edge in the DAG.
  # Terraform sorts the graph itself — the file cannot be written before the
  # name exists, and you never wrote an ordering statement to say so.
  content = <<-EOT
    # managed by terraform - hand edits will be reverted on the next apply
    server_name = "${random_pet.server_name.id}"
    environment = "${var.environment}"
  EOT

  # Note what is NOT here: no "if file exists then skip". Idempotency is a
  # property of the diff engine, not of anything you write.
}

output "server_name" {
  value = random_pet.server_name.id
}

output "config_path" {
  value = local_file.server_config.filename
}
