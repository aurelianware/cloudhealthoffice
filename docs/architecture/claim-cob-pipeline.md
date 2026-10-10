# Coordination of Benefits Pipeline (Capability 5.8)

> **Status — updated October 2026 (PR #1278).** The COB stage now runs at
> Order **275**, before benefit calculation, and settles the payer order
> from coverage-service *and* the 837 (decision table below). When they
> agree and the 837 carries complete prior-payer data (2320 / 2430), the
> Phase-1 "secondary deferred" pend is lifted: the benefit engine applies
> claim-level COB for secondary, tertiary and later payers and the claim
> finalizes. See "COB calculation" and "Deductible credit" below. The
> Phase-1 sections that follow are kept as the history of the decisions.
>
> *Original status — Phase 1, May 2026.* Replaced `CoordinationOfBenefitsStubStage`
> at Order=500 with a real `CloudHealthOffice.CobEngine`-backed
> implementation, shipping CHO-primary adjudication only plus a detection
> hook that pended CHO-secondary scenarios with the stable reason
> `cob-secondary-not-supported-phase-1`.
>
>
> *Accumulator integrity — October 2026 (`feat/accumulator-integrity`).*
> Engine accumulators are written only when a claim's adjudication is final:
> every claim is priced read-only and the write is committed by
> `AccumulatorCommitStage` (a passing claim) or by the examiner resolution
> after its lock-fenced final write (an approval). A void or denial fences the
> claim id in the store. accumulator-service fences a stalled original append
> on the original's own snapshot. See "Accumulator writes: commit on final
> adjudication" below; it replaces the H4, tombstone-snapshot and lock-scope
> limitations.
>
> See [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md)
> for the orchestrator + stage-interface foundation, and
> [`claim-ncci-pipeline.md`](./claim-ncci-pipeline.md) for the immediate
> upstream stage.

## Why this exists

Pre-5.8, every adjudicated claim flowed through a no-op
`CoordinationOfBenefitsStubStage` that returned `Pass` regardless of
whether the member had other coverage. CHO-secondary claims were silently
processed as if CHO were the primary payer — overpayments waiting to
happen, and zero observability into how often the scenario occurs in the
pilot population.

5.8 wires the COB engine that's been built and unit-tested as a class
library since Q1 2026, plus a 5th cached resolution-client pair against
coverage-service. The engine itself is unchanged; 5.8 is consumer-side
wiring + enforcement-mode policy + a Phase 2 hook stub.

## Pipeline placement

```
... 100 Scrubbing               (5.4)
    200 NetworkCredentialing    (5.6)
    250 Pricing
    275 CoordinationOfBenefits  ◄ this doc (was 500 in Phase 1)
    300 BenefitCalculation      (5.5) — applies COB when the stage cleared it
    400 NcciEdits               (5.7)
    600 AiExamination           (stub; 5.9 — may consume CobResult on context)
    999 Persistence             (5.5)
```

*Phase 1 placement (history):* COB ran at 500, after NCCI, detection-only.
It moved before benefit calculation in PR #1278 so the payer order is
settled before the engine prices the claim and writes accumulators.

5 of 6 pipeline stages are now real after 5.8 ships. COB runs after NCCI
edits because edit failures may reduce allowed amounts in Phase 2 work
(those amounts feed CHO-secondary calculation in priorEob territory). For
Phase 1, COB is detection-only — it does not mutate `AllowedAmount`,
`PayerPayment`, or any persisted field. The structured outcome lives on
`ClaimAdjudicationContext.CobResult` (α posture, mirrors 5.4's
`ScrubbingResult`).

## Decisions

### D1 — Direct DI registration; no `AddCobEngine()` extension

CobEngine has no `Configuration/` directory and no fluent registration
extension. The 5.8 wiring registers `ICobCalculationService` and
`IPayerOrderService` directly in `claims-service` `Program.cs` as
Singletons (both services are pure stateless calculators with no
per-request state).

```csharp
builder.Services.AddSingleton<ICobCalculationService, CobCalculationService>();
builder.Services.AddSingleton<IPayerOrderService, PayerOrderService>();
```

Adding a fluent `AddCobEngine()` helper would be two lines of value with
five lines of boilerplate. If a second consumer emerges (e.g.
benefit-plan-service preview / what-if surface), the helper becomes a
focused future PR.

`ICobCalculationService` is registered for Phase 2 priorEob work but is
**not exercised by 5.8 stage logic** (Decision 17). Phase 1 only invokes
`IPayerOrderService.DetermineOrder` for audit-trail rule labelling on
detected CHO-secondary scenarios.

### D2 — Stage replacement via direct DI swap

Same shape as 5.4 / 5.6 / 5.7: in `Program.cs`, the production stub
registration was replaced in place rather than through
`services.RemoveAll<>()`. The stub never shipped to a customer
environment, so removal isn't required.

```csharp
// before 5.8
builder.Services.AddScoped<IClaimAdjudicationStage, CoordinationOfBenefitsStubStage>();
// after 5.8
builder.Services.AddScoped<IClaimAdjudicationStage, CoordinationOfBenefitsStage>();
```

`CoordinationOfBenefitsStubStage.cs` was deleted in this PR (mirrors 5.4
+ 5.7 stub deletions).

### D3 — Phase 2 hook stub with structured detection

5.8 detects CHO-secondary scenarios explicitly via the coverage-service
`/cob` lookup; emits a Pend with stable machine reason
`cob-secondary-not-supported-phase-1` on `CobOutcome.PendReason`. The
work-queue UI uses the stage result's human-readable reason; telemetry +
the Phase 2 priorEob roadmap consume the stable code.

This pattern — "Phase 1 detects what Phase 2 will calculate" — is the
first instance in the platform. It costs ~30-50 lines of detection logic
over a "defer entirely" alternative and provides:

- **Operational telemetry for free** — the
  `cho.claims.adjudication.cob.outcome` counter shows pilot-population
  CHO-secondary frequency within weeks of pilot launch, sizing the
  Phase 2 priorEob effort empirically.
- **Honest gap naming** — pilot ops triage queues see
  `cob-secondary-not-supported-phase-1` and route the claim through the
  manual COB workflow rather than the generic NCCI / network pend
  buckets.
- **Forward-compat** — Phase 2 priorEob work can identify pended claims
  by the stable reason and re-process them once the calculation surface
  is live.

### D4 — Phase 1 does not extend `AdjudicationResult`

CHO-secondary persistence (CobReduction, SecondaryPlanPayment,
PrimaryPayerPayment) is deferred to Phase 2 priorEob work. Phase 1 ships
CHO-primary adjudication only and CHO-primary scenarios complete their
full benefit calculation upstream at Order=300 — the existing
`AdjudicationResult` shape captures CHO-primary fully. This part is
unchanged and still correct: no COB *calculation* fields exist yet.

> **Pend-persistence defect fix (dated diagnostics doc,
> `docs/million-claim-challenge/2026-07-07-expected-pend-diagnostics.md`).**
> The paragraph below originally also deferred *PendDetails* projection —
> that part was wrong and has been fixed. `CobOutcome` living on the
> context only (with no PersistenceStage write) meant an examiner never
> saw *why* a claim pended: `ClaimAdjudicationStageResult.Reason` lives
> only on `ClaimAdjudicationContext.StageResults` for the duration of one
> Service Bus message handler — nothing persists it, so the "human-readable
> pend reason already carried by `ClaimAdjudicationStageResult`" argument
> below never actually reached any UI. `CoordinationOfBenefitsStage` now
> populates `PendDetails` (`PendCode="COB"` — already a documented,
> recognized value, not new vocabulary) whenever it detects a
> secondary/tertiary scenario or a coverage-service outage, mirroring
> `NcciEditsStage`'s existing precedent of recording the deterministic
> snapshot regardless of enforcement mode. `PersistenceStage` in turn now
> projects the orchestrator's Pend outcome onto `ClaimStatus.Pended`. See
> `claim-adjudication-pipeline.md` D9 for the full precedence rule.

~~`CobOutcome` lives on the context only; PersistenceStage projection is
deferred (α posture, consistent with 5.4 ScrubbingResult).~~ *(superseded —
see the note above.)*

~~This is a deliberate non-symmetry with 5.6's `EnforcementOutcome` (which
extends the projection) and 5.7's `PendDetails.EditFailures` (which
extends the projection-bypass shape): COB Phase 1 has nothing
calculation-shaped to persist beyond the human-readable pend reason
already carried by `ClaimAdjudicationStageResult`.~~ *(superseded — the
premise was that the stage-result reason was already visible somewhere
downstream; it wasn't. `PendDetails.EditFailures` stays NCCI/MUE-specific
— COB pends persist `PendDetails` with an empty `EditFailures` list, since
COB has no per-line edit-failure shape to report.)*

### D5 — `CobOutcome` on `ClaimAdjudicationContext`

```csharp
public CobOutcome? CobResult { get; set; }
```

Set once by the COB stage; read by 5.9 AI examination if a
SoftValidation-mode tenant wants AI to consider COB context, and by the
`cho.claims.adjudication.cob.*` telemetry namespace.

### D6 — `CobEnforcementMode` extension on `TenantEnforcementPolicyOptions`

```csharp
public enum CobEnforcementMode
{
    PendForSecondary,    // Default
    Deny,
    SoftValidation,
}
```

`PendForSecondary` matches the Phase 2 hook semantic — pending is the
correct posture when functionality is genuinely unimplemented. Tenants
preferring hard-block on secondary claims set `Deny`; tenants
instrumenting their pilot before hard policy lands set `SoftValidation`.

`TenantEnforcementPolicyOptions` now binds 4 modes (Network, Credentialing,
Ncci, Cob); existing 5.6 / 5.7 binding tests pass without modification.

### D7 — Coverage-service degradation always pends

Different posture from 5.6's `NetworkEnforcementMode` (FailClosed vs
FailOpen): when coverage-service returns `null` (transport failure,
timeout, JSON parse error), the stage produces Pend regardless of
`CobMode`, **including in Deny mode**. "Unable to determine coverage
state" is not structurally a denial scenario — denying claims because
coverage-service is offline would be operationally wrong.

`SoftValidation` mode passes the claim with telemetry capturing the
degradation, matching the other modes' soft-validation philosophy.

### D8 — `IPayerOrderService` exercised in 5.8 for audit-trail rule labelling

Even though calculation is deferred, the stage invokes
`IPayerOrderService.DetermineOrder()` to populate
`CobOutcome.AppliedRule`. This serves three purposes:

1. **Audit trail richness** — operations sees WHY CHO is secondary
   (`MedicareSecondaryPayer` vs `ExplicitCoverageRecord`).
2. **Telemetry differentiation** — Medicare-primary cases route
   differently from commercial-primary in Phase 2 sizing.
3. **Engine surface verification** — exercises `IPayerOrderService` in
   production flow before Phase 2 priorEob work depends on it.

Phase 1 data-source gaps on `CobEntryResponse` (no birthday, no
employment status, no LGHP signal) mean the engine reliably
differentiates only Medicare scenarios. For commercial-primary cases the
engine falls through to `PayerOrderRule.ExplicitCoverageRecord` because
it has no MSP / birthday / longer-duration signal to apply. The stage
keeps the `ExplicitCoverageRecord` label since `CoverageSequence="P"` IS
the explicit determination — it's not a guess, it's a wire-level claim
from the upstream service.

The stage's mapping for non-CHO entries:

| `InsuredInfo` field | Source from `CobEntry` |
|---|---|
| `MemberId` | `PolicyNumber ?? PayerId` |
| `PayerId` | `PayerId` (Phase 2 contract — see D9 below) |
| `PolicyholderBirthDate` | `null` (no source) |
| `CoverageEffectiveDate` | `CoverageBeginDate` |
| `IsActiveEmployee` | `false` (no source) |
| `IsMedicare` | `IsMedicare` |
| `MedicareDesignatedPrimary` | `IsMedicare && CoverageSequence == "P"` |
| `IsLargeGroupHealthPlan` | `false` (no source) |

The `MedicareDesignatedPrimary` mapping is load-bearing: without it,
Medicare-primary entries would silently degrade to MSP-secondary
branching in `PayerOrderService` (a `MedicareDesignatedPrimary=false`
Medicare coverage triggers MSP-secondary by default).

CHO's own `InsuredInfo` (constructed for the engine call):

| `InsuredInfo` field for CHO | Source |
|---|---|
| `MemberId` | `context.Claim.MemberId` |
| `PayerId` | Sentinel `"CHO"` (D9 below) |
| `PolicyholderBirthDate` | `context.ResolvedMember?.DateOfBirth` |
| `CoverageEffectiveDate` | `context.ResolvedMember?.EffectiveDate` |
| `IsActiveEmployee` | `false` (Phase 1 default) |
| `IsMedicare` | `false` (CHO is commercial / Medicaid Phase 1) |
| `MedicareDesignatedPrimary` | `false` |
| `IsLargeGroupHealthPlan` | `false` |

### D9 — Phase 2 follow-ups on the coverage-service contract

Three Phase 1 quirks of the upstream coverage-service contract are
carried unchanged for stability and flagged here for Phase 2:

1. **`CobEntry.PayerId` field semantics.** coverage-service populates
   `PayerId` from `Coverage.OtherInsurance.PolicyNumber`, not from a
   true payer-identity registration. The field name lies. Phase 1
   carries it through unchanged for telemetry continuity; Phase 2
   priorEob work introduces a payer registration and fixes the upstream
   field's source.

2. **404-as-empty-list translation at `HttpCoverageClient` boundary.**
   coverage-service returns `404 Not Found` when a member has zero COB
   entries (whether the member is missing OR has no other insurance).
   Phase 1 `HttpCoverageClient` translates that 404 into an empty
   `IReadOnlyList<CobEntry>` so the stage can rely on
   "empty list = CHO is the only coverage" semantics. Phase 2 may move
   the empty-list semantic to the wire (200 with empty body) once a
   coverage-service contract change is feasible; until then, the
   client-side translation is the canonical signal.

3. **Phase 1 sentinel `"CHO"` PayerId.** `ResolvedBenefitPlan` has no
   payer-identity field today. The stage uses the constant string
   `"CHO"` as `InsuredInfo.PayerId` for CHO's coverage in the engine
   call. Phase 2 will replace this with a real payer registration once
   the platform onboards multiple line-of-business payers.

### D10 — Effective date for `/cob` query

The stage passes the claim's earliest service date as `asOfDate` —
`Min(claim.ServiceDateFrom, claim.ClaimLines.Min(l => l.ServiceDateFrom))`.
Mirrors 5.6's credentialing-as-of-service-date pattern (most-restrictive
interpretation). The shared helper duplicates 5.6's
`NetworkCredentialingStage.ResolveEarliestServiceDate` rather than
extracting a platform-wide utility — two consumers don't yet warrant the
abstraction.

### D11 — Cache TTL: 5 minutes

`CachingCoverageClient` uses a 5-minute TTL keyed by
`(tenantId, memberId, asOfDate-day)`. Mirrors
`CachingProviderMembershipClient` shape and TTL. Coverage records can
terminate without an explicit signal (open-enrollment loss, mid-year
termination), so a longer cache risks stale "no other coverage" results
for claims submitted right after a coverage change.

Empty lists ARE cached (positive answer — "CHO is the only coverage");
null transport-failure results are NOT cached (a transient outage
shouldn't pin "lookup unavailable" for the full TTL window).

### D12 — Engine exception caught at the stage

Pattern parity with 5.4 / 5.6 / 5.7: try/catch around the
`IPayerOrderService.DetermineOrder` invocation. On exception, the stage
defaults `CobOutcome.AppliedRule` to `ExplicitCoverageRecord` (the wire
signal IS the explicit determination) and continues with the mode-driven
secondary outcome. The audit-trail richness is mildly degraded —
operations doesn't get the engine's `Explanation` string — but the
outcome shape is preserved.

## Telemetry

```
cho.claims.adjudication.cob.outcome{scenario=cho_primary_no_secondary
                                    | cho_primary_with_secondary
                                    | cho_secondary_detected
                                    | cho_tertiary_detected
                                    | none}
cho.claims.adjudication.cob.coverage_service{result=success|unavailable}
cho.claims.adjudication.cob.medicare_primary{detected=true|false}
cho.claims.adjudication.cob.applied_rule{rule=ExplicitCoverageRecord
                                         | MedicareSecondaryPayer
                                         | ...}
cho.claims.adjudication.cob.outcome_mode{mode=pend|deny|softvalidation}
```

The Phase 2 sizing signal comes primarily from the
`cho_secondary_detected` and `cho_tertiary_detected` counters and the
`medicare_primary` differentiation.

## Phase 2 priorEob roadmap

This PR intentionally ships only the detection half of COB. Phase 2
work, scheduled for the post-Phase-1 roadmap window, covers:

1. **837 inbound priorEob field on submission.** Surface `priorEob` on
   `IClaimSubmissionService` so claims can carry primary-payer payment
   info at intake.
2. **CHO-secondary calculation.** Wire `ICobCalculationService` into
   the stage; replace the Pend with real `CobLineResult` adjustments to
   `AllowedAmount` / `PayerPayment` / `PatientResponsibility`.
3. **`AdjudicationResult` extension.** Add CHO-secondary persistence
   fields (CobReduction, SecondaryPlanPayment, PrimaryPayerPayment) and
   teach PersistenceStage to project them.
4. **FHIR ExplanationOfBenefit COB extensions.** ExplanationOfBenefit
   currently projects CHO-primary only; Phase 2 5.11 surfaces the
   secondary-payer fields on the FHIR resource.
5. **835 remittance COB CAS segments.** OA/23 reduction codes flow
   through 5.10 in Phase 2.
6. **coverage-service contract fixes** — items D9.1 and D9.2 above.

## COB calculation: secondary, tertiary and later payers

The COB stage (Order 275) settles the payer order; the *calculation* runs
inside the benefit engine (`BenefitCalculationStage`, Order 300), from the
payer data on the 837 itself, on both the per-line and DRG / per-diem paths.

### Payer order: the decision table (`CoordinationOfBenefitsStage`)

The stage runs **before** benefit calculation, so a claim it pends is
priced read-only (`ExecutionMode = Prospective`: no accumulator write) and
only a claim it clears is priced as a later payer. It compares
coverage-service (`/member/{id}/cob`) with the 837 (2000B `SBR01`, 2320,
2430):

| Coverage-service | 837 SBR01 | 837 prior-payer data | Outcome | Reason code |
|---|---|---|---|---|
| unavailable | P / U / absent | — | pend (`PendForSecondary`, `Deny`); pass (`SoftValidation`) | `cob-coverage-service-unavailable` |
| unavailable | S / T / A–H | — | **pend in every mode** | `cob-coverage-service-unavailable` |
| primary (no other coverage, or only later ones) | P / U / absent | — | pass, no COB | — |
| primary | S / T / A–H | — | **pend in every mode** (never paid) | `cob-payer-order-mismatch` |
| secondary (one "P") | U / absent | — | pend (`PendForSecondary`, `SoftValidation`); deny (`Deny`) | `cob-secondary-not-supported-phase-1` |
| secondary or tertiary | P, or a different later position (secondary ↔ tertiary-or-later) | — | **pend in every mode** | `cob-payer-order-mismatch` |
| agree | — | two 2320 loops (or a 2320 and 2000B) with the same sequence | **pend in every mode** | `cob-duplicate-payer-sequence` |
| agree | — | a 2430 SVD01 that names no other payer (2330B NM109, REF*2U, REF*FY) while there are several | **pend in every mode** | `cob-unmatched-line-adjudication` |
| agree | — | no 2320 with AMT*D or 2430 SVD for some earlier sequence | pend (`PendForSecondary`, `SoftValidation`); deny (`Deny`) | `cob-secondary-not-supported-phase-1` |
| agree | — | complete | **pass; COB applied** (`CobOutcome.ApplyCob`, `PayerSequence`) — the claim finalizes | — |

"Agree" means coverage secondary ↔ SBR01 S, coverage tertiary (two "P", or
"P" + "S") ↔ SBR01 T or A–H. The reason code
`cob-secondary-not-supported-phase-1` keeps its legacy name for
work-queue / telemetry continuity; it now means "no usable prior-payer data
on the 837".

**Never priced as primary by accident (re-review N4).** `SoftValidation`
used to *pass* a claim the 837 or coverage-service says is a later payer
when COB could not be applied, and the benefit stage then priced it as
primary. Now no mode passes it: `SoftValidation` pends like
`PendForSecondary`. `BenefitCalculationStage` also guards on its own: unless
the COB stage cleared COB (`CobResult is { ApplyCob: true }`) or an examiner
confirmed this plan is primary, a claim whose SBR01 is S/T/A–H, or whose
coverage scenario is secondary/tertiary, pends with pend code COB — including
when the COB stage is disabled or did not run.

### Examiner resolution of a pended claim (re-review N1)

A pended claim writes no accumulators, so finalizing it by flipping
Pended → Approved used to pay it with **no deductible or OOP ever
recorded**. Approval now re-adjudicates it:

1. `POST /api/claims/work-queue/{id}/resolve` (permission `workqueue:work`)
   with `disposition: "Approved"` (or `/override`, `claims:override-approve`)
   takes a conditional **resolution lock** on the Pended claim (a concurrent
   resolution gets 409), then calls
   `IClaimApprovalReadjudicator.ReadjudicateForApprovalAsync` (the
   orchestrator), which re-runs the whole pipeline with the examiner's
   approval on the context (priced read-only like every run; the resolver
   commits the accumulators after its final write, step 5). The re-run's Adjudicated message
   has its own MessageId (`adjudicated:{ClaimVersionId}:approval:{id}`) so
   Service Bus duplicate detection does not drop it; the advisory AI
   examination does not run.
2. **Only the reviewed pends are overridden (round 3, B1).** The approval
   carries the claim's persisted pend (`ExaminerApproval.ReviewedPend`: the
   pend code and reason plus every `AdditionalPendReasons` entry) — what the
   examiner saw. A review-stage pend on the re-run (`DuplicateClaim`,
   `ProviderIntegrity`, `NetworkCredentialing`, `NcciEdits`, `Scrubbing`)
   becomes Pass only when its code **and exact reason** were reviewed — for
   every code (`NETWORK`, `SCRUB`, `NCCI`, `MUE` as well as `MEDREVIEW`,
   `DUPLICATE`, `RETROELIG`, `SUBRO`, `SPENDDOWN`; round-3 verification M4).
   A new pend (a duplicate of a different claim, a different NCCI edit, …)
   keeps the claim pended and the approval returns **409** for re-review.
   **Transient failures are never overridden**, reviewed or not: "Provider
   integrity check could not be reached", "Duplicate-claim check could not
   be completed", membership / credentialing verification unavailable, "NCCI
   engine threw", coverage-service unavailable. They are not findings; the
   re-run retries the check, and the approval passes once it succeeds.
   **A claim that pends more than once keeps every pend** (round-3
   verification, blocker 1). The stages replace `PendDetails`; the
   orchestrator now merges: the earlier pend stays the claim's pend (it
   routes the work queue, so the COB reason stays first), and each later one
   is added to `AdditionalPendReasons` (with its NCCI / duplicate evidence).
   Benefit calculation's COB guard adds its reason to an existing pend
   instead of dropping it. So DUPLICATE then MEDREVIEW, or COB then
   RETROELIG, are both stored and one approval covers both — before, only the
   last was stored and every approval re-pended on the first.
   Stages that pended without a code (network → `NETWORK`, scrubbing →
   `SCRUB`) now record one, so it is persisted and nameable. Pends that mean
   the claim *cannot be priced* (benefit calculation, pricing) are never
   overridden. What the re-run overrode is recorded for the audit.
3. **COB pends need the payer order (round 3, B2).** A COB pend
   (`cob-payer-order-mismatch`, missing prior-payer data, duplicate /
   unmatched payer data) is never paid as primary by a plain approval: the
   request must carry `payerSequence`, the order the examiner confirmed.
   It is checked, never clamped:
   - only when COB is among the stored pends (the routing pend or an
     additional one), only in 1..N (N = the claim's 2320 payers + this
     plan) — otherwise **400**;
   - disagreeing with the 837's SBR01 needs `claims:override-approve`
     (**403**) and a reason (**400**); disagreeing with coverage-service
     needs the same, or the re-run refuses it
     (`cob-payer-order-override-required`, 409). Coverage-service
     unavailable cannot be overridden: it pends for retry
     (`cob-coverage-service-unavailable`);
   - **1 (primary) when a prior payer paid more than $0** needs a second,
     different approver: the first approval is stored on the claim
     (`PendingExaminerApproval`, with the fingerprint of the pends it
     reviewed and an expiry — `Claims:SecondApprovalTtlHours`, default 72)
     and answered **202**; the same approver again gets 409; a second
     supervisor's approval with the same sequence, for the same pends, before
     the expiry re-runs the claim — otherwise the waiting approval is ignored
     and this one starts over (`cob-primary-over-prior-payment` if it ever reaches
     the stage without one). The reviewer's case — golden 07 approved as
     primary paid $152 on top of $172 already paid against $250 allowed — is
     refused for a single approver.
   Sequence 1 prices the claim as primary (`CobOutcome.ConfirmedByExaminer`);
   2+ applies COB from the 837's 2320/2430 data and still pends if that data
   is incomplete. Without `payerSequence` the API returns 409 with the reason.
4. **What the examiner saw (round-3 verification, M4).** An approval must
   send `pendFingerprint` — the fingerprint of the pends the examiner
   viewed (`pendDetails.fingerprint` on the claim, `pendFingerprint` on the
   work-queue item). If the stored pends differ (the claim was
   re-adjudicated since), **409** with the current pends. The fingerprint
   (`v2-…`, PR #1278 follow-up 5) hashes the **sorted** (ordinal) set of
   every "code: reason" plus the sorted NCCI/MUE edit failures and duplicate
   matches behind them. It no longer includes the pend time: every re-run
   sets a new `PendedAt`, so a failed (transient) approval that re-pended the
   same pends used to invalidate the examiner's fingerprint and any waiting
   first approval. Dropping the time does not reopen the "pends changed
   between page load and click" hole: anything the examiner was shown that
   changes — a pend, a reason, a finding — changes the hash; a re-run that
   produces exactly the same pends and findings leaves the review exactly as
   accurate, and the re-run overrides only pends matching what was reviewed.
   (Fingerprints in the old format, including a waiting first approval's,
   stop matching once at deploy: reload and approve again.)
   Every input (`disposition`, `aiExaminerAgreement`,
   `payerSequence`, the fingerprint, permissions) is validated before
   anything happens — no re-run, no reversal, no write (L7).
5. Only when the re-run passes is the claim set to Approved and finalized —
   with the payment from that pass; its accumulator write
   (`ApprovalReadjudicationResult.PreparedAccumulatorCommit`) is committed
   right **after** the lock-fenced final write lands, never before. Any other
   outcome returns 409 with every unresolved reason; the claim stays pended.
   Once the decision is made, the resolved and finalized events are
   published with `CancellationToken.None` (a disconnecting caller cannot
   leave an approved claim unpublished). The final write is **fenced on the
   resolution-lock token** (`UpdateHoldingResolutionLockAsync`: Mongo filter
   on the token, Cosmos read-check-replace with the ETag); the lock lasts 10
   minutes. A resolver whose lock expired and was taken over gets 409 and
   publishes nothing (L6).
   **The re-run's own write is fenced too (PR #1278 follow-up 1).** The
   approval carries the lock token (`ExaminerApproval.ResolutionLockToken`);
   `PersistenceStage` passes it to `UpdateAdjudicationProjectionAsync`
   (`requiredResolutionLockToken`), whose write lands only while
   `ResolutionLock.Token` still equals it (Mongo: the update filter; Cosmos:
   a patch `FilterPredicate`, evaluated at commit). Before, the projection
   accepted any Submitted / Adjudicated row, so resolver A's re-run that
   outlived its lock could overwrite resolver B's finalized claim (paid 112
   over B's 58), or land between B's re-run and B's re-read and have B
   finalize and publish A's amounts. The follow-up status write
   (`isPend` / guarded status patch) carries the same fence. A refused
   write marks the run `ResolutionLockLost`: the orchestrator then **emits
   nothing** — no audit `ClaimVersionAdjudicated` event, no Service Bus
   message, no adjustment callback (a Reject callback would move an
   in-flight adjustment AwaitingReadjudication → Failed, and the new
   holder's Pass would then be a no-op, leaving the predecessor unreversed)
   — and the resolver returns the lost-lock 409. A denial checks the lock is still held (`HoldsResolutionLockAsync`:
   Pended, this token, unexpired) immediately before reversing the engine
   accumulators, and aborts with the same 409 if not. **Accumulators are
   now under the same lock**: see "Accumulator writes" — the re-run writes
   none, and a resolver that lost its lock never reaches the commit.
6. **Audit.** Every resolution appends an `ExaminerResolutionRecord` to
   `Claim.ExaminerResolutions` — disposition, approver(s) and each
   approver's reason, payer sequence, the fingerprint and list of the
   reviewed pends, the pends overridden, requested / resolved timestamps —
   and the ClaimVersionResolved event carries it
   (`payload.examinerResolution`).
7. **Denial.** An examiner denial reverses the claim's engine accumulators
   terminally (`IBenefitCalculationEngine.ReverseClaimAsync`, idempotent). A
   claim pended since deferred commits wrote none, so nothing is reversed —
   but the claim id is fenced in the store, so no commit of it can land
   afterwards. A claim pended before deferred commits (NCCI / AI pends used
   to write at Order 300) has its write backed out. accumulator-service
   skips a ClaimFinalized event whose status is Denied (and reverses one that
   had applied).
8. **Portal.** The work queue's override and the claim page's approve send
   `pendFingerprint` and `payerSequence` (a payer-order dialog whenever any
   pend is COB — the routing pend code, or an entry of the work-queue item's
   `pendReasons` / `PendDetails.AdditionalPendReasons`, PR #1278 follow-up 4;
   `CobPend`) and show the
   service's 400 / 403 / 409 reason — not "service unavailable"; a 202 shows
   "waiting for a second approver".

**Every pend reason is kept (re-review N3).** The NCCI stage (Order 400) used
to overwrite an earlier pend's details (e.g. the COB stage's at 275), so the
work queue showed only NCCI and approving it lost the COB reason. Now an
earlier non-NCCI pend code is kept — it routes the queue and is listed first —
and the NCCI reason is added to `PendDetails.AdditionalPendReasons` (with its
edit failures). Work-queue items list every reason in `pendReasons`.

### Where the data comes from

| 837 (005010X222A1 / X223A2) | Claim field | Engine input |
|---|---|---|
| 2000B `SBR01` (P/S/T/A–H/U) | `Claim.PayerResponsibilityCode` | `CobInfo.PayerSequence` (1, 2, 3, 4–11) |
| 2320 `SBR01` | `ClaimOtherPayer.PayerResponsibilityCode` | `PriorPayerAdjudication.Sequence` |
| 2330B `NM1*PR` NM103 / NM109, `REF*2U` / `REF*FY` | `PayerName` / `PayerId` / `AdditionalPayerIds` | same |
| 2320 `AMT*D` (payer paid amount) | `ClaimOtherPayer.PaidAmount` | `ClaimPaidAmount` |
| 2320 `CAS` (claim-level adjustments) | `ClaimAdjustments` | `ClaimAdjustments` |
| 2430 `SVD02` / `CAS` (SVD01 = 2330B NM109 / REF*2U / REF*FY) | `LineAdjudications` (or `Claim.UnmatchedOtherPayerLines`) | `Lines` |

`X12837Parser` reads these loops (2330A–I `NM1`/`N3`/`N4`/`REF`/`DMG` never
overwrite the claim's own subscriber, billing or pay-to provider);
`X12837ClaimMapper` attaches each 2430 to its payer by SVD01 against NM109,
REF*2U and REF*FY. An SVD matching none is attributed to the only other
payer when there is exactly one; with several it is kept in
`UnmatchedOtherPayerLines` and the COB stage pends — never dropped, which
would undercount what the earlier payers paid. The model is standard
(complementary); the claim does not carry a plan COB method.

**SNIP level 3 (warnings only).** `X12837SnipValidator` checks TR3 COB
balancing and warns — never rejects — when it does not hold:
`L3-COB-2320-BALANCE` (per 2320 payer: CLM02 = AMT*D + Σ its 2320 CAS +
Σ its 2430 CAS) and `L3-COB-SVD-BALANCE` (per 2430: line charge = SVD02 +
Σ its CAS).

### The calculation: claim level (`CobCalculationService.CalculateClaim`)

Sources: NAIC Coordination of Benefits Model Regulation (MDL-120, 2013):
§7 (the secondary "shall calculate the benefits it would have paid **on the
claim** in the absence of other health care coverage and apply that
calculated amount to any allowable expense under its plan that is unpaid by
the primary plan … total benefits paid … by all plans **for the claim** do
not exceed 100 percent of the total allowable expense **for that claim**"),
§6.A(4) (each secondary takes into account every plan determined before it)
and §3.A (an amount the provider may not charge is not an allowable expense).

1. **Priced as the only plan.** Every line is adjudicated as if this plan
   were primary — deductible, copay, coinsurance and OOP cap carried from
   line to line — giving each line's allowed, cost share and normal benefit
   (allowed − cost share). Those first-pass deductible / OOP updates are
   then rolled back (`AccumulatorWorkingSet.RollbackCostShareUpdates`).
2. **Prior payers per line.** A line a payer reported in 2430 takes its
   SVD02 and 2430 PR CAS; the rest of its 2320 AMT*D is prorated by charge
   across its unreported lines (all lines when it reported every line; a TR3
   shortfall — AMT*D below Σ SVD02 — comes off the 2430 payments pro rata).
   Shares are truncated to the cent; remainders go to the last line with a
   positive charge. Its 2320 PR CAS is kept as one claim-level amount, not
   prorated.
3. **Bounding payer.** For each line, the bounding payer is the last payer
   that *adjudicated* it: paid more than $0, or left a PR — on the line
   (2430) or at claim level (a 2320 PR covering the lines its 2320 amounts
   apply to, even when it also sent 2430 lines for others: re-review N2, the
   same rule as the DRG path). A payer that paid $0 with only CO/OA
   adjustments (CO-27, CO-22, CO-109, CO-96, CO-204, …) did not cover the
   service — its "PR = 0" is not what the member owes, so it bounds nothing
   (review B1).
4. **Claim balance.** Per bounding payer, balance = min(Σ room, that payer's
   PR on its lines — 2430 PR plus its whole 2320 PR when the group holds all
   its claim-level lines); lines with no adjudicating payer, or one that
   reported no CAS, have balance = room. Room = max(0, allowed − prior paid).
5. **Claim totals.** standard: paid = min(Σ normal, balance);
   non-duplication: paid = min(max(0, Σ normal − Σ prior paid), balance);
   member = min(Σ cost share, balance − paid).
6. **Lines.** Each group's balance is split across its lines by room. Our
   payment fills lines by normal benefit up to min(normal, line balance),
   then any line up to its balance; the member's share fills lines up to
   min(cost share, line balance − paid), then (only if needed) up to
   min(cost share, allowed − paid). So per line paid ≤ allowed − prior paid,
   paid + member ≤ allowed, and **one positive OA-23 = allowed − member −
   paid**; PR-1/2/3 are reduced to the member's share (coinsurance, copay,
   then deductible); charge − ΣCAS = paid; no negative CAS.

Line-by-line capping with the 2320 amounts prorated *by charge* (the
previous approach) lost money whenever those proportions differed from where
the member still owed: golden 07 paid $49.37 instead of $58.00 and left
$8.63 neither paid nor billable to the member (review M1). The per-line view
(`MemberPaidOnly`) now prorates a later payer's claim-level amounts by the
balance the earlier payers left on each line (round 3, below) and pays the
same $58.00. The claim-level allowable-
expense rule is the one MDL-120 states; prorating the 2320 amounts "by the
previous payer's line PR" was considered and rejected: the 2320 amounts are
claim-level precisely when the payer gives no line split, so any line split
is an assumption, while the claim-level totals are what the payer reported.

A DRG / per-diem stay is one unit: every prior payer's claim totals against
the stay's allowed (`CobClaimInput.SingleStay`), then the stay's PR, credit
and OA-23 are allocated to lines by allowed.

**CLP02** (835) follows what was applied: `AdjudicationResult.CobPayerSequence`
(set only when COB ran) → 2 secondary, 3 tertiary (also 4–11, which the 835
has no code for), else 1 — not SBR01 alone.

## Deductible credit on secondary and later plans

Per-plan setting `cobDeductibleCredit` on the benefit plan document
(`BenefitPlan.CobDeductibleCredit` → engine `BenefitPlanConfig.CobDeductibleCredit`;
carried through the GET/PUT adapter). Applies only when the plan is not the
first payer:

| Value | Deductible accumulator | 835 |
|---|---|---|
| `NaicFullCredit` (default) | the deductible our own adjudication applied as the only plan (already limited to the remaining deductible and by the OOP cap), including deductible a prior payer paid | claim priced as the only plan, then claim-level COB |
| `MemberPaidOnly` | only the PR-1 the member owes after COB — self-funded ERISA plans with non-duplication / carve-out provisions | **line by line**: each line priced and COB'd before the next, so the next line sees only the deductible the member paid. Same payment as `NaicFullCredit` on golden 07 ($58.00); the per-line split can differ |
| `NoDeductible` | nothing: the deductible is not applied | no PR-1 (the deductible is skipped as a later payer) — Medicaid-secondary plans. Rejected on an HDHP (IRC §223(c)(2)). |

Source: MDL-120 §7, "the secondary plan shall credit to its plan deductible
any amounts it would have credited to its deductible in the absence of other
health care coverage."

**What changes on the 835, plainly.**

- `NaicFullCredit` (and `NoDeductible`): the claim is priced as the only
  plan first, so the deductible met on one line is met for the claim's
  later lines (their PR-1 drops), and the payment is limited at claim level
  (the plan may pay more on some lines than line by line). The deductible
  accumulator gets what the plan applied, which is what NAIC §7 requires and
  what makes the next claim see the deductible as met.
- `MemberPaidOnly`: the deductible only counts what the member pays, so it
  is priced **sequentially within the claim** (re-review): line 1 is priced
  and COB'd, its member-paid deductible is credited, then line 2 is priced
  against what is left, and so on, in line order. That is the only way the
  result does not depend on claim splitting — a provider sending the same
  services as one two-line claim or two one-line claims gets the same
  payment and the member the same deductible. Each line is bounded by its
  own share of the prior payers' amounts; a later payer's 2320-only
  (claim-level) paid amount and PR are spread **by the balance the earlier
  payers left on each line** — the previous payer's line PR (what it left the
  member owing), else charge less what was paid; by charge only for the first
  payer (round 3). NAIC §7 pays the lesser of the normal benefit and the
  remaining balance, and `MemberPaidOnly` is a deductible-credit rule only:
  it must not pay less. Golden `07-tertiary-cob-member-paid-only` now pays
  **$58.00** like golden 07 — L1 $49.15 (= the secondary's PR share,
  58 × 100 / 118), L2 $8.85; spreading by charge put $18 of the secondary's
  PR on L2, where only $9.37 of the allowed was left, and paid $49.37. The
  two settings now differ only in the deductible credit ($0 vs $60); the
  per-line split differs (NAIC's claim-level allocation is 51.03 / 6.97).
- Example (the reviewer's): two lines allowed $500 each, $500 deductible
  left, primary paid L1 $500 (PR 0) and L2 $0 with PR-1 $500.
  `NaicFullCredit`: as the only plan, L1 takes the whole $500 deductible, so
  L2 has none; after COB the plan pays $500 on L2, the member $0, and the
  deductible is credited $500. `MemberPaidOnly`: L1 credits $0 (the member
  paid nothing on it), so L2 still carries the $500 deductible; the plan pays
  $0 and the member owes $500, credited $500. Sent as two one-line claims,
  each setting gives the same result as the one claim
  (`CobClaimLevelReviewTests.ResultDoesNotDependOnClaimSplitting`). Pricing
  `MemberPaidOnly` "as the only plan" first, as before, paid $500 on the one
  claim but $0 on the two. `NaicFullCredit` pools the claim's prior-payer
  amounts (MDL-120 §7 is stated per claim), so when 2320-only amounts are
  prorated, how lines are grouped into claims can still change its
  per-claim payment.

**OOP maximum.** MDL-120 requires deductible credit only; it says nothing
about the out-of-pocket maximum, and what other plans paid is not the
member's out-of-pocket spending. The OOP accumulators therefore always get
the member's share after COB, under every setting.

**Flow.** The engine writes the credited deductible to its working
accumulators (`LineBenefitResult.DeductibleCreditedAmount`, DRG lines
allocated by allowed); claims-service stores it (`DeductibleCreditedAmount`)
and publishes it as `ClaimFinalizedEvent.DeductibleCredited` (only when it
differs from PR-1); accumulator-service's `ComputeDeltas` credits it to the
deductible and keeps PR-1 for the service rollup.

**Concurrency.** Each deductible update carries the plan limit
(`AccumulatorUpdate.ClampAtLimit`); the engine store
(`ChoAccumulatorService`) adds at most what is left under it **at write
time**, inside its optimistic-concurrency retry (a conflict reloads and
re-clamps), so two claims priced against the same starting balance cannot
together credit past the limit.

**Default change.** Plans without the field (every plan before this change)
are NAIC full credit. Accumulators for claims adjudicated from now on differ
on secondary/tertiary claims; past accumulators are not rewritten.

## Re-adjudication, voids and replacements

- **Re-adjudicating the same claim** (same claim id): the engine reads the
  claim's own still-active accumulator updates
  (`IAccumulatorService.GetClaimUpdatesAsync`) and takes them out of the
  starting balances, so the second pass prices exactly like the first (it
  does not meet its own deductible); the commit replaces them with the new
  updates in the same write (see "Accumulator writes").
- **Void** (`ClaimFinalizationService.VoidAsync`, including the reversal
  run voiding the version a replacement superseded): claims-service calls
  `IBenefitCalculationEngine.ReverseClaimAsync` (Decision 16 — the engine
  store is what the next claim is priced against), and the Kafka
  `claim.finalized` event with FinalStatus `Reversed` makes
  accumulator-service back out the claim's recorded `ClaimApplied` deltas
  (`ClaimReversed` row, keyed `{claimId}:reversal` — once).
- **Replacement / void claims** (CLM05-3 = 7 / 8) linked to their original
  (`PredecessorVersionId` → `ClaimFinalizedEvent.OriginalClaimId`):
  accumulator-service reverses the original's deltas before applying the
  replacement's (frequency 8: reverse only) — idempotent with the original's
  own void event, so nothing counts twice.
- **Replacement pricing (re-review N6).** A replacement claim
  (`PredecessorVersionId` → `BenefitResolutionRequest.ReplacesClaimId`) is
  priced with the predecessor's still-active engine accumulator updates taken
  out of the starting balances — as if the predecessor had never applied —
  so a replacement identical to its original pays the same (before, it met
  the deductible with its own predecessor's amounts). In Production the
  engine reverses the predecessor's updates before writing the
  replacement's; the store's reverse is idempotent per claim, so the later
  void of the predecessor (reversal run, Kafka `Reversed` event) does not
  reverse it again.
- `ClaimApplied` rows now record the deltas actually applied after clamping
  at the limits (`DeltasClamped = true`), so a reversal backs out exactly
  that.

### Accumulator writes: commit on final adjudication

**Before.** `BenefitCalculationStage` (Order 300) priced in Production — and
wrote deductible / OOP / visit counts to the engine store — whenever no
*earlier* stage had pended. NCCI (400) and AI review (600) pend later, so an
NCCI-pended claim carried its cost share on the member while it waited, a
claim denied by a later stage kept it, and an approval re-run whose lock
expired could write after the new holder's denial had reversed (nothing):
the denied claim kept deductible 100 / OOP 100 (reproduced by
`ApprovalRerun_LockExpiresMidRun_NewHolderDenies_LeavesNoAccumulators`
against the old code).

**Now.**

1. **Every claim is priced read-only.** `BenefitCalculationStage` always
   calls the engine with `ExecutionMode = Prospective`. The engine returns
   the write it would have made as `BenefitResolutionResult.PreparedAccumulatorCommit`
   (`AccumulatorCommit`: claim id, replaced claim id, member / subscriber,
   plan, plan year, the updates with their deductible clamp, and a fresh
   `CommitId`). Pricing reads are unchanged — a pended claim's would-be
   amounts are simply not in the store, so estimates and later claims are
   priced without them.
2. **A passing claim commits once, before persistence.**
   `AccumulatorCommitStage` (Order 990, required) commits the prepared write
   only when every stage so far passed (`ResolveOutcome == Pass`). Pend, Deny
   or Reject → nothing is written. It is required, so a failed commit throws
   and the Service Bus message is redelivered; the projection has not been
   written yet, so the redelivery re-adjudicates and its commit replaces the
   earlier one. A passing claim whose pricing prepared no commit (an older
   benefit-plan-service) fails the run rather than passing without
   accumulators. A commit the store refuses (the claim id is fenced) rejects
   the run.
3. **An approval commits after its fenced final write.** On an approval
   re-run the stage defers; the resolver
   (`ClaimsController.ResolvePendedClaim`) commits
   `PreparedAccumulatorCommit` only after `UpdateHoldingResolutionLockAsync`
   — the token-fenced replace that sets the claim Approved and clears the
   lock — has landed. That write is the guarantee: it is a conditional write
   on the claim document (Mongo filter on the token; Cosmos ETag replace),
   and once it has landed the claim is no longer Pended, so
   `TryAcquireResolutionLockAsync` (which requires Pended) can never give the
   claim to another resolver, and nobody can deny it. A resolver whose lock
   was taken over gets its final write refused and never reaches the commit.
   Not a read-check: the decision "may this resolver write accumulators?" is
   the outcome of the same atomic write that makes its approval final.
4. **The store fences voided and denied claims** (defence in depth, for a
   commit already in flight when the claim was voided or denied).
   `ReverseClaimAsync` is terminal: `IAccumulatorService.ReverseTerminallyAsync`
   reverses the claim and records its id as fenced in the same versioned
   write, even when nothing was applied (the document is created to hold the
   fence). `CommitAsync` checks the fence inside its own versioned write, so
   whichever lands first, a commit and a denial leave nothing (both orders
   tested on Mongo, Cosmos and Redis). Both stores implement it:
   - `ChoAccumulatorService` (Mongo / Cosmos): per document (individual,
     family), one version-checked write that refuses a fenced claim, is a
     no-op for the same `CommitId`, reverses the claim's own and the
     replaced claim's active transactions, then applies the updates with the
     deductible clamped at write time. `AccumulatorDocument.ReversedClaimIds`,
     `AccumulatorTransaction.CommitId`.
   - `RedisAccumulatorService` (what benefit-plan-service runs): one Lua
     script per hash (single key, cluster-safe) with the same checks; the
     claim's commit is recorded in the hash (`__commit:{claimId}` = commit
     id and the amounts it added) and the fence as `__fence:{claimId}`. A
     terminal reversal invalidates the hash as before (the rebuild from
     claims-service counts only Approved / Paid claims) but keeps the
     fences. A commit on a hash with no amounts (evicted / invalidated) adds
     nothing — the next read rebuilds from claims-service.
   The direct Production apply (`ApplyUpdatesAsync`, other callers of the
   engine) is not fenced and keeps its earlier semantics.
5. **Exactly once.** The commit is idempotent on `CommitId`; a new commit of
   the same claim replaces its earlier one; a replacement's commit takes the
   replaced claim's write back (same member and plan year — otherwise the
   predecessor's void does it on its own snapshot).

Endpoint: benefit-plan-service `POST /api/v1/adjudication/commit-accumulators`
(same permission as `reverse-claim`), returns
`{ outcome: Committed | AlreadyCommitted | RefusedClaimReversed }`.

Tests (real databases): GoldenPath `AccumulatorIntegrityMongoTests` /
`AccumulatorIntegrityCosmosTests` (NCCI-pended claim writes nothing until
approved, then once; denied → nothing and a late commit refused; a claim
pended before the change replaced or reversed; lock expiry mid-run then a
denial by the new holder → zero), `RedisAccumulatorCommitTests` (real
redis-server), `ChoAccumulatorCommitCosmosTests`, `AccumulatorCommitStageTests`,
`ExaminerResolutionTests` (commit after the final write; none on a lost
lock). Mutation-checked: with the old Production pricing and the store fence
disabled, the lock-expiry test leaves deductible 100.

### Exactly-once in accumulator-service (re-review N5)

- **Leases.** A `ProcessedClaim` Pending marker younger than
  `ProcessedClaimLease.Timeout` (2 minutes) means an attempt is in flight:
  `TryBeginAsync` returns `InProgress`, the service writes nothing and
  returns `ApplyOutcome.InProgress`, and the Kafka consumer seeks back and
  retries the message after a pause instead of committing. An older marker
  is a crashed attempt, taken over atomically (Mongo conditional update,
  Cosmos ETag) so only one retry wins. This closes the double reversal
  (two deliveries of a void both re-entering a Pending reversal).
- **Versioned writes.** Each event row's id is one per (snapshot, version)
  (`AccumulatorEvent.BuildId`), and snapshot writes are version-conditional
  (`TryReplaceSnapshotAsync`: Mongo filter on `Version`, Cosmos ETag). A
  writer that loses the race re-reads and retries (up to 8 times, then the
  message is retried). A row appended by a writer that crashed before its
  snapshot write is projected by the next writer before its own; an apply or
  reversal whose row already exists completes its marker without writing
  again.
- **One reversal per claim.** Checked under the versioned write (so even two
  workers past the marker write one), plus a Mongo partial unique index on
  (tenantId, sourceClaimId) for `ClaimReversed` rows.
- **Legacy rows.** `ClaimApplied` rows written before `DeltasClamped`
  recorded the requested deltas. Reversing one replays the snapshot's event
  log (clamping at the current limits, from the smallest starting value that
  reproduces the snapshot) to find what it actually applied — limit 500,
  used 450, a legacy row requesting 200: the void takes back 50, leaving
  450. The amount is never more than the row recorded.
- **Frequency 8 first.** A frequency-8 void naming its original reverses the
  original even when it arrives with FinalStatus `Reversed` (it used to be
  treated as a reversal of itself, reversing nothing).
- **A replacement can overtake its original (round 3, B3).** Kafka is keyed
  on the claim id, so a replacement C2 (another key, another partition) can
  arrive before its original C1's apply. Its reversal of C1 found nothing and
  completed as "nothing to reverse"; C1 then applied on top of C2 (deductible
  600 instead of 300). Now a reversal that finds nothing applied checks C1's
  processed-claim marker: **Pending** (C1's apply in flight) → `InProgress`,
  its own reversal marker released, retried later; **none** → a
  `ReversedBeforeApply` tombstone on C1's marker (both Mongo and Cosmos,
  through `IProcessedClaimStore`), so C1's apply is skipped when it arrives;
  terminal → nothing to reverse.
- **The original applies between the reversal's two reads** (round-3
  verification, blocker 2): the reversal finds no `ClaimApplied` row, then
  the original's apply completes, then the reversal reads its marker —
  Applied. That used to finish the reversal as "nothing to reverse" (600
  instead of 300); now an Applied marker sends it back to re-read the row
  and reverse it. Only orphan, denied or tombstone outcomes mean nothing to
  reverse.
- **A stale Pending original** (its apply crashed) no longer blocks a
  replacement forever (M3): the store's lease decides — a Pending marker
  younger than the lease is a live apply (InProgress, retried); an older one
  is taken over and tombstoned, so the crashed apply's redelivery is skipped.
- **Two replacements of the same original** (follow-up): C2 replaced C1, then
  C3 also names C1. C1 is reversed only once, so both used to count. C3 is
  not applied (`ApplyOutcome.Skipped`, marker `SecondReplacementSkipped`)
  and an `OrphanAccumulatorClaimEvent` reports it for review — a
  replacement must name the latest version (C2). Tombstones now record the
  replacement that made them, so this holds when C2 overtook C1 too. A
  replacement after the original's own void still counts.
- **A void is not a replacement (PR #1278 follow-up 3).** `ClaimReversed`
  rows and reversal tombstones record `ReversalKind` (`Replacement`, `Void`
  — a frequency-8 void or the claim's own void — or `Denial`). Only a
  replacement makes a later replacement of the same original a "second
  replacement". Before, a void V1's row named V1 and a later replacement C2
  of the same original was skipped (deductible 0, reported as an orphan).
  When the original was voided rather than replaced, the first replacement
  to claim the `{original}:replacement` marker is the replacement of record;
  a further one is still skipped. Rows written before the kind was recorded
  keep the earlier rule (another claim as source = a replacement).
- **A stalled apply cannot count after a takeover (PR #1278 follow-up 2).**
  The stale-Pending takeover above assumed the earlier apply had crashed; it
  may only have stalled. Before, C1's apply could stall past the lease before
  its append, C2 take C1's marker over, tombstone it and apply 200, and C1
  then resume, append 100 and overwrite the tombstone (deductible 300). Now:
  every `Proceed` gets a fresh **lease token** on the marker
  (`BeginLeaseAsync`), and completions are conditional on the marker still
  being Pending under that token (`CompleteLeaseAsync`; Mongo update filter,
  Cosmos ETag replace). Taking over a stale Pending marker also appends a
  zero-delta `ClaimTombstoned` row at the next version of the snapshot the
  claim applies to, so an append the stalled apply computed earlier
  conflicts. The apply loop re-checks, after every snapshot read, that it
  still holds its lease and that no `{id}:reversal` has completed, and stops
  if not. If the stalled apply's row lands first, the tombstone row
  conflicts, the reversal finds the row and reverses it — counted once
  either way.
- **The fence goes on the original's own snapshot** (accumulator
  integrity). A replacement can resolve to another member or plan year than
  its original (C1 in 2025, its replacement C2 in 2026). The tombstone row
  used to go on the *reversing* claim's snapshot, so C1's stalled append to
  2025 did not conflict and landed after the takeover (2025: 250 instead of
  50). Now, before every append to a snapshot it has not recorded yet, the
  apply records that snapshot on its own Pending marker, conditional on its
  lease (`IProcessedClaimStore.RecordLeaseTargetAsync`; Mongo update filter,
  Cosmos ETag replace; `ProcessedClaim.TargetSnapshotId` / member / plan
  year). A reversal that takes the lease over reads the marker *after* the
  takeover and appends the zero-delta row on that snapshot; from the
  takeover on, the stalled apply cannot record (no lease) and so cannot
  append. No target recorded means the apply never got that far (or ran on
  an older build): the reversing claim's snapshot is used, as before. The
  reversal itself always backs the original out of the snapshot its
  `ClaimApplied` row names. Tested both ways round (stalled append fenced;
  stalled row first → reversed on 2025) for a plan-year change and a member
  change, on Mongo and Cosmos; mutation-checked.
- **Denied claims** are not applied (`ApplyOutcome.Skipped`, marker
  `DeniedNotApplied`); a claim that had applied and is later finalized as
  Denied is reversed.
- **Indexes at startup (round 3, M7).** The Mongo indexes are created once by
  `AccumulatorMongoIndexInitializer` (a hosted service), not in the scoped
  repositories' constructors. Before creating the unique reversal index it
  re-tags any existing duplicate `ClaimReversed` rows
  (`ClaimReversedDuplicate`, logged for an operator adjustment), so a
  database already holding a double reversal cannot make startup — and the
  consumer — fail in a loop.

## Limitations

- **COB model.** Always standard (complementary) from the 837 path; there is
  no plan-level COB-method setting (non-duplication is engine-supported but
  only reachable through the API's `useComplementaryModel`).
- **Accumulation order is finalization order.** A pended claim's cost share
  counts when it is approved, not by its service date: a later-dated clean
  claim adjudicated while it waits is priced first (it may meet the
  deductible the pended claim would have met). Inherent to not counting
  pended claims.
- **Approval commit after the final write.** If the commit fails after the
  approval's final write (benefit-plan-service down), the approval stands
  and the engine store lacks the claim until someone re-posts the commit
  (logged at error with the commit body; idempotent on its id). There is no
  automatic re-drive. With Redis, the next cache rebuild counts the
  approved claim anyway.
- **First-pass commit before persistence.** A passing claim commits at Order
  990, then persists. If persistence then refuses the write (the version was
  superseded or voided meanwhile), the commit stays until that version's
  void reverses it terminally.
- **Redis cache vs rebuild.** A commit on a cold hash adds nothing and relies
  on the rebuild; a rebuild that runs between a first-pass commit and its
  persistence misses the claim until the next invalidation (as before, for
  the Production write). Redis keeps no per-claim journal for pricing
  (`GetClaimUpdatesAsync`), so a re-adjudication is not priced without its
  own earlier commit (pre-existing).
- **Engine Cosmos store serializer.** `AccumulatorRepositoryCosmos` was tested
  with a camelCase client; benefit-plan-service registers a default
  `CosmosClient` (PascalCase, no `id`), which would not work — moot today,
  since benefit-plan-service runs the Redis store.
- **Legacy-row replay** uses the current limits; a limit changed since the
  row was written, or used amounts loaded into a snapshot outside the event
  log, can make it inexact (never more than the row recorded).
- **Cosmos reversal uniqueness** relies on the versioned write and lease (no
  unique key policy is created from code); Mongo also has the unique index.
- **Store clamp.** `ChoAccumulatorService` and the Redis commit clamp the
  deductible at write time; the Redis direct Production apply does not.
- **Legacy reversal kinds.** Rows and tombstones written before
  `ReversalKind` keep the old rule until backfilled
  (`tools/AccumulatorReversalKindBackfill`,
  [runbook](../operations/ACCUMULATOR-REVERSAL-KIND-BACKFILL.md)); originals
  the evidence cannot classify stay AMBIGUOUS and keep it.
- 2320/2430 `AMT*EAF` (remaining patient liability) is not read; patient
  responsibility comes from PR CAS.
- The 2000B SBR03 group number is still lost when SBR precedes NM1*IL
  (pre-existing).
- Within a claim the per-line attribution of the claim-level payment is a
  documented allocation rule (by normal benefit within each line's
  balance); the claim totals are exact.

## Deploy notes (accumulator integrity)

1. **Order: benefit-plan-service first, then claims-service.** A new
   claims-service against an old benefit-plan-service gets no prepared
   commit: every passing claim fails its run (by design — never pass without
   accumulators) and Service Bus retries it. The new benefit-plan-service is
   compatible with the old claims-service (Production pricing unchanged;
   `reverse-claim` now also fences, which the old claims-service only calls
   for voids and denials). accumulator-service is independent.
2. **Claims pended before the deploy** wrote their accumulators at Order 300.
   Approving one replaces that write (not doubled — Mongo / Cosmos store; the
   Redis store has no record of the old Production write, so its cache counts
   it twice until the next invalidation of that member, as an approval re-run
   did before); denying one backs it out.
3. **Rollback of benefit-plan-service** (Redis store): older builds treat a
   hash holding only fence / commit fields as populated and empty. Flush the
   accumulator cache on rollback (`SCAN … MATCH accum:*` + `DEL`; it rebuilds
   from claims-service). The Mongo / Cosmos engine documents now carry
   `ReversedClaimIds` / `CommitId` and `[BsonIgnoreExtraElements]`; a build
   from before this change cannot read them back (no production data: the
   Mongo store could not write before this change — Guid representation).
4. **accumulator-service** markers gain `TargetSnapshotId` /
   `TargetMember` / plan-year fields; older builds ignore them
   (`[BsonIgnoreExtraElements]`, Cosmos JSON). During a rolling deploy an
   apply on an old pod records no target, and a takeover by a new pod falls
   back to the earlier behaviour for that claim.
5. **Backfill** of legacy reversal kinds: after the deploy, per the runbook.
6. CI runs the Redis store tests against a `redis:7-alpine` service
   (`CHO_TEST_REDIS`); locally they start `redis-server` from the PATH or are
   skipped.

## Cross-references

- [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md) —
  orchestrator + stage interface foundation (5.5).
- [`claim-ncci-pipeline.md`](./claim-ncci-pipeline.md) — immediate
  upstream stage at Order=400 (5.7).
- coverage-service `GET /api/v1/coverage/member/{memberId}/cob` —
  upstream contract consumed by `HttpCoverageClient`.
