# Encounter batch integrity and acknowledgment boundaries

The legacy `BuildFmmisSubmissionBatchAsync` produces **Florida-oriented JSON**.
It is not an accepted Florida interchange or a California PACES 837 generator.
The Kafka producer gate admits only Florida Medicaid adjudication events;
California events currently do not create tracking records through that path.
California EDI requires a separate accepted handoff and reconciliation workflow.
See [California qualification](../implementation/CALIFORNIA-MEDICAID-QUALIFICATION.md).

The batch builder now refuses an empty tenant/batch, mixed tenants, non-Florida
records, nonbatchable lifecycle states, missing identifiers and duplicate
submission/claim identities before fetching claims. A failed, malformed or
wrong-claim read aborts before any batch-status write. Previously the builder
skipped failed reads and still marked all supplied records as batched.
Upstream response bodies are no longer logged. Claims-service remains the
source of truth; there is no new database or FHIR projection (ADRs 006/008/028).

A 999 acknowledges syntax, not final adjudicated encounter acceptance.
New positive 999 outcomes use `SyntaxAccepted` (7) or
`SyntaxAcceptedWithErrors` (8), appended to preserve existing numeric enum
values. The summary exposes both counts. `Accepted` (3) and `PartialAccept`
(4) are retained for compatibility, but the 999 processor no longer writes
them. A final state-response mechanism is still required.

## Rollout and reconciliation

- Update dashboards/consumers to recognize the new syntax-only statuses.
- Inventory historical Accepted/PartialAccept records and reconcile them to
  actual partner/state response evidence. They were previously populated by
  999 processing; **do not treat those historical counts as final acceptance**.
- Do not mechanically rewrite history without the underlying acknowledgment
  evidence. No automatic migration is included.
- Old binaries do not understand new statuses. Before rollback, stop intake
  and reconcile/export these rows; do not silently map them to final Accepted.

Remaining integrity work is explicit: batch selection is not a distributed
reservation; the status write and blob staging are not atomic; the worker
still needs durable staging/retry recovery; a parsed AK9 is not a fully
correlated group/transaction acknowledgment; record-level 277CA/PACES response,
correction/void/replacement and end-to-end completeness need separate design
and partner acceptance. This change does not close those findings or implement
California submission.
