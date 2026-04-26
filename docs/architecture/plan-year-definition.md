# Plan-Year Definition (Foundation)

Status: 5.3 — initial implementation (Phase 1: event emission)
Service: `src/services/benefit-plan-service`
Companion: `src/services/accumulator-service` (subscriber, idempotent)

## Why

Until 5.3, plan-year boundaries were implicit. `BenefitPlan.EffectiveDate`
and `BenefitPlan.TerminationDate` told consumers when a plan was active,
but not how it renewed: was the plan a calendar year, an employer
contract year, a CMS fiscal year, or a per-member enrollment
anniversary? Each downstream service (accumulator-service, benefit
engine, member portal) had to infer the type from naming conventions or
out-of-band tenant config — and any disagreement led to accumulators
resetting on the wrong day.

5.3 makes the plan-year a **first-class declarative concept** so every
consumer derives the same window from the same field.

## Scope

**Phase 1 (this PR):** model + scheduler + event emission only. No
accumulator orchestration; subscribers receive events but are not yet
required to act on them.

**Phase 3 (separate prompt):** accumulator-service consumes the events
and applies `PlanYearResetBehavior` per `AccumulatorTarget`.

`BenefitPlan.EffectiveDate` / `TerminationDate` are preserved verbatim;
they remain authoritative for plan activation. `PlanYearDefinition` is
optional — plans created before this feature deserialize with a null
definition, which the scheduler treats as opt-out (no events emitted).

## Model

```csharp
class BenefitPlan {
    // ... existing fields preserved ...
    PlanYearDefinition? PlanYearDefinition { get; set; }
    List<AccumulatorTarget> AccumulatorTargets { get; set; }
}

class PlanYearDefinition {
    DateTime PlanYearStart;
    DateTime PlanYearEnd;
    PlanYearType PlanYearType;     // Calendar | Contract | Fiscal | EnrollmentAnniversary
    int CarryoverDays;             // retro-claim grace window
    int? AnnualResetDay;
}

class AccumulatorTarget {
    string BenefitCategory;
    string Unit;                   // USD | Visits | Days | Units
    decimal Limit;
    PlanYearResetBehavior ResetBehavior;
    decimal? RolloverCap;          // RolloverWithCap only
}
```

`PlanYearResetBehavior` values:

| Value | Meaning |
| ----- | ------- |
| `ResetAtPlanYearEnd` | Zero the counter at `PlanYearEnd`. Default. |
| `NoReset` | Carry across boundaries unchanged (lifetime maximums). |
| `RolloverWithCap` | Roll up to `RolloverCap`; discard remainder. |
| `InheritFromPredecessorPlan` | Start at predecessor plan's closing balance. |

## Window computation

`PlanYearDefinition.ComputeWindow(asOf)` returns the inclusive
(start, end) window containing `asOf`:

- **CalendarYear** — snaps to January 1 → December 31 of `asOf.Year`.
  The persisted anchor's year is intentionally ignored: a plan declared
  in 2020 is still a calendar plan in 2026.
- **ContractYear / FiscalYear / EnrollmentAnniversary** — rolls the
  persisted anchor forward (or backward) in 1-year hops until the
  window contains `asOf`. A 200-iteration safety cap defends against
  pathological inputs.

## Event stream

Two event types land in the append-only `PlanYearTransitionEvents`
collection (Mongo today; Cosmos when cross-store consistency is
needed — same trajectory as `PlanVersionEvents`):

| Type | Fired when | Purpose |
| ---- | ---------- | ------- |
| `ApproachingTransition` | `now` is within `PlanYearScheduler:ApproachingDays` of `PlanYearEnd` (default 30) | Warm caches; member-portal notifications. |
| `Transition` | `now > PlanYearEnd + CarryoverDays` | Trigger reset / rollover work in accumulator-service (Phase 3). |

Both types share the `PlanVersionEvent` envelope: monotonic `Version`
per `(TenantId, PlanId)`, partition key `{TenantId}:{PlanId}`,
deterministic `EventId`.

### Idempotency

`EventId` is computed deterministically:

```
{transitionType}:{tenantId}:{planId}:{planYearEnd:yyyyMMdd}
```

Two scheduler replicas racing (or a single scheduler running on every
tick) collapse to a single row via the unique index on
`(TenantId, PlanId, EventId)`. The publisher's pre-insert lookup
short-circuits the duplicate so we don't pay for a write that will be
rejected; the unique index is the safety net for two replicas hitting
the lookup-then-insert window simultaneously.

`PlanYearTransitionEventIndexInitializer` ensures both
`(TenantId, PlanId, EventId)` and `(TenantId, PlanId, Version)`
unique indexes exist on startup.

## Scheduler

`PlanYearScheduler` is a `BackgroundService` that periodically:

1. Enumerates plans via `IPlanYearScheduleSource` (Mongo prod /
   in-memory tests).
2. Computes the current plan-year window for each plan with a
   `PlanYearDefinition`.
3. Decides which event (if any) to emit:
   - `now > PlanYearEnd + CarryoverDays` → `Transition`.
   - `now ≥ PlanYearEnd − ApproachingDays` and `now ≤ PlanYearEnd` →
     `ApproachingTransition`.
4. Calls `IPlanYearTransitionPublisher`. Idempotency guarantees the
   publisher does the right thing on reruns.

### Tunables (`appsettings.json` / `PlanYearScheduler:`)

| Key | Default | Notes |
| --- | ------- | ----- |
| `IntervalMinutes` | 360 (6h) | How often the sweep runs. |
| `ApproachingDays` | 30 | 0 disables approaching events. |
| `StartupDelaySeconds` | 30 | Stagger so we don't fight other init. |

### Multi-replica safety

The scheduler is safe to run on every replica. Idempotency lives in the
publisher's deterministic `EventId`; two replicas computing the same
`(planId, planYearEnd, type)` produce one row. This matches the
operational posture of `PlanVersionEventPublisher`.

### Failure handling

A failed sweep logs and retries on the next tick — never takes the host
down. Events are idempotent, so any sweep that partially completed will
be reconciled on the next pass.

## Subscriber contract (Phase 3 — for reference)

`accumulator-service` subscribes to the event stream. Per the existing
[accumulator architecture](accumulator-service.md), the subscriber must
be idempotent: re-delivery of a `Transition` event for the same
`{planId, planYearEnd}` must not double-reset or double-roll. The
subscriber will key on `(TenantId, PlanId, EventId)` (already the
publisher's idempotency key) and persist the high-water mark in its own
`ProcessedPlanYearTransition` store, mirroring `ProcessedClaim`.

## Backward compatibility

- `BenefitPlan.EffectiveDate` and `BenefitPlan.TerminationDate` are
  unchanged in shape and semantics.
- `PlanYearDefinition` is optional. Legacy plans that never set it are
  invisible to the scheduler.
- `AccumulatorTargets` defaults to an empty list. The accumulator-service
  retains its existing behavior (calendar-year reset on snapshot
  rollover) for plans without explicit targets.

## What's *not* in this PR

- No accumulator orchestration. Phase 1 emits events only.
- No bus fan-out. The Mongo stream is the system of record;
  Service Bus / Kafka decorators land alongside Phase 3.
- No member-anniversary fan-out. `EnrollmentAnniversary` plans emit
  one event per plan, not per member — Phase 3 will resolve members
  from coverage-service when applying the transition.
