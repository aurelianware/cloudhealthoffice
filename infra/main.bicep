param subscriptionId string = 'caf68aff-3bee-40e3-bf26-c4166efa952b'
param location string = resourceGroup().location
param connectorLocation string = 'westus2'
param baseName string = 'hipaa-logic'
param storageSku string = 'Standard_LRS'

// Unique storage account name
var storageAccountName = 'hipaa${uniqueString(resourceGroup().id)}'

// ----------------------
// Storage
// ----------------------
resource stg 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: { name: storageSku }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    isHnsEnabled: true
  }
}

resource stgContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${stg.name}/default/hipaa-attachments'
  properties: { publicAccess: 'None' }
}

// ----------------------
// Service Bus
// ----------------------
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

// ----------------------
// App Insights
// ----------------------
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web' }
}

// ----------------------
// Logic App Standard
// ----------------------
resource plan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: '${baseName}-plan'
  location: location
  sku: { name: 'WS1', tier: 'WorkflowStandard' }
  kind: 'elastic'
  properties: { elasticScaleEnabled: false }
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
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${stg.name};AccountKey=${stg.listKeys().keys[0].value};EndpointSuffix=core.windows.net'
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${stg.name};AccountKey=${stg.listKeys().keys[0].value};EndpointSuffix=core.windows.net'
        }
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

// ----------------------
// Integration Account
// ----------------------
@allowed([ 'Free' 'Basic' 'Standard' ])
param iaSku string = 'Free'
param useExistingIa bool = true
param iaName string = '${baseName}-ia'

resource iaExisting 'Microsoft.Logic/integrationAccounts@2019-05-01' existing = if (useExistingIa) {
  name: iaName
}

resource iaNew 'Microsoft.Logic/integrationAccounts@2019-05-01' = if (!useExistingIa) {
  name: iaName
  location: location
  sku: { name: iaSku }
  properties: {}
}

// ----------------------
// Connections (managed APIs for Logic Apps Standard)
// ----------------------
resource conn_sftp 'Microsoft.Web/connections@2016-06-01' = {
  name: 'sftp-ssh'
  location: connectorLocation
  properties: { displayName: 'sftp-ssh' }
}

resource conn_blob 'Microsoft.Web/connections@2016-06-01' = {
  name: 'azureblob'
  location: connectorLocation
  properties: { displayName: 'azureblob' }
}

resource conn_sb 'Microsoft.Web/connections@2016-06-01' = {
  name: 'servicebus'
  location: connectorLocation
  properties: { displayName: 'servicebus' }
}

resource conn_ia 'Microsoft.Web/connections@2016-06-01' = {
  name: 'integrationaccount'
  location: connectorLocation
  properties: { displayName: 'integrationaccount' }
}

// ----------------------
// Outputs
// ----------------------
output storageAccountName string = stg.name
output serviceBusNamespace string = sb.name
output logicAppName string = la.name
output appInsightsName string = insights.name
output integrationAccountName string = useExistingIa ? iaExisting.name : iaNew.name