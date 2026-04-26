# Benefit Plan Adapter Pattern

## Why

Some tenants will eventually source their benefit plan data from external
core platforms (QNXT, Facets, HealthEdge) instead of CHO's internal
`benefit-plan-service`. This pattern introduces a thin abstraction
(`IBenefitPlanAdapter`) selected at request time by tenant configuration,
mirroring the existing `IEligibilityAdapter` surface used by
`eligibility-service`.

For every tenant currently in production the factory resolves to the
default `ChoBenefitPlanAdapter`, which is a near pass-through over the
existing `IBenefitPlanService` and `IBenefitViewService` — so this PR
introduces the seam without changing observable behavior.

## Topology

```
           ┌──────────────────────┐
GET /plans │ BenefitPlansController│   GET /benefit-plans
GET /...   │ MemberViewController  │
           └──────────┬────────────┘
                      │ (every request)
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
public interface IBenefitPlanAdapter
{
    string Platform { get; }
    Task<BenefitPlanAdapterResponse>       GetPlanAsync(...);
    Task<BenefitPlanAdapterResponse>       GetPlanVersionAsync(...);
    Task<MemberBenefitViewAdapterResponse> GetMemberBenefitViewAsync(...);
}
```

The two response envelopes carry a `Platform` string, an optional
`RawResponse` audit string, and a normalized payload (`AdapterBenefitPlan`
or `AdapterMemberBenefitView`). Payload shape mirrors the existing
`BenefitPlan` / `MemberBenefitView` so the CHO pass-through is lossless,
and is structurally compatible with the planned FHIR `InsurancePlan`
projection (Section 5.8 of the migration spec).

## Routing & Tenant Configuration

The factory consults tenant-service via HTTP at request time and reads:

```
GET /api/v1/tenants/{tenantId}
  → response.configuration.benefitPlanPlatform.platform        (string)
  → response.configuration.benefitPlanPlatform.platformSettings (object)
```

The matching adapter is selected case-insensitively by its `Platform`
property. Allowed values today:

| Platform string | Adapter                       | Status |
|-----------------|-------------------------------|--------|
| `cho`           | `ChoBenefitPlanAdapter`       | Active |
| `qnxt`          | `QnxtBenefitPlanAdapter`      | Stub — `NotImplementedException` |
| `facets`        | `FacetsBenefitPlanAdapter`    | Stub — `NotImplementedException` |
| `healthedge`    | `HealthEdgeBenefitPlanAdapter`| Stub — `NotImplementedException` |

On any failure (HTTP error, JSON parse error, unknown platform) the factory
warns and falls back to `cho`. A per-tenant `(platform, settings)` tuple
is cached in the singleton `BenefitPlanTenantConfigCache` for 5 minutes
(thread-safe via `ConcurrentDictionary`).

Tenant-service schema lives in
`src/services/tenant-service/Models/Tenant.cs::BenefitPlanConfig` —
parallel to the existing `EligibilityConfig`.

## DI lifetimes

```csharp
// Singleton — must outlive a single request so the TTL cache survives.
services.AddSingleton<BenefitPlanTenantConfigCache>();

// Scoped — Cho wraps the scoped IBenefitPlanService / IBenefitViewService.
services.AddScoped<IBenefitPlanAdapter, ChoBenefitPlanAdapter>();
services.AddScoped<IBenefitPlanAdapter, QnxtBenefitPlanAdapter>();
services.AddScoped<IBenefitPlanAdapter, FacetsBenefitPlanAdapter>();
services.AddScoped<IBenefitPlanAdapter, HealthEdgeBenefitPlanAdapter>();
services.AddScoped<BenefitPlanAdapterFactory>();

services.AddHttpClient(BenefitPlanTenantConfigCache.HttpClientName)
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
```

This differs from `eligibility-service`, where adapters are stateless and
all registered as singletons. The benefit-plan CHO adapter has to be
scoped because it composes scoped business services; we keep the cache
lifecycle separate.

## Refactored controller endpoints

Only the three GET endpoints that map 1:1 onto the interface route through
the factory:

| Endpoint                                                      | Adapter method              |
|---------------------------------------------------------------|-----------------------------|
| `GET /api/v1/plans/{id}`                                      | `GetPlanAsync`              |
| `GET /api/v1/plans/{id}/versions/{versionId}`                 | `GetPlanVersionAsync`       |
| `GET /api/v1/benefit-plans/{planId}/member-view`              | `GetMemberBenefitViewAsync` |

All other endpoints (search, list versions, accumulation, draft/publish/
amend/supersede, benefit add/remove) keep calling `IBenefitPlanService`
directly. Writes are CHO-internal; expanding adapter coverage to writes
is a future PR.

## Adding a new adapter

1. Implement `IBenefitPlanAdapter` with a unique `Platform` string.
2. Register `services.AddScoped<IBenefitPlanAdapter, MyAdapter>()` in
   `Program.cs` next to the others.
3. Add the platform string to the table above and to the comment in
   `BenefitPlanConfig.Platform` in tenant-service.
4. Configure tenants via `PUT /api/v1/tenants/{id}` with
   `configuration.benefitPlanPlatform.platform = "my-platform"`.
5. Write factory + adapter tests under
   `BenefitPlanService.Tests/Adapters/`.

## Migration TODOs

- **QNXT** (`TODO(qnxt-benefit-plan)`): integrate with the QNXT plan
  inquiry API (BENEFIT_PLAN_INQ on the QNXT benefits stack).
- **Facets** (`TODO(facets-benefit-plan)`): integrate with the Facets
  benefit / product inquiry interface (Open Access XML or Workflow REST).
- **HealthEdge** (`TODO(healthedge-benefit-plan)`): integrate with the
  HealthRules Payer plan inquiry API (HRP REST surface).

Until those land the stubs surface a clear `NotImplementedException`
containing the TODO marker so any tenant misconfigured to point at one of
them fails loudly rather than silently degrading.

## FHIR alignment (Section 5.8)

`AdapterBenefitPlan` and `AdapterMemberBenefitView` were intentionally
shaped so a future FHIR projection layer can map them onto FHIR
`InsurancePlan` without a model redesign. Field-level mapping notes:

| Adapter field                  | FHIR `InsurancePlan` location                |
|--------------------------------|----------------------------------------------|
| `EffectiveDate` / `TerminationDate` | `period`                                |
| `PlanType`                     | `type.coding`                                |
| `Payer`                        | `ownedBy` (org reference)                    |
| `NetworkTiers[]`               | `network[]`                                  |
| `CostSharing.*`                | `plan.specificCost[]`                        |
| `Documents[]`                  | contained `DocumentReference` resources      |
| `VersionId` / `VersionNumber`  | `meta.versionId` / extension                 |

The CHO adapter today returns the existing model unchanged via the
mapper; the QNXT/Facets/HealthEdge adapters will populate the same shape
when implemented.
