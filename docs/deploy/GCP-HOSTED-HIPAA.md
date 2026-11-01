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
| 2 | State services: Kafka (Strimzi) with a TLS-only listener and SCRAM-SHA-512, Redis with TLS, a password and an append-only file, MongoDB with TLS, all on encrypted disks | `HOSTED=true` in `scripts/deploy-gke.sh` (see Deploy) |
| 3 | Ingress: Google external load balancer, managed certificates, Cloud Armor, Identity-Aware Proxy for the portal, public FHIR endpoints under SMART, the SFTP address | Next |
| 4 | Production hosts and sign-in: a `hosted` mode in `scripts/deploy-gke.sh` (Production hosts, synthetic data refused, Cloud KMS token signing, staff sign-in from IAP or the plan's identity provider), and the deploy using the DNS endpoint | `HOSTED=true` and the `deploy-gcp-hosted` workflow (see Deploy) |
| 5 | Secrets in Secret Manager | Database passwords in Secret Manager (`hosted-secrets.tf`), copied into Kubernetes secrets encrypted with the `gke-secrets` key |
| 6 | Security alerts on the audit logs (`hosted-operations.tf`) | In this module. Failed sign-ins in CHO itself are next |
| 7 | Backup for GKE, daily, 35 days, encrypted with its own key (`hosted-operations.tf`) | In this module. The restore drill runs after the first apply |
| 8 | Administrative safeguards: risk analysis, policies, training record, quarterly access review, plan BAA template | Next |

Cloudflare Tunnel and Access stay on the demo only. Cloudflare decrypts traffic at its edge, so it would need its own BAA before it could carry PHI.

## Deploy

`scripts/deploy-gke.sh` with `HOSTED=true` deploys onto the hosted cluster. The `deploy-gcp-hosted` workflow runs it, and then `deploy/gcp/hosted/apply-ingress.sh`, from a manual run only. Every job runs in the GitHub environment `hosted`. Give that environment required reviewers so each deploy is approved, and keep the hosted secrets and variables there, never at repository level. The workflow's header lists each setting and the Terraform output it comes from.

What hosted mode changes, compared with the demo:

| Area | Hosted |
| --- | --- |
| Disks | StorageClass `cho-pd-cmek`: balanced disks encrypted with the `disks` key, kept when a claim is deleted (`Retain`) |
| Kafka | `deploy/gcp/hosted/kafka-cluster.yaml`: one TLS listener on 9093 with SCRAM-SHA-512, brokers spread across zones, a five-year cluster CA. Every service signs in as KafkaUser `cho-services`; the script copies its password into `kafka-secret` |
| MongoDB | TLS required (`requireTLS`), 100 GB encrypted disk |
| Redis | `deploy/gcp/hosted/redis.yaml`: TLS only, a password, an append-only file fsynced every second on an encrypted disk. benefit-plan-service's accumulator state survives a restart |
| Certificates | A private CA in Secret `cho-internal-ca` issues the MongoDB and Redis certificate (825 days, reissued 30 days before expiry). The services trust ConfigMap `cho-ca-bundle`: the public CAs, Kafka's cluster CA and the internal CA, mounted over each image's own bundle |
| Passwords | Generated once, kept in Secret Manager (`cho-mongodb-password`, `cho-redis-password`, `cho-postgres-password`, `cho-pricing-admin-secret`), so a rebuilt or restored cluster gets the same ones. If the deployer can't read a secret, the deploy stops rather than make a new password |
| Tenant | No demo tenant or synthetic member. The plan's tenant (`HOSTED_TENANT`) and a TenantAdmin for each `HOSTED_ADMIN_EMAILS` address |
| Sign-in and hosts | Production hosts, Google Cloud IAP for staff, public FHIR and SMART hosts (`FHIR_HOST`, `AUTH_HOST`) behind the Google load balancer, whose GFE ranges are the only trusted proxies |
| Refused | Cloudflare (tunnel and Access), the demo tenant or a demo cluster context, the small resource profile, Kafka off, the managed FHIR store copy, and the AI claims examiner unless `ANTHROPIC_BAA_CONFIRMED=true` (it sends claim data to Claude, so it needs a BAA with Anthropic) |

The Kafka cluster CA renews 60 days before it expires. Deploy at least once in that window so `cho-ca-bundle` carries the new CA.

Known limits:

- MongoDB and Redis run one replica each. A zone outage stops them until the disk is reattached in another zone or restored from the daily backup.
- Kafka authenticates every client but has no per-topic ACLs: all services share one Kafka identity.
- PostgreSQL (reference-data-service) holds code sets, not member data. It uses the encrypted disk but not TLS.
- `tests`: `python3 -m pytest deploy/gcp/hosted/test_deploy_gke_hosted.py` runs the script against stub `kubectl`, `helm` and `gcloud`. It checks what is applied, not a live cluster. The first real deploy is the proof.
