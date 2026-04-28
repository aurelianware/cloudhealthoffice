# FHIR PractitionerRole projection (capability 5.8)

## What this is

Provider-service projects each `NetworkParticipation` on an Active
individual `Provider` to a hand-built FHIR R4 PractitionerRole
JsonObject and exposes the projection at `/fhir/PractitionerRole/{id}`
and `/fhir/PractitionerRole` (search). Fhir-service keeps the external
`/fhir/r4/PractitionerRole/*` URL surface and proxies those calls to
provider-service via the same typed `ProviderService` `HttpClient`
established by capability 5.7.

The projection conforms to:

- US Core 6.1.0 PractitionerRole profile.
- Da Vinci PDex Plan-Net 1.1.0 PractitionerRole profile (for the
  fields CHO has data for; see *Deferrals*).

## Why this PR

Before 5.8, fhir-service `ProviderDirectoryController` synthesized
PractitionerRole from NPPES taxonomy data. That carried two problems:

1. The synthesized PractitionerRole had no notion of network — every
   PractitionerRole had id `{NPI}-role`, one per provider, no
   participation context. Plan-Net IG requires
   `PractitionerRole.organization`, which 5.4's network-as-Organization
   work made it possible to populate canonically.
2. Panel-gating, network-tier, line-of-business, and effective dating
   live on `NetworkParticipation` (capability 5.5). The synthesized
   shape couldn't surface them.

5.8 inverts the flow for PractitionerRole exactly as 5.7 did for
Practitioner:

```
external client
   │
   ▼
fhir-service /fhir/r4/PractitionerRole/{id}      ← URL stability
   │
   ▼  HTTP, "ProviderService" typed HttpClient
provider-service /fhir/PractitionerRole/{id}     ← projection authority
   │
   ▼
FhirPractitionerRoleProjector
   │
   ├─ IProviderRepository.GetByNPIAsync / SearchAsync /
   │   ListNetworkRosterAsync
   └─ IOrganizationRepository.GetByIdAsync (for organization.display)
```

After 5.8 ships, two of the four provider-directory resources
(Practitioner, PractitionerRole) carry CHO-canonical data. Organization
follows in 5.9. Location remains NPPES-backed until a Phase 2
location-modeling capability lands.

## Service boundaries

| Concern                          | Owner                                                  |
|----------------------------------|--------------------------------------------------------|
| `PractitionerRole` JSON shape    | `provider-service` (`FhirPractitionerRoleProjector`)   |
| `/fhir/r4/*` URL surface         | `fhir-service` (`ProviderDirectoryController`)         |
| Tenant context, JWT, SMART scope | `fhir-service` (perimeter)                             |
| Panel-gating projection          | `provider-service` (`FhirPractitionerRoleProjector`)   |

The proxy adds **no business logic** on the PractitionerRole path. It
forwards the request, passes the body and status code through (with
5xx mapped to a FHIR 502 OperationOutcome to avoid leaking upstream
detail), and is otherwise dumb. The same `ProxyProviderServiceAsync`
helper now serves Practitioner and PractitionerRole — Organization
plugs into the same shape in 5.9.

## Mapping

| FHIR element                       | Source                                                                                |
|------------------------------------|---------------------------------------------------------------------------------------|
| `id`                               | composite-tuple `{npi}-{lobInt}-{yyyymmdd}-{networkId}` — see *ID encoding*           |
| `meta.profile`                     | US Core 6.1.0 + Plan-Net 1.1.0 PractitionerRole                                        |
| `meta.lastUpdated`                 | `Provider.LastUpdatedDate` (ISO 8601, UTC)                                             |
| `active`                           | derived — see *Active flag*                                                            |
| `practitioner.reference`           | `Practitioner/{Provider.NPI}`                                                          |
| `practitioner.display`             | `FirstName MiddleName LastName Credentials` (whitespace-trimmed)                       |
| `organization.reference`           | `Organization/{NetworkParticipation.NetworkId}`                                        |
| `organization.display`             | `Organization.Name` when the network resolves; omitted otherwise                       |
| `code[]`                           | `NetworkParticipation.NetworkTier` text-only CodeableConcept                           |
| `specialty[]` (primary)            | NUCC coding `{system, Provider.TaxonomyCode, PrimarySpecialty}`                        |
| `specialty[]` (secondary)          | text-only CodeableConcept (no `coding`) — see *Deferrals*                              |
| `period.start` / `period.end`      | `EffectiveDate` / `TerminationDate` (UTC, `yyyy-MM-dd`)                                |
| `telecom[]`                        | `Provider.Phone`, `Provider.Fax`, `Provider.Email` in that order                       |
| `extension[panel-gating]`          | grouped extension — see *Panel-gating extension*                                        |

### ID encoding

The FHIR R4 `id` data type grammar is `[A-Za-z0-9\-\.]{1,64}`. The
PractitionerRole composite-tuple is encoded as a dash-delimited compact
form:

```
{npi}-{lobInt}-{yyyymmdd}-{networkId}
```

- `{npi}` = 10-digit National Provider Identifier
- `{lobInt}` = `LineOfBusiness` enum integer value (1–6)
- `{yyyymmdd}` = `EffectiveDate` UTC, compact ISO with no separators (8 digits)
- `{networkId}` = `Organization.OrganizationId` chain key

**Decoder.** Regex `^(?<npi>\d{10})-(?<lob>\d+)-(?<date>\d{8})-(?<network>.+)$`.
The first three captures have fixed shape so the trailing capture
absorbs the rest of the id, including any internal hyphens in the
network id (Guid-shaped chain keys are the common case).

**64-char cap.** Worst case the composite is `10 + 1 + 8 + 3 + len(networkId)`
≈ 22 + `len(networkId)`. For Guid-shaped network ids (36 chars) the
total is 58 characters — fits. For network ids longer than 42 chars,
the projector returns null (the row is invisible to FHIR rather than
emitting a non-conformant id) and reads against such ids return 404.
This guards against a silent FHIR-grammar violation that would only
surface at the consumer.

**No Base64URL.** Base64URL output uses `_` which is not in the FHIR
`id` grammar. The dash-delimited form is grammar-conformant and
trivially reversible.

### Active flag

```
active = participation.EffectiveDate <= UtcNow
      && (participation.TerminationDate is null
          || participation.TerminationDate >= UtcNow)
      && provider.VersionState == Active
      && provider.Status == Active
```

Both the participation period and the provider's version / status must
be active. A terminated participation on an active provider emits
`active=false` and `period.end`. A suspended provider's
PractitionerRoles are not projected at all (the projector returns null
for non-Active providers, so the row is omitted from search results
and 404 on read).

### Panel-gating extension

A single grouped extension at the canonical URL
`http://fhir.cloudhealthoffice.com/StructureDefinition/practitionerrole-panel-gating`,
with sub-extensions:

| sub-extension URL          | Source                                  | Type           |
|----------------------------|-----------------------------------------|----------------|
| `panel-limit`              | `NetworkParticipation.PanelLimit`       | `valueInteger` |
| `panel-accepted`           | `NetworkParticipation.PanelAccepted`    | `valueBoolean` |
| `accepted-lobs`            | `NetworkParticipation.AcceptedLobs`     | repeated `valueCoding` |
| `min-accepted-age-years`   | `NetworkParticipation.MinAcceptedAgeYears` | `valueInteger` |
| `max-accepted-age-years`   | `NetworkParticipation.MaxAcceptedAgeYears` | `valueInteger` |

**Emission rules.**

- Each sub-extension emits only when its source field is non-null.
- `accepted-lobs` emits one `valueCoding` per LOB in the list (using
  CHO's internal `CodeSystem/line-of-business`).
- The grouped parent extension is omitted entirely when all five
  sub-extensions would be empty — legacy / unconstrained participations
  that pre-date capability 5.5 carry no extension on the wire.

## Search semantics

| Query                                               | Behavior                                                                 |
|-----------------------------------------------------|--------------------------------------------------------------------------|
| `?practitioner=Practitioner/{npi}`                  | All visible participations on the latest Active provider                 |
| `?practitioner=Practitioner/{npi}&organization=...` | Conjunction — only participations matching the organization filter      |
| `?organization=Organization/{networkId}`            | All providers in the network, one role per matching participation       |
| `?specialty=...`                                    | Provider-level filter on `PrimarySpecialty` / `TaxonomyCode`            |
| (none of the above)                                 | Empty Bundle (mirrors the existing fhir-service behavior)               |

**Premise correction (capability 5.8 plan-phase).** The
`NetworkParticipation` model has no `Specialty` field today; specialty
is sourced from the linked `Provider` (primary as NUCC-coded
CodeableConcept, secondaries as text-only). The `?specialty=...` filter
matches against the same fields the 5.4 roster uses — `PrimarySpecialty`
substring or `TaxonomyCode` substring, case-insensitive.

**Pagination.** Page-based via `_count` (1–200, default 50) and
`_page` — mirrors the 5.7 Practitioner controller. Cursor-based
pagination is not surfaced here; the operational roster (capability
5.4) keeps the cursor shape because it has different stability
semantics.

**Search-by-organization implementation.** The controller calls
`IProviderRepository.ListNetworkRosterAsync` directly (Decision 8 of
the 5.8 plan-phase). The roster service layer
(`INetworkRosterService.GetRosterAsync`) is intentionally bypassed
because its cursor-binding hash logic does not apply to a FHIR search
and its `NetworkRosterEntry` shape is operational, not FHIR. Both the
roster API and the FHIR PractitionerRole search now go through the
same repository query — see
[network-roster-api.md](network-roster-api.md).

## Tenant scoping

Honored via the existing `TenantMiddleware` mechanism (Decision 8 of
the 5.8 plan-phase). Authenticated / header-scoped callers see their
tenant's PractitionerRoles only. Public CMS-0057-F unauthenticated
access is a separate capability (5.19) — both the Practitioner and
PractitionerRole endpoints will be wired through that capability's
public surface together.

## Verification metadata stays on Practitioner

PractitionerRole references the Practitioner via
`PractitionerRole.practitioner`. Integrity score and verification
extensions live on the Practitioner projection (capability 5.7). They
are NOT duplicated on PractitionerRole. A consumer that wants the
verification metadata follows the practitioner reference — exactly the
posture the previous NPPES-backed PractitionerRole carried, preserved
verbatim.

## Deferrals

### Plan-Net extended extensions — capability 5.17

`PractitionerRole.healthcareService`, `PractitionerRole.location`,
`PractitionerRole.availableTime`, plus the Plan-Net IG profile slices
(`acceptingPatients`, `qualification`, `endpoint`) are not in scope.
CHO does not have data for these fields today; capability 5.17 either
adds the data or formally documents the gap.

### NUCC resolution for secondary specialties

Provider-service stores secondary specialties as free text. The
projector emits text-only CodeableConcept entries — Plan-Net IG
accepts text-only under "extensible" binding strength. A future
capability adds a NUCC resolver and upgrades the secondary entries to
fully-coded.

### Per-participation Specialty field

`NetworkParticipation` does not carry a per-participation specialty
today; specialty derives from the linked Provider. If payer contracts
ever require that a single provider participate in different networks
with different declared specialties, a model change adds the field.
Out of scope for 5.8.

### Inferno wiring — separate capability

CHO has no Inferno test runner in CI today. Conformance is asserted
via xUnit structural tests (US Core PractitionerRole Must Support
elements + Plan-Net IG required slices). The Inferno wiring is a
separate Phase 2 capability and unblocked once 5.9 ships Organization.

## Tests

- [`FhirPractitionerRoleProjectorTests`](../../tests/CloudHealthOffice.ProviderService.Tests/Services/FhirPractitionerRoleProjectorTests.cs)
  — projection correctness, US Core profile structure, edge cases
  (legacy NetworkId-null returns null, Organization-type provider
  returns null, terminated participation emits `period.end` and
  `active=false`, panel-gating extensions emit only when populated),
  composite-id round-trip, determinism check.
- [`FhirPractitionerRoleControllerTests`](../../tests/CloudHealthOffice.ProviderService.Tests/Controllers/FhirPractitionerRoleControllerTests.cs)
  — endpoint behavior, search-parameter semantics
  (practitioner / organization / specialty conjunction), composite-id
  encode/decode round-trip, FHIR OperationOutcome shapes on errors,
  tenant scoping.
- [`ProviderDirectoryControllerPractitionerRoleProxyTests`](../../tests/CloudHealthOffice.FhirService.Tests/Controllers/ProviderDirectoryControllerPractitionerRoleProxyTests.cs)
  — fhir-service proxy verifies status / body / content-type
  pass-through, 5xx → 502, NPPES + verification clients NOT touched on
  the PractitionerRole path.

## Recovery posture

This PR adds a new projection and rewires one existing endpoint.
Failure modes:

- **Composite-ID decoding edge cases** — handled defensively in the
  controller; malformed id returns 404 with FHIR OperationOutcome.
- **fhir-service proxy hop fails** — caller sees 502; fhir-service
  logs the failure with a `PractitionerRole`-labelled structured
  field; provider-service unaffected; revert restores NPPES path if
  needed.
- **Panel-gating extension format wrong** — caught by unit tests and
  conformance assertions; fast fix.
- **Legacy NetworkId-null participation handling** — explicit
  invisible semantic; documented in the projector XML doc and the
  search behavior table above.

Worst-case rollback: revert this PR. fhir-service PractitionerRole
endpoints return to the NPPES path. provider-service projector code
remains but is unwired.

## Cross references

- [fhir-practitioner-projection.md](fhir-practitioner-projection.md) — capability 5.7 details (the proxy pattern this PR extends).
- [fhir-organization-projection.md](fhir-organization-projection.md) — capability 5.9 details.
  After 5.9 ships, `Organization/{networkId}` references emitted by
  PractitionerRole are resolvable via `GET /fhir/r4/Organization/{networkId}`.
- [network-roster-api.md](network-roster-api.md) — operational roster API; capability 5.8 reuses the underlying repository query.
- [network-as-organization.md](network-as-organization.md) — Organization entity (capability 5.3) referenced by `PractitionerRole.organization`.
- [network-participation-backfill.md](network-participation-backfill.md) — panel-gating defaults (capability 5.5) surfaced by the panel-gating extension.
- [provider-versioning.md](provider-versioning.md) — version-state semantics that drive `PractitionerRole.active`.
- [fhir-conformance.md](fhir-conformance.md) — running conformance ledger.
