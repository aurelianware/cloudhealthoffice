# Risk analysis and risk management plan: hosted environment

**Status:** DRAFT. The Security Officer completes it with the real environment before first PHI and signs it (see [README](README.md)).
**Rule:** 45 CFR 164.308(a)(1)(ii)(A) risk analysis, (B) risk management.
**Method:** based on NIST SP 800-30. For each threat, likelihood and impact are rated Low, Medium or High given the controls in place, and residual risk is the higher of the two unless the controls clearly lower it. Any High residual risk needs a dated plan before PHI arrives.

| Field | Value |
| --- | --- |
| Environment | Hosted HIPAA environment, Google Cloud project `________` (region `________`) |
| Prepared by | ________ (Security Officer) |
| Date | ________ |
| Next review | One year from the date above, or sooner after a major change |

## 1. Where ePHI is

| Asset | ePHI held | Encryption at rest | In transit |
| --- | --- | --- | --- |
| MongoDB (in-cluster, one replica) | Members, coverage, claims, authorizations, documents index, audit events | Persistent disk with the CMEK `disks` key | TLS (internal CA), `requireTLS` |
| Redis (in-cluster) | Accumulator state, cached member and claim fragments, data protection keys | Persistent disk with the `disks` key | TLS, password |
| Kafka (Strimzi, three brokers) | Claim, authorization and member events | Persistent disks with the `disks` key | TLS, SCRAM-SHA-512 |
| Cloud Storage: documents bucket | Attachments, letters, member documents | `storage` key | HTTPS (Google APIs) |
| Cloud Storage: CPCDS drop bucket | Plan extract files as uploaded | `storage` key | SFTP (SFTPGo) then HTTPS |
| Cloud Storage: bulk export bucket | Bulk Data `$export` files, 24-hour expiry | `storage` key | HTTPS, signed by fhir-service |
| Backup for GKE | Copies of all the volumes and Kubernetes objects above, 35 days | `backups` key | Google internal |
| Kubernetes secrets | Database and Kafka passwords, internal CA, IAP client | Application-layer encryption with `gke-secrets` | n/a |
| Secret Manager | Database passwords | `secrets` key | HTTPS |
| Cloud Logging | Request metadata, data access audit logs; must not hold ePHI payloads (see 4.6) | Google default, audit bucket kept six years | n/a |
| Portal and APIs in transit | Everything a user or API client sees | n/a | TLS 1.2+ at the Google load balancer (MODERN policy) |

Not in scope, and must never hold ePHI: the GitHub repository, issues and Actions logs; the synthetic demo project; Cloudflare; laptops (see [POLICIES.md](POLICIES.md), section 6).

## 2. Who and what can reach it

| Path | Who | Control |
| --- | --- | --- |
| Portal (staff) | Plan staff and Aurelianware support named in the plan's tenant | Google Cloud IAP (Google sign-in, plan's MFA policy) then a CHO tenant user record with roles |
| FHIR APIs | Members' apps, providers, payers | SMART on FHIR tokens from smart-auth-service, scopes, tenant check |
| SFTP drop | The plan's file transfer account | SSH key per plan, one bucket prefix per plan |
| Kubernetes control plane | Aurelianware deployer (GitHub Actions) and named administrators | DNS endpoint with IAM on every call; no public IP access |
| Google Cloud console and APIs | Named administrators | Google accounts with 2-step verification; IAM roles; no service account keys (organization policy) |
| Deploys | GitHub Actions in the `hosted` environment | Workload Identity Federation, required reviewers |

## 3. Threats, controls and residual risk

| # | Threat | Controls in place | L | I | Residual | Plan |
| --- | --- | --- | --- | --- | --- | --- |
| T1 | Stolen staff credential used to read member data | IAP plus Google 2-step verification; tenant roles; Cloud Armor rate limits; audit logs | M | H | **M** | Require hardware or app-based 2SV for every Aurelianware account; quarterly access review |
| T2 | Application flaw exposes another tenant's data | Tenant isolation checks in every service; one plan per hosted environment at first | L | H | M | Keep one plan per project until multi-tenant isolation testing is done; yearly penetration test |
| T3 | Web attack (injection, scanning) against public endpoints | Cloud Armor WAF rules (preview until tuned, then enforced), rate limiting, TLS policy | M | H | **M** | Turn WAF enforcement on after two weeks of preview logs (`hosted_waf_enforce = true`) |
| T4 | Insider misuse by Aurelianware staff | Least-privilege IAM; data access audit logs kept six years; sanctions policy; training | L | H | M | Quarterly review of who has project and cluster roles |
| T5 | Cloud misconfiguration (public bucket, open firewall) | Organization policies (no public buckets, uniform access, no external VM addresses, US only); Terraform-only changes; IAM change alerts | L | H | L | Keep changes in Terraform; alerts reviewed weekly |
| T6 | Loss of the MongoDB or Redis disk, or a bad deploy that corrupts data | Daily Backup for GKE, 35 days, 7-day delete lock; Retain reclaim policy; backup-failure alert | M | H | **M** | Six-monthly restore drill; add a MongoDB replica set |
| T7 | Zone or region outage | Regional cluster and Kafka across zones; MongoDB and Redis single replica | M | M | M | Contingency plan emergency mode; add MongoDB replica set |
| T8 | Ransomware or deletion by a compromised admin | Backup delete lock; `prevent_destroy` on keys; deletion protection on the cluster; IAM change alerts | L | H | M | Separate backup admin role from project owner when staff grows |
| T9 | Key compromise or loss | Cloud KMS HSM keys, 90-day rotation, no exportable keys; KMS change alerts | L | H | L | None beyond monitoring |
| T10 | ePHI leaks into logs, tickets or GitHub | Data handling rules; PHI redaction checks in CI; training | M | M | M | Review a sample of application logs each quarter (ACCESS-REVIEW step 5) |
| T11 | Subcontractor failure (Google) | Google Cloud BAA; Google's SOC 2 and ISO 27001 reports | L | H | L | Re-check Google's covered-products list yearly |
| T12 | AI examiner sends claim data to a vendor without a BAA | Hosted deploy refuses the examiner unless `ANTHROPIC_BAA_CONFIRMED=true` | L | H | L | Turn on only after a BAA with Anthropic is signed |
| T13 | Lost or stolen laptop with access | Full-disk encryption, screen lock, no ePHI on laptops, sessions behind IAP | M | M | L | Device checklist in the training record |
| T14 | Malicious or compromised dependency or image | Dependency and image scanning in CI; images built from source into the hosted registry; required reviewers on deploys | M | H | M | Pin base images by digest; Binary Authorization later |
| T15 | Late or missed breach notice to the plan | Incident procedure with deadlines; alert email channel | L | M | L | Yearly tabletop exercise |

L: likelihood, I: impact. Residual risks in **bold** have a plan item due before or shortly after first PHI.

## 4. Risk management plan

| Item | Owner | Due | Done |
| --- | --- | --- | --- |
| 4.1 Every Aurelianware Google account uses 2-step verification with a security key or authenticator app (no SMS) | Security Officer | Before first PHI | |
| 4.2 Restore drill passes and timing recorded | Security Officer | Before first PHI | |
| 4.3 WAF enforcement on after preview review | Engineering | Two weeks after go-live | |
| 4.4 MongoDB replica set across zones | Engineering | Before a second plan | |
| 4.5 Penetration test of the hosted endpoints | Security Officer | Within six months of go-live | |
| 4.6 Confirm application logs carry no ePHI payloads (sample review) | Engineering | Before first PHI, then quarterly | |
| 4.7 Base images pinned by digest | Engineering | Within three months | |

## Sign-off

Security Officer: ________________________ Date: ________

CEO: ________________________ Date: ________
