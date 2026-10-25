# PCP assignment transaction rollout

Follow-up: [coverage-writer controls](PCP-COVERAGE-WRITER-CONTROLS.md) extend
production fences to coverage lifecycle writes and check stored future coverage
reservations. The original #1547 boundaries and CI record below are historical;
scheduled/retroactive PCP assignment support and cloud acceptance remain open.

Baseline: merged #1546, main `f36a3e016a0447f6313930a35489dbfaf37f1ba5`.
Proposed architecture: [ADR 031](../adr/031-pcp-assignment-transactions.md).

## Executing boundary

The production DI registration resolves `TransactionalPcpAssignmentService`.
`PcpAssignmentService` remains the validation core; calling it directly is not a
capacity guarantee. Its old barrier characterization now explicitly describes the
unwrapped case. New replica-set tests target the production decorator with distinct
service/repository scopes, and test last-slot contention, same-member replay,
reassignment, tenant isolation, abort after history insertion and unsupported DBs.

The same Mongo session covers active coverage lookup, local current roster read,
assignment history and coverage replacements. Both member and destination-panel
fences are written before reading; driver retries reread state. Coverage revision
increments remain in force. Transaction failure does not leave partial history
or roster writes; fence metadata alone can remain. The core log says "prepared"
because it executes before commit. HTTP failure is not proof of rollback after an
unknown commit; unchanged same-day retries are recognized, while general durable
request-id semantics remain open.

`IPanelCounter` now reads the native Mongo roster in this session, with one member
per slot and medical or legacy-empty line codes only. The old HTTP panel-counter
class remains independently testable but is not the production assignment counter;
the capitation/coverage BaseUrl no longer controls assignment capacity.

## API changes

Current-day UTC assignment only. Non-default effective dates before/after today
return a 400 validation error with code `PCP_EFFECTIVE_DATE_UNSUPPORTED` before
the ordinary validation ladder. Historical/scheduled assignments require an
interval workflow rather than immediately overwriting today's roster. Coordinate
portal/client error handling and date selection before release.

Explicit dental/vision-only coverage no longer takes the legacy medical fallback.
Provider credentials, network, acceptance and age checks remain. Existing members
can keep their slot at a full panel. Source/participation conditions are revalidated
on transaction retry, so this is not an exemption from ordinary provider checks.

## Acceptance before deployment

1. Validate multi-document transactions, snapshot reads, majority writes, topology,
   server/driver versions, collection placement and retry/failover in the intended
   Azure/GCP Mongo deployment. Standalone Mongo refuses assignment with 503; it
   does not silently run the old path. No cloud compatibility is inferred from
   Mongo API connectivity alone.
2. Inventory every PCP-field writer, including 834 imports, admin updates and status
   activation; retire old binaries and route or reconcile those writers. This PR's
   fence protocol covers the assignment API only. An import that adds roster members
   outside it can still exceed a global panel policy. Do not close that launch gate.
3. Approve tenant/NPI/member counting and participation scope. Native count is
   across medical/legacy current coverage, not plan/location-scoped or future
   interval reservations. Reconcile duplicate members, obsolete denormalized PCPs
   and pre-existing over-limit panels before enabling a hard-limit representation.
4. Coordinate new date/error handling with clients, load-test peak assignment
   concurrency, measure retries/hot-panel contention and exercise ambiguous commits.
5. Run the replica-set suite and full service regressions. A dedicated CI workflow
   runs the race/abort cases; the ordinary service suite remains required. Schedule
   reconciliation separately. No production rollout or data repair is performed here.

## Local validation limit

SDK 8.0.426 was downloaded, but MSBuild fails before restore/build in this sandbox
while reading process metadata (`DebugUtils` / `ProcessName`). No successful local
build or test execution is claimed. New tests require a real single-node replica
set; mocks are not used to assert transaction correctness. CI must compile and
execute them. `git diff --check` and source/session-path inspection were performed.

## Focused CI validation

[Atomic PCP Capacity run 40171200454](https://github.com/aurelianware/cloudhealthoffice/actions/runs/40171200454)
on source commit `38af12bfc0a7984c06bdc7d7ad07dd20a6dba267` completed with a
successful build and **27 standard VSTest cases passed**. The selection includes
real replica-set last-slot contention, same-member retries, reassignment, tenant
isolation and rollback, standalone refusal, date restrictions, session/count
policy and the unwrapped service regressions. This supersedes the lack of local
execution for that focused scope; broader CI, all-writer and cloud acceptance
remain separate. No test body was substituted with a mock transaction.
