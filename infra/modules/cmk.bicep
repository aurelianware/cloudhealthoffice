// =========================
// Customer-Managed Keys (CMK) Module - OPTIONAL
// =========================
// BYOK (Bring Your Own Key) encryption for storage accounts
// For organizations requiring key control and rotation policies
// HSM-backed keys in Premium Key Vault

@description('Key Vault resource ID for CMK')
param keyVaultId string

@description('Key Vault URI')
param keyVaultUri string

@description('Storage Account name')
param storageAccountName string

@description('CMK key name')
param cmkKeyName string = 'storage-encryption-key'

@description('Key type')
@allowed([
  'RSA'
  'RSA-HSM'  // Hardware Security Module backed (Premium Key Vault only)
])
param keyType string = 'RSA-HSM'

@description('Key size in bits')
@allowed([
  2048
  3072
  4096
])
param keySize int = 4096

@description('Key operations')
param keyOps array = [
  'encrypt'
  'decrypt'
  'wrapKey'
  'unwrapKey'
]

@description('Enable key rotation')
param enableKeyRotation bool = true

@description('Key rotation policy - rotate every 90 days')
param rotationPolicyDays int = 90

@description('Tags for CMK resources')
param tags object = {
  Environment: 'Production'
  Compliance: 'HIPAA'
  KeyManagement: 'Customer-Managed'
  ManagedBy: 'Bicep'
}

// =========================
// Key Vault Reference
// =========================
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: last(split(keyVaultId, '/'))
}

// =========================
// CMK Key for Storage Encryption
// =========================
resource cmkKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyVault
  name: cmkKeyName
  tags: tags
  properties: {
    kty: keyType
    keySize: keySize
    keyOps: keyOps
    attributes: {
      enabled: true
    }
    rotationPolicy: enableKeyRotation ? {
      attributes: {
        expiryTime: 'P${rotationPolicyDays}D'
      }
      lifetimeActions: [
        {
          trigger: {
            timeBeforeExpiry: 'P7D'  // Rotate 7 days before expiry
          }
          action: {
            type: 'Rotate'
          }
        }
        {
          trigger: {
            timeAfterCreate: 'P${rotationPolicyDays - 7}D'  // Notify before rotation
          }
          action: {
            type: 'Notify'
          }
        }
      ]
    } : null
  }
}

// =========================
// NOTE: CMK Configuration
// =========================
// To enable CMK encryption on a storage account:
// 1. Ensure storage account has system-assigned managed identity
// 2. Grant storage account managed identity "Key Vault Crypto Service Encryption User" role
// 3. Configure storage account encryption.keySource = 'Microsoft.Keyvault'
// 4. Set keyvaultproperties with keyVaultUri, keyName, and optional keyVersion
//
// This module only creates the CMK key in Key Vault
// Storage account configuration must be done in main.bicep

// =========================
// Outputs
// =========================
@description('CMK Key resource ID')
output cmkKeyId string = cmkKey.id

@description('CMK Key name')
output cmkKeyName string = cmkKey.name

@description('CMK Key URI')
output cmkKeyUri string = cmkKey.properties.keyUri

@description('CMK Key version')
output cmkKeyVersion string = last(split(cmkKey.properties.keyUriWithVersion, '/'))
