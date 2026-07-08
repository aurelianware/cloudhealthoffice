# MCC Pend Validation Note

The MCC edge-case corpus includes expected-pend scenarios such as COB review,
retro-eligibility coverage change, subrogation review, dual eligible, and
spend-down cases.

For the next local-k8s run, the platform validator should report these cases as
first-class `Pended` results when claims-service persists `ClaimStatus.Pended`.
The validator observes that state after the timed adjudication pass, so
published throughput, P95, and P99 should continue to be described as excluding
pend-observation polling.

Run instructions should set pend observation explicitly, even though the scripts
default it on:

```bash
PEND_OBSERVATION_ENABLED=true \
PEND_OBSERVATION_TIMEOUT_SECONDS=45 \
PEND_OBSERVATION_INTERVAL_MS=1000 \
SEED_MEMBERS=true \
./scripts/run-mcc-local-k8s.sh
```

Keep member seeding enabled unless the tenant already has the exact synthetic
members generated for the run. The async claims-service path resolves DOB and
gender from member-service; if those members are missing, structural scrubbing
can reject the claim before pend-producing edits run.

If expected-pend claims do not reach a pended or terminal claim status within
the configured window, the validator reports `ObservationTimeout` separately
from `Mismatched` and `Unsupported`.

Current limitation: the validator polls only scenarios whose answer key expects
`Pended`. That keeps benchmark overhead bounded, but it does not catch false
pends for scenarios expected to pay or deny.
