// =========================
// Deployment Key Vault Module
// =========================
// Purpose: Store deployment secrets (SFTP credentials, API keys, connection strings)
// Separate from runtime Key Vault for better security isolation
// HIPAA-compliant configuration with audit logging and RBAC

@description('Key Vault name for deployment secrets')
param keyVaultName string

@description('Azure region for Key Vault')
param location string = resourceGroup().location

@description('Environment (DEV, UAT, PROD)')
@allowed(['DEV', 'UAT', 'PROD'])
param environment string = 'PROD'

@description('SKU for Key Vault - Premium recommended for HIPAA compliance')
@allowed(['standard', 'premium'])
param skuName string = 'premium'

@description('Enable RBAC authorization (recommended for managed identities)')
param enableRbacAuthorization bool = true

@description('Enable soft delete with retention period')
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
param enabledForTemplateDeployment bool = true

// IMPORTANT: Default 'Enabled' is for CI/CD (e.g., GitHub Actions). For production/HIPAA workloads, explicitly review this and typically set to 'Disabled' with private endpoints.
@description('Public network access for Key Vault (review for production/HIPAA; consider Disabled with private endpoints)')
@allowed(['Enabled', 'Disabled'])
param publicNetworkAccess string = 'Enabled'

@description('Network ACL default action')
@allowed(['Allow', 'Deny'])
param networkAclsDefaultAction string = 'Allow'

@description('Array of allowed IP ranges for Key Vault access (optional)')
param allowedIpRanges array = []

@description('Array of VNet subnet IDs for Key Vault access (optional)')
param allowedSubnetIds array = []

@description('Log Analytics Workspace ID for diagnostic logs')
param logAnalyticsWorkspaceId string = ''

@description('Tags for Key Vault resource')
param tags object = {
  Environment: environment
  Purpose: 'DeploymentSecrets'
  Compliance: 'HIPAA'
  ManagedBy: 'Bicep'
  Repository: 'aurelianware/cloudhealthoffice'
}

// =========================
// Key Vault Resource
// =========================
resource deploymentKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: skuName
    }
    tenantId: subscription().tenantId

    // RBAC-based access control (recommended for managed identities and Service Principals)
    enableRbacAuthorization: enableRbacAuthorization

    // Soft delete and purge protection for data recovery and compliance
    enableSoftDelete: enableSoftDelete
    softDeleteRetentionInDays: softDeleteRetentionInDays
    enablePurgeProtection: enablePurgeProtection

    // Enable for Azure service integrations
    enabledForDiskEncryption: enabledForDiskEncryption
    enabledForDeployment: false  // Not needed for deployment Key Vault
    enabledForTemplateDeployment: enabledForTemplateDeployment

    // Network security
    publicNetworkAccess: publicNetworkAccess
    networkAcls: {
      defaultAction: networkAclsDefaultAction
      bypass: 'AzureServices'  // Allow trusted Azure platform services to bypass network ACLs (GitHub Actions uses publicNetworkAccess, not this bypass)
      ipRules: [
        for ipRange in allowedIpRanges: {
          value: ipRange
        }
      ]
      virtualNetworkRules: [
        for subnetId in allowedSubnetIds: {
          id: subnetId
          ignoreMissingVnetServiceEndpoint: false
        }
      ]
    }
  }
}

// =========================
// Diagnostic Settings for Audit Logging
// =========================
resource diagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01' = if (!empty(logAnalyticsWorkspaceId)) {
  name: '${keyVaultName}-diagnostics'
  scope: deploymentKeyVault
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        category: 'AuditEvent'
        enabled: true
        retentionPolicy: {
          enabled: true
          days: 365  // 1 year retention for HIPAA audit requirements
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
output keyVaultId string = deploymentKeyVault.id

@description('Key Vault name')
output keyVaultName string = deploymentKeyVault.name

@description('Key Vault URI')
output keyVaultUri string = deploymentKeyVault.properties.vaultUri

@description('Key Vault location')
output location string = deploymentKeyVault.location

// =========================
// Usage Notes
// =========================
// This Key Vault is designed to store deployment secrets that are:
// 1. Retrieved by GitHub Actions workflows after OIDC authentication
// 2. Used during infrastructure deployment (e.g., SFTP credentials)
// 3. Separate from runtime secrets used by AKS workloads during execution
//
// RBAC Roles Required:
// - Service Principal (GitHub Actions): "Key Vault Secrets User"
// - DevOps Team: "Key Vault Administrator"
// - CI/CD System: "Key Vault Secrets User"
//
// Secret Naming Convention:
// - Use lowercase with hyphens: sftp-host, sftp-username, sftp-password
// - Prefix with service if needed: claims-api-key, clearinghouse-sftp-host
//
// Network Access:
// - Public network access enabled for GitHub Actions
// - Consider private endpoints for enhanced security (requires VNet)
// - Network ACLs can be configured to allow specific IP ranges
//
// Compliance Features:
// - Premium SKU provides HSM-backed key storage
// - Soft delete with 90-day retention prevents accidental deletion
// - Purge protection ensures secrets cannot be permanently deleted during retention
// - Audit logging captures all access and modifications
// - RBAC provides fine-grained access control
