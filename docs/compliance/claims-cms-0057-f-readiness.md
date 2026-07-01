# CMS-0057-F Readiness — Claims Domain

**Scope:** Cloud Health Office (CHO) Claims-domain compliance
posture relative to the CMS Interoperability and Prior
Authorization Final Rule (CMS-0057-F). Subsequent uses of "CHO" in
this document refer to Cloud Health Office.
**Status as of:** May 2026 (Claims Phase 1 close)
**Compliance deadline:** **January 1, 2027** (Patient Access API
expansion mandate; provider-directory and prior-auth components
have separate but adjacent timelines).

For the broader Claims Phase 1 architectural posture, see
[`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md).
For the Phase 2 backlog covering items required for full compliance,
see [`docs/roadmap/claims-phase-2-backlog.md`](../roadmap/claims-phase-2-backlog.md).

This document is **scoped to the Claims domain.** The cross-service
master CMS-0057-F readiness document is
[`CMS-0057-F-READINESS-MATRIX.md`](CMS-0057-F-READINESS-MATRIX.md).
Provider-directory and benefit-plan readiness contributions are
referenced here only to the extent they intersect with the claims
posture.

> **Important caveat.** Compliance readiness statements in this
> document describe Cloud Health Office's **technical capability
> posture**, not legal or regulatory certification. Final compliance
> determination rests with the deploying payer's legal /
> compliance counsel and CMS attestation processes. Where this
> document is uncertain, it states "Phase 2 required" rather than
> claiming compliance.

---

## Executive summary

| Domain | Phase 1 shipped | Phase 2 required | Compliance posture |
|--------|----------------|-------------------|---------------------|
| **FHIR ExplanationOfBenefit read** (authenticated) | ✅ | — | Tenant-authenticated read complete; structurally compliant for member-context EOB access. |
| **Patient Access API — unauthenticated access** | ❌ | ✅ Required (Section 3) | **Phase 2 required for compliance.** |
| **FHIR `_history` operation** | ❌ | ✅ Required (Section 4) | Phase 2 required for full per-version history. |
| **FHIR search-parameter completeness** | ⚠️ Minimal set | ✅ Required (Section 4) | Phase 2 required for full Patient Access API parity. |
| **Claims Provider Directory contributions** | n/a (out of claims domain) | n/a | Provider-service Phase 1 ships Practitioner, PractitionerRole, Organization. Sufficient for provider-directory mandate. |
| **Plan documents (Endpoint resource)** | n/a (out of claims domain) | n/a | BenefitPlan-service Phase 1 ships Endpoint. Sufficient for plan-document mandate. |
| **Formulary** | n/a (out of CHO scope) | n/a | Out of claims/CHO scope (typically PBM territory). |
| **Prior Authorization API** | n/a (out of claims domain) | n/a | Tracked in `prior-auth-service` posture; not enumerated here. |

**Net Claims-domain posture for CMS-0057-F:** Phase 1 establishes the
**authenticated** EOB read foundation. Phase 2 work named in
[Section 3 of the Phase 2 backlog](../roadmap/claims-phase-2-backlog.md#3-cms-0057-f-public-access)
is required to satisfy the **unauthenticated patient-access** mandate
by January 2027.

---

## What Phase 1 ships toward CMS-0057-F readiness

### FHIR ExplanationOfBenefit projection (capability 5.11)

Per [`docs/architecture/claim-fhir-projection.md`](../architecture/claim-fhir-projection.md):

- **Read endpoint:** `GET /fhir/ExplanationOfBenefit/{id}` and
  `GET /fhir/ExplanationOfBenefit` (search) shipped.
- **Authentication:** Tenant-scoped via `TenantMiddleware`. Requires
  caller to present a tenant context (header or JWT claim).
- **Profile alignment:** FHIR R4. Mapped from internal `Claim`
  aggregate via `ExplanationOfBenefitProjector` (sixth FHIR
  projector pattern instance in CHO).
- **Adjudication detail:** CARC/RARC adjudication entries from
  capability 5.7 (NCCI / MUE) flow through. Header-level
  adjudication includes denial CARC + AdjustmentReasons +
  RemarkCodes when claim is denied; payment-date / payer-payment
  populated when paid.
- **AI examination supportingInfo:** Advisory disposition,
  ConfidenceScore, ModelId, PromptVersion. Per Decision 5,
  Rationale and PolicyCitations are gated behind a Phase 2
  redaction/review pipeline.
- **Coverage reference:** Structural reference to `Coverage/{id}`.
  Forward-compat — coverage-service has no FHIR Coverage projection
  yet; the resolved payload is Phase 2.

### Tenant isolation

`TenantMiddleware` extracts tenant identity from the inbound request
(header or JWT claim) and applies it to all repository queries. Phase
1 EOB reads inherit this isolation — a member can only see EOBs for
their own tenant.

### Read-only Phase 1 surface

`PUT` / `POST` on the FHIR EOB controller are intentionally not
present. Claim mutation goes through `POST /api/v1/claims` (5.3) and
`POST /api/v1/claims/{id}/adjustments` (5.12a). EOB is a projection,
not an authoritative resource — consistent with FHIR resource
semantics for adjudication records.

---

## What Phase 2 requires for compliance

### 1. Unauthenticated patient access (PRIMARY GAP)

**Mandate:** CMS-0057-F requires unauthenticated patient access to
the Patient Access API by **January 1, 2027**. Members must be able
to access their own data without payer-managed credentials —
typically via SMART-on-FHIR or equivalent OAuth2 grant flow.

**Phase 1 posture:** Authenticated-only. Mirrors BP 5.8 / Provider
5.7-5.9 posture across CHO services. Callers need a tenant context.

**Phase 2 work (item 3.1 in backlog):**

- SMART-on-FHIR or equivalent patient-authentication surface
- Consent / OAuth2 grant flow
- Rate limiting (industry-standard: ~30 requests/min per patient)
- Audit logging for unauthenticated reads
- De-tenantization (or per-patient tenant resolution) at the
  controller boundary
- Cross-service coordination — Provider, BenefitPlan, Coverage,
  Member also require unauthenticated surfaces for full Patient
  Access API parity (section 2 below)

**Risk if not delivered:** Non-compliance with January 2027 mandate.
Penalty structure varies by deployment context.

### 2. Patient Access API surface beyond EOB

**Mandate:** Patient Access API per CMS-0057-F covers a broader set
than EOB:

- Patient demographics (`Patient`)
- Coverage (`Coverage`)
- Pharmacy / formulary (out of CHO scope; PBM territory)
- Prior authorization (covered by `prior-auth-service`)
- Provider directory (covered by Provider Phase 1)
- Encounter / clinical (typically out of payer-platform scope)

**Claims domain contribution:** EOB only. Other resources are
sourced from coverage-service, member-service, provider-service,
benefit-plan-service. Their Phase 2 readiness is tracked separately.

**Cross-service status (informational):**

| Resource | Source service | Phase 1 status |
|----------|---------------|-----------------|
| `Patient` | member-service | Phase 1 in flight (out of claims scope) |
| `Coverage` | coverage-service | No FHIR Coverage projection yet (Phase 2 cross-service) |
| `Practitioner` | provider-service | Phase 1 shipped |
| `PractitionerRole` | provider-service | Phase 1 shipped |
| `Organization` | provider-service | Phase 1 shipped |
| `InsurancePlan` | benefit-plan-service | Phase 1 shipped |
| `Endpoint` | benefit-plan-service | Phase 1 shipped |
| `ExplanationOfBenefit` | claims-service | Phase 1 shipped (this document) |

### 3. FHIR `_history` operation

**Mandate:** CMS-0057-F doesn't strictly require `_history`, but
patient-access best practice and HL7 conformance expectations
include it. Claim history (original adjudication → adjustment →
re-adjudicated successor) is meaningful given Phase 1's adjustment
workflow (5.12a).

**Phase 1 posture:** EOB read returns the current claim version
only. Without `_history`, the adjustment chain is invisible to FHIR
consumers.

**Phase 2 work (item 2.1 in backlog):** `ExplanationOfBenefitProjector`
extension; repository `GetVersionHistoryAsync(originalClaimId)`;
`GET /fhir/ExplanationOfBenefit/{id}/_history` route.

### 4. FHIR search-parameter completeness

**Mandate:** Patient Access API search-parameter support per FHIR R4
core.

**Phase 1 posture:** Minimal search-parameter set (member context
implicit; status / disposition).

**Phase 2 work (item 2.2 in backlog):** Add `_lastUpdated`, `created`,
`provider`, `status`, `type`, `identifier`, `_include`,
`_revinclude`. Each requires a repository search-seam expansion.

### 5. AI examination Rationale + PolicyCitations exposure

**Not strictly a CMS-0057-F mandate**, but patient-facing surfaces
require careful PHI / adversarial-content handling for AI-generated
content.

**Phase 1 posture:** Surfaces only structural advisory fields
(disposition, ConfidenceScore, ModelId, PromptVersion).

**Phase 2 work (item 2.3 in backlog):** Redaction service +
review-gate workflow. Late Phase 2 — depends on customer-facing
review process maturing.

---

## Compliance-deadline posture

The Patient Access API expansion mandate has a **January 1, 2027**
effective date. As of May 2026 (Claims Phase 1 close), the timeline
is approximately:

- **May 2026 — May 2026** (now): Phase 1 close. Authenticated EOB
  read complete. Other Patient Access API resources at varying
  Phase 1 / Phase 2 maturity per source service.
- **June 2026 — December 2026** (estimated Phase 2 window): Items
  3.1, 3.2, 2.1, 2.2 in
  [Phase 2 backlog](../roadmap/claims-phase-2-backlog.md). Cross-
  service coordination required (coverage-service, member-service
  Phase 2 dependencies).
- **January 2027**: Mandate effective.

**Sequencing implications:**

- Item 3.1 (unauthenticated access) is the **primary Phase 2 driver**
  for compliance. It is large in scope and gates several other
  items.
- Items 2.1 (`_history`), 2.2 (search params) can run in parallel
  with 3.1 once the projection seams exist.
- Cross-service items (Coverage projection, Member projection)
  require coordination with respective service teams; delays in
  those services impact end-to-end Patient Access API readiness
  but not the claims-specific EOB contribution.

---

## What this document does **not** assert

- **Does not certify CHO as CMS-0057-F compliant.** Final compliance
  determination is a legal / regulatory process; this document
  describes technical capability posture only.
- **Does not assert all Phase 2 work will land before January 2027.**
  It states that Phase 2 work is required and tracks the deadline.
  Sequencing decisions during Phase 2 will reaffirm or adjust the
  timeline.
- **Does not cover non-Claims-domain resources.** Provider directory,
  formulary, prior-auth, member demographics, coverage are sourced
  from other CHO services or are out of CHO scope. A future cross-
  service CMS-0057-F readiness document will consolidate.
- **Does not constitute legal advice.** Deploying payers should
  engage their own compliance counsel for attestation processes,
  penalty exposure, and rule-interpretation questions.

---

## References

- [`docs/architecture/claim-fhir-projection.md`](../architecture/claim-fhir-projection.md)
  — Phase 1 EOB projection architecture
- [`docs/architecture/claims-phase-1-closer.md`](../architecture/claims-phase-1-closer.md)
  — Claims Phase 1 architectural posture
- [`docs/roadmap/claims-phase-2-backlog.md`](../roadmap/claims-phase-2-backlog.md)
  — Phase 2 work registry (Section 3 covers CMS-0057-F)
- CMS-0057-F final rule: 89 FR 8758 (February 2024)
