# Network as First-Class Organization (Capability 5.3)

## Why

Until now, the only structure resembling a payer network in CHO was a list
of `NetworkParticipation` records hanging off each `Provider`. That worked
for "is provider X in plan Y?" lookups but meant the network itself had no
identity, no metadata, no lifecycle, and no way to describe a hierarchy
(parent ⇄ sub-network) — all of which are first-class concepts in FHIR R4
`Organization`, in benefit-plan tier definitions, and in how payers
actually shape their products.

This capability introduces a real `Organization` entity in `provider-service`
that represents a payer-defined network. It is **distinct** from a
`Provider` whose `ProviderType=Organization` (which represents a single
facility — hospital, clinic, group practice). The two co-exist:

- `Provider` (`ProviderType=Organization`) — *one facility*.
- `Organization` (this doc) — *one payer-defined network*.

Downstream services reference networks by `OrganizationId`, the stable
chain key, once they need network-tier semantics (capability 5.5) or FHIR
`Organization` projections (capabilities 5.7+). The per-version `Id` is
internal to the version chain and is never used as a cross-version
reference.

## Topology

```
                         ┌─────────────────────────────┐
GET /api/v1/networks*    │      NetworksController      │
                         └──────────────┬───────────────┘
                              writes ↓        ↑ reads
                         ┌──────────────────────────────┐
                         │     IOrganizationService     │
                         └──────────────┬───────────────┘
                                        │
                ┌───────────────────────┴───────────────────────┐
                ▼                                               ▼
        IOrganizationRepository              OrganizationAdapterFactory
        (Cosmos | Mongo)                          │
                                ┌─────────────────┼─────────────────┐
                                ▼                 ▼                 ▼
                         Cho (active)        QNXT (stub)       Facets (stub)
```

Reads route through `IOrganizationAdapter` so a tenant whose network
catalog lives in QNXT or Facets can plug in a real adapter later without
controller changes. Writes always go through `IOrganizationService` /
`IOrganizationRepository` for the CHO-owned data store; vendor-platform
write paths are intentionally out of scope for this capability.

## Entity Shape

`Organization` is FHIR-aligned:

| Field | Type | Notes |
|-------|------|-------|
| `TenantId` | string | Cosmos partition key. |
| `Id` | string | Per-version document id. |
| `OrganizationId` | string | Stable chain key (preserved across versions). |
| `Name` | string | Human-readable network name. |
| `NetworkType` | enum | PPO / HMO / EPO / POS / Indemnity / Custom (Unknown=0 default). |
| `LineOfBusiness` | enum | Preserved from `Provider.NetworkParticipation.LineOfBusiness`. |
| `ParentOrganizationId` | string? | partOf hierarchy → FHIR `Organization.partOf`. |
| `Identifiers` | list | External identifiers (TAX, PRN, NIIP, ...). |
| `EffectiveDate` / `TerminationDate` | datetime | Period of validity. |
| `ContactInfo` | object | Admin contact + address. |
| `Status` | enum | Operational status. |
| Version-chain fields | various | `VersionId` / `VersionNumber` / `VersionState` / `PredecessorVersionId` / `Activated*` / `Suspended*` / `Superseded*`. |

The version-chain semantics mirror **capability 5.1** (Provider Identity &
Versioning): every row is one immutable version, the chain is keyed on
`(TenantId, OrganizationId)`, and default reads resolve to the latest
non-Draft head.

### Enum Defaults — PR #705

`NetworkType` follows the PR #705 enum-handling pattern:

```csharp
public enum NetworkType
{
    Unknown = 0,   // safe default for hydrated documents that predate the field
    PPO = 1,
    HMO = 2,
    EPO = 3,
    POS = 4,
    Indemnity = 5,
    Custom = 99
}
```

Every value is explicitly numbered and `Unknown=0` is the default.
String-only enforcement (rejecting integer enum payloads) is delegated
to the shared MVC JSON options registered by
`AddCloudHealthOfficeJsonOptions`, which constructs a
`JsonStringEnumConverter(allowIntegerValues: false)`. The new enums in
this capability deliberately do **not** carry a type-level
`[JsonConverter(typeof(JsonStringEnumConverter))]` attribute, because
that constructor defaults to `allowIntegerValues: true` and would
override the strict global converter. `OrganizationStatus` and
`OrganizationVersionState` follow the same pattern.

## REST Surface

All routes live under `/api/v1/networks` and require the standard tenant
middleware to populate `HttpContext.Items["TenantId"]`.

| Verb | Path | Purpose |
|------|------|---------|
| `GET` | `/api/v1/networks` | Paginated list. Query filters: `networkType`, `lineOfBusiness`, `parentOrganizationId`, `page`, `pageSize`. |
| `GET` | `/api/v1/networks/{id}` | Fetch a single network's current head. |
| `GET` | `/api/v1/networks/{id}/children` | List children of a parent network (partOf traversal). |
| `POST` | `/api/v1/networks` | Create + activate v1. |
| `PUT` | `/api/v1/networks/{id}` | Amend: clones the head into a new Active version, supersedes the prior. |
| `DELETE` | `/api/v1/networks/{id}` | Soft-delete via `Terminate` transition on the head. |

`PUT` always advances the version chain — it never mutates an Active row
in place. `DELETE` does not remove documents; it flips the head to
`Terminated` and preserves the historical chain.

**PUT is RESTful full-replacement.** Callers must submit the full network
body on every update; any field omitted from the request becomes its
default on the new version (`Identifiers` → empty list, `ContactInfo` →
null, etc.). Partial-update semantics are intentionally out of scope for
this capability — they would require a separate `PATCH` endpoint with
explicit "fields the client touched" tracking.

## partOf Hierarchy

`ParentOrganizationId` is optional and references another network's
`OrganizationId` (the chain key, not a per-version `Id`). Use cases:

- A regional HMO sub-network (`HMO Florida North`) referencing its parent
  product (`Aetna HMO Florida 2025`).
- Tier groupings inside a tiered PPO.

The repository ships an indexed `GetByParentAsync` that pages through
children of a given parent, and `NetworksController.GetChildren` exposes
that as `GET /api/v1/networks/{id}/children`.

## Adapter Pattern

`IOrganizationAdapter` mirrors `IProviderAdapter`:

```csharp
public interface IOrganizationAdapter
{
    string Platform { get; }
    Task<OrganizationAdapterResponse>     GetOrganizationAsync(...);
    Task<OrganizationListAdapterResponse> GetByParentAsync(...);
    Task<OrganizationListAdapterResponse> ListAsync(...);
}
```

Selection happens via `OrganizationAdapterFactory`, which **reuses
`ProviderTenantConfigCache`** — networks live in `provider-service` and
share the same `providerPlatform` block in tenant config:

```
GET /api/v1/tenants/{tenantId}
  → response.configuration.providerPlatform.platform        (string)
  → response.configuration.providerPlatform.platformSettings (object)
```

Sharing the cache keeps the TTL semantics (5 minutes) consistent across
provider and organization reads — one cache miss, two adapter selections.

| Platform | Adapter | Status |
|----------|---------|--------|
| `cho` | `ChoOrganizationAdapter` | Active (default). |
| `qnxt` | `QnxtOrganizationAdapter` | Stub — `NotImplementedException` with `TODO(qnxt-organization)`. |
| `facets` | `FacetsOrganizationAdapter` | Stub — `NotImplementedException` with `TODO(facets-organization)`. |

On any failure (HTTP error, JSON parse, unknown platform) the factory
falls back to `cho`.

## Storage

The `Organizations` collection lives alongside `Providers` in the same
database (`ProviderDB` / `CloudHealthOffice` for Mongo). Mongo indexes:

- `(TenantId, OrganizationId, VersionNumber)` — chain head lookup.
- `(TenantId, OrganizationId, VersionId)` — exact-version fetch.
- `(TenantId, NetworkType)` / `(TenantId, LineOfBusiness)` — list filters.
- `(TenantId, ParentOrganizationId)` — partOf traversal.
- `(TenantId, VersionState)` — head/draft separation.

Cosmos uses `TenantId` as the partition key; queries are scoped to the
partition for tenant isolation.

## Hand-off to benefit-plan-service (Capability 5.5)

Once this PR merges, `benefit-plan-service` capability 5.5 (Network Tiers
+ FHIR Organization) is unblocked:

1. `BenefitPlan.NetworkTier` records reference `Organization` by
   `OrganizationId` (chain key).
2. `benefit-plan-service` resolves the network through a new
   `IOrganizationAdapter` consumer that lives in `benefit-plan-service`
   and calls `provider-service` over HTTP — no direct DB coupling.
3. FHIR projections (`5.7+`) render `Organization.partOf` directly from
   `ParentOrganizationId` and use the `Identifiers` list as
   `Organization.identifier`.
