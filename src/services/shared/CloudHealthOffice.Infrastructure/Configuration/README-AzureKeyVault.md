# Azure Key Vault Integration

## Required Azure Resources

| Resource | Detail |
|----------|--------|
| **Key Vault** | Premium SKU (HSM-backed keys) |
| **AKS Workload Identity** | Federated credential per service's managed identity |
| **RBAC Role Assignments** | `Key Vault Secrets User` per managed identity (NOT Vault-level access policies) |
| **Network Rules** | Deny public access; allow AKS subnet + Azure trusted services |
| **Diagnostic Settings** | Ship audit logs to Log Analytics workspace |

## appsettings.json Configuration

```json
{
  "SecretProvider": {
    "Provider": "AzureKeyVault",
    "AzureKeyVaultUri": "https://cho-prod-kv.vault.azure.net/",
    "ReloadIntervalSeconds": 300,
    "GracefulDegradation": true
  }
}
```

Set `Provider` to `"None"` (or omit the section entirely) to disable Key Vault and use the `NullSecretProvider`.

## Local Development Setup

No special configuration needed — `DefaultAzureCredential` tries these in order:

1. **Environment variables** (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET`)
2. **Workload Identity** (AKS pods)
3. **Managed Identity** (Azure VMs, App Service)
4. **Visual Studio** credential
5. **Azure CLI** (`az login`)

For local dev, just run:

```bash
az login
```

`DefaultAzureCredential` will pick up the CLI token automatically.

## Secret Naming Convention

Azure Key Vault does not allow `:` in secret names. Use `--` (double-dash) as the hierarchy delimiter. The configuration provider maps `--` → `:` automatically.

| Key Vault Secret Name | .NET Configuration Key |
|------------------------|----------------------|
| `CosmosDb--ConnectionString` | `CosmosDb:ConnectionString` |
| `Redis--Password` | `Redis:Password` |
| `Clearinghouse--ApiKey` | `Clearinghouse:ApiKey` |

## Per-Service RBAC (Recommended)

Each microservice should have its own Managed Identity with only `Key Vault Secrets User` scoped to the secrets it actually needs. Avoid granting broad access to all secrets.

Example (Bicep):

```bicep
resource kvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, claimsServiceIdentity.id, keyVaultSecretsUserRole)
  properties: {
    roleDefinitionId: keyVaultSecretsUserRole
    principalId: claimsServiceIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
```

## Rotation

Set a 90-day rotation policy on secrets in Key Vault. The `SecretProviderConfigurationProvider` reloads every `ReloadIntervalSeconds` (default 5 minutes), so rotated secrets are picked up automatically without pod restarts.
