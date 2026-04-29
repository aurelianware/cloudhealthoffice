# Claim Identity & Versioning

Status: 5.1a — initial implementation (versioning fields + event chain).
Cosmos partition-key migration deferred to 5.1b (infra-coordinated PR).
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
├── Id                       == EventId (cross-tenant collision-safe)
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

## Cosmos partition key — current state and 5.1b plan

The Claims Cosmos container partitions by document `Id` (per the
runtime call sites in `ClaimRepository`). The Bicep template at
[infrastructure/azure/modules/cosmos-db.bicep](../../infrastructure/azure/modules/cosmos-db.bicep)
declares `/memberId`. This divergence pre-exists 5.1 and is tracked
outside this PR's scope.

Provider/BP partition by `/TenantId` (single key). Pattern parity
argues Claims should match. The migration is:

1. Update Bicep to declare `partitionKey: ['/TenantId']`.
2. Create a new container with the new partition path.
3. One-shot or admin-callable migration job copies existing claim
   documents to the new container, computing `ClaimVersionId` (set
   equal to existing `Id` for legacy single-version rows).
4. Update service config to point at the new container.
5. Verify operational, then deprecate the old container.

Per modernization-PR discipline (don't fix platform-wide concerns in
single-service PRs), this is **deferred to PR 5.1b**, an
infrastructure-coordinated follow-up. The 5.1a versioning model lives
within the existing partition strategy; 5.1b switches the call sites
from `new PartitionKey(claim.Id)` to `new PartitionKey(tenantId)`.

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

- **Cosmos partition-key migration** — 5.1b.
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
