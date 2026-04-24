# Shared `ICacheProvider`

`ICacheProvider` is the Cloud Health Office cache abstraction. One
interface, three interchangeable backends, lives in
[`CloudHealthOffice.Infrastructure.Caching`](../../src/services/shared/CloudHealthOffice.Infrastructure/Caching/).
Production runs on Redis; dev and test run on `IMemoryCache`.

## What this is — and isn't

`ICacheProvider` covers the **90% of Redis usage that is string key /
object value with TTL**: the read-through decorator pattern most of our
engines already implement, where a Cosmos or Mongo hit is expensive and
can be amortized behind a short-TTL cache. It is deliberately **not** a
general-purpose Redis facade.

**In the interface:** `GetAsync`, `SetAsync`, `RemoveAsync` (single + bulk),
`GetOrSetAsync` (read-through with per-key single-flight coalescing).

**Deliberately NOT in the interface:**

- Hash operations (`HashGet`, `HashSet`, `HashIncrement`) — the atomic
  increment on a hash field is the whole reason `RedisAccumulatorService`
  was designed around Redis; exposing those operations on a neutral cache
  interface would leak Redis semantics into every consumer.
- Pub/sub. That is messaging, not caching — `IMessageBus` is the right
  surface (and for streaming pipelines, Kafka).
- Distributed locks. A legitimate use case, but one that deserves its
  own `IDistributedLock` abstraction when we need it.
- Atomic counters / `Increment`. Same reasoning as hashes — the whole
  point is atomicity, and hiding it behind `ICacheProvider` would imply
  guarantees the abstraction can't make.
- Pattern-based deletion (`KEYS pattern*` / `SCAN`). Expensive on large
  key spaces in production; exposing it invites accidental N-second
  blocking calls. When a legitimate use case arises, callers take a
  bounded dependency on `IConnectionMultiplexer` directly (see the PA
  rule repository below) rather than push that shape into the shared
  interface.

## Decision tree — when to use what

```
Reads dominate, data is regeneratable, loss is recoverable?
  └─ Yes → ICacheProvider
       └─ Key is pure string, value is JSON-round-trippable, TTL fits?
            └─ Yes → ICacheProvider (via AddChoCaching)
            └─ No  → stop, rethink the design

Atomic ops on structured state (hashes, counters, sets)?
  └─ Yes → IConnectionMultiplexer directly, with a class-level
            <remarks> block explaining the specific Redis-native
            capability you need and why ICacheProvider cannot express
            it. Current examples: RedisAccumulatorService, the
            SCAN-based flush path inside RedisPaRuleRepository.

Durable business state the system cannot reconstruct?
  └─ Cosmos / Mongo, never Redis. Don't treat cache as storage.
```

## The two deliberate exceptions

`ICacheProvider` deliberately covers the 90% of Redis usage that is
K/V-with-TTL. Two consumers are called out as deliberate exceptions —
`RedisAccumulatorService` (atomic hash increments) and `PaRuleRepository`
(pattern-based SCAN invalidation) — each keeps direct
`IConnectionMultiplexer` access with a class-level XML comment explaining
the specific Redis-native capability that would be lost by going through
the abstraction.

### 1. `RedisAccumulatorService` (BenefitEngine)

[`Redisaccumulatorservice.cs`](../../src/engines/CloudHealthOffice.BenefitEngine/Services/Redisaccumulatorservice.cs)
uses Redis hashes with `HINCRBYFLOAT` to accumulate member deductible /
out-of-pocket-max totals as claims finalize. The operation MUST be
server-side atomic: two concurrent claim adjudications for the same
member hit the same hash field simultaneously, and a read-modify-write
cycle on the application side would race and lose increments.

Routing that through `ICacheProvider.SetAsync` would JSON-serialize the
entire hash on every update — reintroducing the race the current design
avoids. Routing through a hypothetical `IncrementAsync` on
`ICacheProvider` would bake Redis-specific atomicity guarantees into a
neutral interface that `InMemoryCacheProvider` could not honestly
implement.

### 2. `RedisPaRuleRepository.DeleteAsync` (PriorAuthRuleEngine)

[`PaRuleRepository.cs`](../../src/engines/CloudHealthOffice.PriorAuthRuleEngine/Persistence/PaRuleRepository.cs)
moves K/V operations (read, set, exact-key invalidate on upsert /
bulk-upsert) onto `ICacheProvider`. The `DeleteAsync(ruleId, stateCode)`
path is the exception: the cache is keyed on
`(stateCode, lob, program, tenantId)`, and `(ruleId, stateCode)` alone
cannot reconstruct the cache key set. The only safe option is a
`SCAN` over `pa-rules:{stateCode}:*` and a `DEL` of every match.

Pattern deletion is exactly what `ICacheProvider` does not expose.
Rather than bend the interface, `RedisPaRuleRepository` takes a bounded
second dependency on `IConnectionMultiplexer` and uses it only inside
the flush path. A `TODO(scale)` comment at the SCAN call site documents
the sidecar-index follow-up if rule cardinality grows past a few
thousand keys per state.

## Configuration

```jsonc
"Caching": {
  // Auto | Redis | InMemory | Null — mirrors MessagingOptions.Backend.
  // Auto resolves to Redis when RedisConnectionString is set AND env is
  // not Development; otherwise InMemory. Auto → InMemory outside
  // Development emits a startup warning because a process-local cache
  // doesn't share state across replicas.
  "Backend": "Auto",

  // Canonical location for the Redis connection string. The legacy
  // top-level "Redis:ConnectionString" key is honoured for one release
  // with a deprecation warning.
  "RedisConnectionString": "redis-prod.redis.cache.windows.net:6380,password=...,ssl=true",

  // Safety cap for the single-flight coalescer. Under pathological load
  // (key space expanding faster than factories complete) the coalescer
  // prunes released entries opportunistically and emits
  // cho_cache_singleflight_evictions. Default 10000.
  "SingleFlightMaxInFlight": 10000
}
```

## Tenant prefixing and PHI rejection

Every cache key is mandatorily transformed by `CacheKeyGuard` before
hitting the backend. Two compliance controls:

1. **Tenant prefixing.** The logical key `enrollment:config:txmco01`
   becomes `{env}:{tenantId}:enrollment:config:txmco01`. `tenantId` is
   resolved from `HttpContext.Items["TenantId"]` (set by
   `TenantMiddleware`). Cross-tenant cache pollution is unreachable by
   construction. The sole escape hatch is `CacheScope.Global`, which
   must be passed deliberately at the call site — the guard will not
   infer it from a missing tenant context.

2. **PHI-token rejection.** Cache keys surface in Redis SLOWLOG, ops
   dashboards, and distributed traces. The guard rejects keys whose
   separator-bounded tokens contain any of `ssn`, `mbi`, `dob`,
   `memberId`, `patientId`, `ssnHash` (case-insensitive). The
   pseudonymized form `memberIdHash` is explicitly permitted because
   it is already safe to surface in logs. Violations throw
   `ArgumentException` at runtime — fail fast, not silent.

### Examples

| Logical key                                | Scope  | Result |
|--------------------------------------------|--------|--------|
| `enrollment:config:txmco01`                   | Tenant | ✅ `production:txmco01:enrollment:config:txmco01` |
| `member:memberIdHash:deadbeef`             | Tenant | ✅ allowed — hashed form |
| `feature-flags:pas-auto`                   | Global | ✅ `production:_global:feature-flags:pas-auto` |
| `enrollment:memberId:M12345`               | Tenant | ❌ `ArgumentException` — PHI token `memberId` |
| `enrollment ssn 123`                       | Tenant | ❌ `ArgumentException` — whitespace + PHI token |
| `enrollment:config`                        | Tenant | ❌ `InvalidOperationException` — no tenant on HttpContext |

## Observability

Redis tracing from A.7.4 (`AddRedisInstrumentation` in
`ObservabilityExtensions`) attaches at the `IConnectionMultiplexer`
level, so every `ICacheProvider` operation that lands on
`RedisCacheProvider` produces a `db.system=redis` span without any
additional wiring. When the same physical multiplexer is shared with
the accumulator / PA-rule SCAN path, all three surfaces flow through
the same instrumented client.

Single-flight coalescer evictions emit a counter named
`cho_cache_singleflight_evictions`; sustained non-zero readings indicate
either a pathological key-space growth or a `SingleFlightMaxInFlight`
that needs tuning.

## Migration guidance

- **New consumer is pure K/V + TTL?** Inject `ICacheProvider`. Rename
  the decorator registration to `WithTenantConfigCache()` /
  `WithRuleCache()` — no "Redis" in the name, because backend is an
  `AddChoCaching` concern.
- **New consumer needs atomic hash / counter / SCAN?** Inject
  `IConnectionMultiplexer` directly and add a class-level XML
  `<remarks>` block pointing back to this decision tree. Update this
  document's "Two deliberate exceptions" section so the next person
  knows the rule isn't being bent without a reason.
- **New consumer needs distributed locking?** Come talk to the platform
  team — that deserves its own abstraction (`IDistributedLock`) rather
  than a fourth informal exception.

## Provisioning

[`scripts/azure/provision-redis.sh`](../../scripts/azure/provision-redis.sh)
creates or updates the Azure Cache for Redis instance at Standard SKU,
idempotently. Pattern mirrors `provision-servicebus-queues.sh`.
