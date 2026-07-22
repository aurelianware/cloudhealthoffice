# Claim Adjudication Pipeline (Capability 5.5)

> **Status — Phase 1 foundation, April 2026.** Ships the orchestrator,
> stage interface, BenefitCalculation + Persistence stages, five stub
> stages, Service Bus trigger transport, and resolver clients. Capabilities
> 5.4 / 5.6 / 5.7 / 5.8 / 5.9 each replace one stub stage via DI swap.
> All five are now live — **6/6 pipeline stages real after 5.9, May
> 2026**. See
> [`claim-scrubbing-pipeline.md`](./claim-scrubbing-pipeline.md),
> [`network-credentialing-enforcement.md`](./network-credentialing-enforcement.md),
> [`claim-ncci-pipeline.md`](./claim-ncci-pipeline.md),
> [`claim-cob-pipeline.md`](./claim-cob-pipeline.md), and
> [`claim-ai-examination.md`](./claim-ai-examination.md) for the
> stage-specific architecture. The downstream
> [`claim-remittance-generation.md`](./claim-remittance-generation.md)
> doc covers capability 5.10's operator-initiated finalization
> handoff (Adjudicated → Paid via batched 835 emission).
>
> **Addendum, July 2026 — ProviderIntegrityStage added.** Capability
> 5.10 (see
> [`integrity-score-consumption.md`](./integrity-score-consumption.md))
> built `HttpProviderIntegrityGate` — the federal OIG/LEIE/SAM.gov
> exclusion check — into benefit-plan-service, but it was only ever
> wired into the standalone `AdjudicationController.Adjudicate` HTTP
> endpoint, never into this orchestrator. The original 6-stage scope
> above never included a provider-integrity stage at all, so claims
> processed through this pipeline (`BenefitCalculationStage` →
> `calculate-benefits`, which stays exclusion-check-free by design —
> see D14) were never checked against federal exclusion lists. Found
> during a Million Claim Challenge scale confirmation; fixed by adding
> `ProviderIntegrityStage` (Order=150) as a 7th stage, reached via a
> new side-effect-free endpoint
> (`GET /api/v1/adjudication/provider-integrity/{npi}`) rather than
> folding the check into `calculate-benefits` itself. See "Provider
> integrity stage (added July 2026)" below.

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
   │       150  ProviderIntegrityStage       ★ real (added 7/26)  │
   │       200  NetworkCredentialingStage    ★ real (5.6)         │
   │       300  BenefitCalculationStage      ★ real (5.5)         │
   │       400  NcciEditsStage               ★ real (5.7)         │
   │       500  CoordinationOfBenefitsStage  ★ real (5.8)         │
   │       600  AiExaminationStage           ★ real (5.9)         │
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
not in scope. 5.9 (AI examination) takes exactly that path: the
synchronous pipeline-stage emits a `ClaimPendedEvent` to Kafka and
returns Pend; the AI work itself runs entirely in
claims-examiner-service via a separate Kafka consumer. See
[`claim-ai-examination.md`](./claim-ai-examination.md).

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
can decorate the result before the claim ends up in a human-review queue
(see D9a for how PersistenceStage actually projects `Pend` onto
`ClaimStatus` so the work queue can find it — that projection was missing
until the pend-persistence defect fix).

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

#### D9a — Pend → ClaimStatus projection and its precedence rule (pend-persistence defect fix)

Before this fix, `UpdateAdjudicationProjectionAsync`'s patch operation
list never included `/status` — an orchestrator-computed `Pend` (NCCI/MUE,
COB) populated `PendDetails` (NCCI) or nothing at all (COB, see
`claim-cob-pipeline.md` D4) but never moved `ClaimStatus` off whatever it
was before adjudication ran. Since the examiner work queue filters on
`ClaimStatus.Pended` (`ClaimsController.GetWorkQueueSummary` /
`GetWorkQueueItems`), orchestrator-pended claims never reached a human
examiner. See the dated diagnostics doc
(`docs/million-claim-challenge/2026-07-07-expected-pend-diagnostics.md`)
for the full trace.

Fixed as follows:

1. `PersistenceStage` resolves `isPend` from every stage result recorded
   **before** Persistence runs (Persistence is always `Order=999`, i.e.
   last), using the same Reject > Deny > Pend > Pass precedence the
   orchestrator uses for the emitted event's `Outcome`
   (`ClaimAdjudicationStageResult.ResolveOutcome` — a single shared
   static method; the orchestrator's own `ResolveFinalOutcome` now
   delegates to it, called one stage later so a Persistence-stage
   `Reject` still wins on the emitted event).
2. `isPend` and `PendDetails` are passed to
   `UpdateAdjudicationProjectionAsync`. `isPend=false` is a **pure no-op**
   on `/status` — Pass/Deny/Reject outcomes never touch it, exactly as
   before this fix. Status transitions for those outcomes remain owned by
   other write paths (`UpdateAdjudicationSummaryAsync`, the Argo
   workflow's `update-claim-step`, or `ClaimsController.PendClaim`), not
   this bypass method — this fix does not add terminal-status writes here.
3. **Precedence rule** (documented here — the source of truth, not just
   the PR that introduced it): when `isPend` is true, the repository ALSO
   patches `/status` to `ClaimStatus.Pended`, *unless* the claim's current
   `Status` is already a later-stage disposition — `Approved`, `Denied`,
   `Paid`, `PartiallyPaid`, or `Voided` (`ClaimRepository.IsFinalDisposition`,
   shared by both the Cosmos and Mongo repositories). This guards against
   downgrading a claim that another write path — most plausibly the Argo
   workflow's synchronous `update-claim-step`, or an examiner's
   work-queue override — finalized before this async projection landed.
   `Pended` is deliberately **not** in that list: re-pending an
   already-`Pended` claim (a re-adjudication run that refreshes
   `PendDetails`, e.g. a different edit-failure set) is allowed.
4. `CoordinationOfBenefitsStage` now populates `PendDetails`
   (`PendCode="COB"`) whenever it detects a secondary/tertiary scenario or
   a coverage-service outage, mirroring `NcciEditsStage`'s existing
   precedent of recording the deterministic snapshot regardless of
   enforcement mode (so a Deny-mode COB claim still carries an audit
   trail even though it ends up `Denied`, not `Pended` — Deny outweighs
   Pend in the same precedence rule). No new pend vocabulary: `"COB"` was
   already a documented `PendDetails.PendCode` value and the work queue
   already had a `CobRequired` bucket keyed on it; the stage simply never
   emitted it before.

#### D9b — Synchronous write-back preserves existing pend/final statuses

After D9a, a residual race remained: the Argo workflow's synchronous
`update-claim-step` could call `PUT /api/claims/{id}/adjudication`,
`PUT /api/claims/{id}/adjudication-summary`, or
`PUT /api/claims/{id}/status` after the async orchestrator had already
projected `ClaimStatus.Pended`. That later synchronous write-back only
knew its locally computed payable/deniable disposition, so it could stomp
the pended status back to `Approved` / `Denied` while leaving
`PendDetails` behind.

Fixed as follows:

1. Claims-service status writes now route through
   `IClaimRepository.TryTransitionStatusAsync` /
   `UpdateAdjudicationSummaryAsync`, which apply the shared
   `ClaimRepository.BlocksSynchronousWriteback` guard before status is
   patched.
2. The guard suppresses synchronous write-back when the persisted claim is
   already `Pended` or at a final disposition (`Approved`, `Denied`,
   `Paid`, `PartiallyPaid`, or `Voided`). It still allows normal
   `Received` / `Submitted` / `InReview` transitions.
3. Adjudication totals, timings, denial codes, MPIP fields, and audit data
   still persist even when the status patch is suppressed. Only `/status`
   is protected.
4. Suppressed fast-summary writes return `200 OK` with
   `AdjudicationSummaryWriteResponse` (`StatusPreserved=true`,
   `PersistedStatus=<current status>`) instead of `204 No Content` so the
   MCC validator can score against the authoritative persisted status.
   Unsuppressed writes retain the previous `204 No Content` behavior.
5. Suppressed full adjudication/status writes fold the persisted status
   back into the response payload and skip lifecycle side effects derived
   from a transition that did not actually apply.

This guard is intentionally not a human pend-resolution path. Explicit
examiner override remains owned by `POST work-queue/{id}/override` so a
generic workflow write-back cannot accidentally resolve a pend.

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

## Provider integrity stage (added July 2026)

`ProviderIntegrityStage`, `Order=150`, runs the same federal-exclusion
(OIG/LEIE/SAM.gov) check `HttpProviderIntegrityGate` has always run for
`AdjudicationController.Adjudicate` callers — reached here through a new,
side-effect-free endpoint rather than through `calculate-benefits`
(D14's shim target), which stays exclusion-check-free by design since
portal/preview features also call it and must not be blocked by a live
exclusion check on a hypothetical calculation.

- Checks both `BillingProviderNPI` and `RenderingProviderNPI` (when
  distinct) — mirrors `NetworkCredentialingStage`'s dual-provider check.
- A confirmed exclusion (`ProviderIntegrityResult.IsExcluded`) is a
  `Deny`; `AdjudicationResult.DenialReasonCode`/`DenialReason` are set
  from the gate's response before the stage returns.
- Anything the gate could not confidently resolve either way
  (`RequiresManualReview`, or the HTTP call to benefit-plan-service
  itself failing) is a `Pend` with `PendCode="MEDREVIEW"` — an
  already-recognized `PendDetails.PendCode` value with an existing
  work-queue bucket (`ClaimsController`'s "Medical Review" category).
  No new pend vocabulary introduced.
- Unlike `NetworkCredentialingStage`, this stage has no tenant-configurable
  fail-open mode — a federal exclusion check has no legitimate advisory-only
  posture. It can be disabled entirely via `EnabledStages` (same contract
  every stage has), but while enabled it always enforces.
- `HttpProviderIntegrityGate` itself never fails open: total unavailability
  (both provider-service and provider-verification-service unreachable) or
  a live `Failed`/`ManualReviewRequired` verification status both resolve
  to `Passed=false` + `RequiresManualReview=true`, distinct from a
  confirmed `IsExcluded` finding. See
  [`integrity-score-consumption.md`](./integrity-score-consumption.md).

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
        "ProviderIntegrity": true,
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
| Orchestrator resolves `Pend`, but claim is already `Approved`/`Denied`/`Paid`/`PartiallyPaid`/`Voided` | `ClaimRepository.IsFinalDisposition` guard (D9a) skips the `/status` patch; `AdjudicationResult`/`PendDetails` still project for audit purposes. |
| Argo workflow's `update-claim-step` runs after an async-orchestrator pend | Guarded by D9b: synchronous write-back preserves the persisted `Pended` status while still saving adjudication summary/audit data. |

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
- [`claim-fhir-projection.md`](./claim-fhir-projection.md) —
  capability 5.11 FHIR ExplanationOfBenefit projection that consumes
  the post-adjudication state this pipeline produces (totals, denial
  CARC, NCCI edit failures, AI-examination disposition)
