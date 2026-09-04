# Sample-Filled CMS-0057-F Diligence Checklist

**This is a worked example for the synthetic demo tenant. It is not a live customer.**
Blank original: [CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md](../compliance/CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md)

## Intake summary

| Field | Value |
| --- | --- |
| Payer / organization | Example Regional MCO (synthetic) |
| Lines of business in scope | Medicaid MCO (one LOB) |
| Pilot population | Synthetic members in `demo-tenant` |
| Target go-live / demo date | Week 4 labeled demo; week 8 go/no-go |
| Payer executive sponsor | (to be named on Order Form) |
| Payer compliance owner | (to be named on Order Form) |
| Payer security owner | (to be named on Order Form) |
| Cloud Health Office delivery owner | (to be named on Order Form) |
| Data classification | **Synthetic** |
| Adapter mode | **Demo / Hybrid** (see `/fhir/r4/adapter-status`) |

## Regulatory scope

| Check | Status | Note |
| --- | --- | --- |
| Confirm impacted payer for selected LOBs | Not started | Customer counsel |
| Exact compliance dates by payer type | Not started | CMS / Federal Register mapping |
| Drugs in or out of PA scope | Not started | |
| State rules shorter than federal timeframes | Not started | TX / FL often in play |
| Attestation owner | Not started | CHO does not attest |

## API and workflow readiness (demo tenant)

| Area | Pilot evidence | Status |
| --- | --- | --- |
| Patient Access API | Synthetic Patient/Coverage; EOB via claims-service proxy | Demo / Hybrid |
| Provider Directory | Provider-service proxy | Hybrid |
| Payer-to-Payer API | Out of scope for founding-partner accelerator | Out of scope |
| Prior Authorization API | PAS `$submit` on synthetic fixtures | Demo |
| Prior-auth decision timelines | SLA watchdog exists; staffing is customer | Integration required |
| Public prior-auth metrics | Template only | Phase 2 product; template in week 6 |
| SMART on FHIR | Scope middleware implemented; issuer onboard is customer | Integration required |
| Adapter labels | `/fhir/r4/adapter-status` + response headers | Implemented |

## Security, privacy, operations

| Check | Status |
| --- | --- |
| BAA and contract path | Template in this binder; not signed on the demo tenant |
| Tenant isolation model | Reviewed in security one-pager |
| PHI handling | Synthetic-only for this sample |
| Authentication model | SMART issuer to be registered week 2 of a real pilot |
| Audit logging / retention | Plan in week 2; not a completed customer DR test |
| Incident contacts | [INCIDENT-AND-SUPPORT.md](INCIDENT-AND-SUPPORT.md) |
| Pen-test expectations | Not on file; RAID item |

## Demo evidence (synthetic)

| Demonstration | Required label | Status |
| --- | --- | --- |
| Patient Access flow | Synthetic/demo | Ready in demo script |
| Provider Directory flow | Synthetic/demo or roster-backed | Hybrid proxy |
| PAS `$submit` approval / denial / pend | Synthetic/demo | Ready in demo script |
| Bulk Export job | Synthetic/demo scaffold | Demo |
| Adapter-status inventory | n/a — this is the label source | Ready |

## Go / no-go (example, not a real decision)

- **Go for synthetic demo:** yes.
- **Go for PHI:** no, until BAA + named environment + adapter-status update.
- **Go for CMS attestation:** never CHO’s to grant.
