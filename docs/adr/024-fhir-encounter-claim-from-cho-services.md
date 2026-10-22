# ADR 024: FHIR Encounter and Claim Served From CHO's Own Encounter and Claims Services

## Status

Accepted. Extends [ADR 018](018-fhir-patient-coverage-from-cho-services.md) to
the two resources it left on the mock, and applies
[ADR 008](008-fhir-r4-projections.md) to them.

## Context

ADR 018 bound `Patient` and `Coverage` to member-service and coverage-service
and left `Encounter` and `Claim` on `MockFhirDataAdapter`, which has no tenant
and no CHO data behind it. `FhirDataBindings` refused `ChoNative` for both at
start-up, so no deployment could serve them from plan records.

That is the whole of gap **G-30** that remained, and it is why CMS-0057-F
scenario **PROV-01** stayed GAP after PR #1476 closed the prior-authorization
half of Provider Access: an attributed provider received the member's real
prior authorizations and real `ExplanationOfBenefit`s, and then five synthetic
encounters and three synthetic claims from the mock. A Provider Access API that
answers partly from plan records and partly from fixtures is not a Provider
Access API.

Both records already existed:

- claims-service owns the claim and already projects it as an
  `ExplanationOfBenefit` — the *adjudicated* view. It had no projection of the
  claim **as billed**, which is what FHIR `Claim` is.
- encounter-service owns the 837 encounter submission record. It had no FHIR
  projection at all.

## Decision

1. **The owning service projects; fhir-service fronts.** As in ADR 018.
   - encounter-service gains `EncounterFhirProjector`,
     `GET /api/encounters/{id}/fhir` (read) and
     `GET /api/encounters/fhir/Encounter` (search).
   - claims-service gains `ClaimFhirProjector`, `GET /fhir/Claim/{id}` (read)
     and `GET /fhir/Claim` (search), beside the existing
     `/fhir/ExplanationOfBenefit` endpoints.
   - Both search endpoints take **structured** parameters and return the
     envelope the CHO-native adapter reads:
     `{ "total": n, "items": [ { "resource": … } ] }`. (ADR 018 §1 describes
     this as `{"total", "resources"}`; the shipped contract, which
     member-service and coverage-service also use, is `items[].resource`.)
     FHIR search syntax is parsed only in fhir-service.
2. **`Claim` and `ExplanationOfBenefit` are two faces of one claim, not
   alternatives.** Both projections use the chain-stable `ClaimVersionId` as
   the FHIR id, so `Claim/{id}` and `ExplanationOfBenefit/{id}` name the same
   claim. The `Claim` carries the request — lines, units, charges, diagnoses,
   the prior authorization cited. Nothing adjudication decided appears on it:
   no allowed amount, no payment, no patient responsibility, no denial reason.
   A consumer that wants those reads the EOB.
3. **`Encounter.status` is the state of the contact, not of the EDI
   submission.** The stored record carries both: the real-world visit, and the
   lifecycle of sending it to a payer. Only the first is a FHIR Encounter. An
   encounter still queued for dispatch describes a visit that already
   happened, so it projects as `finished`; projecting `planned` for it would
   misstate the clinical fact. A void — by submission type, by 837 CLM05-3
   frequency code 8, or by the record reaching Voided — withdraws the
   assertion, so it projects as `entered-in-error`. The submission lifecycle
   stays on encounter-service's own API.
4. **`Claim.status` is the fm-status of the resource, not the adjudication
   state.** A pended or denied claim still stands (`active`); only a void
   makes it `cancelled`. FHIR has no element for the adjudication state on a
   Claim, and the EOB is where it is expressed.
5. **A status filter is the exact inverse of what the projection emits.** Both
   search repositories derive their status filter from the projector's rule,
   and a test asserts the agreement against the projector rather than a copy
   of it: a filter that disagreed would answer records whose status is not the
   one asked for. A code that is valid in the resource's value set but that no
   CHO record projects as (`planned`, `draft`) selects nothing and answers an
   empty bundle; a code outside the value set is a 400.
6. **Claim search returns current chain heads only.** The FHIR id is the chain
   head, so a superseded version would answer with a resource whose own id
   reads back as something else. Drafts are excluded. A legacy row with no
   chain id is findable by the id the projection gives it (its own `Id`).
7. **`use=preauthorization` returns nothing from Claim search.** A prior
   authorization is a different record, projected by authorization-service as a
   PDex prior-authorization `ExplanationOfBenefit` (PR #1476). Answering claims
   for it would report them as authorizations. The adapter answers empty
   without calling claims-service.
8. **Binding is configuration, per resource, and every data resource is now
   native-capable.** `FhirAdapters:Sources:Encounter` and
   `…:Claim` take `Mock` or `ChoNative`. The code and `appsettings.json`
   default stays `Mock`; the Kubernetes manifest binds all four data resources
   and Payer-to-Payer to `ChoNative`, so a production render serves no
   synthetic data through `IFhirDataAdapter`. `ChoNative` without the upstream
   URL still stops start-up.
9. **Confinement is checked twice, now on these resources too.** `Encounter`
   and `Claim` searches were narrowed to the authorized member but did not
   verify the result, which mattered less against a fixture and matters now.
   A result naming any other member withholds the whole page (502) rather than
   trimming it: trimming would answer a total the entries contradict. The log
   that records it is best-effort, so withholding never depends on the logging
   infrastructure being reachable.
10. **The new PHI read paths are audited.** encounter-service gains the shared
    record-access audit (`AddRecordAccessAudit`) and both FHIR endpoints are
    recorded, as member-service's Patient projection is. claims-service's
    `Claim` endpoints use its existing `AuditClaimAccess`.
11. **Failures, unsupported input, identity and tenancy** are exactly as
    ADR 018 §6–8 set them.

## What the projections do not claim

Neither projection declares a profile and neither has been validated against
one, so this ADR does not move **G-14**:

- **Encounter** is not US Core. Participants and the service provider are
  NPI-identifier references rather than resolvable Practitioner/Organization
  resources (CHO has no FHIR identity for a provider on this path, and a
  fabricated `Practitioner/{npi}` would not resolve). There is no `location`.
  `type` uses a CHO-private system because the 837 transaction variant has no
  US Core equivalent. Diagnoses travel as `reasonCode` rather than
  `diagnosis`, which would assert Condition resources CHO does not project.
  The CMS place-of-service code rides in a CHO-private extension because
  `class` is a single Coding and cannot carry it.
- **Claim** is not CARIN Blue Button or Da Vinci PAS. The insurer is a display
  string, the provider is an NPI-identifier reference, there is no care team,
  and `priority` is always `normal` because the stored record carries no
  billed priority.

## Scope boundaries

Recorded as follow-ups, not done here:

- Profile validation for both projections (G-14), and CARIN BB conformance for
  the claim-side resources (G-60).
- `Encounter` search honours `_id`, `patient`, `date`, `status` and `type`, and
  `Claim` search `_id`, `patient`, `created`, `status` and `use` — exactly what
  the CapabilityStatement declares. Nothing more is advertised.
- The service lines of an encounter are not on the FHIR Encounter: R4 has no
  element for them. They remain on encounter-service's own API, and the same
  procedures appear on the claim the encounter wraps.
- `MockFhirDataAdapter` stays registered and remains the default binding, for
  Development, Testing and declared synthetic demos.
- Provider Access attribution is still static configured panels (PROV-02).

## Consequences

- A deployment that binds `Encounter` and `Claim` to `ChoNative` serves
  Provider Access and Patient Access entirely from the records the portal and
  claims adjudication use. G-30 is closed.
- Demo environments must hold synthetic encounters and claims in the CHO
  services; an empty store now returns empty bundles instead of the mock's
  fixtures.
- `FhirDataBindings`'s "no CHO-native source" refusal is unreachable while
  every data resource has one. It is kept, and the native-capable set is
  pinned by a test, so adding a resource without an implementation puts the
  refusal back in play rather than silently binding it.
- The adapter-status report names each resource's own upstream; it previously
  described any non-Patient native binding as coverage-service's projection.
