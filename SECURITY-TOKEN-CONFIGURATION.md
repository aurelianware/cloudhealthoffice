# Security Token Configuration Guide

## Overview

This guide explains how to securely configure the `QNXT_API_TOKEN` parameter in Logic App workflows using Azure Key Vault references or Managed Identity, eliminating the need to store secrets in workflow JSON files or deployment configurations.

## ⚠️ Security Requirements

**CRITICAL**: Never commit API tokens, secrets, or credentials to source code or configuration files. All sensitive values must be stored in Azure Key Vault and referenced at runtime.

## What Changed

### Before (Insecure ❌)
```json
"parameters": {
  "qnxt_api_token": {
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
    "defaultValue": "SECURITY: Do NOT store secrets in workflow JSON. Configure QNXT_API_TOKEN as an app setting using Azure Key Vault reference..."
  },
  "QNXT_API_TOKEN": {
    "type": "SecureString"
  }
}
```

## Configuration Methods

### Method 1: Azure Key Vault Reference (Recommended)

This method stores the token in Azure Key Vault and references it from Logic App application settings.

#### Step 1: Store Secret in Key Vault

```bash
# Store the QNXT API token in Azure Key Vault
az keyvault secret set \
  --vault-name "your-keyvault-name" \
  --name "qnxt-api-token" \
  --value "your-actual-qnxt-token-value"

# Get the secret URI
az keyvault secret show \
  --vault-name "your-keyvault-name" \
  --name "qnxt-api-token" \
  --query "id" -o tsv
# Output: https://your-keyvault-name.vault.azure.net/secrets/qnxt-api-token/abc123...
```

#### Step 2: Grant Logic App Access to Key Vault

```bash
# Get the Logic App's managed identity principal ID
PRINCIPAL_ID=$(az webapp identity show \
  --name "your-logic-app-name" \
  --resource-group "your-resource-group" \
  --query principalId -o tsv)

# Grant Key Vault Secrets User role
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Key Vault Secrets User" \
  --scope "/subscriptions/YOUR_SUBSCRIPTION_ID/resourceGroups/YOUR_RG/providers/Microsoft.KeyVault/vaults/YOUR_KEYVAULT"
```

#### Step 3: Configure Logic App Application Setting

```bash
# Set the application setting with Key Vault reference
az webapp config appsettings set \
  --name "your-logic-app-name" \
  --resource-group "your-resource-group" \
  --settings QNXT_API_TOKEN="@Microsoft.KeyVault(SecretUri=https://your-keyvault-name.vault.azure.net/secrets/qnxt-api-token)"
```

#### Step 4: Update Workflow Parameters

When deploying workflows, ensure the parameter references the app setting:

```json
{
  "definition": {
    "$schema": "...",
    "parameters": {
      "QNXT_API_TOKEN": {
        "type": "SecureString"
      }
    }
  },
  "parameters": {
    "QNXT_API_TOKEN": {
      "value": "@appsetting('QNXT_API_TOKEN')"
    }
  }
}
```

### Method 2: Managed Identity with Dynamic Token Acquisition

For even greater security, use Azure Managed Identity to obtain tokens dynamically at runtime without storing them.

#### Step 1: Configure QNXT API to Accept Azure AD Tokens

Work with your QNXT API provider to configure Azure AD authentication.

#### Step 2: Modify Workflow to Use Managed Identity

Update the HTTP action in your workflow to use Managed Identity authentication:

```json
{
  "Call_QNXT_API": {
    "type": "Http",
    "inputs": {
      "method": "POST",
      "uri": "@{parameters('qnxt_base_url')}/api/endpoint",
      "authentication": {
        "type": "ManagedServiceIdentity",
        "audience": "https://your-qnxt-api.com"
      },
      "headers": {
        "Content-Type": "application/json"
      },
      "body": { ... }
    }
  }
}
```

## Affected Workflows

The following workflows have been updated to use `QNXT_API_TOKEN` (SecureString):

1. **ingest275** - 275 attachment ingestion (1 API call)
   - Action: `Call_QNXT_Claim_Linkage_API`
   
2. **ingest278** - 278 processing (1 API call)
   - Action: `Call_QNXT_278_API`
   
3. **process_authorizations** - Authorization processing (3 API calls)
   - Actions:
     - `Call_QNXT_Eligibility_API`
     - `Call_QNXT_Claims_Verification_API`
     - `Call_QNXT_Authorization_API`
   
4. **process_appeals** - Appeals processing (2 API calls)
   - Actions:
     - `Correlate_Appeal_With_Claim`
     - `Call_QNXT_Appeals_API`

## Deployment via GitHub Actions

### Update Workflow YAML

Ensure your GitHub Actions deployment workflow includes the app setting configuration:

```yaml
- name: Configure Logic App Application Settings
  run: |
    az webapp config appsettings set \
      --name "${{ vars.LOGIC_APP_NAME }}" \
      --resource-group "${{ vars.RESOURCE_GROUP }}" \
      --settings \
        QNXT_API_TOKEN="@Microsoft.KeyVault(SecretUri=${{ secrets.KEYVAULT_QNXT_TOKEN_URI }})"
```

### Required GitHub Secrets

Add these secrets to your GitHub repository:

- `KEYVAULT_QNXT_TOKEN_URI`: The full Key Vault secret URI
  - Example: `https://your-keyvault.vault.azure.net/secrets/qnxt-api-token`

## Verification

### Check Application Settings

```bash
# Verify the app setting is configured correctly
az webapp config appsettings list \
  --name "your-logic-app-name" \
  --resource-group "your-resource-group" \
  --query "[?name=='QNXT_API_TOKEN'].{Name:name, Value:value}" -o table
```

Expected output:
```
Name              Value
----------------  ------------------------------------------------------------------
QNXT_API_TOKEN    @Microsoft.KeyVault(SecretUri=https://your-keyvault...
```

### Test Workflow Execution

1. Trigger a workflow manually or via normal process
2. Check Application Insights for successful API calls
3. Verify no token values appear in logs (should show `[REDACTED]` or similar)

## Troubleshooting

### Error: "Key Vault operation failed"

**Cause**: Logic App's managed identity doesn't have permission to read the secret.

**Solution**: Grant the Key Vault Secrets User role:
```bash
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Key Vault Secrets User" \
  --scope "/subscriptions/.../Microsoft.KeyVault/vaults/YOUR_KEYVAULT"
```

### Error: "Parameter 'QNXT_API_TOKEN' not found"

**Cause**: The workflow parameter is not properly configured.

**Solution**: Ensure the workflow parameters.json includes:
```json
{
  "QNXT_API_TOKEN": {
    "value": "@appsetting('QNXT_API_TOKEN')"
  }
}
```

### Error: "401 Unauthorized" from QNXT API

**Cause**: The token value in Key Vault is incorrect or expired.

**Solution**: Update the token in Key Vault:
```bash
az keyvault secret set \
  --vault-name "your-keyvault-name" \
  --name "qnxt-api-token" \
  --value "new-token-value"
```

## Security Best Practices

### 1. Token Rotation
- Rotate QNXT API tokens regularly (recommended: every 90 days)
- Update the Key Vault secret value
- Logic App automatically picks up the new value on next workflow run

### 2. Access Control
- Use Azure RBAC to limit who can read Key Vault secrets
- Grant minimum necessary permissions to Logic App managed identity
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
