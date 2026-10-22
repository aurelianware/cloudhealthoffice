# FHIR Encounter Projection

> Projects CHO's `Encounter` entity — the 837 encounter submission record —
> into a FHIR R4 `Encounter` and exposes it through the public FHIR surface at
> `/fhir/r4/Encounter/*`. Shipped by
> [ADR 024](../adr/024-fhir-encounter-claim-from-cho-services.md) to close the
> encounter half of gap G-30.

## Why

`Encounter` was the last FHIR data resource with no CHO-native source. Reads
came from `MockFhirDataAdapter`, which has no tenant and no CHO data behind it,
so a Provider Access caller received the member's real prior authorizations and
real `ExplanationOfBenefit`s followed by five synthetic encounters. CMS-0057-F
scenario PROV-01 stayed GAP for exactly that reason.

## Architecture

```
external client
       │  GET /fhir/r4/Encounter[/{id}]
       ▼
┌────────────────────────┐                      ┌──────────────────────────────┐
│ fhir-service           │  structured query    │ encounter-service            │
│ EncounterController    │ ───────────────────▶ │ FhirEncounterController      │
│  SMART scopes, patient │ /api/encounters/     │  → IEncounterFhirSearch      │
│  binding, Provider     │   fhir/Encounter     │      Repository (Mongo)      │
│  Access attribution,   │ /api/encounters/     │  → EncounterFhirProjector    │
│  member confinement    │   {id}/fhir          │                              │
│ RoutingFhirDataAdapter │                      │ record-access audited        │
│  → ChoNative adapter   │ ◀─── total + items   │ tenant from the token        │
└────────────────────────┘      [].resource     └──────────────────────────────┘
```

fhir-service is the only place FHIR search syntax is parsed. encounter-service
receives already-resolved filters (an exact id, a member id, a date range, a
status code, a transaction variant, offset and limit) and never parses FHIR.

The route prefix is `api/encounters`, not the `api/v1/...` member-service and
coverage-service use: encounter-service's own API has no `v1` segment, and
adding one for these two endpoints would leave two prefixes in one service.
fhir-service holds the path as a constant
(`ChoNativeFhirDataAdapter.EncounterSearchRoute`).

## What `status` means

This is the one mapping a reader could reasonably get wrong, so it is stated
plainly: **`status` is the state of the real-world contact, not of the EDI
submission.**

The stored record carries both. `EncounterStatus` is a submission lifecycle —
Pending, Queued, Submitted, Acknowledged, Accepted, Rejected,
CorrectionSubmitted, Voided — and FHIR `Encounter.status` is about the patient
contact. An encounter still queued for dispatch describes a visit that already
happened; projecting `planned` for it would misstate the clinical fact.

| Stored | Projected |
|---|---|
| Any non-void state | `finished` |
| `SubmissionType.Void`, `Status == Voided`, or `ClaimFrequencyCode == "8"` | `entered-in-error` |

No other FHIR status is ever emitted. A search for one that is valid in R4 but
that no record projects as (`planned`, `in-progress`, `cancelled`) selects
nothing and answers an empty bundle; a code outside the R4 value set is a 400.
`EncounterFhirSearchRepository`'s status filter is derived from
`EncounterFhirProjector.StatusOf` and a test asserts the agreement against that
method rather than a copy of its rule.

## Field set

| FHIR element | Source | Notes |
|---|---|---|
| `id` | `Encounter.Id` | |
| `meta.lastUpdated` | `LastUpdatedDate` | drives `_lastUpdated` search |
| `identifier` | `EncounterControlNumber` | system `urn:cho:encounter-control-number` |
| `status` | derived | see above |
| `class` | `PlaceOfServiceCode` | v3-ActCode; `21/31/32/51/55/56/61` → `IMP`, `23` → `EMER`, `12` → `HH`, everything else → `AMB` |
| `type` | `EncounterType` | `urn:cho:encounter-type`: professional / institutional / dental (the 837 variant) |
| `subject` | `MemberId` | `Patient/{memberId}`, as every other CHO reference |
| `period` | `ServiceDateFrom` … `ServiceDateTo` | |
| `participant[].individual` | `RenderingProviderNPI` | NPI identifier only, when distinct from the biller |
| `serviceProvider` | `BillingProviderNPI`, `BillingProviderName` | NPI identifier plus display |
| `reasonCode` | `DiagnosisCodes` | ICD-10-CM, in 837 pointer order |
| `extension` | `PlaceOfServiceCode` | `urn:cho:StructureDefinition:encounter-place-of-service` |

## What the projection does not claim

**Not US Core.** No profile is declared and none has been validated (G-14).
Specifically:

- Participants and the service provider are NPI-identifier references rather
  than resolvable `Practitioner`/`Organization` resources. CHO has no FHIR
  identity for a provider on this path, and a fabricated `Practitioner/{npi}`
  reference would not resolve.
- There is no `location`: CHO projects no `Location` resources. The CMS
  place-of-service code is carried in a CHO-private extension instead, because
  `Encounter.class` is a single `Coding` and cannot hold it.
- `type` uses a CHO-private system. The 837 transaction variant has no US Core
  or HL7 `encounter-type` equivalent.
- Diagnoses travel as `reasonCode`, not `diagnosis`, which references
  `Condition` resources CHO does not project.
- The encounter's service lines are absent: FHIR R4 `Encounter` has no element
  for them. They stay on encounter-service's own API, and the same procedures
  appear on the claim the encounter wraps.

Nothing absent from the record is invented: an encounter with no rendering
provider has no `participant`, and one with no diagnoses has no `reasonCode`,
rather than an empty array.

## Search parameters

Exactly what the CapabilityStatement declares and exactly what is applied:
`_id`, `_lastUpdated`, `patient`, `date`, `status`, `type`, plus `_count` and
`_page`. Anything else — an unknown name, a modifier, `_include`, `_sort` — is a
400, because the bundle's self link echoes the query as given.

`date` is **overlap**, not containment: an encounter running 10–12 January
matches `date=ge2026-01-11`, which is what FHIR date search on a period means.

## Tenancy, confinement and audit

- The tenant comes from the validated token on both hops; encounter-service
  filters every query by it and a tenant argument that differs from the
  request's tenant is refused outright in the adapter.
- A patient- or provider-confined request is narrowed by the upstream's
  `memberId` filter, and fhir-service then withholds the whole page (502) if
  any result names another member. Trimming it would answer a total the
  entries contradict. A read of another member's encounter is 404, so its
  existence does not leak.
- Both endpoints are recorded in the shared record-access log, as
  member-service's Patient projection is.

## Cross-references

- [ADR 024](../adr/024-fhir-encounter-claim-from-cho-services.md) — the decision
- [ADR 018](../adr/018-fhir-patient-coverage-from-cho-services.md) — the pattern
- [claim-as-billed-fhir-projection.md](claim-as-billed-fhir-projection.md) — the sibling resource
- `docs/epa-gap-matrix.md` — G-30, G-14, scenario PROV-01
- `tests/Cms0057Acceptance.Tests/Scenarios/ProviderAccessClaimsAndEncountersTests.cs`
