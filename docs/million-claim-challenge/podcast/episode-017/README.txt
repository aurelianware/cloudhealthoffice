# Episode 017 — The Timeout Was Not the Outcome

Status: in progress

Episode 017 begins with the evidence limitation disclosed in Episode 016: 122 claims completed
after the validator's fixed observation window, but the run artifact could not re-score their
workflow outcomes or payments. Manual MongoDB inspection proved the claims were terminal; it did
not repair the validator's evidence.

This work adds automatic post-window reconciliation for Service Bus-only runs. A timed-out claim
now retains its submitted claim ID, is classified as an observation timeout, and is revisited
after timed throughput stops. A terminal result found in that pass restores the persisted
disposition, denial code, payer payment, and workflow score.

The first controlled-overload smoke test forced 936/1,000 claims outside a one-second observation
window. All 936 were reconciled from claims-service in 1.49 seconds. The final run summary reported
1,000/1,000 processed, 130/130 workflow matches, 20/20 exact payment comparisons, and zero
unresolved claims.

This is implementation and smoke-test evidence, not the final Episode 017 million-claim result.
The next benchmark should repeat the full Service Bus corpus with the normal three claims-service
replicas and preserve both in-window and post-window completion counts.

Primary sources:

- `benchmark-results.txt` — controlled live smoke measurements and limitations
- `pr-summary.txt` — implementation scope

Next scale exercise:

- Run the local Kubernetes services against Azure Cosmos DB's MongoDB-compatible endpoint.
- Treat it as a hybrid local-to-Azure persistence benchmark, not an all-local comparison.
- Capture RU provisioning, throttling, server-side retry delay, Mongo error codes, network
  latency, and per-service persistence configuration with the result.
