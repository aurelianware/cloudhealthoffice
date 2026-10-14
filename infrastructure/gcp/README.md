# Cloud Health Office on Google Cloud: demo infrastructure

This Terraform module creates a demo environment for Cloud Health Office on Google Cloud:

- a GKE Autopilot cluster (`cho-demo`, regional)
- an Artifact Registry Docker repository (`cho`) for the service images
- Workload Identity Federation so GitHub Actions can deploy without service account keys, restricted to one repository
- an optional monthly budget alert

> **Demo-grade, not production.** This environment has no Google BAA, customer-managed encryption keys, private cluster or Private Service Connect. Use synthetic data only. The production gaps are listed in [docs/deploy/GCP-DEMO.md](../../docs/deploy/GCP-DEMO.md).

The Azure deployment (`infrastructure/azure/`, `.github/workflows/deploy-azure-aks.yml`) is unaffected.

## Prerequisites

- A Google Cloud project with billing enabled. A dedicated project for the demo makes teardown clean.
- `gcloud` authenticated as a user who can enable APIs and grant IAM roles in that project (Owner, or Editor plus Project IAM Admin).
- Terraform 1.6 or newer.

```bash
gcloud auth application-default login
```

## Create the environment

```bash
cd infrastructure/gcp
terraform init
terraform plan  -var="project_id=YOUR_PROJECT_ID"
terraform apply -var="project_id=YOUR_PROJECT_ID"
```

Optional variables: `region` (default `us-west1`), `cluster_name`, `artifact_repository_id`, `github_repo`, and `billing_account_id` with `monthly_budget_usd` to add a budget alert. The cluster takes about 10 minutes to create.

State is kept locally by default. Keep the `terraform.tfstate` file; you need it to tear the environment down.

## Configure GitHub

Add these to the repository settings (Settings → Secrets and variables → Actions). The deploy workflow, `.github/workflows/deploy-gcp-gke.yml`, reads them.

| Kind | Name | Value |
|---|---|---|
| Secret | `GCP_WORKLOAD_IDENTITY_PROVIDER` | `terraform output -raw workload_identity_provider` |
| Secret | `GCP_DEPLOYER_SERVICE_ACCOUNT` | `terraform output -raw deployer_service_account` |
| Variable | `GCP_PROJECT_ID` | `terraform output -raw project_id` |
| Variable | `GKE_CLUSTER` | `terraform output -raw cluster_name` |
| Variable | `GKE_LOCATION` | `terraform output -raw cluster_location` |
| Variable | `GAR_REPOSITORY` | `terraform output -raw artifact_registry_url` |

Neither secret is a credential. They identify the federation provider and service account; GitHub's short-lived OIDC token is what authenticates.

Application secrets for the demo (for example `ANTHROPIC_API_KEY` for the AI claims examiner) are listed in the deploy workflow and the runbook.

## What the deployer service account can do

| Role | Scope | Why |
|---|---|---|
| `roles/artifactregistry.writer` | The `cho` repository | Push service images |
| `roles/container.admin` | Project | Deploy to the cluster, including the Strimzi Kafka operator's cluster-scoped resources |
| `roles/iam.workloadIdentityUser` | The service account, for this GitHub repository only | Keyless sign-in from GitHub Actions |

`roles/container.admin` is broader than a production deployer should hold. It is acceptable here because the project exists only for the demo.

## Tear down

```bash
terraform destroy -var="project_id=YOUR_PROJECT_ID"
```

This deletes the cluster, the image repository and the federation setup, and stops all charges they incur. Deleting the whole project (`gcloud projects delete YOUR_PROJECT_ID`) also works if the project was created only for the demo.
