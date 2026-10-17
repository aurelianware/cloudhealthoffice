# MCC Platform Validator

The MCC platform validator submits generated Million Claim Challenge claims to a
local Cloud Health Office stack and publishes a run summary with throughput,
latency, platform failures, and workflow-check scoring.

## Workflow Scoring

The validator currently scores:

- `Matched` when the observed outcome and expected business denial code match the
  deterministic MCC answer key.
- `Mismatched` when the claim is scoreable but the observed outcome or business
  denial code differs.
- `Unspecified` when a generated claim has no validator answer-key entry.
- `Unsupported` when the MCC answer key expects a disposition the validator
  cannot observe yet.
- `ObservationTimeout` when an expected-pend claim does not reach a terminal or
  pended claim status within the bounded observation window.

## Scoring Path

The validator submits each claim to claims-service and scores only the outcome
the claims-service adjudication pipeline persisted (the single adjudicator). It
never adjudicates on the side and never writes an adjudication back. After
submission it polls `GET /api/claims/{id}` until the claim reaches a terminal or
pended status, or until `--pend-observation-timeout` expires (poll interval
`--pend-observation-interval-ms`).

`--servicebus-only` and `--no-pend-observation` are still accepted so older
scripts keep working, but they have no effect: this is the only mode.
The former `--synchronous-diagnostic` mode (benefit-plan's dry-run endpoint)
was removed; passing it is now an error.

Defaults:

- `--pend-observation-timeout 45`
- `--pend-observation-interval-ms 1000`

## Pended Edge Cases

The MCC edge-case corpus does include expected-pend scenarios, including COB
review, retro-eligibility coverage change, subrogation review, dual eligible,
and spend-down cases. Claims-service is the authoritative post-adjudication
state source: `GET /api/claims/{id}` returns `ClaimStatus.Pended`, and
`PendDetails.PendCode` provides the reason signal when available.

Because the async claims-service adjudication path resolves member demographics
from member-service, the validator seeds each distinct generated claim member by
default before it submits claims. This is required for pended validation to reach
the business edits: without a resolvable member DOB, the scrubbing stage can
reject the claim structurally before COB/subrogation/retro-eligibility pend logic
runs. Use `--member-url` to point at member-service and `--no-seed-members` only
when the tenant already has the matching synthetic members.

The claims pipeline prices every line through the fee-schedule engine before
benefits apply, and MCC providers have no contract there. After creating its
validation plan the validator therefore seeds the plan's default fee schedule
(`PUT {benefit-url}/api/v1/plans/{planId}/default-fee-schedule`, permission
`settings:manage`): flat CPT/CDT rates plus a blank-procedure flat rate for every
UB-04 revenue code the institutional generator emits
(`SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule`). Without it, lines
fall back to billed charges and claims pend `NOCONTRACT`. The PUT replaces the
plan's one default schedule, so reruns are idempotent. Plain institutional claims
remain unscored: their allowed amounts follow the seeded rates, but paid amounts
depend on the validation plan's cost sharing, which the generator does not model.

Expected-pend scenarios score as:

- `Matched` when claims-service observes `ClaimStatus.Pended`.
- `Mismatched` when claims-service observes a different terminal state such as
  approved/paid or denied.
- `ObservationTimeout` when the claim remains non-terminal until the configured
  timeout (and is still unresolved after post-window reconciliation).
- `Unsupported` only when a future expected-pend subtype requires a signal the
  validator still cannot distinguish from persisted claim state.

## Service Bus Post-Window Reconciliation

The validator polls each submitted claim for a persisted terminal outcome
inside the configured observation window. If that window expires, the validator
preserves the submitted claim ID and marks the result as an observation timeout
instead of discarding the ID as a generic platform failure.

After the timed benchmark stops, the validator revisits those claims in bounded
parallel. A terminal result found during this pass is fully re-scored from
persisted state, including:

- workflow outcome and business-denial code;
- payer payment and the payment-accuracy gate;
- paid, pended, and business-denial summary counts.

The run summary reports the initial Service Bus observation timeouts, late
completions reconciled after the window, and claims still unresolved after
reconciliation as separate values. Timed throughput, P95, and P99 remain based
on the original observation window; reconciliation is recorded as a separate
post-window lifecycle phase.

Defaults:

- Reconciliation is enabled by default.
- `--servicebus-reconciliation-timeout 300`
- The poll interval comes from `--pend-observation-interval-ms`.
- Pass `--no-servicebus-reconciliation` only to reproduce the pre-reconciliation
  behavior intentionally.

For `scripts/run-mcc-local-k8s.sh` and `scripts/sweep-mcc-local-k8s.sh`,
reconciliation can be controlled with `SERVICEBUS_RECONCILIATION_ENABLED` and
`SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS`, and the observation window with
`PEND_OBSERVATION_TIMEOUT_SECONDS` and `PEND_OBSERVATION_INTERVAL_MS`.

The run summary's per-step adjudication timings (`adjudicationStepTimings`,
per-claim `adjudicationStepMilliseconds`) stay in the published contract but are
empty: only the removed dry-run endpoint reported them.

## Pend Diagnostics (`--pend-diagnostics`)

Off by default. When given a path, the validator captures, for every
expected-pend claim and a bounded sample of claims whose observed denial code is
`NCCI_MUE_EDIT_FAILURE` (see `--pend-diagnostics-ncci-sample`, default 200):

- The answer-key expectation (scenario, expected disposition).
- The observed pipeline outcome (paid or not, normalized business denial code).
- The post-adjudication claim state read back from claims-service
  (`ClaimStatus`, `PendDetails`, persisted denial code/reason, per-line
  adjustment reasons when the claim model exposes them).
- The validator's own scoring result (`Matched` / `Mismatched` /
  `ObservationTimeout` / `Unsupported`).

This produces two things:

1. A JSON report at the given path — one row per diagnosed claim. Diffable,
   and easy to aggregate or load into a notebook.
2. An aggregate table printed to the run summary — per scenario: expected
   pend count, observed Paid, observed Denied (grouped by denial code),
   observed Pended, and observation timeouts. This table is meant to be
   pasted directly into an ADR or episode packet.

**This is diagnostic instrumentation only.** It changes no dispositions, no
engine logic, no claim state handling. It performs one additional
`GET /api/claims/{id}` read per diagnosed claim, and that read happens after
`total.Stop()` — the same posture as Service Bus reconciliation — so it never
affects P95/P99/throughput. But a diagnostics-on run reads more claims than a
diagnostics-off run of the same size, so **do not report a diagnostics-on
run's timing as a throughput benchmark result.**
