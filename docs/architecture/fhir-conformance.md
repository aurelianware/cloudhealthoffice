# FHIR conformance posture

This is the running ledger of which Implementation Guides CHO claims
conformance to, what we test against, and where the gaps are.

## In scope (Phase 1)

| IG / profile                                     | Resources covered today                       | Posture                                                 |
|--------------------------------------------------|-----------------------------------------------|---------------------------------------------------------|
| US Core 6.1.0                                    | Patient, Practitioner, PractitionerRole       | Required elements asserted by unit tests                |
| Da Vinci PDex Plan-Net 1.1.0                     | Practitioner, PractitionerRole (subset)       | Fields CHO has data for; extensions deferred to 5.17    |
| FHIR R4 Bundle (`searchset`)                     | Practitioner, PractitionerRole search          | Hand-built JsonObject, asserted by unit tests           |
| FHIR R4 OperationOutcome                         | All error responses                           | Typed `OperationOutcome` model + hand-built JsonObject  |

`meta.profile` values emitted on each resource:

- Patient (member-service) — `http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient`
- Practitioner (provider-service, capability 5.7) —
  `http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitioner`
  and
  `http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Practitioner`
- PractitionerRole (provider-service, capability 5.8) —
  `http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitionerrole`
  and
  `http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-PractitionerRole`

## Phase 2 / deferred

| IG / capability                                  | Status                                                 |
|--------------------------------------------------|--------------------------------------------------------|
| Plan-Net 1.1.0 Organization                      | Capability 5.9                                         |
| Plan-Net 1.1.0 Bundle composite                  | Capability 5.18                                        |
| Plan-Net extended extensions                     | Capability 5.17                                        |
| CMS-0057-F unauthenticated Provider Directory    | Capability 5.19                                        |
| Inferno test suite (Provider Directory)          | Separate Phase 2 capability                            |
| US Core 6.1.0 Practitioner.gender                | Capability 5.17 (Provider entity gains the field)      |

## Conformance testing today

Each projector ships a comprehensive unit-test fixture that asserts
required US Core elements are present, cardinality is honored, and
the projection is byte-deterministic across runs. The relevant test
classes:

- [FhirPatientProjectorTests](../../src/services/member-service/MemberService.Tests/Services/FhirPatientProjectorTests.cs)
  — member-service Patient projection.
- [FhirPractitionerProjectorTests](../../tests/CloudHealthOffice.ProviderService.Tests/Services/FhirPractitionerProjectorTests.cs)
  — provider-service Practitioner projection.
- [FhirPractitionerRoleProjectorTests](../../tests/CloudHealthOffice.ProviderService.Tests/Services/FhirPractitionerRoleProjectorTests.cs)
  — provider-service PractitionerRole projection.

Conformance regressions surface as failing unit tests. There is no
network-driven conformance suite in CI yet (see *Inferno* below).

## Inferno

CMS publishes Inferno test suites for ONC certification. CHO does
not run any Inferno suite in CI today. The Plan-Net Provider Directory
suite would validate capability 5.7 + 5.8 + 5.9 + 5.18 once those
capabilities ship as a Bundle composite. After 5.8, two of the four
projection paths (Practitioner + PractitionerRole) carry CHO-canonical
data; Inferno wiring is unblocked once 5.9 ships Organization.

A separate Phase 2 capability wires the Inferno Provider Directory
suite to CI. Until then, conformance is structural-only via unit
tests, and any failure surfaced by an external Inferno run is a
follow-up bug rather than a CI regression.

## CHO-authored extensions

CHO uses the canonical base `http://fhir.cloudhealthoffice.com/`. All
CHO-authored profiles, code systems, value sets, and extensions live
under this base.

Defined today (see
[ChoFhirCanonicalUrls](../../src/services/fhir-service/Services/ChoFhirCanonicalUrls.cs)):

- Appeals profiles + extensions + code systems + value sets +
  operations (capabilities 3.x).
- `provider-integrity-score` extension (capability 5.7) — emitted on
  Practitioner; carries cached IntegrityScore + IntegrityRating +
  LastVerifiedAt from the projection added in capability 5.4.5.
- `practitionerrole-panel-gating` extension (capability 5.8) — emitted
  on PractitionerRole; grouped extension carrying `panel-limit`,
  `panel-accepted`, `accepted-lobs`, `min-accepted-age-years`,
  `max-accepted-age-years` sub-extensions sourced from the
  panel-gating fields on `NetworkParticipation` (capability 5.5).
- `CodeSystem/line-of-business` (capability 5.8) — internal CodeSystem
  for the LOB codings emitted inside `accepted-lobs` sub-extensions.

A legacy URL
`https://cloudhealthoffice.com/fhir/StructureDefinition/provider-verification`
is used by the NPPES-path enrichment in fhir-service today. The
NPPES path retires after capability 5.9 ships. Do not introduce new
uses of this URL.

## Cross references

- [fhir-practitioner-projection.md](fhir-practitioner-projection.md) — capability 5.7 details.
- [fhir-practitionerrole-projection.md](fhir-practitionerrole-projection.md) — capability 5.8 details.
- [provider-versioning.md](provider-versioning.md) — version-state semantics that drive `Practitioner.active` and `PractitionerRole.active`.
