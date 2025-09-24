
param location string = resourceGroup().location
param baseName string = 'hipaa-attachments'
param storageSku string = 'Standard_LRS'

// Generate a unique storage account name (lowercase, no hyphens, max 24 chars)
var storageAccountName = 'hipaa${uniqueString(resourceGroup().id)}'

// Storage account (Azure Data Lake Storage Gen2)
resource stg 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: { name: storageSku }
  kind: 'StorageV2'
  properties: { 
    minimumTlsVersion: 'TLS1_2'
    isHnsEnabled: true  // Enable hierarchical namespace for Data Lake Gen2
  }
}

// Data Lake container for HIPAA attachments
resource stgContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${stg.name}/default/hipaa-attachments'
  properties: {
    publicAccess: 'None'
  }
}

// Service Bus
resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${baseName}-sb'
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
}
resource sbTopicIn 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'attachments-in'
  properties: {}
}
resource sbTopicRfai 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'rfai-requests'
  properties: {}
}

// App Insights (for telemetry)
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web' }
}

// Logic App Standard (plan + app)
resource plan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: '${baseName}-wfplan'
  location: location
  sku: { name: 'WS1', tier: 'WorkflowStandard' }
}
resource la 'Microsoft.Web/sites@2022-03-01' = {
  name: '${baseName}-la'
  location: location
  kind: 'workflowapp'
  properties: {
    serverFarmId: plan.id
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${stg.name};AccountKey=${stg.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }
        { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING', value: 'DefaultEndpointsProtocol=https;AccountName=${stg.name};AccountKey=${stg.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }
        { name: 'WEBSITE_CONTENTSHARE', value: '${baseName}-la' }
        { name: 'APPINSIGHTS_INSTRUMENTATIONKEY', value: insights.properties.InstrumentationKey }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
      ]
    }
  }
  identity: { type: 'SystemAssigned' }
}

// Outputs
output storageAccountName string = stg.name
output serviceBusNamespace string = sb.name
output logicAppName string = la.name
output appInsightsName string = insights.name
