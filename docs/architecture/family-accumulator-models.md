# Family Accumulator Models — Embedded vs Aggregate (BP 5.7)

This doc describes the plan-level family accumulator pooling model on
`BenefitPlan`, the ACA 45 CFR §156.130 individual out-of-pocket cap that
applies to Aggregate plans, and the gated rollout strategy for runtime
enforcement.

## What changed in 5.7

Before BP 5.7:

- `BenefitEngine.Domain.FamilyAccumulatorModel` enum existed (Embedded /
  Aggregate) and `AccumulatorWorkingSet` honored both modes, but
  `BenefitPlan` had no field exposing the choice. Every plan
  effectively adjudicated as Embedded regardless of author intent.
- Aggregate mode in the engine seeded only family-level accumulators —
  no per-member sub-cap. A single member could in principle absorb the
  entire family OOP pool, which violates ACA §156.130.

After BP 5.7:

- `BenefitPlan.FamilyAccumulatorModel` (top-level field, default
  `Embedded`) carries the plan author's choice. Mirrored on
  `MemberBenefitView`, `AdapterBenefitPlan`, `AdapterMemberBenefitView`,
  and the portal's `MemberBenefitView` DTO.
- `ChoBenefitPlanProvider.MapToConfig` projects the field onto the
  engine's `BenefitPlanConfig.FamilyAccumulatorModel` plus the new
  `AcaIndividualCap` (loaded via `IAcaLimitsProvider` from
  `schemas/aca-oop-limits/limits.json`) and the `IsAcaCapEnforced` flag.
- `AccumulatorWorkingSet` (Aggregate path) seeds an
  `AccumulatorType.AcaIndividualCap` accumulator alongside the family
  pool when `IsAcaCapEnforced` is true. `GetRemainingOopMax` returns
  `min(family pool remaining, ACA individual cap remaining)`;
  `ApplyOopMax` increments both buckets in lockstep.
- `IPlanLimitValidator` is wired into all five
  `BenefitPlanServiceImpl` write surfaces (`CreatePlanAsync`,
  `UpdatePlanAsync`, `CreateDraftAsync`, `AmendPublishedPlanAsync`,
  `PublishVersionAsync`). Plans with cost-sharing limits exceeding the
  ACA caps are rejected with HTTP 400.

## Embedded vs Aggregate semantics

### Embedded

Each member has an individual deductible and individual out-of-pocket
maximum tracked independently. The plan also tracks family aggregates.

- Individual deductible met → that member's portion is satisfied; other
  members continue accumulating against their own individual limits.
- Family deductible met → all members' deductible portions are
  satisfied (the engine returns 0 remaining individual deductible when
  family is met).
- OOP works the same way: meeting family OOP zeroes out each member's
  remaining individual responsibility.

ACA §156.130 individual cap is enforced **at write time** by
`IPlanLimitValidator` ensuring `IndividualOutOfPocketMax ≤
acaIndividualCap`. Runtime enforcement is implicit: the existing
`IndividualOutOfPocketMax` accumulator already constrains members.

### Aggregate

A single shared family pool. No individual sub-limit by default —
members all draw from the same pot.

But ACA §156.130 still applies: even on Aggregate plans, no individual
member may absorb more than the ACA individual OOP cap for the plan
year. Without this, a single member could exhaust the entire family
pool, which is non-compliant.

Aggregate mode therefore tracks two accumulators per network tier:
1. **Family pool** (`FamilyOutOfPocketMax`) — primary ceiling shared
   by all members.
2. **ACA individual cap** (`AcaIndividualCap`) — per-member ceiling
   equal to the §156.130 cap for the plan year. Caps how much of the
   family pool any single member may absorb.

Member responsibility on a claim line is the minimum of:
- Family pool remaining
- AcaIndividualCap remaining for this member

The ACA cap accumulator is `AccumulatorScope.Individual`, so each
member's working-set hydrates only their own row from the persistent
store. This is the same hydration shape as the existing
`IndividualOutOfPocketMax` accumulator in Embedded mode.

## Gated rollout — `IsAcaCapEnforced` (G8)

Rolling out runtime ACA cap enforcement across a population of
in-flight Aggregate plans creates an operational concern: a member who
has already accumulated $10,000 against an uncapped Aggregate pool
shouldn't suddenly hit the cap mid-claim and get a confusing change in
member responsibility.

The rollout is therefore gated by `BenefitPlanConfig.IsAcaCapEnforced`,
set by `ChoBenefitPlanProvider.MapToConfig`:

- **New publishes** (plans with `PublishedAt` ≥ the post-5.7 cutoff,
  currently `2026-04-28T00:00:00Z`) get `IsAcaCapEnforced=true`. Cap
  is enforced from the first claim.
- **Legacy plans** (`PublishedAt` before the cutoff) hydrate with
  `IsAcaCapEnforced=false`. Engine behavior is unchanged from pre-5.7
  — Aggregate plans run with family pool only, no per-member cap.
- **Drafts** (`PublishedAt == null`) get `IsAcaCapEnforced=true`. The
  draft is not yet adjudicating live claims, and any future publish
  goes through the validator anyway.
- **Embedded plans** always have `IsAcaCapEnforced=false`. The flag
  only matters in Aggregate mode.

Operators flip a legacy plan to enforced state by re-publishing the
plan version (creating an amendment via `POST /amend` and publishing
it). `PublishedAt` advances past the cutoff, the new
`BenefitPlanConfig` carries `IsAcaCapEnforced=true`, and the new
working-set seeds the cap accumulator on the next claim.

This is **transition support, not permanent legacy support.** The
team's intent is to deprecate the flag within 6–12 months of 5.7
shipping. Until then, operators are expected to roll their Aggregate
plans through one full publish-cycle so the entire population
converges on enforced state.

## Validation — `IPlanLimitValidator`

Hard-validation gate; throws `PlanLimitValidationException` (mapped to
HTTP 400 by the controllers). Distinct from the soft-validation
`INetworkTierSoftValidator` — regulatory caps are not negotiable.

The validator runs on **both** modes:
- **Embedded**: `IndividualOutOfPocketMax ≤ acaIndividualCap` AND
  `FamilyOutOfPocketMax ≤ acaFamilyCap`.
- **Aggregate**: same two checks. The runtime-cap accumulator gives
  defense-in-depth; the validator is the primary guard against
  noncompliant plans landing in the store at all.

Plan-year resolution (consumed by both the validator and
`ChoBenefitPlanProvider`):

```
PlanYearResolver.Resolve(plan):
    if plan.PlanYearDefinition.PlanYearStart != default:
        return PlanYearStart.Year
    return plan.EffectiveDate.Year
```

`PlanYearDefinition` (5.3) is authoritative when present because it
carries the author's explicit plan-year intent. `EffectiveDate.Year`
is a defensible fallback for plans authored before 5.3 or that opted
out of the optional `PlanYearDefinition`.

If the resolved plan year is not in `schemas/aca-oop-limits/limits.json`,
the validator **fails closed** with a 400 response that lists the
configured years. Better to force operators to refresh the seed file
than silently accept a plan against missing caps.

## Configuration — `schemas/aca-oop-limits/limits.json`

File-backed seed file loaded once at service startup by
`AcaLimitsProvider`. See
[`schemas/aca-oop-limits/README.md`](../../schemas/aca-oop-limits/README.md)
for the schema, source attribution, and update cadence.

Operators bump the file when CMS publishes the annual NBPP final rule.
The file ships with values for plan years 2024–2027 (2027 is a
projection per the revised methodology — verify against the actual
2027 NBPP final rule when published).

**ACA cost-sharing max ≠ IRS HSA-qualified HDHP max.** They are
different limits with different regulatory sources. This file holds
the ACA value only; the IRS HDHP cap is enforced separately. See the
schema README for details.

## Telemetry

- `cho.benefit_plan.plan_limit_validation_failures.total` — counter,
  one increment per 400 rejection from `IPlanLimitValidator`.
  Dimensions: `cho.caller`, `cho.tenant_id`, `cho.reason`
  (`PlanYearNotConfigured` | `IndividualOopExceedsAcaCap` |
  `FamilyOopExceedsAcaCap`).

The validator is hard, not soft — every increment corresponds to a
plan that operators tried to land but couldn't. Spikes here are a
signal that either the seed file is out of date for a plan year
operators are authoring against, or operator authoring tooling needs a
nudge to surface the cap before submit.

## Out of scope for 5.7

- `BenefitRulePredicate` evaluation in the calc engine — shipped in
  **BP 5.10**; see
  [`adjudication-api-stabilization.md`](adjudication-api-stabilization.md).
- `Unknown=0` migration on `FamilyAccumulatorModel` and
  `AccumulatorType` enums (deferred to a follow-up "BenefitEngine enum
  hygiene" PR — see PR #705 convention).
- `accumulator-service` standalone schema work — its typed-column
  storage doesn't touch the new `AcaIndividualCap` bucket.
- Portal UX distinguishing Embedded vs Aggregate visually — DTO
  mirrors only. The string field is on the wire so a future UX PR can
  pick it up without another round-trip change.
- `BenefitResolutionResult` shape changes — `AccumulatorSnapshot`
  already surfaces the new bucket via enum-name-string keying.

## Cross-references

- [`docs/architecture/plan-versioning.md`](plan-versioning.md) —
  `FamilyAccumulatorModel` is naturally version-identity-bearing.
  Changing it on a Published plan requires a new version (the same as
  any cost-sharing change).
- [`docs/architecture/plan-year-definition.md`](plan-year-definition.md)
  — `PlanYearDefinition` is the primary plan-year source consumed by
  the validator's resolver.
- [`schemas/aca-oop-limits/README.md`](../../schemas/aca-oop-limits/README.md)
  — seed file authoring, CMS source attribution, methodology change.
- [`schemas/service-category-mappings/README.md`](../../schemas/service-category-mappings/README.md)
  — sister versioned-seed pattern from 5.6.
