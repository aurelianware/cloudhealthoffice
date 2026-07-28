# Episode 016 — The Million Went Through the Bus

Status: draft

Episode 016 follows the open sustained-throughput question from Part 15 into the platform's
actual asynchronous claims path. The validator gained a Service Bus-only mode, the local
Kubernetes bootstrap gained a least-privilege Azure Service Bus connection, and a full
1,000,000-claim run reached 155.89 claims/sec. The run recorded 122 claims outside the
validator's 180-second observation window; post-run verification found all 1,000,000 claims
terminal, 2,000,000 lifecycle events, no dead letters, no pod restarts, and no claims-service
error logs.

The episode also keeps a boundary the benchmark had historically blurred: MCC submits structured
JSON and does not test raw EDI parsing. A separate 100,000-claim X12 837P file was therefore sent
through the real raw-837 endpoint at importer concurrency 64. All 100,000 claims were accepted and
terminal, with 333.59 claims/sec for parse-and-submit and 199.42 claims/sec end-to-end through
Service Bus adjudication.

Primary sources:

- PR #1040 — bounded parallel raw-837 import and Azure Service Bus claims path
- PR #1041 — local least-privilege Service Bus bootstrap and in-memory fallback
- PR #1042 — Service Bus-only MCC validation and async benefit-plan identifier fix
- PR #1044 — validated raw importer concurrency 32 and batched authorization seeding
- `benchmark-results.txt` — exact Episode 16 measurements and limitations
- `pr-summary.txt` — implementation history
- `article.txt` — draft field note
- `podcast-prompt.txt` — two-host production prompt
- `visual-prompts.txt` — generation notes for the three article illustrations

Production notes:

- Keep the MCC JSON workload and raw 837 workload separate.
- Do not describe the 122 observation-window timeouts as lost claims; every claim later reached a
  terminal state.
- Do not describe the latest 1M run as zero-failure. The validator exited nonzero because those
  122 claims missed its 180-second observation window.
- Do not describe valid status-6 business denials in the raw 837 fixture as platform failures.
- These are local Docker Desktop Kubernetes measurements, not production-cloud capacity claims.
