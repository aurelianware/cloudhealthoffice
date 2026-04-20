# Accumulator Service

Tracks per-member plan-year accumulators: individual & family deductibles,
out-of-pocket maxima, and per-benefit-category usage. Driven by finalized
claim events and manual operator adjustments; read by the portal
(Accumulators tab) via member-service.

## Responsibilities

1. Project `ClaimFinalizedEvent`s onto a per-(tenant, member, plan-year)
   `AccumulatorSnapshot`.
2. Serve point-in-time reads (`GET /api/v1/accumulators/{memberId}`) and
   history (`/history`).
3. Record authorized manual adjustments with full audit trail.
4. Emit `AccumulatorAdjustedEvent` so downstream analytics / audit
   projectors see every mutation.

## Data model

### `AccumulatorSnapshot`

Current-state read model. One per `(tenantId, memberId, planYearStart)`.
Document id is deterministic: `{tenantId}:{memberId}:{yyyyMMdd}` so a retro
claim can resolve its target without a separate lookup.

Fields: individual / family deductible & OOP (used + limit),
`ServiceAccumulators[]` per benefit category, `Version` (monotonic), and
timestamps.

### `AccumulatorEvent`

Append-only event stream. This is the source of truth for audit and for
rebuilding the snapshot by replay. Unique constraints:

- `(tenantId, eventId)` — wire-level dedup.
- `(tenantId, aggregateId, version)` — strict ordering per snapshot.

Event types: `ClaimApplied`, `ManualAdjustment`, `OrphanSkipped`,
`DuplicateSkipped`.

### `ProcessedClaim`

Idempotency marker keyed by `(tenantId, claimId)`. Upsert-then-check on a
unique index makes duplicate detection race-free. Stores `ProcessedAt` and
`ResultingEventId` so a support engineer debugging a duplicate-claim
question can see exactly when and by which event the claim was applied.

## Event contract: `ClaimFinalizedEvent`

Internal CHO event, **not** a FHIR ExplanationOfBenefit. EOB is a
query-time projection surfaced by claims-service for Patient Access /
Payer-to-Payer (Phase 3); coupling an internal event to FHIR versioning
cadence is a footgun. Carry exactly what accumulator aggregation needs,
flat:

- Envelope: `EventId` (GUID), `EventSchemaVersion` (int), `EventType`,
  `OccurredAt`.
- Identity: `TenantId`, `ClaimId`, `ClaimNumber`, `MemberId`.
- Time: `ServiceDate` (for plan-year selection), `AdjudicationTimestamp`
  (when the decision was made), `PlanYearStart` / `PlanYearEnd`
  (producer-asserted; optional).
- Amounts (claim-level sums): `DeductibleApplied`, `CoinsuranceApplied`,
  `CopayApplied`, `OopApplied`, `PlanPaid`, `MemberResponsibility`.
- Flags: `IsFamilyAggregate`, `BenefitCategory`, `FinalStatus`.
- `LineItems[]` — per-line applied amounts. Populated when a single claim
  spans multiple benefit categories. Most claims have one line.

Idempotency is keyed by `(TenantId, ClaimId)` — not `EventId` — because
re-finalization of the same claim must be deduped even across event-id
regenerations (e.g. producer restart replays).

## Snapshot selection (retro claims)

Snapshot selection uses **ServiceDate**, not `AdjudicationTimestamp` or
"today". A claim finalized today for a service date six months ago lands
in the plan year that contained the service date. This is asserted by
`AccumulatorServiceTests.Apply_RetroClaim_TargetsPriorYearSnapshotNotCurrent`.

## Orphan handling

When `ClaimFinalizedEvent.ServiceDate` does not map to any known
plan-year snapshot (e.g. predates earliest coverage):

- Log a structured warning with `{ClaimId, TenantId, MemberId, ServiceDate}`.
- Emit `OrphanAccumulatorClaimEvent` to `accumulators.orphan.v1`.
- Mark the `ProcessedClaim` with `Outcome = OrphanSkipped`.
- Do **not** silently drop, and do **not** crash — this is a data-quality
  alert condition that operational tooling should surface.

## HTTP surface

- `GET /api/v1/accumulators/{memberId}?asOfDate=YYYY-MM-DD` — canonical
  read. Returns a zero-state response (not 404) when no snapshot exists,
  so the portal renders cleanly for new members.
- `GET /api/v1/accumulators/{memberId}/history` — full snapshot list
  across plan years plus the most recent 200 events.
- `POST /api/v1/accumulators/{memberId}/adjust` — manual adjustment.
  Requires `ActorId` and `Reason`; both are written to the audit event.
- `GET /api/v1/members/{memberId}/accumulators` — compat alias for the
  member-service client. **TODO(deprecate-members-accumulators-alias)**:
  retire alongside the `/api/v1/plans` pattern retired in PR #652.

Every request must carry `X-Tenant-ID`. Missing header → 400.

## Messaging

Currently Kafka via `Confluent.Kafka`, consistent with claims-service:

- Consumer: `claims.finalized.v1` (group
  `accumulator-service.claims-finalized`,
  `EnableAutoCommit=false`, commit only on terminal outcome).
- Producer: `accumulators.adjusted.v1`, `accumulators.orphan.v1`.

**TODO(addendum-a)**: evaluate migration to the Service Bus-backed
`IMessageBus` abstraction at the Phase 1 / Phase 2 boundary. Claims
events have pub-sub fan-out characteristics (accumulators, risk
adjustment, analytics, condition-service), so Kafka may be the right
choice once formalized — that decision is explicitly out of scope for
this PR.

## Multi-tenancy

Every collection / container is partition-keyed on `tenantId`.
Repositories never issue a query without a `tenantId` predicate. The
shared `TenantMiddleware` (see
`CloudHealthOffice.Infrastructure.Middleware`) resolves the tenant from
JWT claims first, `X-Tenant-ID` header second, `tenantId` query
parameter as a dev-only fallback.

## Configuration

| Setting | Purpose |
| --- | --- |
| `MongoDb:ConnectionString` | Primary storage. Mongo path when set. |
| `CosmosDb:ConnectionString` | Fallback storage. Cosmos path when Mongo is unset. |
| `Kafka:BootstrapServers` | Kafka brokers. Consumer + publisher disable gracefully when unset. |
| `Kafka:ClaimFinalized:GroupId` | Consumer group for claims.finalized.v1. |

`member-service` must have `Downstream:AccumulatorService:BaseUrl`
configured outside Development — an unset value is a **startup error**,
not a lazy 503 at call time (per PR #650 convention).

## Testing

- `AccumulatorServiceTests` — unit tests for apply, idempotency, retro,
  orphan, family aggregate, multi-line attribution, manual adjustment,
  tenant isolation.
- `AccumulatorsControllerTests` — integration tests via
  `WebApplicationFactory<Program>`, with in-memory substitutions for the
  repository / processed-claim store / event publisher.

No external infrastructure required to run either suite.
