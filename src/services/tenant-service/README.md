# Tenant Management Service

Multi-tenant SaaS tenant management for Cloud Health Office. This service provides CRUD operations for managing health plan tenants, API key generation, subscription management, and Stripe billing integration.

## Features

### Tenant Management
- ✅ **CRUD operations** for health plan tenants
- ✅ **Automatic tenant ID generation** (e.g., `blueshield-ca-a1b2c3d4`)
- ✅ **Tenant status management** (pending, active, suspended, terminated)
- ✅ **Multi-tier support** (Starter, Professional, Enterprise)
- ✅ **Configuration management** (enabled modules, clearinghouse settings)

### API Key Management
- ✅ **Cryptographically secure API key generation** (`cho_xxxxx...`)
- ✅ **SHA256 hashing** (never store plain-text keys)
- ✅ **Scoped permissions** (claims:read, claims:write, etc.)
- ✅ **Expiration support** (optional expiry dates)
- ✅ **Key revocation** (disable without deletion)

### Usage Tracking
- ✅ **Monthly metrics** (claims, prior auths, eligibility checks, API calls)
- ✅ **Automatic reset** on first day of each month
- ✅ **Storage tracking** (GB used)
- ✅ **Last activity timestamp**

### Stripe Billing (Optional)
- ✅ **Customer creation** in Stripe
- ✅ **Subscription management** (create, update, cancel)
- ✅ **Webhook handling** (payment success/failure, subscription changes)
- ✅ **Invoice retrieval** (upcoming + history)
- ✅ **Automated tier changes** with proration

## API Endpoints

### Tenants

```http
POST   /api/v1/tenants              Create new tenant
GET    /api/v1/tenants              Get all tenants
GET    /api/v1/tenants/{tenantId}   Get tenant by ID
PUT    /api/v1/tenants/{tenantId}   Update tenant
DELETE /api/v1/tenants/{tenantId}   Delete tenant

POST   /api/v1/tenants/{tenantId}/activate   Activate tenant
POST   /api/v1/tenants/{tenantId}/suspend    Suspend tenant
```

### API Keys

```http
POST   /api/v1/tenants/{tenantId}/api-keys          Create API key
GET    /api/v1/tenants/{tenantId}/api-keys          List API keys
DELETE /api/v1/tenants/{tenantId}/api-keys/{keyId}  Revoke API key
```

### Usage

```http
GET    /api/v1/tenants/{tenantId}/usage     Get usage metrics
```

### Billing

```http
POST   /api/v1/billing/tenants/{tenantId}/subscribe          Create subscription
GET    /api/v1/billing/tenants/{tenantId}/upcoming-invoice   Get upcoming invoice
GET    /api/v1/billing/tenants/{tenantId}/invoices          Get invoice history
PUT    /api/v1/billing/tenants/{tenantId}/tier              Update subscription tier
POST   /api/v1/billing/tenants/{tenantId}/cancel            Cancel subscription

POST   /api/v1/billing/webhook                              Stripe webhook (internal)
```

## Example Usage

### Create Tenant

```bash
curl -X POST http://localhost:8080/api/v1/tenants \
  -H "Content-Type: application/json" \
  -d '{
    "tenantName": "Blue Shield California",
    "organizationName": "Blue Shield of California",
    "subscriptionTier": "professional",
    "contactInfo": {
      "primaryContact": "John Smith",
      "email": "admin@blueshieldca.com",
      "phone": "+1-555-0100"
    },
    "enabledModules": ["claims", "eligibility", "authorizations"],
    "clearinghouse": {
      "name": "Availity",
      "senderId": "BSCA123",
      "receiverId": "AVAILITY"
    }
  }'
```

**Response:**
```json
{
  "id": "abc123...",
  "tenantId": "blue-shield-of-california-a1b2c3d4",
  "tenantName": "Blue Shield California",
  "subscriptionTier": "professional",
  "status": "pending",
  "createdAt": "2026-02-06T10:00:00Z"
}
```

### Generate API Key

```bash
curl -X POST http://localhost:8080/api/v1/tenants/blue-shield-of-california-a1b2c3d4/api-keys \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Production Key",
    "scopes": ["claims:read", "claims:write", "eligibility:read"],
    "expiresAt": "2027-02-06T00:00:00Z"
  }'
```

**Response (key shown only once!):**
```json
{
  "keyId": "key_xyz789",
  "name": "Production Key",
  "apiKey": "cho_a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0",
  "createdAt": "2026-02-06T10:05:00Z",
  "expiresAt": "2027-02-06T00:00:00Z",
  "scopes": ["claims:read", "claims:write", "eligibility:read"]
}
```

### Create Stripe Subscription

```bash
curl -X POST "http://localhost:8080/api/v1/billing/tenants/blue-shield-of-california-a1b2c3d4/subscribe?tier=professional"
```

**Response:**
```json
{
  "customerId": "cus_xxxxx",
  "subscriptionId": "sub_xxxxx",
  "tier": "professional"
}
```

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `CosmosDb__Endpoint` | Cosmos DB endpoint | - |
| `CosmosDb__Key` | Cosmos DB key | - |
| `CosmosDb__DatabaseName` | Database name | `CloudHealthOffice` |
| `CosmosDb__TenantContainerName` | Container name | `Tenants` |
| `Stripe__SecretKey` | Stripe API secret key | - |
| `Stripe__WebhookSecret` | Stripe webhook signing secret | - |
| `Stripe__PricingIds__starter_monthly` | Stripe price ID for Starter tier | - |
| `Stripe__PricingIds__professional_monthly` | Stripe price ID for Professional tier | - |
| `Stripe__PricingIds__enterprise_monthly` | Stripe price ID for Enterprise tier | - |

### Cosmos DB Setup

Create container with partition key `/id`:

```bash
az cosmosdb sql container create \
  --account-name <cosmos-account> \
  --database-name CloudHealthOffice \
  --name Tenants \
  --partition-key-path "/id" \
  --throughput 400
```

### Stripe Setup

1. Create Stripe account at https://stripe.com
2. Get API keys from Dashboard → Developers → API keys
3. Create products and pricing:
   - **Starter**: [Contact sales](mailto:sales@cloudhealthoffice.com)
   - **Professional**: [Contact sales](mailto:sales@cloudhealthoffice.com)
   - **Enterprise**: Custom pricing
4. Set up webhook endpoint: `https://your-domain/api/v1/billing/webhook`
5. Copy webhook signing secret

## Deployment

### Kubernetes

```bash
# Deploy service
kubectl apply -f k8s/tenant-service-deployment.yaml

# Verify deployment
kubectl get pods -n cloudhealthoffice -l app=tenant-service
kubectl get svc -n cloudhealthoffice tenant-service

# Test health endpoint
kubectl port-forward -n cloudhealthoffice svc/tenant-service 8080:80
curl http://localhost:8080/health
```

### Docker

```bash
# Build image
docker build -t ghcr.io/aurelianware/cloudhealthoffice-tenant-service:latest .

# Run locally
docker run -p 8080:8080 \
  -e CosmosDb__Endpoint=https://localhost:8081 \
  -e CosmosDb__Key=<key> \
  ghcr.io/aurelianware/cloudhealthoffice-tenant-service:latest
```

## Security Considerations

### API Key Storage
- ✅ Keys hashed with SHA256 before storage
- ✅ Only prefix (first 8 chars) stored for display
- ✅ Plain-text key shown **only once** on creation
- ✅ Cannot retrieve plain-text key after creation

### Tenant Isolation
- Each tenant has unique `tenantId`
- All services use `X-Tenant-ID` header or JWT claim
- Cosmos DB queries filtered by tenant
- API keys scoped to single tenant

### Stripe Security
- Webhook signature verification (prevents spoofing)
- PCI compliance (Stripe handles payment data)
- Customer metadata includes tenant ID for tracking

## Development

### Run Locally

```bash
# Set environment variables
export CosmosDb__Endpoint=https://localhost:8081
export CosmosDb__Key=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==

# Run service
dotnet run
```

### Swagger UI

Navigate to: http://localhost:8080/swagger

## Integration with Other Services

All microservices should validate tenant context by calling Tenant Service:

```csharp
// Extract API key from header
var apiKey = Request.Headers["X-API-Key"];

// Validate with Tenant Service
var response = await httpClient.PostAsync(
    "http://tenant-service.cloudhealthoffice/api/v1/auth/validate", 
    new StringContent(apiKey)
);

if (response.IsSuccessStatusCode)
{
    var tenant = await response.Content.ReadFromJsonAsync<Tenant>();
    // Use tenant.TenantId for data isolation
}
```

## Monitoring

### Health Checks
- `/health` - Liveness probe
- `/ready` - Readiness probe

### Metrics to Track
- Tenant creation rate
- Active tenant count
- API key generation rate
- Subscription changes
- Payment failure rate
- Webhook processing time

## Roadmap

- [ ] Usage-based billing (per-claim pricing)
- [ ] Automated dunning (email reminders for failed payments)
- [ ] Tenant admin portal integration
- [ ] Audit log for all tenant operations
- [ ] Multi-region support
- [ ] Tenant export/backup
- [ ] SSO integration (SAML/OIDC)

## License

BSL 1.1 - See [LICENSE](../../LICENSE)
