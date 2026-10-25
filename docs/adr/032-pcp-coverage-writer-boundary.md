# ADR 032: PCP capacity at the coverage write boundary

## Status

Proposed; implementation for review. Extends ADR 031 without approving a plan's
assignment policy, temporal PCP scheduling or a managed-cloud deployment.

## Problem

An assignment API transaction alone cannot stop a separate enrollment
reinstatement or coverage status/date edit admitting another PCP member. A
current-count-only check can also overlook already recorded future coverage.

## Decision

Keep domain-owned Mongo persistence (ADRs 006/017). Production explicitly creates
`CoverageRepositoryMongo` with `PcpRosterWriteGuard`; every create, update, status
transition and deletion uses `PcpWriteTransaction`. The member and destination
NPI fences share ADR 031's identifiers with assignment writers. Repository reads
and writes join the same snapshot/primary/majority transaction. A nested write
must match the active tenant/member/NPI scope. Stale revisions cannot reactivate
or overwrite a newer PCP assignment. Unsupported transaction stores fail closed.
This extends the topology requirement to ordinary coverage writes as well.

PCP NPI and assignment-date edits outside the assignment API are rejected, so
imports cannot silently bypass history. An initial coverage with a PCP already
stamped is rejected; create coverage and use the assignment API for a supported
effective date. Direct database writes, raw repository constructors and old
binaries are outside the supported production boundary and require access and
rollout controls. No claim is made that arbitrary database credentials are fenced.

Admission is checked over the stored destination-NPI coverage spans. Health and
legacy-empty lines count distinct members; Pending future spans reserve capacity,
COBRA date-of-service spans count, Suspended/unknown and malformed terminated rows
do not. Start is clamped to the PCP assignment date; termination is inclusive.
All coverage and participation boundaries are examined. Only newly occupied
member-days (or a plan/LOB change) require provider admission, so premium-only
edits, duplicate coverage, termination and deletion can proceed on full panels
without an available provider directory. Source unavailability or a capacity/
participation failure aborts the complete write and surfaces an exception.

Scope remains global tenant/NPI. Provider status, credentialing and participation
are external snapshots; a provider policy/version change is not atomically
serialized with coverage. Existing age/clinical/member-choice rules remain the
assignment API's responsibility. This guard is not a state-specific policy pack.

## Consequences and remaining work

Future **coverage reservations** are protected, not scheduled PCP changes. Keep
`PCP_EFFECTIVE_DATE_UNSUPPORTED` for future/retroactive PCP changes until a separate
assignment-interval workflow and all downstream readers are implemented. This
change does not rewrite historical assignments, move a current PCP early, send
notices, order ID cards, implement capitation adjustments or remove existing
inconsistency. Reconciliation scheduling, provider-limit versioning, plan/location
scope and 250k-member peak-load/cloud acceptance remain open.

A finite provider participation cannot admit an open-ended new reservation past
its end without another matching participation. This conservative refusal and
409/503 responses need enrollment/operations acceptance; do not work around them
by dropping enrollment or silently clearing PCP fields. Establish approved
exception resolution and data cleanup before deployment.

See [rollout and tests](../operations/PCP-COVERAGE-WRITER-CONTROLS.md).
