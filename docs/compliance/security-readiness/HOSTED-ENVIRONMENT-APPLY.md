# Applying the hosted HIPAA environment

**Status as of:** November 21, 2026. Written and validated in CI, never applied. Nothing on this page has been run.
**Build reference:** [GCP-HOSTED-HIPAA.md](../../deploy/GCP-HOSTED-HIPAA.md) has every command. This page is the checklist around it: the decisions, the accounts, the order, the time and the cost.

## What exists

| Workstream | In the repository |
| --- | --- |
| 1. Infrastructure base | `infrastructure/gcp` with `hosted = true`: four HSM keys, private regional cluster, organization policies, six-year audit log bucket |
| 2. State services | Kafka with TLS and SCRAM, Redis with TLS and a password, MongoDB with TLS, all on encrypted disks (`deploy/gcp/hosted/`, `scripts/deploy-gke.sh` with `HOSTED=true`) |
| 3. Ingress | Google load balancer, managed certificates, Cloud Armor, IAP (`hosted-ingress.tf`, `deploy/gcp/hosted/apply-ingress.sh`) |
| 4. Production hosts and sign-in | Hosted mode in the deploy script; the `deploy-gcp-hosted` workflow, manual only, in GitHub environment `hosted` |
| 5. Secrets | Secret Manager for database passwords (`hosted-secrets.tf`) |
| 6. Security alerts | Five audit log alerts (`hosted-operations.tf`) |
| 7. Backups | Backup for GKE, daily, 35 days, 7-day delete lock |
| 8. Administrative safeguards | Drafts in [hipaa-hosted/](../hipaa-hosted/README.md) |

The proof so far is `terraform validate` in CI and stub tests of the deploy and ingress scripts (`deploy/gcp/hosted/test_*.py`). The first real apply is the real test, so budget time for fixes.

## Decisions to make first

| Decision | Recommendation |
| --- | --- |
| When to apply | Create the organization and project and accept the Google BAA now; they're free. Apply the infrastructure when a plan starts its security review, or when the raise closes, whichever is first. Applying starts the monthly run cost and the SOC 2 clock |
| Region | `us-central1` (Iowa), as in the build doc. `us-south1` (Dallas) is closer to Texas plans; check that every service the profile uses, and HSM keys, are offered there before choosing it |
| Domain and host names | Three names under a subdomain the plan will recognize, for example `portal.hosted.cloudhealthoffice.com`, `fhir.…` and `auth.…`. Record type DNS only, never Cloudflare-proxied |
| One project per plan | Yes, at first. The risk analysis (T2) depends on it until multi-tenant isolation testing is done |
| Terraform state | Keep it in a Cloud Storage bucket in the hosted project, versioned and encrypted with the `storage` key, not on a laptop. Add a `backend "gcs"` block for the hosted apply only. State holds resource names, not PHI, but it is the only record of what was built |
| Who applies | The CTO, with Owner on the project and Organization Policy Administrator |
| Alert recipients | A `security@` group plus a phone on an on-call rota (needed for a Texas Medicaid plan's one-hour notice clock) |
| Audit log lock | Leave `hosted_lock_audit_logs = false` until counsel confirms six years; locking can't be undone |
| AI claims examiner | Off (`ANTHROPIC_BAA_CONFIRMED` unset) until a BAA with Anthropic is signed |

## Order of steps

Time is working time for one person who knows the code, assuming no surprises on first apply.

| # | Step | Who | Time |
| --- | --- | --- | --- |
| 1 | Create the Google Cloud organization for the company domain (Cloud Identity Free, DNS TXT verification) | Account owner | Under an hour, plus DNS propagation |
| 2 | Create the hosted project under the organization; link billing; set a budget alert | Account owner | 15 minutes |
| 3 | Accept the Google Cloud HIPAA BAA for the project (IAM & Admin, Privacy) | Account owner | 10 minutes |
| 4 | Request quota: about 32 vCPU and 500 GB SSD in the region | Account owner | Request in minutes; approval can take one to three business days |
| 5 | Enforce 2-step verification with security keys on every account that will have project roles | Security Officer | An hour |
| 6 | `terraform apply` with `hosted=true` (command in the build doc) | CTO | Half a day, including the cluster build |
| 7 | Create the IAP OAuth client (console, once) | CTO | 30 minutes |
| 8 | Fill the GitHub environment `hosted`: required reviewers, secrets and variables from the Terraform outputs (list at the top of `deploy-gcp-hosted.yml`) | CTO | An hour |
| 9 | Run `deploy-gcp-hosted` (build images, deploy, apply ingress) | CTO, approved by a second person | Half a day to two days for first-apply fixes |
| 10 | DNS A records for the three hosts; wait for the managed certificate | CTO | An hour, certificate usually within an hour |
| 11 | Smoke test with a **synthetic** test tenant created for the purpose, then delete it | CTO | Half a day |
| 12 | Restore drill (build doc, "Backups and alerts"); record the time | Security Officer | Half a day |
| 13 | Two weeks of WAF preview logs, tune false positives, then `hosted_waf_enforce = true` | CTO | Spread over two weeks |
| 14 | Penetration test of the hosted endpoints | Outside firm | One to two weeks of testing, plus scheduling |
| 15 | Complete the risk analysis with the real environment; sign it and the policies | Security Officer and CEO | A day |

Steps 1 to 12 take about one to two weeks of elapsed time. Steps 13 and 14 run in parallel and take two to four weeks more.

## After a plan signs

With all of the above done, onboarding a plan is configuration:

| Step | Time |
| --- | --- |
| BAA and any state flow-down signed (the long pole: start legal review in the first security call) | Before this list starts |
| `HOSTED_TENANT`, `HOSTED_TENANT_NAME` and `HOSTED_ADMIN_EMAILS` for the plan; redeploy | An hour |
| Add plan staff to `hosted_portal_members` (IAP); re-apply | An hour |
| Plan's SFTP account and SSH key for the file drop (`cpcds-drops.tf`) | An hour |
| Register the plan's identity provider in smart-auth-service if it doesn't use IAP | Half a day |
| First file drop: CPCDS and authorization extracts, import, reconcile counts with the plan | One to three days, depending on file quality |
| Portal idle timeout, alert routing and incident contacts set for this plan | An hour |

So a plan can be live within a few days of signing. If a second plan signs, it gets its own project: repeat steps 2 to 12 with new state, about a week.

## Run cost

Estimated from Google Cloud list prices for the defaults (three e2-standard-8 nodes, one per zone). Check with the pricing calculator before applying; the budget alert in the apply command is $2,000 a month.

| Item | Per month |
| --- | --- |
| Three e2-standard-8 nodes | About $590 |
| Regional cluster management fee | About $73 |
| Disks (boot and data, balanced) | About $50 |
| Load balancer, Cloud NAT, Cloud Armor Standard | About $80 to $150 |
| Cloud KMS HSM keys (six keys, rotating) | Under $30, growing slowly as versions accumulate |
| Logging (data access logs, six-year bucket) | $20 to $200, depending on traffic |
| Backup for GKE and backup storage | About $50 to $100 |
| **Total** | **About $900 to $1,500** |

Cloud Armor's full Adaptive Protection is part of Cloud Armor Enterprise, which costs several thousand dollars a month. Confirm which tier the profile needs; Standard is enough for a pilot.

## Gaps to close in code before first PHI

These are small and belong in their own pull requests:

1. Portal session idle timeout: 2 hours today (`src/portal/CloudHealthOffice.Portal/Program.cs`). Lower to 15 to 30 minutes, configurable.
2. Failed sign-in alerting inside CHO (workstream 6, "next").
3. A `backend "gcs"` block for hosted Terraform state.
4. Base images pinned by digest (risk plan item 4.7).
5. Update workstream 8's status in [GCP-HOSTED-HIPAA.md](../../deploy/GCP-HOSTED-HIPAA.md) from "Next" to "Drafted, not adopted" (done in this change).
