> **Note:** This document references Azure Logic Apps, which were the original orchestration runtime. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details.

# Multi-Tenant + Environment Folder Structure Proposal

## Current State (Problems)

### SFTP Folders
```
/home/logicapp/
├── upload/
│   ├── 275/  ← ALL tenants share same folder ❌
│   └── 278/  ← ALL tenants share same folder ❌
└── download/
    └── 277/  ← ALL tenants share same folder ❌
```

**Issues:**
- No tenant isolation in SFTP
- No environment separation (prod/preprod/dev mixing)
- Risk of cross-tenant data leakage
- Cannot route files to correct tenant automatically

### Blob Storage
```
hipaa-attachments/raw/275/yyyy/MM/dd/file.edi  ← No tenant or env ❌
hipaa-attachments/raw/278/yyyy/MM/dd/file.edi  ← No tenant or env ❌
```

**Issues:**
- All tenants' files in same folder
- No environment separation
- Cannot filter by tenant in Azure Portal

### Cosmos DB
```
✅ Already partitioned by /tenantId (GOOD!)
❌ No environment separation
```

---

## Proposed Structure

### 1. SFTP Folder Structure (Recommended: Tenant → Trading Partner → Environment)

```
/home/{tenantId}/{tradingPartner}/{environment}/
├── inbound/
│   ├── 275/  ← Clinical attachments FROM trading partner
│   ├── 278/  ← Prior auth requests FROM trading partner
│   └── 837/  ← Claims FROM trading partner
└── outbound/
    ├── 277/  ← RFAI responses TO trading partner
    └── 999/  ← Acknowledgments TO trading partner

Example:
/home/bcbs-florida/availity/prod/inbound/275/
/home/bcbs-florida/availity/prod/outbound/277/
/home/bcbs-florida/change-healthcare/prod/inbound/278/
/home/bcbs-florida/change-healthcare/preprod/inbound/275/
/home/uhc-texas/optum/prod/inbound/278/
/home/test-tenant/clearinghouse-sandbox/dev/inbound/278/
```

**Benefits:**
- **Tenant isolation**: Each health plan has dedicated space
- **Trading partner separation**: Multiple clearinghouses per tenant
- **Environment isolation**: Prod/preprod/dev never mix
- **Real-world mapping**: Matches actual EDI relationships
- **Easy provisioning**: Add new trading partner = mkdir
- **Per-partner credentials**: Different SFTP users per trading partner
- **Audit trail**: Clear who sent what file

**Trading Partner Examples:**
- `availity` - Availity LLC (largest clearinghouse)
- `change-healthcare` - Change Healthcare/Emdeon
- `optum` - Optum/United Healthcare
- `relay-health` - Relay Health/McKesson
- `waystar` - Waystar (formerly ZirMed)
- `clearinghouse-sandbox` - Test/development partner

### 2. Alternative Structure (Environment-First - NOT Recommended)

```
/home/{environment}/{tenantId}/{tradingPartner}/
├── inbound/275/
└── outbound/277/

Example:
/home/prod/bcbs-florida/availity/inbound/275/
/home/preprod/bcbs-florida/availity/inbound/275/
```

**Why NOT recommended:**
- Harder to manage tenant lifecycle
- Trading partner changes require multiple folder updates
- Less intuitive for tenant onboarding

### 3. Blob Storage Structure

```
{environment}/{tenantId}/{tradingPartner}/{stage}/{transactionType}/yyyy/MM/dd/{filename}

Examples:
prod/bcbs-florida/availity/raw/275/2026/02/07/attachment-12345.edi
prod/bcbs-florida/availity/processed/275/2026/02/07/attachment-12345.json
prod/bcbs-florida/change-healthcare/raw/278/2026/02/07/prior-auth-67890.edi
prod/uhc-texas/optum/raw/275/2026/02/07/attachment-11111.edi
preprod/bcbs-florida/availity/raw/275/2026/02/07/test-attachment.edi
dev/test-tenant/clearinghouse-sandbox/raw/278/2026/02/07/sample.edi

Archive files:
prod/bcbs-florida/availity/archive/275/2026/02/attachment-12345.edi.zip
prod/bcbs-florida/availity/archive/278/2026/01/prior-auth-*.edi.zip
```

**Benefits:**
- **Container-level isolation**: `cho-prod`, `cho-preprod`, `cho-dev`
- **Trading partner traceability**: Know which clearinghouse sent the file
- **Retention policies per partner**: Some partners require 7-year retention
- **SAS tokens scoped to tenant + partner**: Secure file sharing
- **Lifecycle management**: Archive by trading partner (e.g., Availity files after 90 days)
- **Audit compliance**: "Show all files from Change Healthcare to BCBS Florida in Jan 2026"

### 4. Service Bus Topics/Queues

```
{environment}-{transaction}-{tenantId}

Examples:
prod-edi-275-bcbs-florida
prod-edi-278-bcbs-florida
prod-rfai-277-uhc-texas
preprod-edi-275-bcbs-florida
dev-edi-278-test-tenant
```

**Alternative (Shared Topics with Tenant Filter):**
```
Topic: prod-edi-275
Subscription: bcbs-florida (filter: tenantId = 'bcbs-florida')
Subscription: uhc-texas (filter: tenantId = 'uhc-texas')
```

### 5. Cosmos DB Containers

**Option A: Environment-Separated Databases**
```
Database: cho-prod
  Containers: Authorizations (partitioned by /tenantId)
             Attachments (partitioned by /tenantId)
             Members (partitioned by /tenantId)

Database: cho-preprod
  Containers: Authorizations (partitioned by /tenantId)
             ...

Database: cho-dev
  Containers: Authorizations (partitioned by /tenantId)
             ...
```

**Option B: Single Database with Environment Field**
```
Database: CloudHealthOffice
  Containers: 
    Authorizations (partition: /environment_tenantId)
      - Composite key: "prod_bcbs-florida"
      - Composite key: "preprod_bcbs-florida"
```

---

## Recommended Implementation

### Phase 1: SFTP + Blob Storage (Immediate)

**SFTP Structure:**
```
/home/{tenantId}/{tradingPartner}/{environment}/inbound/{type}/
/home/{tenantId}/{tradingPartner}/{environment}/outbound/{type}/
```

**Blob Container Structure:**
```
Container: cho-{environment}
Path: {tenantId}/{tradingPartner}/{stage}/{type}/yyyy/MM/dd/

Stages: raw, processed, archive, error
```

**Example Full Paths:**
```
SFTP: /home/bcbs-florida/availity/prod/inbound/275/attachment-001.edi
Blob: prod/bcbs-florida/availity/raw/275/2026/02/07/attachment-001.edi
      prod/bcbs-florida/availity/processed/275/2026/02/07/attachment-001.json
      prod/bcbs-florida/availity/archive/275/2026/02/attachment-001.edi.zip
```

### Phase 2: Cosmos DB Separation (Medium Priority)

**Database per Environment:**
- `cho-prod` → Production tenant data
- `cho-preprod` → Pre-production testing
- `cho-dev` → Development/sandbox

### Phase 3: Service Bus Topics (Future)

**Hybrid Approach:**
- Shared topics per environment
- Tenant-specific subscriptions with SQL filters
- Example: `prod-edi-275` topic → 50 tenant subscriptions

---

## Migration Strategy

### 1. Update SFTP Server Deployment

```yaml
# k8s/sftp-server-deployment.yaml
stringData:
  users.conf: |
    # Format: username:password:uid:gid:home_directory
    
    # BCBS Florida + Availity (Production)
    bcbs-fl-availity-prod:SecurePass123:1001:1001:bcbs-florida/availity/prod
    
    # BCBS Florida + Change Healthcare (Production)
    bcbs-fl-changehc-prod:SecurePass456:1002:1002:bcbs-florida/change-healthcare/prod
    
    # UHC Texas + Optum (Production)
    uhc-tx-optum-prod:SecurePass789:1003:1003:uhc-texas/optum/prod
    
    # BCBS Florida + Availity (Preprod)
    bcbs-fl-availity-preprod:PreprodPass123:2001:2001:bcbs-florida/availity/preprod
    
    # Test Tenant + Sandbox Clearinghouse (Dev)
   tenantId": {
    "type": "String",
    "defaultValue": "bcbs-florida"
  },
  "tradingPartnerId": {
    "type": "String",
    "defaultValue": "availity"
  },
  "environment": {
    "type": "String",
    "defaultValue": "prod"
  },
  "sftp_inbound_folder": {
    "value": "@concat('/', parameters('tenantId'), '/', parameters('tradingPartnerId'), '/', parameters('environment'), '/inbound/275')"
  },
  "blob_raw_folder": {
    "value": "@concat(parameters('environment'), '/', parameters('tenantId'), '/', parameters('tradingPartnerId'), '/raw/275')"
  },
  "x12_sender_id": {
    "value": "@parameters('tradingPartner_SenderId')",
    "description": "X12 ISA06 - Interchange Sender ID (e.g., Availity: '030240928')"
  },
  "x12_receiver_id": {
    "value": "@parameters('tenantId_ReceiverId')",
    "description": "X12 ISA08 - Interchange Receiver ID (health plan)"
  }
}
```

**Trading Partner Metadata (ConfigMap or Cosmos DB):**
```json
{
  "tradingPartnerId": "availity",
  "tradingPartnerName": "Availity LLC",
  "x12SenderId": "030240928",
  "sftpUsername": "bcbs-fl-availity-prod",
  "contactEmail": "edi-support@availity.com",
  "supportedTransactions": ["275", "276", "277", "278", "837"],
  "testEndpoint": "sftp-test.availity.com",
  "prodEndpoint": "sftp.availity.com"xamples: `bcbs-fl-availity-prod`, `uhc-tx-optum-preprod`

### 2. Update Logic Apps Parameters

```json
{
  "sftp_inbound_folder": {
    "value": "@concat('/', parameters('tenantId'), '/', parameters('environment'), '/inbound/275')"
  },
  "blob_raw_folder": {
    "value": "@concat(parameters('environment'), '/', parameters('tenantId'), '/raw/275')"
  }
}
```

### 3. Add Environment Detection Middleware

```csharp
// services/shared/Middleware/EnvironmentMiddleware.cs
public class EnvironmentMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") 
            ?? "Development";
        
        context.Items["Environment"] = environment.ToLower();
        
        await next(context);
    }
}
```

### 4. Update Repository Layer

```csharp
// services/authorization-service/Repositories/AuthorizationRepository.cs
public class AuthorizationRepository
{
    private string GetPartitionKey()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        var environment = _httpContextAccessor.HttpContext?.Items["Environment"]?.ToString() ?? "prod";
        
        return $"{environment}_{tenantId}"; // e.g., "prod_bcbs-florida"
    }
}
```

---

## Security Benefits

### 1. **Defense in Depth**
- Layer 1: SFTP user credentials (tenant-specific)
- Layer 2: SFTP chroot (tenant can't see other folders)
- Layer 3: Blob container SAS tokens (tenant-scoped)
- Layer 4: Cosmos DB partition key (tenant isolation)
- Layer 5: JWT claims validation (tenant_id in token)

### 2. **Compliance (HIPAA/SOC 2)**
- Clear audit trail per tenant/environment
- No cross-tenant data leakage
- Environment separation prevents prod data in dev
- Easy to demonstrate isolation in audits

### 3. **Incident Response**
- Contain breach to single tenant/environment
- Roll back specific tenant data
- Disable compromised tenant without affecting others

---

## Cost Considerations

### Current (Single-Tenant Structure)
- 1 SFTP server: ~$50/month
- 1 Blob container: ~$20/month (100 GB)
- 1 Cosmos DB: ~$100/month (400 RU/s)
- **Total: ~$170/month**

### Proposed (Multi-Tenant Structure)
- 1 SFTP server (with tenant folders): ~$50/month
- 3 Blob containers (prod/preprod/dev): ~$25/month (120 GB total)
- 3 Cosmos DB databases: ~$300/month (3 × 400 RU/s)
- **Total: ~$375/month**

**Optimization:**
- Use single Cosmos DB with composite partition keys: **$100/month**
- Final cost: **~$175/month** (same as current!)

---

## Rollout Plan

### Week 1: SFTP + Blob Storage
1. Update SFTP deployment with tenant folders
2. Provision initial tenants (bcbs-florida, uhc-texas, test-tenant)
3. Update Logic Apps to use tenant-specific paths
4. Test file upload/download for each tenant

### Week 2: Cosmos DB Environment Separation
1. Create `cho-preprod` and `cho-dev` databases
2. Update connection strings in Kubernetes secrets
3. Deploy services with environment-aware partition keys
4. Migrate test data to preprod/dev
Trading Partner Management

### Trading Partner Registry (Cosmos DB Container: `TradingPartners`)

```json
{
  "id": "availity-bcbs-florida-prod",
  "partitionKey": "bcbs-florida",
  "tenantId": "bcbs-florida",
  "tradingPartnerId": "availity",
  "environment": "prod",
  "partnerName": "Availity LLC",
  "partnerType": "Clearinghouse",
  
  "x12Config": {
    "senderId": "030240928",
    "receiverId": "BCBSFL001",
    "isaQualifier": "ZZ",
    "testIndicator": "P"
  },
  
  "sftpConfig": {
    "enabled": true,
    "username": "bcbs-fl-availity-prod",
    "host": "sftp-service.cho-sftp.svc.cluster.local",
    "port": 22,
    "paths": {
      "inbound": {
        "base": "/bcbs-florida/availity/prod/inbound",
        "275": "/bcbs-florida/availity/prod/inbound/275",
        "276": "/bcbs-florida/availity/prod/inbound/276",
        "278": "/bcbs-florida/availity/prod/inbound/278",
        "837": "/bcbs-florida/availity/prod/inbound/837"
      },
      "outbound": {
        "base": "/bcbs-florida/availity/prod/outbound",
        "277": "/bcbs-florida/availity/prod/outbound/277",
        "999": "/bcbs-florida/availity/prod/outbound/999",
        "824": "/bcbs-florida/availity/prod/outbound/824"
      }
    }
  },
  
  "blobConfig": {
    "containerName": "cho-prod",
    "paths": {
      "raw": "prod/bcbs-florida/availity/raw/{transactionType}/{yyyy}/{MM}/{dd}",
      "processed": "prod/bcbs-florida/availity/processed/{transactionType}/{yyyy}/{MM}/{dd}",
      "archive": "prod/bcbs-florida/availity/archive/{transactionType}/{yyyy}/{MM}",
      "error": "prod/bcbs-florida/availity/error/{transactionType}/{yyyy}/{MM}/{dd}"
    },
    "retentionPolicies": {
      "raw": 90,
      "processed": 365,
      "archive": 2555,
      "error": 180
    }
  },
  
  "transactionTypes": ["275", "276", "277", "278", "837", "835", "999", "824"],
  
  "contactInfo": {
    "email": "edi-support@availity.com",
    "phone": "1-800-AVAILITY",
    "technicalContact": "John Smith",
    "escalationEmail": "edi-escalation@availity.com"
  },
  
  "businessRules": {
    "maxFileSize": 10485760,
    "allowedFileTypes": [".edi", ".x12", ".txt"],
    "pollingInterval": "PT5M",
    "processingTimeout": "PT10M",
    "maxRetries": 3,
    "retryBackoff": "PT1M"
  },
  
  "status": "Active",
  "createdAt": "2026-01-15T00:00:00Z",
  "lastTestedAt": "2026-02-07T12:00:00Z",
  "lastSuccessfulTransmission": "2026-02-07T14:30:00Z"
}
```

**Path Template Variables:**
- `{tenantId}` → `bcbs-florida`
- `{tradingPartnerId}` → `availity`
- `{environment}` → `prod`, `preprod`, `dev`
- `{transactionType}` → `275`, `276`, `277`, `278`, `837`, etc.
- `{yyyy}` → `2026`
- `{MM}` → `02`
- `{dd}` → `07`

### Benefits of Trading Partner Dimension

1. **Multiple Clearinghouses per Tenant**
   - BCBS Florida → Availity (primary) + Change Healthcare (backup)
   - UHC Texas → Optum (in-network) + Relay Health (out-of-network)

2. **Trading Partner-Specific Configuration**
   - Each partner has unique X12 sender/receiver IDs
   - Different file naming conventions
   - Varying processing SLAs
   - Custom retry logic per partner

3. **Failover & Redundancy**
   - Primary: `bcbs-florida/availity/prod/`
   - Failover: `bcbs-florida/change-healthcare/prod/`
   - Switch by changing Logic App parameter

4. **Testing & Migration**
   - Test new partner: `bcbs-florida/new-clearinghouse/preprod/`
   - Side-by-side comparison with existing partner
   - Gradual migration (route 10% → 50% → 100%)

5. **Compliance & Auditing**
   - "Show all 275s from Availity to BCBS Florida in Q1 2026"
   - Track processing times per trading partner
   - SLA monitoring per partner relationship

## Questions to Decide

1. **SFTP folder hierarchy:** Include trading partner?
   - Recommendation: **YES** → `/home/{tenantId}/{tradingPartner}/{environment}/`
   
2. **Trading partner metadata:** Cosmos DB or ConfigMap?
   - Recommendation: **Cosmos DB** (dynamic, queryable, UI-editable)
   
3. **Cosmos DB strategy:** Separate databases or composite partition keys?
   - Recommendation: **Composite keys** → `/environment_tenantId_tradingPartnerId`
   
4. **Service Bus:** Per-tenant topics or shared topics with filters?
   - Recommendation: **Shared topics** with filters on `tenantId` and `tradingPartnerId`
   
5. **SFTP folder hierarchy:** Tenant-first or environment-first?
   - Recommendation: **Tenant-first** (`/home/{tenantId}/{environment}/`)
   
2. **Cosmos DB strategy:** Separate databases or composite partition keys?
   - Recommendation: **Composite keys** (cost-effective, single database)
   
3. **Service Bus:** Per-tenant topics or shared topics with filters?
   - Recommendation: **Shared topics** (scales better, easier management)
   
4. **Environment naming:** prod/preprod/dev or production/staging/development?
   - Recommendation: **Short names** (prod/preprod/dev) for path efficiency

---

## Next Steps

1. **Approve structure**: Choose SFTP hierarchy (tenant-first recommended)
2. **Update SFTP deployment**: Add tenant-specific users and folders
3. **Update Logic Apps**: Add `tenantId` and `environment` parameters
4. **Update backend services**: Add environment detection middleware
5. **Test with 2-3 pilot tenants**: Validate isolation works correctly
6. **Document onboarding process**: How to provision new tenant/environment

Would you like me to start implementing any of these changes?
