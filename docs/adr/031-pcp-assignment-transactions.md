# ADR 031: Mongo transaction boundary for current-day PCP assignment

## Status

Proposed; scoped implementation for review. This is not managed-cloud or full
panel-policy acceptance.

## Decision

Coverage owns the roster and assignment history under ADRs 006/017. The production
`IPcpAssignmentService` is a transaction decorator over the validation core. It
uses a snapshot/primary/majority Mongo transaction encompassing coverage reads,
native roster count, prior-history closure, new-history insertion and medical
coverage updates. Tenant/member and tenant/NPI fence documents are written first
inside the transaction so competing replicas conflict even when inserting different
member documents. Driver transaction retries reread and revalidate; no lease or
process-local lock permits a stale writer to continue after expiry.

Fence identities hash a structured tenant/type/key tuple. Fence rows contain no
capacity counter to reconcile: occupancy is read from retained coverage records.
Initial fence creation is outside the transaction; failed assignments may leave
harmless fence metadata. Assignment/roster writes cannot partially commit through
this boundary. A request-scoped async-flow session joins the existing repositories.

Capacity is one distinct medical/legacy member per tenant/NPI **current roster**,
not coverage rows or a separately hosted HTTP count. A member already on the panel
does not need another slot. An unchanged same-day retry returns existing history
after validation; this is not a general request-id idempotency protocol.

The implementation requires Mongo multi-document transactions. Unsupported stores
fail unavailable; there is no nontransactional production fallback. All replicas
must deploy the decorator. Nonmedical-only coverage cannot receive a PCP.
Current-day UTC assignments are supported; historical and scheduled changes
explicitly fail with `PCP_EFFECTIVE_DATE_UNSUPPORTED` pending interval scheduling.
This new validation response/date restriction requires API/portal release review.

## Remaining boundaries

This serializes competing **assignment API** writers. Enrollment/import jobs,
direct Coverage writes, status reactivation and legacy binaries do not acquire
these fences. Their all-writer policy/reconciliation must be accepted before
claiming a platform-wide hard limit. Provider participation/limits are an external
validation snapshot, not a transaction with provider-service. Current global NPI
scope may be stricter than plan/location-specific business policies; acceptance
must select those scopes rather than silently invent them.

No future/retroactive interval reservations, provider-limit version fence,
general request-id deduplication, reconciliation scheduler, migration or deployed
250k-member performance/recovery acceptance is claimed. Retain the existing
reconciliation job and establish its scheduler as separate work.

See [the runbook](../operations/PCP-ATOMIC-CAPACITY.md).
