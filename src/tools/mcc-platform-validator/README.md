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

## Pended Edge Cases

The MCC edge-case corpus does include expected-pend scenarios, including COB
review, retro-eligibility coverage change, subrogation review, dual eligible,
and spend-down cases.

The synchronous benefit-plan adjudication response exposes `Success`, denial
reason fields, totals, and timings, but not a claim/workflow pend status.
Claims-service is the authoritative post-adjudication state source for this
validator: `GET /api/claims/{id}` returns `ClaimStatus.Pended`, and
`PendDetails.PendCode` provides the reason signal when available.

Because the async claims-service adjudication path resolves member demographics
from member-service, the validator seeds each distinct generated claim member by
default before it submits claims. This is required for pended validation to reach
the business edits: without a resolvable member DOB, the scrubbing stage can
reject the claim structurally before COB/subrogation/retro-eligibility pend logic
runs. Use `--member-url` to point at member-service and `--no-seed-members` only
when the tenant already has the matching synthetic members.

The validator therefore scores expected-pend scenarios in a post-adjudication
observation pass. The timed benchmark pass completes first; then the validator
polls claims-service for expected-pend claims only. `P95`, `P99`, stage timings,
and claims/sec are computed from submission, adjudication, and writeback timing
and exclude this polling window.
For expected-pend claims, the synchronous adjudication projection is not written
back to claims-service because that response cannot represent the pended state;
the async claims workflow remains the source of truth for those claims.

This observation pass is intentionally one-directional for benchmark cost: it
polls only claims whose answer-key disposition is `Pended`. It can prove that
expected-pend scenarios did or did not pend, but it does not detect the inverse
failure mode where a non-pend scenario unexpectedly lands in `ClaimStatus.Pended`
after the synchronous adjudication response said paid or denied.

Defaults:

- Pend observation is enabled by default. Pass `--no-pend-observation` to turn it
  off.
- `--pend-observation-timeout 45`
- `--pend-observation-interval-ms 1000`

The local-k8s scripts also default `PEND_OBSERVATION_ENABLED=true`. Set
`PEND_OBSERVATION_ENABLED=false` only when reproducing the pre-observation
validator behavior intentionally.

Expected-pend scenarios score as:

- `Matched` when claims-service observes `ClaimStatus.Pended`.
- `Mismatched` when claims-service observes a different terminal state such as
  approved/paid or denied.
- `ObservationTimeout` when the claim remains non-terminal until the configured
  timeout.
- `Unsupported` only when a future expected-pend subtype requires a signal the
  validator still cannot distinguish from persisted claim state.

## Service Bus Post-Window Reconciliation

`--servicebus-only` polls each submitted claim for a persisted terminal outcome
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

- Reconciliation is enabled whenever `--servicebus-only` is active.
- `--servicebus-reconciliation-timeout 300`
- The poll interval comes from `--pend-observation-interval-ms`.
- Pass `--no-servicebus-reconciliation` only to reproduce the pre-reconciliation
  behavior intentionally.

For `scripts/run-mcc-local-k8s.sh` and `scripts/sweep-mcc-local-k8s.sh`, set
`SERVICEBUS_ONLY=true` to select the asynchronous path. Reconciliation can be
controlled with `SERVICEBUS_RECONCILIATION_ENABLED` and
`SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS`.

## Pend Diagnostics (`--pend-diagnostics`)

Off by default. When given a path, the validator captures, for every
expected-pend claim and a bounded sample of NCCI/MUE-denied claims (see
`--pend-diagnostics-ncci-sample`, default 200):

- The answer-key expectation (scenario, expected disposition).
- The full synchronous adjudication response from
  `POST /api/v1/adjudication/adjudicate` (`Success`, every denial/error field
  present, totals) — captured verbatim, not reshaped.
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
`total.Stop()` — the same posture as `--pend-observation` — so it never
affects P95/P99/throughput. But a diagnostics-on run reads more claims than a
diagnostics-off run of the same size, so **do not report a diagnostics-on
run's timing as a throughput benchmark result.**
