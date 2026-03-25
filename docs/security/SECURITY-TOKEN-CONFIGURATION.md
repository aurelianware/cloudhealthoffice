# Security Token Configuration Guide

## Overview

This guide explains how to securely configure the `CLAIMS_BACKEND_API_TOKEN` parameter in Argo Workflows on AKS using Azure Key Vault references (via Secrets Store CSI Driver) or Kubernetes Workload Identity, eliminating the need to store secrets in workflow YAML files or deployment configurations.

## ⚠️ Security Requirements

**CRITICAL**: Never commit API tokens, secrets, or credentials to source code or configuration files. All sensitive values must be stored in Azure Key Vault and referenced at runtime.

## What Changed

### Before (Insecure ❌)
```json
"parameters": {
  "claims_backend_api_token": {
    "type": "String",
    "defaultValue": "<token>"
  }
}
```

### After (Secure ✅)
```json
"parameters": {
  "_SECURITY_NOTE": {
    "type": "String",
    "defaultValue": "SECURITY: Do NOT store secrets in workflow JSON. Configure CLAIMS_BACKEND_API_TOKEN as an app setting using Azure Key Vault reference..."
  },
  "CLAIMS_BACKEND_API_TOKEN": {
    "type": "SecureString"
  }
}
```

## Configuration Methods

### Method 1: Azure Key Vault Reference (Recommended)

This method stores the token in Azure Key Vault and syncs it to Kubernetes secrets via the Secrets Store CSI Driver.

#### Step 1: Store Secret in Key Vault

```bash
# Store the claims backend API token in Azure Key Vault
az keyvault secret set \
  --vault-name "your-keyvault-name" \
  --name "claims-backend-api-token" \
  --value "your-actual-backend-token-value"

# Get the secret URI
az keyvault secret show \
  --vault-name "your-keyvault-name" \
  --name "claims-backend-api-token" \
  --query "id" -o tsv
# Output: https://your-keyvault-name.vault.azure.net/secrets/claims-backend-api-token/abc123...
```

#### Step 2: Grant AKS Workload Identity Access to Key Vault

```bash
# Get the AKS cluster's kubelet managed identity principal ID
PRINCIPAL_ID=$(az aks show \
  --name "your-aks-cluster-name" \
  --resource-group "your-resource-group" \
  --query "identityProfile.kubeletidentity.objectId" -o tsv)

# Grant Key Vault Secrets User role
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Key Vault Secrets User" \
  --scope "/subscriptions/YOUR_SUBSCRIPTION_ID/resourceGroups/YOUR_RG/providers/Microsoft.KeyVault/vaults/YOUR_KEYVAULT"
```

#### Step 3: Configure Kubernetes Secret via Secrets Store CSI Driver

```bash
# Create a SecretProviderClass to sync the Key Vault secret into a K8s secret
kubectl apply -f - <<EOF
apiVersion: secrets-store.csi.x-k8s.io/v1
kind: SecretProviderClass
metadata:
  name: claims-backend-token
  namespace: argo
spec:
  provider: azure
  parameters:
    keyvaultName: "your-keyvault-name"
    objects: |
      array:
        - |
          objectName: claims-backend-api-token
          objectType: secret
    tenantId: "your-tenant-id"
  secretObjects:
    - secretName: claims-backend-api-token
      type: Opaque
      data:
        - objectName: claims-backend-api-token
          key: token
EOF
```

#### Step 4: Reference Secret in Argo Workflow Steps

When deploying Argo Workflows, reference the Kubernetes secret in workflow step environment variables:

```yaml
# In the Argo Workflow template
containers:
  - name: call-claims-backend
    env:
      - name: CLAIMS_BACKEND_API_TOKEN
        valueFrom:
          secretKeyRef:
            name: claims-backend-api-token
            key: token
```

### Method 2: Workload Identity with Dynamic Token Acquisition

For even greater security, use Azure Workload Identity (pod-level managed identity) to obtain tokens dynamically at runtime without storing them.

#### Step 1: Configure claims backend API to Accept Azure AD Tokens

Work with your claims backend API provider to configure Azure AD authentication.

#### Step 2: Configure AKS Workload Identity Federation

Enable Workload Identity on the AKS cluster and configure a federated credential for the service account used by Argo Workflow pods:

```bash
# Enable Workload Identity on AKS (if not already enabled)
az aks update \
  --resource-group "your-resource-group" \
  --name "your-aks-cluster" \
  --enable-oidc-issuer \
  --enable-workload-identity

# Create a Kubernetes service account annotated with the Azure identity
kubectl apply -f - <<EOF
apiVersion: v1
kind: ServiceAccount
metadata:
  name: argo-claims-sa
  namespace: argo
  annotations:
    azure.workload.identity/client-id: "your-azure-ad-app-client-id"
EOF
```

## Affected Workflows

The following Argo Workflow steps have been updated to use `CLAIMS_BACKEND_API_TOKEN` (via Kubernetes secret):

1. **ingest275** - 275 attachment ingestion (1 API call)
   - Action: `Call_Claims_Backend_Claim_Linkage_API`
   
2. **ingest278** - 278 processing (1 API call)
   - Action: `Call_Claims_Backend_278_API`
   
3. **process_authorizations** - Authorization processing (3 API calls)
   - Actions:
     - `Call_Claims_Backend_Eligibility_API`
     - `Call_Claims_Backend_Claims_Verification_API`
     - `Call_Claims_Backend_Authorization_API`
   
4. **process_appeals** - Appeals processing (2 API calls)
   - Actions:
     - `Correlate_Appeal_With_Claim`
     - `Call_Claims_Backend_Appeals_API`

## Deployment via GitHub Actions

### Update Deployment Workflow YAML

Ensure your GitHub Actions deployment workflow deploys the SecretProviderClass and syncs secrets to Kubernetes:

```yaml
- name: Deploy Secrets Store CSI Driver Configuration
  run: |
    kubectl apply -f infra/k8s/secret-provider-claims-backend.yaml -n argo
```

### Required GitHub Secrets

Add these secrets to your GitHub repository:

- `AZURE_CLIENT_ID`: The Azure AD app client ID used for Workload Identity
- Key Vault secret URI is referenced in the SecretProviderClass YAML, not as a GitHub secret

## Verification

### Check Kubernetes Secret

```bash
# Verify the Kubernetes secret is synced from Key Vault
kubectl get secret claims-backend-api-token -n argo -o jsonpath='{.data.token}' | base64 -d | head -c 5
# Should show the first 5 characters of the token (for verification only)

# Verify the SecretProviderClass is deployed
kubectl get secretproviderclass claims-backend-token -n argo
```

### Test Workflow Execution

1. Trigger an Argo Workflow manually or via normal process
2. Check Application Insights for successful API calls
3. Verify no token values appear in pod logs (should show `[REDACTED]` or similar)

## Troubleshooting

### Error: "Key Vault operation failed"

**Cause**: AKS workload identity doesn't have permission to read the secret.

**Solution**: Grant the Key Vault Secrets User role to the AKS kubelet identity:
```bash
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Key Vault Secrets User" \
  --scope "/subscriptions/.../Microsoft.KeyVault/vaults/YOUR_KEYVAULT"
```

### Error: "Secret not found in pod environment"

**Cause**: The Kubernetes secret is not properly synced from Key Vault via Secrets Store CSI Driver.

**Solution**: Verify the SecretProviderClass and ensure a pod with the CSI volume has been started:
```bash
# Check SecretProviderClass status
kubectl describe secretproviderclass claims-backend-token -n argo

# Ensure the CSI driver pod is running
kubectl get pods -n kube-system -l app=secrets-store-csi-driver
```

### Error: "401 Unauthorized" from claims backend API

**Cause**: The token value in Key Vault is incorrect or expired.

**Solution**: Update the token in Key Vault:
```bash
az keyvault secret set \
  --vault-name "your-keyvault-name" \
  --name "claims-backend-api-token" \
  --value "new-token-value"
```

## Security Best Practices

### 1. Token Rotation
- Rotate claims backend API tokens regularly (recommended: every 90 days)
- Update the Key Vault secret value
- Secrets Store CSI Driver automatically syncs the new value (based on rotation poll interval)

### 2. Access Control
- Use Azure RBAC to limit who can read Key Vault secrets
- Grant minimum necessary permissions to AKS workload identity
- Use separate Key Vaults for DEV/UAT/PROD environments

### 3. Audit and Monitoring
- Enable Key Vault diagnostic logs
- Monitor access to secrets using Azure Monitor
- Set up alerts for unauthorized access attempts

### 4. Network Security
- Use private endpoints for Key Vault (recommended for HIPAA compliance)
- Restrict Key Vault network access to specific subnets
- Enable Key Vault firewall rules

## Related Documentation

- [SECURITY-HARDENING.md](SECURITY-HARDENING.md) - Complete security hardening guide
- [DEPLOYMENT.md](DEPLOYMENT.md) - Deployment procedures including Key Vault setup
- [SECURITY.md](SECURITY.md) - Overall security practices and requirements

## Support

For issues or questions about secure token configuration:
1. Review the troubleshooting section above
2. Check Application Insights logs for detailed error messages
3. Consult the Azure Key Vault documentation: https://docs.microsoft.com/azure/key-vault/
