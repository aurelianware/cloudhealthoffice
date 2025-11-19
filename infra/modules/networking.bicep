// =========================
// Networking Module - VNet and Private DNS Zones
// =========================
// VNet with subnets for Logic Apps and Private Endpoints
// Private DNS zones for Storage, Service Bus, and Key Vault
// Network security for HIPAA-compliant isolation

@description('Virtual Network name')
param vnetName string

@description('Azure region for VNet')
param location string = resourceGroup().location

@description('VNet address prefix')
param vnetAddressPrefix string = '10.0.0.0/16'

@description('Logic Apps subnet address prefix')
param logicAppsSubnetPrefix string = '10.0.1.0/24'

@description('Private Endpoints subnet address prefix')
param privateEndpointsSubnetPrefix string = '10.0.2.0/24'

@description('Tags for networking resources')
param tags object = {
  Environment: 'Production'
  Compliance: 'HIPAA'
  ManagedBy: 'Bicep'
}

// =========================
// Virtual Network
// =========================
resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: 'logic-apps-subnet'
        properties: {
          addressPrefix: logicAppsSubnetPrefix
          delegations: [
            {
              name: 'delegation'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
          serviceEndpoints: [
            {
              service: 'Microsoft.Storage'
              locations: [
                location
              ]
            }
            {
              service: 'Microsoft.ServiceBus'
              locations: [
                location
              ]
            }
            {
              service: 'Microsoft.KeyVault'
              locations: [
                location
              ]
            }
          ]
        }
      }
      {
        name: 'private-endpoints-subnet'
        properties: {
          addressPrefix: privateEndpointsSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
          privateLinkServiceNetworkPolicies: 'Enabled'
        }
      }
    ]
  }
}

// =========================
// Private DNS Zone for Storage (blob)
// =========================
resource privateDnsZoneStorage 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.blob.${environment().suffixes.storage}'
  location: 'global'
  tags: tags
}

resource privateDnsZoneStorageLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: privateDnsZoneStorage
  name: '${vnetName}-storage-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

// =========================
// Private DNS Zone for Service Bus
// =========================
resource privateDnsZoneServiceBus 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.servicebus.windows.net'
  location: 'global'
  tags: tags
}

resource privateDnsZoneServiceBusLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: privateDnsZoneServiceBus
  name: '${vnetName}-servicebus-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

// =========================
// Private DNS Zone for Key Vault
// =========================
resource privateDnsZoneKeyVault 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
}

resource privateDnsZoneKeyVaultLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: privateDnsZoneKeyVault
  name: '${vnetName}-keyvault-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

// =========================
// Outputs
// =========================
@description('Virtual Network resource ID')
output vnetId string = vnet.id

@description('Virtual Network name')
output vnetName string = vnet.name

@description('Logic Apps subnet resource ID')
output logicAppsSubnetId string = vnet.properties.subnets[0].id

@description('Private Endpoints subnet resource ID')
output privateEndpointsSubnetId string = vnet.properties.subnets[1].id

@description('Storage Private DNS Zone resource ID')
output storageDnsZoneId string = privateDnsZoneStorage.id

@description('Service Bus Private DNS Zone resource ID')
output serviceBusDnsZoneId string = privateDnsZoneServiceBus.id

@description('Key Vault Private DNS Zone resource ID')
output keyVaultDnsZoneId string = privateDnsZoneKeyVault.id
