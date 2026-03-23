# Tenant Environment Provisioning

## Overview

When a new health plan signs up via the self-service portal, the system automatically provisions:

1. **Cosmos DB tenant partition** (single database, multi-tenant via partition key)
2. **SFTP folder structure** for all environments (prod, preprod, dev)
3. **SFTP credentials** (username, password, SSH key)
4. **Environment-specific configuration** for each region

## Environment Structure

Health plans typically run multiple environments (called "regions" in QNXT/Facets):

- **Production (prod)** - Live claims processing
- **PPMO (Pre-Production Model Office)** - Testing configuration changes before prod
- **Dev/Test** - Development and integration testing

Each environment gets its own isolated SFTP folder structure and can connect to different clearinghouses/trading partners.

## SFTP Folder Structure (Multi-Environment)

```
/tenants/{tenant-id}/
├── prod/                          # Production environment
│   ├── availity/                  # Trading partner
│   │   ├── inbound/
│   │   │   ├── 837/              # Professional claims
│   │   │   ├── 835/              # ERA/remittance
│   │   │   ├── 270/              # Eligibility inquiry
│   │   │   └── ...
│   │   ├── outbound/
│   │   │   ├── 271/              # Eligibility response
│   │   │   ├── 277/              # Claim status
│   │   │   └── ...
│   │   ├── archive/
│   │   └── errors/
│   ├── change-healthcare/         # Different clearinghouse
│   └── quest-diagnostics/         # Lab partner
├── preprod/                       # PPMO - testing config changes
│   ├── availity/
│   │   ├── inbound/
│   │   └── outbound/
│   └── ...
└── dev/                           # Development/testing
    ├── sandbox/                   # Test clearinghouse
    └── ...
```

## Automatic Provisioning Flow

### 1. User Signs Up at Portal

User fills out signup form:
- Organization name
- Subscription tier
- Contact info
- Enabled modules

### 2. Tenant Creation (Signup.razor)

```csharp
var createTenantRequest = new CreateTenantRequest
{
    AzureTenantId = azureTenantId,
    OrganizationName = signupModel.OrganizationName,
    TenantDisplayName = signupModel.TenantName,
    Tier = signupModel.SubscriptionTier,
    EnabledModules = enabledModules,
    
    // NEW: Provision all environments by default
    Environments = new List<string> { "prod", "preprod", "dev" }
};

createdTenantId = await TenantService.CreateTenantAsync(createTenantRequest);
```

### 3. Backend Provisioning (TenantManagementService.cs)

```csharp
public async Task<Tenant> CreateTenantAsync(CreateTenantRequest request)
{
    var tenantId = GenerateTenantId(request.OrganizationName);
    
    // 1. Create tenant record in Cosmos DB
    var tenant = new Tenant { ... };
    var created = await _repository.CreateAsync(tenant);
    
    // 2. Provision SFTP folders for all environments
    await _sftpProvisioningService.ProvisionTenantFoldersAsync(tenantId, request.Environments);
    
    // 3. Generate SFTP credentials (stored in Key Vault)
    var sftpCredentials = await _credentialService.GenerateSftpCredentialsAsync(tenantId);
    
    // 4. Send welcome email with credentials
    await _emailService.SendWelcomeEmailAsync(tenant, sftpCredentials);
    
    return created;
}
```

### 4. SFTP Provisioning Service (New)

```csharp
public class SftpProvisioningService : ISftpProvisioningService
{
    public async Task ProvisionTenantFoldersAsync(string tenantId, List<string> environments)
    {
        // Get SFTP pod
        var podName = await GetSftpPodAsync();
        
        foreach (var env in environments)
        {
            // Create base environment directory
            await ExecuteKubectlAsync($"exec {podName} -- mkdir -p /home/tenants/{tenantId}/{env}");
            
            // Set ownership (tenant UID/GID from user config)
            var tenantUid = await GetTenantUidAsync(tenantId);
            await ExecuteKubectlAsync($"exec {podName} -- chown {tenantUid}:{tenantUid} /home/tenants/{tenantId}/{env}");
            
            // Set permissions (750 = owner read/write/execute, group read/execute, no access for others)
            await ExecuteKubectlAsync($"exec {podName} -- chmod 750 /home/tenants/{tenantId}/{env}");
        }
        
        _logger.LogInformation("Provisioned SFTP folders for tenant {TenantId}: {Environments}", 
            tenantId, string.Join(", ", environments));
    }
    
    public async Task ProvisionTradingPartnerAsync(string tenantId, string environment, 
        string partnerId, List<string> transactionTypes)
    {
        var podName = await GetSftpPodAsync();
        
        // Create trading partner directory in specific environment
        var basePath = $"/home/tenants/{tenantId}/{environment}/{partnerId}";
        
        // Create inbound folders for each transaction type
        foreach (var txn in transactionTypes)
        {
            await ExecuteKubectlAsync($"exec {podName} -- mkdir -p {basePath}/inbound/{txn}");
            await ExecuteKubectlAsync($"exec {podName} -- mkdir -p {basePath}/outbound/{txn}");
        }
        
        // Create common folders
        await ExecuteKubectlAsync($"exec {podName} -- mkdir -p {basePath}/archive");
        await ExecuteKubectlAsync($"exec {podName} -- mkdir -p {basePath}/errors");
        
        // Set ownership
        var tenantUid = await GetTenantUidAsync(tenantId);
        await ExecuteKubectlAsync($"exec {podName} -- chown -R {tenantUid}:{tenantUid} {basePath}");
    }
}
```

## Environment Configuration in Cosmos DB

Tenant records include environment-specific config:

```json
{
  "id": "bcbs-florida",
  "tenantId": "bcbs-florida",
  "organizationName": "Blue Cross Blue Shield of Florida",
  "environments": [
    {
      "name": "prod",
      "displayName": "Production",
      "status": "active",
      "tradingPartners": [
        {
          "partnerId": "availity",
          "partnerName": "Availity Clearinghouse",
          "transactionTypes": ["276", "277", "278", "837", "835"],
          "isActive": true
        },
        {
          "partnerId": "change-healthcare",
          "partnerName": "Change Healthcare",
          "transactionTypes": ["835"],
          "isActive": true
        }
      ],
      "sftpPath": "/tenants/bcbs-florida/prod"
    },
    {
      "name": "preprod",
      "displayName": "PPMO (Pre-Production Model Office)",
      "status": "active",
      "tradingPartners": [
        {
          "partnerId": "availity",
          "partnerName": "Availity Test Environment",
          "transactionTypes": ["276", "277", "278", "837"],
          "isActive": true
        }
      ],
      "sftpPath": "/tenants/bcbs-florida/preprod"
    },
    {
      "name": "dev",
      "displayName": "Development",
      "status": "active",
      "tradingPartners": [
        {
          "partnerId": "sandbox",
          "partnerName": "Test Clearinghouse",
          "transactionTypes": ["837"],
          "isActive": true
        }
      ],
      "sftpPath": "/tenants/bcbs-florida/dev"
    }
  ]
}
```

## Trading Partner Provisioning (Post-Signup)

After initial signup, tenant can add trading partners via portal:

1. Navigate to **Settings → Trading Partners**
2. Click **Add Trading Partner**
3. Fill out form:
   - Partner name (Availity, Change Healthcare, etc.)
   - Environment (prod, preprod, dev)
   - Transaction types (837, 835, 270, etc.)
4. Portal calls API:

```bash
POST /api/v1/tenants/{tenantId}/trading-partners
{
  "environment": "prod",
  "partnerId": "availity",
  "partnerName": "Availity Clearinghouse",
  "transactionTypes": ["276", "277", "278", "837", "835"]
}
```

5. Backend provisions SFTP folders:

```csharp
await _sftpProvisioningService.ProvisionTradingPartnerAsync(
    tenantId, 
    "prod", 
    "availity", 
    new List<string> { "276", "277", "278", "837", "835" }
);
```

## SFTP Credentials

Each tenant gets one set of SFTP credentials that works across all environments:

- **Username**: `{tenant-id}` (e.g., `bcbs-florida`)
- **Password**: Auto-generated, stored in Azure Key Vault
- **SSH Key**: Optional, can be generated on request
- **Home Directory**: `/tenants/{tenant-id}/`

Users navigate to environments via `cd`:

```bash
sftp bcbs-florida@sftp.cloudhealthoffice.com

# Production
cd prod/availity/inbound/837
put claim.edi

# Test in PPMO before promoting to production
cd ../../preprod/availity/inbound/837
put test-claim.edi

# Development
cd ../../dev/sandbox/inbound/837
put dev-claim.edi
```

## Environment Promotion Workflow

Typical workflow for healthcare payer customers:

1. **Dev**: Test integration with sandbox clearinghouse
2. **PPMO**: Test with real clearinghouse test environment, validate config changes
3. **Production**: Promote validated configuration

Portal supports copying configuration between environments:

```bash
# Copy trading partner config from PPMO to Production
POST /api/v1/tenants/{tenantId}/environments/preprod/promote
{
  "targetEnvironment": "prod",
  "promoteTradingPartners": true,
  "promoteBenefitPlans": false
}
```

## Implementation Tasks

### Phase 1: Database Schema (Week 1)
- [ ] Update `Tenant` model to include `environments` array
- [ ] Add `TenantEnvironment` and `TradingPartner` models
- [ ] Migration script to add environments to existing tenants

### Phase 2: SFTP Provisioning Service (Week 1-2)
- [ ] Create `SftpProvisioningService` with Kubernetes exec commands
- [ ] Update `TenantManagementService.CreateTenantAsync()` to call provisioning
- [ ] Add `ProvisionTradingPartnerAsync()` method
- [ ] Write integration tests

### Phase 3: Portal UI (Week 2)
- [ ] Add environment selector to dashboard (dropdown: prod/preprod/dev)
- [ ] Create Trading Partner management page
- [ ] Add "Add Trading Partner" form with environment selection
- [ ] Update SFTP credentials display to show environment paths

### Phase 4: Argo Workflows Updates (Week 3)
- [ ] Add `environment` parameter to all Argo Workflow definitions
- [ ] Update SFTP paths to include environment: `/{tenantId}/{environment}/{partnerId}/inbound/{transactionType}`
- [ ] Update Cosmos DB queries to filter by environment partition

### Phase 5: Documentation (Week 3)
- [ ] Update onboarding guide with environment concepts
- [ ] Add QNXT/Facets migration guide (map regions to environments)
- [ ] Create video walkthrough of environment setup

## Testing Checklist

- [ ] Signup creates prod/preprod/dev folders automatically
- [ ] SFTP credentials work for all environments
- [ ] Trading partner addition creates correct environment folders
- [ ] Environment selector in portal filters data correctly
- [ ] File uploaded to prod environment doesn't appear in preprod
- [ ] Configuration promotion copies trading partners between environments
- [ ] Existing tenants can add new environments

## Migration for Existing Tenants

For tenants already onboarded (pre-environments):

```bash
# Run migration script
./scripts/migrate-tenants-to-environments.sh

# For each existing tenant:
# 1. Creates preprod/ and dev/ folders alongside existing prod/ folders
# 2. Adds environments array to Cosmos DB record
# 3. Defaults all existing trading partners to "prod" environment
```

## API Endpoints

```bash
# List environments for tenant
GET /api/v1/tenants/{tenantId}/environments

# Get specific environment details
GET /api/v1/tenants/{tenantId}/environments/{environment}

# Add trading partner to environment
POST /api/v1/tenants/{tenantId}/environments/{environment}/trading-partners

# Promote configuration from one environment to another
POST /api/v1/tenants/{tenantId}/environments/{sourceEnv}/promote
```

## Questions / Decisions Needed

1. **Default environments**: Should we always provision prod/preprod/dev, or let tenant choose during signup?
2. **Environment naming**: Use "preprod" or "ppmo"? (Suggest "preprod" for broader industry understanding, but allow alias)
3. **Promotion automation**: Should we auto-sync certain config (benefit plans, fee schedules) from prod to preprod?
4. **Environment limits**: Professional tier gets 2 environments (prod + preprod), Enterprise gets all 3?
5. **SFTP credentials per environment**: One set of credentials for all environments (current design), or separate credentials per environment for better isolation?

## Next Steps

Ready to implement this? I can start with:

1. Update the Tenant model to include environments
2. Create the SftpProvisioningService
3. Wire it into the signup flow
4. Add portal UI for managing trading partners per environment

Let me know which parts you'd like me to prioritize!
