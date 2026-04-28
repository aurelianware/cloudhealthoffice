# FHIR Practitioner projection (capability 5.7)

## What this is

Provider-service projects each `Provider` row with
`ProviderType.Individual` to a hand-built FHIR R4 Practitioner JsonObject
and exposes the projection at `/fhir/Practitioner/{npi}` and
`/fhir/Practitioner` (search). Fhir-service keeps the external
`/fhir/r4/Practitioner/*` URL surface and proxies those calls to
provider-service via a typed `ProviderService` `HttpClient`.

The projection conforms to:

- US Core 6.1.0 Practitioner profile.
- Da Vinci PDex Plan-Net 1.1.0 Practitioner profile (for the fields
  CHO has data for; see *Deferrals*).

## Why the data flow inverted

Before 5.7, `fhir-service ProviderDirectoryController` called NPPES
directly and enriched with `provider-verification-service`. CHO's own
`provider-service` (versioned `Provider`, IntegrityScore from 5.4.5,
credentialing from 5.6, panel-gating from 5.5) was not consulted at
all. That made the directory the union of every NPI in NPPES rather
than the providers a payer has a relationship with.

5.7 inverts the flow for Practitioner only:

```
external client
   │
   ▼
fhir-service /fhir/r4/Practitioner/{npi}        ← URL stability
   │
   ▼  HTTP, "ProviderService" typed HttpClient
provider-service /fhir/Practitioner/{npi}        ← projection authority
   │
   ▼
FhirPractitionerProjector  →  IProviderRepository.GetByNPIAsync
                              IProviderRepository.SearchAsync
```

Organization, PractitionerRole, and Location stay on the NPPES path
in this PR. Capability 5.8 redirects PractitionerRole, capability 5.9
redirects Organization, then a cleanup PR retires the NPPES code
entirely and deletes the HYBRID STATE comment block.

## Service boundaries

| Concern                          | Owner                            |
|----------------------------------|----------------------------------|
| `Practitioner` JSON shape        | `provider-service` (`FhirPractitionerProjector`) |
| `/fhir/r4/*` URL surface         | `fhir-service` (`ProviderDirectoryController`)   |
| Tenant context, JWT, SMART scope | `fhir-service` (perimeter)       |
| Integrity-score projection       | `provider-verification-service` writes; `provider-service` caches and projects |

The proxy adds **no business logic** on the Practitioner path. It
forwards the request, passes the body and status code through (with
5xx mapped to a FHIR 502 OperationOutcome to avoid leaking upstream
detail), and is otherwise dumb.

## Mapping

| FHIR element                       | Source                                                                |
|------------------------------------|-----------------------------------------------------------------------|
| `id`                               | `Provider.NPI`                                                         |
| `meta.profile`                     | US Core 6.1.0 + Plan-Net 1.1.0 Practitioner                            |
| `meta.lastUpdated`                 | `Provider.LastUpdatedDate` (ISO 8601, UTC)                             |
| `identifier[]`                     | NPI with system `http://hl7.org/fhir/sid/us-npi`                       |
| `active`                           | `VersionState == Active && Status == Active`                           |
| `name[].family`                    | `Provider.LastName`                                                    |
| `name[].given[]`                   | `Provider.FirstName`, `Provider.MiddleName` (in that order, when set)  |
| `name[].suffix[]`                  | `Provider.Credentials` parsed comma-separated, trimmed, empties dropped |
| `gender`                           | **NOT EMITTED** — see Deferrals                                        |
| `address[]`                        | `Provider.Address` / `City` / `State` / `ZipCode`                      |
| `telecom[]`                        | `Phone`, `Fax`, `Email` in that order                                  |
| `qualification[].code` (primary)   | NUCC coding `{system, TaxonomyCode, PrimarySpecialty}`                 |
| `qualification[].code` (secondary) | text-only CodeableConcept (no `coding`) — see Deferrals                 |
| `qualification[].code` (board)     | v2-0360 `BC` "Board Certified" + period + issuer                       |
| `communication[]`                  | `LanguagesSpoken` mapped to BCP-47 codings                             |
| `extension`                        | `provider-integrity-score` (only when `IntegrityScore` non-null)       |

## Deferrals (NOT in scope for 5.7)

These are explicit out-of-scope choices, ratified during the plan
phase. None of them are bugs.

### Practitioner.gender — capability 5.17

`Provider` has no `Gender` field today. US Core 6.1.0
Practitioner.gender is `Must Support 0..1`, so omission is
conformant. Adding the field to the versioned Provider entity is its
own scope; it lands alongside other Plan-Net demographics (race,
ethnicity, etc.) in capability 5.17.

### Secondary specialty NUCC codes — future capability

`Provider.PrimarySpecialty` (string) sits alongside
`Provider.TaxonomyCode` (the NUCC code itself, e.g. `207R00000X`).
`SecondarySpecialties` is `List<string>` with no parallel
taxonomy-code list, and the NUCC crosswalk lives in
`provider-verification-engine`, not provider-service.

The projection emits primary specialty with full NUCC coding and
emits secondaries as text-only `CodeableConcept` entries (no
`coding`). FHIR `CodeableConcept` permits text without coding;
conformant. A future capability adds secondary-taxonomy storage and
resolution.

### Plan-Net extended extensions — capability 5.17

Cultural-competency, accessibility, gender-affirming care, and
populations-served extensions on Plan-Net Practitioner are not
emitted. Those fields don't exist on `Provider` today; adding them
is capability 5.17.

### Public CMS-0057-F endpoint — capability 5.19

5.7 is internal-facing. Public unauthenticated access (CMS-0057-F
Provider Directory API requirements) is capability 5.19, which adds
a separate route, payer-id-scoped reads, and CMS-required search
parameters.

### Plan-Net Bundle composite — capability 5.18

Plan-Net's `[Practitioner, PractitionerRole, Organization]` Bundle
composite is capability 5.18. The proxy pattern established in 5.7
is the first piece of that composite — fhir-service will assemble
the Bundle by calling each domain service's FHIR endpoint once 5.8
and 5.9 ship.

### Inferno wiring — separate capability

CHO has no Inferno test runner in CI today. Conformance is asserted
via unit tests (`FhirPractitionerProjectorTests`). A separate Phase 2
capability wires the Inferno Provider Directory test suite.

## Tenant scoping

The endpoint honors the existing `TenantMiddleware` mechanism on
both services: JWT `tenant_id` claim → `X-Tenant-ID` header
fallback → 401 if missing. Authenticated callers see their tenant's
providers only. Public CMS-0057-F access lives on a separate
endpoint added by capability 5.19.

The proxy chains `TenantHeaderPropagationHandler` and
`CorrelationIdPropagationHandler` (mirrors the
`HttpFhirAppealAdapter` registration) so tenant + correlation ids
flow end-to-end.

## Integrity-score extension URI

`http://fhir.cloudhealthoffice.com/StructureDefinition/provider-integrity-score`,
defined by `ChoFhirCanonicalUrls.ProviderIntegrityScoreExt` in
fhir-service and mirrored as `ChoProviderFhirUrls.ProviderIntegrityScoreExt`
in provider-service (until a shared FHIR-infrastructure project
lands). The mirror is intentional and documented in both files —
provider-service does not reference fhir-service.

The legacy
`https://cloudhealthoffice.com/fhir/StructureDefinition/provider-verification`
URL on the NPPES enrichment path is left untouched. It dies with the
NPPES code in the post-5.8/5.9 cleanup PR.

## Code references

- [src/services/provider-service/Services/IFhirPractitionerProjector.cs](../../src/services/provider-service/Services/IFhirPractitionerProjector.cs)
- [src/services/provider-service/Services/FhirPractitionerProjector.cs](../../src/services/provider-service/Services/FhirPractitionerProjector.cs)
- [src/services/provider-service/Services/FhirExtensionBuilder.cs](../../src/services/provider-service/Services/FhirExtensionBuilder.cs)
- [src/services/provider-service/Services/ChoProviderFhirUrls.cs](../../src/services/provider-service/Services/ChoProviderFhirUrls.cs)
- [src/services/provider-service/Controllers/FhirPractitionerController.cs](../../src/services/provider-service/Controllers/FhirPractitionerController.cs)
- [src/services/provider-service/Models/ProviderIntegrityProjection.cs](../../src/services/provider-service/Models/ProviderIntegrityProjection.cs)
- [src/services/fhir-service/Controllers/ProviderDirectoryController.cs](../../src/services/fhir-service/Controllers/ProviderDirectoryController.cs)
- [src/services/fhir-service/Services/ChoFhirCanonicalUrls.cs](../../src/services/fhir-service/Services/ChoFhirCanonicalUrls.cs)

## Cross references

- [provider-versioning.md](provider-versioning.md) — version chain semantics that drive `Practitioner.active`.
- [network-roster-api.md](network-roster-api.md) — same `IProviderRepository`, same tenant model.
- [provider-adapter-pattern.md](provider-adapter-pattern.md) — the FHIR projection sits alongside the adapter pattern; both consume `Provider`.
- [credentialing-workflow.md](credentialing-workflow.md) — credentialing-status is intentionally NOT surfaced on Practitioner (provider-operations metadata, not directory-consumer-facing). PractitionerRole in 5.9 may surface it.
- [fhir-conformance.md](fhir-conformance.md) — overall conformance posture and the Inferno deferral.
