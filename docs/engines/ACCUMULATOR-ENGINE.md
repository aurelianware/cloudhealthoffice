# Accumulator Engine

The Accumulator Engine tracks per-member and per-family deductibles, out-of-pocket maximums, visit counts, and day/dollar limits across a benefit plan year. It is consumed by the Benefit Calculation Engine during claims adjudication and cost-sharing calculation.

## Table of contents

- [Design philosophy](#design-philosophy)
- [Architecture](#architecture)
- [Redis key layout](#redis-key-layout)
- [Cache miss and rebuild](#cache-miss-and-rebuild)
- [Atomic writes after adjudication](#atomic-writes-after-adjudication)
- [Claim reversal](#claim-reversal)
- [Annual plan-year reset](#annual-plan-year-reset)
- [Audit trail](#audit-trail)
- [Dependency graph](#dependency-graph)
- [DI registration](#di-registration)
- [Configuration reference](#configuration-reference)
- [QNXT equivalence](#qnxt-equivalence)

---

## Design philosophy

Accumulator balances are **derived values**, not stored state. The source of truth is the finalized claim history in the claims-service. Redis acts as a sub-millisecond hot cache on top of that history, keeping adjudication within its 500 ms latency budget.

This mirrors the QNXT pattern: instead of maintaining an `AccumBalance` table with update/retry loops, CHO calculates accumulators at runtime from claim lines and caches the result atomically.

**Consequences:**
- No optimistic concurrency retry loops for concurrent claims on the same member
- No risk of double-counting from race conditions: `HINCRBYFLOAT` is atomic
- Cache self-heals on miss — even if Redis is flushed, data is never lost
- Annual reset is handled by TTL expiry, not a batch delete job

---

## Architecture

```
Adjudication workflow
        │
        ▼
┌─────────────────────────┐
│  BenefitCalculationEngine│  ◀── reads accumulators to compute cost-sharing
└────────────┬────────────┘
             │ IAccumulatorService
             ▼
┌─────────────────────────┐
│  RedisAccumulatorService │
└──────┬──────────┬────────┘
       │          │
  HGETALL     HINCRBYFLOAT
  (read)       (write)
       │          │
       ▼          ▼
  ┌─────────────────┐       cache miss
  │   Redis Hash    │──────────────────▶ IClaimsAccumulatorSource
  └─────────────────┘                          │
                                               │ GET /api/claims/accumulator-totals
                                               ▼
                                       ┌──────────────┐
                                       │ claims-service│ (Cosmos / MongoDB)
                                       └──────────────┘

  After adjudication:
  RedisAccumulatorService ──fire-and-forget──▶ IAccumulatorAuditWriter
                                                       │
                                                       ▼
                                               MongoAccumulatorAuditWriter
                                               (MongoDB / Cosmos — durable audit)
```

---

## Redis key layout

Two hash keys are maintained per adjudication:

| Key pattern | Scope |
|---|---|
| `accum:{tenantId}:IND:{memberId}:{planId}:{planYear}` | Individual member |
| `accum:{tenantId}:FAM:{subscriberId}:{planId}:{planYear}` | Family (subscriber + all dependents) |

**Hash fields** use the format `{AccumulatorType}:{NetworkTier}`:

```
accum:tenant-001:IND:M12345:plan-abc:2026
  IndividualDeductible:InNetwork        →  "750.00"
  IndividualDeductible:OutOfNetwork     →  "0.00"
  IndividualOopMax:InNetwork            →  "1250.00"
  IndividualOopMax:OutOfNetwork         →  "0.00"
  VisitCount:98                         →  "3"       ← service type code suffix

accum:tenant-001:FAM:S67890:plan-abc:2026
  FamilyDeductible:InNetwork            →  "1500.00"
  FamilyOopMax:InNetwork                →  "2100.00"
```

**Accumulator types** (`AccumulatorType` enum):

| Type | Description |
|---|---|
| `IndividualDeductible` | Per-member deductible applied year-to-date |
| `IndividualOopMax` | Per-member out-of-pocket maximum year-to-date |
| `FamilyDeductible` | Aggregate family deductible across all enrolled members |
| `FamilyOopMax` | Aggregate family out-of-pocket maximum |
| `VisitCount` | Visit counter keyed by service type code (e.g., `VisitCount:98`) |
| `DayCount` | Inpatient day counter (e.g., skilled nursing facility days) |
| `DollarLimit` | Dollar-based benefit limit (e.g., chiropractic annual maximum) |

**Network tiers** (`NetworkTier` enum): `InNetwork`, `OutOfNetwork`, `Tiered1`, `Tiered2`.

**TTL**: 425 days (14 months). Covers the plan year plus a 2-month run-out period for late claims. Keys expire naturally; no batch cleanup is required.

---

## Cache miss and rebuild

When `HGETALL` returns an empty hash (key not found or expired):

1. `RedisAccumulatorService` calls `IClaimsAccumulatorSource.CalculateAccumulatorsAsync()`
2. The host service implementation (`ClaimsServiceAccumulatorSource`) calls `GET /api/claims/accumulator-totals` on the claims-service
3. The claims-service queries finalized claim lines (status `Approved`, `PartiallyPaid`, `Paid`) for the member/plan/year and aggregates by accumulator type and network tier
4. Results are written back to Redis via `HMSET` + `EXPIRE`
5. On HTTP failure, the service logs a warning and returns an empty snapshot list — adjudication continues using zero accumulators rather than failing

The claims-service endpoint:

```
GET /api/claims/accumulator-totals
  ?ownerId={memberId|subscriberId}
  &scope={Individual|Family}
  &benefitPlanId={planId}
  &planYear={YYYY}
```

Required configuration in benefit-plan-service:
```json
{
  "Services": {
    "ClaimsServiceUrl": "http://claims-service"
  }
}
```

---

## Atomic writes after adjudication

After cost-sharing is calculated for a claim, the adjudication workflow calls `ApplyUpdatesAsync()` with a list of `AccumulatorUpdate` objects.

Updates are batched per scope and sent in a single Redis pipeline using `HINCRBYFLOAT`:

```
HINCRBYFLOAT accum:t1:IND:M123:plan-abc:2026 IndividualDeductible:InNetwork 250.00
HINCRBYFLOAT accum:t1:IND:M123:plan-abc:2026 IndividualOopMax:InNetwork     350.00
HINCRBYFLOAT accum:t1:FAM:S456:plan-abc:2026 FamilyDeductible:InNetwork     250.00
HINCRBYFLOAT accum:t1:FAM:S456:plan-abc:2026 FamilyOopMax:InNetwork         350.00
```

Each `HINCRBYFLOAT` is individually atomic. The pipeline reduces round trips; order within a pipeline is not guaranteed but all increments are applied.

---

## Claim reversal

When a claim is voided or reversed, the cache keys for both the individual and family scopes are deleted:

```
DEL accum:{tenantId}:IND:{memberId}:{planId}:{planYear}
DEL accum:{tenantId}:FAM:{subscriberId}:{planId}:{planYear}
```

The next read for that member will trigger a cache miss and rebuild from claim history — which no longer includes the reversed claim. This is always correct regardless of whether the key existed.

---

## Annual plan-year reset

Redis keys expire naturally via TTL at 14 months. No explicit reset operation is required for normal operation.

For environments with a regulatory requirement to explicitly purge accumulator data at plan year end, the `ResetForPlanYearAsync` method can be extended to scan for and delete matching keys. At scale, Redis `SCAN` is preferable to `KEYS` — but TTL expiry is the recommended approach.

The audit trail in MongoDB/Cosmos is retained indefinitely per compliance requirements.

---

## Audit trail

`IAccumulatorAuditWriter` is an optional, non-blocking interface. When registered, it receives accumulator update events **fire-and-forget** after Redis is updated. Adjudication does not wait for the durable write.

The production implementation `MongoAccumulatorAuditWriter` persists `AccumulatorDocument` records to MongoDB or Cosmos DB for:
- Member portal display ("you've met $750 of your $1,500 deductible")
- Compliance reporting and audit
- Cache rebuild fallback if `IClaimsAccumulatorSource` is unavailable

If the audit write fails, it is logged as a warning. The cache is self-healing — the next rebuild from claims history will produce the correct state.

---

## Dependency graph

`RedisAccumulatorService` requires five dependencies, all registered by the host service:

| Interface | Required | Registration |
|---|---|---|
| `IConnectionMultiplexer` | Yes | `ConnectionMultiplexer.Connect(Redis:ConnectionString)` |
| `IClaimsAccumulatorSource` | Yes | `ClaimsServiceAccumulatorSource` (typed `HttpClient`) |
| `IBenefitEngineTenantContext` | Yes | `HttpContextTenantContext` (reads from `TenantMiddleware`) |
| `IAccumulatorAuditWriter` | No (optional) | `MongoAccumulatorAuditWriter` |
| `IAccumulatorRepository` | Yes (for audit writer) | `AccumulatorRepositoryMongo` or `AccumulatorRepositoryCosmos` |

---

## DI registration

In the host service's `Program.cs`:

```csharp
// 1. Redis connection
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:ConnectionString"]!));

// 2. Claims source (typed HttpClient)
builder.Services.AddHttpClient<IClaimsAccumulatorSource, ClaimsServiceAccumulatorSource>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:ClaimsServiceUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(10);
});

// 3. Tenant context
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IBenefitEngineTenantContext, HttpContextTenantContext>();

// 4. Accumulator repository (for audit writer)
//    Register whichever matches your DB choice:
builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryMongo>();
// or:
builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryCosmos>();

// 5. Optional audit writer
builder.Services.AddScoped<IAccumulatorAuditWriter, MongoAccumulatorAuditWriter>();

// 6. Engine wiring
builder.Services.AddBenefitEngine()
    .UseChoBenefitPlanProvider()
    .UseRedisAccumulatorService();
```

---

## Configuration reference

| Key | Required | Description |
|---|---|---|
| `Redis:ConnectionString` | Yes | StackExchange.Redis connection string |
| `Services:ClaimsServiceUrl` | Yes | Base URL of the claims-service (e.g., `http://claims-service`) |
| `CosmosDb:DatabaseName` | Cosmos only | Database name (default: `CloudHealthOffice`) |
| `CosmosDb:Endpoint` / `CosmosDb:Key` | Cosmos only | Cosmos DB credentials |
| `MongoDb:ConnectionString` | Mongo only | MongoDB connection string |
| `MongoDb:DatabaseName` | Mongo only | MongoDB database name |
| `BenefitEngine:AccumulatorContainer` | Cosmos only | Container name (default: `Accumulators`) |
| `BenefitEngine:AccumulatorCollection` | Mongo only | Collection name (default: `Accumulators`) |

---

## QNXT equivalence

| CHO component | QNXT equivalent |
|---|---|
| `AccumulatorDocument` | `AccumBalance` table |
| `AccumulatorType.IndividualDeductible` | `ACCUM_TYPE = 'DED'` + `SCOPE = 'IND'` |
| `AccumulatorType.IndividualOopMax` | `ACCUM_TYPE = 'OOP'` + `SCOPE = 'IND'` |
| `AccumulatorType.FamilyDeductible` | `ACCUM_TYPE = 'DED'` + `SCOPE = 'FAM'` |
| `AccumulatorType.VisitCount` | `ACCUM_TYPE = 'VISIT'` + service type code |
| `IClaimsAccumulatorSource` | QNXT runtime AccumBalance calculation from `CLAIM_LINE` |
| Plan-year TTL | QNXT annual AccumBalance reset batch job |
