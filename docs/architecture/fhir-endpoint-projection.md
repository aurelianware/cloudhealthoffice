# FHIR Endpoint Projection (Capability BP 5.9)

> Projects CHO's `BenefitPlan.Documents[]` (already-modeled
> `PlanDocumentReference`) into Plan-Net IG 1.1.0 conformant `Endpoint`
> resources, dereferenceable at `/fhir/r4/Endpoint/{id}` and referenced
> from `InsurancePlan.endpoint[]`. Closes the BP 5.8 deferral on the
> `endpoint[]` slot and establishes the Endpoint pattern for the rest
> of the FHIR surface.

## Why

BP 5.8 shipped `InsurancePlan` with an empty `endpoint: []`. BP 5.9
fills it. Three CMS-adjacent regulatory surfaces motivate the work:

1. **ACA SBC** (45 CFR §147.200) — payers must publish a Summary of
   Benefits and Coverage; the URL must be discoverable.
1. **CMS Transparency in Coverage** (45 CFR §147.211) — payers must
   publish in-network and out-of-network machine-readable rate files
   (MRFs) at known URLs.
1. **CMS-0057-F** — formulary URL discoverability for member-facing
   apps via the Patient Access API.

## Architecture

```
external client
       │  GET /fhir/r4/Endpoint/{EndpointId}
       ▼
┌──────────────────┐                  ┌─────────────────────────────┐
│ fhir-service     │   typed proxy    │ benefit-plan-service        │
│ Endpoint         │ ───────────────▶ │ FhirEndpointController      │
│ Controller       │   /fhir/Endpoint │  ▶ FhirEndpointProjector    │
└──────────────────┘   /{id}          └─────────────────────────────┘
```

The fhir-service `EndpointController` is a thin proxy over
benefit-plan-service's `FhirEndpointController` — same `BenefitPlanService`
typed `HttpClient` registration that the BP 5.8 `InsurancePlanController`
uses, and the same `FhirControllerBase.ProxyUpstreamServiceAsync`
status-translation rule.

Inside benefit-plan-service the projector is hand-built (`JsonObject`,
no `Hl7.Fhir.R4` dep) and is consumed in two places:

- `FhirEndpointController` — read + search returns full `Endpoint`
  resources.
- `FhirInsurancePlanProjector` (BP 5.8) — calls
  `IFhirEndpointProjector.OrderedProjectableDocuments(plan)` to populate
  `InsurancePlan.endpoint[]` with `Reference(Endpoint/{id})` entries.
  Both surfaces share one ordering rule (Decision 8) so a Reference
  always resolves to the Endpoint that its order would predict.

## Source-of-truth mapping

| FHIR `Endpoint` element | Source on `PlanDocumentReference` / parent plan | Notes |
|---|---|---|
| `id` | `PlanDocumentReference.Id` (verbatim) | Decision 2 |
| `status` | derived | Decision 5 |
| `connectionType` | constant `static-document` under CHO CodeSystem | Decision 1 |
| `name` | `DisplayName` ?? per-DocType display | |
| `payloadType[].coding` | `DocType` mapped to CHO CodeSystem | Decision 3 |
| `payloadMimeType[]` | `ContentType` | Decision 6 — pass-through, no inference |
| `address` | `Location` (HTTPS only) | Decision 4 |
| `period.start` | `EffectiveDate` (when present) | |
| `meta.profile` | Plan-Net `plannet-Endpoint` | |

## Decisions

### Decision 1 — `connectionType`

The HL7 `http://terminology.hl7.org/CodeSystem/endpoint-connection-type`
CodeSystem has no code for "static downloadable document"; available
codes are FHIR-protocol-shaped (`hl7-fhir-rest`, `direct-project`,
`dicom-*`).

CHO publishes a CodeSystem
`http://fhir.cloudhealthoffice.com/CodeSystem/endpoint-connection-type`
with one code, `static-document`. Emit it as the sole coding under
`Endpoint.connectionType`. A future capability swaps in a standard
code if HL7 publishes one. Misusing `hl7-fhir-rest` for a PDF link
would be dishonest.

### Decision 2 — `Endpoint.id`

`PlanDocumentReference.Id` (verbatim) — matches the BP 5.8 stance for
`InsurancePlan.id = BenefitPlan.PlanId` (use the source-system
identifier directly). The 64-char FHIR id limit is comfortable for a
GUID. Operators don't author Endpoint IDs; they author plan IDs and
documents.

### Decision 3 — `payloadType` codings

CHO publishes a CodeSystem
`http://fhir.cloudhealthoffice.com/CodeSystem/plan-document-type`
with one code per `PlanDocumentType` enum value:

| Enum | Code | Display |
|---|---|---|
| SBC | `sbc` | Summary of Benefits and Coverage |
| EOC | `eoc` | Evidence of Coverage |
| Formulary | `formulary` | Drug Formulary |
| SPD | `spd` | Summary Plan Description |
| MachineReadableRateFile | `mrf` | Machine-Readable Rate File |
| Other | `other` | Other Plan Document |

Plan-Net IG does not bind `payloadType`; no standard FHIR CodeSystem
covers SBC / EOC / SPD / MRF.

### Decision 4 — `address`

The URL the `PlanDocumentReference.Location` field carries when it is
HTTPS. When `Location` is the reserved internal
`documentreference/{id}` form (Phase 2 forward-compat), the document
is **not** projectable to an Endpoint — Endpoints require an external
address. The projector skips such documents. The skip is a normal
projection outcome, not an error: the document still exists on the
plan, it just isn't surfaced through the Endpoint slot.

### Decision 5 — `status`

Derived from `PlanDocumentReference.EffectiveDate` and the parent
plan's lifecycle:

- `active` — `EffectiveDate` is null OR `≤ now` AND the parent plan
  version is Published and not retired.
- `off` — `EffectiveDate > now` (future-dated).
- `off` — parent plan is retired (terminated and termination is in
  the past).
- `entered-in-error` is never emitted (CHO has no soft-delete state
  for plan documents today).

### Decision 6 — `payloadMimeType`

Pass through `PlanDocumentReference.ContentType` when present. Skip
the field when null. No inference (don't guess `application/pdf` for
a `.pdf` URL — operators authored ContentType when they had it).

### Decision 7 — Hash exposure

`PlanDocumentReference.ContentHashSha256` is base64-SHA256, matching
`Attachment.hash`. Plan-Net's `Endpoint` profile has no
`Attachment`-shaped slot, so hash exposure has nowhere to land. It
waits for the Phase 2 `DocumentReference` projection in
member-document-service.

### Decision 8 — `InsurancePlan.endpoint[]` ordering

Stable: `(DocType ordinal, EffectiveDate desc, Id)` where DocType
ordinal is `SBC=1, EOC=2, Formulary=3, SPD=4, MRF=5, Other=6`. SBC is
the consumer-facing document — Plan-Net member-app consumers expect
it first.

The same ordering applies to the search-Bundle entry order, so a
collection consumer sees a deterministic, predictable shape regardless
of plan authoring history.

## Validation

`PlanDocumentValidation.ValidateLocation` is wired into
`ValidateDocuments` next to `ValidateHash`. It accepts:

- HTTPS URLs (the operator-authored external address Endpoint
  projection requires).
- The reserved `documentreference/{id}` form (Phase 2 forward-compat;
  rejects bare `documentreference/`).

Plain HTTP, relative URLs, and other schemes are rejected with a
field-name-aware message. Producer-boundary only — setter-side
validation would break Mongo hydration for any historical malformed
document, same trust posture as `ValidateHash`.

## Search surface

`GET /fhir/r4/Endpoint?…` honors a deliberately small subset of FHIR
search parameters:

- `_id` — token. Bare value or empty-system-pipe form. Other
  system|value pairs return an empty bundle (FHIR token semantics —
  unknown system, no match).
- `status` — token. `active` / `off` filter the projected status.
- `connection-type` — token. Only `static-document` matches today
  (Decision 1).

`organization=` is **deferred** — Endpoint is currently only
referenced by InsurancePlan; there is no Organization→Endpoint link.
Provider 5.x deferred its own `Organization.endpoint[]` to Phase 2.

## Tenant scoping

Same as BP 5.8 InsurancePlan — requests honor the existing
`TenantMiddleware` mechanism. Wrong-tenant lookups return 404 rather
than 200 with empty payload. Public CMS-0057-F unauthenticated access
is a Phase 2 capability.

## Out of scope / explicit deferrals

| Item | Why deferred |
|---|---|
| `DocumentReference` resource projection | Phase 2 migration to member-document-service. `Endpoint` is enough for Plan-Net; `DocumentReference` is the Patient-Access shape and lives in member-document-service when it lands. |
| `Endpoint?organization=` search | No Organization→Endpoint link today. Provider 5.x deferred `Organization.endpoint[]` to Phase 2. |
| `Endpoint.managingOrganization` | Same reason — payer-Organization linking is a future capability (BP 5.8 Decision 12). |
| Document content fetching / HEAD probing for hash verification | Out-of-band. Plan author warrants the URL; CHO does not act as a CDN. |
| Multi-language `Endpoint.name` | `PlanDocumentReference.DisplayName` is single-locale today. Phase 2 i18n. |
| Versioned `Endpoint.id` (e.g. include `meta.versionId`) | Same stance as InsurancePlan — FHIR resource versioning is separate from BP 5.1 plan-version chain. |

## Cross references

- [fhir-insuranceplan-projection.md](fhir-insuranceplan-projection.md)
  — capability BP 5.8 details; the projector consumed here populates
  `InsurancePlan.endpoint[]`.
- [fhir-conformance.md](fhir-conformance.md) — running ledger of the
  CHO conformance posture.
- [`schemas/plan-documents/`](../../schemas/plan-documents/) — the
  operator-facing `PlanDocumentType` reference.
