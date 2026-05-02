# Claim Adjustment Workflow (Capability 5.12a)

## What this capability ships

5.12a ships the **operator-initiated adjustment workflow** that
exercises the 5.1a versioning infrastructure for the first time
in production. An authorized operator submits a corrected claim
that re-runs the full 6-stage adjudication pipeline; the
predecessor version is superseded and prepared for reversal; the
new version is created as a fresh row on the same
`ClaimVersionId` chain with `PredecessorVersionId` set.

5.12a is the **first half** of the 5.12 capability, split per the
Plan-First ratification. 5.12b ships the payment-service
`ReversalRunService` that batches the predecessor accumulator
reversal (via the BP engine) + 835 reversal envelope emission +
the predecessor's final transition to `ClaimStatus.Voided`. See
[claim-reversal-run.md](claim-reversal-run.md) for the 5.12b
detail; the lifecycle wiring described below
(`AwaitingReadjudication → PendingReversal → Active`) is where
the two halves compose.

## Surface

### claims-service

| Endpoint | Behavior |
|---|---|
| `POST /api/v1/claims/{predecessorClaimId}/adjustments` | Create new adjustment. Idempotent on `Idempotency-Key` header (same key + same body → 200; same key + different body → 409). |
| `GET /api/v1/claims/{predecessorClaimId}/adjustments` | List adjustments scoped to a predecessor (Phase 1 returns 0 or 1 row). |
| `GET /api/v1/adjustments?status=...` | Filtered list across the tenant. Consumed by 5.12b's `ReversalRunService` for batch creation. |
| `GET /api/v1/adjustments/{id}` | Fetch a single adjustment by id. |

### benefit-plan-service

| Endpoint | Behavior |
|---|---|
| `POST /api/v1/adjudication/reverse-claim` | Idempotent reversal of a claim's accumulator impact via `IBenefitCalculationEngine.ReverseClaimAsync`. The engine path was already wired through `ChoAccumulatorService.ReverseAsync` with `IsReversed=true` journaling since BP 5.10; 5.12a only adds the HTTP wrapper. Returns `204 No Content` on success. |

## Lifecycle (Decision 18 — ratified order)

```
       operator submits adjustment
                 │
                 ▼
   ┌─────────────────────────────┐
   │  AwaitingReadjudication     │  ← initial state on creation
   │  - new version persisted    │
   │  - predecessor superseded   │
   │  - pipeline running async   │
   └──────────────┬──────────────┘
                  │ (5.12b — pipeline finalize callback,
                  │  not shipped in 5.12a)
                  ▼
   ┌─────────────────────────────┐
   │  PendingReversal            │  ← new version Adjudicated/Paid;
   │  - predecessor accums still │     predecessor still has impact
   │    booked                   │
   │  - awaiting ReversalRun     │
   └──────────────┬──────────────┘
                  │ (5.12b — operator runs ReversalRun;
                  │  BP engine reversal + predecessor void
                  │  + reversal 835 emission)
                  ▼
   ┌─────────────────────────────┐
   │  Active                     │  ← terminal happy-path
   │  - accumulators unwound     │
   │  - predecessor Voided       │
   │  - reversal 835 emitted     │
   └─────────────────────────────┘

   Off-path: any step's unrecoverable failure → Failed
             (FailureReason carries diagnostic)
```

**Why this order matters.** The original prompt specified
`PendingReversal → AwaitingReadjudication → Active`, which is
impossible: re-adjudication must run *before* the reversal can
batch (the new version anchors what the reversal is replacing).
Plan-First caught the inversion. The corrected order reflects
the actual workflow: re-adjudication runs synchronously
(via `IClaimSubmissionService.SubmitAsync`), the pipeline runs
asynchronously via the existing Service Bus subscription, and
reversal is the operator-batched terminal step.

## Cross-service event surface

5.12a emits **three new signals**, all keyed to the predecessor:

| Signal | Transport | Consumer |
|---|---|---|
| `ClaimVersionSuperseded` | Mongo `ClaimVersionEvents` stream | Audit/lineage. 5.5 path. |
| `ClaimVersionReversed` (NEW, value `7`) | Mongo `ClaimVersionEvents` stream | Audit/lineage; future FHIR `_history`. |
| `ClaimVersionReversedMessage` (NEW) | Service Bus `claim-version-events` topic, MessageType=`ClaimVersionReversed` | 5.12b `ReversalRunService` reactive batch trigger. |

The new version's submission emits the standard
`ClaimVersionSubmitted` (Mongo + Service Bus) per 5.5; that's
not a 5.12a-new signal — it's the existing dual-emit path being
exercised on a versioned-row creation.

## Decision 16 — accumulator-service drift acceptance

Per the Plan-First Decision 16 ratification, accumulator
reversal is routed through the **BP engine path** (BP service
exposes the new HTTP endpoint; engine internals already journal
`IsReversed=true` per claim with idempotency). The
**accumulator-service `AccumulatorSnapshot` Kafka projection
will briefly drift** after a reversal until its next refresh
cycle.

This drift is **acceptable** because:

1. **Source of truth is the engine `AccumulatorDocument`** — the
   member-facing accumulator state and the next adjudication
   pipeline run both read through the BP engine, which sees
   the reversed state immediately.
2. **`AccumulatorSnapshot` is an internal read projection**
   used for member-portal historical reads and operator audit
   panes. A bounded drift window (≤ next snapshot rebuild) is
   operationally acceptable for those consumers.
3. **The accumulator-service Kafka consumer is non-reversal-aware**
   today: dedupe is keyed by `(TenantId, ClaimId)` and never
   reads `FinalStatus`, so emitting a "Reversed" event for the
   predecessor would be silently dropped as duplicate. Routing
   reversal through the engine path avoids the silent-drop
   bug entirely.

**Recovery path.** If member-portal reads surface stale
accumulator amounts after a reversal:

- Operators can trigger a snapshot rebuild for the affected
  member by replaying recent finalize events (existing tooling).
- Phase 2 will extend `accumulator-service.ClaimFinalizedConsumer`
  with EventId-keyed dedup + signed deltas + a `Reverse` branch
  on `ApplyClaimFinalizedAsync` (~150 LOC) so the snapshot
  stream becomes reversal-native. Tracked as a deferred
  follow-up; not gated on 5.12a or 5.12b.

## AI examination on adjustment versions (Gap 6)

The new version runs **fresh AI examination** — the
predecessor's `Claim.AiExamination` snapshot is not carried
over. The adjustment service zeroes
`AdapterClaim.AiExamination`, `AdapterClaim.PendDetails`, and
`AdapterClaim.AdjudicationResult` on the corrected payload
before persistence so a stale predecessor signal cannot leak
into the new version's pipeline run.

The predecessor's `AiExamination` remains accessible via
`PredecessorVersionId` chain navigation for audit
reconstruction.

## Why `ClaimFinalizationService.VoidAsync` extends rather than
splits (Gap 1)

`ClaimFinalizationService` already owns the
Adjudicated → Paid transition with the unified
version-event-emission + Kafka-emission posture. Extending it
with a sibling `VoidAsync` keeps both lifecycle writes inside
one service, sharing:

- The repository projection-bypass write paths
  (`MarkVoidedProjectionAsync` / `MarkSupersededProjectionAsync`
  added in 5.12a)
- The dual-emit `IClaimVersionEventPublisher` +
  `IClaimEventPublisher` posture
- The idempotency conventions (already-Voided is a no-op, same
  shape as already-Paid)
- The error-mapping enums (`ClaimVoidOutcome` mirrors
  `ClaimFinalizationOutcome` shape)

A sibling `IClaimVoidService` would have duplicated all of the
above for one method.

## Constraints (deferred to Phase 2)

- **Adjustment chain depth > 1** (Decision 11). Phase 1 enforces
  depth=1 via the `(TenantId, ClaimVersionId)` unique index on
  `ClaimAdjustment` + an explicit `predecessor.PredecessorVersionId == null`
  check in the service layer. Phase 2 widens the key with a
  generation field.
- **Programmatic / event-driven adjustment triggers** (Decision 1).
  Phase 1 ships operator-initiated only; no
  claims-examiner-service auto-recommendation, no inbound 837
  corrected-claim parser.
- **Auto-reverse mode** (Decision 2). Phase 1 ships manual-batched
  reversal via 5.12b `ReversalRun`; no automatic reversal on
  adjustment creation.
- **Partial-edit semantics** (Decision 4). Phase 1 ships full
  re-adjudication only — operator submits a full corrected
  payload; the new version runs through all 6 pipeline stages.
- **Reversal-only operations without re-adjudication.** Pure
  voids (operator decides claim should be deleted, no
  replacement) are captured by the existing
  `ClaimVersionVoided` semantic but do not flow through 5.12b's
  `ReversalRun` queue in Phase 1.
- **`AccumulatorSnapshot` reversal-native consumer**
  (Decision 16). See drift-acceptance section above.
- **Predecessor void via `ReversalRun`** (Gap 1 follow-up).
  5.12a wires `ClaimFinalizationService.VoidAsync`; 5.12b's
  `ReversalRunService` invokes it as the terminal step of a
  reversal batch.
- **FHIR `_history` operation.** 5.12a's chain becomes the
  foundation; the operation lands in a future capability.

## Cross-references

- [claim-versioning.md](claim-versioning.md) — 5.1a versioning
  fields + Mongo event chain. 5.12a is the first production
  consumer of `Claim.PredecessorVersionId`.
- [claim-adjudication-pipeline.md](claim-adjudication-pipeline.md)
  — 5.5 orchestrator + pipeline stages. Re-adjudication of an
  adjustment version runs the same pipeline unchanged; the
  PersistenceStage handles `PredecessorVersionId != null`
  transparently.
- [claim-remittance-generation.md](claim-remittance-generation.md)
  — 5.10 PaymentRun + `ClaimFinalizationService`. 5.12a
  extends `ClaimFinalizationService` with `VoidAsync`. The
  5.12b `ReversalRun` is structurally separate from
  PaymentRun (distinct controller, distinct repository,
  distinct envelope type) but shares
  `BatchEraGeneratorService` + `CarcRarcMappingService`
  extensions.
- [accumulator-service.md](accumulator-service.md) — see
  Decision 16 drift-acceptance section above for how 5.12a
  interacts with the `AccumulatorSnapshot` projection.

## Code touchpoints

| Surface | Path |
|---|---|
| `ClaimAdjustment` aggregate | `src/services/claims-service/Models/ClaimAdjustment.cs` |
| `ClaimAdjustmentRequest` / list filter | `src/services/claims-service/Models/ClaimAdjustmentRequest.cs` |
| Repository (Mongo + Cosmos noop) | `src/services/claims-service/Repositories/ClaimAdjustmentRepository.cs` |
| Service | `src/services/claims-service/Services/ClaimAdjustmentService.cs` |
| Controller | `src/services/claims-service/Controllers/ClaimAdjustmentsController.cs` |
| `ClaimVersionReversed = 7` enum value | `src/services/claims-service/Models/ClaimVersionEvent.cs` |
| `ClaimVersionReversedMessage` | `src/services/claims-service/Models/Messaging/ClaimVersionMessages.cs` |
| `IClaimVersionEventPublisher.PublishVersionReversedAsync` | `src/services/claims-service/Services/ClaimVersionEventPublisher.cs` |
| Supersession + void projection bypass | `src/services/claims-service/Repositories/ClaimRepository.cs` (+ Mongo sibling) |
| `ClaimFinalizationService.VoidAsync` | `src/services/claims-service/Services/ClaimFinalizationService.cs` |
| `HttpBenefitCalculationEngineClient.ReverseClaimAsync` (real wire) | `src/services/claims-service/Services/Adjudication/HttpBenefitCalculationEngineClient.cs` |
| BP `POST /api/v1/adjudication/reverse-claim` | `src/services/benefit-plan-service/Controllers/AdjudicationController.cs` |
