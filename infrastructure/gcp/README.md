# Cloud Health Office on Google Cloud: demo infrastructure

This Terraform module creates a demo environment for Cloud Health Office on Google Cloud:

- a GKE cluster (`cho-demo`): by default a zonal Standard cluster with one e2-standard-8 node on standard persistent disk, which fits a new billing account's default quotas; set `autopilot = true` for a regional Autopilot cluster once you have about 24 vCPU and 500 GB of SSD quota
- an Artifact Registry Docker repository (`cho`) for the service images
- Workload Identity Federation so GitHub Actions can deploy without service account keys, restricted to one repository
- an optional monthly budget alert

> **Demo-grade, not production.** This environment has no Google BAA, customer-managed encryption keys, private cluster or Private Service Connect. Use synthetic data only. The production gaps are listed in [docs/deploy/GCP-DEMO.md](../../docs/deploy/GCP-DEMO.md).
>
> `hosted = true` builds the base of the hosted HIPAA environment instead (customer-managed keys, a private regional cluster, organization policies, six-year audit logs), in a separate project under a Google Cloud BAA. See [docs/deploy/GCP-HOSTED-HIPAA.md](../../docs/deploy/GCP-HOSTED-HIPAA.md). Never set it in the demo project.

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

Optional variables: `autopilot` (default `false`), `zone` (default `us-west1-a`), `node_machine_type` (default `e2-standard-8`), `node_count`, `node_disk_size_gb`, `region` (default `us-west1`), `cluster_name`, `artifact_repository_id`, `github_repo`, and `billing_account_id` with `monthly_budget_usd` to add a budget alert. The cluster takes about 10 minutes to create.

State is kept locally by default. Keep the `terraform.tfstate` file; you need it to tear the environment down.

## Configure GitHub

Add these to the repository settings (Settings → Secrets and variables → Actions). The deploy workflow, `.github/workflows/deploy-gcp-gke.yml`, reads them.

| Kind | Name | Value |
|---|---|---|
| Secret | `GCP_WORKLOAD_IDENTITY_PROVIDER` | `terraform output -raw workload_identity_provider` |
| Secret | `GCP_DEPLOYER_SERVICE_ACCOUNT` | `terraform output -raw deployer_service_account` |
| Variable | `GCP_PROJECT_ID` | `terraform output -raw project_id` |
| Variable | `GKE_CLUSTER` | `terraform output -raw cluster_name` |
| Variable | `GKE_LOCATION` | `terraform output -raw cluster_location` (the zone for Standard, the region for Autopilot; update it if you switch) |
| Variable | `GAR_REPOSITORY` | `terraform output -raw artifact_registry_url` |
| Variable | `DOCUMENTS_BUCKET` | `terraform output -raw documents_bucket` |
| Variable | `DOCUMENTS_SERVICE_ACCOUNT` | `terraform output -raw documents_service_account` |
| Variable | `MANAGED_FHIR_STORE` | `terraform output -raw managed_fhir_store_id` (only with `enable_managed_fhir_store = true`) |
| Variable | `MANAGED_FHIR_STORE_SERVICE_ACCOUNT` | `terraform output -raw managed_fhir_store_service_account` |
| Variable | `MANAGED_FHIR_STORE_TENANT` | Optional: the CHO tenant to publish (default `demo`) |

Neither secret is a credential. They identify the federation provider and service account; GitHub's short-lived OIDC token is what authenticates.

Application secrets for the demo (for example `ANTHROPIC_API_KEY` for the AI claims examiner) are listed in the deploy workflow and the runbook.

## What the deployer service account can do

| Role | Scope | Why |
|---|---|---|
| `roles/artifactregistry.writer` | The `cho` repository | Push service images |
| `roles/container.admin` | Project | Deploy to the cluster, including the Strimzi Kafka operator's cluster-scoped resources |
| `roles/iam.workloadIdentityUser` | The service account, for this GitHub repository only | Keyless sign-in from GitHub Actions |

`roles/container.admin` is broader than a production deployer should hold. It is acceptable here because the project exists only for the demo.

## Document storage

document-service stores document bytes (attachment-service and member-document-service file through it, and correspondence-service stores rendered letters there) in one private bucket (`<project_id>-cho-documents` unless `documents_bucket_name` is set). Each CHO container is a prefix inside it, for example `documents/tenants/<tenant>/...`.

- **Access:** uniform bucket-level access, public access prevention enforced. The services reach it as the `cho-documents` Google service account through Workload Identity: no keys exist. That account has `roles/storage.objectUser` on this bucket only, and `roles/iam.serviceAccountTokenCreator` on itself so it can sign pre-signed upload links.
- **Which pods:** only the Kubernetes service accounts in `document_service_accounts` (default `document-service` in `cloudhealthoffice`). `deploy/gcp/render_manifests.py` creates those accounts and points those Deployments at them when `DOCUMENTS_BUCKET` and `DOCUMENTS_SERVICE_ACCOUNT` are set.
- **Versions:** versioning is on; a replaced or deleted version is kept `documents_noncurrent_days` (30) days. Abandoned pre-signed staging uploads (`*.upload`) are deleted after a day.
- **Workload Identity** is enabled on the cluster and the node pool (`GKE_METADATA`). On an existing Standard cluster, applying this updates the cluster in place and rolls the node pool.

## Managed FHIR store, BigQuery and Vertex AI

Off by default. With `enable_managed_fhir_store = true`, `fhir-store.tf` creates a Cloud Healthcare API dataset and R4 FHIR store that fhir-service publishes finalized claims' ExplanationOfBenefit into ([ADR 023](../../docs/adr/023-managed-fhir-store-projection.md)), and configures the store to stream every write to the BigQuery dataset `cho_fhir`. Add `enable_vertex_ai = true` for a BigQuery connection that can call Vertex AI, so BigQuery ML remote models run over those tables. Queries and the end-to-end walkthrough: [Managed FHIR store analytics](../../docs/architecture/managed-fhir-store-analytics.md).

- **Access:** fhir-service reaches the store as the `cho-fhir-store` Google service account through Workload Identity (Kubernetes service account `fhir-service-sa`): `roles/healthcare.fhirResourceEditor` and `roles/healthcare.fhirStoreAdmin` on this store only. The Healthcare service agent writes to the BigQuery dataset, the Pub/Sub topic and the export bucket.
- **Store settings:** client-assigned ids (`enable_update_create`), referential integrity off (CHO publishes resources as events arrive and enforces integrity itself), Pub/Sub change notifications, ANALYTICS_V2 BigQuery streaming, data access audit logs. Enforce ImplementationGuides with `managed_fhir_enabled_implementation_guides` once the IGs are loaded into the store.
- **Location:** the Healthcare dataset is in `managed_fhir_store_location` (default `us-central1`); BigQuery and the export bucket in `managed_fhir_bigquery_location` (default `US`).
- **Synthetic data only**, like the rest of this directory: no BAA, no CMEK, no VPC Service Controls.

## Tear down

```bash
terraform destroy -var="project_id=YOUR_PROJECT_ID"
```

This deletes the cluster, the image repository and the federation setup, and stops all charges they incur. With the managed FHIR store on, the BigQuery dataset and export bucket are likewise kept while they hold data unless `managed_fhir_force_destroy=true`. The documents bucket is not deleted while it holds objects, so stored documents are never removed by accident: empty it first, or apply with `-var="documents_force_destroy=true"` before destroying. Deleting the whole project (`gcloud projects delete YOUR_PROJECT_ID`) also works if the project was created only for the demo.
