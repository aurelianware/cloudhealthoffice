# MCC Pend Validation Note

The MCC edge-case corpus includes expected-pend scenarios such as COB review,
retro-eligibility coverage change, subrogation review, dual eligible, and
spend-down cases.

For the next local-k8s run, the platform validator should report these cases as
first-class `Pended` results when claims-service persists `ClaimStatus.Pended`.
The validator observes that state after the timed adjudication pass, so
published throughput, P95, and P99 should continue to be described as excluding
pend-observation polling.

If expected-pend claims do not reach a pended or terminal claim status within
the configured window, the validator reports `ObservationTimeout` separately
from `Mismatched` and `Unsupported`.
