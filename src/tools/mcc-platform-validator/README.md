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

The validator therefore scores expected-pend scenarios in a post-adjudication
observation pass. The timed benchmark pass completes first; then the validator
polls claims-service for expected-pend claims only. `P95`, `P99`, stage timings,
and claims/sec are computed from submission, adjudication, and writeback timing
and exclude this polling window.

Defaults:

- `--pend-observation-timeout 45`
- `--pend-observation-interval-ms 1000`

Use `--no-pend-observation` to disable the post-adjudication observation pass.

Expected-pend scenarios score as:

- `Matched` when claims-service observes `ClaimStatus.Pended`.
- `Mismatched` when claims-service observes a different terminal state such as
  approved/paid or denied.
- `ObservationTimeout` when the claim remains non-terminal until the configured
  timeout.
- `Unsupported` only when a future expected-pend subtype requires a signal the
  validator still cannot distinguish from persisted claim state.
