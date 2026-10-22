# FHIR Claim Projection (the claim as billed)

> Projects CHO's `Claim` entity into a FHIR R4 `Claim` — what the provider
> billed — and exposes it at `/fhir/r4/Claim/*`. The *adjudicated* view of the
> same record is the `ExplanationOfBenefit` projection, documented in
> [claim-fhir-projection.md](claim-fhir-projection.md). Shipped by
> [ADR 024](../adr/024-fhir-encounter-claim-from-cho-services.md) to close the
> claim half of gap G-30.

## Why

`Claim` reads came from `MockFhirDataAdapter`: three synthetic claims, no
tenant. claims-service already projected the record as an EOB, but FHIR `Claim`
and FHIR `ExplanationOfBenefit` are not alternatives — a `Claim` is the request
for adjudication and an EOB is what adjudication did with it. A Provider Access
caller asking for `Claim` was asking for the billed detail and getting
fixtures.

## Claim and ExplanationOfBenefit are two faces of one claim

Both projections use the chain-stable `ClaimVersionId` as the FHIR id, so
`Claim/{id}` and `ExplanationOfBenefit/{id}` name the same claim. The division
is strict:

| | `Claim` | `ExplanationOfBenefit` |
|---|---|---|
| Lines, units, charges, modifiers | yes | yes |
| Diagnoses, prior auth cited | yes | yes |
| Allowed amount, payment, patient responsibility | **no** | yes |
| Denial reason, CARC coding, NCCI/MUE findings | **no** | yes |
| AI-examination disposition | **no** | yes |

A test asserts that no adjudication figure or reason code appears anywhere in
the `Claim` JSON.

## What `status` means

FHIR `Claim.status` is an `fm-status` code about the *resource*, not an
adjudication outcome. A pended or denied claim still stands.

| Stored `ClaimStatus` | Projected |
|---|---|
| `Voided` | `cancelled` |
| everything else (Submitted, Received, InAdjudication, Pended, Approved, Deny, Denied, Paid, PartiallyPaid) | `active` |

FHIR has no element for the adjudication state on a `Claim`; the EOB is where
it is expressed. As with Encounter, `ClaimFhirSearchRepository`'s status filter
is derived from `ClaimFhirProjector.StatusOf` and the agreement is asserted
against that method.

## Versions

Searches return **current chain heads only**: `SupersededAt == null` and
`VersionState != Draft`, as `ClaimCriteriaFilter` defines current. The FHIR id
is the chain head, so returning a superseded version would answer with a
resource whose own id reads back as something else. A read resolves the chain
head through `GetLatestVersionAsync`.

A legacy row predating the version chain carries no `ClaimVersionId`; the
projection gives it its own `Id`, and `_id` search matches either field so that
id is findable.

## Field set

| FHIR element | Source | Notes |
|---|---|---|
| `id` | `ClaimVersionId` (else `Id`) | shared with the EOB projection |
| `identifier` | `ClaimNumber` | system `urn:cho:claim-number` |
| `status` | derived | see above |
| `type` | `ClaimType` | HL7 `claim-type`: professional / institutional / **oral** (dental) |
| `use` | constant `claim` | see below |
| `patient` | `MemberId` | `Patient/{memberId}` |
| `created` | `SubmittedDate` | FHIR `created` search parameter |
| `priority` | constant `normal` | 1..1 in R4; the record carries no billed priority |
| `provider` | `BillingProviderNPI` | NPI identifier reference |
| `billablePeriod` | `ServiceDateFrom` … `ServiceDateTo` | |
| `insurance[0]` | `CoverageId`, `PriorAuthorizationNumber` | `focal: true`; the cited authorization is `preAuthRef` |
| `diagnosis[]` | `DiagnosisCodes` | ICD-10-CM; `sequence` keeps the 837 pointer so line pointers resolve; `type` principal (ABK) / secondary |
| `item[]` | `ClaimLines` | `productOrService` CPT/HCPCS, `servicedPeriod`, `quantity`, `net`, `modifier`, `diagnosisSequence`, `revenue`, `detail` (NDC) |
| `total` | `TotalChargeAmount` | USD |
| `extension` | `PlaceOfServiceCode` | `urn:cho:StructureDefinition:claim-place-of-service` |

Two details worth stating because they are easy to get wrong:

- **`item.net` is the line total, not a unit price.** The stored
  `ChargeAmount` is the total billed for the line across all units (837
  SV102/SV203). `unitPrice` is deliberately absent rather than set to `net`,
  which would imply `net = unitPrice × quantity`.
- **`diagnosis.sequence` keeps the 837 pointer numbers** whenever they can
  serve as keys (all present, all distinct), because `item.diagnosisSequence`
  points at them. When they cannot, the projection falls back to ordinal
  numbering, which keeps the resource internally consistent even though the
  line pointers will then match nothing.

## `use=preauthorization` returns nothing

A prior authorization is a different record, projected by authorization-service
as a PDex prior-authorization `ExplanationOfBenefit` with
`use = preauthorization` (PR #1476). Answering claims for `use=preauthorization`
would report them as authorizations, so the CHO-native adapter answers an empty
bundle without calling claims-service.

## What the projection does not claim

**Not CARIN Blue Button and not Da Vinci PAS.** No profile is declared and none
has been validated (G-14, G-60). The insurer is a display string, the provider
is an NPI-identifier reference rather than a resolvable `Organization`, there is
no `careTeam`, and `priority` is always `normal`. A claim with no `CoverageId`
gets an identifier-free `coverage.display` — "this plan" — rather than a
fabricated `Coverage/{id}` that would not resolve.

## Search parameters

Exactly what the CapabilityStatement declares and exactly what is applied:
`_id`, `_lastUpdated`, `patient`, `created`, `status`, `use`, plus `_count` and
`_page`. Anything else is a 400.

## Tenancy, confinement and audit

As for Encounter (see
[encounter-fhir-projection.md](encounter-fhir-projection.md)): tenant from the
token on both hops, a 404 on a read outside the confined member, a 502
withholding the whole page if a confined search returns another member's claim,
and claims-service's existing `AuditClaimAccess` on both endpoints.

## Cross-references

- [ADR 024](../adr/024-fhir-encounter-claim-from-cho-services.md) — the decision
- [claim-fhir-projection.md](claim-fhir-projection.md) — the adjudicated view
- [encounter-fhir-projection.md](encounter-fhir-projection.md) — the sibling resource
- `docs/epa-gap-matrix.md` — G-30, G-14, G-60, scenario PROV-01
- `tests/Cms0057Acceptance.Tests/Scenarios/ProviderAccessClaimsAndEncountersTests.cs`
