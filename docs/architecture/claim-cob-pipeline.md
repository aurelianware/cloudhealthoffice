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
| secondary (one "P") | U / absent | — | mode-driven pend (the Phase-1 posture) | `cob-secondary-not-supported-phase-1` |
| secondary or tertiary | P, or a different later position (secondary ↔ tertiary-or-later) | — | **pend in every mode** | `cob-payer-order-mismatch` |
| agree | — | two 2320 loops (or a 2320 and 2000B) with the same sequence | **pend in every mode** | `cob-duplicate-payer-sequence` |
| agree | — | a 2430 SVD01 that names no other payer (2330B NM109, REF*2U, REF*FY) while there are several | **pend in every mode** | `cob-unmatched-line-adjudication` |
| agree | — | no 2320 with AMT*D or 2430 SVD for some earlier sequence | mode-driven pend | `cob-secondary-not-supported-phase-1` |
| agree | — | complete | **pass; COB applied** (`CobOutcome.ApplyCob`, `PayerSequence`) — the claim finalizes | — |

"Agree" means coverage secondary ↔ SBR01 S, coverage tertiary (two "P", or
"P" + "S") ↔ SBR01 T or A–H. The reason code
`cob-secondary-not-supported-phase-1` keeps its legacy name for
work-queue / telemetry continuity; it now means "no usable prior-payer data
on the 837". If the COB stage did not run at all (disabled) and the 837
says later payer, `BenefitCalculationStage` pends (pend code COB) rather
than price the claim as primary.

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
   Remainders go to the last line with a positive charge. Its 2320 PR CAS is
   kept as one claim-level amount, not prorated.
3. **Bounding payer.** For each line, the bounding payer is the last payer
   that *adjudicated* it: paid more than $0, or left a PR. A payer that paid
   $0 with only CO/OA adjustments (CO-27, CO-22, CO-109, CO-96, CO-204, …)
   did not cover the service — its "PR = 0" is not what the member owes, so
   it bounds nothing (review B1).
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

Line-by-line capping (the previous approach) lost money whenever claim-level
prior-payer amounts were prorated in proportions different from this plan's
line amounts: golden 07 paid $49.37 instead of $58.00 and left $8.63 neither
paid nor billable to the member (review M1). The claim-level allowable-
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
| `MemberPaidOnly` | only the PR-1 the member owes after COB — self-funded ERISA plans with non-duplication / carve-out provisions | **same as `NaicFullCredit`** |
| `NoDeductible` | nothing: the deductible is not applied | no PR-1 (the deductible is skipped as a later payer) — Medicaid-secondary plans. Rejected on an HDHP (IRC §223(c)(2)). |

Source: MDL-120 §7, "the secondary plan shall credit to its plan deductible
any amounts it would have credited to its deductible in the absence of other
health care coverage."

**What changes on the 835, plainly.** Compared with line-by-line COB, a
multi-line secondary or tertiary claim's 835 changes under every setting:
because the claim is priced as the only plan first, the deductible met on
one line is met for the claim's later lines (their PR-1 drops), and the
payment is limited at claim level (the plan may pay more on some lines).
`NaicFullCredit` and `MemberPaidOnly` give the same 835; they differ only in
what the deductible accumulator records for the claims that follow.

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
  does not meet its own deductible); in Production it reverses them before
  writing the new updates (the store's apply is idempotent per claim).
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
- `ClaimApplied` rows now record the deltas actually applied after clamping
  at the limits, so a reversal backs out exactly that.

## Limitations

- **COB model.** Always standard (complementary) from the 837 path; there is
  no plan-level COB-method setting (non-duplication is engine-supported but
  only reachable through the API's `useComplementaryModel`).
- **Accumulators for claims pended after benefit calculation.** A claim the
  COB stage (or any earlier stage) pends is priced read-only. A claim a
  *later* stage pends (NCCI at 400) still writes accumulators at 300 — a
  pre-existing ordering issue outside COB.
- **Store clamp.** Only `ChoAccumulatorService` (Mongo / Cosmos) clamps at
  write time; the Redis accumulator service does not.
- 2320/2430 `AMT*EAF` (remaining patient liability) is not read; patient
  responsibility comes from PR CAS.
- The 2000B SBR03 group number is still lost when SBR precedes NM1*IL
  (pre-existing).
- Within a claim the per-line attribution of the claim-level payment is a
  documented allocation rule (by normal benefit within each line's
  balance); the claim totals are exact.

## Cross-references

- [`claim-adjudication-pipeline.md`](./claim-adjudication-pipeline.md) —
  orchestrator + stage interface foundation (5.5).
- [`claim-ncci-pipeline.md`](./claim-ncci-pipeline.md) — immediate
  upstream stage at Order=400 (5.7).
- coverage-service `GET /api/v1/coverage/member/{memberId}/cob` —
  upstream contract consumed by `HttpCoverageClient`.
