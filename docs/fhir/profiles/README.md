# CHO FHIR Profiles

Cloud Health Office-authored FHIR R4 profiles, extensions, code
systems, value sets, and operation definitions, all under the
canonical namespace CHO owns.

## Canonical namespace

CHO publishes its FHIR profile artifacts under:

- `http://fhir.cloudhealthoffice.com/StructureDefinition/{id}` — profiles and extensions
- `http://fhir.cloudhealthoffice.com/CodeSystem/{id}` — CHO-local code systems
- `http://fhir.cloudhealthoffice.com/ValueSet/{id}` — CHO-local value sets
- `http://fhir.cloudhealthoffice.com/OperationDefinition/{id}` — custom operations

These URLs are permanent. Once a FHIR resource claims conformance to
one of them via `meta.profile`, the URL cannot change without
invalidating every persisted resource's profile declaration.

## Conformance posture

CHO appeal profiles follow Da Vinci profiling conventions but do not
claim conformance to any external Implementation Guide.
Post-adjudication appeals are not covered by any stable external
FHIR IG at time of authoring.

All prior CHO FHIR work (PAS, CRD, DTR, PDex, Patient Access) reuses
external HL7 canonical profile URLs (US Core, Da Vinci IGs). This
directory is the first place in the repository to author CHO-local
profiles under a CHO-owned canonical namespace.

## Current artifacts (PR 1 — appeals)

**StructureDefinitions — Resource profiles (4)**

| File | Profiles |
|---|---|
| `StructureDefinition-cho-appeal-task.json` | `Task` — the appeal work item |
| `StructureDefinition-cho-appeal-communication.json` | `Communication` — appeal-related communications |
| `StructureDefinition-cho-appeal-document-reference.json` | `DocumentReference` — supporting attachments |
| `StructureDefinition-cho-appeal-claim-response.json` | `ClaimResponse` — appeal decision outcome |

**StructureDefinitions — Extensions (7)**

| File | Applies to |
|---|---|
| `StructureDefinition-cho-appeal-level.json` | `Task` |
| `StructureDefinition-cho-appeal-line-of-business.json` | `Task` |
| `StructureDefinition-cho-appeal-target-response-date.json` | `Task` |
| `StructureDefinition-cho-appeal-urgent-flag.json` | `Task` |
| `StructureDefinition-cho-appeal-x12-275-control-number.json` | `DocumentReference.identifier` |
| `StructureDefinition-cho-appeal-x12-275-transmission-code.json` | `DocumentReference.content.format` |
| `StructureDefinition-cho-appeal-task-reference.json` | `ClaimResponse` |

**CodeSystems (6)**

- `CodeSystem-cho-appeal-type.json` — reconsideration, peer-review, external-review, grievance
- `CodeSystem-cho-appeal-level.json` — first-level, second-level, external-review
- `CodeSystem-cho-appeal-line-of-business.json` — commercial, medicare, medicaid, marketplace
- `CodeSystem-cho-appeal-x12-275-transmission-code.json` — X12 PWK02 codes (AA, BM, EL, FT, FX, IL, OZ)
- `CodeSystem-cho-appeal-communication-category.json` — appeal-argument, reviewer-note, decision-rationale
- `CodeSystem-cho-appeal-attachment-type.json` — CHO-specific attachment categories

**ValueSets (9)**

Three ValueSets narrow HL7 base code systems using explicit
`compose.include.concept[]` enumeration (not remote filters), so
offline validators do not require a live terminology server:

- `ValueSet-cho-appeal-task-status.json` — narrows `http://hl7.org/fhir/task-status` to 7 codes
- `ValueSet-cho-appeal-communication-status.json` — narrows `http://hl7.org/fhir/event-status` to 2 codes
- `ValueSet-cho-appeal-document-status.json` — narrows `http://hl7.org/fhir/document-reference-status` to 3 codes

Six ValueSets wrap the CHO-authored CodeSystems:

- `ValueSet-cho-appeal-type.json`
- `ValueSet-cho-appeal-level.json`
- `ValueSet-cho-appeal-line-of-business.json`
- `ValueSet-cho-appeal-x12-275-transmission-code.json`
- `ValueSet-cho-appeal-communication-category.json`
- `ValueSet-cho-appeal-attachment-type.json`

**OperationDefinitions (1)**

- `OperationDefinition-cho-appeal-submit.json` — `POST [base]/Task/$cho-appeal-submit`; accepts a transaction Bundle, returns a transaction-response Bundle.

**Total: 27 JSON artifacts + this README = 28 files.**

## Conformance notes

Several profile element bindings narrow a base R4 required binding to
a strict subset (e.g. `Task.status` narrows the 11-code base set to
the 7 codes CHO actually uses). FHIR R4 conformance rules explicitly
permit this: a required binding on a derived profile may narrow its
parent's required binding provided the derived value set is a strict
subset of the parent's.

## Discovery

The `fhir-service` CapabilityStatement advertises the FHIR conformance-
resource endpoints (StructureDefinition, CodeSystem, ValueSet,
OperationDefinition) with `read` + `search-type` interactions:

    GET /fhir/r4/metadata

Each profile, code system, value set, and operation definition is
served directly by canonical URL. For example:

    GET /fhir/r4/StructureDefinition/cho-appeal-task
    GET /fhir/r4/CodeSystem/cho-appeal-type
    GET /fhir/r4/ValueSet/cho-appeal-task-status
    GET /fhir/r4/OperationDefinition/cho-appeal-submit

Search endpoints return a `Bundle` (type `searchset`) of all artifacts
of the given kind — useful for tooling that wants the complete set:

    GET /fhir/r4/StructureDefinition    -> all 11 profiles + extensions
    GET /fhir/r4/CodeSystem             -> all 6 code systems
    GET /fhir/r4/ValueSet               -> all 9 value sets
    GET /fhir/r4/OperationDefinition    -> all 1 operation definition

**Note on `CapabilityStatement.rest.resource.supportedProfile`:** this
PR does NOT yet advertise the Task / Communication / DocumentReference /
ClaimResponse profiles via the CapabilityStatement's `supportedProfile`,
nor the `cho-appeal-submit` operation in `rest.operation`. Advertising
read/search interactions or an operation name before the runtime
endpoints exist would be a false conformance claim. Those advertisements
land in a subsequent PR alongside the runtime implementations. Until
then, profiles and the operation definition remain fully discoverable
via the conformance-resource endpoints listed above.

## Adding a new profile

1. Hand-author the artifact JSON in this directory. Follow HL7's
   file-naming convention: `{resourceType}-{id}.json`.
2. Use the CHO canonical namespace: every `url` under
   `http://fhir.cloudhealthoffice.com/`.
3. Include this sentence in the `description`: "Authored by Cloud
   Health Office under its own canonical namespace; does not claim
   conformance to any external Implementation Guide."
4. Add a constant for the URL in `FhirService.Services.ChoFhirCanonicalUrls`.
5. If the artifact should be advertised in CapabilityStatement, update
   `MetadataController`.
6. Add artifact-validity and controller tests under
   `tests/CloudHealthOffice.FhirService.Tests/FhirArtifacts/`.
7. Generate the snapshot hash — run the test suite; on first failure
   the `ArtifactSnapshotTests` failure message includes a mechanical
   `echo <hash> > <file>` command to register the snapshot.

The `fhir-service` project embeds every `.json` in this directory
directly into the built assembly as an embedded resource via an
`<EmbeddedResource Include="...docs/fhir/profiles/*.json"
LogicalName="FhirArtifacts.%(Filename)%(Extension)" />` item in
`fhir-service.csproj` — there is no separate copy target. MSBuild
participates in incremental rebuilds: editing a profile causes
`fhir-service.dll` to rebuild and the new artifact is served. No
extra wiring is needed after the steps above.

## Tooling

All artifacts are hand-authored. We do not use Forge, Simplifier, or
FHIR Shorthand (sushi) at this time. If the artifact count grows past
~50, revisit that decision.
