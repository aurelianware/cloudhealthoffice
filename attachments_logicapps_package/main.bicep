
param location string = resourceGroup().location
// param baseName string = 'hipaa-integration'
param storageSku string = 'Standard_LRS'

// Storage account
resource stg 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: 'sftp'
  location: location
  sku: { name: storageSku }
  kind: 'StorageV2'
  properties: { minimumTlsVersion: 'TLS1_2' }
}

// Service Bus
resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'servicebus'
  location: location
  sku: { name: 'Basic', tier: 'Basic' }
}
resource sbTopicIn 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  name: 'servicebus/attachments-in'
  properties: {}
}
resource sbTopicRfai 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  name: 'servicebus/rfai-requests'
  properties: {}
}
resource sbTopicEdi278 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  name: 'servicebus/edi-278'
  properties: {}
}

// App Insights (for telemetry)
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appinsights'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web' }
}

// Logic App Standard (plan + app)
resource plan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: 'hipaa-integration-wfplan'
  location: location
  sku: { name: 'WS1', tier: 'WorkflowStandard' }
}
resource la 'Microsoft.Web/sites@2022-03-01' = {
  name: 'hipaa-integration-la'
  location: location
  kind: 'workflowapp'
  properties: {
    serverFarmId: plan.id
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage', value: stg.properties.primaryEndpoints.blob }
        { name: 'WORKFLOWS_TENANT_ID', value: '' } // fill in if needed
        { name: 'APPINSIGHTS_INSTRUMENTATIONKEY', value: insights.properties.InstrumentationKey }
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
