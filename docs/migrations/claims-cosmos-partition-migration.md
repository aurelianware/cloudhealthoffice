# Operator runbook — Claims Cosmos partition migration to `/tenantId` (5.1b)

Status: capability 5.1b shipped. This runbook covers the operator
sequence for moving production claim documents from the legacy
`Claims` Cosmos container (`/memberId` Bicep declaration, `/Id`
runtime partition) to the canonical `ClaimsV2` container
(`/tenantId` partition).

Architecture context: see
[`docs/architecture/claim-versioning.md`](../architecture/claim-versioning.md)
section "Cosmos partition key — `/tenantId` (5.1b)" for the why.
This runbook covers the how.

## Why migrate

- Eliminates cross-partition fan-out on the versioning surface
  (`GetLatestVersionAsync`, `GetVersionAsync`, `ListVersionsAsync`,
  `UpdateAdjudicationProjectionAsync`, `Mark*ProjectionAsync`).
  Meaningful Cosmos RU savings at scale.
- Pattern parity with Provider, BenefitPlan, and AiExaminationAudit
  (all already partition by `/tenantId`).
- Tenant isolation enforced at the storage layer — defense in depth
  beyond filter-clause isolation.

## Pre-flight checklist

Before kicking off the migration:

- [ ] **Snapshot / backup the legacy `Claims` container.** Cosmos
  point-in-time-restore window must cover the full migration window
  plus the 30-day rollback retention window.
- [ ] **Confirm peak/off-peak traffic windows for claims-service.**
  Plan the cutover (step 5 below) for a 2–5 minute low-traffic
  pause window. Claims-service writes are predominantly batch-driven
  (837 ingestion + adjudication pipeline running off Service Bus
  subscription); a brief pause window is operationally feasible.
- [ ] **Identify the on-call DRI for the cutover window.**
- [ ] **Verify metrics dashboards are live** for
  `cho.claims.cosmos_migration.runs.total`,
  `cho.claims.cosmos_migration.documents.total`, and
  `cho.claims.cosmos_migration.duration` (ChoMetrics).
- [ ] **Confirm log aggregation captures claims-service** at the
  Information level — the migration emits structured start /
  complete log lines tagged with the run id.

## Step 1 — Bicep deploy (creates `ClaimsV2`)

The 5.1b PR added the `claimsV2Container` resource alongside the
existing `claimsContainer`. Deploy the Bicep module:

```
az deployment group create \
    --resource-group <rg> \
    --template-file infrastructure/azure/modules/cosmos-db.bicep \
    --parameters @<env>.parameters.json
```

Verify the new container exists:

```
az cosmosdb sql container show \
    --account-name <cosmos-account> \
    --database-name ClaimsDB \
    --name ClaimsV2 \
    --resource-group <rg>
```

The Bicep change is purely additive — service traffic continues
against the legacy `Claims` container until cutover. Bicep failure
at this step does not affect production reads/writes.

## Step 2 — Deploy claims-service with migration capability

Standard claims-service deployment. After deploy, confirm:

```
GET /api/v1/admin/claims/cosmos-migration/status
```

Returns 503 — migration is gated by default. Returning 503 (not
404) confirms the route is registered and intentionally gated.

## Step 3 — Enable the migration

Set `ClaimsCosmosMigration:MigrationsEnabled=true` via the standard
configuration channel (Azure App Configuration / Key Vault /
deployment env vars). Reload claims-service or rely on
`IOptionsMonitor` to pick up the change. Verify:

```
GET /api/v1/admin/claims/cosmos-migration/status
```

Returns 200 with `migrationsEnabled: true` and the configured
source / target container names.

## Step 4 — Dry-run migration

```
POST /api/v1/admin/claims/cosmos-migration/run
Content-Type: application/json

{
  "dryRun": true,
  "batchSize": 100
}
```

Response shape:

```
{
  "migrationRunId": "...",
  "startedAt": "...",
  "completedAt": "...",
  "durationSeconds": ...,
  "dryRun": true,
  "sourceContainer": "Claims",
  "targetContainer": "ClaimsV2",
  "documentsRead": <int>,
  "documentsWritten": <int>,    // would-have-been-written in dry-run
  "documentsSkipped": 0,        // first dry-run; ClaimsV2 is empty
  "documentsErrored": 0,
  "documentsHydrated": <int>,   // legacy rows requiring hydration
  "outcome": "success",
  "issues": []
}
```

Verify:

- [ ] `documentsRead` matches the operator's expected source count
  (e.g., from `SELECT VALUE COUNT(1) FROM c` on the `Claims`
  container).
- [ ] `documentsErrored == 0`. If not zero, inspect `issues[]`. The
  most common cause is documents missing a `TenantId` field —
  these cannot be partitioned in `ClaimsV2` and must be either
  back-filled or excluded prior to apply.
- [ ] `documentsHydrated` aligns with operator expectation of
  pre-versioning legacy data volume.

## Step 5 — Apply migration

```
POST /api/v1/admin/claims/cosmos-migration/run
Content-Type: application/json

{
  "dryRun": false,
  "batchSize": 100
}
```

Idempotent: if interrupted (network blip, pod restart), re-run
with the same body. Already-written documents are skipped via the
batched idempotency check, not rewritten.

Watch:

- `cho.claims.cosmos_migration.documents.total{cho.outcome="written"}`
  ramps to match `documentsRead`.
- `cho.claims.cosmos_migration.documents.total{cho.outcome="errored"}`
  stays flat at zero.
- Structured logs tagged `runId={MigrationRunId}` show progress
  (start + complete; per-batch progress is not logged — telemetry
  is the per-batch surface).

## Step 6 — Cutover

When the apply pass reports `outcome: "success"`:

1. **Pause new claim ingestion.** Stop the 837 ingestion job /
   pause the upstream encounter-submission pipeline. The
   adjudication pipeline can keep draining its Service Bus
   subscription.
2. **Wait for the Service Bus subscription drain.** Watch the
   `claim-version-events` subscription's active message count fall
   to zero (Azure Service Bus metrics).
3. **Run a final delta migration pass.** Same endpoint, `dryRun:
   false`. Picks up any rows written during step 1's pause window
   that weren't in the apply pass. Should typically write a small
   number of documents.
4. **Flip the runtime container.** Update
   `CosmosDb:ContainerName` from `"Claims"` to `"ClaimsV2"` via
   the configuration channel. Redeploy claims-service (or restart
   pods to force config reload).
5. **Resume claim ingestion.**
6. **Smoke test.** Submit one test claim, fetch it via
   `GET /api/v1/claims/{id}`, query its versioning chain. All
   surfaces should respond normally.

## Step 7 — Validation window

For the next 30 days:

- Monitor `cho.claims.processing.duration` for regressions (the
  versioning surface should now be faster, not slower).
- Monitor `cho.claims.adjudication.outcome.total` for any
  unexpected denied / pended ratio shifts.
- Monitor application logs for any `ClaimVersionStateException` or
  Cosmos errors at unexpected rates.

If a regression is identified within the validation window:

**Rollback procedure:**
1. Flip `CosmosDb:ContainerName` back to `"Claims"` via config.
2. Redeploy claims-service.
3. Operations may need to migrate post-cutover writes back to the
   legacy container (rare — most issues are read-side perf
   regressions, not data correctness). Run the migration endpoint
   with `sourceContainer: "ClaimsV2"`, `targetContainer:
   "Claims"` (config flip) — the service tolerates either as the
   source or target.
4. File the regression detail and reschedule the cutover.

## Step 8 — Deprecate legacy `Claims` container

After 30 days of green validation:

- [ ] Open a focused Bicep PR removing the `claimsContainer`
  resource declaration in
  `infrastructure/azure/modules/cosmos-db.bicep`.
- [ ] Confirm no service still references `CosmosDb:ContainerName:
  "Claims"`.
- [ ] Set `ClaimsCosmosMigration:MigrationsEnabled=false` and
  remove the configuration value.
- [ ] Delete the `Claims` container manually via Azure portal /
  CLI after the PR merges (Bicep delete may not actually delete
  data depending on `complete` vs `incremental` deployment mode —
  verify with `az cosmosdb sql container list`).

## Telemetry reference

| Metric | Type | Dimensions |
|---|---|---|
| `cho.claims.cosmos_migration.runs.total` | counter | `cho.outcome` (success / partial / failed), `cho.dry_run` |
| `cho.claims.cosmos_migration.documents.total` | counter | `cho.outcome` (written / would_write / skipped / errored) |
| `cho.claims.cosmos_migration.duration` | histogram (s) | `cho.outcome`, `cho.dry_run` |

## Endpoint reference

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/admin/claims/cosmos-migration/run` | Run dry-run or apply |
| `GET` | `/api/v1/admin/claims/cosmos-migration/status` | Last-run summary + running flag |

Both return 503 when `ClaimsCosmosMigration:MigrationsEnabled=false`.
The deployment-layer ACL (NetworkPolicy / gateway) is the load-bearing
authorization control; the feature flag is a defence-in-depth
tripwire.

## Failure modes

| Failure | Detection | Mitigation |
|---|---|---|
| Migration writes corrupt data | Smoke test post-cutover | Rollback to legacy container via config flip |
| Bicep deployment fails | Bicep deploy step | Bicep failure does not affect running service; investigate template / RBAC and retry |
| Migration runs out of resources mid-run | Run returns `outcome: partial` | Re-run with same body — idempotent; remaining docs migrate next pass |
| Cross-tenant data leak | Defense-in-depth tenant guard in repository | Storage-layer partition + in-memory `TenantId` equality check both prevent; alert on `cho.claims.cosmos_migration.documents.total{cho.outcome="errored"}` |
| Documents written during cutover lost | Final delta pass + brief read-only window | Step 6.3 above |
| Concurrent operator-triggered runs | 409 Conflict from second invocation | In-process running flag rejects second run; first completes |

## Known follow-ups

- **Payments container** at
  [`infrastructure/azure/modules/cosmos-db.bicep`](../../infrastructure/azure/modules/cosmos-db.bicep)
  has the same `/memberId` divergence — out of scope for 5.1b.
  When payment-service operational pressure justifies it, ship a
  parallel `PaymentsV2` migration mirroring this PR's shape.
