# Claim Adjudication Pipeline (Capability 5.5)

> **Status — Phase 1 foundation, April 2026.** Ships the orchestrator,
> stage interface, BenefitCalculation + Persistence stages, five stub
> stages, Service Bus trigger transport, and resolver clients. Capabilities
> 5.4 / 5.6 / 5.7 / 5.8 / 5.9 each replace one stub stage via DI swap.
> 5.4 (Scrubbing) and 5.6 (NetworkCredentialing) are now live — see
> [`claim-scrubbing-pipeline.md`](./claim-scrubbing-pipeline.md) for
> the structural-validation stage.

## Why this exists

Before 5.5 the claim version chain ended at `Submitted` and stayed there
forever — there was no production code path that produced an
`AdjudicationResult` on a claim. The 5.1a projection-bypass method
`IClaimRepository.UpdateAdjudicationProjectionAsync` had been registered
on the repository interface since January 2026 with zero callers.

5.5 introduces the orchestration seam that turns a submitted claim into
an adjudicated one. It is the architecturally load-bearing PR of Claims
Phase 1: every subsequent claims-Phase-1 capability replaces one stage in
this pipeline rather than building its own orchestration.

## Pipeline shape

```
POST /api/v1/claims                                     (capability 5.3)
   │
   ├─ Mongo append-only ClaimVersionSubmitted event     (system of record)
   └─ Service Bus → claim-version-events                (trigger transport)
                              │
                              ▼
            adjudication-orchestrator subscription      (capability 5.5)
                              │
                              ▼
   ┌──────────────────────────────────────────────────────────────┐
   │  IClaimAdjudicationOrchestrator                              │
   │   ├── re-fetch via IClaimAdapter.GetClaimAsync(...)          │
   │   ├── resolve plan (cached)  • resolve member (cached)       │
   │   └── iterate stages by Order ascending:                     │
   │                                                              │
   │       100  ScrubbingStage               ★ real (5.4)         │
   │       200  NetworkCredentialingStage    ★ real (5.6)         │
   │       300  BenefitCalculationStage      ★ real (5.5)         │
   │       400  NcciEditsStubStage           (5.7 replaces)       │
   │       500  CoordinationOfBenefitsStubStage (5.8 replaces)    │
   │       600  AiExaminationStubStage       (5.9 replaces)       │
   │       999  PersistenceStage             ★ real (5.5)         │
   └──────────────────────────────────────────────────────────────┘
                              │
   ┌──────────────────────────┴───────────────────────────────────┐
   │  Mongo append-only ClaimVersionAdjudicated event             │
   │  Service Bus → claim-version-events (MessageType=Adjudicated)│
   └──────────────────────────────────────────────────────────────┘
```

After PersistenceStage runs, the orchestrator emits a
`ClaimVersionAdjudicatedMessage` back onto the same topic. Future
capabilities (5.10 remittance, 5.12 adjustment workflow, ...) attach
their own subscriptions filtered on `MessageType=ClaimVersionAdjudicated`.

## Decisions

### D1 — Service Bus is the trigger transport

`IMessageBus` (the platform's canonical async-messaging abstraction). Not
Mongo change streams (no service uses them; new pattern), not Kafka
(`IMessageBus` doc says "Kafka usage stays on its own dedicated client —
IMessageBus is not a Kafka facade").

### D2 — Single broad topic + per-consumer subscription

Topic `claim-version-events`; subscription `adjudication-orchestrator`.
Mirrors `appeals-api`'s `payer-appeal-status-updates` topic +
`clearinghouse-push` subscription pattern. New downstream subscribers
(5.10, 5.12) add their own subscriptions to the same topic with their own
filter rules.

### D3 — Bicep delta lives in `main.bicep`

Topic + subscription + correlation filter (`MessageType=ClaimVersionSubmitted`)
declared in `infrastructure/azure/main.bicep`. No new
`claims-service.bicep` module — the service has nothing else infra-specific
that warrants one.

### D4 — Dual emission on submission

`ClaimSubmissionService` emits **both** the Mongo append-only event
(system-of-record audit chain — preserved from 5.3) **and** the Service
Bus topic message (NEW trigger transport). Order: Mongo first, Service
Bus second. Service Bus failure does not fail the submission (degraded
mode); the audit chain captures every submission either way.

### D5 — Synchronous pipeline within one Service Bus message

When the orchestrator consumes a `ClaimVersionSubmittedMessage`, all
stages run sequentially in the message handler. Async-between-stages is
not in scope. If 5.9 (AI examination) needs async semantics it adds a
separate subscription rather than fragmenting the main pipeline.

### D6 — Stage ordering is fixed in code, not config

Per-tenant configuration (`AdjudicationPipelineOptions.EnabledStages`)
controls only **whether** a stage runs, never the order. Stage ordering
is platform-level architecture (NCCI before COB before persistence,
etc.); tenant-configurable order would produce inconsistent adjudication
across tenants. Pattern parity:
`BenefitCalculationEngine`'s internal phase ordering is also fixed.

### D7 — Short-circuit on terminal stage failure

`Reject` and `Deny` outcomes set `Continue=false`; remaining
non-persistence stages skip. PersistenceStage **always** runs (forced
via `IsRequired=true`) so the version chain captures the failure
outcome. `Pend` is recoverable — pipeline continues so subsequent stages
can decorate the result before the claim ends up in a human-review queue.

### D8 — Stage interface

```csharp
public interface IClaimAdjudicationStage
{
    string Name { get; }                  // for telemetry + EnabledStages key
    int Order { get; }                    // 100/200/300/.../999
    bool IsRequired { get; }              // true → bypasses EnabledStages + short-circuit
    Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context, CancellationToken ct);
}
```

`ClaimAdjudicationContext` is **mutable**. Stages append outcomes to
`StageResults`, decorate the building `AdjudicationResult` /
`LineAdjudicationResults`, and set `BenefitResolutionResult` /
`ResolvedPlan` / `ResolvedMember` as they pass through. Single-threaded
within one message handler so mutability is safe.

### D9 — PersistenceStage uses the bypass method

`PersistenceStage` calls
`IClaimRepository.UpdateAdjudicationProjectionAsync(...)`, **not**
`UpdateAsync`. Reasons:

- Adjudication state is operationally distinct from claim identity — the
  whole purpose of the bypass pattern.
- Adjudication writes must not produce a new claim version row;
  per-version churn is reserved for adjustments (5.12) and reversals.
- 5th instance of the projection-bypass pattern across the platform
  (Provider integrity, Provider credentialing, Provider panel-gating, BP
  network tiers, claims adjudication).

### D10 — Resolution clients are cached HTTP clients

`IBenefitPlanResolver` calls benefit-plan-service's
`GET /api/v1/plans/{id}`; `IMemberResolver` calls member-service's
`GET /api/v1/members/{memberId}`. Both wrapped with a 5-minute in-process
TTL via `IMemoryCache`, keyed by `(tenantId, id)`. Pattern parity with
BP 5.6's `CachingServiceCategoryMappingRepository`. Negative results are
not cached so a transient downstream outage doesn't pin "missing".

### D11 — Adjudicated event emission

After the pipeline completes (regardless of outcome), the orchestrator
emits **both** the Mongo append-only `ClaimVersionAdjudicated` audit
event and the Service Bus `ClaimVersionAdjudicatedMessage`. Same
degraded-mode posture as the submission service: each emission is
wrapped independently and a failure logs but does not unwind the run.

### D12 — Idempotency via deterministic MessageId

`ClaimVersionSubmittedMessage` carries `MessageId =
"submitted:{ClaimVersionId}"`; `ClaimVersionAdjudicatedMessage` carries
`MessageId = "adjudicated:{ClaimVersionId}"`. The Bicep topic enables
native Service Bus duplicate detection (`requiresDuplicateDetection: true`
with a 1-hour window). The orchestrator further hardens the idempotent
read path: if a re-delivery fires for an already-adjudicated claim
(`AdapterClaim.AdjudicationResult` populated with
`AllowedAmount > 0`), the handler logs and completes the message
without re-running the pipeline.

### D13 — Replace mode only in Phase 1

`BenefitCalculationStage` calls
`IBenefitCalculationEngine.CalculateAsync(...)`, not the operating-mode
variant. Augment-mode comparison ships in Phase 2 once a real legacy
adjudication result is available to compare against.

### D14 — Engine is consumed via HTTP shim from claims-service

`HttpBenefitCalculationEngineClient` implements `IBenefitCalculationEngine`
in claims-service by calling benefit-plan-service's existing
`POST /api/v1/adjudication/calculate-benefits` endpoint. The engine ships
as a class library (BP 5.10) but its host-side collaborators
(`IBenefitPlanProvider`, `IAccumulatorService`, `IBenefitRuleGate`,
`IServiceCategoryResolver`) are wired in benefit-plan-service against
benefit-plan-service's data stores. Standing them up inside claims-service
would mean importing the entire plan + accumulator data layer — that's a
Phase 2 split. The shim consumes the canonical engine through the same
HTTP surface portal/preview features already use.

`HttpBenefitCalculationEngineClient` only implements the
`CalculateAsync` member; `CalculateWithModeAsync` and `ReverseClaimAsync`
throw `NotImplementedException` because the pipeline doesn't use them.
Adding those surfaces is a follow-up driven by capability 5.12 (adjustment
workflow).

## Configuration

```json
{
  "Messaging": {
    "Backend": "Auto",
    "ServiceBusConnectionString": "..."
  },
  "Adjudication": {
    "Pipeline": {
      "EnabledStages": {
        "Scrubbing": true,
        "NetworkCredentialing": true,
        "BenefitCalculation": true,
        "NcciEdits": true,
        "CoordinationOfBenefits": true,
        "AiExamination": true
      }
    }
  },
  "Services": {
    "BenefitPlanService": "http://benefit-plan-service:8080",
    "MemberService": "http://member-service:8080"
  }
}
```

A missing key in `EnabledStages` is treated as enabled. `Persistence` is
always enabled regardless.

## Runtime guarantees and failure modes

| Failure | Behavior |
|---|---|
| Submission Service Bus emission fails | Submission still returns 201; Mongo audit chain captures the event; operators can replay. |
| Subscription consumer crashes mid-run | Service Bus abandons the message; redelivers up to `MaxDeliveryCount=10`; then DLQs. |
| Stage throws (non-required) | Treated as `Reject`; pipeline short-circuits to PersistenceStage; failure outcome is captured on the version. |
| `UpdateAdjudicationProjectionAsync` returns false | PersistenceStage returns `Reject`; orchestrator continues (no further work) and emits the adjudicated event with that outcome. |
| `UpdateAdjudicationProjectionAsync` throws | PersistenceStage rethrows; Service Bus abandons the message and redelivers. |
| Re-delivery for already-adjudicated claim | Orchestrator detects via populated `AdjudicationResult` and completes the message without re-running the pipeline. |

## Replacement contract for stub stages

Capabilities 5.4 / 5.6 / 5.7 / 5.8 / 5.9 each ship a real stage that:

1. Implements `IClaimAdjudicationStage` with the **same `Name`** and
   **same `Order`** as the stub it replaces. Same `Name` keeps tenant
   `EnabledStages` config working unchanged across the swap; same
   `Order` keeps platform-level pipeline semantics stable.
2. Removes the stub registration via `services.RemoveAll<StubStage>()`
   and adds the real registration.
3. Adds its own collaborators (engine integrations, downstream
   resolvers) to the DI graph.
4. Adds its own configuration section if it needs feature flags or
   tunables — does NOT extend `AdjudicationPipelineOptions`.
5. Updates this document with its real stage's behavior.

## Cross-references

- [`claim-scrubbing-pipeline.md`](./claim-scrubbing-pipeline.md) —
  structural validation at Order=100 (capability 5.4)
- [`claim-versioning.md`](./claim-versioning.md) — version chain that
  PersistenceStage's bypass writes against
- [`claim-adapter-pattern.md`](./claim-adapter-pattern.md) — adapter
  surface the orchestrator uses for canonical claim reads
- [`claim-submission-api.md`](./claim-submission-api.md) — entry point
  that emits `ClaimVersionSubmittedMessage`
- [`adjudication-api-stabilization.md`](./adjudication-api-stabilization.md)
  — Phase 1 closer, capability 5.13
- [`accumulator-service.md`](./accumulator-service.md) — Kafka stream
  that stays independent of this Service Bus topic
- [`benefit-plan-adapter-pattern.md`](./benefit-plan-adapter-pattern.md)
  — plan resolution surface the resolver decorator caches
