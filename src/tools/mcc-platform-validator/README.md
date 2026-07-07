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

## Pended Edge Cases

The MCC edge-case corpus does include expected-pend scenarios, including COB
review, retro-eligibility coverage change, subrogation review, dual eligible,
and spend-down cases.

The validator does not currently score those as a first-class `Pended` outcome.
It calls the benefit-plan adjudication endpoint directly, whose response exposes
`Success`, denial reason fields, totals, and timings, but not a claim/workflow
pend status. Claims-service and the Argo adjudication workflow can represent
`ClaimStatus.Pended` and 277 `P:16:85`; this validator does not yet run through
that workflow path or read claim status back after adjudication.

Until that observable signal is wired into the validator, expected-pend scenarios
are reported as `Unsupported` rather than silently counted as matched,
mismatched, or unspecified.

## TODO

- Add a validator path that observes real pend state, either by running the full
  claim workflow and reading `ClaimStatus.Pended` / 277 status, or by extending
  the adjudication response contract with a first-class pend result.
