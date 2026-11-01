# Security policies: hosted environment

**Status:** DRAFT, not adopted. In effect only once the Security Officer and the CEO sign below.
**Applies to:** every Aurelianware employee, contractor and system with access to the hosted HIPAA environment (the "workforce").

## 1. Assigned responsibility (164.308(a)(2))

| Role | Name | Duties |
| --- | --- | --- |
| Security Officer | ________ | Owns these policies, the risk analysis, access reviews, incident response and the contingency plan |
| Privacy Officer | ________ | Owns PHI use and disclosure under the plan BAAs, requests from plans, breach risk assessments |
| Backup for each | ________ | Acts when the officer is unavailable for more than one business day |

One person may hold both roles.

## 2. Workforce security and sanctions (164.308(a)(1)(ii)(C), (a)(3))

- Access to the hosted environment is granted only to named people who need it for their job, after they complete training ([TRAINING-RECORD.md](TRAINING-RECORD.md)) and sign this policy.
- The Security Officer approves each grant in writing (an email or ticket is enough) and records it in the next access review.
- When someone leaves or changes role, their access is removed the same business day. The Security Officer checks the removal against the IAM policy and the tenant user list.
- **Sanctions.** A violation leads, according to its severity and intent, to retraining, written warning, removal of access, or termination. Every sanction is documented and kept six years. Intentional misuse of PHI may also be reported to authorities.

## 3. Access control (164.308(a)(4), 164.312(a), (d))

- **Unique identities.** Every person signs in with their own Google account. No shared accounts. Service accounts are used only by workloads, never by people, and have no keys (an organization policy blocks keys).
- **Authentication.** Aurelianware accounts use Google 2-step verification with a security key or an authenticator app, not SMS. Plan staff reach the portal through Google Cloud IAP with their own organization's sign-in and MFA policy.
- **Least privilege.**
  - Project roles are granted to named people only.
  - Owner is held by at most two people.
  - Day-to-day work uses narrower roles.
  - Cluster access goes through the DNS endpoint with IAM.
  - Inside CHO, tenant roles (TenantAdmin, ClaimsSupervisor, MemberServices and so on) limit what each user sees.
- **Minimum necessary.** Support staff look at member records only to resolve a ticket the plan raised, and note the ticket in their work.
- **Emergency access.** If IAP or Google sign-in is down, the Security Officer may use the break-glass procedure in [CONTINGENCY-PLAN.md](CONTINGENCY-PLAN.md), section 4. Each use is logged and reviewed.
- **Automatic logoff.** IAP sessions expire, and the portal signs out after inactivity. Workstations lock after five minutes (section 6).

## 4. Audit controls and activity review (164.308(a)(1)(ii)(D), 164.312(b))

- Data access audit logs are on for every Google Cloud service and copied to the `cho-audit` log bucket, kept six years.
- CHO writes application audit events for reads and changes of member data.
- Alerts go to the security email list for IAM policy changes, KMS key changes, logging changes, organization policy changes and failed backups. The Security Officer acknowledges each alert within one business day.
- Each quarter, the Security Officer runs the activity review in [ACCESS-REVIEW.md](ACCESS-REVIEW.md).

## 5. Integrity and transmission security (164.312(c), (e))

- Traffic from outside reaches the environment only through the Google load balancer, with TLS 1.2 or later (Google's MODERN policy).
- Inside the cluster, Kafka, MongoDB and Redis require TLS. Google also encrypts traffic between its machines.
- Every store that holds ePHI is encrypted at rest with customer-managed Cloud KMS keys, rotated every 90 days.
- Changes to the environment are made only through Terraform and the approved deploy workflow, with the code reviewed in a pull request.

## 6. Devices and workstations (164.310(b), (c), (d))

- Google Cloud provides the facility safeguards of its data centers under its BAA. Aurelianware has no servers of its own.
- Laptops used to administer the environment must have full-disk encryption, a screen lock at five minutes or less, automatic OS updates, and a supported OS.
- **No ePHI on laptops, phones, removable media, personal email or chat.** Work on member data happens in the portal or the cluster, not in downloaded files. If a plan sends a file by another route, delete it after moving it into the drop, and tell the Security Officer.
- A lost or stolen device is reported to the Security Officer within one hour of discovery and handled under [INCIDENT-RESPONSE.md](INCIDENT-RESPONSE.md).
- When a device is retired, its disk is wiped or destroyed.

## 7. Subcontractors (164.308(b), 164.502(e))

Only these subcontractors may create, receive, maintain or transmit ePHI for the hosted environment:

| Subcontractor | Purpose | Agreement |
| --- | --- | --- |
| Google Cloud | Hosting, storage, keys, logging, load balancing, IAP | Google Cloud BAA accepted in the hosted project |
| Anthropic (optional) | AI claims examiner, advisory only | Only after a BAA with Anthropic is signed; the deploy refuses the examiner without `ANTHROPIC_BAA_CONFIRMED=true` |

GitHub, Cloudflare, email and chat providers, and analytics tools must never receive ePHI. Adding a subcontractor needs a signed BAA first, an update to the risk analysis, and notice to each plan as its BAA requires.

## 8. Evaluation (164.308(a)(8))

The Security Officer reviews these policies and the technical controls once a year and after any major change (see the [README](README.md) calendar). The review checks that each control above still exists and works. Record it with the date and the findings.

## 9. Documentation (164.316)

- These policies, the risk analysis and every record they require are kept in the private compliance store for six years from creation or from when they were last in effect, whichever is later.
- They are available to the workforce members who carry them out.
- The Security Officer updates them when the environment, the law or the risk changes.

## Acknowledgement and adoption

Adopted by:

Security Officer: ________________________ Date: ________

CEO: ________________________ Date: ________

Each workforce member signs the acknowledgement in [TRAINING-RECORD.md](TRAINING-RECORD.md).
