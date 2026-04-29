# Service Category Mapping (Capability BP 5.6)

**Status:** Implemented in BP 5.6.
**Owners:** benefit-plan-service.
**Related:**
[`declarative-benefit-model.md`](declarative-benefit-model.md) (BP 5.4 typed
benefits) ·
[`plan-versioning.md`](plan-versioning.md) (BP 5.1 plan version chain) ·
[`network-tier-organization-reference.md`](network-tier-organization-reference.md)
(BP 5.5 — for the parallel admin-gate pattern).

## Why this exists

The benefit calculation engine resolves each adjudicated claim line to a
service category (e.g. _Office Visit_, _Inpatient Hospital_, _Pharmacy_)
before looking up the operator-authored cost share for that line. The
mapping from procedure code (CPT/HCPCS/REV/NDC) to service category is
the load-bearing seam between the **claims world** and the **benefit
world**. Without it, every claim line would have to declare its own
benefit category — a contract every clearinghouse and 837 transmitter
ignores.

Before BP 5.6 this seam was wired but unimplemented: the resolver
contract existed, the data model existed, but
`NullServiceCategoryMappingRepository` returned an empty list and the
resolver fell through to a tiny POS-code inference fallback. BP 5.6
ships:

1. A real Cosmos / Mongo storage backend implementing the read seam.
2. An admin write API for authoring tenant-default and plan-specific
   override mappings.
3. A curated CHO seed bundle with ~18 operator-friendly categories
   covering the common claim shapes.

## Resolution flow

`ServiceCategoryResolver.ResolveAsync(tenantId, planId, code, codeType, pos, modifiers, revCode)`:

1. **Plan-specific override** — read mappings keyed by
   `(tenantId, planId)`. First matching `ProcedureCodeRule` wins.
   `MatchedBy = "PlanOverride"`.
2. **Tenant-level default** — read mappings keyed by `(tenantId, null)`.
   `MatchedBy = "TenantDefault"`.
3. **POS-code inference fallback** — built into the resolver, not the
   repository. POS 11 → X12 service type 98, POS 21/22/23 → 48, etc.
   `MatchedBy = "SystemDefault"`.
4. **Null** — when no mapping matches and no POS inference applies, the
   resolver returns null and `BenefitCalculationEngine` denies the line
   with code 18 ("No benefit category mapping").

## Storage

**Document shape**: mapping documents are keyed by their per-row `Id`
(GUID) rather than the logical tuple `(tenantId, benefitPlanId?, serviceTypeCode)`.
The tuple is **not** a hard uniqueness constraint — multiple rows can
exist for the same tuple after a seeder version-bump re-apply (see
[Seed re-application](#seed-re-application) below). The resolver iterates
mappings in **newest-first order** (`createdAt DESC`), so first-match-wins
naturally prefers the most recent row when duplicates exist. Tenant-default
mappings have `benefitPlanId = null`. The same collection hosts a
`documentType="system-defaults-applied"` sibling document used by the
seeder for per-tenant idempotency tracking.

**Backend selection**: same `MongoDb:ConnectionString` switch used by
`BenefitPlanRepository`. Cosmos uses partition key `tenantId`; Mongo
uses an `_id` index. The same class implements the read, write, and
applied-record seams.

**Container/collection name**: `ServiceCategoryMappings` by default;
override with `CosmosDb:ServiceCategoryMappingsContainerName`. The
config key intentionally mirrors Cosmos for both backends so a tenant
migrating between Cosmos and Mongo sees identical config keys.

## Caching

`CachingServiceCategoryMappingRepository` decorates the raw storage
backend with an `IMemoryCache` keyed on
`("svccatmap", tenantId, benefitPlanId)`. Default TTL 5 minutes,
configurable via `ServiceCategoryMapping:CacheTtl`. Writes invalidate
the cache entry for the affected scope; cross-pod cache coherence relies
on the TTL window rather than a distributed invalidation channel.

The cache layer is cleanly separated from the storage backend so the
backend can be tested in isolation and the cache decorator can be unit-
tested without spinning up a real Cosmos / Mongo client.

## Admin write API

Routes (under `/api/v1/service-category-mappings`):

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/` | List mappings for the tenant on the request. `?planId=` filters to plan overrides; omitted returns tenant defaults. |
| `GET` | `/{id}` | Fetch a single mapping. |
| `POST` | `/` | Create. |
| `PUT` | `/{id}` | Replace. |
| `DELETE` | `/{id}` | Remove. |
| `POST` | `/seed-system-defaults` | Apply the curated seed bundle to the tenant on the request. Idempotent. |

Bulk-import and validate endpoints are deliberately deferred to a follow-
up capability (see "Out of scope for BP 5.6" below).

## Authorization model

The write endpoints (POST / PUT / DELETE / seed) sit behind the
**`ServiceCategoryMapping:AdminWriteEnabled`** defence-in-depth gate
(default `false`). When the flag is false the controller returns 503
Service Unavailable; when the flag is true the route accepts writes
from any caller permitted by the gateway.

This intentionally mirrors the established pattern in
[`network-tier-organization-reference.md`](network-tier-organization-reference.md) —
benefit-plan-service does not yet have claim-based authorization in any
controller, and BP 5.6 is not the place to introduce a new authorization
pattern. The deployment layer (NetworkPolicy, gateway ACL) is the
load-bearing control. Claim-based auth is a service-wide initiative
tracked separately.

## System-default seed bundle

The curated bundle ships at
`schemas/service-category-mappings/system-defaults.json`. The bundle is
**per-installation**: one curated source-of-truth file applied
**per-tenant** on demand by the seeder. The seeder runs at startup to
validate and warm the bundle; tenant application is operator-triggered
via the seed admin endpoint.

The bundle carries a positive integer `version`. The seeder records the
last applied version per tenant in a `SystemDefaultsApplied` document
and skips reruns at the same version.

### Seed re-application

To re-apply with bundle changes, bump `version` and trigger the seed
admin endpoint for affected tenants. Re-application **inserts new
mapping rows alongside existing seeded rows**; it does **not** replace
or upsert prior rows — that is a deliberate choice so operator-
authored overrides aren't silently overwritten on a version bump.

Because this leaves multiple rows for the same `serviceTypeCode` after a
re-apply, the storage backends sort `GetMappingsAsync` results by the
mapping's `CreatedAt` field **descending**. The resolver iterates the
result list with first-match-wins semantics, so a freshly seeded row
naturally wins against an older row for the same procedure code. The
ordering is deterministic across pods and across Cosmos vs Mongo.

Operators clean up superseded seed rows manually via the `DELETE`
admin endpoint when the duplicate-row debris becomes operationally
inconvenient.

See [`schemas/service-category-mappings/README.md`](../../schemas/service-category-mappings/README.md)
for bundle authoring conventions.

## Known incoherence — `ServiceTypeCode` vs `Benefit.ServiceCategory`

`ServiceCategoryResolver` produces a `ServiceTypeCode`. The benefit
calculation engine joins it to the plan's `Benefit.ServiceCategory`
(free-text plan-author label) at
[`BenefitCalculationEngine.cs:317`](../../src/engines/CloudHealthOffice.BenefitEngine/Services/BenefitCalculationEngine.cs#L317)
and `:428`, via [`Providers.cs:114`](../../src/engines/CloudHealthOffice.BenefitEngine/Services/Providers.cs#L114)
(`plan.GetCategory(serviceTypeCode)`). At
[`ChoBenefitPlanProvider.cs:39`](../../src/services/benefit-plan-service/Services/ChoBenefitPlanProvider.cs#L39),
`Benefit.ServiceCategory` flows directly into the
`BenefitCategoryConfig.ServiceTypeCode` field — making the join
key-equal between the two surfaces.

This produces a known incoherence with X12 5010 standards:

- The **resolver POS fallback** emits **X12 5010 codes** like `"98"`
  (Professional Visit), `"48"` (Inpatient Hospital), `"86"` (ER).
- **Operator-authored `Benefit.ServiceCategory`** is typically free-text
  like `"Office Visit"`, `"Inpatient Hospital"`, `"ER"`.
- **The two surfaces don't match.** Adjudication via the POS fallback
  produces a denial code 18 (No benefit category mapping) for any plan
  whose `Benefit.ServiceCategory` values aren't X12 codes.

**BP 5.6 takes a deliberate position**: the seed bundle uses
**operator-friendly text labels** matching the plan-author convention.
A plan with `Benefit.ServiceCategory = "Office Visit"` adjudicates
correctly against the seeded `Office Visit` mapping. X12 5010 alignment
remains a **Phase 2** capability — a dedicated translation-layer
follow-up will introduce a `ServiceTypeCodeAlias` table joining
canonical X12 codes to operator text labels.

This decision is recorded explicitly so future changes to either
surface honour the constraint that they must remain key-equal until the
translation-layer capability lands.

## Effective-date fields (shipped in BP 5.10)

The `ServiceCategoryMapping` entity carries `EffectiveStart`,
`EffectiveEnd`, and `IsActive` fields as of BP 5.6. As of **BP 5.10**
the resolver filters on these fields against the claim line's service
date — see
[`adjudication-api-stabilization.md`](adjudication-api-stabilization.md)
for the inclusive-bound semantics, the `IsActive` kill-switch posture,
and the producer-boundary 400 on `EffectiveEnd < EffectiveStart`.

The entity also carries `CreatedAt` (DateTimeOffset, populated by the
storage backends on insert if unset). Like the effective-date fields,
this one is consumed today — the storage backends sort by
`CreatedAt DESC` so the resolver's first-match-wins iteration is
deterministic across seeder version-bump re-applies. `UpdatedBy`,
`UpdatedAt`, and operator audit fields remain deferred to a service-
wide audit-pattern initiative.

## Out of scope for BP 5.6

- **Bulk import / validate endpoints** — file-format UX, partial-failure
  semantics, and dry-run mode warrant their own design pass. The CRUD
  surface unblocks the common authoring case.
- **Effective-date resolver filtering** — shipped in **BP 5.10**. See
  [`adjudication-api-stabilization.md`](adjudication-api-stabilization.md).
- **Audit fields** (`UpdatedBy`, `UpdatedAt`) — deferred to a service-
  wide audit-pattern initiative. `CreatedAt` is in scope (added in BP
  5.6 to drive deterministic resolver ordering across re-applies — see
  "Seed re-application" above).
- **Version chain on mapping documents** — mappings are operational
  reference data; updates are last-write-wins with operational audit
  via structured request logging.
- **`BenefitRulePredicate` evaluation** — shipped in **BP 5.10**. See
  [`adjudication-api-stabilization.md`](adjudication-api-stabilization.md).
- **X12 ↔ free-text translation layer** — Phase 2 capability;
  unchanged from BP 5.6 — the load-bearing follow-up that closes the
  incoherence documented above.

## Operating notes

| Concern | Where to look |
|---|---|
| Cosmos / Mongo selection | `Program.cs` — same `MongoDb:ConnectionString` switch as `BenefitPlanRepository` |
| Cache TTL | `ServiceCategoryMapping:CacheTtl` (default 5 min) |
| Admin gate | `ServiceCategoryMapping:AdminWriteEnabled` (default false) |
| Seed bundle | `schemas/service-category-mappings/system-defaults.json` (copied to ContentRoot at build time) |
| Seed startup behaviour | `ServiceCategoryMapping:SeedSystemDefaultsOnStartup` (default true; tests disable) |
| Per-tenant seed application | `POST /api/v1/service-category-mappings/seed-system-defaults` with `X-Tenant-ID` header |
| Per-tenant idempotency record | `SystemDefaultsApplied` document in the same collection |

## Recovery posture

Failure modes and recovery:

- **Seed bundle fails to load at startup** — service logs the parse
  error, the bundle stays null, and the seed admin endpoint returns
  503. Resolver still works via tenant/plan mappings and the POS
  fallback. Fix the bundle and restart.
- **Cache TTL too short / too long** — tune
  `ServiceCategoryMapping:CacheTtl` without a code change.
- **Admin write disabled tripwire trips on a deploy** — flip the flag
  to true (the deployment-layer ACL is still load-bearing).
- **Worst-case rollback** — revert the PR. Mapping data persists in
  storage; the next release re-registers the repository and continues
  reading existing rows. There is no Null fallback after BP 5.6 — the
  resolver reads from the configured backend or fails to read (POS
  fallback continues to produce results regardless).
