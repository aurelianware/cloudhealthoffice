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
