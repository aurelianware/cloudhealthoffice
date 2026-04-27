# Provider Adapter Pattern

## Why

Some tenants will eventually source their provider directory from external
core platforms (QNXT, Facets, HealthEdge) instead of Cloud Health Office's
internal `provider-service`. This pattern introduces a thin abstraction
(`IProviderAdapter`) selected at request time by tenant configuration,
mirroring the existing `IBenefitPlanAdapter` and `IEligibilityAdapter`
surfaces.

For every tenant currently in production the factory resolves to the
default `ChoProviderAdapter` (the Cloud Health Office implementation),
which is a near pass-through over the existing `IProviderRepository` —
so this PR introduces the seam without changing observable behavior.

## Topology

```
                ┌────────────────────────┐
GET /providers/* │   ProvidersController  │
                └────────────┬───────────┘
                             │ (every read request)
                     ┌───────▼───────┐
                     │   Factory     │  ── reads tenant cfg ──▶  tenant-service
                     └───────┬───────┘     (cached 5 min)
                             │
              ┌──────────────┼──────────────┬──────────────┐
              ▼              ▼              ▼              ▼
          Cho (active)    QNXT (stub)   Facets (stub)   HealthEdge (stub)
```

## Interface

```csharp
public interface IProviderAdapter
{
    string Platform { get; }
    Task<ProviderAdapterResponse>       GetProviderAsync(...);
    Task<ProviderAdapterResponse>       GetProviderByNpiAsync(...);
    Task<NetworkAdapterResponse>        GetNetworkAsync(...);          // placeholder — capability 5.3
    Task<ProviderRosterAdapterResponse> GetNetworkRosterAsync(...);
    Task<ProviderRosterAdapterResponse> SearchProvidersAsync(...);
}
```

The response envelopes carry a `Platform` string, an optional
`RawResponse` audit string, and a normalized payload (`AdapterProvider`,
`IReadOnlyList<AdapterProvider>`, or `AdapterNetwork`). Payload shape
mirrors the existing `Provider` so the Cloud Health Office pass-through is lossless, and
is structurally compatible with the planned FHIR `Practitioner` /
`Organization` projections (Sections 5.7–5.9 of the migration spec).

`GetNetworkAsync` is a deliberate **placeholder**: the Network entity
itself ships in capability 5.3. Until then every adapter throws
`NotImplementedException` from this method so any caller that reaches
for it before 5.3 lands fails loudly with a `TODO(provider-network-5.3)`
marker.

## Routing & Tenant Configuration

The factory consults tenant-service via HTTP at request time and reads:

```
GET /api/v1/tenants/{tenantId}
  → response.configuration.providerPlatform.platform        (string)
  → response.configuration.providerPlatform.platformSettings (object)
```

The matching adapter is selected case-insensitively by its `Platform`
property. Allowed values today:

| Platform string | Adapter                       | Status |
|-----------------|-------------------------------|--------|
| `cho`           | `ChoProviderAdapter`          | Active |
| `qnxt`          | `QnxtProviderAdapter`         | Stub — `NotImplementedException` |
| `facets`        | `FacetsProviderAdapter`       | Stub — `NotImplementedException` |
| `healthedge`    | `HealthEdgeProviderAdapter`   | Stub — `NotImplementedException` |

On any failure (HTTP error, JSON parse error, unknown platform) the factory
warns and falls back to `cho`. A per-tenant `(platform, settings)` tuple
is cached in the singleton `ProviderTenantConfigCache` for 5 minutes
(thread-safe via `ConcurrentDictionary`).

## DI lifetimes

```csharp
// Singleton — must outlive a single request so the TTL cache survives.
services.AddSingleton<ProviderTenantConfigCache>();

// Scoped — Cho wraps the scoped IProviderRepository.
services.AddScoped<IProviderAdapter, ChoProviderAdapter>();
services.AddScoped<IProviderAdapter, QnxtProviderAdapter>();
services.AddScoped<IProviderAdapter, FacetsProviderAdapter>();
services.AddScoped<IProviderAdapter, HealthEdgeProviderAdapter>();
services.AddScoped<ProviderAdapterFactory>();

services.AddHttpClient(ProviderTenantConfigCache.HttpClientName)
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
```

This matches the benefit-plan adapter wiring. The `ChoProviderAdapter`
has to be scoped because it composes the scoped `IProviderRepository`;
the cache lifecycle is kept separate so its TTL window is meaningful
across requests.

## Refactored controller endpoints

Only the read endpoints that map 1:1 onto the interface route through
the factory:

| Endpoint                                            | Adapter method            |
|-----------------------------------------------------|---------------------------|
| `GET /api/v1/providers/{id}`                        | `GetProviderAsync`        |
| `GET /api/v1/providers/npi/{npi}`                   | `GetProviderByNpiAsync`   |
| `GET /api/v1/providers/search`                      | `SearchProvidersAsync`    |
| `GET /api/v1/providers/list`                        | `SearchProvidersAsync`    |
| `GET /api/v1/providers/{id}/network-status`         | `GetProviderAsync` (then in-controller participation filter) |
| `GET /api/v1/providers/{id}/rates`                  | `GetProviderAsync` (then in-controller rate filter)          |

Both the canonical `/api/v1/providers` mount and the legacy `/api/Providers`
mount are covered (the controller is dual-routed).

All other endpoints — version-chain reads (`GET /{id}/versions`,
`GET /{id}/versions/{versionId}`), version lifecycle writes
(`POST /drafts`, `POST /amend`, `POST /{id}/versions/{versionId}/activate`,
`/suspend`, `/terminate`, `POST /{id}/reactivate`), legacy
`POST /api/Providers`, `PUT /{id}`, `DELETE /{id}`,
`POST /{id}/network-participations`, `PUT /{id}/credentialing`, and the
bank-account endpoints — keep calling `IProviderRepository` and
`IProviderVersioningService` directly. These are Cloud Health Office
internal write paths and chain reads; expanding adapter coverage to writes
is a future PR.

## Adding a new adapter

1. Implement `IProviderAdapter` with a unique `Platform` string.
2. Register `services.AddScoped<IProviderAdapter, MyAdapter>()` in
   `Program.cs` next to the others.
3. Add the platform string to the table above and to the tenant-service
   `ProviderConfig.Platform` enum / docs.
4. Configure tenants via `PUT /api/v1/tenants/{id}` with
   `configuration.providerPlatform.platform = "my-platform"`.
5. Write factory + adapter tests under
   `tests/CloudHealthOffice.ProviderService.Tests/Adapters/`.

## Migration TODOs

- **QNXT** (`TODO(qnxt-provider)`): integrate with the QNXT provider
  directory API (PROVIDER_INQ on the QNXT provider stack).
- **Facets** (`TODO(facets-provider)`): integrate with the Facets
  provider inquiry interface (Open Access XML or Workflow REST).
- **HealthEdge** (`TODO(healthedge-provider)`): integrate with the
  HealthRules Payer provider inquiry API (HRP REST surface).
- **Network entity** (`TODO(provider-network-5.3)`): every adapter's
  `GetNetworkAsync` throws today. Capability 5.3 introduces the Network
  model + repository and fills in this method on `ChoProviderAdapter`.
- **Verification surface** (capability 5.10): the integrity score and
  rating fields on `AdapterProvider` are populated from the cached
  values stored on `Provider` itself. The dedicated decoration seam that
  refreshes these from `ProviderVerificationOrchestrator` runs adjacent
  to the adapter — keeping the adapter contract orchestrator-free so
  vendor adapters get the same enrichment without each having to
  re-implement verification.

Until those land the stubs surface a clear `NotImplementedException`
containing the platform-specific TODO marker so any tenant misconfigured
to point at one of them fails loudly rather than silently degrading.

## FHIR alignment (Sections 5.7–5.9)

`AdapterProvider` was intentionally shaped so a future FHIR projection
layer can map it onto FHIR `Practitioner` (individual NPI Type 1) or
`Organization` (NPI Type 2) without a model redesign. Field-level mapping
notes:

| Adapter field                                | FHIR location                          |
|----------------------------------------------|----------------------------------------|
| `Npi`                                        | `identifier` (system NPI URI)          |
| `FirstName` / `MiddleName` / `LastName`      | `Practitioner.name`                    |
| `OrganizationName` / `DBAName`               | `Organization.name` / `Organization.alias` |
| `Address` / `City` / `State` / `ZipCode`     | `Practitioner.address` / `Organization.address` |
| `Phone` / `Email` / `Fax`                    | `telecom`                              |
| `TaxonomyCode` / `PrimarySpecialty`          | `Practitioner.qualification` (NUCC)    |
| `BoardCertifications[]`                      | `Practitioner.qualification` repeats   |
| `NetworkParticipations[]`                    | bound via FHIR `PractitionerRole`      |
| `IntegrityScore` / `IntegrityRating` / `LastVerifiedAt` | payer-specific verification extension |
| `VersionId` / `VersionNumber`                | `meta.versionId` / extension           |

The `ChoProviderAdapter` today returns the existing model unchanged via
the mapper; the QNXT/Facets/HealthEdge adapters will populate the same
shape when implemented.

## See also

- `docs/architecture/benefit-plan-adapter-pattern.md` — sibling pattern,
  same shape.
- `docs/architecture/provider-versioning.md` — version chain identity
  carried through every adapter response.
