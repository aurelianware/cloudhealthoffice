# Coordination of Benefits Pipeline (Capability 5.8)

> **Status — Phase 1, May 2026.** Replaces `CoordinationOfBenefitsStubStage`
> at Order=500 with a real `CloudHealthOffice.CobEngine`-backed
> implementation. Phase 1 ships **CHO-primary adjudication only** plus a
> structured Phase 2 hook stub that detects CHO-secondary scenarios via
> coverage-service's `/member/{id}/cob` endpoint and emits a Pend with the
> stable reason `cob-secondary-not-supported-phase-1`. The detection-only
> posture produces explicit Phase 2 sizing telemetry rather than letting
> CHO-secondary claims fall into generic Pend reasons.
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
    300 BenefitCalculation      (5.5)
    400 NcciEdits               (5.7)
    500 CoordinationOfBenefits  ◄ 5.8 — this doc
    600 AiExamination           (stub; 5.9 — may consume CobResult on context)
    999 Persistence             (5.5)
```

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

The detection stage above reads coverage-service. The *calculation* runs
inside the benefit engine (`BenefitCalculationStage`, Order 300) from the
payer data on the 837 itself, on both the per-line and DRG / per-diem paths.

### Where the data comes from

| 837 (005010X222A1 / X223A2) | Claim field | Engine input |
|---|---|---|
| 2000B `SBR01` (P/S/T/A–H/U) | `Claim.PayerResponsibilityCode` | `CobInfo.PayerSequence` (1, 2, 3, 4–11) |
| 2320 `SBR01` | `ClaimOtherPayer.PayerResponsibilityCode` | `PriorPayerAdjudication.Sequence` |
| 2330B `NM1*PR` NM103 / NM109 | `PayerName` / `PayerId` | same |
| 2320 `AMT*D` (payer paid amount) | `ClaimOtherPayer.PaidAmount` | `ClaimPaidAmount` |
| 2320 `CAS` (claim-level adjustments) | `ClaimAdjustments` | `ClaimAdjustments` |
| 2430 `SVD02` / `CAS` (line adjudication; SVD01 = 2330B NM109) | `LineAdjudications` | `Lines` |

`X12837Parser` reads these loops (and keeps 2330A–I `NM1`/`N3`/`N4`/`DMG`
from overwriting the claim's own subscriber and billing provider);
`X12837ClaimMapper` attaches each 2430 to its payer by SVD01.
`BenefitCalculationStage.BuildCob` sends the engine every other payer whose
sequence is below ours. No COB when we are primary, our sequence is unknown
(`U` / absent), or no earlier payer is on the claim. The model is standard
(complementary); the claim does not carry a plan COB method.

### Line vs claim level (`PriorPayerAllocator`)

Per prior payer: a line it reported in 2430 takes its SVD02 and 2430 PR CAS.
The rest of its 2320 AMT*D (claim paid − Σ SVD02) is prorated by charge
across the lines it did not report (all lines when it reported every line);
a shortfall (TR3: AMT*D = Σ 2430 SVD02 − Σ 2320 CAS) comes off the 2430 line
payments in proportion. 2320 PR CAS is prorated by charge the same way.
Shares are truncated to the cent, remainder on the last line. A DRG stay
uses each payer's sums (= AMT*D). X12 leaves SVD vs AMT*D to the payer
("Payers should indicate what prior payer payment amount impacted their
payment for the specific line", RFI #2806); 2430 is the line-specific one.

### The calculation (`CobCalculationService`)

Sources: NAIC Coordination of Benefits Model Regulation (MDL-120, 2013):
§7 (the secondary "shall calculate the benefits it would have paid ... in
the absence of other health care coverage and apply that calculated amount
to any allowable expense under its plan that is unpaid by the primary plan",
total benefits of all plans ≤ 100% of the allowable expense) and §6.A(4)
("Each secondary plan shall take into consideration the benefits of the
primary plan or plans and the benefits of any other plan ... [that] has its
benefits determined before those of that secondary plan").

Per unit (line or stay):

```
priorPaid = Σ every prior payer's paid amount
balance   = min(allowed − priorPaid, last prior payer's PR when reported), ≥ 0
standard  : paid = min(normal benefit, balance)
non-dup   : paid = min(max(0, normal benefit − priorPaid), balance)
member    = min(pre-COB cost share, balance − paid)
OA-23     = allowed − member − paid      (one positive amount: all prior payers' impact)
```

The PR-1/2/3 amounts are reduced to `member` (coinsurance, copay, then
deductible); charge − ΣCAS = paid on every line and claim; no negative CAS.
CLP02 is 2 (processed as secondary) or 3 (tertiary; also payers 4–11, which
835 has no code for).

## Deductible credit on secondary and later plans

Per-plan setting `cobDeductibleCredit` on the benefit plan document
(`BenefitPlan.CobDeductibleCredit` → engine `BenefitPlanConfig.CobDeductibleCredit`).
Applies only when the plan is not the first payer:

| Value | Deductible accumulator | 835 |
|---|---|---|
| `NaicFullCredit` (default) | the deductible our own adjudication applied before COB (already limited to the remaining deductible and by the OOP cap), including deductible a prior payer paid | the unit itself: PR-1 = what the member owes; later lines of the same claim treat the credited deductible as met, so their PR-1 is lower than under `MemberPaidOnly` |
| `MemberPaidOnly` | only the PR-1 the member owes after COB (behavior before this setting) — self-funded ERISA plans with non-duplication / carve-out provisions | as before this setting |
| `NoDeductible` | nothing: the deductible is not applied | no PR-1 (the deductible is skipped as a later payer) — Medicaid-secondary plans |

Source: MDL-120 §7, "the secondary plan shall credit to its plan deductible
any amounts it would have credited to its deductible in the absence of other
health care coverage."

**Within the claim.** Under `NaicFullCredit` the plan prices the claim as
it would have with no other coverage (MDL-120 §7), so the deductible
credited on an earlier line counts as met for the claim's later lines
(`AccumulatorWorkingSet.ApplyDeductibleWithCredit` writes the credit to the
working accumulators the later lines read). Their pre-COB deductible, and so
the member's share and PR-1, drop; each line still balances (charge − ΣCAS =
paid, one positive OA-23, no negative CAS) and CLP04 = Σ SVC03. **This
changes the 835 of multi-line secondary and tertiary claims under the
default setting** compared with `MemberPaidOnly`, where later lines still
meet the deductible the member did not pay. The total credited never passes
the deductible limit. A DRG stay is one unit, so the setting does not change
its 835.

**OOP maximum.** MDL-120 requires deductible credit only; it says nothing
about the out-of-pocket maximum, and what other plans paid is not the
member's out-of-pocket spending. The OOP accumulators therefore always get
the member's share after COB, under every setting.

The engine writes the credited deductible to its working accumulators
(`LineBenefitResult.DeductibleCreditedAmount`, DRG lines allocated by
allowed); claims-service stores it (`DeductibleCreditedAmount`) and
publishes it as `ClaimFinalizedEvent.DeductibleCredited` (only when it
differs from PR-1); accumulator-service's `ComputeDeltas` credits it to the
deductible and keeps PR-1 for the service rollup.

**Default change.** Plans without the field (every plan before this change)
are NAIC full credit. Accumulators for claims adjudicated from now on differ
on secondary/tertiary claims; past accumulators are not rewritten.

## Cross-references

- [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md) —
  orchestrator + stage interface foundation (5.5).
- [`claim-ncci-pipeline.md`](./claim-ncci-pipeline.md) — immediate
  upstream stage at Order=400 (5.7).
- coverage-service `GET /api/v1/coverage/member/{memberId}/cob` —
  upstream contract consumed by `HttpCoverageClient`.
