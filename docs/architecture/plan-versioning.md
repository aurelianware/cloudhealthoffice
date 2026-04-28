# Plan Identity & Versioning

Status: 5.1 — initial implementation
Service: `src/services/benefit-plan-service`

## Why

Adjudication, eligibility, and audit consumers all need to answer "what
did this plan look like at the time of service?" — but until now the
benefit-plan service mutated `BenefitPlan` documents in place. This made
historical replay impossible and conflated three distinct events
(authoring, publishing, terminating) into a single CRUD update.

5.1 establishes an immutable, append-only version chain on each plan.

## Identity model

A *plan* is the abstract entity identified by `(TenantId, PlanId)`.
Each *version* is a row in the `BenefitPlans` collection with a stable
`VersionId` (ULID, Crockford base-32, lexicographically sortable by
creation time) and a 1-based `VersionNumber` monotonic within the
`(TenantId, PlanId)` chain.

```
BenefitPlan
├── Id                       Cosmos document id (used by point-reads)
├── TenantId                 partition key — multi-tenant isolation
├── PlanId                   chain key
├── VersionId                ULID — stable per-version identifier
├── VersionNumber            1-based monotonic
├── VersionState             Draft | Published | Superseded
├── PredecessorVersionId     null on the genesis version
├── PublishedAt / PublishedBy
├── SupersededAt / SupersededByVersionId
└── ... benefit, cost-sharing, document fields ...
```

`Id` and `VersionId` are distinct on purpose: `Id` keeps point-reads
cheap (the existing `GetByIdAsync(id, tenantId)` path is unchanged),
while `VersionId` is the chain identifier downstream consumers persist.

## State machine

```
                  ┌──────────┐  publish     ┌────────────┐   supersede    ┌──────────────┐
   create draft → │  Draft   │ ───────────▶ │ Published  │ ─────────────▶ │  Superseded  │
                  └──────────┘              └────────────┘                └──────────────┘
                       ▲                          │
                       │ amend                    │
                       └──────────────────────────┘
```

- **Draft** — mutable. Only `UpdateDraftAsync` may change it.
- **Published** — read-only at the application layer. The repository's
  `UpdateAsync` rejects writes against Published rows with
  `PlanVersionStateException` → controller maps to HTTP 409.
- **Superseded** — terminal. Reached only via
  `PublishAndSupersedeAsync` when a successor version is published; the
  predecessor's `SupersededByVersionId` points at the new Published
  version.

Standalone supersede (terminate without a successor) is reserved.
`SupersedeVersionAsync` exists in the API surface but currently throws
`InvalidOperationException`; promoting it to a real terminating
transition is tracked as a follow-up.

## Lookup contracts

| Caller intent | Method |
| --- | --- |
| "Give me this version exactly" | `GetVersionAsync(planId, versionId, tenantId)` |
| "Give me whatever is current today" *(legacy callers)* | `GetByPlanIdAsync(planId, tenantId)` — delegates to `GetLatestPublishedAsync(...DateTime.UtcNow)` |
| "Give me the version effective at this date" | `GetLatestPublishedAsync(planId, tenantId, asOf)` |
| "Show me the chain" | `ListVersionsAsync(planId, tenantId, pageSize, cont)` |

`ChoBenefitPlanProvider` (the engine seam consumed by
`BenefitCalculationEngine`) continues to call `GetByIdAsync(versionDocId, …)`
unchanged — claims that have a specific version stamped on them resolve
to that exact version, while flows without a stamped version see
"latest as-of-today" via the legacy method.

## Backward compatibility

The collection contains pre-existing rows that lack identity fields.
Both repository implementations apply the same hydration rule on read:

```
if (string.IsNullOrEmpty(plan.VersionId)) {
    plan.VersionId = plan.Id;
    plan.VersionNumber = 1;
    plan.VersionState = PlanVersionState.Published;
}
```

Empty `VersionId` is the unambiguous legacy marker — every write since
this PR populates it. No data backfill is required.

The existing `IsActive` flag is kept on the wire and is now derived:
new code treats `IsActive == true` as `VersionState == Published`. The
legacy `POST /api/v1/plans` endpoint still creates a Published v1 in
one shot for clients that don't need an explicit draft → publish flow.

## Atomicity

`PublishAndSupersedeAsync` flips the draft to Published and the
predecessor (if any) to Superseded in a single transaction:

- **Cosmos** — transactional batch on the `(TenantId)` partition key.
- **Mongo** — session transaction (requires a replica set). On
  single-node deployments we fall back to sequential writes and emit a
  warning log so ops can spot the split-state risk.

Repositories perform the writes; the service layer is responsible for
applying the new states and timestamps before calling.

## Events

| Event | EventId | Emitted when |
| --- | --- | --- |
| `PlanVersionPublished` | `published:{versionId}` | A draft is moved to Published. |
| `PlanVersionSuperseded` | `superseded:{from}->{to}` | A predecessor is flipped to Superseded by a new Published version. |

Events live in the `PlanVersionEvents` collection (Mongo). Envelope
mirrors `MemberEvent`: client-supplied `EventId` for idempotency,
monotonic `Version` per `(TenantId, PlanId)`, partition key
`{TenantId}:{PlanId}`, payload as a JSON object (BSON-mirrored as a
string).

Bus fan-out (claims-service, eligibility-service notifications) is
intentionally not wired in this PR — `IPlanVersionEventPublisher` is
positioned for a decorator that adds bus publishing without touching
call sites. Cosmos-only deployments without the events stream
provisioned register a no-op publisher that warns on emit.

## Audit log

Every state transition appends a `PlanVersionTransition` row
(`PlanVersionTransitions` collection) with `from`/`to`/`type`/`reason`/
`actor` so audit consumers don't need to replay the event stream to
reconstruct history.

## API surface

Added on `BenefitPlansController` (under `/api/v1/plans`):

```
POST   /api/v1/plans/drafts                                  → 201 Draft
POST   /api/v1/plans/{id}/amend                              → 201 Draft
POST   /api/v1/plans/{id}/versions/{versionId}/publish       → 200 Published
POST   /api/v1/plans/{id}/versions/{versionId}/supersede     → 409 (reserved)
GET    /api/v1/plans/{id}/versions                           → page<Version>
GET    /api/v1/plans/{id}/versions/{versionId}               → Version

PUT    /api/v1/plans/{id}    against Published or Superseded → 409 Conflict
```

The hyphenated `/api/v1/benefit-plans/...` root is reserved for the
member-view consolidation tracked under `TODO(deprecate-plans-route)`;
versions endpoints will be mirrored there when that consolidation
lands.

## Projection metadata — exempt from versioning

A small, deliberately-bounded set of fields on a `BenefitPlan` row is
operationally distinct from version identity. These fields are
patched in place on the head Published row through sibling repository
methods that bypass the `UpdateAsync` "Published is read-only" guard.
The exemption is the exact pattern Provider 5.4.5 / 5.5 established in
[`provider-versioning.md`](./provider-versioning.md) under the section
of the same name; the principle is unchanged here.

### Why an exemption exists

A version is identity. Cost-sharing math, benefit categories,
network-tier definitions, plan-year boundaries, prior-auth gates — all
of those are part of what makes one Published version distinct from
its predecessor. Updating any of them must produce a new Draft, then a
new Published version, with its own row in the chain.

A handful of fields on the same document are *projections* of state
managed by another service or another lifecycle. They are not part of
the plan's identity; they are a cached read-side snapshot kept on the
plan row for query convenience. Forcing every refresh through a full
amendment would create a Published row per refresh — millions per
year across a tenant population — purely to track operational metadata
that has no benefit-design content.

Both lanes coexist. Identity-bearing fields go through `UpdateAsync`
(fails on Published); projection-metadata fields go through their
dedicated sibling method.

### Fields exempt today

| Field | Owner | Sibling method | Capability |
|-------|-------|----------------|------------|
| `NetworkTiers[].NetworkId` | provider-service `Organization` (5.3) | `UpdateNetworkTiersAsync(tenantId, planId, tiers, ct)` | 5.5 — NetworkTier as Reference to Organization |

**Identity-bearing additions (NOT exempt):** `BenefitPlan.FamilyAccumulatorModel`
(BP 5.7) is identity-bearing by design. Changing the model on a
Published plan affects in-flight adjudications materially and requires
a new version, just like any cost-sharing change. No bypass method is
provided.

`UpdateNetworkTiersAsync` resolves the head Published row by chain
key, then patches the entire `NetworkTiers` collection with a single
field-scoped op (Cosmos `PatchItemAsync` `Set("/networkTiers", tiers)`;
Mongo `FindOneAndUpdateAsync` with sort-by-`VersionNumber` and `$set`,
patching the head row in one round trip). No `PlanVersionEvent` is
emitted — the operation is a projection-metadata refresh, not a chain
transition.

### Invariants the bypass must preserve

1. **No version-state writes.** The bypass method must not change
   `VersionState`, `VersionId`, `VersionNumber`, `PredecessorVersionId`,
   `PublishedAt`, `SupersededAt`, or any other identity field. A
   patch op that touches any of those is a bug.
2. **`UpdateAsync` still enforces immutability.** A regular
   `UpdateAsync` against a Published row continues to raise
   `PlanVersionStateException`. The projection patch and the identity
   write are fully orthogonal — covered by
   [`UpdateNetworkTiersAsyncTests`](../../src/services/benefit-plan-service/BenefitPlanService.Tests/Repositories/UpdateNetworkTiersAsyncTests.cs).
3. **Single field scope.** Each bypass method patches exactly one
   logical concern. A future field that wants the exemption must add
   a new sibling method, not extend an existing one to cover unrelated
   fields.
4. **Idempotent.** Re-running the same patch is safe by construction:
   the bypass writes deterministic values, never relative deltas.

### Adding a new projection-metadata field

Future capabilities that want this exemption should:

1. Document the field's owner (the service / lifecycle that produces
   the value).
2. Add a sibling repository method named
   `Update<Field>ProjectionAsync` (or `Update<Field>Async` when the
   semantics are richer than a projection refresh — see
   `UpdateNetworkTiersAsync`).
3. Implement Cosmos and Mongo variants symmetrically. The Mongo
   variant uses `UpdateOneAsync` with `$set`; the Cosmos variant uses
   `PatchItemAsync` with field-scoped `Set` ops.
4. Add a row to the table above so the exemption stays auditable.
5. Cover both lanes in tests: the bypass succeeds against Published,
   and the corresponding `UpdateAsync` against the same row still
   throws.

A field that doesn't meet the "managed by another service or another
lifecycle" bar isn't projection metadata — it's identity, and the
amendment path is the right tool.

## Caveats / follow-ups

- **Terminate-without-successor.** `SupersedeVersionAsync` currently
  throws. Add a `Terminate` transition type once a UX is agreed.
- **Bus fan-out.** Decorator over `IPlanVersionEventPublisher` not yet
  wired.
- **Cross-tenant draft isolation.** Drafts are partitioned by
  `TenantId` like every other row; no extra access control beyond the
  tenant middleware is implemented in this PR.
- **Hard validation of `NetworkTier.NetworkId`.** Capability 5.5
  ships nullable `NetworkId` with soft-validation telemetry. The
  follow-up flips to hard validation once
  `cho.benefit_plan.network_tier_missing_networkid_writes.total` reads
  zero across all tenants for a sustained window. See
  [`network-tier-organization-reference.md`](./network-tier-organization-reference.md).
