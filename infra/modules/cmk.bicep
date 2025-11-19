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

// ================================================================
// ⚠️  IMPORTANT: Post-Deployment CMK Configuration Required
// ================================================================
// This module creates the encryption key in Key Vault but does NOT configure
// storage accounts to use it. Additional manual steps are required after deployment.
//
// COMPLETE IMPLEMENTATION GUIDE:
// See SECURITY-HARDENING.md § "Customer-Managed Keys (Optional)" for full step-by-step
// instructions including Azure CLI commands and verification steps.
//
// QUICK REFERENCE - Required Steps After Deploying This Module:
//
// 1. Enable managed identity on storage account:
//    az storage account update --name <storage-name> --resource-group <rg> --assign-identity
//
// 2. Get storage account principal ID:
//    STORAGE_IDENTITY=$(az storage account show --name <storage-name> --resource-group <rg> \
//                       --query identity.principalId -o tsv)
//
// 3. Grant Key Vault Crypto Service Encryption User role:
//    az role assignment create --assignee $STORAGE_IDENTITY \
//                              --role "Key Vault Crypto Service Encryption User" \
//                              --scope <cmk-key-id>
//
// 4. Configure storage account to use CMK:
//    az storage account update --name <storage-name> --resource-group <rg> \
//                              --encryption-key-source Microsoft.Keyvault \
//                              --encryption-key-vault "https://<vault-name>.vault.azure.net/" \
//                              --encryption-key-name <cmk-key-name>
//
// 5. Verify encryption configuration:
//    az storage account show --name <storage-name> --resource-group <rg> --query "encryption"
//
// For automated deployment scripts, consider creating a separate Bicep module or shell script
// that orchestrates both this CMK module and the storage account configuration steps above.

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
