# PCP coverage-writer controls

Baseline: merged #1547, main `1b9319c17bf1ab376a0155d0c7dbf6100090ee77`.
Decision: proposed [ADR 032](../adr/032-pcp-coverage-writer-boundary.md).

## Supported boundary

Production's explicit repository factory requires `PcpRosterWriteGuard`. The
assignment API, coverage create/update/delete, member termination/reinstatement
and status sweep share member/destination-panel transaction fences. 834 imports
use those HTTP coverage endpoints; no new cross-service database access is added.
The FHIR coverage repository is read-only. Raw fixtures/direct DB writers are not
protected and must not be mistaken for a supported enrollment interface.

The guard compares old and proposed roster membership across coverage spans,
including future Pending coverage. Multiple coverage rows for one medical/legacy
member consume one slot. An inclusive termination on a day overlaps an admission
on that day; admission the following day can reuse the slot. The PCP assignment
date clamps the start. Existing future reservations can therefore reject a new
open-ended current assignment even when today's panel has space.

Provider participation changes inside a proposed interval are rechecked. Missing
provider/participation or an exceeded limit aborts the transaction; unchanged
membership, reductions and duplicate member coverage remain possible. A plan/LOB
change is revalidated even if the global member count does not increase. This is
a global tenant/NPI control, not approved California location/plan panel policy.

## Integration behavior

- Ordinary coverage writers return 409 for `PANEL_FULL`, invalid roster admission,
  `PCP_ASSIGNMENT_API_REQUIRED` or `COVERAGE_CONCURRENCY_CONFLICT`. Reload for a
  concurrency conflict; capacity/policy failures need an enrollment exception.
- Transaction/provider infrastructure failure returns 503. Do not treat this as
  success, drop the enrollment, or retry by bypassing the supported repository.
- The PCP assignment API retains its structured 400 validation response. If an
  interval check fails after history preparation, the entire transaction aborts.
- PCP NPI/assignment-date edits and pre-stamped new PCP coverage must use the
  assignment API. Current-day UTC only remains the supported PCP change date.
- Production coverage writes now require a transaction-capable Mongo topology,
  even for ordinary coverage creation. Coordinate this broader requirement with
  release owners and validate actual managed Azure/GCP behavior before rollout.

Finite participation terms can refuse open-ended admission beyond the term. This
is intentional conservative behavior pending approved renewal/exception policy.
No automatic clearing of a PCP, member termination or administrative overbooking
is implemented. Provider policy changes remain external snapshots.

## Test and validation contract

Focused CI selects the existing assignment races plus `PcpRosterWriterTests`,
`PcpCoverageIntervalsTests` and `PcpRosterWriteMiddlewareTests`. The new replica-set
cases exercise reinstatement versus assignment, two independent reactivations,
future-reservation rejection with history rollback, adjacent/overlapping coverage,
sweep revision conflicts, direct-edit refusal, full-panel maintenance/deletion,
provider failure/expiry, tenant isolation, duplicate members and standalone refusal.
Pure interval cases cover later peaks, inclusive ends, assignment-date clamps and
medical/legacy status semantics. HTTP middleware cases verify explicit 409/503.

Local SDK 8.0.426 starts, but CLI fails on process start-time metadata, and direct
MSBuild fails on sandbox ProcessName metadata before restore/build. No successful
local executable test run is claimed. CI results must be recorded separately with
source commit and selected scope. Historical #1547's 27 cases are not new evidence.

Before deployment inventory/retire old writers and binaries; migrate inconsistent
or unversioned roster data with controlled reconciliation; approve plan/provider
scope and member exceptions; test peak contention, long panels, driver retries,
managed topology/failover and recoverability. The interval scan reads the provider's
roster and is not yet qualified for the plan's 250k-member workload.

## Focused CI result

[Atomic PCP Capacity run 40217632511](https://github.com/aurelianware/cloudhealthoffice/actions/runs/40217632511)
on source commit `e36185df49fce9d8106311cebb9910d2b30cff06` built successfully
with zero warnings/errors and passed **48 standard VSTest cases**, zero failed.
This includes the real replica-set admission races, future-reservation/history
rollback, standalone refusal and interval/error-mapping cases described above.
The initial test-build namespace/async-analyzer issues were corrected before this
successful run. Later documentation changes do not reuse this as a new execution.
Broader CI, legacy-writer retirement, exception operations and managed-cloud/
250k-member workload acceptance remain separate.

## Other PCP-change acceptance gaps

Member-service publishes `PcpChanged` after a successful proxied assignment;
coverage-service itself does not provide a durable event/outbox for direct calls.
ID-card service accepts explicit orders and exposes PCP template bindings, but its
native PDF renderer currently omits those fields. No
consumer connects PCP changes to replacement-card orders. Automatic reissue,
assignment-based deduplication, effective-date-aware digital content and print/mail
fulfillment reconciliation remain a separate acceptance requirement. Do not infer
physical mailing from generation of a PDF or an `Issued` order status.

Scheduled/retroactive PCP assignment intervals, downstream historical/current
readers, capitation effects, reconciliation scheduling and full cloud/plan launch
acceptance remain open. This change only protects supported coverage writers and
existing coverage reservations; it does not close CA-08 as a whole.
