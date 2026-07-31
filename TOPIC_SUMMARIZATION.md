# IAM Roles

An IAM role is a named bundle of cloud permissions that a workload or a user can *temporarily borrow* instead of *permanently owning*. It has two halves: a **permissions policy** (what the role can do) and a **trust policy** (who is allowed to assume it). When something assumes the role, the cloud provider's Security Token Service (STS) hands out short-lived credentials — usually valid for about an hour — that carry those permissions. When the hour expires, the credentials die on their own.

An engineer reaches for an IAM role any time code, a machine, or a cross-account pipeline needs to call cloud APIs without stapling a long-lived access key into a config file. That covers an EC2 instance reading from S3, a Lambda writing to DynamoDB, a GitHub Actions workflow deploying via OIDC federation, a central logging account pulling logs from ten workload accounts, and a developer taking one hour of admin rights via SSO with a full audit trail. It is not for authenticating your product's end users — Cognito, Auth0, or your own auth system covers that — and it is not for per-request business-logic authorization inside your app. GCP calls the same primitive a **service account**; Azure calls it a **managed identity**.

Picture a movie-studio lot. Employees carry permanent ID badges — those are IAM users. To enter the vault where the master reels are stored, nobody adds vault access to their personal badge. Instead, they walk to the front desk and check out a **temporary vault badge**. The badge lists which doors it opens (permissions policy), a posted sign at the desk names who is allowed to check it out (trust policy), and it expires at end of shift (session duration). That temporary badge is an IAM role. The front desk is STS. The act of checking it out is `AssumeRole`. A stolen badge is a stolen hour, not a stolen forever — and that is the whole point.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/cloud/iam-roles/
