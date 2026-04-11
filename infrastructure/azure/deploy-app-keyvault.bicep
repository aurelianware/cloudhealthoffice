// =============================================================================
// Standalone deployment wrapper for modules/app-keyvault.bicep
// =============================================================================
// Provisions ONLY the application Key Vault, without re-running the full
// main.bicep (which provisions storage, integration accounts, service bus,
// AKS, and other resources). Safe to run repeatedly — the bicep module is
// idempotent and the role assignment uses a deterministic guid.
//
// The module enforces the HIPAA-ready defaults already defined in
// modules/app-keyvault.bicep: Premium SKU, RBAC authorization, soft-delete
// with 90-day retention, purge protection, network-ACL default-deny with
// AKS subnet allow-list, 365-day diagnostic audit logging.
//
// Usage:
//   az deployment group create \
//     --resource-group rg-cloudhealthoffice-prod \
//     --template-file infrastructure/azure/deploy-app-keyvault.bicep \
//     --parameters \
//         aksKubeletIdentityPrincipalId=<kubelet-oid> \
//         aksSubnetId=<aks-subnet-resource-id> \
//         logAnalyticsWorkspaceId=<workspace-resource-id>
//
// The companion parameter queries (run these once, paste into the command):
//   az aks show -g <rg> -n <cluster> \
//     --query identityProfile.kubeletidentity.objectId -o tsv
//   az aks show -g <rg> -n <cluster> \
//     --query agentPoolProfiles[0].vnetSubnetID -o tsv
//   az monitor log-analytics workspace show -g <rg> -n <workspace> \
//     --query id -o tsv
// =============================================================================

targetScope = 'resourceGroup'

@description('Base name prefix. Final vault name will be {baseName}-app-kv.')
@minLength(3)
@maxLength(20)
param baseName string = 'cloudhealthoffice'

@description('Azure region; defaults to the resource group location.')
param location string = resourceGroup().location

@description('Key Vault SKU. HIPAA workloads should use premium for HSM-backed keys.')
@allowed([
  'standard'
  'premium'
])
param skuName string = 'premium'

@description('AKS kubelet managed identity principal ID. Granted Key Vault Secrets User on the vault so pods can read secrets via workload identity. Query with: az aks show -g <rg> -n <cluster> --query identityProfile.kubeletidentity.objectId -o tsv')
param aksKubeletIdentityPrincipalId string

@description('AKS subnet resource ID. Added to the vault network ACL virtual-network rule allow-list so pods reach Key Vault over the service endpoint. Query with: az aks show -g <rg> -n <cluster> --query agentPoolProfiles[0].vnetSubnetID -o tsv')
param aksSubnetId string

@description('Log Analytics workspace resource ID for audit event diagnostics. Leave empty to skip (not recommended for HIPAA). Query with: az monitor log-analytics workspace show -g <rg> -n <workspace> --query id -o tsv')
param logAnalyticsWorkspaceId string = ''

@description('Environment tag applied to the vault resource.')
@allowed([
  'Production'
  'UAT'
  'Development'
])
param environment string = 'Production'

// cloudhealthoffice (17 chars) + '-app-kv' (7 chars) = 24 chars exactly —
// Key Vault names are limited to 3-24 characters globally. Longer baseName
// values will fail deployment.
var vaultName = '${baseName}-app-kv'

module appKeyVault 'modules/app-keyvault.bicep' = {
  name: 'deploy-app-keyvault-${baseName}'
  params: {
    name: vaultName
    location: location
    skuName: skuName
    aksKubeletIdentityPrincipalId: aksKubeletIdentityPrincipalId
    aksSubnetId: aksSubnetId
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    tags: {
      Environment: environment
      Compliance: 'HIPAA'
      CostCenter: 'Security'
      ManagedBy: 'Bicep'
      Purpose: 'ApplicationSecrets'
      DeployedVia: 'deploy-app-keyvault.bicep'
    }
  }
}

@description('Resource ID of the provisioned Key Vault.')
output vaultId string = appKeyVault.outputs.keyVaultId

@description('Name of the provisioned Key Vault.')
output vaultName string = appKeyVault.outputs.keyVaultName

@description('URI of the provisioned Key Vault (e.g. https://cloudhealthoffice-app-kv.vault.azure.net/). Set this as SecretProvider:AzureKeyVaultUri in runtime config.')
output vaultUri string = appKeyVault.outputs.keyVaultUri
