# CMS-0057-F Pilot Diligence Checklist

**Status as of:** July 2026
**Purpose:** define the intake, evidence, integration, security, and go/no-go
checks for a CMS-0057-F Compliance Accelerator pilot.

This checklist supports pilot planning. It does not replace payer legal,
compliance, security, or procurement review.

## Intake Summary

| Field | Value |
| --- | --- |
| Payer / organization | |
| Lines of business in scope | |
| Pilot population | |
| Target go-live / demo date | |
| Payer executive sponsor | |
| Payer compliance owner | |
| Payer security owner | |
| Cloud Health Office delivery owner | |
| Data classification | Synthetic / de-identified / limited PHI / production PHI |
| Adapter mode | Demo / hybrid / live |

## Regulatory Scope

| Check | Owner | Evidence | Status |
| --- | --- | --- | --- |
| Confirm whether payer is an impacted payer for selected LOBs. | Payer compliance | LOB inventory and counsel review. | Not started |
| Identify exact compliance dates by payer type and contract/rating/plan year. | Payer compliance | CMS/Federal Register mapping. | Not started |
| Confirm whether pilot includes drugs or excludes drugs from CMS-0057-F prior-auth scope. | Payer compliance | Benefit and UM policy note. | Not started |
| Confirm state law or contract requirements that are shorter than federal timeframes. | Payer compliance | State/contract matrix. | Not started |
| Define attestation owner and final approval process. | Payer compliance | Governance RACI. | Not started |

## Source-System and Data Readiness

| Check | Owner | Evidence | Status |
| --- | --- | --- | --- |
| Patient/member source identified. | Payer IT | System name, API/feed, sample schema. | Not started |
| Coverage/benefit source identified. | Payer IT | Coverage map, benefit plan versioning. | Not started |
| Claims/EOB source identified. | Payer IT | Claims-service or CAPS integration map. | Not started |
| Provider directory/network source identified. | Payer IT | Provider roster and freshness process. | Not started |
| Prior authorization source identified. | Payer UM/IT | Authorization status and decision model. | Not started |
| UM criteria and rules owner identified. | Payer UM | Rule source, approval workflow. | Not started |
| Denial reason taxonomy approved. | Payer UM/compliance | Reason-code mapping and letter alignment. | Not started |
| Historical data scope defined. | Payer compliance/IT | Date range and retained data classes. | Not started |

## API and Workflow Readiness

| Area | Pilot evidence | Production dependency | Status |
| --- | --- | --- | --- |
| Patient Access API | `/fhir/r4/compliance-status`, Patient/Coverage/EOB demo, SMART scopes. | Identity, consent, live source adapters, data breadth, search/history completeness. | Not started |
| Provider Access API | Provider Directory proxy/projection demo and SMART scope evidence. | Attributed-provider logic, patient opt-out, data minimization, provider roster operations. | Not started |
| Payer-to-Payer API | Bulk FHIR and consent lifecycle demonstration. | Opt-in workflow, historical scope, inbound/outbound exchange, export storage, audit retention. | Not started |
| Prior Authorization API | PAS `$submit`, CRD, DTR, pended/approved/denied examples. | Payer UM rules, clinical documentation governance, manual review queues, source reconciliation. | Not started |
| Prior-auth decision timelines | SLA calculation and watchdog evidence. | Operational queue staffing, escalation workflow, paused/resumed SLA policy. | Not started |
| Public prior-auth metrics | Metrics template populated with sample/synthetic data. | Production metric store, reconciliation, publication workflow, payer sign-off. | Not started |

## Security, Privacy, and Operations

| Check | Owner | Evidence | Status |
| --- | --- | --- | --- |
| BAA and contract path identified. | Legal/procurement | Draft agreement or existing vehicle. | Not started |
| Tenant isolation model reviewed. | Security | Architecture diagram and test evidence. | Not started |
| PHI handling and de-identification approach approved. | Security/privacy | Data handling memo. | Not started |
| Authentication and authorization model approved. | Security/IAM | SMART/OAuth issuer, scopes, client registration flow. | Not started |
| Audit logging and retention plan approved. | Security/compliance | Log schema, retention policy, access review. | Not started |
| Incident response and support contacts defined. | Operations | Escalation matrix. | Not started |
| Backup, DR, and availability expectations defined. | Operations | RTO/RPO and monitoring plan. | Not started |
| Vulnerability/pen-test expectations defined. | Security | Test plan or third-party review plan. | Not started |

## Demo Evidence Checklist

| Demonstration | Required label | Evidence artifact | Status |
| --- | --- | --- | --- |
| Patient Access flow | Synthetic/demo or live payer-backed. | Screenshot, request/response sample, source label. | Not started |
| Provider Directory flow | Synthetic/demo or live roster-backed. | Request/response sample and data freshness note. | Not started |
| PAS `$submit` approval | Synthetic/demo or live UM-rule-backed. | Request/response bundle and rule source label. | Not started |
| PAS `$submit` denial | Synthetic/demo or live UM-rule-backed. | Denial reason and correspondence mapping. | Not started |
| Pended/manual review | Synthetic/demo or live queue-backed. | Queue evidence and SLA status. | Not started |
| Bulk Export job | Synthetic/demo or live export-backed. | Job status, manifest, storage/security note. | Not started |
| Compliance status endpoint | Tenant config evidence. | `/fhir/r4/compliance-status` output. | Not started |

## Go/No-Go Questions

- Are all demo-mode resources labeled and separated from live payer-backed
  evidence?
- Has payer compliance reviewed the readiness matrix and accepted the wording?
- Is there a named owner for every production dependency?
- Are public prior-authorization metrics traceable to source records?
- Can the payer explain how denial reasons, extensions, and appeals are counted?
- Are security, privacy, BAA, audit logging, and retention requirements either
  approved or explicitly listed as blockers?
- Does the production backlog distinguish implementation gaps from legal or
  operational attestation work?

## References

- Readiness matrix: [CMS-0057-F-READINESS-MATRIX.md](CMS-0057-F-READINESS-MATRIX.md)
- Demo-mode guide: [CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md](CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md)
- Metrics template: [CMS-0057-F-PRIOR-AUTH-METRICS-TEMPLATE.md](CMS-0057-F-PRIOR-AUTH-METRICS-TEMPLATE.md)
