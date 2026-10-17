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

## Claim lifecycle: Pay / Deny before Paid / Denied

Modeled on QNXT's PAY/PAID and DENY/DENIED (owner-approved product
decision). An adjudicated outcome stays editable until the payment cycle
tells the provider; only then is it final.

| Phase | `ClaimStatus` (portal label) | `VersionState` | Editable? |
| --- | --- | --- | --- |
| Adjudicated, not yet remitted | `Pended`, `Approved` ("Pay"), `Deny` (= 10) | `Submitted` (Pended), `Adjudicated` (Pay / Deny) | Yes — reprocess, examiner resolve / override (`POST work-queue/{id}/resolve`, `/override`) |
| Finalized by the payment cycle (835 sent) | `Paid`, `Denied` | `Paid`, `Denied` (terminal) | No — only an adjustment (new version, `POST /api/v1/claims/{id}/adjustments`) or, for Paid, a void / reversal |
| Corrected / reversed | `Voided` (and any row `Adjusted`) | `Voided`, `Adjusted` (terminal) | No |

- The pipeline's Deny / Reject persists `Deny`, never `Denied`
  (`PersistenceStage`). Pass persists `Approved`.
- The payment run remits `Approved` claims (paid) and `Deny` claims ($0,
  CLP02 = 4) in its 835s, then finalizes each through
  `POST /api/claims/{id}/remittance` (`ClaimFinalizationService.FinalizeAsync`):
  `Approved` → `Paid`, `Deny` + $0 → `Denied`. A $0 remittance on an
  already-`Denied` claim is an idempotent 200; a paid remittance on
  `Deny` / `Denied` is 422.
- A resolve / override of a finalized claim (`Paid`, `Denied`, `Voided`,
  `Adjusted`) is 422 with a pointer to the adjustment / void path. An
  examiner's "Denied" disposition writes `Deny`.
- Accumulators: a move to `Deny` takes back what the claim contributed with a
  NON-terminal reversal (`reverse-claim` with `terminal: false` →
  `IAccumulatorService.ReverseAsync`, no fence), so a claim reprocessed back
  to Pay commits again (new commit id) and counts once. From Pay the reversal
  is always queued; from Pended / Deny only if the store reports a write for
  the claim (`accumulator-commits/lookup`), else nothing. `Deny` → `Denied`
  adds nothing. Voids and replacements keep the terminal (fencing) reversal.
  The reversal is commit-scoped (compare-and-reverse): it names the claim's
  `LatestAccumulatorCommitId` (recorded whenever a commit is queued) and
  takes it back only while that commit is the applied one — if the claim went
  back to Pay and a newer commit landed, it is a no-op (`Superseded`). It
  also fences that commit id (Cho `ReversedCommitIds`, Redis
  `__fence:commit:{id}`), so the same commit arriving late is refused
  (`RefusedCommitReversed`, dropped by the outbox) while a newer one is not.
  A re-Pay write cancels a still-queued reversal in the same lock-fenced
  write. Claims whose commit is unknown (legacy) carry the sentinel
  `unknown` and reverse blind, as before. The outbox re-reads the claim at
  dispatch, so a queued commit on a claim now Deny / Denied / Voided is
  dropped.
- `PartiallyPaid` (= 9) is legacy: never assigned (adjudication never set
  it), still read as payable and, via `VersionState=Paid`, finalized. The
  portal derives "Paid with line denials" from the lines instead.
- `ClaimStatusSemantics` (claims-service) answers "denied in either
  phase", "payable", "finalized", "reprocessable" so call sites don't
  compare to individual statuses.
- Existing rows: `Denied` claims set at adjudication before `Deny` existed
  are moved by the opt-in backfill (docs/operations/CLAIM-DENY-STATUS-BACKFILL.md).

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
                                Adjudicated (Deny) │ $0 remittance (835 sent)
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
- **Adjudicated** — adjudication has run, the payment cycle has not
  finalized it. The `ClaimStatus.Approved` ("Pay") and `ClaimStatus.Deny`
  operational sub-states live here (await the payment run).
  Re-adjudication is permitted via `UpdateAdjudicationProjectionAsync`
  without rolling a new version.
- **Paid** — terminal for paid claims. The `ClaimStatus.PartiallyPaid`
  operational sub-state lives here too.
- **Denied** — terminal: the denial was finalized by the payment cycle
  (its 835 went out; `Deny` → `Denied` on the $0 remittance).
- **Adjusted** — terminal for the predecessor of an adjustment chain.
  `SupersededByVersionId` points at the replacement version.
- **Voided** — terminal for reversed/cancelled claims. Every void
  (`POST {id}/void`, `DELETE {id}`, `PUT {id}/status` with `Voided`)
  goes through `IClaimFinalizationService.VoidAsync`: Paid /
  PartiallyPaid / Adjusted only (422 otherwise), with the terminal
  accumulator reversal saved by the void write.

`UpdateAsync` rejects writes against `Paid`, `Denied`, `Voided`, and
`Adjusted` rows with `ClaimVersionStateException`. The adjustment
workflow (5.12) is the only legitimate path forward from those terminal
states; it creates a new version with `PredecessorVersionId` pointing
at the old row and marks the old row `Adjusted`.

## Hydration: legacy ClaimStatus → ClaimVersionState

| Legacy `ClaimStatus`                                  | Hydrated `ClaimVersionState` |
| ----------------------------------------------------- | ---------------------------- |
| `Submitted`, `Received`, `InAdjudication`, `Pended`   | `Submitted`                  |
| `Approved` ("Pay"), `Deny`                            | `Adjudicated`                |
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

## Storage

Claims, their version events and adjustments are stored in MongoDB only
(ADR 006). The 5.1b Cosmos partition-key migration (`ClaimsV2`, the
`/api/v1/admin/claims/cosmos-migration` endpoints and their runbook) went
with the Cosmos claim store in remediation Phase 5b. Every read and write
filters on `TenantId`; `GetByIdAsync`, `MarkSupersededProjectionAsync` and
`MarkVoidedProjectionAsync` also check the loaded row's `TenantId`.

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

EphemeralMongo (MongoDB 7, one mongod per test collection via `tests/Shared/EphemeralMongo/MongoRunnerFixture.cs`) backs the repository tests.

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
