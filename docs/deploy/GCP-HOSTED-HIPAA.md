# Hosted HIPAA environment on Google Cloud

Status: written ahead of a signed pilot, not applied. The infrastructure base, daily backups and security alerts are in `infrastructure/gcp` as the `hosted` profile. The environment must not hold real member data until every workstream is done, the restore drill has passed, and the policies are signed.

This is the environment Aurelianware runs for plans that send real data to a CHO we host (Mode B of the [CPCDS trial kit](../integration/CPCDS-TRIAL-KIT.md)). It is a separate Google Cloud project from the [demo](GCP-DEMO.md). They share no cluster, key, bucket or credential, and the demo keeps synthetic data only.

## Before the first apply

These steps need the account owner.

1. **Organization.** Create a Google Cloud organization for the company domain (Cloud Identity Free, verified with a DNS TXT record). The organization policies below are set on the project and need it.
2. **Project.** Create a new project under the organization, linked to billing. Never reuse the demo project.
3. **BAA.** In the console, open IAM & Admin, then Privacy (`console.cloud.google.com/iam-admin/privacy`), choose the new project, click **Review and Accept** under the Google Cloud HIPAA Business Associate Addendum, then **I Accept**. Google's steps: [Privacy compliance and records for Google Cloud](https://support.google.com/cloud/answer/6329727). Use only [BAA-covered products](https://docs.cloud.google.com/docs/security/compliance/hipaa) for PHI.
4. **Quota.** Request about 32 vCPU and 500 GB of SSD in the region. The hosted cluster runs `node_count` nodes in each of the region's three zones.
5. **Roles.** The person applying needs Owner on the project and Organization Policy Administrator (for the policies), or set `hosted_enforce_org_policies = false` and have an organization administrator set them.

## Apply the infrastructure base

Use separate state from the demo, for example a fresh working copy or a `-state` file kept with the project.

```bash
cd infrastructure/gcp
terraform init
terraform apply \
  -var="project_id=YOUR_HOSTED_PROJECT" \
  -var="region=us-central1" \
  -var="cluster_name=cho-hosted" \
  -var="hosted=true" \
  -var='hosted_alert_emails=["security@example.com"]' \
  -var="enable_token_signing_kms=true" \
  -var="enable_cpcds_drops=true" \
  -var="enable_bulk_export=true" \
  -var="billing_account_id=XXXXXX-XXXXXX-XXXXXX" \
  -var="monthly_budget_usd=2000"
```

With `hosted = true` the module builds:

| Area | What it sets |
| --- | --- |
| Encryption keys | Key ring `cho-cmek` with four keys (`gke-secrets`, `disks`, `storage`, `artifacts`), HSM by default, rotated every 90 days and protected from `terraform destroy`. Each Google service agent can use only its own key. |
| Encrypted resources | Kubernetes secrets (application-layer encryption), node boot disks, every bucket (documents, CPCDS drops, bulk export) and the Artifact Registry repository |
| Network | VPC `cho-hosted` with Private Google Access and flow logs, and Cloud NAT for outbound traffic |
| Cluster | Regional Standard cluster, private nodes, Shielded nodes with secure boot, balanced disks, a least-privilege node account, surge upgrades, deletion protection always on |
| Control plane access | The DNS endpoint, which checks IAM on every call (`gcloud container clusters get-credentials cho-hosted --region REGION --dns-endpoint`). The IP endpoint takes only `hosted_authorized_networks`, empty by default. |
| Organization policies | US-only locations, no service account keys, no external VM addresses, no serial port, OS Login, uniform and non-public buckets |
| Audit logs | Data access logs for every service, copied to log bucket `cho-audit` and kept 2,192 days (six years). Set `hosted_lock_audit_logs = true` once the retention is agreed. Locking can't be undone. |

The hosted profile refuses `autopilot = true` and `enable_managed_fhir_store = true`. The managed FHIR store copy writes to a Healthcare dataset, BigQuery and a multi-region bucket that these keys don't cover yet.

Two outputs feed the next steps: `hosted_disk_kms_key` (the encrypted StorageClass) and `hosted_storage_kms_key`.

## Backups and alerts

`hosted-operations.tf` adds:

- **Backup plan `cho-daily`.** Every namespace, its secrets and its volume data (MongoDB, Kafka, Redis), daily at `hosted_backup_cron` (07:00 UTC). Backups are kept `hosted_backup_retain_days` (35) and can't be deleted by anyone for the first `hosted_backup_delete_lock_days` (7). They're encrypted with key `backups`. The Backup for GKE agent can also use the `disks` key, so it can restore encrypted volumes.
- **Alert policies**, emailed to `hosted_alert_emails`. Each fires on one matching audit log entry, at most once every five minutes:

| Alert | Fires when |
| --- | --- |
| IAM policy changed | Any `SetIamPolicy` call in the project |
| Encryption key disabled, destroyed or changed | A Cloud KMS key or key version is disabled, destroyed or updated |
| Audit log routing or retention changed | The audit sink, a log bucket or a log is changed or deleted |
| Organization policy changed | A project policy is created, changed or deleted |
| Daily cluster backup failed | A Backup for GKE backup ends in `FAILED` ([Google's filter](https://docs.cloud.google.com/kubernetes-engine/docs/add-on/backup-for-gke/how-to/alerts)) |

**Restore drill**, once after the first apply and then every quarter:

1. In the console, open Backup for GKE and pick the latest `cho-daily` backup.
2. Restore it into a new namespace (for example `restore-drill`) on the same cluster, with volume data.
3. Time it, then check that MongoDB starts and that a known member and claim read back.
4. Delete the namespace, and record the date, the time taken and the result.

## Workstreams

| # | Workstream | Status |
| --- | --- | --- |
| 1 | Infrastructure base: keys, private regional cluster, organization policies, audit log bucket | In this module |
| 2 | State services: Managed Service for Apache Kafka with TLS and SASL, Memorystore for Redis, MongoDB on an encrypted SSD StorageClass with TLS | Next |
| 3 | Ingress: Google external load balancer, managed certificates, Cloud Armor, Identity-Aware Proxy for the portal, public FHIR endpoints under SMART, the SFTP address | Next |
| 4 | Production hosts and sign-in: a `hosted` mode in `scripts/deploy-gke.sh` (Production hosts, synthetic data refused, Cloud KMS token signing, staff sign-in from IAP or the plan's identity provider), and the deploy using the DNS endpoint | Next |
| 5 | Secrets in Secret Manager behind the secret provider interface | Next |
| 6 | Security alerts on the audit logs (`hosted-operations.tf`) | In this module. Failed sign-ins in CHO itself are next |
| 7 | Backup for GKE, daily, 35 days, encrypted with its own key (`hosted-operations.tf`) | In this module. The restore drill runs after the first apply |
| 8 | Administrative safeguards: risk analysis, policies, training record, quarterly access review, plan BAA template | Next |

Cloudflare Tunnel and Access stay on the demo only. Cloudflare decrypts traffic at its edge, so it would need its own BAA before it could carry PHI.
