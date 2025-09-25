
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

// Service Bus (using 'svc' suffix instead of reserved 'sb', Standard tier for Topics)
resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${baseName}-svc'
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
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
resource sbTopicEdi278 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'edi-278'
  properties: {}
}

// App Insights (for telemetry)
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web' }
}

// Logic App Standard (plan + app) - use proper Logic Apps configuration
resource plan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: '${baseName}-plan'
  location: location
  sku: { name: 'WS1', tier: 'WorkflowStandard' }
  kind: 'elastic'
  properties: {
    elasticScaleEnabled: false
  }
}
resource la 'Microsoft.Web/sites@2022-03-01' = {
  name: '${baseName}-la'
  location: location
  kind: 'functionapp,workflowapp'
  properties: {
    serverFarmId: plan.id
    siteConfig: {
      netFrameworkVersion: 'v6.0'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${stg.name};AccountKey=${stg.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }
        { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING', value: 'DefaultEndpointsProtocol=https;AccountName=${stg.name};AccountKey=${stg.listKeys().keys[0].value};EndpointSuffix=core.windows.net' }
        { name: 'WEBSITE_CONTENTSHARE', value: '${baseName}-la' }
        { name: 'APPINSIGHTS_INSTRUMENTATIONKEY', value: insights.properties.InstrumentationKey }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }
      ]
    }
  }
  identity: { type: 'SystemAssigned' }
}

 @allowed([
  'Free'
  'Basic'
  'Standard'
])
param iaSku string = 'Free'          // Free by default
param useExistingIa bool = true      // true = reuse, false = create
param iaName string = '${baseName}-ia'
param iaResourceGroup string = resourceGroup().name  // change if IA is elsewhere

// scope to the RG that holds your existing IA
resource iaResourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' existing = {
  name: iaResourceGroup
}

// Reference existing
resource iaExisting 'Microsoft.Logic/integrationAccounts@2019-05-01' existing = if (useExistingIa) {
  scope: iaRg
  name: iaName
}

// Create new
resource iaNew 'Microsoft.Logic/integrationAccounts@2019-05-01' = if (!useExistingIa) {
  name: iaName
  location: location
  sku: { name: iaSku }
  properties: {}
}


// Outputs
output storageAccountName string = stg.name
output serviceBusNamespace string = sb.name
output logicAppName string = la.name
output appInsightsName string = insights.name
output integrationAccountName string = integrationAccount.name
output integrationAccountName string = useExistingIa ? iaExisting.name : iaNew.name