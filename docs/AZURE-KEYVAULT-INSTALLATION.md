# Azure Key Vault Installation Guide

## Architecture Overview

There are two paths for secrets to reach CHO microservices. **Phase A** is the quick
win; **Phase B** is the end goal that eliminates Kubernetes Secrets entirely.

### Phase A — CI/CD Bridge (deploy first)

```
Azure Key Vault
      │
      ▼
GitHub Actions  ──  az keyvault secret show
      │
      ▼
kubectl create secret generic  ──►  K8s Secret (etcd)
      │
      ▼
Pod env vars  ──►  appsettings / IConfiguration
```

- Workflow fetches from Key Vault when `KEY_VAULT_NAME` is set
- Falls back to GitHub Secrets when it's not (backwards compatible)
- Secrets still land in K8s Secrets — acceptable as a transitional step

### Phase B — CSI Direct Mount (end goal)

```
Azure Key Vault
      │
      ▼
CSI SecretStore Driver  ──►  volume mount in pod
      │
      ▼
ISecretProvider  ──►  IConfiguration overlay
```

- Pods authenticate directly to Key Vault via Workload Identity
- No secrets in etcd, env vars, or K8s Secret manifests
- Automatic reload every 5 minutes (configurable)
- Graceful degradation on transient Key Vault failures

---

## Prerequisites

- Azure CLI (`az`) authenticated with Contributor + User Access Administrator
- AKS cluster with managed identity (not service principal)
- `kubectl` configured to target the cluster
- `helm` installed (for CSI driver)

---

## Phase A: CI/CD Bridge Setup

### Step 1: Provision Key Vault

```bash
./scripts/setup-azure-keyvault.sh \
  --resource-group rg-cloudhealthoffice-prod \
  --vault-name cho-app-kv \
  --aks-cluster cho-aks \
  --location eastus \
  --log-analytics cho-logs
```

This creates the Key Vault with Premium SKU, RBAC auth, purge protection,
network deny rules, diagnostic logging, and AKS kubelet RBAC.

### Step 2: Populate Secrets

```bash
# Copy the template and fill in real values
cp scripts/secrets-manifest.example.env scripts/secrets-manifest.env
# ⚠  Edit scripts/secrets-manifest.env with real values — NEVER commit this file

./scripts/populate-keyvault-secrets.sh \
  --vault-name cho-app-kv \
  --file scripts/secrets-manifest.env
```

### Step 3: Validate Access

```bash
./scripts/validate-keyvault-access.sh --vault-name cho-app-kv --verbose
```

### Step 4: Enable in GitHub Actions

```bash
# Set the variable that activates Key Vault fetching in the workflow
gh variable set KEY_VAULT_NAME -b 'cho-app-kv'

# These should already be set for OIDC login:
# gh secret set AZURE_CLIENT_ID -b '...'
# gh secret set AZURE_TENANT_ID -b '...'
# gh secret set AZURE_SUBSCRIPTION_ID -b '...'
```

### Step 5: Deploy and Verify

Trigger a deployment and check the "Fetch secrets from Azure Key Vault" step
in the workflow logs. Each secret should show "Fetched" or "Not found" (falls
back to GitHub Secret).

### Step 6: Remove GitHub Secrets (when ready)

Only after verifying all secrets are in Key Vault and deployments succeed:

```bash
gh secret delete COSMOS_DB_CONNECTION_STRING
gh secret delete STRIPE_SECRET_KEY
# ... etc
```

---

## Phase B: CSI Direct Mount + Workload Identity

### Step 1: Install the CSI Driver Add-on

```bash
az aks enable-addons \
  --addons azure-keyvault-secrets-provider \
  --name cho-aks \
  --resource-group rg-cloudhealthoffice-prod
```

Verify:

```bash
kubectl get pods -n kube-system -l app=secrets-store-csi-driver
kubectl get pods -n kube-system -l app=secrets-store-provider-azure
```

### Step 2: Enable OIDC Issuer and Workload Identity on AKS

```bash
az aks update \
  --name cho-aks \
  --resource-group rg-cloudhealthoffice-prod \
  --enable-oidc-issuer \
  --enable-workload-identity

# Get the OIDC issuer URL (needed for federated credentials)
AKS_OIDC_ISSUER=$(az aks show \
  --name cho-aks \
  --resource-group rg-cloudhealthoffice-prod \
  --query "oidcIssuerProfile.issuerUrl" -o tsv)

echo "OIDC Issuer: $AKS_OIDC_ISSUER"
```

### Step 3: Create Managed Identities and Federated Credentials

Each service needs its own managed identity for per-service RBAC scoping.
Run for **each service** in the table below:

```bash
SERVICE_NAME="claims-service"   # ← change per service
RESOURCE_GROUP="rg-cloudhealthoffice-prod"
KEY_VAULT_NAME="cho-app-kv"
NAMESPACE="cloudhealthoffice"

# 1. Create managed identity
az identity create \
  --name "${SERVICE_NAME}-identity" \
  --resource-group "$RESOURCE_GROUP"

# 2. Get identity details
CLIENT_ID=$(az identity show \
  --name "${SERVICE_NAME}-identity" \
  --resource-group "$RESOURCE_GROUP" \
  --query clientId -o tsv)

PRINCIPAL_ID=$(az identity show \
  --name "${SERVICE_NAME}-identity" \
  --resource-group "$RESOURCE_GROUP" \
  --query principalId -o tsv)

# 3. Create federated credential linking K8s SA → managed identity
az identity federated-credential create \
  --name "${SERVICE_NAME}-fed-cred" \
  --identity-name "${SERVICE_NAME}-identity" \
  --resource-group "$RESOURCE_GROUP" \
  --issuer "$AKS_OIDC_ISSUER" \
  --subject "system:serviceaccount:${NAMESPACE}:${SERVICE_NAME}-sa" \
  --audiences "api://AzureADTokenExchange"

# 4. Assign Key Vault Secrets User to the identity
KV_ID=$(az keyvault show --name "$KEY_VAULT_NAME" --query id -o tsv)

az role assignment create \
  --assignee-object-id "$PRINCIPAL_ID" \
  --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" \
  --scope "$KV_ID"

echo "Service: $SERVICE_NAME"
echo "Client ID: $CLIENT_ID  ← use in ServiceAccount annotation"
```

### Step 4: Create Kubernetes ServiceAccounts

For each service, create the SA with the Workload Identity annotation:

```bash
SERVICE_NAME="claims-service"
CLIENT_ID="<from step 3>"

cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: ServiceAccount
metadata:
  name: ${SERVICE_NAME}-sa
  namespace: cloudhealthoffice
  annotations:
    azure.workload.identity/client-id: "${CLIENT_ID}"
  labels:
    azure.workload.identity/use: "true"
EOF
```

### Step 5: Apply the SecretProviderClass

Edit `infrastructure/k8s/secret-provider-class.yaml` to replace placeholders:

```bash
KEY_VAULT_NAME="cho-app-kv"
TENANT_ID="<your-azure-ad-tenant-id>"
CLIENT_ID="<workload-identity-client-id>"

sed \
  -e "s/<KEY_VAULT_NAME>/$KEY_VAULT_NAME/g" \
  -e "s/<AZURE_TENANT_ID>/$TENANT_ID/g" \
  -e "s/<WORKLOAD_IDENTITY_CLIENT_ID>/$CLIENT_ID/g" \
  infrastructure/k8s/secret-provider-class.yaml \
  | kubectl apply -f -
```

### Step 6: Update Deployment Manifests

Add the ServiceAccount and CSI volume to each service's deployment YAML:

```yaml
spec:
  serviceAccountName: <service-name>-sa    # Workload Identity SA
  containers:
    - name: <service-name>
      # ... existing config ...
      volumeMounts:
        - name: secrets-store
          mountPath: /mnt/secrets-store
          readOnly: true
      env:
        - name: SecretProvider__Provider
          value: "AzureKeyVault"
        - name: SecretProvider__AzureKeyVaultUri
          value: "https://cho-app-kv.vault.azure.net/"
  volumes:
    - name: secrets-store
      csi:
        driver: secrets-store.csi.k8s.io
        readOnly: true
        volumeAttributes:
          secretProviderClass: azure-kv-secrets
```

### Step 7: Remove K8s Secret References

Once CSI mounts are verified working, remove the `envFrom` / `env.valueFrom`
references to the old K8s Secrets from deployment manifests. The ISecretProvider
reads directly from Key Vault — no env vars needed.

---

## Service Inventory — Workload Identity Checklist

Each service below needs Steps 3-6 completed. Track progress here.

### .NET Microservices (29)

| # | Service | Identity Created | Fed Cred | RBAC | SA Applied | Deployment Updated |
|---|---------|:---:|:---:|:---:|:---:|:---:|
| 1 | appeals-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 2 | ar-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 3 | attachment-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 4 | authorization-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 5 | benefit-plan-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 6 | capitation-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 7 | claims-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 8 | coverage-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 9 | eligibility-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 10 | encounter-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 11 | enrollment-import-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 12 | ffs-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 13 | fhir-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 14 | member-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 15 | payment-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 16 | premium-billing-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 17 | provider-contracts-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 18 | provider-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 19 | provider-verification-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 20 | reference-data-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 21 | rfai-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 22 | risk-adjustment-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 23 | smart-auth-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 24 | sponsor-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 25 | tenant-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 26 | trading-partner-service | ☐ | ☐ | ☐ | ☐ | ☐ |
| 27 | CHO.TerminologyService | ☐ | ☐ | ☐ | ☐ | ☐ |
| 28 | CloudHealthOffice.PricingApi | ☐ | ☐ | ☐ | ☐ | ☐ |

### Blazor Portal (1)

| # | Service | Identity Created | Fed Cred | RBAC | SA Applied | Deployment Updated |
|---|---------|:---:|:---:|:---:|:---:|:---:|
| 30 | CloudHealthOffice.Portal | ☐ | ☐ | ☐ | ☐ | ☐ |

### Non-.NET Containers (8) — No Workload Identity Needed

These containers receive secrets via K8s Secret env vars (populated by the CI/CD
workflow from Key Vault). They do NOT use ISecretProvider.

| Container | Secret Source |
|-----------|--------------|
| x12-parser | K8s Secret env vars (via CI/CD) |
| x12-276-parser | K8s Secret env vars (via CI/CD) |
| x12-834-parser | K8s Secret env vars (via CI/CD) |
| x12-encoder | K8s Secret env vars (via CI/CD) |
| claims-publisher | K8s Secret env vars (via CI/CD) |
| kafka-publisher | K8s Secret env vars (via CI/CD) |
| sftp-fetcher | K8s Secret env vars (via CI/CD) |
| metadata-extractor | K8s Secret env vars (via CI/CD) |

---

## Secret Rotation

### Check for expiring secrets

```bash
# Table output (interactive)
./scripts/rotate-keyvault-secrets.sh --vault-name cho-app-kv --days 14

# JSON output (for CI/CD alerting)
./scripts/rotate-keyvault-secrets.sh --vault-name cho-app-kv --days 30 --format json
```

### Rotate a secret

```bash
# 1. Generate new value externally (Stripe dashboard, Azure portal, etc.)
# 2. Update in Key Vault
az keyvault secret set \
  --vault-name cho-app-kv \
  --name "Stripe--SecretKey" \
  --value "sk_live_new_value_here" \
  --expires "$(date -u -d '+90 days' +%Y-%m-%dT%H:%M:%SZ)"

# 3. Services pick up new value automatically:
#    - Phase A: next CI/CD deployment
#    - Phase B: within ReloadIntervalSeconds (default 5 minutes)
```

---

## Rollback

### Revert to GitHub Secrets only

```bash
# Remove the variable — workflow falls back to GitHub Secrets
gh variable delete KEY_VAULT_NAME
```

No code changes needed. The `${KV_*:-${{ secrets.* }}}` pattern in the
workflow means GitHub Secrets are always the fallback.

### Disable ISecretProvider in a service

Set the environment variable in the deployment manifest:

```yaml
env:
  - name: SecretProvider__Provider
    value: "None"
```

The NullSecretProvider returns nulls for everything — the service falls back
to its existing appsettings / env var configuration.
