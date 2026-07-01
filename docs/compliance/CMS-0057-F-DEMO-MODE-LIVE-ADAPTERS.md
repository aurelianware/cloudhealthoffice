# CMS-0057-F Demo Mode vs Live Adapters

**Status as of:** July 2026
**Purpose:** keep CMS-0057-F buyer demos and pilot evidence honest by labeling
which flows use synthetic/demo data and which flows are backed by live payer
systems.

This document is an evidence-labeling guide. It does not certify production
readiness.

## Labeling Rules

Every demo artifact, screenshot, request/response sample, and buyer-facing
claim should carry one of these labels:

| Label | Meaning | Buyer-safe wording |
| --- | --- | --- |
| Demo data | Uses seeded or synthetic data in Cloud Health Office. | "Demonstrates technical behavior with synthetic data." |
| Hybrid | Uses live configuration with one or more mocked or seeded services. | "Pilot wiring in progress; source labels shown per resource." |
| Live payer-backed | Uses payer-approved source systems and tenant configuration. | "Backed by payer source-system integration for this pilot scope." |
| Out of scope | Not demonstrated in the pilot. | "Not in current pilot scope." |

## Current Adapter Evidence

| Capability | Current repo evidence | Demo-mode risk | Live-adapter requirement |
| --- | --- | --- | --- |
| Patient, Claim, core FHIR reads | `fhir-service` registers `MockFhirDataAdapter` and `MockPatientAccessDataProvider` for generic FHIR data surfaces. | A demo can look broader than the live integration actually is. | Replace or wrap with payer-backed adapters for pilot resources before calling the flow live. |
| Appeals FHIR resources | `Appeals:UseMockAdapter` selects mock appeal data by config, defaulting to development mode when no appeals service is present. | Appeal demos can be seeded unless explicitly configured for HTTP adapter. | Set `Appeals:UseMockAdapter=false`, configure `Services:AppealsServiceUrl`, and capture request evidence. |
| ExplanationOfBenefit | `ExplanationOfBenefitController` proxies to claims-service `/fhir/ExplanationOfBenefit`; claims-service owns the canonical EOB projection. | EOB evidence depends on whether claims-service is seeded, synthetic, or live. | Label claims-service data source and show tenant/header propagation evidence. |
| Provider Directory | FHIR service exposes provider-directory proxies and provider-service projections. | NPPES or test roster data may be mistaken for payer network data. | Identify source roster, update cadence, network affiliation, and endpoint publication process. |
| PAS `$submit` | PAS controller validates FHIR, performs provider verification checks, calls auto-adjudication, persists decisions, and records metrics. | Auto-decisions can rely on no-op or demo rule behavior if payer rules are not loaded. | Load payer-approved UM rules, denial taxonomy, attachment workflow, and manual review queue. |
| CRD/DTR | CRD and DTR services/controllers are registered in `fhir-service`. | Cards/questionnaires can represent generic scenarios rather than payer policy. | Load payer-specific rules, questionnaires, prepopulation sources, and provider/EHR launch context. |
| Bulk FHIR export | Bulk Export controller/service exists and has test coverage. | A completed job can be a technical scaffold rather than a production export. | Define storage, encryption, manifest retention, recipient access, and audit controls. |
| Consent service | Consent lifecycle and repository patterns exist. | Demo consent may not match payer opt-in/opt-out policy. | Configure payer-approved consent text, revocation workflow, audit retention, and line-of-business rules. |
| `/fhir/r4/compliance-status` | Compliance controller returns tenant-level CMS-0057-F posture based on configuration checks. | A positive config report can overstate production attestation if source-system integration is incomplete. | Pair endpoint output with this adapter table and the readiness matrix. |

## Demo Script Requirements

Before any buyer or pilot demo:

1. Name the tenant and data class: synthetic, de-identified, limited PHI, or
   production PHI.
2. State whether each shown resource is demo, hybrid, live payer-backed, or out
   of scope.
3. Keep screenshots and exported samples in a folder named with the adapter
   mode and date.
4. For live payer-backed flows, capture the source system, request id,
   correlation id, tenant id, timestamp, and reviewer.
5. For demo/hybrid flows, state the production dependency before discussing
   compliance impact.

## Evidence Folder Convention

Use this naming pattern for pilot evidence:

```text
cms-0057-pilot-evidence/
  YYYY-MM-DD-demo-mode/
  YYYY-MM-DD-hybrid/
  YYYY-MM-DD-live-payer-backed/
```

Each folder should include:

- `README.md` describing scope, tenant, data classification, and source labels.
- Request/response samples with PHI removed unless the customer-approved
  secure evidence process permits PHI.
- Screenshots or logs showing correlation id and tenant id where available.
- A short list of production dependencies not demonstrated by that evidence.

## Buyer-Safe Phrases

- "This flow demonstrates technical capability with synthetic data."
- "This flow is live for the selected pilot resource but not yet generalized to
  all payer source systems."
- "This endpoint is implementation evidence, not legal attestation."
- "Production readiness depends on payer source-system integration, operating
  procedures, and compliance review."

## Phrases to Avoid

- "Certified compliant."
- "100% compliant out of the box."
- "Production-ready compliance."
- "Live" when the data source is seeded, synthetic, or mock-backed.
