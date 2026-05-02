# Claim Identity & Versioning

Status: 5.1a — initial implementation (versioning fields + event chain).
5.1b — Cosmos partition-key migration to `/tenantId` (infra-coordinated).
Service: `src/services/claims-service`

Cross-references:
- [provider-versioning.md](provider-versioning.md) — the reference pattern.
- [plan-versioning.md](plan-versioning.md) — the second instance of the pattern.

## Why

The 837/277/835 lifecycle is inherently versioned: a single claim can
move from submitted → pended → adjudicated → adjusted → paid, and an
adjustment is, in healthcare-system terms, a new claim that references
the predecessor. Until 5.1, claims-service mutated a single row in place
through every transition, leaving the audit surface to a Kafka event
stream that was best-effort by design (degraded-mode semantics for
accumulator-service consumption).

Capability 5.1 establishes an immutable, append-only version chain per
claim and an audit-grade Mongo event stream alongside the existing
Kafka notification stream. The pattern matches Provider 5.1 and Benefit
Plan 5.1 exactly — three versioned-entity domains now share one shape.

The version chain is the foundation that capabilities 5.5
(adjudication), 5.11 (FHIR ExplanationOfBenefit projection), and 5.12
(adjustment workflow) consume.

## Identity model

A *claim* is the abstract entity identified by `(TenantId, ClaimVersionId)`.
Each *version* is a row in the `Claims` collection with a per-row `Id`
and a 1-based `VersionNumber` monotonic within the chain.

```
Claim
├── Id                       per-version document id
├── TenantId                 multi-tenant isolation
├── ClaimVersionId           chain key — stable across versions
├── VersionNumber            1-based monotonic
├── VersionState             Unknown(legacy) | Draft | Submitted | Adjudicated | Paid | Denied | Adjusted | Voided
├── PredecessorVersionId     null on the genesis version; set on adjustments
├── PublishedAt / PublishedBy        when this version left Draft
├── SupersededAt / SupersededByVersionId   when this version was adjusted
├── Status (legacy ClaimStatus)      operational sub-state, see "Reconciliation" below
├── PendDetails                      transient pipeline-stage detail (Pended sub-state)
├── AdjudicationResult                (projection-bypass writes — see below)
├── ClaimLines[]                      with per-line AdjudicationResult
└── ... ClaimNumber, MemberId, BillingProviderNPI, service dates, charges, ...
```

Legacy rows persisted before these fields existed hydrate to
`ClaimVersionId = Id`, `VersionNumber = 1`, and a `VersionState` derived
from the legacy `ClaimStatus` (see hydration table below). This keeps
the existing 22 controller endpoints and the accumulator-service Kafka
contract working without a data migration.

## State machine

```
                  ┌──────────┐  publish  ┌───────────┐  adjudicate  ┌──────────────┐
   create draft → │  Draft   │ ────────▶ │ Submitted │ ───────────▶ │ Adjudicated  │
                  └──────────┘           └───────────┘              └──────────────┘
                                              │  ▲                       │
                                              │  │ adjudicate            │ approve+pay
                                              │  │ (re-run)              ▼
                                              │  │                ┌──────────┐
                                              │  │                │   Paid   │
                                              │  │                └──────────┘
                                              ▼  │                       │
                                          ┌──────────┐  void             │ supersede
                                          │  Voided  │ ◀─────────────────┘ (5.12)
                                          └──────────┘                    ▼
                                                                  ┌────────────┐
                                                                  │  Adjusted  │
                                                                  └────────────┘
                                              │ deny
                                              ▼
                                          ┌──────────┐
                                          │  Denied  │
                                          └──────────┘
```

- **Unknown** (`= 0`) — default for uninitialized / legacy rows. Hydration
  on read maps `Unknown` to a real state via the legacy `ClaimStatus`.
  No new writes should produce `Unknown`.
- **Draft** — mutable. Capability 5.3 (Submission API) is the canonical
  producer; until 5.3 ships, controller-driven creates seed
  `VersionState = MapStatusToVersionState(Status)` directly.
- **Submitted** — claim is in flight. Encompasses the operational
  `ClaimStatus` sub-states `Submitted`, `Received`, `InAdjudication`, and
  `Pended` (Pended is a transient pipeline-stage outcome captured by
  `PendDetails`, not a version transition).
- **Adjudicated** — adjudication has run. The `ClaimStatus.Approved`
  operational sub-state lives here (awaits payment). Re-adjudication is
  permitted via `UpdateAdjudicationProjectionAsync` without rolling a
  new version.
- **Paid** — terminal for paid claims. The `ClaimStatus.PartiallyPaid`
  operational sub-state lives here too.
- **Denied** — terminal for denied claims (no payment).
- **Adjusted** — terminal for the predecessor of an adjustment chain.
  `SupersededByVersionId` points at the replacement version.
- **Voided** — terminal for reversed/cancelled claims.

`UpdateAsync` rejects writes against `Paid`, `Denied`, `Voided`, and
`Adjusted` rows with `ClaimVersionStateException`. The adjustment
workflow (5.12) is the only legitimate path forward from those terminal
states; it creates a new version with `PredecessorVersionId` pointing
at the old row and marks the old row `Adjusted`.

## Hydration: legacy ClaimStatus → ClaimVersionState

| Legacy `ClaimStatus`                                  | Hydrated `ClaimVersionState` |
| ----------------------------------------------------- | ---------------------------- |
| `Submitted`, `Received`, `InAdjudication`, `Pended`   | `Submitted`                  |
| `Approved`                                            | `Adjudicated`                |
| `Paid`, `PartiallyPaid`                               | `Paid`                       |
| `Denied`                                              | `Denied`                     |
| `Voided`                                              | `Voided`                     |

The `ClaimStatus` enum is preserved for backward compatibility (the
existing 22 controller endpoints and the accumulator-service Kafka
contract both depend on its values). It captures **operational
sub-state** — what stage the work is at — while `ClaimVersionState`
captures **lifecycle state** — where the version sits in its
state-machine. Migrating `ClaimStatus` to the PR #705 enum convention
(`Unknown=0`, `JsonStringEnumConverter`) is out of scope for 5.1; that
is its own focused enum-hygiene PR.

## Append-only event stream

Every state transition appends one immutable `ClaimVersionEvent` row to
the Mongo `ClaimVersionEvents` collection. The collection is sized for
audit-grade retention and indexed for the publisher's idempotency and
monotonicity invariants:

```
unique (TenantId, ClaimVersionId, EventId)   — idempotency key
unique (TenantId, ClaimVersionId, Version)   — monotonic ordering
```

Both indexes are created at startup by
`ClaimVersionEventIndexInitializer` (an `IHostedService`); Mongo silently
no-ops on an existing matching spec, so re-runs are safe.

`ClaimVersionEvent` shape:

```
ClaimVersionEvent
├── Id                       "{PartitionKey}:{EventId}" — tenant-scoped Mongo _id
├── PartitionKey             "{TenantId}:{ClaimVersionId}"
├── TenantId
├── ClaimVersionId           chain key
├── VersionId                per-row document id
├── EventId                  deterministic, e.g. "submitted:{VersionId}"
├── EventType                ClaimVersionSubmitted | Adjudicated | Paid | Denied | Superseded | Voided
├── Version                  monotonic per (TenantId, ClaimVersionId)
├── SchemaVersion            = 1
├── OccurredAt
├── ActorId, CorrelationId
└── Payload                  state-specific JSON
```

Cross-tenant isolation holds at three layers:
1. The unique compound index `(TenantId, ClaimVersionId, EventId)` —
   the application-level idempotency contract.
2. The `PartitionKey` shape `{TenantId}:{ClaimVersionId}` — keeps
   tenants on disjoint Cosmos / Mongo partitions when the events
   collection is multi-tenant-shared.
3. The Mongo `_id` shape `{PartitionKey}:{EventId}` — guarantees
   that a deterministic EventId from one tenant cannot mask a write
   from another even if the unique index is somehow dropped.

State transitions are encoded by `EventType`, not by explicit
`FromState` / `ToState` fields — this matches the Provider/Plan event
shapes precisely. The `ClaimVersionPended` event type intentionally
does NOT exist: Pended is a transient sub-state of `Submitted`, captured
by `PendDetails` and the existing `claims.pended.v1` Kafka topic, not by
the version stream.

The `MongoClaimVersionEventPublisher` retries on duplicate-key conflicts
(another writer raced to the same `Version`), refetches the existing
event on the second pass, and returns the persisted instance. Five
retry attempts with backoff `2 / 5 / 25 / 100 / 250 ms` cover transient
contention; persistent failure throws.

## Kafka notifications — unchanged

The existing Kafka topics `claims.pended.v1` and `claims.finalized.v1`
remain the consumer interface for the accumulator-service and any
future downstream subscriber. 5.1 does NOT alter their payload shape or
emission path; the dual-emit refactor and a new `claims.versions.v1`
broader-stream topic are deferred to a future capability that brings a
real downstream consumer.

The Mongo event stream is the **system-of-record audit surface**; Kafka
is the derived notification stream. Mongo write success is independent
of Kafka — Kafka failure preserves the existing degraded-mode
semantics (logged, not propagated; claim DB is truth).

## Projection metadata — exempt from versioning

Adjudication state is operationally distinct from claim identity.
Each adjudication run shouldn't produce a new claim version (an 837
adjudicated three times during a single business day would otherwise
explode the version chain). Capability 5.5 writes adjudication results
through a dedicated repository method that bypasses the version-state
guard:

```csharp
Task<bool> UpdateAdjudicationProjectionAsync(
    string tenantId,
    string claimVersionId,
    AdjudicationResult adjudicationResult,
    IReadOnlyList<LineAdjudicationResult> lineResults,
    CancellationToken ct = default);
```

Implementation:
- **Cosmos**: `PatchItemAsync` setting `/adjudicationResult`,
  `/claimLines`, `/lastUpdatedDate`. No new row, no event emitted.
- **Mongo**: `UpdateOneAsync` with the same `$set` shape.

Adjustments DO produce new versions — those go through the regular
`UpdateAsync` path which trips the terminal-state guard, so the only
legitimate way out of `Paid`/`Denied`/`Voided` is the adjustment
workflow (5.12) creating a fresh version with `PredecessorVersionId`.

This is the **5th instance of the projection-metadata bypass pattern**:

| Service                | Bypass method                              | Purpose                            |
| ---------------------- | ------------------------------------------ | ---------------------------------- |
| provider-service       | `UpdateIntegrityProjectionAsync`           | Verification integrity score (5.4.5) |
| provider-service       | `UpdatePanelGatingDefaultsAsync`           | Network-participation defaults (5.5) |
| provider-service       | `UpdateCredentialingProjectionAsync`       | Credentialing chain projection (5.6) |
| benefit-plan-service   | `UpdateNetworkTiersAsync`                  | Plan network tier list (5.5)        |
| **claims-service**     | **`UpdateAdjudicationProjectionAsync`**    | **Adjudication results (5.1)**       |

Each bypass shares one architectural justification: the bypassed field
is a *projection of operational state*, not a constituent of the
versioned entity's identity. Writing to a projection field doesn't
change what the entity *is*, only what we *know about its operational
status right now*.

## Cosmos partition key — `/tenantId` (5.1b)

5.1b moved the Claims Cosmos container from the legacy `/memberId`
Bicep declaration / `/Id` runtime partition to the canonical
`/tenantId` partition. Pattern parity with Provider, Benefit Plan,
and AiExaminationAudit. The change eliminates cross-partition
fan-out on the versioning surface (`GetLatestVersionAsync`,
`GetVersionAsync`, `ListVersionsAsync`,
`UpdateAdjudicationProjectionAsync`,
`MarkSupersededProjectionAsync`, `MarkVoidedProjectionAsync`); each
becomes an efficient single-partition operation.

### Why a new container, not an in-place repartition

Cosmos containers cannot be renamed and their partition key cannot
be changed. Both the Bicep declaration and the SDK enforce this —
the only "rename" Cosmos supports is delete + recreate, which
destroys data. 5.1b therefore introduced a sibling container,
`ClaimsV2`, declared at
[infrastructure/azure/modules/cosmos-db.bicep](../../infrastructure/azure/modules/cosmos-db.bicep)
with `partitionKey: ['/tenantId']`, and an operator-triggered
migration job that copies documents from the legacy `Claims`
container into `ClaimsV2`. The legacy container is preserved during
a 30-day rollback window then removed in a focused follow-up Bicep
PR.

### Migration tooling shape

`POST /api/v1/admin/claims/cosmos-migration/run` is the
operator-facing surface, gated by
`ClaimsCosmosMigration:MigrationsEnabled` (defaults to false; the
deployment-layer ACL is the load-bearing authorization, the flag is
a defence-in-depth tripwire). Mirrors the shape of
`NetworkTierBackfillAdminController` in benefit-plan-service:
503-when-disabled (not 404 — operators need to know the route
exists and is intentionally gated), idempotent reruns, status
endpoint at `GET /api/v1/admin/claims/cosmos-migration/status`.

The migration logic lives in
[`Services/Migrations/ClaimMigrationService.cs`](../../src/services/claims-service/Services/Migrations/ClaimMigrationService.cs).
Three properties of the implementation are worth recording for
future engineers:

1. **Hydrate-on-write** — every document is passed through
   `ClaimRepository.Hydrate` before writing to `ClaimsV2`. Legacy
   rows missing `ClaimVersionId` (`""`), `VersionNumber` (`0`), or
   `VersionState` (`Unknown`) land in the new container fully
   canonicalized. Downstream readers don't need to re-Hydrate
   post-migration.
2. **Batched idempotency check** — for each page of source
   documents (default 100), a single `ARRAY_CONTAINS(@ids, c.id)`
   query against `ClaimsV2` partitioned by tenant resolves the
   subset already migrated. Per-document point-reads would
   dominate RU spend on rerun.
3. **Single-flight semantics** — concurrent invocations are
   rejected with 409 Conflict via an in-process running flag.
   Two simultaneous runs would double-count outcomes and produce
   confusing telemetry; operators get an explicit signal instead.

### Cutover protocol

1. Deploy the Bicep change (creates `ClaimsV2` alongside the
   existing `Claims` container).
2. Deploy claims-service with the migration capability
   (`CosmosDb:ContainerName` still points at `Claims`).
3. Set `ClaimsCosmosMigration:MigrationsEnabled=true` and run the
   endpoint with `dryRun=true`. Verify counters and surface any
   hydration anomalies before the apply pass.
4. Re-run with `dryRun=false`. Idempotent reruns are safe.
5. Plan a 2–5 minute low-traffic pause window. Drain the Service
   Bus subscription, do a final delta migration pass, flip
   `CosmosDb:ContainerName: "ClaimsV2"`, redeploy.
6. Verify production traffic on `ClaimsV2` for the 30-day
   retention window.
7. Open the follow-up Bicep PR removing the legacy `Claims`
   container declaration.

### Defense-in-depth tenant guard preserved

`GetByIdAsync`, `MarkSupersededProjectionAsync`, and
`MarkVoidedProjectionAsync` still perform an in-memory
`response.Resource.TenantId == tenantId` check after the
partition-keyed read. With `/tenantId` partitioning a cross-tenant
lookup surfaces as Cosmos 404 already, but the explicit equality
check is intentionally retained: it makes the tenant-isolation
contract explicit at the read point and catches any future code
path that might bypass the partition-keyed read. Cheap, defensive,
intentionally **NOT** dead code.

### Known follow-up: Payments container

The Payments Cosmos container at
[infrastructure/azure/modules/cosmos-db.bicep](../../infrastructure/azure/modules/cosmos-db.bicep)
also declares `/memberId` — out of scope for 5.1b. Worth a focused
follow-up PR (mirroring the 5.1b shape) when payment-service
operational pressure justifies it.

### Operator runbook

[`docs/migrations/claims-cosmos-partition-migration.md`](../migrations/claims-cosmos-partition-migration.md).

## Tests

`tests/CloudHealthOffice.ClaimsService.Tests/`:
- `Models/ClaimVersionStateTests.cs` — enum ordering, PR #705
  conventions, JSON wire format pinned.
- `Services/ClaimVersionEventPublisherTests.cs` — monotonic version,
  idempotency on duplicate `EventId`, cross-tenant isolation, partition
  key shape, deterministic event ids.
- `HostedServices/ClaimVersionEventIndexInitializerTests.cs` — both
  unique indexes created at startup; idempotent on repeat runs;
  collection-name override honored.
- `Repositories/ClaimRepositoryVersioningTests.cs` — legacy hydration,
  CreateAsync seeds chain, UpdateAsync rejects terminal states,
  GetLatestVersion / GetVersion / ListVersions, the
  `UpdateAdjudicationProjectionAsync` bypass behavior, accumulator
  filter co-existence (legacy ClaimStatus + new ClaimVersionState).

EphemeralMongo7 backs the Mongo-touching tests; the Cosmos repository
exercises share their behavior with the Mongo backend through the
common `IClaimRepository` interface.

## Out of scope for 5.1a

- **Cosmos partition-key migration** — shipped in 5.1b (see
  "Cosmos partition key — `/tenantId` (5.1b)" above).
- **Kafka `claims.versions.v1` broader-stream topic** — Phase 2, when a
  consumer materializes.
- **`ClaimEventPublisher` Kafka emission refactor** — preserved
  unchanged; existing topics `claims.pended.v1` / `claims.finalized.v1`
  continue as before.
- **`ClaimStatus` enum migration to PR #705** — separate enum-hygiene
  PR.
- **`GetAccumulatorTotalsAsync` Cosmos string-vs-int filter quirk** —
  pre-existing; tracked outside 5.1.
- **Adapter pattern (5.2)**, **Submission API (5.3)**, **Adjudication
  Pipeline (5.5)**, **FHIR ExplanationOfBenefit projection (5.11)**,
  **Adjustment Workflow (5.12)** — all consume the version chain
  established here; each ships in its own PR.
