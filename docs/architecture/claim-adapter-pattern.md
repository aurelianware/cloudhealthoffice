# Claim Adapter Pattern

## Why

Some tenants will eventually source their claims from external core
platforms (QNXT, Facets, HealthEdge) instead of CHO's internal
`claims-service`. This pattern introduces a thin abstraction
(`IClaimAdapter`) selected at request time by tenant configuration,
mirroring the existing `IProviderAdapter` and `IBenefitPlanAdapter`
seams.

For every tenant currently in production the factory resolves to the
default `ChoClaimAdapter`, which is a near pass-through over the
existing `IClaimRepository`. So this PR introduces the seam without
changing observable behavior — capability 5.3 (Submission API) is
the first capability that wires controllers through the adapter.

## Topology

```
                  ┌─────────────────────────┐
   POST /claims   │ ClaimsController        │  GET /claims/{id}
   GET  /claims   │ ClaimsV1Controller      │  POST /claims/search
                  └────────────┬────────────┘
                               │ (every request)
                       ┌───────▼────────┐
                       │ Factory        │  ── reads tenant cfg ──▶ tenant-service
                       └───────┬────────┘     (cached 5 min)
                               │
            ┌──────────────────┼──────────────────┬───────────────────┐
            ▼                  ▼                  ▼                   ▼
        Cho (active)        QNXT (stub)       Facets (stub)      HealthEdge (stub)
```

## Interface

```csharp
public interface IClaimAdapter
{
    string Platform { get; }

    Task<ClaimAdapterResponse>            GetClaimAsync(...);
    Task<ClaimAdapterResponse>            GetClaimByNumberAsync(...);
    Task<ClaimAdapterResponse>            GetClaimVersionAsync(...);
    Task<ClaimVersionListAdapterResponse> ListClaimVersionsAsync(...);
    Task<ClaimAdapterResponse>            SubmitClaimAsync(...);
    Task<ClaimSearchAdapterResponse>      SearchClaimsAsync(...);
    Task<ClaimSearchAdapterResponse>      SearchClaimsForMemberAsync(...);
}
```

Every response envelope carries a `Platform` string, an optional
`RawResponse` audit field, and the normalized payload. Payload shape
uses vendor-neutral DTOs — `AdapterClaim`, `AdapterClaimLine`,
`AdapterDiagnosisCode`, `AdapterAdjudicationResult`, and
`AdapterLineAdjudicationResult` — that mirror their domain
counterparts field-for-field with `From(...)` / `To*()` round-trip
mappers. This matches `AdapterProvider` and `AdapterBenefitPlan` from
the sibling services.

`ListClaimVersionsAsync` is the one capability not present on the
provider or benefit-plan adapters; claims chains routinely produce
many versions per chain (submission → adjudication → adjustment →
reversal), and capabilities 5.11 (FHIR `Bundle` of all
`ClaimResponse` versions) and 5.12 (Adjustment Workflow chain
visualization) need vendor-neutral access to the full version list.

## What stays off the adapter

Three CHO-internal surfaces are deliberately not on the adapter
interface:

| Surface | Why it stays off the adapter |
|---|---|
| `IClaimRepository.UpdateAdjudicationProjectionAsync` | Projection-metadata bypass for adjudication writes. The 5.5 orchestrator writes adjudication state via this CHO-internal seam; vendor systems own their own adjudication state, so the same surface doesn't generalize. |
| `IClaimVersionEventPublisher` | Mongo append-only system-of-record audit chain (5.1a). Claims version events are CHO-internal; a vendor system has its own audit equivalent. |
| `IClaimEventPublisher` (Kafka) | Operational event stream. Same logic — CHO-internal. |
| `GetClaimsSummaryAsync`, `GetAccumulatorTotalsAsync` | Operational and accumulator-service boundaries. Stays on `IClaimRepository`; consumers hit the repo directly until/unless a vendor surface needs the equivalent. |

## Routing & tenant configuration

The factory consults tenant-service via HTTP and reads:

```
GET /api/v1/tenants/{tenantId}
  → response.configuration.claimsPlatform.platform        (string)
  → response.configuration.claimsPlatform.platformSettings (object)
```

The matching adapter is selected case-insensitively by its `Platform`
property. Allowed values today:

| Platform string | Adapter                 | Status                                |
|-----------------|-------------------------|---------------------------------------|
| `cho`           | `ChoClaimAdapter`       | Active                                |
| `qnxt`          | `QnxtClaimAdapter`      | Stub — `NotImplementedException`      |
| `facets`        | `FacetsClaimAdapter`    | Stub — `NotImplementedException`      |
| `healthedge`    | `HealthEdgeClaimAdapter`| Stub — `NotImplementedException`      |

On any failure (HTTP error, JSON parse error, unknown platform) the
factory warns and falls back to `cho`. A per-tenant
`(platform, settings)` tuple is cached in the singleton
`ClaimTenantConfigCache` for 5 minutes (thread-safe via
`ConcurrentDictionary`).

The `claimsPlatform` field is additive on the tenant-service
configuration document — existing tenant docs without the field
default to `cho`. No tenant-service code change is required for this
capability.

## DI lifetimes

```csharp
// Singleton — must outlive a single request so the TTL cache survives.
services.AddSingleton<ClaimTenantConfigCache>();

// Scoped — Cho wraps the scoped IClaimRepository.
services.AddScoped<IClaimAdapter, ChoClaimAdapter>();
services.AddScoped<IClaimAdapter, QnxtClaimAdapter>();
services.AddScoped<IClaimAdapter, FacetsClaimAdapter>();
services.AddScoped<IClaimAdapter, HealthEdgeClaimAdapter>();
services.AddScoped<ClaimAdapterFactory>();

services.AddHttpClient(ClaimTenantConfigCache.HttpClientName)
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
```

Identical lifetime mix to `provider-service` and `benefit-plan-service`.
The Singleton cache only consumes other singletons, and the Scoped
factory consuming the Singleton cache is the established direction
(no DI validation issues).

## Coexistence with `IClaimRepository`

5.2 ships the adapter alongside the repository. Both abstractions
remain in use after this capability:

- `IClaimRepository` — direct data access; controllers continue to
  inject it directly; CHO-internal services (5.5 orchestrator, 5.10
  remittance, 5.12 adjustment workflow) consume it on the write path.
- `IClaimAdapter` — vendor-neutral abstraction; capability 5.3 wires
  the V1 submission controller through the factory; capabilities 5.11
  (FHIR projection) and beyond consume it for vendor-neutral reads.

Submission writes via `SubmitClaimAsync` delegate straight to
`CreateAsync`, which already initializes the version chain
(`ClaimVersionId=Id`, `VersionNumber=1`,
`VersionState=Submitted`) per capability 5.1a. The adapter does not
emit version events itself — that wiring is the submission service's
concern in capability 5.3.

## Adding a new adapter

1. Create `<Vendor>ClaimAdapter` in `src/services/claims-service/Adapters/`,
   implementing `IClaimAdapter` with a constant `Platform` string
   (lowercase, no hyphens).
2. Translate vendor-specific request/response shapes to/from the
   `AdapterClaim` DTO and the seven response envelopes.
3. Register the adapter in `Program.cs` as
   `AddScoped<IClaimAdapter, <Vendor>ClaimAdapter>()`.
4. Update the tenant `claimsPlatform.platform` config to the new
   value for any tenant that should use it.

## Testing

The adapter tests live at `tests/CloudHealthOffice.ClaimsService.Tests/Adapters/`:

- `ClaimAdapterFactoryTests` — tenant routing, case-insensitive
  match, fallback on HTTP failure, fallback on unknown platform,
  cache TTL, settings isolation.
- `ChoClaimAdapterTests` — repository delegation for each method,
  including a `SubmitClaimAsync` test that explicitly verifies the
  `AdapterClaim` round-trip is lossless on the submission path.
- `StubClaimAdapterTests` — every vendor stub method throws
  `NotImplementedException` carrying the migration TODO and the doc
  reference.
- `ClaimTenantConfigCacheTests` — cache hit/miss, JSON shape, HTTP
  failure fallback, defensive URL encoding for tenant ids.

## Cross-references

- `docs/architecture/provider-adapter-pattern.md` — sibling pattern
  for provider directory backends.
- `docs/architecture/benefit-plan-adapter-pattern.md` — sibling
  pattern for benefit-plan retrieval.
- `docs/architecture/claim-versioning.md` — versioning scaffolding
  (5.1a) the adapter relies on for `ClaimVersionId`, `VersionState`,
  `GetLatestVersionAsync`, `GetVersionAsync`, `ListVersionsAsync`.
