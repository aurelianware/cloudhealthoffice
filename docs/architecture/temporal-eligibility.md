# Temporal Eligibility & Batch Verification

Roadmap section 5.2 — extends `eligibility-service` with two new capabilities
that complement the existing real-time 270/271 inquiry flow.

## 1. Temporal eligibility

### Endpoint

```
GET /api/v1/eligibility/temporal?memberId={id}&serviceDate=YYYY-MM-DD
X-Tenant-ID: <tenant>
```

Returns every coverage that was active on `serviceDate` together with:

- COB order (`cobOrder`, `coverageSequence` = P / S / T / O),
- plan snapshot (`planId`, `planVersion`, `coverageLevel`, `insuranceLineCode`,
  effective / termination dates),
- accumulator snapshot (`accumulators.source = "stub"` until accumulator-service
  lands in a later roadmap prompt),
- `isRetroactive` flag (true when the coverage's effective date is in the past
  at query time and still covers the service date),
- `isCOBRA` flag mapped from the Coverage-service status code.

### Internals

- `TemporalEligibilityService` is a **read projection only** — it goes directly
  to `coverage-service` (`GET /api/v1/coverage/member/{id}/active?serviceDate=...`)
  and does *not* flow through `IEligibilityAdapter`. The adapter path is
  reserved for live 270/271 round-trips; the temporal endpoint is a query over
  already-stored state.
- COB ordering is derived from the coverage record's `OtherInsurance.IsPrimaryPayer`
  and `MedicareCoverage.IsPrimaryPayer` flags. When neither is set, coverages
  are ordered by ascending `effectiveDate` so the earliest becomes primary.
- Accumulators are fetched through `IAccumulatorClient`. The current binding is
  `StubAccumulatorClient` which returns zeroed values and stamps `source = "stub"`.
  When accumulator-service ships, swap the DI binding — no other change is
  required.
- Tenant boundary is enforced by the existing `TenantMiddleware`; the
  controller reads `HttpContext.Items["TenantId"]` and forwards it to the
  outbound Coverage-service call via `X-Tenant-ID`.

## 2. Batch eligibility

### Endpoints

```
POST /api/v1/eligibility/batch           (text/csv or application/json)
  → 202 Accepted + BatchEligibilityJob (id, status, counts)
  → Location: /api/v1/eligibility/batch/{jobId}

GET  /api/v1/eligibility/batch/{jobId}        → status snapshot
GET  /api/v1/eligibility/batch/{jobId}/result → CSV result download
```

- CSV format: header must contain `memberId` or `subscriberId`, plus
  `serviceDate` (ISO-8601).
- JSON format: array of `{ memberId, subscriberId, serviceDate }`.
- Hard cap: **10,000 rows per submission** (`BatchEligibilityService.MaxRows`).

### Execution model

| Rows         | Path                                                                 |
| ------------ | -------------------------------------------------------------------- |
| ≤ 100        | Inline during the POST. Client still receives a jobId + polling URL. |
| 101 – 10,000 | Queued onto `IBatchQueue`; `BatchEligibilityQueueWorker` drains it.  |

The 100-row threshold is `BatchEligibilityService.InlineThreshold`. In
production `IBatchQueue` binds to an Azure Service Bus queue; for unit tests
and single-instance deployments, the default binding is `InMemoryBatchQueue`
(backed by `System.Threading.Channels`).

### Reuse

Every row is verified through `EligibilityAdapterFactory.GetAdapterAsync(tenantId)`,
so batch submissions honor the **same** tenant-platform routing as live 270/271
calls — no parallel verification path exists.

### Resumability

- Each job has a stable `id` returned by the POST.
- `GET /api/v1/eligibility/batch/{jobId}` is idempotent and returns the
  current snapshot (`Queued`, `Running`, `Completed`, `Failed`, `Cancelled`)
  plus `processedRows` / `totalRows` so clients can show a progress bar.
- `ProcessJobAsync` is idempotent once a job reaches `Completed` — redelivered
  Service Bus messages are a no-op.
- Row-level errors are captured in the result CSV (`error` column). The first
  ~20 errors are also summarised on the job object for quick triage.

### Result file

The result is a CSV with columns:

```
rowNumber, subscriberId, serviceDate, isEligible, statusCode,
planId, groupNumber, coverageLevel, coverageBeginDate, coverageEndDate, error
```

It's stored in `IBatchJobStore` (in-memory by default, pluggable to MongoDB
or Cosmos DB + blob storage) and served via `GET /batch/{jobId}/result` once
`Status = Completed`.

## 3. Multi-tenancy

- All reads and writes are keyed by tenantId inside `IBatchJobStore` — jobs
  from tenant A are never visible to tenant B.
- `TemporalEligibilityService` forwards the tenant id to `coverage-service` via
  the `X-Tenant-ID` header; coverage-service's own middleware then enforces
  isolation on its end.
- The Service Bus queue message carries `TenantId` so the consumer resolves the
  right adapter before verification.

## 4. Storage modes

Batch storage is resolved at startup by
`BatchEligibilityServiceCollectionExtensions.AddBatchEligibilityStorage`
from the `BatchEligibility:StorageMode` config value:

| Mode       | Job store                                   | Queue                         | Notes                                                                             |
| ---------- | ------------------------------------------- | ----------------------------- | --------------------------------------------------------------------------------- |
| InMemory   | `InMemoryBatchJobStore`                     | `InMemoryBatchQueue`          | Dev only. Jobs do not survive restarts. No cross-replica visibility. Warn on boot. |
| Persistent | `BatchJobStore` (+ Blob for payloads)       | `ServiceBusBatchQueue`        | Production. `BatchJobStore` runs against either MongoDB or Cosmos DB for NoSQL via a pluggable `IBatchJobContainer` (`MongoContainerAdapter` / `CosmosContainerAdapter`) — MongoDB is checked first. Required connection strings: MongoDB or Cosmos, Blob Storage, Service Bus. |
| Auto       | Persistent when a document store (Mongo or Cosmos) + Blob + Service Bus are all set; InMemory in dev when they're not; throws at startup otherwise. |

Never registers both in-memory and persistent implementations simultaneously.

### 4.1 Multi-replica behavior

- **InMemory** is single-instance only. `GET /batch/{jobId}` on a different
  pod returns 404; queued messages stay on the pod that received them.
- **Persistent** gives true multi-replica semantics:
  - Job state lives in MongoDB or Cosmos DB (`batch-jobs`
    container/collection, partitioned/scoped by `tenantId`) → any pod can
    read/write any job.
  - Queue delivery comes from Service Bus with `MaxConcurrentCalls = 4`,
    `AutoCompleteMessages = false`. Messages complete on handler success,
    abandon on failure; Service Bus auto-DLQs after `MaxDeliveryCount = 10`.
  - Result and input CSVs over `BatchEligibility:InlineMaxBytes` (default
    1 MB) are written to Azure Blob Storage at
    `{tenantId}/{jobId}/{kind}.csv`. Reads always go back through the
    service — no SAS URIs are minted.
- **TTL**: the Cosmos container is provisioned with `defaultTtl = 7 days`
  (see `scripts/azure/provision-batch-eligibility.sh`); `MongoContainerAdapter`
  creates an equivalent TTL index on `expiresAt`. The blob container has a
  matching 7-day lifecycle rule. The in-memory store's 24-hour `Evict()`
  sweep applies only in dev.

### 4.2 Streaming CSV

The queued path (>100 rows) never materializes the full row set in memory:

- **Submit**: `StreamingCsvParser` consumes the incoming request via
  `TextReader` and, as rows arrive, `StreamingCsvWriter` writes the
  normalized input CSV directly to `IBatchJobStore.SaveResultStreamAsync`.
  Input is then stored inline (&lt; `InlineMaxBytes`) or in blob storage.
- **Process**: `BatchEligibilityService.ProcessJobAsync` opens the input
  via `OpenResultStreamAsync` and pulls rows through the parser in
  chunks of 100 (`ProcessingChunkSize`). Each chunk calls through the
  existing `EligibilityAdapterFactory` adapter and writes one result row
  at a time via `StreamingCsvWriter`. Peak working set stays bounded by
  chunk size and the small parser/writer buffers (&lt; 16 KB each).
- **Inline path** (≤100 rows): unchanged. The small bounded cost is
  acceptable and keeps the request path simple.

### 4.3 Provisioning

Run `scripts/azure/provision-batch-eligibility.sh` once per environment
with the following env vars set:

```
COSMOS_ACCOUNT, COSMOS_DB (default: cho)
SERVICEBUS_NAMESPACE
STORAGE_ACCOUNT
RESOURCE_GROUP
```

Creates:
- Cosmos container `batch-jobs`, PK `/tenantId`, TTL 7 days.
- Service Bus queue `batch-eligibility`, `MaxDeliveryCount=10`, DLQ on expiry.
- Blob container `batch-eligibility` with a 7-day delete lifecycle rule.

## 5. Follow-ups

- Replace `StubAccumulatorClient` with the real accumulator-service client
  once prompt 5.3 lands.
- Promote the `IBatchQueueSender` / `IBatchQueueProcessor` abstractions to
  `shared/CloudHealthOffice.Infrastructure` if another service adopts
  Service Bus (currently only batch eligibility uses it).
