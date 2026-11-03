# Retro rate reprocessing

When a fee schedule changes after claims were paid on it (a state publishes a
retroactive Medicaid rate, a contract amendment backdates a rate, a schedule
was loaded with an error), the paid claims need to be found, re-priced and
corrected with an adjustment. This page describes what CHO records today and
the steps still to come.

## Today: what is recorded

### Fee schedule change history

Every create and replace through the fee schedule store (the CSV, Medicare
PFS, Medi-Cal and TMHP importers, the library deploy, the plan-default PUT)
writes two things in benefit-plan-service's database:

- **`FeeScheduleChanges`**: one record per stored revision (`id` is
  `{scheduleId}@{revision}`), with the kind (`Created` or `Replaced`), the
  revision before and after, the effective range (and the previous range when
  it moved), the plan it is the default for, supplemental plans, and what moved:
  - `changedCodes`: procedure codes (or `rev:{code}` for revenue-code-only
    lines) whose line was added, removed or changed;
  - counts of lines added, removed, raised, lowered and otherwise changed
    (RVUs, rate type, unit caps, indicators);
  - `allCodes: true` when a schedule-level value changed (conversion factor,
    GPCI, percent of Medicare, Medicare reference schedule, per diem, DRG base
    rate, effective or term date, plan assignment), with the values listed in
    `scheduleLevelChanges`, or when more than 5,000 codes changed.
- **`FeeScheduleRevisions`**: the whole schedule, lines included, as it stood
  before each replace (`id` is `{scheduleId}@{revision}`). Creates do not need
  one: a new effective date makes a new schedule id, and the old schedule is
  untouched.

Reading them (permission `benefits:read`):

```
GET /api/v1/fee-schedules/changes?scheduleId=&since=&limit=
GET /api/v1/fee-schedules/revision?id={scheduleId}&revision=3&code=992&skip=0&take=100
```

The revision endpoint returns 404 for a revision that was never replaced: the
live schedule still holds it.

The history is written after the schedule. If the history write fails, the
schedule write stands and the failure is logged as an error ("written but its
change history was not recorded"), because failing the call would hand the
caller a revision that is no longer current.

Recommended indexes:

```
db.FeeScheduleChanges.createIndex({ tenantId: 1, feeScheduleId: 1, changedAt: -1 })
db.FeeScheduleChanges.createIndex({ tenantId: 1, changedAt: -1 })
db.FeeScheduleRevisions.createIndex({ tenantId: 1, feeScheduleId: 1, revision: -1 })
```

### Pricing provenance on claims

The pricing stage records how every line was priced on
`adjudicationResult.linePricing`:

| Field | Meaning |
|---|---|
| `lineNumber`, `procedureCode`, `serviceDate` | The line and the date that picked the rates in force |
| `feeScheduleId`, `feeScheduleName` | The schedule that priced it, or the Medicaid rate table row for a state DRG / per diem stay; null for UCR, capitation, percent of billed |
| `feeScheduleRevision` | The schedule's revision when the line was priced; null when the id is not a fee schedule |
| `feeScheduleType`, `rateSource` | As the pricing engine reported them |
| `baseAmount`, `allowedAmount`, `lesserOfBilledApplied` | The amounts pricing returned |

A line whose `feeScheduleId` matches a `Replaced` change and whose
`feeScheduleRevision` is below that change's `newRevision` was priced on the old
rates. Claims priced before this was recorded have an empty `linePricing`; for
them, the plan (`defaultForPlanId`) and date of service are the fallback match.

## Dry-run impact report

`POST /api/v1/claims/retro-rate/impact-runs` (claims-service, permission
`claims:adjust`) measures one change against the claims already paid:

```json
{ "feeScheduleId": "tenant-1:TMHP adult:20260901", "revision": 3, "limit": 2000 }
```

`revision` is the change's `newRevision`; leave it out for the schedule's
latest change. The run:

1. Reads the change from benefit-plan-service (`GET /api/v1/fee-schedules/changes`).
2. Reads candidate claims: current-version, unadjusted Approved / PartiallyPaid /
   Paid claims whose dates of service overlap the change's window (the earlier
   of the old and new effective dates to the later term date) and that either
   name the schedule in `linePricing` or `supplementalPayments`, or are on the
   plan the schedule is the default (or supplemental) for. At most `limit`
   (capped by `Adjudication:RetroRate:MaxClaimsPerRun`, default 2,000);
   `truncated` says when there were more.
3. Keeps the claims the change actually touches (`match`):
   - `Provenance`: a line was priced on this schedule at an older revision and
     bills a changed code (any code when the change has `allCodes`);
   - `Supplemental`: a supplemental payment on a changed code came from it;
   - `PlanFallback`: the claim has no pricing provenance (priced before it was
     recorded), is on the schedule's plan and bills a changed code. Broader:
     a provider contract may have priced it instead;
   - `NewSchedule`: the change created a schedule effective back over claims
     on its plan that were priced on something else.
4. Re-prices each one through the shadow pipeline (the same read-only stages
   shadow adjudication uses: no persistence, no event, no accumulator write)
   and compares it with what was paid, line by line.
5. Classifies each claim by `direction`: `Underpaid` (an additional payment is
   owed), `Overpaid` (a recoupment, only where the plan opts in), `Unchanged`
   (within `Adjudication:RetroRate:Tolerance`, default $0.01), or `Undecided`
   (the re-price pended, denied or rejected; its pend code says why).

The run returns totals: claims affected, counts by direction and match, total
underpaid, total overpaid, net, member liability change, the 25 providers with
the largest net, failures, and notes for the reader. Per-claim results:

```
GET /api/v1/claims/retro-rate/impact-runs
GET /api/v1/claims/retro-rate/impact-runs/{runId}
GET /api/v1/claims/retro-rate/impact-runs/{runId}/results?direction=Underpaid&limit=200
GET /api/v1/claims/retro-rate/impact-runs/{runId}/claims.csv
```

Limits:

- Member cost share is recalculated against today's accumulators, which
  already include the paid claim, so member liability changes on deductible or
  out-of-pocket claims are estimates. Medicaid claims with no cost share are
  exact.
- A run is synchronous and capped; very large changes need several runs or the
  async runner that comes with the reprocessing run.
- Nothing is adjusted. That is the next step.

## Coming next

1. **Reprocessing run.** Approved by an operator; creates adjustments in bulk
   through the existing adjustments API and reversal runs (CLP02=22 reversal
   plus corrected claim on the 835). Underpayments are paid by default.
   Recoupments of overpayments are opt-in per plan or contract.
2. **Portal page** for change history, impact and approval.
3. **State presets** (Texas, California) and Medicaid encounter replacements.
