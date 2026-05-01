# AI-Backed Examination Pipeline (Capability 5.9)

> **Status — Phase 1, May 2026.** Replaces `AiExaminationStubStage` at
> Order=600 with a real `AiExaminationStage` that detects NCCI bundling
> pends with at least one modifier-addressable edit failure and emits a
> `ClaimPendedEvent` to Kafka `claims.pended.v1` so claims-examiner-service
> picks the claim up asynchronously, calls Anthropic with NCCI context,
> writes a structured advisory recommendation back via
> `PUT /api/claims/{id}/ai-examination`, and emits a
> `ClaimAiExaminationCompletedEvent` to Service Bus topic
> `ai-examination-events` for downstream pipelines (5.10 remittance).
>
> Most of the AI examination infrastructure already shipped as part of
> claims-examiner-service's bring-up — `AnthropicClient`,
> `AnthropicAuthHandler`, `ExaminerOrchestrator`, `ExaminerPromptBuilder`,
> `ClaimPendedConsumer`. 5.9's scope is **bridge wiring**: the
> orchestrator's pipeline-stage path didn't reach Kafka, so AI examination
> was structurally unreachable from pipeline-driven pends. 5.9 closes
> that gap and adds the completion-event surface that downstream
> capabilities subscribe to.
>
> See [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md)
> for the orchestrator + stage-interface foundation,
> [`claim-ncci-pipeline.md`](./claim-ncci-pipeline.md) for the upstream
> stage that populates `Claim.PendDetails.EditFailures`, and
> [`CLAIMS-EXAMINER-SERVICE.md`](./CLAIMS-EXAMINER-SERVICE.md) for the
> Anthropic integration preserved unchanged by 5.9.

## Pipeline placement

```
... 100 Scrubbing               (5.4)
    200 NetworkCredentialing    (5.6)
    300 BenefitCalculation      (5.5)
    400 NcciEdits               (5.7 — populates Claim.PendDetails.EditFailures)
    500 CoordinationOfBenefits  (5.8)
    600 AiExamination           ◄ 5.9 — this doc
    999 Persistence             (5.5)
```

After 5.9 ships, **6 of 6 pipeline stages are real**. Pipeline cluster
complete.

## Eligibility filter — single source of truth

The stage triggers AI examination only when **all three** conditions hold:

```csharp
context.PendDetails is not null
&& string.Equals(context.PendDetails.PendCode, "NCCI", StringComparison.OrdinalIgnoreCase)
&& context.PendDetails.EditFailures.Any(e => e.IsModifierAddressable());
```

`NcciEditFailureSnapshot.IsModifierAddressable()` is the predicate
`ExaminerOrchestrator.SelectAddressableEdit` uses on the consumer side:

```csharp
public bool IsModifierAddressable() =>
    string.Equals(EditType, "NcciPair", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(RuleId, "NE001", StringComparison.OrdinalIgnoreCase);
```

Calling the snapshot's helper directly from both sides eliminates
filter drift — there's exactly one definition of "modifier-addressable",
shipped in shared `CloudHealthOffice.Events`. Producer and consumer can
never disagree about scope.

### Why not `ModifierOverridePresent`?

`ModifierOverridePresent` is a **claim attribute** — it indicates the
submitter already attached a `-59`/`X{EPSU}` modifier on the claim line.
`IsModifierAddressable()` is a **rule attribute** — it indicates the
NCCI edit is one a modifier *could* legally override.

The point of AI examination is to suggest a modifier when none is
present. Triggering on `ModifierOverridePresent == true` would route
already-modifier-attached claims to AI (which has nothing to add) while
skipping the cases where AI could actually help — exactly backwards.

The Plan-First Decision 2 ratification text said `ModifierOverridePresent`,
but the audit caught the inverted semantic and the user ratified the
correction (Gap A.1). The actual `ExaminerOrchestrator` always used the
rule-attribute predicate; 5.9 mirrors it.

## Mode policy — `AiEnforcementMode`

`TenantEnforcementPolicyOptions.AiMode` controls the stage's posture.
Phase 1 ships **two real modes**:

| Mode | Behavior |
|---|---|
| `BestEffort` (default) | Eligibility passes → emit Kafka event + return Pend with reason `pending-ai-examination`; pipeline continues to PersistenceStage. AI examination is advisory; the absence of a recommendation never blocks claim processing because the pend already has a structured human work-queue path via `PendDetails`. |
| `Disabled` | Operational kill switch. Stage runs (so telemetry captures kill-switch usage) but short-circuits to `Pass` with `outcome="not_applicable"`, `reason="ai-disabled-by-policy"`. Distinct from removing the stage from `EnabledStages`, which would also suppress telemetry. |

### Why `Required` is deferred (Gap H.3)

A natural third mode would be `Required` — fork Pend-vs-Pass on
degraded Kafka so tenants who treat AI advisory as a release gate get
ops triage when the broker is down. **5.9 does not ship it.**

Today's `IClaimEventPublisher.PublishClaimPendedAsync` contract swallows
all producer failures internally and returns `Task` (not `Task<bool>`).
The stage cannot observe whether the event reached the broker. A
`Required` enum value with no underlying signal would be functionally
identical to `BestEffort` — operators reading
`AiEnforcementMode.Required` would assume it does what it says, debug
confused production behavior, and lose framework trust.

`Required` lands as an additive enum value in a focused follow-up once
the publisher contract gains a delivery signal — e.g.,
`Task<bool> TryPublishClaimPendedAsync(...)` or an `IsAvailable` probe.
Better to ship two real modes than three with one being deceptive.

## Kafka emission — placement and rationale (Decision 8a)

The stage emits `ClaimPendedEvent` directly via
`IClaimEventPublisher.PublishClaimPendedAsync(context.Claim.ToClaim(), context.TenantId, ct)`.
Two alternatives were considered:

- **Stage emits (chosen).** Scope-aligned with 5.9 capability — only
  AI-eligible pends reach Kafka. Other pend codes (AUTH, MEDREVIEW,
  COB) don't have AI consumers in scope; broad emission would be
  noise.
- **PersistenceStage emits.** Generic for any pended claim with
  `PendDetails`. Pushes 5.9 scope into a stage owned by 5.5; future
  capabilities can generalize PersistenceStage's emission if and when
  multiple consumers want a single broad pend stream.

Selective-invocation matches the Decision 2 principle: only emit
events the consumer would act on.

## Race condition mitigation (Decision 16 / D.1)

The stage emits to Kafka at Order=600. PersistenceStage at Order=999
then writes the claim to the head row. **If the consumer races
persistence**, the consumer's `GET /api/claims/{id}` 404s before
PersistenceStage has finished.

`ClaimPendedConsumer` commits-and-drops on failure (no retry semantics
at the Kafka level), so a 404 left unmitigated would silently never
get re-examined. 5.9 ships a bounded-retry mitigation **on the consumer
side, inside `ClaimsServiceClient.GetClaimAsync`**:

- **3 attempts**, **250 ms** backoff between each.
- On exhaustion: log warning, return null. Consumer commits offset;
  claim remains pended-without-AI. Operations alarm on
  `cho.claims_examiner.claim_not_found` exhaustion to catch any
  systemic latency regression.
- Non-404 errors throw immediately — no retry on transport failures.
- Success on first attempt incurs **zero** delay.

Why this placement:
- **Contained** to the consumer service; doesn't change the producer
  side, the orchestrator interface, or the Kafka consumer's commit
  semantics.
- **Additive**; ships with the capability that introduces the race.
- **Bounded** — if a claim genuinely doesn't exist after 750 ms, it
  almost certainly never will, and pending-without-AI is operationally
  acceptable for the rare case (operators see the metric).

The architectural alternative — adding an orchestrator post-stage hook
to emit AFTER PersistenceStage — would violate "no changes to
`IClaimAdjudicationOrchestrator` interface" and add complexity that
would propagate to other capabilities.

## Async resume — completion event

After successful write-back to claims-service via the existing
`PUT /api/claims/{id}/ai-examination` endpoint (preserved unchanged),
`ExaminerOrchestrator` emits a `ClaimAiExaminationCompletedEvent` to
Service Bus topic `ai-examination-events`. Payload is intentionally
minimal:

```csharp
public class ClaimAiExaminationCompletedEvent
{
    public string ClaimId;
    public string TenantId;
    public string RecommendedDisposition;  // Approve | Deny | RequestInfo | EscalateToHuman
    public double ConfidenceScore;
    public DateTimeOffset CompletedAt;
    public string? CorrelationId;
}
```

Consumers that need `Rationale`/`PolicyCitations`/`ModelId`/`PromptVersion`
fetch the full `Claim.AiExamination` record via `GET /api/claims/{id}`.
Keeping the event small (Decision C.1) makes it cheap to dedup at the
broker and forward-compatible — adding fields is non-breaking;
trimming them isn't.

### Idempotency (Decision 15)

`SendOptions.MessageId` = `"ai-completed:{claimId}"`. AI examination
is **terminal for the pend cycle** (Decision 3); one logical event per
claim is correct semantics. The Service Bus dedup window (default
1 hour) drops re-emissions cleanly.

The disposition is intentionally NOT part of the key. Different
invocations could yield different recommendations (e.g., RFAI fetch
retry succeeds on the second attempt), and using disposition would let
two distinct keys coexist for the same logical completion.

### When the completion event is emitted

The orchestrator emits the completion event **only when
`SetAiExaminationAsync` returned `true`**:

- **Success path** — Anthropic returned a structured tool result →
  write-back succeeded → emit with the recommended disposition.
- **Fallback path** — Anthropic exception or model declined the tool
  → orchestrator writes `EscalateToHuman` fallback → write-back
  succeeded → emit with `EscalateToHuman`. EscalateToHuman is still a
  terminal recommendation; downstream consumers shouldn't be left
  waiting for an event that never comes.
- **No emit** — `SetAiExaminationAsync` returned `false` (HTTP 409
  Conflict because the claim is no longer Pended; a human already
  acted). The AI recommendation is moot; no notification.

## Why Phase 1 ships this shape

This is the **highest-leverage capability after 5.5** because most of
the work was already done — Anthropic integration, prompt builder,
Kafka consumer, scope filter, write-back endpoint were all built
during claims-examiner-service bring-up. The architectural decision
(option 5: selective invocation + async resume via separate
subscription) was the load-bearing work; 5.9's implementation is
bridge wiring + scope filter + new completion event.

After 5.9:
- 6/6 pipeline stages real — pipeline cluster COMPLETE
- 7th instance of pipeline-stage DI replacement (after 5.4/5.5/5.6/5.7/5.8)
- `TenantEnforcementPolicyOptions` extends to 5 modes (Network,
  Credentialing, Ncci, Cob, Ai)
- First Service Bus topic emitted by a non-claims-service source
  (claims-examiner-service emits completion events) — establishes
  cross-service Service Bus event flow pattern
- Selective invocation pattern proven at production-relevant scope
  (only modifier-addressable NCCI bundling pends)
- Async resume pattern proven via existing Kafka pend infrastructure;
  preserves orchestrator's synchronous-stage semantics from 5.5

## Telemetry conventions

Stage emits under the `cho.claims.adjudication.ai_examination.*`
namespace, mirroring 5.4/5.6/5.7/5.8:

```
cho.claims.adjudication.ai_examination.outcome
    {status="not_applicable|skipped|triggered"}
cho.claims.adjudication.ai_examination.reason
    {reason="not-applicable-no-pend-details|not-applicable-non-ncci-pend|
             ai-disabled-by-policy|no-modifier-addressable-edits|
             pending-ai-examination"}
cho.claims.adjudication.ai_examination.eligible_edits_count
cho.claims.adjudication.ai_examination.kafka_emission
    {result="success|exception"}
cho.claims.adjudication.ai_examination.mode
    {mode="BestEffort|Disabled"}
cho.claims.adjudication.ai_examination.pend_code
    (only set when PendDetails non-null but PendCode != "NCCI")
```

claims-examiner-service emits:

```
cho.claims_examiner.completed_event
    {result="success|degraded"}
cho.claims_examiner.completed_event.disposition
    {value="approve|deny|request_info|escalate_to_human"}
cho.claims_examiner.claim_not_found
    (counter; alarm threshold = 1% of inbound NCCI events at scale)
```

## Pre-existing infrastructure preserved

5.9 does not modify:

- `AnthropicClient`, `AnthropicAuthHandler`, Anthropic options binding
- `ExaminerOrchestrator.ProcessAsync` filter logic (the `PendCode == "NCCI"`
  check + `SelectAddressableEdit` path); 5.9 only **adds** the
  post-write-back completion-event emission
- `ExaminerPromptBuilder`, `IProviderRfaiHistoryClient`,
  `NoOpProviderRfaiHistoryClient`
- `ClaimPendedConsumer` (background service), Kafka consumer config
- `ClaimsController.SetAiExamination` endpoint (advisory-only;
  doesn't touch Status / AdjudicationResult / PendDetails)
- `ClaimsController.Pend` legacy endpoint (still emits via the same
  `IClaimEventPublisher.PublishClaimPendedAsync` — separate code path
  from the new pipeline-driven emission, no duplication concern)
- `Claim.AiExamination` model
- `Claim.PendDetails` shape (5.7)
- `IClaimEventPublisher` interface (consumed as-is; future
  `Required` mode work will revisit)
- Kafka topic `claims.pended.v1`, `ClaimPendedEvent` shape
- Service Bus topic `claim-version-events` (5.5)
- `accumulator-service`, `fhir-service`, other services
- Cosmos partition strategy (5.1b deferred)

## Recovery posture

| Failure mode | Recovery |
|---|---|
| Stage replacement breaks pipeline | Caught by 5.5 orchestrator tests + new stage tests + new `Continue=true` semantics tests; revert restores stub stage |
| Kafka emission breaks legacy `ClaimsController.Pend` path | Unlikely — separate code paths sharing the same publisher. Caught by ClaimsController integration tests if present; revert is straightforward |
| claims-examiner-service breaks consuming new event | Modifications are additive (new emission, no consumer change); revert restores prior behavior cleanly |
| Service Bus topic missing in deployment | Completion event emission fails; degrades gracefully — claim DB still has `AiExamination` populated via HTTP write-back; 5.10 consumer has nothing to consume but doesn't break |
| Filter divergence between stage and `ExaminerOrchestrator` | Mirrored predicate via `IsModifierAddressable()` makes drift impossible; if anyone ever inlines the filter, integration tests catch wasted Kafka events |
| Race condition: Kafka consumer faster than PersistenceStage | Caught by `ClaimsServiceClient.GetClaimAsync` 3-attempt × 250 ms retry; if exhausted, alarm on `cho.claims_examiner.claim_not_found` triggers ops investigation |
| Duplicate completion events | Caught by `ai-completed:{claimId}` MessageId Service Bus dedup |
| `AiExaminationStubStage` deletion breaks DI registration | Caught at startup; revert restores stub |
| `Disabled` mode misconfigured / kill-switch never disengaged | Telemetry surfaces `outcome="not_applicable", reason="ai-disabled-by-policy"` in dashboards; ops sees kill-switch usage at a glance and re-enables |

Worst-case rollback: revert this PR. Stub stage restored;
claims-examiner-service emission removed; new event type removed;
PersistenceStage was never modified; Kafka topic preserved; Service
Bus topic might exist as orphan (harmless). No data changes; no
migration to undo.
