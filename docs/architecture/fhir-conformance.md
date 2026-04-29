# FHIR conformance posture

This is the running ledger of which Implementation Guides CHO claims
conformance to, what we test against, and where the gaps are.

## In scope (Phase 1)

| IG / profile                                     | Resources covered today                       | Posture                                                 |
|--------------------------------------------------|-----------------------------------------------|---------------------------------------------------------|
| US Core 6.1.0                                    | Patient, Practitioner, PractitionerRole, Organization, InsurancePlan | Required elements asserted by unit tests          |
| Da Vinci PDex Plan-Net 1.1.0                     | Practitioner, PractitionerRole, Organization, InsurancePlan (subset), Endpoint | Fields CHO has data for; extensions deferred to 5.17 |
| FHIR R4 Bundle (`searchset`)                     | Practitioner, PractitionerRole, Organization, InsurancePlan search | Hand-built JsonObject, asserted by unit tests     |
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
- Organization (provider-service, capability 5.9) —
  `http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization`
  and
  `http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Organization`
  (two source entities: `Organization` network → `type=ins`;
  `Provider` with `ProviderType=Organization` → `type=prov`)
- InsurancePlan (benefit-plan-service, capability BP 5.8) —
  `http://hl7.org/fhir/us/core/StructureDefinition/us-core-insuranceplan`
  and
  `http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-InsurancePlan`
  (source entity: `BenefitPlan` head Published version; non-Active
  versions return null per the empirical Provider 5.7 stance)
- Endpoint (benefit-plan-service, capability BP 5.9) —
  `http://hl7.org/fhir/us/davinci-pdex-plan-net/StructureDefinition/plannet-Endpoint`
  (source entity: `BenefitPlan.Documents[]` on the head Published
  version; one Endpoint per projectable `PlanDocumentReference`,
  internal `documentreference/{id}` references are skipped per
  Decision 4)

## Phase 2 / deferred

| IG / capability                                  | Status                                                 |
|--------------------------------------------------|--------------------------------------------------------|
| Plan-Net 1.1.0 extended Organization extensions  | Capability 5.17 (accessibility, languages, populations)|
| Plan-Net 1.1.0 Organization.endpoint             | Phase 2 (Plan-Net publishing URLs; provider-side)      |
| Plan-Net 1.1.0 InsurancePlan.coverageArea / contact / alias | Phase 2 (BenefitPlan needs the fields)        |
| Plan-Net 1.1.0 Endpoint?organization=            | Phase 2 — Endpoint is only referenced by InsurancePlan today; no Organization→Endpoint link |
| `Endpoint.managingOrganization`                  | Phase 2 — depends on Payer-Organization linking (BP 5.8 Decision 12) |
| FHIR `DocumentReference` projection              | Phase 2 — lives in member-document-service when it lands; carries hash exposure that Plan-Net `Endpoint` has no slot for (BP 5.9 Decision 7) |
| Plan-Net 1.1.0 Bundle composite                  | Capability 5.18                                        |
| Plan-Net extended extensions (Practitioner)      | Capability 5.17                                        |
| Coverage.class slice → InsurancePlan reference   | Capability BP 5.8 follow-up — see fhir-insuranceplan-projection.md |
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

- [FhirOrganizationProjectorTests](../../tests/CloudHealthOffice.ProviderService.Tests/Services/FhirOrganizationProjectorTests.cs)
  — provider-service Organization projection (both source entities).
- [FhirInsurancePlanProjectorTests](../../src/services/benefit-plan-service/BenefitPlanService.Tests/Services/FhirInsurancePlanProjectorTests.cs)
  — benefit-plan-service InsurancePlan projection (capability BP 5.8).
- [FhirEndpointProjectorTests](../../src/services/benefit-plan-service/BenefitPlanService.Tests/Services/FhirEndpointProjectorTests.cs)
  — benefit-plan-service Endpoint projection (capability BP 5.9).

Conformance regressions surface as failing unit tests. There is no
network-driven conformance suite in CI yet (see *Inferno* below).

## Inferno

CMS publishes Inferno test suites for ONC certification. CHO does
not run any Inferno suite in CI today. The Plan-Net Provider Directory
suite would validate capability 5.7 + 5.8 + 5.9 + 5.18 once those
capabilities ship as a Bundle composite. After 5.8, two of the four
projection paths (Practitioner + PractitionerRole) carry CHO-canonical
data; Inferno wiring is unblocked once 5.9 ships Organization (which it now has).

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
- `insuranceplan-family-accumulator-model` extension (capability BP 5.8) —
  emitted on InsurancePlan; `valueCode` Embedded | Aggregate sourced from
  `BenefitPlan.FamilyAccumulatorModel` (BP 5.7).
- `insuranceplan-aca-cap-enforced` extension (capability BP 5.8) —
  emitted on InsurancePlan AND on the ACA-cap `plan.generalCost` entry;
  `valueBoolean=true` only when `AcaCapEnforcementPolicy.IsEnforced(plan)`
  returns true (Aggregate-mode + post-2026-04-28-cutoff plans).
- `CodeSystem/plan-product-shape` (capability BP 5.8) — CHO-canonical
  CodeSystem for the InsurancePlan.type product-shape coding (HMO / PPO /
  EPO / POS / HDHP / Medicaid / Medicare / Commercial).
- `CodeSystem/insuranceplan-general-cost-type` (capability BP 5.8) —
  CHO-canonical CodeSystem for `plan.generalCost.type` (deductible /
  out-of-pocket-max / aca-individual-cap).
- `CodeSystem/insuranceplan-cost-qualifier` (capability BP 5.8) —
  CHO-canonical CodeSystem for `cost.qualifiers` (in-network /
  out-of-network / copay / coinsurance).
- `CodeSystem/network-tier` (capability BP 5.8) — CHO-canonical system
  for `plan.identifier` carrying the tier name.
- `plan-id` system (capability BP 5.8) — CHO-canonical identifier
  system for `InsurancePlan.identifier[0]` carrying `BenefitPlan.PlanId`.
- `CodeSystem/endpoint-connection-type` (capability BP 5.9) —
  CHO-canonical CodeSystem for `Endpoint.connectionType`. Publishes
  one code, `static-document`, because the HL7
  `endpoint-connection-type` CodeSystem has no "static downloadable
  document" code (Decision 1).
- `CodeSystem/plan-document-type` (capability BP 5.9) — CHO-canonical
  CodeSystem for `Endpoint.payloadType.coding`. One code per
  `PlanDocumentType` enum value (`sbc`, `eoc`, `formulary`, `spd`,
  `mrf`, `other`). Plan-Net does not bind `payloadType`; no standard
  FHIR CodeSystem covers SBC / EOC / SPD / MRF (Decision 3).

A legacy URL
`https://cloudhealthoffice.com/fhir/StructureDefinition/provider-verification`
is used by the NPPES-path enrichment in fhir-service today. The
NPPES path retires after capability 5.9 ships. Do not introduce new
uses of this URL.

## Cross references

- [fhir-practitioner-projection.md](fhir-practitioner-projection.md) — capability 5.7 details.
- [fhir-practitionerrole-projection.md](fhir-practitionerrole-projection.md) — capability 5.8 details.
- [fhir-organization-projection.md](fhir-organization-projection.md) — capability 5.9 details.
- [fhir-insuranceplan-projection.md](fhir-insuranceplan-projection.md) — capability BP 5.8 details.
- [fhir-endpoint-projection.md](fhir-endpoint-projection.md) — capability BP 5.9 details (Plan Documents → FHIR Endpoint).
- [provider-versioning.md](provider-versioning.md) — version-state semantics that drive `Practitioner.active` and `PractitionerRole.active`.
- [family-accumulator-models.md](family-accumulator-models.md) — BP 5.7 (FamilyAccumulatorModel + ACA cap), source of the BP 5.8 custom extensions.
