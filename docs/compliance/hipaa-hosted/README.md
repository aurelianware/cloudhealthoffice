# HIPAA administrative safeguards: Aurelianware-hosted environment

**Status:** DRAFT, not adopted. Not legal advice. Have counsel review it before adoption.
**Scope:** the hosted HIPAA environment Aurelianware runs on Google Cloud for plans that send real member data (Mode B of the [CPCDS trial kit](../../integration/CPCDS-TRIAL-KIT.md)). Its technical build is in [GCP-HOSTED-HIPAA.md](../../deploy/GCP-HOSTED-HIPAA.md).
**Out of scope:** the synthetic demo (no PHI), and plans that host CHO themselves (Mode A), where the plan's own program applies.

These documents are workstream 8 of the hosted build plan. The HIPAA Security Rule needs them as much as it needs the encryption and audit logs. A plan's security review asks for them, and **no real member data enters the environment until they are adopted.**

| Document | HIPAA Security Rule | What it is |
| --- | --- | --- |
| [RISK-ANALYSIS.md](RISK-ANALYSIS.md) | 164.308(a)(1)(ii)(A) and (B) | Assets, threats, current controls, residual risk, and the risk management plan |
| [POLICIES.md](POLICIES.md) | 164.308(a)(2) to (a)(5), (a)(8), (b); 164.310; 164.312; 164.316 | Security officer, workforce, access, audit review, devices, encryption, subcontractors, documentation |
| [INCIDENT-RESPONSE.md](INCIDENT-RESPONSE.md) | 164.308(a)(6); 164.410 | Security incident procedure and breach notification to the plan |
| [CONTINGENCY-PLAN.md](CONTINGENCY-PLAN.md) | 164.308(a)(7) | Backup, disaster recovery, emergency mode, restore testing, criticality |
| [TRAINING-RECORD.md](TRAINING-RECORD.md) | 164.308(a)(5) | Security awareness training content and the signed record |
| [ACCESS-REVIEW.md](ACCESS-REVIEW.md) | 164.308(a)(3), (a)(4), (a)(1)(ii)(D) | Quarterly access and activity review checklist and record |

The plan-facing BAA is [docs/diligence/BAA-TEMPLATE.md](../../diligence/BAA-TEMPLATE.md). It names Google Cloud as the subcontractor for the hosted environment.

## Adopting them

1. The CEO names the Security Officer and Privacy Officer (one person may hold both in a company this size). Fill in the names in [POLICIES.md](POLICIES.md).
2. Counsel reviews the policies, the incident procedure and the BAA template.
3. The Security Officer completes the risk analysis with the real environment (project ids, people, vendors). Each open risk gets an owner and a date.
4. The Security Officer and the CEO sign and date each document. Adopted copies go to the compliance record (below). This repository keeps the template.
5. Every workforce member with access completes the training and signs the record before they get access.
6. The restore drill in the contingency plan passes, with its timing recorded.
7. Then the hosted environment may receive PHI under a signed BAA with the plan.

## Where the adopted records live

Adopted, signed copies and every completed record (risk analysis, access reviews, training records, incident log, restore drill results) live in a private compliance store. Never put them in this public repository. Use Google Drive with access limited to the Security Officer and the CEO, or similar. Keep each one six years from when it was created or last in effect, whichever is later (164.316(b)(2)).

## Review calendar

| When | What |
| --- | --- |
| Before first PHI | Everything above |
| Quarterly | [ACCESS-REVIEW.md](ACCESS-REVIEW.md); audit log and alert review |
| Every six months | Restore drill ([CONTINGENCY-PLAN.md](CONTINGENCY-PLAN.md)) |
| Yearly, and after any major change | Risk analysis update; policy review; refresher training; incident response tabletop |
| Major change means | A new plan, a new subcontractor, a new data type (for example clinical records), a new region, or a security incident |

HHS proposed changes to the Security Rule in January 2025. Among them: an asset inventory and network map, restore within 72 hours, multi-factor authentication, and a yearly compliance audit. These documents already meet most of those proposals where the cost is small, so a final rule needs fewer changes.
