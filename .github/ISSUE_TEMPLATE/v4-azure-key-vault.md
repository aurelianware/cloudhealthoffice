---
name: 'Azure Key Vault Integration'
about: Migrate all secrets to Azure Key Vault for production-grade security
title: '[v4.0] Azure Key Vault Integration - Production Security Hardening'
labels: 'security, infrastructure, priority:critical'
assignees: ''
---

## 🎯 Objective

Migrate all application secrets from environment variables and configuration files to Azure Key Vault with managed identity authentication. This is a **BLOCKER** for Beta launch and clearinghouse integration.

**Priority:** 🔴 **CRITICAL**  
**Effort:** 1-2 weeks (1 developer)  
**Depends On:** Azure subscription with Key Vault quota  
**Blocks:** Clearinghouse integration, customer onboarding

---

## 📋 Success Criteria

- [ ] All production secrets stored in Azure Key Vault (Premium SKU with HSM)
- [ ] All 17 microservices read secrets from Key Vault via managed identity
- [ ] Portal reads Stripe API keys from Key Vault
- [ ] SFTP credentials migrated from local config to Key Vault
- [ ] Cosmos DB connection strings use Key Vault references
- [ ] Zero secrets in code, config files, or environment variables
- [ ] Secret rotation automated (90-day policy)
- [ ] Audit logging enabled for all secret access
- [ ] Smoke tests pass in staging environment

---

## 🔧 Implementation Steps

### Phase 1: Azure Key Vault Setup (Day 1)

**1.1 Create Key Vault Resource**
```bash
# Create Key Vault (Premium SKU for HSM-backed keys)
az keyvault create \
  --name "kv-cho-prod-${LOCATION}" \
  --resource-group "rg-cloudhealthoffice-prod" \
  --location "eastus" \
  --sku "Premium" \
  --enable-rbac-authorization true \
  --enable-purge-protection true \
  --retention-days 90

# Configure network access (allow Azure services + deny public)
az keyvault network-rule add \
  --name "kv-cho-prod-eastus" \
  --resource-group "rg-cloudhealthoffice-prod" \
  --vnet-name "vnet-cho-prod" \
  --subnet "snet-aks"

az keyvault update \
  --name "kv-cho-prod-eastus" \
  --resource-group "rg-cloudhealthoffice-prod" \
  --default-action Deny
```

**1.2 Enable Audit Logging**
```bash
# Send Key Vault logs to Log Analytics
az monitor diagnostic-settings create \
  --name "KeyVaultAudit" \
  --resource $(az keyvault show --name "kv-cho-prod-eastus" -g "rg-cloudhealthoffice-prod" --query id -o tsv) \
  --workspace $(az monitor log-analytics workspace show --name "law-cho-prod" -g "rg-cloudhealthoffice-prod" --query id -o tsv) \
  --logs '[{"category": "AuditEvent", "enabled": true, "retentionPolicy": {"enabled": true, "days": 365}}]' \
  --metrics '[{"category": "AllMetrics", "enabled": true}]'
```

**1.3 Configure Managed Identity for AKS**
```bash
# Enable managed identity on AKS cluster
az aks update \
  --resource-group "rg-cloudhealthoffice-prod" \
  --name "cho-aks-prod" \
  --enable-managed-identity

# Get AKS managed identity
AKS_IDENTITY=$(az aks show -g "rg-cloudhealthoffice-prod" -n "cho-aks-prod" --query identityProfile.kubeletidentity.clientId -o tsv)

# Grant Key Vault Secrets User role
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee $AKS_IDENTITY \
  --scope $(az keyvault show --name "kv-cho-prod-eastus" -g "rg-cloudhealthoffice-prod" --query id -o tsv)
```

---

### Phase 2: Migrate Secrets (Days 2-3)

**2.1 Audit Current Secrets**
Create inventory of all secrets currently in:
- GitHub Secrets (production)
- appsettings.json files
- Kubernetes ConfigMaps/Secrets
- Environment variables

**2.2 Upload Secrets to Key Vault**
```bash
# Cosmos DB secrets
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "CosmosDb--ConnectionString" --value "${COSMOS_CONNECTION_STRING}"

# Stripe secrets
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "Stripe--SecretKey" --value "${STRIPE_SECRET_KEY}"
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "Stripe--PublishableKey" --value "${STRIPE_PUBLISHABLE_KEY}"
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "Stripe--WebhookSecret" --value "${STRIPE_WEBHOOK_SECRET}"

# Azure AD secrets (portal)
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "AzureAd--ClientSecret" --value "${AZURE_AD_CLIENT_SECRET}"

# SFTP credentials (per tenant)
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "SFTP--clouddentaloffice--Password" --value "${SFTP_PASSWORD}"

# Application Insights
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "ApplicationInsights--ConnectionString" --value "${APPINSIGHTS_CONNECTION_STRING}"

# Service Bus (if still using)
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "ServiceBus--ConnectionString" --value "${SERVICEBUS_CONNECTION_STRING}"
```

**2.3 Clearinghouse Credentials (for future integration)**
```bash
# Availity (sandbox then production)
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "Availity--Username" --value "${AVAILITY_USERNAME}"
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "Availity--Password" --value "${AVAILITY_PASSWORD}"
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "Availity--SftpHost" --value "sftp.availity.com"

# Change Healthcare
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "ChangeHealthcare--ApiKey" --value "${CHANGE_API_KEY}"
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "ChangeHealthcare--ClientId" --value "${CHANGE_CLIENT_ID}"
az keyvault secret set --vault-name "kv-cho-prod-eastus" --name "ChangeHealthcare--ClientSecret" --value "${CHANGE_CLIENT_SECRET}"
```

---

### Phase 3: Update Applications (Days 4-7)

**3.1 Install Azure.Extensions.AspNetCore.Configuration.Secrets**

Add to all service `.csproj` files:
```xml
<PackageReference Include="Azure.Extensions.AspNetCore.Configuration.Secrets" Version="1.3.2" />
<PackageReference Include="Azure.Identity" Version="1.13.1" />
```

**3.2 Update Program.cs (Microservices)**

Add to `Program.cs` in all 17 microservices:
```csharp
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add Key Vault configuration
if (builder.Environment.IsProduction())
{
    var keyVaultEndpoint = new Uri($"https://kv-cho-prod-eastus.vault.azure.net/");
    builder.Configuration.AddAzureKeyVault(
        keyVaultEndpoint,
        new DefaultAzureCredential()); // Uses managed identity in AKS
}

// Rest of configuration...
```

**3.3 Update Portal Program.cs**

```csharp
// In portal/CloudHealthOffice.Portal/Program.cs
if (builder.Environment.IsProduction())
{
    var keyVaultEndpoint = new Uri(builder.Configuration["KeyVault:VaultUri"] 
        ?? "https://kv-cho-prod-eastus.vault.azure.net/");
    
    builder.Configuration.AddAzureKeyVault(
        keyVaultEndpoint,
        new DefaultAzureCredential());
}
```

**3.4 Remove Hardcoded Secrets**

Update all appsettings.json files to use Key Vault references:
```json
{
  "CosmosDb": {
    "ConnectionString": "" // Now loaded from Key Vault
  },
  "Stripe": {
    "SecretKey": "",
    "PublishableKey": "",
    "WebhookSecret": ""
  },
  "AzureAd": {
    "ClientSecret": "" // Now loaded from Key Vault
  }
}
```

---

### Phase 4: Update Kubernetes Deployments (Days 8-10)

**4.1 Remove Kubernetes Secrets**

Delete or comment out existing secret manifests:
```bash
# Backup existing secrets
kubectl get secrets -n cho-svcs -o yaml > secrets-backup-$(date +%Y%m%d).yaml

# Verify apps will use Key Vault instead
# (Don't delete secrets until apps are updated and tested)
```

**4.2 Update Deployment Manifests**

Add Key Vault URI as environment variable (not secret):
```yaml
# k8s/base/deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: member-service
spec:
  template:
    spec:
      containers:
      - name: member-service
        image: ghcr.io/aurelianware/cloudhealthoffice-member-service:latest
        env:
        - name: KeyVault__VaultUri
          value: "https://kv-cho-prod-eastus.vault.azure.net/"
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        # Secrets now loaded from Key Vault via managed identity
```

**4.3 Enable Workload Identity (Optional - More Secure)**

For pod-level identity instead of node-level:
```bash
# Enable workload identity on AKS
az aks update \
  --resource-group "rg-cloudhealthoffice-prod" \
  --name "cho-aks-prod" \
  --enable-workload-identity

# Create service account with federated identity
# (Advanced - can defer to post-Beta)
```

---

### Phase 5: Testing & Validation (Days 11-14)

**5.1 Staging Environment Testing**
```bash
# Deploy to staging namespace
kubectl apply -k k8s/overlays/staging

# Verify secret access
kubectl logs -n cho-svcs-staging deployment/member-service | grep "KeyVault"

# Test API endpoints
curl https://staging-api.cloudhealthoffice.com/api/v1/members/health
```

**5.2 Smoke Tests**
- [ ] Portal login works (Azure AD client secret from Key Vault)
- [ ] Stripe checkout works (API keys from Key Vault)
- [ ] Cosmos DB queries work (connection string from Key Vault)
- [ ] Application Insights telemetry appears (instrumentation key from Key Vault)
- [ ] SFTP connection succeeds (credentials from Key Vault)

**5.3 Security Validation**
- [ ] No secrets in application logs
- [ ] Key Vault audit logs show access attempts
- [ ] Managed identity authentication working (no access key in config)
- [ ] Secret rotation policy configured (90-day expiry)

**5.4 Production Deployment**
```bash
# Blue/Green deployment to minimize downtime
kubectl apply -k k8s/overlays/production

# Monitor for errors
kubectl rollout status deployment/member-service -n cho-svcs
kubectl logs -f deployment/member-service -n cho-svcs --tail=100
```

---

## 🚨 Rollback Plan

If Key Vault integration breaks production:

**Step 1: Immediate Rollback**
```bash
# Restore previous deployment
kubectl rollout undo deployment/member-service -n cho-svcs

# Verify rollback
kubectl rollout status deployment/member-service -n cho-svcs
```

**Step 2: Restore Secrets**
```bash
# Re-apply backed-up Kubernetes secrets
kubectl apply -f secrets-backup-YYYYMMDD.yaml
```

**Step 3: Root Cause Analysis**
- Check managed identity permissions
- Verify Key Vault network rules allow AKS subnet
- Review audit logs for access denials

---

## 📚 Reference Documentation

- [Azure Key Vault Best Practices](https://learn.microsoft.com/en-us/azure/key-vault/general/best-practices)
- [ASP.NET Core Key Vault Configuration](https://learn.microsoft.com/en-us/aspnet/core/security/key-vault-configuration)
- [AKS Managed Identity](https://learn.microsoft.com/en-us/azure/aks/use-managed-identity)
- [Key Vault RBAC Permissions](https://learn.microsoft.com/en-us/azure/key-vault/general/rbac-guide)

---

## 🔐 Security Considerations

- **Principle of Least Privilege:** Each service should only access secrets it needs (future: split Key Vaults per service)
- **Secret Rotation:** Automate with Azure Automation or Logic Apps (90-day rotation policy)
- **Audit All Access:** Monitor for unusual access patterns (spike in reads, access from unexpected IPs)
- **Break Glass Procedure:** Document manual Key Vault access for emergencies
- **Backup Secrets:** Export secrets to secure offline storage (encrypted USB drive in safe)

---

## ✅ Definition of Done

- [ ] All production secrets migrated to Azure Key Vault
- [ ] All 17 microservices updated to use Key Vault
- [ ] Portal updated to use Key Vault
- [ ] Staging environment tested successfully
- [ ] Production deployment completed with zero downtime
- [ ] Rollback procedure documented and tested
- [ ] Old secrets rotated/invalidated
- [ ] GitHub Secrets cleaned up (only deployment credentials remain)
- [ ] Documentation updated in SECURITY.md
- [ ] Team trained on Key Vault access procedures

---

## 📅 Timeline

| Day | Task | Owner | Status |
|-----|------|-------|--------|
| 1 | Key Vault setup + managed identity | DevOps | ⬜ Not Started |
| 2-3 | Migrate secrets to Key Vault | DevOps | ⬜ Not Started |
| 4-7 | Update all applications | Backend Dev | ⬜ Not Started |
| 8-10 | Update Kubernetes manifests | DevOps | ⬜ Not Started |
| 11-12 | Staging testing | QA | ⬜ Not Started |
| 13-14 | Production deployment | DevOps | ⬜ Not Started |

**Target Completion:** 2 weeks from start
