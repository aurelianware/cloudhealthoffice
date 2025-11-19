// =========================
// Private Endpoints Module
// =========================
// Private endpoints for Storage Account (blob), Service Bus, and Key Vault
// Network isolation for HIPAA-compliant PHI workloads

@description('Azure region for private endpoints')
param location string = resourceGroup().location

@description('Private Endpoints subnet resource ID')
param subnetId string

@description('Storage Account resource ID')
param storageAccountId string

@description('Storage Account name')
param storageAccountName string

@description('Service Bus namespace resource ID')
param serviceBusId string

@description('Service Bus namespace name')
param serviceBusName string

@description('Key Vault resource ID')
param keyVaultId string

@description('Key Vault name')
param keyVaultName string

@description('Storage Private DNS Zone resource ID')
param storageDnsZoneId string

@description('Service Bus Private DNS Zone resource ID')
param serviceBusDnsZoneId string

@description('Key Vault Private DNS Zone resource ID')
param keyVaultDnsZoneId string

@description('Tags for private endpoints')
param tags object = {
  Environment: 'Production'
  Compliance: 'HIPAA'
  ManagedBy: 'Bicep'
}

// =========================
// Private Endpoint for Storage Account (blob)
// =========================
resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = {
  name: '${storageAccountName}-blob-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${storageAccountName}-blob-connection'
        properties: {
          privateLinkServiceId: storageAccountId
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

resource storageDnsGroupBlob 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = {
  parent: storagePrivateEndpoint
  name: 'blob-dns-zone-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config1'
        properties: {
          privateDnsZoneId: storageDnsZoneId
        }
      }
    ]
  }
}

// =========================
// Private Endpoint for Service Bus
// =========================
resource serviceBusPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = {
  name: '${serviceBusName}-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${serviceBusName}-connection'
        properties: {
          privateLinkServiceId: serviceBusId
          groupIds: [
            'namespace'
          ]
        }
      }
    ]
  }
}

resource serviceBusDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = {
  parent: serviceBusPrivateEndpoint
  name: 'servicebus-dns-zone-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config1'
        properties: {
          privateDnsZoneId: serviceBusDnsZoneId
        }
      }
    ]
  }
}

// =========================
// Private Endpoint for Key Vault
// =========================
resource keyVaultPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = {
  name: '${keyVaultName}-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: subnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${keyVaultName}-connection'
        properties: {
          privateLinkServiceId: keyVaultId
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
}

resource keyVaultDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = {
  parent: keyVaultPrivateEndpoint
  name: 'keyvault-dns-zone-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config1'
        properties: {
          privateDnsZoneId: keyVaultDnsZoneId
        }
      }
    ]
  }
}

// =========================
// Outputs
// =========================
@description('Storage Account Private Endpoint resource ID')
output storagePrivateEndpointId string = storagePrivateEndpoint.id

@description('Service Bus Private Endpoint resource ID')
output serviceBusPrivateEndpointId string = serviceBusPrivateEndpoint.id

@description('Key Vault Private Endpoint resource ID')
output keyVaultPrivateEndpointId string = keyVaultPrivateEndpoint.id
