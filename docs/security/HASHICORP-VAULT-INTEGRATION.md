# HashiCorp Vault Integration Guide

**Last Updated:** 2026-02-19  
**Objective:** Multi-cloud secret management using HashiCorp Vault for production-grade security

---

## 🎯 Overview

This guide details the integration of HashiCorp Vault as the secret management solution for Cloud Health Office, enabling true multi-cloud deployment capabilities across Azure, AWS, GCP, and on-premises environments.

### Why HashiCorp Vault?

✅ **Multi-Cloud Support:** Deploy to any cloud (Azure, AWS, GCP) or on-premises  
✅ **Unified Secret Management:** Single solution across all environments  
✅ **Dynamic Secrets:** Generate credentials on-demand with automatic rotation  
✅ **Fine-Grained Access Control:** Policy-based access with AppRole, Kubernetes auth  
✅ **Encryption as a Service:** Built-in encryption/decryption for PHI  
✅ **Comprehensive Audit Logging:** Complete audit trail for HIPAA compliance  
✅ **Active-Active HA:** Enterprise-grade availability with auto-unseal  
✅ **No Vendor Lock-In:** Open-source core, enterprise features optional

---

## 📋 Architecture

### Deployment Options

**Option 1: Vault on Kubernetes (Recommended)**
```
┌─────────────────────────────────────────────────────┐
│              Kubernetes Cluster (AKS/EKS/GKE)       │
├─────────────────────────────────────────────────────┤
│  ┌──────────────┐    ┌──────────────┐              │
│  │   Vault Pod  │◀──▶│ Consul/Raft  │              │
│  │  (StatefulSet│    │  (Storage)   │              │
│  └──────────────┘    └──────────────┘              │
│         ▲                                           │
│         │                                           │
│  ┌──────┴───────────────────────────┐              │
│  │   Microservices (17 services)    │              │
│  │   - Vault Agent Sidecar          │              │
│  │   - Kubernetes Service Account   │              │
│  └──────────────────────────────────┘              │
└─────────────────────────────────────────────────────┘
```

**Option 2: Managed Vault (Azure Container Instances)**
```
┌─────────────────────────────────────────────────────┐
│              Azure Container Instances               │
├─────────────────────────────────────────────────────┤
│  ┌──────────────┐    ┌──────────────┐              │
│  │  Vault ACI   │◀──▶│  Azure File  │              │
│  │  Container   │    │   Storage    │              │
│  └──────────────┘    └──────────────┘              │
│         ▲                                           │
│         │                                           │
│  ┌──────┴───────────────────────────┐              │
│  │   Logic Apps / AKS Services      │              │
│  │   - Token-based Auth             │              │
│  │   - AppRole Authentication       │              │
│  └──────────────────────────────────┘              │
└─────────────────────────────────────────────────────┘
```

---

## 🚀 Phase 1: HashiCorp Vault Deployment

### 1.1 Prerequisites

**Required Tools:**
```bash
# Install Vault CLI
wget https://releases.hashicorp.com/vault/1.15.4/vault_1.15.4_linux_amd64.zip
unzip vault_1.15.4_linux_amd64.zip
sudo mv vault /usr/local/bin/
vault --version  # Should show v1.15.4 or later

# Install Helm (for Kubernetes deployment)
curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
```

**Azure/AWS/GCP CLI:**
```bash
# Azure CLI (if deploying on AKS)
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash

# AWS CLI (if deploying on EKS)
curl "https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip" -o "awscliv2.zip"
unzip awscliv2.zip && sudo ./aws/install

# Google Cloud SDK (if deploying on GKE)
curl https://sdk.cloud.google.com | bash
```

### 1.2 Vault Deployment on Kubernetes

**Step 1: Add HashiCorp Helm Repository**
```bash
helm repo add hashicorp https://helm.releases.hashicorp.com
helm repo update
```

**Step 2: Create Vault Configuration**

Create `infra/vault/values.yaml`:
```yaml
global:
  enabled: true
  tlsDisable: false  # Use TLS in production

server:
  enabled: true
  image:
    repository: hashicorp/vault
    tag: 1.15.4

  ha:
    enabled: true
    replicas: 3
    raft:
      enabled: true
      setNodeId: true
      config: |
        ui = true
        
        listener "tcp" {
          tls_disable = 0
          address = "[::]:8200"
          cluster_address = "[::]:8201"
          tls_cert_file = "/vault/userconfig/vault-tls/tls.crt"
          tls_key_file  = "/vault/userconfig/vault-tls/tls.key"
        }

        storage "raft" {
          path = "/vault/data"
          
          retry_join {
            leader_api_addr = "https://vault-0.vault-internal:8200"
          }
          retry_join {
            leader_api_addr = "https://vault-1.vault-internal:8200"
          }
          retry_join {
            leader_api_addr = "https://vault-2.vault-internal:8200"
          }
        }

        service_registration "kubernetes" {}

  resources:
    requests:
      memory: 256Mi
      cpu: 250m
    limits:
      memory: 512Mi
      cpu: 500m

  dataStorage:
    enabled: true
    size: 10Gi
    storageClass: managed-premium  # Use appropriate storage class

  auditStorage:
    enabled: true
    size: 10Gi

ui:
  enabled: true
  serviceType: LoadBalancer  # Change to ClusterIP with Ingress for production
  externalPort: 8200
```

**Step 3: Deploy Vault**
```bash
# Create namespace
kubectl create namespace vault

# Create TLS certificates (production requires real certs)
kubectl create secret generic vault-tls \
  --namespace vault \
  --from-file=tls.crt=./certs/vault.crt \
  --from-file=tls.key=./certs/vault.key

# Deploy Vault
helm install vault hashicorp/vault \
  --namespace vault \
  --values infra/vault/values.yaml

# Wait for pods to be ready
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=vault \
  --namespace vault \
  --timeout=5m
```

**Step 4: Initialize Vault**
```bash
# Initialize Vault (SAVE THESE KEYS SECURELY!)
kubectl exec -n vault vault-0 -- vault operator init \
  -key-shares=5 \
  -key-threshold=3 \
  -format=json > vault-keys.json

# IMPORTANT: Backup vault-keys.json to secure offline storage!
# Example: Encrypt and store in password manager
gpg --symmetric --cipher-algo AES256 vault-keys.json

# Extract unseal keys and root token
VAULT_UNSEAL_KEY_1=$(jq -r '.unseal_keys_b64[0]' vault-keys.json)
VAULT_UNSEAL_KEY_2=$(jq -r '.unseal_keys_b64[1]' vault-keys.json)
VAULT_UNSEAL_KEY_3=$(jq -r '.unseal_keys_b64[2]' vault-keys.json)
ROOT_TOKEN=$(jq -r '.root_token' vault-keys.json)

# Unseal all Vault pods
for i in 0 1 2; do
  kubectl exec -n vault vault-$i -- vault operator unseal $VAULT_UNSEAL_KEY_1
  kubectl exec -n vault vault-$i -- vault operator unseal $VAULT_UNSEAL_KEY_2
  kubectl exec -n vault vault-$i -- vault operator unseal $VAULT_UNSEAL_KEY_3
done

# Verify status
kubectl exec -n vault vault-0 -- vault status
```

### 1.3 Alternative: Vault on Azure Container Instances

For simpler Azure-only deployments, use Azure Container Instances:

```bash
# Create using provided Bicep module
az deployment group create \
  --resource-group rg-cloudhealthoffice-prod \
  --template-file infra/modules/vault-aci.bicep \
  --parameters vaultVersion=1.15.4 \
               dnsNameLabel=cho-vault-prod
```

See `infra/modules/vault-aci.bicep` for full implementation.

---

## 🔐 Phase 2: Vault Configuration

### 2.1 Enable Audit Logging

```bash
# Set VAULT_ADDR and VAULT_TOKEN
export VAULT_ADDR="https://vault.cloudhealthoffice.com:8200"
export VAULT_TOKEN="$ROOT_TOKEN"

# Enable file audit device
vault audit enable file file_path=/vault/audit/audit.log

# Verify audit logging
vault audit list
```

### 2.2 Enable Secrets Engines

```bash
# Enable KV v2 secrets engine for application secrets
vault secrets enable -path=secret kv-v2

# Enable Transit engine for encryption
vault secrets enable transit

# Create encryption key for PHI
vault write -f transit/keys/phi-encryption
```

### 2.3 Configure Authentication Methods

**Kubernetes Authentication (for microservices):**
```bash
# Enable Kubernetes auth
vault auth enable kubernetes

# Configure Kubernetes auth
vault write auth/kubernetes/config \
  kubernetes_host="https://$KUBERNETES_SERVICE_HOST:$KUBERNETES_SERVICE_PORT" \
  kubernetes_ca_cert=@/var/run/secrets/kubernetes.io/serviceaccount/ca.crt \
  token_reviewer_jwt=@/var/run/secrets/kubernetes.io/serviceaccount/token

# Create policy for microservices
vault policy write cho-microservices - <<EOF
path "secret/data/cloudhealthoffice/*" {
  capabilities = ["read", "list"]
}

path "transit/encrypt/phi-encryption" {
  capabilities = ["update"]
}

path "transit/decrypt/phi-encryption" {
  capabilities = ["update"]
}
EOF

# Create Kubernetes role
vault write auth/kubernetes/role/cho-microservices \
  bound_service_account_names=cho-service-account \
  bound_service_account_namespaces=cho-svcs \
  policies=cho-microservices \
  ttl=1h
```

**AppRole Authentication (for GitHub Actions):**
```bash
# Enable AppRole auth
vault auth enable approle

# Create policy for CI/CD
vault policy write cho-cicd - <<EOF
path "secret/data/deployment/*" {
  capabilities = ["read", "list"]
}
EOF

# Create AppRole
vault write auth/approle/role/github-actions \
  secret_id_ttl=0 \
  token_ttl=20m \
  token_max_ttl=30m \
  policies=cho-cicd

# Get Role ID (store in GitHub Variables)
vault read auth/approle/role/github-actions/role-id

# Generate Secret ID (store in GitHub Secrets)
vault write -f auth/approle/role/github-actions/secret-id
```

---

## 📦 Phase 3: Populate Secrets

### 3.1 Application Secrets

```bash
# Cosmos DB secrets
vault kv put secret/cloudhealthoffice/cosmosdb \
  connection-string="${COSMOS_CONNECTION_STRING}"

# Stripe secrets
vault kv put secret/cloudhealthoffice/stripe \
  secret-key="${STRIPE_SECRET_KEY}" \
  publishable-key="${STRIPE_PUBLISHABLE_KEY}" \
  webhook-secret="${STRIPE_WEBHOOK_SECRET}"

# Azure AD secrets (for portal)
vault kv put secret/cloudhealthoffice/azuread \
  client-secret="${AZURE_AD_CLIENT_SECRET}"

# SFTP credentials (per tenant)
vault kv put secret/cloudhealthoffice/sftp/clouddentaloffice \
  host="sftp.clearinghouse.example.com" \
  username="payer-health-plan-001" \
  password="${SFTP_PASSWORD}"

# Application Insights
vault kv put secret/cloudhealthoffice/appinsights \
  connection-string="${APPINSIGHTS_CONNECTION_STRING}"

# Service Bus
vault kv put secret/cloudhealthoffice/servicebus \
  connection-string="${SERVICEBUS_CONNECTION_STRING}"
```

### 3.2 Clearinghouse Credentials

```bash
# Availity (sandbox then production)
vault kv put secret/cloudhealthoffice/clearinghouse/availity \
  username="${AVAILITY_USERNAME}" \
  password="${AVAILITY_PASSWORD}" \
  sftp-host="sftp.availity.com"

# Change Healthcare
vault kv put secret/cloudhealthoffice/clearinghouse/changehealthcare \
  api-key="${CHANGE_API_KEY}" \
  client-id="${CHANGE_CLIENT_ID}" \
  client-secret="${CHANGE_CLIENT_SECRET}"
```

### 3.3 Verify Secrets

```bash
# List secrets
vault kv list secret/cloudhealthoffice/

# Read secret (without values - metadata only)
vault kv metadata get secret/cloudhealthoffice/cosmosdb

# Read secret value (for verification)
vault kv get secret/cloudhealthoffice/cosmosdb
```

---

## 🔧 Phase 4: Application Integration

### 4.1 Install VaultSharp NuGet Package

Add to all service `.csproj` files:
```xml
<PackageReference Include="VaultSharp" Version="1.13.0.1" />
<PackageReference Include="VaultSharp.Extensions.Configuration" Version="1.13.0.1" />
```

Or use the provided script:
```bash
./scripts/add-vault-packages.sh
```

### 4.2 Create Shared Vault Configuration Provider

Create `src/services/shared/Configuration/VaultConfigurationProvider.cs`:

```csharp
using VaultSharp;
using VaultSharp.V1.AuthMethods;
using VaultSharp.V1.AuthMethods.Kubernetes;
using VaultSharp.V1.AuthMethods.AppRole;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.Shared.Configuration;

public static class VaultConfigurationExtensions
{
    public static IConfigurationBuilder AddVaultConfiguration(
        this IConfigurationBuilder builder,
        IConfiguration bootstrapConfig)
    {
        var vaultEndpoint = bootstrapConfig["Vault:Address"] 
            ?? Environment.GetEnvironmentVariable("VAULT_ADDR");
        var vaultRole = bootstrapConfig["Vault:Role"] ?? "cho-microservices";
        var authMethod = bootstrapConfig["Vault:AuthMethod"] ?? "kubernetes";

        if (string.IsNullOrEmpty(vaultEndpoint))
        {
            // Vault not configured - skip
            return builder;
        }

        IAuthMethodInfo authMethodInfo = authMethod.ToLower() switch
        {
            "kubernetes" => new KubernetesAuthMethodInfo(vaultRole),
            "approle" => new AppRoleAuthMethodInfo(
                roleId: bootstrapConfig["Vault:RoleId"],
                secretId: bootstrapConfig["Vault:SecretId"]),
            _ => throw new ArgumentException($"Unsupported auth method: {authMethod}")
        };

        var vaultClientSettings = new VaultClientSettings(vaultEndpoint, authMethodInfo);
        var vaultClient = new VaultClient(vaultClientSettings);

        builder.Add(new VaultConfigurationSource
        {
            Client = vaultClient,
            BasePath = "secret/data/cloudhealthoffice",
            ReloadOnChange = true,
            ReloadInterval = TimeSpan.FromMinutes(5)
        });

        return builder;
    }
}

public class VaultConfigurationSource : IConfigurationSource
{
    public IVaultClient Client { get; set; }
    public string BasePath { get; set; }
    public bool ReloadOnChange { get; set; }
    public TimeSpan ReloadInterval { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new VaultConfigurationProvider(this);
    }
}

public class VaultConfigurationProvider : ConfigurationProvider
{
    private readonly VaultConfigurationSource _source;
    private Timer _reloadTimer;

    public VaultConfigurationProvider(VaultConfigurationSource source)
    {
        _source = source;
    }

    public override void Load()
    {
        LoadAsync().GetAwaiter().GetResult();

        if (_source.ReloadOnChange)
        {
            _reloadTimer = new Timer(
                _ => LoadAsync().GetAwaiter().GetResult(),
                null,
                _source.ReloadInterval,
                _source.ReloadInterval);
        }
    }

    private async Task LoadAsync()
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Read all secrets from base path
            var secrets = await _source.Client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
                path: _source.BasePath.Replace("secret/data/", ""),
                mountPoint: "secret");

            if (secrets?.Data?.Data != null)
            {
                FlattenSecrets(secrets.Data.Data, string.Empty, data);
            }
        }
        catch (Exception ex)
        {
            // Log error but don't crash application
            Console.WriteLine($"Failed to load Vault secrets: {ex.Message}");
        }

        Data = data;
        OnReload();
    }

    private void FlattenSecrets(
        IDictionary<string, object> secrets,
        string prefix,
        IDictionary<string, string> data)
    {
        foreach (var kvp in secrets)
        {
            var key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}:{kvp.Key}";

            if (kvp.Value is IDictionary<string, object> nestedDict)
            {
                FlattenSecrets(nestedDict, key, data);
            }
            else
            {
                data[key] = kvp.Value?.ToString();
            }
        }
    }
}
```

### 4.3 Update Program.cs in All Microservices

Update `Program.cs` in all 17 microservices:

```csharp
using CloudHealthOffice.Shared.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add Vault configuration (multi-cloud secret management)
if (builder.Environment.IsProduction())
{
    builder.Configuration.AddVaultConfiguration(builder.Configuration);
}

// Rest of configuration...
var app = builder.Build();
```

Example for specific service (`src/services/member-service/Program.cs`):
```csharp
using CloudHealthOffice.Shared.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add Vault configuration
if (builder.Environment.IsProduction() || builder.Environment.IsStaging())
{
    builder.Configuration.AddVaultConfiguration(builder.Configuration);
}

// Add services to container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Cosmos DB configuration (now from Vault)
builder.Services.AddSingleton<CosmosClient>(sp =>
{
    var connectionString = builder.Configuration["CosmosDb:ConnectionString"];
    return new CosmosClient(connectionString);
});

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

### 4.4 Update Kubernetes Deployments

**Create Kubernetes ServiceAccount:**

Create `infra/k8s/vault-serviceaccount.yaml`:
```yaml
apiVersion: v1
kind: ServiceAccount
metadata:
  name: cho-service-account
  namespace: cho-svcs
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: cho-tokenreview-binding
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: system:auth-delegator
subjects:
- kind: ServiceAccount
  name: cho-service-account
  namespace: cho-svcs
```

**Update Deployment Manifests:**

Add Vault configuration to deployments:
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: member-service
  namespace: cho-svcs
spec:
  template:
    spec:
      serviceAccountName: cho-service-account  # Use Vault-enabled SA
      containers:
      - name: member-service
        image: ghcr.io/aurelianware/cloudhealthoffice-member-service:latest
        env:
        - name: VAULT_ADDR
          value: "https://vault.vault.svc.cluster.local:8200"
        - name: Vault__Address
          value: "https://vault.vault.svc.cluster.local:8200"
        - name: Vault__Role
          value: "cho-microservices"
        - name: Vault__AuthMethod
          value: "kubernetes"
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        # Secrets now loaded from Vault
```

---

## 🔄 Phase 5: Update GitHub Actions Workflows

### 5.1 Update deploy.yml

Modify `.github/workflows/deploy.yml`:

```yaml
- name: Azure Login (OIDC)
  uses: azure/login@v2
  with:
    client-id: ${{ secrets.AZURE_CLIENT_ID }}
    tenant-id: ${{ secrets.AZURE_TENANT_ID }}
    subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

- name: Get Secrets from HashiCorp Vault
  id: vault-secrets
  shell: bash
  env:
    VAULT_ADDR: ${{ vars.VAULT_ADDR }}
    VAULT_ROLE_ID: ${{ vars.VAULT_ROLE_ID }}
    VAULT_SECRET_ID: ${{ secrets.VAULT_SECRET_ID }}
  run: |
    set -euo pipefail
    
    # Install Vault CLI
    wget -q https://releases.hashicorp.com/vault/1.15.4/vault_1.15.4_linux_amd64.zip
    unzip -q vault_1.15.4_linux_amd64.zip
    sudo mv vault /usr/local/bin/
    
    # Login to Vault using AppRole
    VAULT_TOKEN=$(vault write -field=token auth/approle/login \
      role_id="$VAULT_ROLE_ID" \
      secret_id="$VAULT_SECRET_ID")
    
    export VAULT_TOKEN
    
    # Retrieve secrets
    SFTP_HOST=$(vault kv get -field=host secret/cloudhealthoffice/sftp/clouddentaloffice)
    SFTP_USERNAME=$(vault kv get -field=username secret/cloudhealthoffice/sftp/clouddentaloffice)
    SFTP_PASSWORD=$(vault kv get -field=password secret/cloudhealthoffice/sftp/clouddentaloffice)
    
    # Mask secrets in logs
    echo "::add-mask::$SFTP_HOST"
    echo "::add-mask::$SFTP_USERNAME"
    echo "::add-mask::$SFTP_PASSWORD"
    
    # Export as environment variables
    {
      echo "SFTP_HOST=$SFTP_HOST"
      echo "SFTP_USERNAME=$SFTP_USERNAME"
      echo "SFTP_PASSWORD=$SFTP_PASSWORD"
    } >> "$GITHUB_ENV"
    
    echo "✓ Secrets retrieved from Vault successfully"

- name: Deploy Infrastructure
  uses: azure/arm-deploy@v2
  with:
    template: infra/main.bicep
    parameters: >
      baseName=${{ env.BASE_NAME }}
      location=${{ env.LOCATION }}
      sftpHost=${{ env.SFTP_HOST }}
      sftpUsername=${{ env.SFTP_USERNAME }}
      sftpPassword=${{ env.SFTP_PASSWORD }}
```

### 5.2 Add GitHub Variables and Secrets

**In GitHub Repository Settings → Secrets and variables:**

**Variables (non-sensitive):**
- `VAULT_ADDR`: `https://vault.cloudhealthoffice.com:8200`
- `VAULT_ROLE_ID`: (Get from `vault read auth/approle/role/github-actions/role-id`)

**Secrets (sensitive):**
- `VAULT_SECRET_ID`: (Get from `vault write -f auth/approle/role/github-actions/secret-id`)

---

## 🧪 Phase 6: Testing & Validation

### 6.1 Local Testing

```bash
# Test Vault connectivity
vault status

# Test secret retrieval
vault kv get secret/cloudhealthoffice/cosmosdb

# Test Kubernetes auth (from pod)
kubectl exec -it -n cho-svcs deployment/member-service -- \
  vault login -method=kubernetes role=cho-microservices
```

### 6.2 Staging Environment Testing

```bash
# Deploy to staging
kubectl apply -k infra/k8s/overlays/staging

# Check pod logs for Vault connection
kubectl logs -n cho-svcs-staging deployment/member-service | grep -i vault

# Test API with secrets from Vault
curl https://staging-api.cloudhealthoffice.com/api/v1/health
```

### 6.3 Smoke Tests

- [ ] Microservices start successfully and retrieve secrets from Vault
- [ ] Portal authenticates with Azure AD (client secret from Vault)
- [ ] Stripe integration works (API keys from Vault)
- [ ] Cosmos DB connections succeed (connection string from Vault)
- [ ] SFTP connections succeed (credentials from Vault)
- [ ] Application Insights telemetry flows (instrumentation key from Vault)
- [ ] No secrets visible in logs or environment variables

---

## 🔒 Security Best Practices

### Secret Rotation

**Automated Rotation with Vault:**
```bash
# Enable automatic rotation for dynamic secrets
vault write database/rotate-root/cosmosdb

# Configure rotation for static secrets
vault write sys/rotate/config \
  secret/cloudhealthoffice/sftp/clouddentaloffice \
  rotation_period="2160h"  # 90 days
```

### Access Control

**Principle of Least Privilege:**
```bash
# Create service-specific policies
vault policy write member-service - <<EOF
path "secret/data/cloudhealthoffice/cosmosdb" {
  capabilities = ["read"]
}

path "secret/data/cloudhealthoffice/servicebus" {
  capabilities = ["read"]
}
EOF

# Assign policy to role
vault write auth/kubernetes/role/member-service \
  bound_service_account_names=member-service-sa \
  bound_service_account_namespaces=cho-svcs \
  policies=member-service \
  ttl=1h
```

### Audit Logging

**Query Vault audit logs:**
```bash
# View recent audit entries
kubectl exec -n vault vault-0 -- tail -f /vault/audit/audit.log

# Parse with jq
kubectl exec -n vault vault-0 -- cat /vault/audit/audit.log | jq '.type, .auth.display_name, .request.path'
```

**Forward to centralized logging:**
```bash
# Configure Fluent Bit to forward Vault logs
# See infra/k8s/logging/fluent-bit-vault.yaml
```

---

## 🔄 Rollback Procedures

### Immediate Rollback to Azure Key Vault

If Vault integration causes issues:

**Step 1: Revert application changes**
```bash
# Revert Program.cs changes
git revert <commit-hash-of-vault-integration>
git push origin main
```

**Step 2: Update Kubernetes deployments**
```bash
# Remove Vault environment variables
kubectl set env deployment/member-service -n cho-svcs \
  VAULT_ADDR- \
  Vault__Address- \
  Vault__Role- \
  Vault__AuthMethod-

# Add Azure Key Vault reference
kubectl set env deployment/member-service -n cho-svcs \
  KeyVault__VaultUri=https://cho-prod-kv.vault.azure.net/
```

**Step 3: Verify rollback**
```bash
kubectl rollout status deployment/member-service -n cho-svcs
kubectl logs -f deployment/member-service -n cho-svcs --tail=50
```

---

## 📚 Reference Documentation

- [HashiCorp Vault Official Docs](https://www.vaultproject.io/docs)
- [Vault on Kubernetes](https://www.vaultproject.io/docs/platform/k8s)
- [VaultSharp .NET Client](https://github.com/rajanadar/VaultSharp)
- [Vault Best Practices](https://learn.hashicorp.com/tutorials/vault/pattern-centralized-secrets)
- [HIPAA Compliance with Vault](https://www.hashicorp.com/blog/hashicorp-vault-hipaa-compliance)

---

## 🆘 Troubleshooting

### Issue: "Vault is sealed"

**Symptoms:** API requests return 503 Service Unavailable

**Solution:**
```bash
# Unseal Vault using 3 of 5 unseal keys
kubectl exec -n vault vault-0 -- vault operator unseal <key1>
kubectl exec -n vault vault-0 -- vault operator unseal <key2>
kubectl exec -n vault vault-0 -- vault operator unseal <key3>
```

### Issue: "Permission denied" reading secrets

**Symptoms:** Application logs show "permission denied" errors

**Solution:**
```bash
# Verify policy is attached to role
vault read auth/kubernetes/role/cho-microservices

# Test with Vault CLI from pod
kubectl exec -it -n cho-svcs deployment/member-service -- sh
vault login -method=kubernetes role=cho-microservices
vault kv get secret/cloudhealthoffice/cosmosdb
```

### Issue: Network timeout connecting to Vault

**Symptoms:** Connection timeouts or "context deadline exceeded"

**Solution:**
```bash
# Verify Vault service is accessible
kubectl get svc -n vault

# Test DNS resolution
kubectl run -it --rm debug --image=busybox --restart=Never -- \
  nslookup vault.vault.svc.cluster.local

# Check network policies
kubectl get networkpolicies -n cho-svcs
```

---

## ✅ Migration Checklist

### Prerequisites
- [ ] HashiCorp Vault deployed (Kubernetes or ACI)
- [ ] Vault initialized and unsealed
- [ ] Authentication methods configured (Kubernetes + AppRole)
- [ ] Audit logging enabled

### Secret Population
- [ ] All application secrets migrated to Vault
- [ ] Clearinghouse credentials added
- [ ] Encryption keys configured (Transit engine)
- [ ] Secret retrieval verified

### Application Updates
- [ ] VaultSharp packages added to all services
- [ ] Shared configuration provider created
- [ ] All 17 microservices updated
- [ ] Kubernetes ServiceAccount created
- [ ] Deployments updated with Vault config

### Workflow Updates
- [ ] GitHub Actions workflows updated
- [ ] Vault AppRole credentials stored in GitHub
- [ ] Secret retrieval tested in CI/CD
- [ ] Old Azure Key Vault references removed

### Testing
- [ ] DEV environment deployed successfully
- [ ] UAT environment tested
- [ ] Production smoke tests passed
- [ ] Rollback procedure documented and tested

### Documentation
- [ ] SECURITY.md updated
- [ ] ARCHITECTURE.md updated
- [ ] Deployment guides updated
- [ ] Team training completed

---

**Maintained by:** Cloud Health Office DevOps Team  
**Last Updated:** 2026-02-19  
**Next Review:** 2026-05-19 (Quarterly review)
