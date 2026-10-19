# Authorization-number uniqueness (D-39 / D-37)

MongoDB enforces a unique compound index on `TenantId` + `AuthorizationNumber`.
The same number can exist in two tenants. A second document in the same tenant
cannot claim an existing number. The tenant/status index also supports the SLA
worker and tenant enumeration (D-37).

## Deployment preflight

Run the following read-only check against each authorization database before
rolling out. Use the configured authorization collection if it is not
`Authorizations`. Field names below match the Mongo authorization document.

```javascript
db.Authorizations.aggregate([
  { $group: { _id: { tenant: "$TenantId", number: "$AuthorizationNumber" },
              count: { $sum: 1 }, ids: { $push: "$_id" } } },
  { $match: { count: { $gt: 1 } } }
])
```

Resolve each duplicate with the owning plan's data steward. Preserve the
request hashes, histories, references from claims and attachments, and audit
trail. Do not automatically delete, renumber, or select a winner from existing
data. Missing/empty authorization numbers also require review before indexing.

The startup initializer builds the shared database indexes before serving
requests. If existing duplicates prevent the index build, startup fails; records
are not deleted or changed. Index errors must be resolved before rollout.
For database-per-tenant deployments, every create or whole-document update also
awaits initialization for its actual collection. Check every tenant database;
there is no implicit enumeration or migration of tenant databases.

## Concurrent requests and retries

The Mongo index decides which insert wins; the preliminary number lookup is
only an optimization. Duplicate-number writes become a domain conflict (409).
On the internal PAS recording endpoint, a losing insert reads the tenant's
winning record: matching request hashes return that stored record (200), while
a different hash returns 409. No caller-supplied outcome overwrites the winner.

Initialization is shared across request scopes per Mongo client/collection.
Failed initialization may be retried after correction. Request cancellation
does not cancel initialization used by other writers.

## Rollback

Keep the indexes on rollback. Older pods may report a duplicate-key error
instead of the newer idempotent response, but dropping the index would restore
the data-integrity defect. No Cosmos SDK migration is needed: that backend was
removed in ADR 017.
