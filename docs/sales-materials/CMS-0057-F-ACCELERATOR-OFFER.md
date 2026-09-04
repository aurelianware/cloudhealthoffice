# CMS-0057-F Compliance Accelerator

**The SKU we sell first.** One page. Founding-partner terms.

**Status as of:** September 2026
**Legal entity:** Aurelianware, Inc.
**Canonical readiness:** [CMS-0057-F-READINESS-MATRIX.md](../compliance/CMS-0057-F-READINESS-MATRIX.md)
**Canonical positioning:** [POSITIONING.md](../POSITIONING.md) Layer 1
**Diligence binder:** [docs/diligence/README.md](../diligence/README.md)

This is a commercial offer, not a CMS certification, legal opinion, or payer attestation.

---

## The job

Impacted payers must expose Patient Access, Provider Directory / Provider Access, Prior Authorization (Da Vinci CRD → DTR → PAS), and related SMART-on-FHIR controls on a CMS clock that generally begins **January 1, 2027**. Most already run QNXT, Facets, HealthEdge, or a home-grown core. They do not have 12–18 months for a core replacement.

Cloud Health Office deploys **beside** that core. We do not touch how claims are adjudicated today.

---

## What you buy

A fixed-scope **6–8 week** implementation that stands up Cloud Health Office as the FHIR, SMART/OAuth, prior-authorization, audit, and evidence layer for **one tenant, one line of business**.

| In scope | Out of scope |
| --- | --- |
| Patient Access API surface (Patient, Coverage, EOB mapping evidence) | Replacing QNXT / Facets / HealthEdge |
| Provider Directory FHIR surface | Claims adjudication cutover |
| SMART-on-FHIR scope enforcement | Payer-to-payer exchange as a turnkey deliverable |
| PAS `$submit`, CRD, DTR as configured | Correspondence / denial letters |
| Bulk FHIR and consent **building blocks** | CMS certification or legal attestation |
| Adapter-mode labels (Demo / Hybrid / Live) on every FHIR response | Production PHI until a BAA is signed |
| Diligence packet, synthetic demo tenant, runbook outline | Public prior-auth metrics as a finished CMS filing |
| Appeals as a **written expansion option**, not week-1 work | Multi-state, multi-core, multi-clearinghouse first cut |

Every demo and evidence artifact is labeled **Demo data**, **Hybrid**, **Live payer-backed**, or **Out of scope**. See [`GET /fhir/r4/adapter-status`](../../src/services/fhir-service/Controllers/AdapterStatusController.cs).

---

## Founding-partner price

Indicative founding-partner terms for the **first three** Layer 1 payers. Executed numbers live on the [order form](contracts/order-form-template.md). This is not list pricing for later customers.

| Item | Founding-partner term |
| --- | --- |
| **Accelerator package** | **$90,000** fixed, 6–8 weeks, one LOB, one tenant |
| **Included runtime** | 90 days of Layer 1 environment after kickoff |
| **Thereafter** | Layer 1 PMPM on the order form, founding-partner discount vs later list |
| **Optional live-adapter add-on** | **+$60,000** to wire Patient, Coverage, and EOB to named payer source systems inside the same window |
| **Optional second LOB** | **+$45,000** if scoped at kickoff |
| **What we ask in return** | Case study (named or anonymized) and two reference calls in year 1 |

Not free. A $0 “complete platform” install is not this offer.

Payment: 50% on order-form signature, 50% at week-6 evidence packet. BAA required before any PHI.

---

## Six-to-eight week shape

| Week | Focus | You leave with |
| --- | --- | --- |
| 1 | Intake, LOB, success criteria, diligence checklist | Signed scope, data-class decision (synthetic until BAA) |
| 2 | Environment, identity model, logging / audit plan | Tenant in your Kubernetes / AKS (or CHO-hosted sandbox) |
| 3 | Source-system map | Patient, coverage, claims/EOB, provider, prior-auth, consent owners |
| 4 | Labeled synthetic demo | Patient Access, Provider Directory, PAS `$submit` approve / pend / deny, Bulk Export job |
| 5 | Live-adapter path for selected resources | Adapter-status report showing Demo vs Hybrid vs Live per resource |
| 6 | Prior-auth metrics template + operations | Draft metrics, SLA queue plan, runbook outline |
| 7–8 | Validation and go / no-go | Evidence packet, open gaps, production backlog, commercial amendment for Layer 2 (appeals) |

Success criteria are in the [accelerator brief](../compliance/CMS-0057-F-COMPLIANCE-ACCELERATOR-BRIEF.md). Go / no-go is a payer decision. We do not attest for you.

---

## Who this is for

Regional Medicaid MCO, Medicare Advantage plan, CHIP, or QHP issuer, roughly **50,000–300,000 members**, already on Azure or Kubernetes (or willing to be), with a CMS-0057-F program that is late. Texas and Florida plans are a sharper fit (TMPPM / AHCA configuration already in the repo).

Not for: a national Blues procurement as the first logo; a greenfield “replace Facets this quarter” program; a science project with no compliance owner.

---

## Proof you can inspect before you buy

- Source-available under BSL 1.1. Production use requires a commercial license.
- [Readiness matrix](../compliance/CMS-0057-F-READINESS-MATRIX.md) — implemented vs integration-required vs Phase 2.
- [Million Claim Challenge](../million-claim-challenge/) — local Kubernetes adjudication evidence. Local validation, not a production-cloud capacity claim. Layer 1 does not depend on it.
- Appeals four-PR sequence (#677 / #678 / #680 / #681) — the Layer 2 expansion path, sold as an amendment.

No production reference customer yet. That is why founding-partner terms exist.

---

## Next step

1. 20-minute labeled synthetic demo ([script](demo-materials/cms-0057-f-accelerator-demo.md)).
2. Diligence binder review with your CISO / privacy counsel ([docs/diligence](../diligence/README.md)).
3. Order form + BAA. Kickoff week 1.

**Sales:** sales@cloudhealthoffice.com
**Licensing:** licensing@cloudhealthoffice.com
