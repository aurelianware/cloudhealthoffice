# Azure Key Vault Auto-Configuration

This document explains how to use the Azure Key Vault auto-configuration feature in Cloud Health Office services.

## Overview

The services in Cloud Health Office now support automatic configuration from Azure Key Vault. When configured, services will automatically load secrets from Key Vault at startup, eliminating the need to manually set environment variables for sensitive configuration.

## Features

- ✅ **Auto-discovery**: Automatically loads secrets from Key Vault when `KEY_VAULT_URI` is set
- ✅ **Fallback support**: Falls back to environment variables if Key Vault is not configured
- ✅ **Managed Identity**: Uses Azure Managed Identity for authentication (no keys required)
- ✅ **HIPAA compliance**: Integrates with HIPAA-compliant Key Vault infrastructure
- ✅ **Zero code changes**: Works transparently with existing configuration code

## Supported Services

- **eligibility-service**: Auto-loads Cosmos DB, Event Grid, and backend API secrets

## Quick Start

### 1. Set Key Vault URI

Set the `KEY_VAULT_URI` environment variable to your Azure Key Vault URI:

```bash
export KEY_VAULT_URI="https://my-keyvault.vault.azure.net/"
```

### 2. Configure Managed Identity

Ensure your service has a managed identity assigned and has the appropriate Key Vault role:

```bash
# Assign "Key Vault Secrets User" role to the managed identity
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee <managed-identity-principal-id> \
  --scope /subscriptions/<subscription-id>/resourceGroups/<rg>/providers/Microsoft.KeyVault/vaults/<vault-name>
```

### 3. Populate Key Vault with Secrets

Store your secrets in Key Vault using the appropriate naming convention:

```bash
# Example: Store Cosmos DB endpoint
az keyvault secret set \
  --vault-name my-keyvault \
  --name cosmos-endpoint \
  --value "https://my-cosmos.documents.azure.com:443/"

# Example: Store backend API token
az keyvault secret set \
  --vault-name my-keyvault \
  --name backend-api-token \
  --value "your-secret-token"
```

### 4. Start the Service

The service will automatically load secrets from Key Vault on startup:

```bash
npm start
```

You should see log messages like:

```
[Startup] Auto-configuring Key Vault...
[KeyVault] Initialized client for https://my-keyvault.vault.azure.net/
[KeyVault] Loaded COSMOS_ENDPOINT from Key Vault secret: cosmos-endpoint
[KeyVault] Loaded BACKEND_API_TOKEN from Key Vault secret: backend-api-token
[KeyVault] Auto-configured 2 secrets from Key Vault
[Startup] Key Vault auto-configuration complete
```

## Secret Naming Convention

Secrets in Key Vault follow a kebab-case naming convention. Environment variable names are automatically converted:

| Environment Variable | Key Vault Secret Name |
|---------------------|----------------------|
| `COSMOS_ENDPOINT` | `cosmos-endpoint` |
| `COSMOS_KEY` | `cosmos-key` |
| `STORAGE_ACCOUNT_NAME` | `storage-account-name` |
| `STORAGE_CONNECTION_STRING` | `storage-connection-string` |
| `EVENT_GRID_ENDPOINT` | `event-grid-endpoint` |
| `EVENT_GRID_KEY` | `event-grid-key` |
| `BACKEND_BASE_URL` | `backend-base-url` |
| `BACKEND_API_TOKEN` | `backend-api-token` |
| `KAFKA_BOOTSTRAP_SERVERS` | `kafka-bootstrap-servers` |
| `KAFKA_SASL_USERNAME` | `kafka-sasl-username` |
| `KAFKA_SASL_PASSWORD` | `kafka-sasl-password` |

## Priority Order

The auto-configuration follows this priority order:

1. **Environment Variable** (if already set) - highest priority
2. **Key Vault Secret** (if available and `KEY_VAULT_URI` is set)
3. **Default Value** (hardcoded defaults in the code) - lowest priority

This means you can:
- Override Key Vault secrets by setting environment variables
- Use Key Vault for production secrets
- Use default values for development/testing

## Common Secrets

The following secrets are automatically loaded by both services:

### Cosmos DB
- `COSMOS_ENDPOINT`: Cosmos DB endpoint URL
- `COSMOS_KEY`: Cosmos DB access key (optional with managed identity)

### Storage
- `STORAGE_ACCOUNT_NAME`: Azure Storage account name
- `STORAGE_CONNECTION_STRING`: Storage connection string

### Event Grid
- `EVENT_GRID_ENDPOINT`: Event Grid topic endpoint
- `EVENT_GRID_KEY`: Event Grid access key (optional with managed identity)

### Backend API
- `BACKEND_BASE_URL`: Claims backend API base URL
- `BACKEND_API_TOKEN`: Backend API authentication token

## Development Mode

For local development without Key Vault:

1. **Option 1**: Don't set `KEY_VAULT_URI`
   - Services will use environment variables and defaults

2. **Option 2**: Set environment variables explicitly
   ```bash
   export COSMOS_ENDPOINT="https://localhost:8081"
   export BACKEND_BASE_URL="http://localhost:5000"
   ```

3. **Option 3**: Use a `.env` file (with dotenv loader)
   ```
   COSMOS_ENDPOINT=https://localhost:8081
   BACKEND_BASE_URL=http://localhost:5000
   ```

## Production Deployment

### Azure Container Apps

```yaml
env:
  - name: KEY_VAULT_URI
    value: "https://prod-keyvault.vault.azure.net/"
identity:
  type: SystemAssigned
```

### Kubernetes with Azure Workload Identity

```yaml
apiVersion: v1
kind: Pod
metadata:
  name: eligibility-service
  labels:
    azure.workload.identity/use: "true"
spec:
  serviceAccountName: eligibility-service-sa
  containers:
  - name: service
    image: cloudhealthoffice/eligibility-service:latest
    env:
    - name: KEY_VAULT_URI
      value: "https://prod-keyvault.vault.azure.net/"
```

### Azure App Service / Azure Functions

Set `KEY_VAULT_URI` in Application Settings. The managed identity is automatically configured.

## Troubleshooting

### Service can't access Key Vault

**Error**: `Failed to initialize Key Vault client`

**Solution**: Ensure:
1. `KEY_VAULT_URI` is correctly formatted
2. Managed identity is assigned to the service
3. Managed identity has "Key Vault Secrets User" role
4. Network access allows the service to reach Key Vault

### Secret not found

**Error**: `Secret {name} not found in Key Vault`

**Solution**: 
1. Check secret name matches the naming convention (kebab-case)
2. Verify secret exists in Key Vault: `az keyvault secret list --vault-name <vault>`
3. Check if secret is marked as required in the code

### Service still uses environment variables

**Behavior**: Key Vault auto-configuration runs, but env vars are used

**Explanation**: This is expected! Environment variables have higher priority than Key Vault. If an env var is already set, it won't be overridden by Key Vault.

**Solution**: Unset the environment variable if you want to use Key Vault:
```bash
unset COSMOS_ENDPOINT
```

## Security Best Practices

1. **Use Premium Key Vault** for HIPAA compliance (HSM-backed keys)
2. **Enable audit logging** to track secret access
3. **Rotate secrets regularly** using Key Vault rotation policies
4. **Use managed identity** instead of service principals when possible
5. **Limit Key Vault access** to specific managed identities
6. **Enable soft delete and purge protection** on production Key Vaults

## Integration with Deployment Pipeline

The Key Vault auto-configuration works seamlessly with the existing deployment pipeline:

1. Deploy infrastructure with Key Vault (using Bicep templates)
2. Populate Key Vault with secrets (using deployment scripts)
3. Deploy services with `KEY_VAULT_URI` configured
4. Services auto-load secrets on startup

See [DEPLOYMENT-SECRETS-SETUP.md](../../DEPLOYMENT-SECRETS-SETUP.md) for full deployment guide.

## Related Documentation

- [DEPLOYMENT-SECRETS-SETUP.md](../../DEPLOYMENT-SECRETS-SETUP.md) - Full secrets management guide
- [infra/modules/keyvault.bicep](../../infra/modules/keyvault.bicep) - Key Vault infrastructure
- [infra/modules/deployment-keyvault.bicep](../../infra/modules/deployment-keyvault.bicep) - Deployment Key Vault

## Example: Complete Setup

```bash
# 1. Create Key Vault
az keyvault create \
  --name prod-cho-kv \
  --resource-group prod-rg \
  --location eastus \
  --enable-rbac-authorization true

# 2. Assign managed identity role
PRINCIPAL_ID=$(az containerapp show --name eligibility-service --resource-group prod-rg --query identity.principalId -o tsv)
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee $PRINCIPAL_ID \
  --scope /subscriptions/<sub-id>/resourceGroups/prod-rg/providers/Microsoft.KeyVault/vaults/prod-cho-kv

# 3. Store secrets
az keyvault secret set --vault-name prod-cho-kv --name cosmos-endpoint --value "https://prod-cosmos.documents.azure.com:443/"
az keyvault secret set --vault-name prod-cho-kv --name backend-api-token --value "prod-token-xyz"

# 4. Configure service
az containerapp update \
  --name eligibility-service \
  --resource-group prod-rg \
  --set-env-vars KEY_VAULT_URI=https://prod-cho-kv.vault.azure.net/

# 5. Service auto-loads secrets on next restart
az containerapp restart --name eligibility-service --resource-group prod-rg
```

## Support

For issues or questions:
- Check logs for `[KeyVault]` prefixed messages
- Verify Key Vault access using Azure Portal
- Review Application Insights for detailed error traces
- File an issue in the repository with logs
