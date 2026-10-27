> **Obsolete (ADR 017).** The native Cosmos DB SDK (SQL API) path this guide
> describes was removed in remediation Phase 5b. Every service stores its data
> in MongoDB; in Azure that is Azure DocumentDB (MongoDB vCore), provisioned by
> `infrastructure/azure/modules/documentdb-mongo-cluster.bicep` via
> `enableDocumentDb` in `main.bicep`, with `MongoDb--ConnectionString` in Key
> Vault (include `retrywrites=false`). The text below is kept for history only.

# Cosmos DB Integration - Deployment Guide

## Overview

Cloud Health Office now uses **Azure Cosmos DB** for persistent storage of enrollment and claims data. This document outlines the deployment and configuration of Cosmos DB integration for the 834 Enrollment Import Service and 837 Claims Service.

## Infrastructure Status

### Cosmos DB Account
- **Name**: `your-cosmos-account`
- **Region**: West US 2
- **Consistency Level**: Session
- **Type**: GlobalDocumentDB

### Database
- **Name**: `CloudHealthOffice`
- **Throughput**: 400 RU/s (shared across containers)

### Containers

| Container | Partition Key | Throughput | Purpose |
|-----------|--------------|------------|---------|
| **Members** | `/id` | 400 RU/s | 834 enrollment subscriber and dependent data |
| **Coverage** | `/id` | 400 RU/s | 834 enrollment coverage records (health, dental, vision) |
| **Sponsors** | `/id` | 400 RU/s | 834 enrollment sponsor information |
| **Claims** | `/id` | 400 RU/s | 837 professional/institutional/dental claims |

## Deployed Services

### ✅ 834 Enrollment Import Service

**Status**: Deployed and tested  
**Namespace**: `cloudhealthoffice`  
**Pods**: 2 replicas (auto-scaling 2-10 based on CPU 70%/Memory 80%)  
**Image**: `ghcr.io/aurelianware/cloudhealthoffice-enrollment-import-service:latest`

**Configuration**:
```yaml
CosmosDb__Endpoint: https://your-cosmos-account.documents.azure.com:443/
CosmosDb__DatabaseName: CloudHealthOffice
CosmosDb__MembersContainer: Members
CosmosDb__CoverageContainer: Coverage
```

**Test Results**:
- ✅ Successfully imported 1 subscriber + 1 dependent
- ✅ Created 2 coverage records (Health + Dental)
- ✅ Duplicate detection working (maintenance type 021)
- ✅ Multi-tenant isolation verified

### ✅ 837 Claims Service

**Status**: Deployed and tested  
**Namespace**: `cloudhealthoffice`  
**Pods**: 3 replicas (auto-scaling 3-20 based on CPU 70%/Memory 80%)  
**Image**: `ghcr.io/aurelianware/cloudhealthoffice-claims-service:latest`

**Configuration**:
```yaml
CosmosDb__Endpoint: https://your-cosmos-account.documents.azure.com:443/
CosmosDb__DatabaseName: CloudHealthOffice
CosmosDb__ContainerName: Claims
```

**Test Results**:
- ✅ Successfully submitted professional claim (837P)
- ✅ Successfully submitted institutional claim (837I)
- ✅ Professional: 3 service lines with CPT codes and diagnosis pointers
- ✅ Institutional: 5 service lines with revenue codes (ICU, cardiac cath, labs, pharmacy)
- ✅ Total charges: $350 (professional), $27,650 (institutional)
- ✅ Multi-tenant isolation verified
- ✅ Claims retrieval from Cosmos DB working

**Sample Professional Claim (837P)**:
```json
{
  "claimNumber": "CLM2025010700001",
  "claimType": 1,
  "memberId": "BSCA123456789",
  "totalChargeAmount": 350.00,
  "placeOfServiceCode": "11",
  "diagnosisCodes": [
    {"code": "E11.9", "description": "Type 2 diabetes mellitus"},
    {"code": "I10", "description": "Essential hypertension"}
  ],
  "claimLines": [
    {"procedureCode": "99213", "chargeAmount": 150.00},
    {"procedureCode": "80053", "chargeAmount": 75.00},
    {"procedureCode": "83036", "chargeAmount": 125.00}
  ]
}
```

**Sample Institutional Claim (837I)**:
```json
{
  "claimNumber": "CLM2025010700002",
  "claimType": 2,
  "facilityName": "City General Hospital - Main Campus",
  "totalChargeAmount": 27650.00,
  "placeOfServiceCode": "21",
  "diagnosisCodes": [
    {"code": "I21.09", "description": "ST elevation myocardial infarction"},
    {"code": "I10", "description": "Essential hypertension"},
    {"code": "E11.9", "description": "Type 2 diabetes mellitus"}
  ],
  "claimLines": [
    {"lineNumber": 1, "revenueCode": "0200", "chargeAmount": 8500.00, "description": "ICU room and board"},
    {"lineNumber": 2, "revenueCode": "0730", "chargeAmount": 450.00, "description": "ECG"},
    {"lineNumber": 3, "revenueCode": "0481", "chargeAmount": 4200.00, "description": "Cardiac catheterization"},
    {"lineNumber": 4, "revenueCode": "0300", "chargeAmount": 600.00, "description": "Laboratory"},
    {"lineNumber": 5, "revenueCode": "0250", "chargeAmount": 1500.00, "description": "Pharmacy"}
  ]
}
```

## Kubernetes Secrets

The Cosmos DB credentials are stored in Kubernetes secret `database-secret` in the `cloudhealthoffice` namespace:

```bash
kubectl create secret generic database-secret \
  --from-literal=endpoint='https://your-cosmos-account.documents.azure.com:443/' \
  --from-literal=key='<primary-key>' \
  -n cloudhealthoffice
```

## System.Text.Json Serialization

Both services use a custom `CosmosSystemTextJsonSerializer` class to ensure proper JSON serialization with System.Text.Json (required for .NET 8.0 compatibility):

```csharp
public class CosmosSystemTextJsonSerializer : CosmosSerializer
{
    private readonly JsonObjectSerializer _systemTextJsonSerializer;

    public CosmosSystemTextJsonSerializer() : this(new JsonSerializerOptions())
    {
    }

    public CosmosSystemTextJsonSerializer(JsonSerializerOptions jsonSerializerOptions)
    {
        _systemTextJsonSerializer = new JsonObjectSerializer(jsonSerializerOptions);
    }

    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            if (stream.CanSeek && stream.Length == 0)
            {
                return default!;
            }
            return (T)_systemTextJsonSerializer.Deserialize(stream, typeof(T), default)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var streamPayload = new MemoryStream();
        _systemTextJsonSerializer.Serialize(streamPayload, input, typeof(T), default);
        streamPayload.Position = 0;
        return streamPayload;
    }
}
```

**Required Package**: `Azure.Core` (version 1.35.0+)

## Model Annotations

Models must include the `[JsonPropertyName("id")]` attribute for Cosmos DB compatibility:

```csharp
using System.Text.Json.Serialization;

public class Claim
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    // Other properties...
}
```

## Partition Key Strategy

All containers use `/id` as the partition key for consistency and simplicity. Multi-tenant isolation is achieved through the `TenantId` property in all documents, with application-level filtering in queries and read operations.

**Benefits**:
- Consistent partition key strategy across all containers
- Simplified CRUD operations (no need to specify partition key separately)
- Natural document-level isolation
- Easy to understand and maintain

**Trade-offs**:
- Requires tenant ID verification in read operations
- Cannot leverage Cosmos DB partition-level isolation for tenants
- Queries must include `WHERE tenantId = @tenantId` filter

## Testing

### Test Enrollment Payload

See `test-enrollment-payload.json` for a complete 834 enrollment import test payload.

### Test Claim Payload

See `test-claim-payload.json` for a complete 837P professional claim test payload.

See `test-claim-institutional-payload.json` for a complete 837I institutional claim test payload.

### Port Forwarding for Local Testing

```bash
# Enrollment Import Service
kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 8081:80

# Claims Service
kubectl port-forward -n cloudhealthoffice svc/claims-service 8082:80
```

### Test Commands

```bash
# Test enrollment import
curl -X POST http://localhost:8081/api/v1/enrollments \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: test-tenant-001" \
  -d @tprofessional claim (837P)
curl -X POST http://localhost:8082/api/Claims \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: test-tenant-001" \
  -d @test-claim-payload.json

# Test institutional claim (837I)
curl -X POST http://localhost:8082/api/Claims \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: test-tenant-001" \
  -d @test-claim-institutionalpe: application/json" \
  -H "X-Tenant-ID: test-tenant-001" \
  -d @test-claim-payload.json

# Retrieve claim by ID
curl -H "X-Tenant-ID: test-tenant-001" \
  http://localhost:8082/api/Claims/{claim-id}
```

## Monitoring

### Cosmos DB Metrics

Monitor the following in Azure Portal:

- **Request Units (RU/s)**: Ensure within provisioned throughput
- **Storage**: Track document count and data size
- **Latency**: Monitor P99 latency for read/write operations
- **Availability**: Ensure 99.99% SLA compliance

### Service Health

```bash
# Check pod status
kubectl get pods -n cloudhealthoffice -l app=enrollment-import-service
kubectl get pods -n cloudhealthoffice -l app=claims-service

# Check logs
kubectl logs -n cloudhealthoffice -l app=enrollment-import-service --tail=100
kubectl logs -n cloudhealthoffice -l app=claims-service --tail=100

# Check HPA scaling
kubectl get hpa -n cloudhealthoffice
```

## benefit-plan-service: Cosmos deployments created before the D-31 fix

Applies only to benefit-plan-service running on the Cosmos DB **SQL (native) API**
(`Database:Provider=CosmosDb`). Deployments on MongoDB, or on Cosmos DB for MongoDB
(reached with a `MongoDb:ConnectionString`), are unaffected: on that path the service
never registered the faulty client, and MongoDB documents are written by the BSON
serializer, which this fix does not touch.

**What changed.** Before D-31, benefit-plan-service registered its own `CosmosClient` with
the SDK's default Newtonsoft serializer, overriding the shared client from
`AddChoDatabase`. Every repository the service resolves wrote PascalCase documents through
it: `"Id"` instead of `"id"`, `"TenantId"` instead of `"tenantId"`, enums as numbers, nulls
stored as `null`. The service now uses the shared `CosmosSystemTextJsonSerializer`
(camelCase, enums as camelCase strings, null properties omitted), which is what its
queries and the containers' partition keys expect. The fixed service does not read the
old shape correctly: a PascalCase document has no `id`, its `tenantId` / `stateCode` and
other filtered properties are undefined to camelCase queries, and numeric enums no longer
match.

In practice the old client's writes were rejected: either 400 "Document does not contain
an id field", or a partition-key mismatch, because the document carried `TenantId` /
`StateCode` while the container's path is `/tenantId` / `/stateCode`. Old-shape documents
can therefore exist only in a container that was created with a PascalCase partition-key
path (`/TenantId`, `/StateCode`); every other old write was refused. Most such deployments
hold no benefit-plan documents at all. Check before deciding anything.

**Containers involved** (names are the configuration defaults; partition key paths must
be exactly these, lower camelCase). Several are shared with other services, which already
use the shared serializer and must keep running against them:

| Container | Written / read by | Partition key |
|---|---|---|
| `BenefitPlans` (`CosmosDb:ContainerName`) | benefit-plan-service (BenefitPlanRepository) | `/tenantId` |
| `PlanVersionTransitions` | benefit-plan-service | `/tenantId` |
| `ServiceCategoryMappings` | benefit-plan-service | `/tenantId` |
| `Accumulators` | benefit-plan-service (BenefitEngine AccumulatorRepositoryCosmos) | `/tenantId` |
| `FeeSchedules`, `ProviderContracts` | benefit-plan-service (FeeScheduleEngine) | `/tenantId` |
| `NcciPairs`, `MueEntries`, `NcciVersion`, `NcciLoadLedger` | benefit-plan-service and **claims-service** (NcciEngine; created at startup) | `/tenantId` |
| `prior-auth-rules` | benefit-plan-service and **fhir-service** (PriorAuthRuleEngine) | `/stateCode` |
| `enrollment-cache` | benefit-plan-service and **fhir-service** (ProviderEnrollmentService) | `/stateCode` |
| `enrollment-tenant-config` | benefit-plan-service and **fhir-service** (ProviderEnrollmentService) | `/tenantId` |

A container created with `/TenantId`, `/StateCode` or `/id` must be re-created with the
path above; Cosmos cannot change a container's partition key.

**Detect the old shape.** Cosmos property names are case-sensitive, so in Data Explorer run,
per container:

```sql
SELECT VALUE COUNT(1) FROM c WHERE IS_DEFINED(c.Id) OR IS_DEFINED(c.TenantId) OR IS_DEFINED(c.StateCode)
```

A non-zero count means documents from before the fix. `SELECT TOP 5 * FROM c WHERE IS_DEFINED(c.Id)`
shows them, and `SELECT c.id, c.TenantId, c.StateCode FROM c WHERE IS_DEFINED(c.Id) OR IS_DEFINED(c.TenantId) OR IS_DEFINED(c.StateCode)`
lists their ids and partition key values.

**Option 1: re-seed (preferred when the data can be re-created).**

1. Scale benefit-plan-service to zero.
2. For each container that holds old documents:
   - **Partition key path correct** (lower camelCase as in the table): delete only the
     documents the detection query finds, by id and partition key value (Data Explorer, or
     `DeleteItemAsync` in a one-off script). Do not drop the container: claims-service and
     fhir-service may be using it.
   - **Partition key path wrong** (`/TenantId`, `/StateCode`, `/id`): the container has to be
     dropped and re-created, e.g.
     `az cosmosdb sql container create -g <rg> -a <account> -d <database> -n BenefitPlans --partition-key-path /tenantId`.
     For a shared container (NCCI: claims-service; `prior-auth-rules`, `enrollment-*`:
     fhir-service), scale those services to zero too before dropping it, and back up after
     step 3. The NCCI containers are re-created by the service at startup.
3. Deploy the fixed service and scale it (and any service scaled down in step 2) back up. On
   startup the PA-rule seeder writes the platform prior-auth rules; check the log for
   `PriorAuthRuleEngine: seeded`.
4. Call `POST /api/v1/prior-auth-rules/seed-platform` with a PlatformAdmin token
   (`platform:admin`); it reports `seededRules` (0 if startup already seeded them).
5. Re-create the tenant data from its source: plans through the benefit-plan API (or the
   tenant-onboarding seed step), fee schedules and contracts through their imports, the
   NCCI quarter through the NCCI load endpoint. Service-category system defaults are
   re-applied by `SystemDefaultMappingSeeder` the first time each tenant is seen again;
   tenant-specific mappings must be re-entered.

**Option 2: migrate in place.** Rewrite each old document (read it, transform it, replace it
under the same partition key value, then delete the PascalCase original if its id differs):

- Every property name, at every depth (nested objects and array items included), becomes
  camelCase: first letter lower-cased (`Id` → `id`, `TenantId` → `tenantId`,
  `NetworkTiers[].TierName` → `networkTiers[].tierName`, `Column1Code` → `column1Code`).
  `NcciVersion`'s `id` was already lower-case.
- Enums stored as numbers become their member name in camelCase: `"planType": 1` →
  `"planType": "ppo"`, `"versionState": 1` → `"published"`, `"lob": 3` → `"medicaid"`,
  `"networkStatus": 0` → `"inNetwork"`, `"cobDeductibleCredit": 1` → `"naicFullCredit"`.
  The enrollment documents' `status`, `providerType` and `defaultGateMode` were already
  strings (`"Active"`, `"PhysicianMD"`) and keep their values; `supportedLobs` stays a number.
- Remove properties whose value is `null` (the service omits them; its queries treat
  missing and null alike, but removing them keeps one shape).
- Dates, numbers and string values are unchanged.
- The partition key property (`tenantId` or `stateCode`) must be present, lower camelCase,
  and equal to the partition key value the document is stored under.

## authorization-service: Cosmos deployments created before the D-18 fix

Applies only to authorization-service running on the Cosmos DB **SQL (native) API**
(`Database:Provider=CosmosDb`), container `Authorizations` (`CosmosDb:ContainerName`),
partition key `/tenantId`. Deployments on MongoDB, or on Cosmos DB for MongoDB, are
unaffected: MongoDB documents are written by the BSON serializer, which this fix does not
touch. No production deployment is known to hold Cosmos authorization data.

**What changed.** Before D-18, authorization-service registered its own `CosmosClient`
(overriding the shared one from `AddChoDatabase`) with a System.Text.Json serializer that
had no enum converter, so enums were stored as **numbers**, while every query compared them
with PascalCase strings: search by status or line of business, the summary, the SLA
watchdog's open authorizations and the retention sweep found nothing. The service now uses
the shared `CosmosSystemTextJsonSerializer` and its queries bind enum values as parameters.
In the stored document:

| Property | Before | After |
|---|---|---|
| `status` | number: 1 Submitted, 2 InReview, 3 Pended, 4 Approved, 5 Modified, 6 Denied, 7 Expired, 8 Cancelled | camelCase string: `"submitted"`, `"inReview"`, `"pended"`, `"approved"`, `"modified"`, `"denied"`, `"expired"`, `"cancelled"` |
| `lineOfBusiness` | number: 0 Unknown, 1 Commercial, 2 Medicare, 3 Medicaid, 4 Exchange, 5 TRICARE, 6 VA | `"unknown"`, `"commercial"`, `"medicare"`, `"medicaid"`, `"exchange"`, `"tricare"`, `"va"` |
| `authorizationType` | number: 0 Unknown, 1 PreAuthorization, 2 ConcurrentReview, 3 RetrospectiveReview, 4 Referral | `"unknown"`, `"preAuthorization"`, `"concurrentReview"`, `"retrospectiveReview"`, `"referral"` |

Unchanged: property names (already camelCase), `id`, `tenantId` and the partition key,
dates, numbers and strings, null properties (omitted before and after), and
`slaEscalation` and `statusHistory[].status`, which carry their own property-level
converter and stay PascalCase strings (`"Warning"`, `"Approved"`).

The fixed service still **reads** an old document (the enum converter accepts numbers), so
`GET` by id or number keeps working, but no query matches its numeric `status` or
`lineOfBusiness`: such a record is invisible to search, the summary, the SLA watchdog and
the retention sweep until it is migrated.

**When.** Run detection and migration only **after the rollout has completed**, when no pod
of the old version is left. Until then an old pod keeps writing numeric enums (every
create and update rewrites the whole document with its own serializer), so a document
migrated early can be turned back into the old shape, and a detection run would miss
documents written after it.

**Detect the old shape** (Data Explorer, `Authorizations` container):

```sql
SELECT VALUE COUNT(1) FROM c
WHERE IS_NUMBER(c.status) OR IS_NUMBER(c.lineOfBusiness) OR IS_NUMBER(c.authorizationType)
```

**Migrate in place** (ids and partition key values do not change): for each document the
query finds, replace each numeric `status`, `lineOfBusiness` and `authorizationType` with
the string in the table above, and replace the document under the same `tenantId` (or
patch just those properties with `set` operations). Touch no other property. There is no
migration tool in the repository; this is a one-off script or Data Explorer edit.
Scaling authorization-service down is not required if each write carries the document's
ETag (`If-Match`), so a concurrent write by the service is not lost; retry a 412. The SLA
watchdog escalates a migrated, overdue record on its next 15-minute tick.

## Troubleshooting

### Common Issues

**Issue**: `'id' field missing` error  
**Solution**: Ensure model has `[JsonPropertyName("id")]` attribute and CosmosSystemTextJsonSerializer is configured

**Issue**: `PartitionKey mismatch` error  
**Solution**: Verify partition key in container matches partition key used in code (should be `/id`)

**Issue**: Pods not pulling image  
**Solution**: Remove `imagePullSecrets` from deployment (images are public on GHCR)

**Issue**: ConfigMap key not found  
**Solution**: Verify ConfigMap keys match environment variable names in deployment

## Next Steps

- ✅ 834 Enrollment Import Service deployed
- ✅ 837 Claims Service deployed
- ⏳ 835 Remittance Service (coming soon)
- ⏳ 277 Claim Status Service (coming soon)
- ⏳ 278 Prior Authorization Service (coming soon)

## Related Documentation

- [DEPLOYMENT.md](DEPLOYMENT.md) - Complete deployment guide
- [ARCHITECTURE.md](ARCHITECTURE.md) - System architecture overview
- [README.md](README.md) - Platform overview
