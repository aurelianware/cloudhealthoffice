# Contingency plan: hosted environment

**Status:** DRAFT, not adopted. The restore drill in section 5 must pass before first PHI.
**Rule:** 45 CFR 164.308(a)(7): data backup plan, disaster recovery plan, emergency mode operation plan, testing and revision, applications and data criticality analysis.

## 1. Criticality

| Function | Criticality | Recovery time objective | Recovery point objective |
| --- | --- | --- | --- |
| Member and provider FHIR APIs (Patient Access, Provider Access, Payer-to-Payer, Provider Directory) | High: regulatory APIs | 24 hours | 24 hours |
| Prior authorization (CRD, DTR, PAS) and decisions | High: regulatory timeframes (72 hours expedited, 7 days standard) | 24 hours | 24 hours |
| Portal for plan staff | High | 24 hours | 24 hours |
| CPCDS file drop and import | Medium: plans can resend files | 72 hours | Last processed file |
| Claims, accumulators, letters | Medium during a pilot: the plan's core system stays the system of record | 72 hours | 24 hours |
| Reporting and bulk export | Low | 7 days | 24 hours |

Recovery point is the daily backup. Recovery time assumes the region is up. For a region loss, see section 3.

## 2. Data backup plan

| What | How | Retention |
| --- | --- | --- |
| All volumes (MongoDB, Redis, Kafka, PostgreSQL) and Kubernetes objects in the cluster | Backup for GKE plan `cho-daily`, every day at 07:00 UTC, encrypted with the `backups` key | 35 days; cannot be deleted for the first 7 days |
| Database passwords | Secret Manager, one version per change | Until destroyed on purpose |
| Documents, CPCDS drops, bulk export files | Cloud Storage buckets with versioning where enabled | Bucket lifecycle rules |
| Infrastructure | Terraform in the repository, with state kept for the hosted project | Git history |
| Audit logs | Log bucket `cho-audit` | Six years |

A failed backup sends an alert to the security email list. The Security Officer acknowledges it within one business day and makes sure the next backup succeeds.

## 3. Disaster recovery plan

**Lost data or a bad change (cluster still up).** Restore the affected namespace or volumes from the latest good backup:

```bash
gcloud beta container backup-restore restore-plans create cho-restore \
  --project=PROJECT --location=REGION \
  --backup-plan=projects/PROJECT/locations/REGION/backupPlans/cho-daily \
  --cluster=projects/PROJECT/locations/REGION/clusters/cho-hosted \
  --cluster-resource-conflict-policy=use-existing-version \
  --namespaced-resource-restore-mode=delete-and-restore \
  --volume-data-restore-policy=restore-volume-data-from-backup \
  --selected-namespaces=cloudhealthoffice
gcloud beta container backup-restore restores create restore-$(date +%Y%m%d%H%M) \
  --project=PROJECT --location=REGION --restore-plan=cho-restore \
  --backup=projects/PROJECT/locations/REGION/backupPlans/cho-daily/backups/BACKUP
```

Then rerun the deploy workflow so the CA bundle and secrets match Secret Manager.

**Lost cluster.**

1. `terraform apply` rebuilds the cluster. The keys, buckets and secrets survive, because they are protected from destroy.
2. Restore the backup into the new cluster.
3. Rerun the deploy workflow.
4. Rerun `apply-ingress.sh`. The static IP survives, so DNS doesn't change.

**Lost region.** The backups live in the same region. Recovering from a region loss means waiting for the region to come back, unless a cross-region backup has been added (an open item in the [risk analysis](RISK-ANALYSIS.md), T7). Tell affected plans under section 4 below.

## 4. Emergency mode operation

When the environment is impaired but running (for example MongoDB is down while Kafka is up):

- The incident lead decides whether to pause inbound feeds (the CPCDS drop, the import jobs) so nothing is half-processed.
- Security controls stay on in emergency mode. Never turn off TLS, IAP, audit logging or tenant checks to restore service faster.
- **Break-glass access** is for when IAP or Google sign-in is down and an administrator must reach the cluster:
  1. The Security Officer grants themself `roles/container.admin` for the shortest time needed. The IAM alert fires, which is intended.
  2. They reach the cluster through the DNS endpoint and record every action.
  3. They remove the grant afterwards and log the use in the incident log.
- Tell the plan's contact when the service is degraded for more than 4 hours. For the regulatory APIs, the plan may need to tell its own regulators.

## 5. Testing and revision

**Restore drill, every six months and before first PHI.**

1. Restore the latest backup into a scratch namespace (or a scratch cluster in the same project).
2. Check that MongoDB starts, that member and claim counts match the source, and that Redis has its append-only file.
3. Record the backup's age, the time taken and any problems in the compliance store.
4. Delete the scratch restore.

The drill passes if the restore completes within the 24-hour recovery time objective and the counts match. Update this plan after every drill, incident, or major change.

## Sign-off

Security Officer: ________________________ Date: ________
