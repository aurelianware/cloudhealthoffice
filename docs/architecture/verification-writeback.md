# Verification Result Write-Back (Capability 5.4.5)

Status: 5.4.5 — projection write-back  
Service: `src/services/provider-service`  
Depends on: `provider-verification-service` (HTTP)

## Why

Capability 5.1 (Provider Identity & Versioning) declared cached
integrity-score columns on the `Provider` entity:

```csharp
public int? IntegrityScore { get; set; }
public string? IntegrityRating { get; set; }
public DateTimeOffset? LastVerifiedAt { get; set; }
public DateTimeOffset? NextVerificationDue { get; set; }
```

…but no write path populated them. `ProviderVerificationOrchestrator`
in `src/engines/CloudHealthOffice.ProviderVerificationEngine` produced a
verification record on a transient object that was never persisted back
to the entity. Capability 5.4 (Network Roster API) shipped a roster
that reads `Provider.IntegrityScore` directly — and got `null` for
every row in production.

5.4.5 closes the gap. The roster's `IntegrityScore` column lights up;
adjudication, FHIR projections, and the provider profile card can read
the cached value without making per-request HTTP calls into
verification-service.

## Architecture — hosted projection in provider-service

Of four reasonable architectures (hosted projection, outbound webhook,
event bus, change-feed listener), 5.4.5 ships **hosted projection**.
Three reasons:

1. **Consistency with platform pattern.** `PlanYearScheduler` and
   `PlanYearTransitionEventIndexInitializer` already establish the
   hosted-service convention. A new `IntegrityProjectionWorker` fits
   the slot natively — provider-service already registers a hosted
   service (`ProviderVersionEventIndexInitializer`).
2. **No new infrastructure.** Provider-service has no Kafka package
   today; PR 7.2's `IProviderVersionEventPublisher` is in-process
   Mongo only. Event-bus adoption (Option C) would require wiring
   Kafka into provider-service in this PR — out of scope.
3. **Idempotency is straightforward.** Each refresh runs against a
   single Provider row keyed by `(TenantId, ProviderId)`. Restartable.
   Failure modes are observable and retriable.

Trade-off: pull-based, not push-based. For the verification cadence
(NPPES daily, LEIE daily, PECOS weekly, FSMB monthly), pull-based is
fine.

```
┌──────────────────────────────────────────────────────────────────┐
│ provider-service                                                 │
│                                                                  │
│   IntegrityProjectionWorker (BackgroundService)                  │
│        │ every SweepInterval (default 1h)                        │
│        ▼                                                         │
│   ListProviderTenantIdsAsync()  ── distinct tenants              │
│        │                                                         │
│        ▼  per tenant                                             │
│   ProviderIntegrityProjectionService.RefreshTenantAsync(tenant)  │
│        │                                                         │
│        ├─► ListProvidersForIntegrityRefreshAsync                 │
│        │     filter: NextVerificationDue <= now OR IS NULL       │
│        │     paginate: 100/page                                  │
│        │                                                         │
│        ├─► HttpProviderVerificationClient.VerifyBatchAsync(npis) │
│        │     POST /api/v1/providers/verify/batch  (HTTP)         │
│        │     returns ProviderVerificationRecord per NPI          │
│        │                                                         │
│        ├─► UpdateIntegrityProjectionAsync(score, rating, ...)    │
│        │     Cosmos: PatchItemAsync (4 Set ops on head row)      │
│        │     Mongo:  FindOneAndUpdateAsync ($set on 4 fields)    │
│        │     bypasses UpdateAsync's version-state guard          │
│        │                                                         │
│        └─► PublishRefreshedAsync(...)                            │
│              ProviderVerificationEvents append-only stream       │
│              EventId = "refreshed:{providerId}:{verifiedAtIso}"  │
└──────────────────────────────────────────────────────────────────┘
```

## Projection metadata — exempt from versioning

`IntegrityScore`, `IntegrityRating`, `LastVerifiedAt`, and
`NextVerificationDue` are **projection metadata**, not provider-identity
fields. They are computed by `provider-verification-service` and
written back without ratifying a new version.

Concretely:

- `Provider.UpdateAsync` continues to throw `ProviderVersionStateException`
  on non-Draft rows (PR 5.1 invariant preserved).
- `IProviderRepository.UpdateIntegrityProjectionAsync` is a separate
  write path that bypasses the state guard — it `$set`s only the four
  projection fields on the head Active row. No new `VersionNumber` is
  created.
- See `provider-versioning.md` "Projection metadata — exempt from
  versioning" for the policy statement.

This avoids unbounded version-chain growth (verification refreshes run
every 24h on the shortest-window cadence — emitting a new version per
refresh would rapidly drown identity-change versions in noise) and
preserves the audit semantic that "each version represents an
identity change."

## Verification-service contract

Hosted worker calls **`POST /api/v1/providers/verify/batch`** on
`provider-verification-service` (Minimal API at
`src/services/provider-verification-service/Program.cs`). HTTP — not
project reference — so:

- Service boundary preserved (mirrors `HttpProviderIntegrityGate` in
  `benefit-plan-service`).
- The verification engine's six data-source HTTP clients (NPPES,
  LEIE, PECOS, Open Payments, Medicare, FSMB) are not duplicated in
  provider-service.

The endpoint caps batch size at 100 NPIs;
`IntegrityProjectionOptions.PageSize` defaults to 100 so each page is
exactly one HTTP call. `HttpProviderVerificationClient` returns an
empty result on transport failure or non-2xx; the projection service
treats that as "no record" — cached scores stay put, the row's
`NextVerificationDue` is left alone, the next sweep retries.

## Schedule cadence — composite-on-shortest-window

Per-source refresh windows (configurable in
`IntegrityProjectionOptions.Windows`):

| Source | Default window | Regulatory cadence |
|--------|---------------|--------------------|
| NPPES | 24 hours | Daily registry update |
| LEIE / SAM | 24 hours | Daily exclusion list |
| PECOS | 7 days | Weekly enrollment delta |
| Open Payments | 90 days | Quarterly payment cycle |
| Medicare Utilization | 90 days | Quarterly utilization cycle |
| FSMB | 30 days | Monthly licensing review |

The composite refresh runs at the **shortest active window** —
currently 24 hours (NPPES). After every successful refresh, the
worker computes
`NextVerificationDue = LastVerifiedAt + ShortestActiveWindow()` and
writes that back so the next sweep skips this row until it's due
again.

### Composite cadence trade-off

"Shortest active window" means the full composite re-runs every 24h,
including the 90-day Open Payments / Medicare sources. Operationally
that's redundant — those source adapters might re-fetch identical
data each refresh. The trade-off:

- **What we ship (5.4.5):** one materialised
  `Provider.NextVerificationDue` per row. Simple, low storage cost,
  one Cosmos/Mongo column. Re-runs the full composite at the
  shortest cadence.
- **What we deferred (5.10):** per-source materialised refresh
  state (six per-source `LastRefreshedAt` columns on `Provider`).
  Each source refreshes on its own cadence; composite is recomputed
  whenever any source updates. Honors all six cadences but adds six
  fields and six timestamp comparisons per sweep.

Capability 5.10 (Verification Integrity Score Surface) is the right
place to revisit if the redundant fetches become operationally
visible. Until then, the simpler model wins.

## Iteration strategy

Per-tenant pagination via `ListProvidersForIntegrityRefreshAsync`,
mirroring the pattern established by `ListNetworkRosterAsync`:

- **Cosmos:** `OFFSET / LIMIT` query, partition-scoped on `TenantId`,
  filter on `(versionState = Active OR legacy) AND (nextVerificationDue
  <= dueBefore OR null)`.
- **Mongo:** `Find().Sort(ProviderId).Skip(safeSkip).Limit(pageSize)`,
  same filter logic. Stable sort on `ProviderId, Id` so paginated
  sweeps produce deterministic ordering.

Per-tenant cap (`MaxProvidersPerTenantPerSweep`, default 1000)
protects round-robin fairness when one tenant has a fully-due backlog
post-deploy. Admin backfill bypasses the cap via
`request.MaxProviders`.

The worker enumerates distinct tenants via
`ListProviderTenantIdsAsync`. Cross-partition scan; called once per
sweep so RU cost is bounded by the sweep cadence (default 1h).

## On-demand refresh endpoint

```
POST /api/v1/providers/{id}/verification/refresh?force=true
```

Returns the patched `IntegrityProjectionRefreshResult` synchronously.
Used by credentialing workflows and credential-event-driven
re-verification.

Route is `/{id}/verification/refresh` (not `/{id}/verify`) to avoid
ambiguity with verification-service's
`GET /api/v1/providers/{npi}/verify`. The two services would otherwise
share the `/api/v1/providers/...` path-prefix at the gateway layer.

## Backfill — admin HTTP endpoint

```
POST /api/v1/admin/providers/backfill-integrity-projection?tenantId=X&maxProviders=N
```

Admin-callable. Forces an integrity-projection refresh for every Active
provider in the named tenant, regardless of `NextVerificationDue`.
Idempotent (last-write-wins on the four projection-metadata fields):
use to populate legacy null projections, recover from extended
verification-service outages, or operator-driven data-quality refresh.

### Auth posture — defence in depth

Two gates protect this route:

1. **`IntegrityProjection:AdminBackfillEnabled` flag, default `false`.**
   The controller returns `503 Service Unavailable` (NOT 404 — operators
   need to know the endpoint exists but is gated) until configuration
   explicitly opts in. This is a tripwire: provider-service does not
   yet configure authentication (`Program.cs` calls `UseAuthorization()`
   with no `AddAuthentication()`), so without this guard a
   misconfigured gateway / NetworkPolicy could expose a route that
   triggers large cross-service work.
2. **Deployment-layer ACL.** Even with the flag enabled, the load-bearing
   authorization is in the deployment layer:
   - Kubernetes `NetworkPolicy` restricting the `/api/v1/admin/...`
     prefix to operator pods only;
   - Gateway / ingress ACL with mTLS or signed admin JWT;
   - Or both.

The flag is a safety net, not authentication. Enabling it without a
deployment-layer restriction exposes the endpoint.

A capitation-service-style CLI was considered (precedent:
`SplitCapitationContracts.cs`); HTTP fits provider-service's existing
surface better and avoids an exec environment per tenant.

## Event stream — `ProviderVerificationEvents`

Each successful refresh emits a `ProviderVerificationRefreshed` event
to a new Mongo collection `ProviderVerificationEvents`, mirroring the
PR 5.1 `ProviderVersionEvents` pattern:

- **Idempotent EventId**:
  `refreshed:{providerId}:{verifiedAtUtcIso}`.
  Re-emitting at the same `verifiedAt` returns the existing event.
- **Monotonic Version per `(TenantId, ProviderId)`**.
- **Partition key**: `{TenantId}:{ProviderId}`.
- **Indexes** (provisioned by
  `ProviderVerificationEventIndexInitializer`):
  - `(TenantId, ProviderId, EventId)` UNIQUE — idempotency.
  - `(TenantId, ProviderId, Version)` UNIQUE — monotonic ordering.

No cross-service consumer ships in 5.4.5. Capability 5.10 is the
planned subscriber. Cosmos-only deployments register
`NoopProviderVerificationEventPublisher` which logs a warning per
emit so ops can spot the missing wiring.

## `HttpProviderIntegrityGate` posture — migrated in 5.10

5.4.5 left `benefit-plan-service` consuming the score live via
`HttpProviderIntegrityGate.CheckAsync(npi)` per adjudication request.
Capability 5.10 (Integrity Score Surface) migrates the gate to a
cached-or-live pattern: the gate reads `Provider.IntegrityScore` from
provider-service first and falls back to the live verification path
only when the cached score is null, stale beyond
`ProviderIntegrityGate:StalenessFallbackThreshold` (default 7 days),
or the caller explicitly opts in via `forceRefresh: true`.

See `docs/architecture/integrity-score-consumption.md` for the
canonical decision tree, the per-path telemetry shape
(`cho.provider.integrity_gate.decisions.total`), and the staleness
alerting gauge (`cho.provider.integrity_score.stale_count`) that
5.10 piggybacks on this worker's sweep.

## Configuration

```jsonc
{
  "IntegrityProjection": {
    "Enabled": true,
    "AdminBackfillEnabled": false,  // opt in per environment; pair with NetworkPolicy
    "SweepInterval": "01:00:00",
    "PageSize": 100,
    "MaxProvidersPerTenantPerSweep": 1000,
    "Windows": {
      "Nppes":               "1.00:00:00",
      "LeieSam":             "1.00:00:00",
      "Pecos":               "7.00:00:00",
      "OpenPayments":        "90.00:00:00",
      "MedicareUtilization": "90.00:00:00",
      "Fsmb":                "30.00:00:00"
    }
  },
  "ProviderVerification": {
    "BaseUrl": "http://provider-verification-service",
    "TimeoutSeconds": 30
  }
}
```

`Enabled=false` keeps the worker idle (admin backfill + on-demand
refresh remain available).

## Recovery posture

| Failure mode | Behavior |
|---|---|
| Hosted worker crashes | Restartable; idempotent refresh; re-runs after pod restart. |
| Verification source down | `HttpProviderVerificationClient` returns empty; refresh skipped; cached score preserved; row's `NextVerificationDue` untouched so next sweep retries. |
| Repository write fails | Caught, logged, counts as `Failed` in tenant sweep result; next sweep retries the row. |
| Backfill partial | Idempotent; rerun completes the gap. |
| Event publication fails after patch landed | Logged; patch already applied; re-emitting on next sweep is idempotent (deterministic `EventId` on `(providerId, verifiedAt)`). |
| Projection-metadata write interpreted as version-identity change | Would be a regression — `IntegrityProjectionWritePathTests.UpdateIntegrityProjection_does_not_create_a_new_version` guards against it. |

The work is purely additive. Worst-case rollback: revert the PR;
roster's `IntegrityScore` column returns to null;
`HttpProviderIntegrityGate` continues to work for adjudication
critical path; nothing else regresses.

## Out of scope (deferred to capability 5.10)

- Per-source materialised refresh-state on `Provider`.
- Migration of `HttpProviderIntegrityGate` to read from
  `Provider.IntegrityScore` instead of live HTTP.
- `ProviderVerificationRefreshed` event consumers / fan-out.
- Application-layer admin-auth middleware on the backfill endpoint.
- Per-source schedule overrides per tenant.
