# ADR 017: MongoDB API Is the Only Document Persistence Path

## Status

Accepted. Narrows [ADR 006](006-persistence-boundaries.md).

## Context

Most services could persist through either the MongoDB driver or the native
Azure Cosmos DB SDK (`Microsoft.Azure.Cosmos`, SQL API), chosen at start-up. About
30 services and engines carried both implementations. The two paths behaved
differently (ETag patches vs. filter-based compare-and-set, partition keys,
enum spelling in SQL predicates), so every behaviour needed two tests, and
some capabilities existed on only one path (claim adjustments threw on
Cosmos). The deployed target is a managed MongoDB-compatible service, so the
SDK path bought nothing the Mongo path could not do.

## Decision

1. **The MongoDB driver and wire protocol are the only document persistence
   path.** The native Cosmos DB SDK, its repositories, serializer, health check,
   emulator tests and the SQL-API Bicep module are removed. A service fails at
   start-up when `MongoDb:ConnectionString` is not set.
2. **In Azure, the store is Azure DocumentDB (formerly Azure Cosmos DB for
   MongoDB vCore)**, provisioned by
   `infrastructure/azure/modules/documentdb-mongo-cluster.bicep` (`enableDocumentDb`
   in `main.bicep`). The RU-based Cosmos DB for MongoDB account is kept only for
   the Million Claim Challenge throttling benchmark, deployed by its own script
   (`infrastructure/azure/COSMOS-MONGODB-FREE-TIER.md`); it is not a deployment
   target.
3. **PostgreSQL stays where ADR 006 put it**: reference-data-service's
   relational lookups. This ADR is about document storage only.
4. Tests run against real MongoDB 7 (EphemeralMongo); there is no second
   backend to keep in step.

## Compatibility with Azure DocumentDB

Checked against Microsoft's Azure DocumentDB
[MQL compatibility](https://learn.microsoft.com/azure/documentdb/compatibility-query-language)
and [limits](https://learn.microsoft.com/azure/documentdb/limitations) pages
(retrieved 2026-10-16).

| Feature the code uses | Azure DocumentDB | Notes / workaround |
|---|---|---|
| Multi-document transactions (benefit plan, provider, organization, family relationship, personal representative, premium invoice) | Supported; **30-second maximum lifetime** | Each transaction is a short paired write. Keep them that way; never wrap a batch or a remote call in one. A `WriteConflict` (112) is answered as 409. |
| Retryable writes | Not documented as supported; Microsoft's connection strings set `retrywrites=false` | The connection string must carry `retrywrites=false`. Without driver retries a write that fails on a transient network error surfaces to the caller; request paths answer 5xx and the client retries, and writes are conditional on a revision or idempotency key so a retry cannot apply twice (see `docs/architecture/mongo-write-concurrency.md`). |
| Unique and compound unique indexes (62) | Supported | Unique index builds run in the foreground and **block writes** on that collection. Index initializers run at start-up against empty or small collections; build a new unique index on a large collection in a maintenance window. |
| Aggregation: `$match`, `$group`, `$lookup`, `$unwind`, `$project`, `$facet`, `$sort`, `$out`, `$merge` (45 pipelines) | Supported | — |
| Change streams | Supported | Not used. |
| Text, hashed or geospatial indexes, collations, `hint` | — | Not used. |
| Driver `BulkWriteAsync` | Supported (insert/update/delete commands) | The MongoDB 8.0 cross-collection `bulkWrite` command is not supported and is not used. A single batch may not exceed 25,000 writes. |
| Sorting | Done in memory, not pushed down to an index | Large unbounded sorts can hit the tier's query-memory limit; list endpoints page and filter by tenant first. |
| Cursors | 10-minute lifetime | Long exports must page instead of holding one cursor. |
| Sharding | Up to 10 physical shards | The demo uses one shard. If collections are sharded later, `TenantId` is the natural shard key, and unique indexes must then include it. |

The demo environment is wiped and rebuilt, so moving to DocumentDB needs no
data migration.

## Consequences

Positive:

- One implementation and one test suite per repository.
- Concurrency, idempotency and outbox semantics are defined once, on the
  Mongo path, and tested on a real server.
- Smaller dependency surface (`Microsoft.Azure.Cosmos` and its transitive
  Newtonsoft and native interop libraries are gone).

Tradeoffs:

- Behaviour specific to Azure DocumentDB (transaction lifetime, foreground
  unique-index builds, in-memory sorts, retryable writes) is not exercised by
  EphemeralMongo. It is listed above and needs a smoke run against a real
  cluster before a production deployment.
- Some `CosmosDb:*` configuration keys remain as collection-name overrides read
  by the Mongo path: benefit-plan (`ContainerName`,
  `ServiceCategoryMappingsContainerName`, `PlanVersionTransitionsContainer`,
  `PlanVersionEventsContainer`, `PlanYearTransitionEventsContainer`), provider
  (`ProviderTransitionsContainer`, `CredentialingEventsContainer`,
  `ProviderVersionEventsContainer`, `ProviderVerificationEventsContainer`,
  `ProviderParticipationEventsContainer`), authorization and sponsor
  (`ContainerName`), attachment (`AttachmentsContainerName`), claims
  (`ClaimVersionEventsContainer`, `ClaimAdjustmentsContainer`) and
  trading-partner (`COSMOS_CONTAINER_TRADING_PARTNERS`). They are misnamed, not
  Cosmos-only; renaming them is a configuration change left for later.
- `src/tools/migration-wizard`, which wrote TriZetto exports straight into
  Cosmos SQL containers that no service read, was removed with the SDK.
  Nothing references `Microsoft.Azure.Cosmos` any more.

## References

- [ADR 006: Fit-for-purpose persistence boundaries](006-persistence-boundaries.md)
- [Mongo write concurrency](../architecture/mongo-write-concurrency.md)
