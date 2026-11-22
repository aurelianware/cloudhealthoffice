# HIPAA compliance posture

**Status as of:** November 21, 2026. Not legal advice. There is no HIPAA certification; a business associate complies by having the safeguards, the documentation and the BAAs. This page says which of those exist.

## Our role

When a plan sends real data to a CHO that Aurelianware hosts, Aurelianware is the plan's **business associate**, and Google Cloud is Aurelianware's **subcontractor** business associate. When a plan hosts CHO itself (Mode A), Aurelianware supplies software and normally never touches PHI, so no BAA is needed unless support staff get access to the plan's environment.

As a business associate, Aurelianware must:

- meet the whole Security Rule (45 CFR 164.302 to 164.318) for the ePHI it holds;
- use and disclose PHI only as the BAA allows (Privacy Rule, 164.502(e) and 164.504(e));
- report breaches to the plan (164.410);
- get a BAA from every subcontractor that handles PHI (164.308(b), 164.502(e)(1)(ii)).

Texas adds the Medical Records Privacy Act (Health and Safety Code chapter 181, which applies to anyone who handles PHI in Texas and requires HIPAA training) and the breach notice law (Business and Commerce Code 521.053). A Texas Medicaid plan also flows down its state Data Use Agreement. See [BAA-APPROACH.md](BAA-APPROACH.md).

## Status key

- **Built**: in code or Terraform, tested in CI. Not yet running anywhere with PHI.
- **Drafted**: written, not adopted or signed.
- **Gap**: not done.

## Administrative safeguards (164.308)

| Standard | Implementation | Status |
| --- | --- | --- |
| Security management: risk analysis, risk management | [RISK-ANALYSIS.md](../hipaa-hosted/RISK-ANALYSIS.md): 15 threats, controls, residual risk, a dated plan | Drafted. Needs the real project ids and people, and sign-off |
| Sanction policy | [POLICIES.md](../hipaa-hosted/POLICIES.md), section 2 | Drafted |
| Information system activity review | Quarterly [ACCESS-REVIEW.md](../hipaa-hosted/ACCESS-REVIEW.md); audit alerts acknowledged within one business day | Drafted. No review run yet |
| Assigned security responsibility | Security Officer and Privacy Officer | **Gap**: not named |
| Workforce security, clearance, termination | Named access only, after training; same-day removal | Drafted. No background check policy (**gap**) |
| Information access management | Least-privilege IAM; tenant roles inside CHO | Built (IAM in Terraform, roles in code); policy drafted |
| Security awareness and training | [TRAINING-RECORD.md](../hipaa-hosted/TRAINING-RECORD.md), 45 minutes, yearly | Drafted. Nobody trained yet |
| Security incident procedures | [INCIDENT-RESPONSE.md](../hipaa-hosted/INCIDENT-RESPONSE.md) | Drafted. Needs the Texas DUA clocks (24 hours, and 1 hour for some Medicaid data); see [BAA-APPROACH.md](BAA-APPROACH.md) |
| Contingency plan: backup, DR, emergency mode, testing, criticality | [CONTINGENCY-PLAN.md](../hipaa-hosted/CONTINGENCY-PLAN.md); RTO 24 hours for regulatory APIs; daily backups for 35 days | Drafted. Backup plan built. **Restore drill never run** |
| Evaluation | Yearly policy and control review | Drafted |
| Business associate contracts | Plan BAA [template](../../diligence/BAA-TEMPLATE.md); Google Cloud BAA | Template drafted. **Google BAA not yet accepted** (no hosted project) |

## Physical safeguards (164.310)

| Standard | Implementation | Status |
| --- | --- | --- |
| Facility access | Google Cloud data centers under Google's BAA. Aurelianware has no servers or office that holds PHI | Inherited once the Google BAA is accepted |
| Workstation use and security | Full-disk encryption, five-minute lock, updates, no PHI on laptops | Drafted. No device management to prove it (**gap**) |
| Device and media controls | No PHI on removable media; wipe on retirement | Drafted |

## Technical safeguards (164.312)

| Standard | Implementation | Status |
| --- | --- | --- |
| Unique user identification | Every person signs in with their own account; service accounts have no keys (organization policy) | Built |
| Emergency access | Break-glass procedure, [CONTINGENCY-PLAN.md](../hipaa-hosted/CONTINGENCY-PLAN.md) section 4 | Drafted |
| Automatic logoff | IAP session expiry; portal session | **Gap**: the portal session idle timeout is 2 hours (`src/portal/CloudHealthOffice.Portal/Program.cs`). Lower to 15 to 30 minutes before first PHI |
| Encryption and decryption at rest | Customer-managed Cloud KMS keys (HSM, 90-day rotation) on Kubernetes secrets, disks, buckets, the image registry and backups | Built |
| Audit controls | Google data access logs for every service, kept six years in `cho-audit`; CHO record access log (who read which member's record, allowed or denied) | Built. Failed sign-in alerting inside CHO is a **gap** |
| Integrity | Changes through reviewed pull requests and Terraform only; TLS on every internal store; backups with a delete lock | Built |
| Person or entity authentication | Google 2-step verification for staff; plan staff through IAP and their own identity provider; SMART on FHIR tokens for API clients | Built. 2-step with security keys for company accounts is policy, not yet enforced (**gap**) |
| Transmission security | TLS 1.2+ (Google MODERN policy) at the load balancer; TLS on Kafka (SCRAM-SHA-512), MongoDB and Redis inside the cluster; SFTP for file drops | Built. PostgreSQL (code sets, no member data) has no TLS |

## Organizational and documentation requirements (164.314, 164.316)

| Standard | Status |
| --- | --- |
| BAA terms meet 164.314(a) and 164.504(e) | Template drafted; counsel review is a **gap** |
| Policies written, available, kept six years, reviewed | Drafted; the private compliance store is not set up yet (**gap**) |

## Breach notification (164.410)

The incident procedure notifies the plan without unreasonable delay, within the BAA's deadline (15 days in our template; HIPAA's outer limit is 60). A Texas Medicaid plan's DUA is much shorter. See [BAA-APPROACH.md](BAA-APPROACH.md).

## Before the first byte of PHI

From the [hipaa-hosted README](../hipaa-hosted/README.md) and [GCP-HOSTED-HIPAA.md](../../deploy/GCP-HOSTED-HIPAA.md), in one list:

1. Security Officer and Privacy Officer named.
2. Counsel reviews the policies, the incident procedure and the BAA.
3. Policies signed by the Security Officer and CEO.
4. Every workforce member trained and signed; 2-step with security key or app on every account.
5. Google Cloud organization and hosted project created; Google BAA accepted.
6. Hosted profile applied ([HOSTED-ENVIRONMENT-APPLY.md](HOSTED-ENVIRONMENT-APPLY.md)).
7. Risk analysis completed with the real environment and signed.
8. Restore drill passed and timed.
9. Application logs sampled and confirmed free of PHI payloads.
10. Portal idle timeout lowered.
11. BAA signed with the plan, and any state flow-down signed.
