# FHIR Organization projection (capability 5.9)

## What this is

Provider-service projects both a payer-defined **`Organization` network
entity** (capability 5.3) and a **`Provider` with
`ProviderType=Organization`** (a facility, clinic, or group practice) to
hand-built FHIR R4 Organization JsonObjects and exposes the projection at
`/fhir/Organization/{id}` and `/fhir/Organization` (search).
Fhir-service keeps the external `/fhir/r4/Organization/*` URL surface and
proxies those calls to provider-service via the same typed `ProviderService`
`HttpClient` established by capabilities 5.7 and 5.8.

The projection conforms to:

- US Core 6.1.0 Organization profile.
- Da Vinci PDex Plan-Net 1.1.0 Organization profile (for the fields CHO
  has data for today; see *Deferrals*).

## Why this PR (the HYBRID STATE is closed)

Before 5.9, fhir-service `ProviderDirectoryController` served
Organization from the NPPES API: NPI-2 lookup, arbitrary public NPI
registry, no tenant context. That carried three problems:

1. **No CHO network entity.** The Network-as-first-class-Organization
   work (capability 5.3 / 5.4) exists only in provider-service. NPPES
   has no concept of a payer's contracted network.
2. **No provider-organization CHO data.** Facilities, clinics, and group
   practices enrolled in CHO's directory via `Provider` with
   `ProviderType=Organization` were invisible to the FHIR Organization
   endpoint.
3. **Universe scope.** NPPES covers all registered US providers, not
   just CHO-enrolled providers. This was the wrong scope for a payer
   directory.

5.9 inverts the flow for Organization exactly as 5.7 did for Practitioner
and 5.8 did for PractitionerRole:

```
external client
   │
   ▼
fhir-service /fhir/r4/Organization/{id}      ← URL stability
   │
   ▼  HTTP, "ProviderService" typed HttpClient
provider-service /fhir/Organization/{id}     ← projection authority
   │
   ▼
FhirOrganizationProjector
   │
   ├─ IProviderRepository.GetByNPIAsync / SearchAsync
   │   (for Provider with ProviderType=Organization)
   └─ IOrganizationRepository.GetByIdAsync / ListAsync
       (for Organization network entity)
```

After 5.9 ships, the HYBRID STATE comment block in
`ProviderDirectoryController` is removed. Practitioner, PractitionerRole,
and Organization are all CHO-canonical. Location remains NPPES (separate
concern; Plan-Net treats Location as a distinct resource backed by its own
data source, out of scope here).

## Two source entities, one FHIR resource type

This is the load-bearing design distinction for 5.9.

| Source entity | What it is | FHIR `type` code | FHIR `id` |
|---|---|---|---|
| `Organization` (capability 5.3) | Payer-defined network | `ins` | `OrganizationId` (chain key) |
| `Provider` with `ProviderType=Organization` | Facility / clinic / group practice | `prov` | NPI (NPI-2, 10 digits) |

FHIR consumers that care about the distinction can inspect the `type`
coding. Consumers that don't can treat all results uniformly as
Organization resources.

## Id-resolution strategy (Decision 6)

`GET /fhir/Organization/{id}` uses shape-based discrimination:

- **10-digit numeric string** → interpreted as NPI-2 → look up `Provider`
  with `ProviderType=Organization` whose `NPI` matches. If found and
  Active, project as `type=prov`. If not found, return 404.
- **Anything else** → interpreted as OrganizationId chain key → look up
  `Organization` entity. If found and Active, project as `type=ins`.
  If not found, return 404.

**NPI wins on 10-digit input.** If a tenant has authored an
OrganizationId that happens to be 10 digits, the NPI path runs first and
the OrganizationId is unreachable via the read endpoint. This is a
configuration edge case (OrganizationId defaults to GUID-shaped, which is
never 10 digits) and is documented in `FhirOrganizationController` XML
docs so operators can plan accordingly.

## Search semantics (Decision 7 — Option 7a)

`GET /fhir/Organization` uses a single endpoint, parameter-discriminated:

| Parameter | Source entity searched |
|---|---|
| `?npi={10-digit}` | Provider-as-Org exact NPI match |
| `?identifier=ORG:{orgId}` | Organization entity chain-key lookup |
| `?identifier=urn:cho:network\|{orgId}` | Organization entity (FHIR system\|value form) |
| `?name=...`, `?city=...`, `?state=...`, `?postal-code=...` | Both; results merged in Bundle |
| `?type=prov` | Provider-as-Org only |
| `?type=ins` | Organization entity only |

**Why merged results are correct:** FHIR consumers expect to query by
`name` or address and get all matching resources of that type, regardless
of internal subdivision. The `type` field in each Bundle entry
discriminates for consumers that need it.

## Field mapping

### `Organization` network entity → FHIR Organization (type=ins)

| FHIR field | CHO source |
|---|---|
| `id` | `OrganizationId` (chain key) |
| `active` | `VersionState == Active` |
| `type[0].coding[0].code` | `"ins"` |
| `name` | `Organization.Name` |
| `identifier[]` | `Organization.Identifiers` list (system/value/type/use passthrough) |
| `telecom[]` | `ContactInfo.Phone`, `.Email`, `.Fax` |
| `address[]` | `ContactInfo.Address/City/State/ZipCode` |
| `contact[]` | `ContactInfo.PrimaryContactName` + telecom + address |
| `partOf.reference` | `"Organization/{ParentOrganizationId}"` (when non-null) |
| `meta.lastUpdated` | `LastUpdatedDate` |
| `meta.profile[]` | US Core 6.1.0 + Plan-Net 1.1.0 Organization profile URLs |

### `Provider` with `ProviderType=Organization` → FHIR Organization (type=prov)

| FHIR field | CHO source |
|---|---|
| `id` | `Provider.NPI` |
| `active` | `VersionState == Active && Status == Active` |
| `type[0].coding[0].code` | `"prov"` |
| `name` | `Provider.OrganizationName` |
| `alias[]` | `Provider.DBAName` (when present; Decision 11) |
| `identifier[0]` | NPI — system `http://hl7.org/fhir/sid/us-npi` |
| `identifier[1]` | TaxId (EIN) — system `urn:oid:2.16.840.1.113883.4.4` (when present) |
| `telecom[]` | `Provider.Phone`, `.Email`, `.Fax` |
| `address[]` | `Provider.Address/City/State/ZipCode` |
| `meta.lastUpdated` | `LastUpdatedDate` |
| `meta.profile[]` | US Core 6.1.0 + Plan-Net 1.1.0 Organization profile URLs |

## Null-return contract

`IFhirOrganizationProjector.Project(...)` returns `null` when the input
is not projectable. Callers map null to FHIR 404 OperationOutcome (read
path) or skip the row (search).

| Case | Projector path affected |
|---|---|
| `provider.ProviderType == Individual` | Provider path |
| `provider.VersionState != Active` | Provider path |
| `provider.Status != Active` | Provider path |
| `provider.OrganizationName` is null/empty | Provider path |
| `network.VersionState != Active` | Network path |
| `network.Name` is null/empty | Network path |

## Integration with prior capabilities

### 5.8 (PractitionerRoleProjector)

PractitionerRole's `organization` field emits
`Organization/{networkId}`. After 5.9, the reference is resolvable:
`GET /fhir/r4/Organization/{networkId}` returns the network entity. The
PractitionerRole projector's reference emission was already correct; 5.9
just makes the referenced resource available.

### 5.7 (PractitionerProjector)

No change to Practitioner. No Organization field on Practitioner itself.
Consumers navigate `Practitioner → PractitionerRole → Organization` via
standard FHIR reference traversal.

### 5.3 (Organization entity versioning)

The projector reads only the head Active version. Earlier versions are
queryable internally but not surfaced via FHIR (consistent with Practitioner
treatment). Terminated Organizations (`VersionState == Terminated`) return
null → 404 on the read path; they are excluded from search results.

### 5.4 (Network Roster)

Roster API and FHIR Organization endpoint are complementary: roster lists
the providers in a network; the FHIR Organization endpoint describes the
network itself.

## Deferrals (capability 5.17 territory)

The following Plan-Net IG 1.1.0 Organization extensions are not emitted
today because CHO does not have the source data:

- `org-description` — free-text organization description
- `qualification` — organizational qualification/accreditation
- `insurancePlan` — reference to InsurancePlan resources
- `endpoint` — FHIR endpoint (Phase 2, Plan-Net publishing)
- Language-of-service, accessibility, populations-served at org level

These are tracked under capability 5.17.

## Behavioral change vs. prior state

**Before 5.9:** `GET /fhir/r4/Organization/{npi}` and
`GET /fhir/r4/Organization?npi=...` served arbitrary NPI-2 records from
the global NPPES registry regardless of CHO enrollment.

**After 5.9:** The same URLs serve only CHO-enrolled organizations (those
in the provider-service `Organization` or `Provider` collections). An NPI-2
registered in NPPES but not enrolled in a CHO tenant is invisible.

This is documented in the PR description. The NPPES NPI-lookup surface
(public-tools path) remains available for full-registry queries; that use
case is distinct from the CHO provider directory.

## Cross-references

- [fhir-practitioner-projection.md](fhir-practitioner-projection.md) — capability 5.7 details.
- [fhir-practitionerrole-projection.md](fhir-practitionerrole-projection.md) — capability 5.8 details.
- [network-as-organization.md](network-as-organization.md) — Organization entity (capability 5.3).
- [provider-versioning.md](provider-versioning.md) — version-state semantics.
- [fhir-conformance.md](fhir-conformance.md) — running conformance posture ledger.
