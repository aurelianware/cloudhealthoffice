# Network Roster API (Capability 5.4)

## Why

Capability 5.3 introduced `Organization` as the first-class network entity
(see `network-as-organization.md`). 5.4 turns that entity into something
operationally useful: **for a given network, return the providers that
participate in it**, with the filters payer ops actually use day-to-day.

Until now there was no roster endpoint. Provider directory lookups went
through `GET /api/v1/providers/search` with a `planId` filter, which
worked only when callers happened to know plan IDs. The new endpoint
operates at the network level — the same level callers reason about
when they ask "who's in this product?".

## Endpoint

```
GET /api/v1/networks/{id}/roster
```

`{id}` is the chain key (`Organization.OrganizationId`) — never a
per-version `Id`.

### Query parameters

| Name | Type | Notes |
|------|------|-------|
| `lineOfBusiness` | enum | Filters participations matching this LOB. |
| `specialty` | string | NUCC taxonomy code or specialty substring. Case-insensitive match against `PrimarySpecialty` and `TaxonomyCode`. |
| `tier` | string | Network tier exact match (e.g. `Tier1`). |
| `acceptingNewPatients` | bool | Filters both the provider flag and the matched participation. |
| `asOfDate` | datetime | Snapshot date. Defaults to `DateTime.UtcNow`. |
| `page` | int | 1-based. Used only when `cursor` is null. |
| `pageSize` | int | Defaults to **100**, hard cap **1000** (`Math.Clamp`). |
| `sortBy` | string | `name` (default) or `integrityScore`. `distance` is reserved — see "Deferred — distance sort". |
| `sortDirection` | string | `asc` or `desc`. Default depends on `sortBy`. |
| `cursor` | string | Opaque pagination token from a prior response's `nextCursor`. Overrides `page`. |

Filters AND-combine. `asOfDate` applies to both the provider chain (the
matched row must have `(TerminationDate is null OR TerminationDate >= asOf)`)
and the matching participation (`EffectiveDate <= asOf` AND
`(TerminationDate is null OR TerminationDate >= asOf)`).

### Response shape

```jsonc
{
  "items": [
    {
      "providerId": "p-...",
      "versionId": "...",
      "provider": {
        "npi": "1234567890",
        "providerType": "Individual",
        "displayName": "Test Adams MD",
        "primarySpecialty": "207R00000X",
        "taxonomyCode": "207R00000X",
        "address": "...",
        "city": "...",
        "state": "FL",
        "zipCode": "33101",
        "acceptingNewPatients": true
      },
      "participation": {
        "planId": "...",
        "lineOfBusiness": "Medicare",
        "networkTier": "Tier1",
        "acceptingNewPatients": true,
        "effectiveDate": "2024-01-01T00:00:00Z",
        "terminationDate": null,
        "panelGating": {
          "panelLimit": 1500,
          "panelAccepted": false,
          "acceptedLobs": ["Medicare"],
          "minAcceptedAgeYears": 18,
          "maxAcceptedAgeYears": 64
        }
      },
      "integrityScore": {
        "score": 88,
        "rating": "Clear",
        "lastVerifiedAt": "2025-01-01T00:00:00Z"
      }
    }
  ],
  "nextCursor": "eyJPZmZ...",
  "pageSize": 100
}
```

### Status codes

| Code | When |
|------|------|
| `200` | Success. |
| `400` | Cursor invalid / cursor filter mismatch / unsupported `sortBy` / `sortBy=distance`. Body is `{ error, message }`. |
| `404` | Network does not exist in the caller's tenant. |

## Tenant scope

Roster results only include providers in the same tenant as the network.
Two layers enforce this:

1. The controller asserts `network = OrganizationService.GetByIdAsync(id)`
   under the request's `TenantId` — a foreign tenant's network 404s
   before the provider collection is touched.
2. `IProviderRepository.ListNetworkRosterAsync` always passes
   `TenantId = query.TenantId` into every backend query (Cosmos partition
   key, Mongo filter equality).

## Linkage: how a provider appears in a roster

A provider participates in a network when they have at least one
`NetworkParticipation` row with `NetworkId == OrganizationId`. The
`NetworkId` field was added in this capability; legacy participations
written before 5.4 carry no link to the `Organization` chain.

> **Migration semantic.** Legacy participations without `NetworkId` are
> **invisible to `GET /api/v1/networks/{id}/roster` by design** — this is
> the expected behavior, not a bug. Plan-level lookups
> (`GET /api/v1/providers/search?planId=...`) keep working unchanged.
> The migration path is **per-tenant backfill**: as `Organization` rows
> are authored, the responsible team (network ops) populates
> `NetworkParticipation.NetworkId` against existing provider records.
> No automated backfill ships in this PR — that's a separate task driven
> by tenant onboarding readiness.

## Sort orders

| `sortBy` | Direction | Behaviour |
|----------|-----------|-----------|
| `name` | `asc` (default) / `desc` | Sorts by `lastName`, then `organizationName`, then `id` for stability. |
| `integrityScore` | `desc` (folded) | Highest score first. **Nulls last.** Any explicit direction is folded to `desc` (ascending makes no operational sense). |
| `distance` | — | **Deferred** — see below. Returns 400 with `error=distance_sort_unsupported`. |

### Nulls-last for `integrityScore`

Cosmos and Mongo disagree on null sort order on descending. The
repository emits a deterministic backend-native order; the service then
reorders the page-sized slice to push providers with no
`IntegrityScore` to the tail. Implemented in
`NetworkRosterService.ApplyNullsLastForIntegrityScore`.

## Pagination cursor

Cursor is **opaque** and URL-safe base64 of:

```jsonc
{ "Offset": 100, "AsOfDate": "...", "FilterHash": "ab12..." }
```

**Filter-hash binding.** `FilterHash` is the SHA-256/128-bit hex of the
canonicalized filter set + sort. Reusing a cursor with mutated filters
returns 400 (`cursor_filter_mismatch`) — callers must restart from page
1 when filters change.

`AsOfDate` is locked into the cursor on first decode so re-paging
doesn't drift if the wall clock advances between pages.

A short page (items count < `pageSize`) implies the result set is
exhausted; `nextCursor` is null. We don't peek ahead — the price is one
spurious empty round-trip when the total is exactly a multiple of
`pageSize`. Trade-off documented; revisit if it shows up in metrics.

## Performance

Target: **P95 < 400 ms** for standard pagination (100-row page,
single-network filter, partition-scoped query).

Backing this:

- Cosmos: queries are partition-scoped on `TenantId`. The
  `EXISTS(... networkParticipations WHERE n.networkId = @networkId)`
  pattern matches the existing `c.networkParticipations` array shape
  used by `SearchAsync` and is multikey-covered.
- Mongo: a new compound index
  `(TenantId, NetworkParticipations.NetworkId)` is provisioned by
  `ProviderRepositoryMongo.CreateIndexes()`. The common
  `(network + tier)` combo is covered by an additional
  `(TenantId, NetworkParticipations.NetworkId, NetworkParticipations.NetworkTier)`
  index.

The roster path **never** invokes
`ProviderVerificationOrchestrator`. `IntegrityScore` is read directly
from the cached column on the Provider row. This satisfies the "no
real-time multi-source call on the roster query path" constraint.

## Verification write-back — resolved by capability 5.4.5

`Provider.IntegrityScore`, `IntegrityRating`, `LastVerifiedAt`, and
`NextVerificationDue` are now populated by capability 5.4.5
(`docs/architecture/verification-writeback.md`).

`IntegrityProjectionWorker` (a `BackgroundService` in provider-service)
sweeps due providers per tenant on a schedule, calls
`provider-verification-service` over HTTP via
`POST /api/v1/providers/verify/batch`, and patches the four projection
fields onto the head Active row via
`IProviderRepository.UpdateIntegrityProjectionAsync`.

For roster operators:

- After 5.4.5 ships and the per-tenant backfill runs (admin endpoint
  `POST /api/v1/admin/providers/backfill-integrity-projection?tenantId=X`),
  the `integrityScore` envelope on each roster row reflects the
  cached projection from the most recent verification. No change to
  roster code or schema.
- The nulls-last sort (`NetworkRosterService.ApplyNullsLastForIntegrityScore`)
  continues to work correctly during the backfill window — partially
  populated tenants will have some null and some populated rows, and
  nulls-last sort handles both cleanly.
- Live HTTP fetch via `HttpProviderIntegrityGate` in
  `benefit-plan-service` migrated to the cached-or-live pattern in
  capability 5.10 — see `docs/architecture/integrity-score-consumption.md`
  for the canonical decision tree. The roster path was already on the
  canonical cached-projection path; 5.10's migration affects only the
  adjudication critical path, leaving roster behavior unchanged.

## Deferred — distance sort

`sortBy=distance` is reserved but **not yet supported**. The roster
endpoint returns `400 distance_sort_unsupported` when called.

Reason: a meaningful "distance" sort needs a geospatial index on
provider addresses (`Cosmos geospatial query` / `Mongo 2dsphere`). An
in-memory implementation that pulls a candidate window and sorts
client-side has scaling limits (large networks won't fit the window)
and correctness gaps (the ordering is wrong as soon as the window
truncates results below the desired cut-off).

Follow-up plan:

1. Add `lat`/`lng` (or geocoded `nearZip`) on `NetworkRosterQuery`.
2. Add a `GeoPoint` projection on `Provider` (lat/lng for the practice
   address), populated by the address-validation pipeline.
3. Provision Cosmos geospatial index / Mongo 2dsphere on that field.
4. Extend `ListNetworkRosterAsync` to accept a geo predicate and emit a
   distance projection on each row (`DistanceMiles`).
5. Lift the `400 distance_sort_unsupported` guard.

Tracked separately to keep this PR focused on the roster surface.

## Storage indices

Mongo (added in this PR):

```
(TenantId, NetworkParticipations.NetworkId)
(TenantId, NetworkParticipations.NetworkId, NetworkParticipations.NetworkTier)
```

Cosmos: relies on the default container indexing policy (full indexing
on all properties) plus partition-scoped queries. No new index
provisioning required.

## Testing

Coverage in `tests/CloudHealthOffice.ProviderService.Tests/Services`:

- `NetworkRosterServiceTests` — paging across cursor pages, filter
  composition (LOB + specialty + acceptingNewPatients), `asOfDate`
  edges, tenant isolation, sort orders, page-size clamping, panel
  gating projection, integrity envelope projection, terminated chain
  exclusion, multi-participation row selection, cursor-mismatch
  rejection, invalid-cursor handling.
- `NetworkRosterCursorTests` — filter-hash stability + divergence,
  `ResolveSort` table, nulls-last reordering helper.

The in-memory `InMemoryProviderRepository` implements the same contract
(`ListNetworkRosterAsync`) so tests run without spinning Cosmos or
Mongo.

## Capability hand-off

- benefit-plan-service can now render network rosters under a
  `BenefitPlan.NetworkTier` reference (capability 5.5) by joining the
  tier's `NetworkId` against this endpoint.
- FHIR projections (5.7+) can render `PractitionerRole` collections
  scoped to a network using the same query path. Capability 5.8 wires
  this connection: `GET /fhir/PractitionerRole?organization=Organization/{networkId}`
  reuses `IProviderRepository.ListNetworkRosterAsync` (the same query
  this endpoint drives) and projects each matching participation to a
  FHIR PractitionerRole. The two surfaces are equivalent in source data
  and complementary in shape: roster is the operational summary
  (`NetworkRosterEntry`) for admin tooling; PractitionerRole is the
  FHIR-canonical projection for external consumers. See
  [fhir-practitionerrole-projection.md](fhir-practitionerrole-projection.md).
