// Appeals API Infrastructure Module
// Provisions resources for Health Plan Appeals Integration
// Payer-agnostic design using generic parameters

@description('Base name for all resources')
param baseName string

@description('Azure region for resource deployment')
param location string = resourceGroup().location

@description('Tags to apply to all resources')
param tags object = {}

@description('Environment identifier (dev, uat, prod)')
param environment string = 'dev'

@description('Health plan payer identifier for configuration (generic placeholder)')
param payerId string = '{config.payerId}'

@description('Service Bus namespace name')
param serviceBusNamespaceName string

@description('Availity Bedlam API endpoint for pushing appeal status updates')
param availityBedlamEndpoint string = 'https://api.availity.com/bedlam/v1/appeals/status'

@description('Key Vault resource ID for storing secrets')
param keyVaultId string

@description('Storage account name for appeal documents')
param storageAccountName string

@description('Application Insights resource ID for logging')
param appInsightsId string

@description('Enable private endpoints for secure networking')
param enablePrivateEndpoints bool = false

@description('Virtual Network ID for private endpoint integration')
param vnetId string = ''

@description('Subnet ID for private endpoint integration')
param subnetId string = ''

// Service Bus Topics for Appeals Integration
resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// Topic: payer-appeal-status-updates
// Used by backend to publish appeal status changes that need to be pushed to Availity
resource payerAppealStatusUpdatesTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'payer-appeal-status-updates'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    supportOrdering: true
    status: 'Active'
  }
}

// Subscription: availity-push
// Consumed by appeal_update_from_payer_to_availity workflow
resource availityPushSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: payerAppealStatusUpdatesTopic
  name: 'availity-push'
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT5M'
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
    enableBatchedOperations: true
  }
}

// Storage Container: appeals
// Stores appeal documents (provider uploads and decision letters)
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource appealsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${storageAccount.name}/default/appeals'
  properties: {
    publicAccess: 'None'
    metadata: {
      purpose: 'Appeal documents storage'
      documentTypes: 'PROVIDER_UPLOAD, DECISION_LETTER'
    }
  }
}

// Key Vault Secrets for Appeals Integration
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: last(split(keyVaultId, '/'))
}

// Secret: Availity Bedlam API Key
// Used by appeal_update_from_payer_to_availity workflow
resource availityBedlamApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'availity-bedlam-api-key'
  properties: {
    value: 'PLACEHOLDER-REPLACE-WITH-ACTUAL-KEY'
    contentType: 'text/plain'
    attributes: {
      enabled: true
    }
  }
  tags: union(tags, {
    purpose: 'Availity Bedlam API authentication'
    workflow: 'appeal_update_from_payer_to_availity'
  })
}

// Secret: Authorization API Endpoint
// Used by appeal_document_download workflow
resource authorizationApiEndpointSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'authorization-api-endpoint'
  properties: {
    value: 'https://api.healthplan.local/authorization'
    contentType: 'text/plain'
    attributes: {
      enabled: true
    }
  }
  tags: union(tags, {
    purpose: 'Authorization service endpoint'
    workflow: 'appeal_document_download'
  })
}

// Application Insights Custom Event Tracking
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: last(split(appInsightsId, '/'))
}

// Outputs for use in Logic App configuration
output payerAppealStatusUpdatesTopicName string = payerAppealStatusUpdatesTopic.name
output availityPushSubscriptionName string = availityPushSubscription.name
output appealsContainerName string = 'appeals'
output availityBedlamApiKeySecretUri string = availityBedlamApiKeySecret.properties.secretUri
output authorizationApiEndpointSecretUri string = authorizationApiEndpointSecret.properties.secretUri
output serviceBusConnectionString string = listKeys(serviceBusNamespace.id, serviceBusNamespace.apiVersion).primaryConnectionString
output storageAccountConnectionString string = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${listKeys(storageAccount.id, storageAccount.apiVersion).keys[0].value};EndpointSuffix=${az.environment().suffixes.storage}'

// Configuration outputs for Logic Apps
output appealConfiguration object = {
  payerId: payerId
  availityBedlamEndpoint: availityBedlamEndpoint
  serviceBusTopic: payerAppealStatusUpdatesTopic.name
  serviceBusSubscription: availityPushSubscription.name
  appealsContainerPath: 'hipaa-attachments/appeals'
  environment: environment
}

// Monitoring outputs
output monitoringConfiguration object = {
  appInsightsConnectionString: appInsights.properties.ConnectionString
  appInsightsInstrumentationKey: appInsights.properties.InstrumentationKey
}

// Security outputs
output securityConfiguration object = {
  keyVaultUri: keyVault.properties.vaultUri
  availityBedlamApiKeySecretName: availityBedlamApiKeySecret.name
  authorizationApiEndpointSecretName: authorizationApiEndpointSecret.name
}
