# Plan Documents — Operator Reference

Operator-friendly reference for the `PlanDocumentType` codes used on
`BenefitPlan.Documents[]` (capability **BP 5.9 — Plan Documents → FHIR
Endpoint projection**). This directory mirrors
[`schemas/service-category-mappings/`](../service-category-mappings/) in
purpose: a low-friction reference that lives next to the data it
describes.

The codes here drive the `Endpoint.payloadType.coding` slot under the
CHO CodeSystem
`http://fhir.cloudhealthoffice.com/CodeSystem/plan-document-type`
(BP 5.9 Decision 3). Plan-Net IG 1.1.0 does not bind `payloadType`, so
no standard FHIR CodeSystem covers these.

## Codes

| Enum (`PlanDocumentType`) | Code | What it is | Regulatory anchor |
|---|---|---|---|
| `SBC` | `sbc` | Summary of Benefits and Coverage | ACA, 45 CFR §147.200 |
| `EOC` | `eoc` | Evidence of Coverage / Certificate of Coverage | State insurance code; carrier-specific |
| `Formulary` | `formulary` | Drug formulary (covered medications + tiering) | CMS-0057-F (Patient Access API formulary discoverability) |
| `SPD` | `spd` | Summary Plan Description | ERISA, 29 CFR §2520.102-2 |
| `MachineReadableRateFile` | `mrf` | In-network and out-of-network rate files | CMS Transparency in Coverage, 45 CFR §147.211 |
| `Other` | `other` | Anything that doesn't fit a typed slot above | — |

## Authoring conventions

A `PlanDocumentReference` carries:

| Field | Notes |
|---|---|
| `id` | GUID. Becomes `Endpoint.id` verbatim (Decision 2). |
| `docType` | One of the enum values above. Drives FHIR `payloadType` and the BP 5.8 `InsurancePlan.endpoint[]` ordering (SBC first, then EOC, Formulary, SPD, MRF, Other). |
| `location` | HTTPS URL **or** the reserved `documentreference/{id}` form (Phase 2 forward-compat). HTTP, relative URLs, and other schemes are rejected at producer boundaries by `PlanDocumentValidation.ValidateLocation`. |
| `displayName` | Optional. Surfaces as `Endpoint.name`; falls back to the per-DocType display when absent. Single-locale today. |
| `contentType` | Optional MIME type. Pass-through to `Endpoint.payloadMimeType` — no inference (operators authored ContentType when they had it). |
| `contentHashSha256` | Optional. Base64-encoded SHA-256, exactly 32 decoded bytes (matches `Attachment.hash`). Validated at producer boundaries via `PlanDocumentValidation.ValidateHash`. **Not currently exposed in `Endpoint`** — Plan-Net's `Endpoint` profile has no `Attachment`-shaped slot; hash exposure waits for the Phase 2 `DocumentReference` projection. |
| `effectiveDate` | Optional. Surfaces as `Endpoint.period.start`; future-dated documents emit `Endpoint.status = "off"`. |
| `version` | Optional. Free-text version string. Not currently surfaced in the FHIR `Endpoint` projection. |

### When to choose `MachineReadableRateFile` vs `Other`

`MachineReadableRateFile` is the right choice for any document that
implements the CMS Transparency in Coverage rate-file format
(in-network rate file, out-of-network allowed-amount file). Pre-BP-5.9
plans that stored MRFs under `Other` continue to round-trip without
migration; new plans should use `mrf` so the FHIR projection is
lossless.

Use `Other` only when none of the typed codes fit — for example, a
state-specific addendum or a member-handbook supplement.

## Validation

`PlanDocumentValidation.ValidateDocuments` is wired at every plan-write
boundary in `BenefitPlansController` (create, update, amend). It calls:

- `ValidateLocation` — must be HTTPS or `documentreference/{id}`.
- `ValidateHash` — when `contentHashSha256` is set, must be Base64
  SHA-256 (32 decoded bytes).

The validator throws `ArgumentException` with `ParamName` set to the
field path so clients can surface a clean field-level error
(`documents[2].location`, `documents[2].contentHashSha256`).

## Related

- Architecture doc:
  [`docs/architecture/fhir-endpoint-projection.md`](../../docs/architecture/fhir-endpoint-projection.md)
- BP 5.8 InsurancePlan projection:
  [`docs/architecture/fhir-insuranceplan-projection.md`](../../docs/architecture/fhir-insuranceplan-projection.md)
- Conformance ledger:
  [`docs/architecture/fhir-conformance.md`](../../docs/architecture/fhir-conformance.md)
