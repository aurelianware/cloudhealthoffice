// =========================
// Key Vault Module - HIPAA Compliant
// =========================
// Premium SKU with HSM-backed keys support
// Soft delete (90 days), purge protection enabled
// Network ACLs defaulting to deny
// RBAC authorization
// Diagnostic settings for audit logging

@description('Key Vault name')
param keyVaultName string

@description('Azure region for Key Vault')
param location string = resourceGroup().location

@description('SKU for Key Vault')
@allowed([
  'standard'
  'premium'  // Premium provides HSM-backed keys
])
param skuName string = 'premium'

@description('Enable RBAC authorization instead of access policies')
param enableRbacAuthorization bool = true

@description('Enable soft delete with 90-day retention')
param enableSoftDelete bool = true

@description('Soft delete retention in days (90 for HIPAA compliance)')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 90

@description('Enable purge protection (cannot be disabled once enabled)')
param enablePurgeProtection bool = true

@description('Enable Key Vault for disk encryption')
param enabledForDiskEncryption bool = true

@description('Enable Key Vault for template deployment')
param enabledForDeployment bool = false

@description('Enable Key Vault for ARM template deployment')
param enabledForTemplateDeployment bool = true

@description('Network ACL default action')
@allowed([
  'Allow'
  'Deny'
])
param networkAclsDefaultAction string = 'Deny'

@description('Array of allowed IP ranges for Key Vault access')
param allowedIpRanges array = []

@description('Array of VNet subnet IDs for Key Vault access')
param allowedSubnetIds array = []

@description('Enable public network access')
param publicNetworkAccess string = 'Disabled'  // Disabled for private endpoint only access

@description('Tags for Key Vault resource')
param tags object = {
  Environment: 'Production'
  Compliance: 'HIPAA'
  CostCenter: 'Security'
  ManagedBy: 'Bicep'
}

@description('Log Analytics Workspace ID for diagnostic logs')
param logAnalyticsWorkspaceId string = ''

// =========================
// Key Vault Resource
// =========================
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: skuName
    }
    tenantId: subscription().tenantId
    
    // RBAC-based access control (recommended for managed identities)
    enableRbacAuthorization: enableRbacAuthorization
    
    // Soft delete and purge protection for data recovery
    enableSoftDelete: enableSoftDelete
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enablePurgeProtection: enablePurgeProtection
    
    // Enable for various Azure services
    enabledForDiskEncryption: enabledForDiskEncryption
    enabledForDeployment: enabledForDeployment
    enabledForTemplateDeployment: enabledForTemplateDeployment
    
    // Network security
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: networkAclsDefaultAction
      bypass: 'AzureServices'  // Allow trusted Azure services
      ipRules: [for ipRange in allowedIpRanges: {
        value: ipRange
      }]
      virtualNetworkRules: [for subnetId in allowedSubnetIds: {
        id: subnetId
        ignoreMissingVnetServiceEndpoint: false
      }]
    }
  }
}

// =========================
// Diagnostic Settings for Audit Logging
// =========================
resource diagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(logAnalyticsWorkspaceId)) {
  name: '${keyVaultName}-diagnostics'
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
      {
        category: 'AzurePolicyEvaluationDetails'
        enabled: true
        retentionPolicy: {
          enabled: true
          days: 90
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

@description('Key Vault URI')
output keyVaultUri string = keyVault.properties.vaultUri


