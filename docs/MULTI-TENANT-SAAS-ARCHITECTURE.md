# Cloud Health Office - Multi-Tenant SaaS Architecture

## Overview

Cloud Health Office is designed as a **multi-tenant SaaS platform** where health plans can sign up and get their own isolated environment while sharing the underlying infrastructure.

## Multi-Tenant Isolation Strategy

### Hybrid Approach: Logical Isolation + Namespace Segmentation

```
┌─────────────────────────────────────────────────────────────────┐
│  Shared AKS Cluster                                             │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  Shared Services (cho-platform namespace)                  │ │
│  │  • Authentication Service                                  │ │
│  │  • Tenant Management API                                   │ │
│  │  • Billing & Metering Service                              │ │
│  │  • Onboarding Workflow                                     │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  Core Services (cloudhealthoffice namespace)                        │ │
│  │  • Eligibility, Benefit, Provider, Reference Data          │ │
│  │  • Multi-tenant aware (TenantId in all requests)           │ │
│  │  • Data isolated by partition key                          │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐         │
│  │ Tenant:      │  │ Tenant:      │  │ Tenant:      │         │
│  │ AETNA        │  │ BLUE CROSS   │  │ CIGNA        │         │
│  │ (namespace)  │  │ (namespace)  │  │ (namespace)  │         │
│  │ Optional for │  │ Optional for │  │ Optional for │         │
│  │ premium tier │  │ premium tier │  │ premium tier │         │
│  └──────────────┘  └──────────────┘  └──────────────┘         │
└─────────────────────────────────────────────────────────────────┘
```

## Tenant Isolation Layers

### 1. **Data Layer Isolation** (Cosmos DB)

**Partition Strategy**: Use `tenantId` as partition key

```csharp
public class BenefitPlan
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Multi-tenant partition key
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;
    
    // ... rest of properties
}
```

**Cosmos DB Container Configuration**:
```json
{
  "id": "benefit-plans",
  "partitionKey": {
    "paths": ["/tenantId"],
    "kind": "Hash"
  },
  "indexingPolicy": {
    "includedPaths": [
      { "path": "/tenantId/?" },
      { "path": "/planId/?" },
      { "path": "/effectiveDate/?" }
    ]
  }
}
```

**Benefits**:
- ✅ Automatic data isolation at database level
- ✅ Queries scoped to single tenant (fast)
- ✅ Prevents accidental cross-tenant data leaks
- ✅ RU/s costs attributable to specific tenant

### 2. **API Layer Isolation** (Middleware)

**Tenant Context Middleware** (ASP.NET Core):

```csharp
public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantService tenantService)
    {
        // Extract tenant ID from JWT claim or header
        var tenantId = context.User.FindFirst("tenant_id")?.Value;
        
        if (string.IsNullOrEmpty(tenantId))
        {
            // Fallback to X-Tenant-ID header (for service-to-service calls)
            tenantId = context.Request.Headers["X-Tenant-ID"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Missing tenant context");
            return;
        }

        // Validate tenant is active
        var tenant = await tenantService.GetTenantAsync(tenantId);
        if (tenant == null || !tenant.IsActive)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Tenant not active");
            return;
        }

        // Set tenant context for the request
        context.Items["TenantId"] = tenantId;
        context.Items["Tenant"] = tenant;

        await _next(context);
    }
}
```

**Usage in Controllers**:
```csharp
[ApiController]
[Route("api/v1/plans")]
public class BenefitPlansController : ControllerBase
{
    private string TenantId => HttpContext.Items["TenantId"] as string;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<BenefitPlan>>> GetPlans()
    {
        // Automatically scoped to current tenant
        var plans = await _service.GetPlansAsync(TenantId);
        return Ok(plans);
    }
}
```

### 3. **Authentication & Authorization**

**Azure AD B2C Multi-Tenant Setup**:

```json
{
  "AzureAdB2C": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain": "cloudhealthoffice.onmicrosoft.com",
    "ClientId": "your-client-id",
    "SignUpSignInPolicyId": "B2C_1_SignUpSignIn",
    "TenantClaim": "extension_TenantId"
  }
}
```

**JWT Token Structure**:
```json
{
  "sub": "user@healthplan.com",
  "tenant_id": "aetna-prod-001",
  "tenant_name": "Aetna",
  "roles": ["admin", "claims_adjudicator"],
  "plan_access": ["AETNA-HMO-001", "AETNA-PPO-002"],
  "iat": 1738713600,
  "exp": 1738800000
}
```

### 4. **Database Schema (PostgreSQL Reference Data)**

**Row-Level Security** for shared reference data:

```sql
-- Tenants table
CREATE TABLE tenants (
    tenant_id VARCHAR(50) PRIMARY KEY,
    tenant_name VARCHAR(200) NOT NULL,
    is_active BOOLEAN DEFAULT true,
    subscription_tier VARCHAR(20), -- 'starter', 'professional', 'enterprise'
    created_at TIMESTAMP DEFAULT NOW(),
    billing_email VARCHAR(200)
);

-- CPT codes (shared across tenants)
CREATE TABLE cpt_codes (
    code VARCHAR(10) PRIMARY KEY,
    description TEXT,
    category VARCHAR(100),
    is_active BOOLEAN DEFAULT true
);

-- Tenant-specific CPT overrides (custom pricing, restrictions)
CREATE TABLE tenant_cpt_overrides (
    tenant_id VARCHAR(50) REFERENCES tenants(tenant_id),
    cpt_code VARCHAR(10) REFERENCES cpt_codes(code),
    custom_description TEXT,
    is_covered BOOLEAN DEFAULT true,
    requires_prior_auth BOOLEAN DEFAULT false,
    PRIMARY KEY (tenant_id, cpt_code)
);

-- Enable Row-Level Security
ALTER TABLE tenant_cpt_overrides ENABLE ROW LEVEL SECURITY;

-- Policy: Users can only see their tenant's overrides
CREATE POLICY tenant_isolation_policy ON tenant_cpt_overrides
    USING (tenant_id = current_setting('app.current_tenant')::VARCHAR);
```

## Tenant Onboarding Workflow

### Automated Self-Service Onboarding

```yaml
apiVersion: argoproj.io/v1alpha1
kind: Workflow
metadata:
  name: tenant-onboarding
  namespace: cho-platform
spec:
  entrypoint: onboard-tenant
  arguments:
    parameters:
      - name: tenant-name
        value: "Blue Shield California"
      - name: tenant-id
        value: "blueshield-ca-prod"
      - name: admin-email
        value: "admin@blueshieldca.com"
      - name: subscription-tier
        value: "professional" # starter, professional, enterprise

  templates:
    - name: onboard-tenant
      steps:
        # Step 1: Create tenant record in database
        - - name: create-tenant-record
            template: create-db-record

        # Step 2: Provision Cosmos DB containers with tenant data
        - - name: provision-cosmos-containers
            template: setup-cosmos

        # Step 3: Create Azure AD B2C user
        - - name: create-admin-user
            template: create-ad-user

        # Step 4: Set up billing in Stripe/Azure
        - - name: setup-billing
            template: configure-billing

        # Step 5: Create default benefit plans
        - - name: seed-default-data
            template: import-default-plans

        # Step 6: Send welcome email
        - - name: send-welcome-email
            template: send-email

        # Step 7: (Optional) Create dedicated namespace for enterprise tier
        - - name: create-namespace
            template: create-k8s-namespace
            when: "{{workflow.parameters.subscription-tier}} == 'enterprise'"
```

## Subscription Tiers

| Feature | Starter | Professional | Enterprise |
|---------|---------|--------------|------------|
| **Price/Month** | $500 | $2,500 | Custom |
| **Claims/Month** | 10,000 | 100,000 | Unlimited |
| **Prior Auths/Month** | 500 | 5,000 | Unlimited |
| **Eligibility Checks** | 5,000 | 50,000 | Unlimited |
| **Users** | 5 | 25 | Unlimited |
| **Custom Workflows** | ❌ | Limited | ✅ Full |
| **Dedicated Namespace** | ❌ | ❌ | ✅ |
| **SLA** | 99% | 99.9% | 99.99% |
| **Support** | Email | Phone | Dedicated CSM |
| **Custom Integrations** | ❌ | Limited | ✅ |
| **HITRUST Compliance** | ❌ | ✅ | ✅ |

## Billing & Metering

### Usage Tracking (Prometheus Metrics)

```csharp
public class TenantMetricsService
{
    private static readonly Counter ClaimsProcessed = Metrics
        .CreateCounter("claims_processed_total", "Claims processed by tenant",
            new CounterConfiguration { LabelNames = new[] { "tenant_id", "result" } });

    private static readonly Counter EligibilityChecks = Metrics
        .CreateCounter("eligibility_checks_total", "Eligibility checks by tenant",
            new CounterConfiguration { LabelNames = new[] { "tenant_id" } });

    private static readonly Counter PriorAuths = Metrics
        .CreateCounter("prior_auths_total", "Prior auth requests by tenant",
            new CounterConfiguration { LabelNames = new[] { "tenant_id" } });

    public void RecordClaimProcessed(string tenantId, string result)
    {
        ClaimsProcessed.WithLabels(tenantId, result).Inc();
    }
}
```

### Monthly Billing Job

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: monthly-billing
  namespace: cho-platform
spec:
  schedule: "0 0 1 * *" # First day of month at midnight
  jobTemplate:
    spec:
      template:
        spec:
          containers:
          - name: billing-processor
            image: acr.azurecr.io/cho/billing-service:latest
            env:
              - name: STRIPE_API_KEY
                valueFrom:
                  secretKeyRef:
                    name: stripe-credentials
                    key: api-key
            command: ["/bin/sh", "-c"]
            args:
              - |
                # Query Prometheus for usage metrics
                curl "http://prometheus:9090/api/v1/query?query=sum(claims_processed_total) by (tenant_id)"
                
                # Generate invoices
                # Send to Stripe
                # Update tenant billing records
```

## Tenant Management API

```csharp
[ApiController]
[Route("api/v1/tenants")]
[Authorize(Roles = "PlatformAdmin")]
public class TenantsController : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<Tenant>> CreateTenant([FromBody] TenantCreateRequest request)
    {
        var tenant = new Tenant
        {
            TenantId = GenerateTenantId(request.Name),
            TenantName = request.Name,
            SubscriptionTier = request.Tier,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.CreateAsync(tenant);
        
        // Trigger onboarding workflow
        await _argoService.StartWorkflow("tenant-onboarding", new
        {
            tenantId = tenant.TenantId,
            adminEmail = request.AdminEmail
        });

        return CreatedAtAction(nameof(GetTenant), new { id = tenant.TenantId }, tenant);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Tenant>> GetTenant(string id)
    {
        var tenant = await _repository.GetAsync(id);
        return tenant != null ? Ok(tenant) : NotFound();
    }

    [HttpGet("{id}/usage")]
    public async Task<ActionResult<TenantUsage>> GetUsage(string id, [FromQuery] DateTime? startDate)
    {
        var usage = await _metricsService.GetTenantUsageAsync(id, startDate ?? DateTime.UtcNow.AddDays(-30));
        return Ok(usage);
    }

    [HttpPut("{id}/suspend")]
    public async Task<IActionResult> SuspendTenant(string id)
    {
        await _repository.UpdateAsync(id, t => t.IsActive = false);
        return NoContent();
    }
}
```

## Security Considerations

### 1. Data Isolation Checklist
- ✅ All database queries include `WHERE tenantId = @TenantId`
- ✅ Cosmos DB partition key enforced
- ✅ No cross-tenant JOINs or queries
- ✅ Audit logs track tenant context
- ✅ Unit tests verify tenant isolation

### 2. API Security
- ✅ JWT tokens include tenant_id claim
- ✅ Middleware validates tenant on every request
- ✅ Service-to-service calls include X-Tenant-ID header
- ✅ Rate limiting per tenant
- ✅ API keys scoped to tenant

### 3. Compliance (HIPAA, HITRUST)
- ✅ PHI data encrypted at rest (Cosmos DB, PostgreSQL)
- ✅ PHI data encrypted in transit (TLS 1.3)
- ✅ Audit logs for all data access
- ✅ Tenant data isolated (no accidental leakage)
- ✅ Business Associate Agreements (BAA) per tenant

## Cost Allocation

### Per-Tenant Resource Tracking

```promql
# Cosmos DB RU/s by tenant
sum(rate(cosmosdb_request_units_total[5m])) by (tenant_id)

# CPU usage by tenant (via labels)
sum(rate(container_cpu_usage_seconds_total{tenant_id!=""}[5m])) by (tenant_id)

# Storage by tenant
sum(cosmosdb_storage_bytes) by (tenant_id)
```

### Monthly Cost Report

| Tenant | Claims | Eligibility | RU/s Used | Storage | Est. Cost |
|--------|--------|-------------|-----------|---------|-----------|
| Aetna | 50,000 | 25,000 | 5,000 | 10GB | $1,200 |
| Blue Cross | 30,000 | 15,000 | 3,000 | 6GB | $750 |
| Cigna | 75,000 | 40,000 | 8,000 | 15GB | $1,800 |

## Multi-Tenant Dashboard (Grafana)

```
┌─────────────────────────────────────────────────────────┐
│  Cloud Health Office - SaaS Platform Metrics            │
├─────────────────────────────────────────────────────────┤
│  Active Tenants: 127        MRR: $156,450              │
│  Claims/Month: 2.1M         Eligibility: 1.3M          │
└─────────────────────────────────────────────────────────┘

Top Tenants by Usage:
┌──────────────────┬──────────┬──────────┬──────────┐
│ Tenant           │ Claims   │ Users    │ MRR      │
├──────────────────┼──────────┼──────────┼──────────┤
│ Aetna            │ 350,000  │ 45       │ $5,000   │
│ Blue Cross CA    │ 280,000  │ 32       │ $4,200   │
│ Humana           │ 210,000  │ 28       │ $3,800   │
└──────────────────┴──────────┴──────────┴──────────┘

Churn Risk (usage declining):
• Regional Health Plan - down 40% this month
• Community Care - no activity for 15 days
```

## Implementation Checklist

### Phase 1: Core Multi-Tenancy (This Week)
- [ ] Add TenantId to all data models
- [ ] Implement TenantMiddleware in all APIs
- [ ] Update Cosmos DB containers with partition keys
- [ ] Add tenant validation in controllers
- [ ] Update JWT tokens with tenant_id claim

### Phase 2: Tenant Management (Next Week)
- [ ] Build Tenant Management API
- [ ] Create tenant onboarding workflow
- [ ] Implement usage tracking/metering
- [ ] Set up billing integration (Stripe)

### Phase 3: SaaS Features (Week 3-4)
- [ ] Self-service tenant signup
- [ ] Tenant admin portal (Blazor)
- [ ] Usage dashboards per tenant
- [ ] Billing portal
- [ ] Automated provisioning

## Example: Multi-Tenant Benefit Plan Service

**Updated Model**:
```csharp
public class BenefitPlan
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    [Required]
    public string TenantId { get; set; } = string.Empty;

    // ... rest of properties
}
```

**Updated Repository**:
```csharp
public async Task<IEnumerable<BenefitPlan>> GetPlansAsync(string tenantId)
{
    var query = new QueryDefinition(
        "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.isActive = true")
        .WithParameter("@tenantId", tenantId);

    return await _container.GetItemQueryIterator<BenefitPlan>(
        query,
        requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) }
    ).ToListAsync();
}
```

---

## Summary

Multi-tenancy makes Cloud Health Office a **true SaaS platform** where:
- ✅ Health plans sign up self-service
- ✅ Data is completely isolated and secure
- ✅ Usage is tracked and billed accurately
- ✅ Platform scales with tenant growth
- ✅ Costs are allocated per tenant
- ✅ HIPAA/HITRUST compliant

**Next Step**: Update Benefit Plan Service with multi-tenant support (30 minutes), then apply pattern to all services.

Ready to implement? 🚀
