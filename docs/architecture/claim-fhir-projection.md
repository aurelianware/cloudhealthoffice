# FHIR ExplanationOfBenefit Projection (Capability 5.11)

> Projects CHO's `Claim` entity into a FHIR R4
> `ExplanationOfBenefit` (EOB) resource and exposes it through the
> public FHIR surface at
> `/fhir/r4/ExplanationOfBenefit/*`. This is the patient-access /
> CARIN BB foundational resource for adjudicated claims and the first
> claims-domain FHIR resource CHO publishes externally.

## Why

Claims pipeline cluster (capabilities 5.4 – 5.9) shipped the
adjudication state. 5.10 produces internal-format remittance (835
EDI) for payer-payer / payer-provider notification. 5.11 produces
the **external** patient-access format (FHIR EOB) that EHRs,
patient apps, and quality-measurement systems consume.

The `IExplanationOfBenefitProjector` and a partial implementation
already shipped with the v1 member-scoped portal endpoint
(`/api/v1/claims`). 5.11 ships:

1. The canonical FHIR controller in claims-service
   (`/fhir/ExplanationOfBenefit/*`).
1. A thin proxy controller in fhir-service so external consumers see
   one façade (`/fhir/r4/ExplanationOfBenefit/*`).
1. Projector enhancements that complete the CARIN BB shape:
   coinsurance + patient-responsibility totals, denial adjudication,
   line-level NCCI/MUE edit-failure adjudication, AI-examination
   advisory disposition as `supportingInfo`, and the Coverage
   reference.

## Architecture

```
external client
       │  GET /fhir/r4/ExplanationOfBenefit/{id}
       ▼
┌──────────────────┐   typed proxy     ┌─────────────────────────────┐
│ fhir-service     │ ────────────────▶ │ claims-service              │
│ Explanation      │ /fhir/Explanation │ FhirExplanationOfBenefit    │
│ OfBenefit        │ OfBenefit/{id}    │ Controller                  │
│ Controller       │                   │  ▶ ExplanationOfBenefit     │
└──────────────────┘                   │    Projector                │
                                       └─────────────────────────────┘
                                                 │
                                       IClaimRepository.GetLatestVersion
                                                 │
                                                 ▼
                                       (Cosmos / Mongo)
```

Two-hop pattern matches BP 5.8 (InsurancePlan), Provider 5.7
(Practitioner), Provider 5.8 (PractitionerRole), Provider 5.9
(Organization), and BP 5.9 (Endpoint). The proxy helper that
translates upstream 5xx → 502 OperationOutcome is
`FhirControllerBase.ProxyUpstreamServiceAsync`, extracted in BP
5.8.

## Identifier shape

`ExplanationOfBenefit.id` carries
`Claim.ClaimVersionId` — the chain-stable identifier that does
not change across adjustments (Decision 13/D-E). Hydration
ensures legacy rows (predating the version chain) alias
`ClaimVersionId == Id`, so callers can use either value
interchangeably.

`ExplanationOfBenefit.identifier[0]` is
`{ system: urn:cho:claim-number, value: claim.ClaimNumber }`
— the payer-assigned claim number. This is what the operator and
the provider see; FHIR `id` is the chain-stable resource handle.

`ExplanationOfBenefit.meta.lastUpdated` is `claim.LastUpdatedDate`
(ISO 8601). Phase 2 will use this to back the `_lastUpdated` search
parameter once the repository grows an index seam.

## Field-set classification

### In scope (5.11)

| FHIR element | Source | Notes |
|---|---|---|
| `id` | `Claim.ClaimVersionId` (fallback `Id`) | Decision D-E |
| `meta.lastUpdated` | `Claim.LastUpdatedDate` | ISO 8601 |
| `identifier[0]` | `Claim.ClaimNumber` | `urn:cho:claim-number` |
| `status` | `Claim.Status` | active / cancelled / draft |
| `type.coding` | `Claim.ClaimType` | Professional / Institutional / Dental → professional / institutional / oral |
| `use` | constant `claim` | |
| `patient.reference` | `Patient/{Claim.MemberId}` | resolves against member-service's FHIR Patient projection |
| `created` | `Claim.SubmittedDate` | |
| `insurer.display` | constant `CloudHealthOffice` | |
| `provider.identifier` | `Claim.BillingProviderNPI` | `http://hl7.org/fhir/sid/us-npi` |
| `outcome` | `Claim.Status` | queued / complete / partial / error |
| `billablePeriod` | `Claim.ServiceDateFrom` / `ServiceDateTo` | |
| `insurance[]` | `Claim.CoverageId` | `Coverage/{CoverageId}` reference; omitted when null (Decision 15) |
| `diagnosis[]` | `Claim.DiagnosisCodes` | ICD-10-CM coding |
| `item[]` | `Claim.ClaimLines` | sequence + CPT productOrService + servicedPeriod + unitPrice + net + quantity |
| `item[].adjudication[]` | `Claim.PendDetails.EditFailures` | NCCI/MUE failures keyed off `AffectedLineNumbers` (Decision 9) |
| `total[]` | `Claim.AdjudicationResult` | submitted / eligible / benefit / copay / deductible / coinsurance / patient-responsibility |
| `adjudication[]` (header) | `Claim.AdjudicationResult` | denial CARC + AdjustmentReasons + RemarkCodes |
| `payment.date` / `payment.amount` | `Claim.AdjudicationResult.PaymentDate` / `PayerPayment` | populated when payment was issued |
| `supportingInfo[]` | `Claim.AiExamination` | advisory disposition + ConfidenceScore + ModelId + PromptVersion (Decision 5) |

### Deferred (Phase 2)

- `_history` operation (per-version reads) — needs the adjustment
  chain from capability 5.12
- `_lastUpdated`, `created`, `provider`, `status`, `type`,
  `identifier`, `_include`, `_revinclude` search parameters — need a
  repository search-seam expansion
- AI-examination `Rationale` and `PolicyCitations` — need a
  redaction / review gate before patient-facing exposure (Decision 5)
- Resolved `Coverage/{id}` payload — coverage-service has no FHIR
  Coverage projection yet; the structural reference is forward-compat
  (Decision 15)
- Public unauthenticated CMS-0057-F access — Phase 1 is
  authenticated-only, mirroring BP 5.8 / Provider 5.7-5.9 posture
- `PUT` / `POST` (FHIR create / update) — claim lifecycle goes
  through `POST /api/v1/claims` (capability 5.3); EOB is read-only
  in Phase 1

## NCCI / MUE edit-failure projection

Each `NcciEditFailureSnapshot` becomes one
`item[].adjudication[]` entry per affected line. The category coding
is the engine-suggested CARC (`SuggestedCarc`) when present, or
the generic adjudication-reason fallback `237`
(Legislated/Regulatory Penalty) when the engine did not produce
one. The reason coding identifies the engine rule:

- **NCCI pair edits**: `{Column1Code}-{Column2Code}` under
  `urn:cho:ncci-edit`
- **MUE / non-pair edits**: `RuleId` under `urn:cho:ncci-edit`

When the engine supplies `SuggestedRarc` it lands as a FHIR
extension (`urn:cho:ncci-rarc`) on the same adjudication entry —
RARC remark codes are advisory context, not category-grade.

Failures with no `AffectedLineNumbers` are dropped from the
projection — they're a flag-level signal without enough context to
attach to a specific line.

## AI-examination supportingInfo

The advisory disposition lands at the **header** level
(`supportingInfo[]`), not line level (Decision 8 — claim-level
recommendation, not line-level). The entry carries:

- `category` = `info` (FHIR claim-information-category)
- `code.coding[0].code` = `RecommendedDisposition` (Approve / Deny /
  RequestInfo / EscalateToHuman)
- `code.coding[0].system` = `urn:cho:ai-examination-disposition`
- `valueString` = `Confidence: {0.00-1.00}`
- `reason.coding[0].display` = `model={ModelId} prompt={PromptVersion}`
  attribution — auditors read this; patient apps typically ignore
  reason
- `timingDateTime` = `GeneratedAt`

`Rationale` and `PolicyCitations` are deliberately **omitted from
Phase 1** (Decision 5). They're free-text LLM output up to 4000
characters; pushing unredacted model output through patient-access
endpoints is not acceptable without a redaction / review gate. A
follow-up capability adds them once that gate exists.

The disposition is **always advisory**: capability 5.9 explicitly
makes the deterministic pipeline authoritative. The
`supportingInfo` slot is FHIR's convention for "context that
informed adjudication but isn't itself the determination" — a
correct semantic match.

## Header denial / adjustment trail

When the claim has a denial code or any
`AdjustmentReasons` / `RemarkCodes`, the projector emits a header
`adjudication[]` array. CARIN BB consumers read denial reasons
from the header so they don't need to walk line-level adjudication
to reconstruct a denial:

- **Denial entry** (when `DenialReasonCode` is set): category
  coding under `https://x12.org/codes/claim-adjustment-reason-codes`
  (X12 CARC), reason coding with the `DenialReason` free-text display
- **AdjustmentReasons[]**: one entry per CARC adjustment with
  `category.text = GroupCode` (CO/PR/PI/OA), `category.coding[0]`
  = `ReasonCode`, and `amount` = adjustment dollar value
- **RemarkCodes[]**: one entry per RARC under
  `https://x12.org/codes/remittance-advice-remark-codes`

## Coverage reference (Decision 15)

`insurance[0].coverage.reference = "Coverage/{Claim.CoverageId}"`
when the claim has a `CoverageId`. Phase 1 dereferences may 404 —
coverage-service has no FHIR Coverage projection yet — but the
structural reference is forward-compatible: when coverage-service
ships its projector, the link resolves naturally.

When `Claim.CoverageId` is null the `insurance[]` array is omitted
entirely (rather than emitting an empty array or a `Coverage/null`
reference). This prevents misleading dangling references for the
narrow set of legacy claims that lack coverage linkage.

The original Decision 15 spec suggested `Coverage/{MemberId}` but
that's semantically wrong — Coverage and Patient are different
FHIR resources with different ids. This doc supersedes the spec on
that point (per Plan-First gate amendment).

## Tenant scoping

`HttpContext.Items["TenantId"]` (populated by the shared
`TenantMiddleware`) gates every read and search. Authenticated
callers see their tenant's claims only. The controller adds a
defensive `Tenant context missing → FHIR 400` guard so a
mis-deployed lenient-tenant configuration never leaks data — but
in normal deployments the middleware fills the tenant context
upstream, and the guard is a belt-and-suspenders protection.

Public CMS-0057-F unauthenticated access is **Phase 2** —
mirroring BP 5.8 and the Provider 5.7-5.9 controllers. Until that
ships, callers need a tenant context (header or JWT claim).

## Search parameters

Phase 1 supports the minimum set CMS-0057-F Patient Access requires
plus pagination:

| Parameter | Required | Semantics |
|---|---|---|
| `patient` | required (unless `_id`) | member id (`Claim.MemberId`); validated by SMART scope enforcement upstream |
| `_id` | optional | direct lookup of a chain-head version by `ClaimVersionId` |
| `_count` | optional, default 50, max 200 | page size |
| `_page` | optional, default 1 | 1-based pagination cursor |

When both `patient` and `_id` are supplied, the `_id` lookup must
also belong to the requested patient — otherwise the response is an
empty bundle (rather than 403, which would leak existence-of-resource
to a SMART-bound caller).

The fhir-service proxy adds two behaviors on top of this:

1. **SMART patient auto-injection**: when no explicit `patient`
   param is supplied but the JWT carries a `patient` claim
   (`SmartPatientId`), the proxy injects it before forwarding so
   patient apps don't need to know their own member id.
1. **Short-circuit on missing patient context**: when neither
   `patient` nor `_id` nor a SMART binding is available, the proxy
   returns FHIR 400 immediately rather than forwarding a request
   that's guaranteed to fail upstream.

## Read semantics

`GET /fhir/r4/ExplanationOfBenefit/{id}` returns the **head version
in effect at `DateTime.UtcNow`** of the chain identified by `{id}`.
This corresponds to `IClaimRepository.GetLatestVersionAsync(id,
DateTime.UtcNow)`. Per-version reads (FHIR `_history` operation) are
deferred to Phase 2 alongside the adjustment workflow (capability
5.12) — without an adjustment chain, history is a single row.

Pre-adjudication claims are returned with `status=draft` and
`outcome=queued` (Decision 13 — option **c**, project all states
with explicit FHIR status semantics). Patient apps that want to
hide in-progress claims can filter on `status != draft` client-side.

## Cross-references

- `claim-versioning.md` — version chain semantics that drive
  `ClaimVersionId` resolution
- `claim-adjudication-pipeline.md` — pipeline stages whose output
  drives the projector (5.5 / 5.7 / 5.9 are the most directly
  relevant)
- `fhir-insuranceplan-projection.md` — BP 5.8 reference that this
  capability mirrors
- `claim-ai-examination.md` — capability 5.9; source of the
  `supportingInfo` advisory entry
- `claim-ncci-pipeline.md` — capability 5.7; source of the
  `item[].adjudication[]` edit-failure entries

## After this PR

- 6th instance of FHIR projector pattern (Practitioner,
  PractitionerRole, Organization, InsurancePlan, Endpoint,
  ExplanationOfBenefit)
- 4th instance of fhir-service proxy controller pattern
  (BenefitPlanService InsurancePlan + Endpoint, ProviderService
  Practitioner / PractitionerRole / Organization, ClaimsService
  ExplanationOfBenefit)
- First claims-domain FHIR resource published externally
- Patient-access foundation (CMS-0057-F Phase 1 scope)
- Capability 5.10 (835 EDI) can ship with internal-format-aware scope
  since FHIR EOB is now the external-format channel
