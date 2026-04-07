// =========================
// Application Key Vault Module
// =========================
// Secret provider for microservice application secrets.
// Distinct from the deployment Key Vault (deployment-keyvault.bicep).
// Premium SKU with HSM-backed keys, RBAC authorization,
// AKS kubelet RBAC, network deny + AKS subnet, audit diagnostics.

@description('Key Vault resource name')
param name string

@description('Azure region')
param location string = resourceGroup().location

@description('Key Vault SKU')
@allowed([
  'standard'
  'premium'
])
param skuName string = 'premium'

@description('AKS kubelet managed identity principal ID — granted Key Vault Secrets User')
param aksKubeletIdentityPrincipalId string

@description('AKS subnet resource ID for network ACL virtual network rules')
param aksSubnetId string

@description('Log Analytics workspace resource ID for diagnostic settings')
param logAnalyticsWorkspaceId string = ''

@description('Resource tags')
param tags object = {}

// =========================
// Key Vault Resource
// =========================
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: skuName
    }
    tenantId: subscription().tenantId

    // RBAC-based access control (recommended for managed identities)
    enableRbacAuthorization: true

    // Soft delete and purge protection for data recovery
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true

    // Not needed for application secret storage
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false

    // Network security — deny by default, allow AKS subnet + Azure trusted services
    // Phase B: switch to 'Disabled' after provisioning a private endpoint + DNS zone
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      ipRules: []
      virtualNetworkRules: empty(aksSubnetId) ? [] : [
        {
          id: aksSubnetId
          ignoreMissingVnetServiceEndpoint: false
        }
      ]
    }
  }
}

// =========================
// RBAC: Key Vault Secrets User for AKS Kubelet Identity
// =========================
// Role definition ID for "Key Vault Secrets User": 4633458b-17de-408a-b874-0445c86b69e6
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource aksSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(aksKubeletIdentityPrincipalId)) {
  name: guid(keyVault.id, aksKubeletIdentityPrincipalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: aksKubeletIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// =========================
// Diagnostic Settings — AuditEvent to Log Analytics
// =========================
resource diagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(logAnalyticsWorkspaceId)) {
  name: '${name}-diagnostics'
  scope: keyVault
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        category: 'AuditEvent'
        enabled: true
        retentionPolicy: {
          enabled: true
          days: 365  // 1 year retention for HIPAA compliance
        }
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
        retentionPolicy: {
          enabled: true
          days: 90
        }
      }
    ]
  }
}

// =========================
// Outputs
// =========================
@description('Key Vault resource ID')
output keyVaultId string = keyVault.id

@description('Key Vault name')
output keyVaultName string = keyVault.name

@description('Key Vault URI (e.g. https://cho-app-kv.vault.azure.net/)')
output keyVaultUri string = keyVault.properties.vaultUri
