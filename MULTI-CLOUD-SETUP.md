# Multi-Cloud Document Store Configuration Guide

This guide shows how to use the cloud-agnostic document store abstraction to support both Azure and DigitalOcean deployments with a single codebase.

## Architecture

```
┌─────────────────────────────────────────┐
│  Service (MemberService, ClaimsService) │
│  Uses: IDocumentStore<T>                │
└─────────────────┬───────────────────────┘
                  │
                  ├─ CloudProvider=Azure ────────────► CosmosDocumentStore
                  │                                     └─► Azure Cosmos DB
                  │
                  └─ CloudProvider=DigitalOcean ───────► MongoDocumentStore
                                                         └─► MongoDB (Managed)
```

## Step 1: Add Infrastructure Package Reference

Update your service's `.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="../shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj" />
</ItemGroup>
```

## Step 2: Update Program.cs

**BEFORE** (Azure-only):
```csharp
using Microsoft.Azure.Cosmos;
using MemberService.Repositories;

// Cosmos DB client (singleton)
builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["CosmosDb:Endpoint"];
    var key = configuration["CosmosDb:Key"];
    return new CosmosClient(endpoint, key, new CosmosClientOptions { ... });
});

// Register repositories
builder.Services.AddScoped<IMemberRepository>(sp =>
{
    var cosmosClient = sp.GetRequiredService<CosmosClient>();
    return new MemberRepository(cosmosClient, "CloudHealthOffice");
});
```

**AFTER** (Multi-cloud):
```csharp
using CloudHealthOffice.Infrastructure.DocumentStore;
using MemberService.Repositories;

// Cloud-agnostic document store (auto-detects Azure vs DigitalOcean)
builder.Services.AddDocumentStore(builder.Configuration);

// Register repositories (no changes needed if using IDocumentStore)
builder.Services.AddScoped<IMemberRepository>(sp =>
{
    var databaseName = builder.Configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
    var documentStore = sp.CreateDocumentStore<Member>(
        databaseName: databaseName,
        containerName: "Members",
        partitionKeyPath: "/tenantId");
    return new MemberRepository(documentStore);
});
```

## Step 3: Configuration Files

### Azure Deployment (appsettings.Production.json)

```json
{
  "CloudProvider": "Azure",
  "CosmosDb": {
    "Endpoint": "https://cho-cosmos-prod.documents.azure.com:443/",
    "Key": "{{FROM_KEYVAULT}}",
    "DatabaseName": "CloudHealthOffice"
  }
}
```

### DigitalOcean Deployment (appsettings.DigitalOcean.json)

```json
{
  "CloudProvider": "DigitalOcean",
  "MongoDB": {
    "ConnectionString": "mongodb+srv://cho-user:{{PASSWORD}}@cho-mongodb-cluster.mongodb.net/?retryWrites=true&w=majority",
    "DatabaseName": "CloudHealthOffice"
  }
}
```

## Step 4: Repository Pattern (Optional Refactor)

If you want repositories to be fully cloud-agnostic, inject `IDocumentStore<T>` instead of `CosmosClient`:

**CloudAgnosticMemberRepository.cs** (see [services/member-service/Repositories/CloudAgnosticMemberRepository.cs](services/member-service/Repositories/CloudAgnosticMemberRepository.cs)):
```csharp
using CloudHealthOffice.Infrastructure.DocumentStore;

public class CloudAgnosticMemberRepository : IMemberRepository
{
    private readonly IDocumentStore<Member> _store;

    public CloudAgnosticMemberRepository(IDocumentStore<Member> store)
    {
        _store = store;
    }

    public async Task<Member?> GetByIdAsync(string tenantId, string id)
    {
        return await _store.GetByIdAsync(id, tenantId);
    }

    public async Task<IEnumerable<Member>> SearchAsync(string tenantId, string? lastName = null)
    {
        var query = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new Dictionary<string, object> { { "tenantId", tenantId } };
        
        if (!string.IsNullOrEmpty(lastName))
        {
            query += " AND c.lastName = @lastName";
            parameters["lastName"] = lastName;
        }

        return await _store.QueryAsync(query, parameters, tenantId);
    }
}
```

**Program.cs (with cloud-agnostic repository)**:
```csharp
builder.Services.AddScoped<IMemberRepository>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
    var documentStore = sp.CreateDocumentStore<Member>(
        databaseName: databaseName,
        containerName: "Members",
        partitionKeyPath: "/tenantId");
    return new CloudAgnosticMemberRepository(documentStore);
});
```

**Backward Compatibility**:
The existing `MemberRepository` (using `CosmosClient`) continues to work on Azure deployments because `AddDocumentStore` registers `CosmosClient` automatically when `CloudProvider=Azure`. For DigitalOcean deployments, switch to `CloudAgnosticMemberRepository`.

## Step 5: Environment Variables (Kubernetes)

### Azure Deployment
```yaml
env:
  - name: CloudProvider
    value: "Azure"
  - name: CosmosDb__Endpoint
    valueFrom:
      secretKeyRef:
        name: cosmos-secrets
        key: endpoint
  - name: CosmosDb__Key
    valueFrom:
      secretKeyRef:
        name: cosmos-secrets
        key: primaryKey
```

### DigitalOcean Deployment
```yaml
env:
  - name: CloudProvider
    value: "DigitalOcean"
  - name: MongoDB__ConnectionString
    valueFrom:
      secretKeyRef:
        name: mongodb-secrets
        key: connectionString
```

## CI/CD: Toggling Cloud Deployments

### GitHub Actions Workflow (.github/workflows/deploy.yml)

```yaml
name: Deploy Services

on:
  workflow_dispatch:
    inputs:
      deploy_to_azure:
        description: 'Deploy to Azure'
        type: boolean
        default: true
      deploy_to_digitalocean:
        description: 'Deploy to DigitalOcean'
        type: boolean
        default: false
  push:
    branches: [main]

jobs:
  deploy-azure:
    if: github.event.inputs.deploy_to_azure == 'true' || github.event_name == 'push'
    runs-on: ubuntu-latest
    steps:
      - name: Deploy to Azure AKS
        run: |
          kubectl config use-context cho-azure-prod
          kubectl set image deployment/member-service \
            member-service=ghcr.io/aurelianware/member-service:${{ github.sha }}
          kubectl set env deployment/member-service CloudProvider=Azure

  deploy-digitalocean:
    if: github.event.inputs.deploy_to_digitalocean == 'true'
    runs-on: ubuntu-latest
    steps:
      - name: Deploy to DigitalOcean Kubernetes
        run: |
          kubectl config use-context cho-do-prod
          kubectl set image deployment/member-service \
            member-service=ghcr.io/aurelianware/member-service:${{ github.sha }}
          kubectl set env deployment/member-service CloudProvider=DigitalOcean
```

**Usage**:
- **Both clouds**: Push to `main` → Azure deploys automatically, DigitalOcean requires manual trigger
- **Azure only**: Manual workflow, check "Deploy to Azure", uncheck "Deploy to DigitalOcean"
- **DigitalOcean only**: Manual workflow, uncheck "Deploy to Azure", check "Deploy to DigitalOcean"
- **Toggle seamlessly**: Change boolean flags in GitHub Actions UI

## Query Compatibility Notes

### Cosmos SQL vs MongoDB

The document store abstraction handles simple queries automatically. For complex queries:

**Cosmos SQL (Azure)**:
```sql
SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.createdDate DESC
```

**MongoDB Equivalent** (auto-converted by MongoDocumentStore):
```json
{
  "tenantId": "tenant123",
  "status": "active"
}
```

For advanced queries (joins, aggregations), consider:
1. Keep queries simple (filter + sort)
2. Use repository-specific methods for complex logic
3. Add cloud-specific query builders if needed

## Migration Strategy

### Phase 1: Abstraction (Week 1)
- ✅ Add CloudHealthOffice.Infrastructure package
- Update 1-2 services to use IDocumentStore
- Test on Azure (no behavior change)

### Phase 2: DigitalOcean Pilot (Week 2)
- Provision DigitalOcean MongoDB cluster
- Deploy 1-2 services to DigitalOcean
- Validate functionality parity

### Phase 3: Full Dual-Cloud (Week 3)
- Update all 17 services
- Configure CI/CD for both clouds
- Production deployment strategy:
  - Dev/Test: DigitalOcean (cost savings)
  - Production: Azure (enterprise SLA)

## Cost Comparison

| Component | Azure (Monthly) | DigitalOcean (Monthly) | Savings |
|-----------|----------------|------------------------|---------|
| Database | $400 (Cosmos) | $120 (Managed MongoDB) | 70% |
| Compute | $200 (AKS) | $80 (Kubernetes) | 60% |
| Storage | $40 (Blob) | $25 (Spaces) | 38% |
| **Total** | **$640** | **$225** | **65%** |

## Benefits of Dual-Cloud Support

1. **Cost Flexibility**: Dev on DO, prod on Azure
2. **Customer Choice**: Enterprise customers select preferred cloud
3. **Geographic Expansion**: Azure US-East, DO Europe
4. **Vendor Negotiation**: Competitive leverage
5. **No Lock-In**: True cloud portability
6. **Disaster Recovery**: Cross-cloud failover capability

## Next Steps

1. Add infrastructure package to all services
2. Update Program.cs in all 17 microservices
3. Test locally with both Azure Cosmos Emulator and MongoDB
4. Configure GitHub Actions toggles
5. Deploy to DigitalOcean test environment
6. Document runbook for cloud switching
