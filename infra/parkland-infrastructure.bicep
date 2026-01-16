// Parkland Community Health Plan - Infrastructure Template
// Deploys Azure resources for Parkland's private CHO integration environment
// Network: Spoke VNet connected to Parkland Hospital ExpressRoute

targetScope = 'resourceGroup'

@description('Base name for all resources (e.g., parkland-cho)')
@minLength(3)
@maxLength(20)
param baseName string = 'parkland-cho'

@description('Azure region for deployment')
param location string = resourceGroup().location

@description('Environment (dev, uat, prod)')
@allowed(['dev', 'uat', 'prod'])
param environment string = 'dev'

@description('Hub VNet resource ID (containing ExpressRoute gateway)')
param hubVNetId string

@description('Enable private endpoints for all services')
param enablePrivateEndpoints bool = true

@description('Tags for all resources')
param tags object = {
  Organization: 'Parkland Community Health Plan'
  Environment: environment
  ManagedBy: 'Infrastructure-as-Code'
  CostCenter: 'IT-Integration'
  Compliance: 'HIPAA'
}

// ============================================================================
// VARIABLES
// ============================================================================

var storageAccountName = '${replace(baseName, '-', '')}storage${uniqueString(resourceGroup().id)}'
var keyVaultName = '${baseName}-kv-${environment}'
var logAnalyticsName = '${baseName}-logs-${environment}'
var appInsightsName = '${baseName}-ai-${environment}'
var aksClusterName = '${baseName}-aks-${environment}'
var kafkaNamespaceName = '${baseName}-kafka-${environment}'
var vnetName = '${baseName}-spoke-vnet'
var aksSubnetName = 'aks-subnet'
var servicesSubnetName = 'services-subnet'
var privateEndpointSubnetName = 'private-endpoints-subnet'

// ============================================================================
// NETWORKING
// ============================================================================

// Spoke VNet for Parkland CHO
resource vnet 'Microsoft.Network/virtualNetworks@2023-05-01' = {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.200.0.0/16'
      ]
    }
    subnets: [
      {
        name: aksSubnetName
        properties: {
          addressPrefix: '10.200.0.0/20'
          networkSecurityGroup: {
            id: aksNsg.id
          }
          serviceEndpoints: [
            {
              service: 'Microsoft.Storage'
            }
            {
              service: 'Microsoft.KeyVault'
            }
          ]
        }
      }
      {
        name: servicesSubnetName
        properties: {
          addressPrefix: '10.200.16.0/24'
          networkSecurityGroup: {
            id: servicesNsg.id
          }
          serviceEndpoints: [
            {
              service: 'Microsoft.Storage'
            }
            {
              service: 'Microsoft.KeyVault'
            }
          ]
        }
      }
      {
        name: privateEndpointSubnetName
        properties: {
          addressPrefix: '10.200.17.0/24'
          privateEndpointNetworkPolicies: 'Disabled'
          privateLinkServiceNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

// VNet peering to hub (ExpressRoute)
resource vnetPeering 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2023-05-01' = {
  parent: vnet
  name: 'spoke-to-hub'
  properties: {
    allowVirtualNetworkAccess: true
    allowForwardedTraffic: true
    allowGatewayTransit: false
    useRemoteGateways: true
    remoteVirtualNetwork: {
      id: hubVNetId
    }
  }
}

// Network Security Group for AKS
resource aksNsg 'Microsoft.Network/networkSecurityGroups@2023-05-01' = {
  name: '${aksSubnetName}-nsg'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowKubernetesAPI'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: 'VirtualNetwork'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'AllowHTTPSInbound'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '10.0.0.0/8'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

// Network Security Group for Services
resource servicesNsg 'Microsoft.Network/networkSecurityGroups@2023-05-01' = {
  name: '${servicesSubnetName}-nsg'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'AllowHTTPSInbound'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: 'VirtualNetwork'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

// ============================================================================
// LOG ANALYTICS & MONITORING
// ============================================================================

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 730 // 2 years for HIPAA compliance
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    RetentionInDays: 90
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// ============================================================================
// AZURE KEY VAULT
// ============================================================================

resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'premium' // Premium for HSM-backed keys
    }
    tenantId: subscription().tenantId
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    enableRbacAuthorization: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: enablePrivateEndpoints ? 'Deny' : 'Allow'
      virtualNetworkRules: enablePrivateEndpoints ? [] : [
        {
          id: '${vnet.id}/subnets/${aksSubnetName}'
        }
        {
          id: '${vnet.id}/subnets/${servicesSubnetName}'
        }
      ]
    }
  }
}

// Private endpoint for Key Vault
resource keyVaultPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoints) {
  name: '${keyVaultName}-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: '${vnet.id}/subnets/${privateEndpointSubnetName}'
    }
    privateLinkServiceConnections: [
      {
        name: '${keyVaultName}-pl'
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
}

// ============================================================================
// STORAGE ACCOUNT (DATA LAKE GEN2)
// ============================================================================

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_GRS' // Geo-redundant for disaster recovery
  }
  kind: 'StorageV2'
  properties: {
    isHnsEnabled: true // Enable Data Lake Gen2
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    encryption: {
      services: {
        blob: {
          enabled: true
        }
        file: {
          enabled: true
        }
      }
      keySource: 'Microsoft.Storage'
    }
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: enablePrivateEndpoints ? 'Deny' : 'Allow'
      virtualNetworkRules: enablePrivateEndpoints ? [] : [
        {
          id: '${vnet.id}/subnets/${aksSubnetName}'
          action: 'Allow'
        }
        {
          id: '${vnet.id}/subnets/${servicesSubnetName}'
          action: 'Allow'
        }
      ]
    }
  }
}

// Blob service with lifecycle management
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

// Containers for file ingestion and FHIR data
resource fileIngestionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'parkland-file-ingestion'
  properties: {
    publicAccess: 'None'
  }
}

resource fhirDataContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'parkland-fhir-data'
  properties: {
    publicAccess: 'None'
  }
}

resource archiveContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  parent: blobService
  name: 'parkland-archive'
  properties: {
    publicAccess: 'None'
  }
}

// Lifecycle management policy
resource lifecyclePolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'ArchiveOldFiles'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                'parkland-file-ingestion/'
              ]
            }
            actions: {
              baseBlob: {
                tierToCool: {
                  daysAfterModificationGreaterThan: 30
                }
                tierToArchive: {
                  daysAfterModificationGreaterThan: 90
                }
              }
            }
          }
        }
        {
          name: 'RetainCompliance'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                'parkland-archive/'
              ]
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: 2555 // 7 years for HIPAA
                }
              }
            }
          }
        }
      ]
    }
  }
}

// Private endpoint for Storage
resource storagePrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoints) {
  name: '${storageAccountName}-blob-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: '${vnet.id}/subnets/${privateEndpointSubnetName}'
    }
    privateLinkServiceConnections: [
      {
        name: '${storageAccountName}-blob-pl'
        properties: {
          privateLinkServiceId: storage.id
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

// ============================================================================
// AZURE KUBERNETES SERVICE (AKS)
// ============================================================================

resource aks 'Microsoft.ContainerService/managedClusters@2023-10-01' = {
  name: aksClusterName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: '${baseName}-${environment}'
    enableRBAC: true
    networkProfile: {
      networkPlugin: 'azure'
      networkPolicy: 'calico'
      serviceCidr: '10.201.0.0/16'
      dnsServiceIP: '10.201.0.10'
      loadBalancerSku: 'standard'
    }
    agentPoolProfiles: [
      {
        name: 'systempool'
        count: 3
        vmSize: 'Standard_D4s_v3'
        mode: 'System'
        type: 'VirtualMachineScaleSets'
        availabilityZones: [
          '1'
          '2'
          '3'
        ]
        enableAutoScaling: true
        minCount: 3
        maxCount: 6
        vnetSubnetID: '${vnet.id}/subnets/${aksSubnetName}'
        maxPods: 110
        osDiskSizeGB: 128
        osDiskType: 'Managed'
      }
      {
        name: 'workerpool'
        count: 3
        vmSize: 'Standard_D8s_v3'
        mode: 'User'
        type: 'VirtualMachineScaleSets'
        availabilityZones: [
          '1'
          '2'
          '3'
        ]
        enableAutoScaling: true
        minCount: 3
        maxCount: 10
        vnetSubnetID: '${vnet.id}/subnets/${aksSubnetName}'
        maxPods: 110
        osDiskSizeGB: 256
        osDiskType: 'Managed'
      }
    ]
    addonProfiles: {
      omsagent: {
        enabled: true
        config: {
          logAnalyticsWorkspaceResourceID: logAnalytics.id
        }
      }
      azureKeyvaultSecretsProvider: {
        enabled: true
        config: {
          enableSecretRotation: 'true'
          rotationPollInterval: '2m'
        }
      }
    }
  }
}

// Grant AKS managed identity access to Key Vault
resource aksKeyVaultRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.id, keyVault.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

// Grant AKS managed identity access to Storage
resource aksStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks.id, storage.id, 'Storage Blob Data Contributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') // Storage Blob Data Contributor
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

// ============================================================================
// EVENT HUB NAMESPACE (FOR KAFKA PROTOCOL)
// ============================================================================

resource eventHubNamespace 'Microsoft.EventHub/namespaces@2023-01-01-preview' = {
  name: kafkaNamespaceName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 2
  }
  properties: {
    kafkaEnabled: true
    zoneRedundant: true
  }
}

// Event Hub for member events
resource memberEventsHub 'Microsoft.EventHub/namespaces/eventhubs@2023-01-01-preview' = {
  parent: eventHubNamespace
  name: 'member-events'
  properties: {
    messageRetentionInDays: 7
    partitionCount: 4
  }
}

// Event Hub for file ingestion events
resource fileIngestionEventsHub 'Microsoft.EventHub/namespaces/eventhubs@2023-01-01-preview' = {
  parent: eventHubNamespace
  name: 'file-ingestion-events'
  properties: {
    messageRetentionInDays: 7
    partitionCount: 4
  }
}

// Event Hub for QNXT integration events
resource qnxtEventsHub 'Microsoft.EventHub/namespaces/eventhubs@2023-01-01-preview' = {
  parent: eventHubNamespace
  name: 'qnxt-events'
  properties: {
    messageRetentionInDays: 7
    partitionCount: 4
  }
}

// ============================================================================
// OUTPUTS
// ============================================================================

output vnetId string = vnet.id
output vnetName string = vnet.name
output aksClusterName string = aks.name
output aksClusterId string = aks.id
output storageAccountName string = storage.name
output storageAccountId string = storage.id
output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
output logAnalyticsWorkspaceId string = logAnalytics.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output eventHubNamespaceName string = eventHubNamespace.name
output eventHubNamespaceId string = eventHubNamespace.id

@description('Kubernetes get-credentials command')
output aksGetCredentialsCommand string = 'az aks get-credentials --resource-group ${resourceGroup().name} --name ${aks.name}'

@description('Storage account connection string (retrieve from Key Vault)')
output storageConnectionStringSecretName string = 'storage-connection-string'

@description('Event Hub connection string (retrieve from Key Vault)')
output eventHubConnectionStringSecretName string = 'eventhub-connection-string'
