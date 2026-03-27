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

@description('Clearinghouse API API endpoint for pushing appeal status updates')
param clearinghouseApiEndpoint string = 'https://api.clearinghouse.com/bedlam/v1/appeals/status'

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
// Used by backend to publish appeal status changes that need to be pushed to the clearinghouse
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

// Subscription: clearinghouse-push
// Consumed by appeal_update_from_payer_outbound workflow
resource clearinghousePushSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: payerAppealStatusUpdatesTopic
  name: 'clearinghouse-push'
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

// Secret: Clearinghouse API API Key
// Used by appeal_update_from_payer_outbound workflow
resource clearinghouseApiApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'clearinghouse-api-api-key'
  properties: {
    value: 'PLACEHOLDER-REPLACE-WITH-ACTUAL-KEY'
    contentType: 'text/plain'
    attributes: {
      enabled: true
    }
  }
  tags: union(tags, {
    purpose: 'Clearinghouse API API authentication'
    workflow: 'appeal_update_from_payer_outbound'
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

// Outputs for use in AKS service configuration
output payerAppealStatusUpdatesTopicName string = payerAppealStatusUpdatesTopic.name
output clearinghousePushSubscriptionName string = clearinghousePushSubscription.name
output appealsContainerName string = 'appeals'
output clearinghouseApiApiKeySecretUri string = clearinghouseApiApiKeySecret.properties.secretUri
output authorizationApiEndpointSecretUri string = authorizationApiEndpointSecret.properties.secretUri
output serviceBusConnectionString string = listKeys(serviceBusNamespace.id, serviceBusNamespace.apiVersion).primaryConnectionString
output storageAccountConnectionString string = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${listKeys(storageAccount.id, storageAccount.apiVersion).keys[0].value};EndpointSuffix=${az.environment().suffixes.storage}'

// Configuration outputs for AKS Argo Workflows
output appealConfiguration object = {
  payerId: payerId
  clearinghouseApiEndpoint: clearinghouseApiEndpoint
  serviceBusTopic: payerAppealStatusUpdatesTopic.name
  serviceBusSubscription: clearinghousePushSubscription.name
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
  clearinghouseApiApiKeySecretName: clearinghouseApiApiKeySecret.name
  authorizationApiEndpointSecretName: authorizationApiEndpointSecret.name
}
