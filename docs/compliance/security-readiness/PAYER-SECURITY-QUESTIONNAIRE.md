# Payer security questionnaire: draft answers

**Status as of:** November 21, 2026. Draft answers for the Aurelianware-hosted environment, written ahead of any plan's questionnaire. Review and date each answer before sending. Every "Built" control is in code or Terraform but **not yet running**, because the hosted environment has not been applied. Say so on every questionnaire until it is.

A Texas Medicaid plan such as Parkland Community Health Plan (PCHP) usually sends its own vendor risk questionnaire, often based on the Shared Assessments SIG Lite, HITRUST or NIST SP 800-53 (which the Texas HHS SPI form uses). The questions below are grouped the way those questionnaires group them, so the answers can be pasted into whichever form arrives.

**Answer key:** Yes (in place), Built (in code or Terraform, not yet operating), Drafted (written, not adopted), Planned (with a trigger), No.

## 1. Company and scope

| Question | Answer |
| --- | --- |
| Legal name, ownership | Aurelianware, Inc. Founder-owned, raising a pre-seed round |
| Product and service in scope | Cloud Health Office (CHO): health plan administration and CMS-0057-F interoperability (FHIR APIs, prior authorization, claims), delivered as an Aurelianware-hosted environment on Google Cloud, or as software the plan hosts |
| Will you store, process or transmit our PHI? | Yes, in the hosted model. No, if the plan hosts CHO, unless support staff are given access |
| Data types | Member enrollment and coverage, claims, prior authorizations, provider data, documents and letters. Received through SFTP file drops (CPCDS and authorization extracts) and FHIR APIs |
| Where is data stored? | Google Cloud, one dedicated project per plan, in a single US region. An organization policy restricts resource locations to the US |
| Offshore access? | No. No staff, contractors or subcontractors outside the US, and no PHI stored or viewed outside the US |
| Number of employees with access | Two founders today. Access is named, approved and reviewed quarterly |
| Subcontractors with PHI access | Google Cloud only (Google Cloud BAA). Anthropic only if the optional AI claims examiner is turned on, and only after a BAA with Anthropic is signed; the deploy refuses it otherwise |

## 2. Certifications and assessments

| Question | Answer |
| --- | --- |
| SOC 2 Type II? | No. Planned: Type I about three months after the hosted environment is applied, then a Type II with a three-month first window ([SOC2-ROADMAP.md](SOC2-ROADMAP.md)). Google Cloud's own SOC 2 and ISO 27001 reports cover the infrastructure |
| HITRUST? | No. Not planned unless a plan requires it |
| ISO 27001? | No |
| Independent penetration test? | Not yet. Planned before first PHI or within six months of go-live, of the hosted web, API and cloud configuration. The letter will be shared under NDA |
| TX-RAMP? | No. It applies to cloud services contracted by Texas state agencies. Tell us if your organization requires it |
| HIPAA risk analysis? | Drafted, NIST SP 800-30 based, 15 threats ([RISK-ANALYSIS.md](../hipaa-hosted/RISK-ANALYSIS.md)). Completed and signed before first PHI |

## 3. Governance, policies and people

| Question | Answer |
| --- | --- |
| Information security program and policies | Drafted: security policies, incident response and breach notification, contingency plan, training, access review ([hipaa-hosted/](../hipaa-hosted/README.md)). Adopted and signed before first PHI. Copies available on request within three business days once adopted |
| Named security and privacy officers | Planned: named before first PHI |
| Policy review cadence | Yearly and after any major change |
| Background checks | Planned: policy adopted, and checks for anyone with access to the hosted environment |
| Security awareness training | Drafted: before access and yearly, with signed acknowledgement; quarterly reminders |
| Sanctions policy | Drafted |
| Confidentiality agreements | Planned for all workforce members |

## 4. Access control and identity

| Question | Answer |
| --- | --- |
| Unique user IDs, no shared accounts | Yes by design. Every person signs in with their own account. Service accounts are for workloads only and have no keys (organization policy blocks keys) |
| MFA for your administrators | Policy (drafted) requires Google 2-step verification with a security key or authenticator app, no SMS. Enforcement planned before first PHI |
| MFA for our users | Plan staff reach the portal through Google Cloud Identity-Aware Proxy (IAP) signing in with the plan's own identity, so the plan's MFA policy applies. The plan's own identity provider can be used instead |
| SSO | Yes: through IAP, or the plan's OpenID Connect identity provider |
| Role-based access | Built. Each endpoint requires a permission; roles include TenantAdmin, ClaimsExaminer, ClaimsSupervisor, UMCoordinator, MemberServices, EnrollmentSpecialist, ProviderRelations, Finance, ComplianceOfficer and ComplianceViewer. The plan's TenantAdmin manages its own users |
| Least privilege for administrators | Built. At most two project Owners; narrower roles for daily work; cluster access through IAM-checked DNS endpoint only, no public control plane IP |
| Access reviews | Drafted: quarterly ([ACCESS-REVIEW.md](../hipaa-hosted/ACCESS-REVIEW.md)) |
| Termination | Drafted: access removed the same business day |
| Session timeout | IAP sessions expire. The portal's idle timeout is 2 hours today; it will be lowered to 15 to 30 minutes before first PHI |
| API access | SMART on FHIR (OAuth 2.0) with scopes, patient binding and tenant checks. Provider Directory and discovery documents are public as CMS requires |
| Vendor support access to our data | Only to resolve a ticket the plan raised, logged, by named people |

## 5. Data protection

| Question | Answer |
| --- | --- |
| Encryption at rest | Built. Customer-managed Cloud KMS keys, HSM-backed, rotated every 90 days, on Kubernetes secrets, all disks (MongoDB, Kafka, Redis), every storage bucket, the image registry and backups. Keys are protected from deletion and each Google service can use only its own key |
| Encryption in transit | Built. TLS 1.2 or later (Google MODERN policy) at the load balancer. Inside the cluster, TLS on Kafka (with SCRAM-SHA-512), MongoDB and Redis. SFTP for file transfer |
| Tenant isolation | One dedicated Google Cloud project per hosted plan at first. Inside CHO, the tenant comes only from the validated token, and every query is filtered by tenant, with tests in each service |
| PHI in logs | Built. A mandatory PHI-scrubbing processor on telemetry, and PHI redaction checks in CI. Application logs are sampled quarterly to confirm |
| Data retention and disposal | Backups expire after 35 days. Bulk export files expire after 24 hours. On termination, PHI is returned or destroyed; audit logs that record access are kept six years as HIPAA documentation |
| Data loss prevention | Organization policies block public buckets and external VM addresses. No PHI on laptops, email or chat (policy) |
| Use of our data for other purposes, AI training | No. Data is used only to provide the services. The optional AI claims examiner is off unless a BAA with the model provider exists and the plan agrees, and is advisory only |

## 6. Network and infrastructure

| Question | Answer |
| --- | --- |
| Hosting | Google Cloud, BAA-covered services only |
| Network segmentation | Built. Dedicated VPC, private cluster nodes with no external addresses, Cloud NAT for outbound, Private Google Access |
| Firewall and WAF | Built. Cloud Armor on every public backend: Google's preconfigured rules for SQL injection, XSS, file inclusion, code execution and scanners (logged for two weeks to tune, then enforced), per-IP rate limiting with a ban, and Adaptive Protection |
| DDoS | Google's global load balancer and Cloud Armor Adaptive Protection |
| Hardened hosts | Built. GKE Shielded nodes with secure boot, least-privilege node service account, automatic surge upgrades |
| Infrastructure as code | Yes. All changes through Terraform and a reviewed deploy workflow with required approval |

## 7. Logging, monitoring and vulnerability management

| Question | Answer |
| --- | --- |
| Audit logging | Built. Data access logs for every Google Cloud service, copied to a dedicated log bucket kept six years, lockable once retention is agreed. CHO logs who read which member's record, allowed or denied |
| Security alerting | Built. Alerts on IAM changes, key changes, logging changes, organization policy changes and failed backups. Alerts in CHO on failed sign-ins are planned |
| 24x7 monitoring | No. Alerts go to the security list and will page an on-call phone before a Medicaid plan signs. Acknowledgement within one business day today |
| Vulnerability scanning | Yes. Trivy (images and filesystem), npm audit, .NET vulnerable package checks, Dependabot, gitleaks secret scanning and CodeQL static analysis on every pull request and weekly. Advisory waivers are documented with a reason and an exit |
| Patching | Dependabot updates; GKE automatic upgrades; images rebuilt from source on every deploy |
| Supply chain | Images built from source into the plan's own registry. Base images pinned by digest within three months of go-live; Binary Authorization later |

## 8. Secure development

| Question | Answer |
| --- | --- |
| SDLC | Pull requests with CI checks: unit and integration tests (about 310,000 lines of tests), security scans, PHI checks, FHIR profile validation and Inferno conformance suites. Nine ONC Inferno test kits (including Da Vinci CRD, DTR and PAS) pass against synthetic data |
| Code review | Every change goes through a pull request. Deploys to the hosted environment run in a GitHub environment with required reviewers. Branch protection rules will be exported as evidence |
| Source code availability | Source-available under BSL 1.1. Your security team can read the code it is reviewing |
| Production data in development | Never. Development and the demo use synthetic data only; the hosted deploy refuses the demo tenant and synthetic data |
| Secrets management | Google Secret Manager; no secrets in the repository; secret scanning in CI; keyless deploys through Workload Identity Federation |

## 9. Incident response and breach notification

| Question | Answer |
| --- | --- |
| Incident response plan | Drafted ([INCIDENT-RESPONSE.md](../hipaa-hosted/INCIDENT-RESPONSE.md)): triage within 4 hours of report, risk assessment, notice, review within 30 days. Tabletop yearly |
| Breach notice to the plan | Our template BAA says 15 calendar days. For a Texas Medicaid plan we accept the DUA clocks: one hour from discovery for Medicaid confidential information from a federal system of records, 24 hours otherwise, written report by the third business day |
| Breaches in the last three years | None. No production PHI has been held |
| Cyber insurance | Planned: $1M to $5M cyber liability before first PHI. Certificate on request once bound |

## 10. Business continuity and disaster recovery

| Question | Answer |
| --- | --- |
| Backups | Built. Daily Backup for GKE of every namespace, its secrets and volumes, encrypted with its own key, kept 35 days, undeletable for 7 days. Alert on a failed backup |
| RTO and RPO | 24 hours and 24 hours for the regulatory APIs, prior authorization and the portal; 72 hours for claims and file import during a pilot ([CONTINGENCY-PLAN.md](../hipaa-hosted/CONTINGENCY-PLAN.md)) |
| DR testing | Restore drill before first PHI, then every six months. Not yet run |
| High availability | Regional cluster across three zones; Kafka spread across zones. MongoDB and Redis run one replica today, so a zone outage means a reattach or a restore. A MongoDB replica set is planned before a second plan |
| Business continuity of the company | A two-person company. The plan's core system remains the system of record during a pilot; data can be exported through the FHIR Bulk Data API; source code is available to the plan |

## 11. Physical security

| Question | Answer |
| --- | --- |
| Data centers | Google Cloud's, under its BAA and its SOC 2 and ISO 27001 reports |
| Offices | No PHI in any office. Remote workforce |
| Workstations | Drafted: full-disk encryption, five-minute screen lock, automatic updates, no PHI on laptops. Device management planned for SOC 2 |

## Answers to fix before sending to a plan

These are the "Planned" and "Drafted" answers that close cheaply before a security review, and the ones a reviewer will press on:

1. Apply the hosted environment and run the restore drill.
2. Adopt the policies; name the officers; train and sign.
3. Enforce 2-step verification with security keys.
4. Lower the portal idle timeout.
5. Penetration test.
6. Cyber insurance.
7. On-call phone paging for security alerts.
