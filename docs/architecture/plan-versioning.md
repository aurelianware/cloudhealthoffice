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

## Caveats / follow-ups

- **Terminate-without-successor.** `SupersedeVersionAsync` currently
  throws. Add a `Terminate` transition type once a UX is agreed.
- **Bus fan-out.** Decorator over `IPlanVersionEventPublisher` not yet
  wired.
- **Cross-tenant draft isolation.** Drafts are partitioned by
  `TenantId` like every other row; no extra access control beyond the
  tenant middleware is implemented in this PR.
