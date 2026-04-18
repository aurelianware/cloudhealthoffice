# Family Relationships Backfill Runbook

**Change:** graduates `Member.SubscriberMemberId` (a single-valued FK) to a
symmetric `FamilyRelationship` edge table. Legacy field is marked `[Obsolete]`
and derived from the graph on read.

---

## 1. Pre-flight

Run these read-only counts in the target environment before starting:

```bash
# Count members that have a subscriber FK (candidates for backfill)
mongosh "$MONGO_URI" --eval '
  db.Members.countDocuments({
    tenantId: "<TENANT_ID>",
    subscriberMemberId: { $exists: true, $ne: null }
  })
'

# Count relationships already in the graph (should be 0 pre-migration)
mongosh "$MONGO_URI" --eval '
  db.FamilyRelationships.countDocuments({ tenantId: "<TENANT_ID>" })
'
```

Verify the deployment requirement for MongoDB transactions (used by the symmetric-pair
writer): the Mongo cluster must be a **replica set**. Standalone Mongo will reject
the transactions with a clear error rather than silently breaking the symmetric
invariant. On Cosmos DB, symmetric pairs use `TransactionalBatch` within the
`/tenantId` partition — no additional configuration required.

## 2. Deploy

1. Deploy member-service with this change set. The service will:
   - Accept writes to the new `FamilyRelationshipsController` endpoints.
   - Auto-create a relationship pair whenever a dependent is created via
     `POST /api/v1/members` with `SubscriberMemberId` set (the shim).
   - Continue writing `Member.SubscriberMemberId` on legacy write paths.
   - Start deriving `Member.SubscriberMemberId` from the graph on reads where the
     graph is populated; falls back to the stored legacy value otherwise.

2. Verify the startup log shows:
   ```
   FamilyRelationship indexes ensured.
   ```

## 3. Backfill

Run per tenant. The tool is resumable — if the process is killed it picks up
where it left off on the next run.

```bash
# Dry-run first (no writes) to see the edge list
dotnet run --project tools/FamilyRelationshipsBackfill -- \
  --tenant <TENANT_ID> --dry-run

# For real
dotnet run --project tools/FamilyRelationshipsBackfill -- \
  --tenant <TENANT_ID> --batch 500
```

Configuration is read from `tools/FamilyRelationshipsBackfill/appsettings.json`
and/or env vars:

- `MongoDb__ConnectionString` (required)
- `MongoDb__DatabaseName` (default `CloudHealthOffice`)

Progress is written to a `BackfillJobs` collection, one row per tenant. To
re-run from scratch for a tenant:

```bash
dotnet run --project tools/FamilyRelationshipsBackfill -- \
  --tenant <TENANT_ID> --reset
```

### Idempotency

The tool calls `FamilyRelationshipService.CreateAsync`, which rejects duplicate
active pairs. Re-running the backfill is safe:

- Missing pairs get created.
- Pairs that already exist (from shim auto-creation on new writes, or a prior
  backfill run) are counted as `alreadyLinked` and skipped.

## 4. Verification

After the run completes for a tenant, verify the invariants:

```javascript
// Expected: (count of non-subscriber members with SubscriberMemberId) * 2
//   (one forward + one inverse row per legacy edge)
db.FamilyRelationships.countDocuments({ tenantId: "<TENANT_ID>" });

// Every pair must have exactly 2 rows (symmetric-graph invariant)
db.FamilyRelationships.aggregate([
  { $match: { tenantId: "<TENANT_ID>" } },
  { $group: { _id: "$pairId", rows: { $sum: 1 } } },
  { $match: { rows: { $ne: 2 } } },
]); // expected: empty cursor

// Each pair must be same-tenant (Phase 1 constraint)
db.FamilyRelationships.aggregate([
  { $group: { _id: "$pairId", tenants: { $addToSet: "$tenantId" } } },
  { $match: { tenants: { $not: { $size: 1 } } } },
]); // expected: empty cursor
```

## 5. Rollback

Safe rollback: the `Members` collection is untouched by the backfill — only the
new `FamilyRelationships` collection is written.

```javascript
db.FamilyRelationships.deleteMany({ tenantId: "<TENANT_ID>" });
db.BackfillJobs.deleteMany({ tenantId: "<TENANT_ID>" });
```

The member-service will continue to honor the legacy `SubscriberMemberId` field
as the source of truth after rollback; graph-derivation degrades gracefully to
returning `null` and the stored field value is used.

## 6. Known phase-1 constraints

- **Same-tenant only.** Cross-tenant family relationships (dual-coverage spouses
  on different employer plans at the same payer) are rejected. These are
  deferred to a later phase with a dedicated architecture doc.
- **Soft-delete only.** `DELETE /{relId}` soft-deletes within 24h of creation
  for data-entry errors. Normal wind-down is `POST /{relId}/end`. Hard delete
  is not exposed — claims, authorizations, and QMCSO records depend on historical
  edges remaining retrievable for audit.
- **Legacy FK still written.** The 834 enrollment path still writes
  `Member.SubscriberMemberId`; the shim mirrors it onto the graph. Once the
  enrollment-import-service migrates to the new API (separate PR), the legacy
  FK can be dropped.
