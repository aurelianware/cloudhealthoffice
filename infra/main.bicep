// =========================
// Parameters
// =========================
param location string = resourceGroup().location        // core resources region
param connectorLocation string = 'eastus'              // managed API connections region
param baseName string = 'hipaa-integration'
param storageSku string = 'Standard_LRS'

// Integration Account controls (create or reuse in THIS RG)
@allowed([
  'Free'
  'Basic'
  'Standard'
])
param iaSku string = 'Free'
param useExistingIa bool = false
param iaName string = 'prod-integration-account'



// Toggle B2B (X12) managed connection
param enableB2B bool = true

// SFTP connection params
param sftpHost string = 'sftp.example.com'
param sftpUsername string = 'logicapp'
@secure()
param sftpPassword string = ''          // if key-based auth, change connection param block

// Blob connection params (defaults resolved from storage created here)
param blobAccountName string = ''       // if empty, uses stg.name
@secure()
param blobAccountKey string = ''        // if empty, uses stg.listKeys().keys[0].value

// Service Bus connection string (SAS) - optional; if empty, we generate from auth rule
@secure()
param serviceBusConnectionString string = ''


// =========================
 // Variables
var storageAccountName = 'staging${uniqueString(resourceGroup().id)}'
var effectiveBlobAccountName = empty(blobAccountName) ? stg.name : blobAccountName
var effectiveBlobAccountKey  = empty(blobAccountKey)  ? stg.listKeys().keys[0].value : blobAccountKey

// IA name resolved whether creating new or reusing existing
var effectiveIaName = useExistingIa ? iaExisting.name : iaNew.name


// =========================
// Storage (ADLS Gen2)
// =========================
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
  properties: {
    publicAccess: 'None'
  }
}


// =========================
 // Service Bus (Standard)
// =========================
resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'prod-pchp-integration-sb'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

resource sbAuth 'Microsoft.ServiceBus/namespaces/AuthorizationRules@2022-10-01-preview' = {
  name: 'RootManageSharedAccessKey'   // ✅ leaf name only
  parent: sb
  properties: {
    rights: [
      'Listen'
      'Send'
      'Manage'
    ]
  }
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

// Build SB connection string AFTER sbAuth exists
var serviceBusConnectionStringGenerated = empty(serviceBusConnectionString)
  ? sbAuth.listKeys().primaryConnectionString
  : serviceBusConnectionString


// =========================
// Application Insights
// =========================
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
}


// =========================
// Logic App Standard (Plan + App)
// =========================
resource plan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: 'prod-integration-plan'
  location: location
  sku: {
    name: 'WS1'
    tier: 'WorkflowStandard'
  }
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
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'APPINSIGHTS_INSTRUMENTATIONKEY', value: insights.properties.InstrumentationKey }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'node' }
      ]
    }
  }
  identity: {
    type: 'SystemAssigned'
  }
}


// =========================
// Integration Account (reuse or create in THIS RG)
// =========================
resource iaExisting 'Microsoft.Logic/integrationAccounts@2019-05-01' existing = if (useExistingIa) {
  name: iaName
}

resource iaNew 'Microsoft.Logic/integrationAccounts@2019-05-01' = if (!useExistingIa) {
  name: iaName
  location: location
  sku: {
    name: iaSku
  }
  properties: {}
}


// =========================
// Managed API Connections (2016-06-01) in connectorLocation
// Names MUST match connections.json in repo
// =========================
resource connSftp 'Microsoft.Web/connections@2016-06-01' = {
  name: '${baseName}-sftp'
  location: connectorLocation
  properties: {
    displayName: '${baseName}-sftp'
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', connectorLocation, 'sftpwithssh')
    }
    parameterValues: {
      hostName: sftpHost
      username: sftpUsername
      password: sftpPassword
      // Optional hardening:
      // acceptAnySshHostKey: true
      // sshHostKeyFingerprint: 'SHA256:...'
    }
  }
}

resource connBlob 'Microsoft.Web/connections@2016-06-01' = {
  name: 'azureblob'
  location: connectorLocation
  properties: {
    displayName: 'azureblob'
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', connectorLocation, 'azureblob')
    }
    parameterValues: {
      accountName: effectiveBlobAccountName
      accessKey:  effectiveBlobAccountKey
    }
  }
}

resource connSb 'Microsoft.Web/connections@2016-06-01' = {
  name: 'servicebus'
  location: connectorLocation
  properties: {
    displayName: 'servicebus'
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', connectorLocation, 'servicebus')
    }
    parameterValues: {
      connectionString: serviceBusConnectionStringGenerated
    }
  }
}

var iaResourceId = resourceId('Microsoft.Logic/integrationAccounts', effectiveIaName)

resource connIa 'Microsoft.Web/connections@2016-06-01' = if (enableB2B) {
  name: 'integrationaccount'
  location: connectorLocation
  properties: {
    displayName: 'integrationaccount'
    api: {
      
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', connectorLocation, 'x12')
    }
    parameterValues: {
      // Managed X12 needs the IA ARM ID as a STRING. No SAS/Callback URL.
      integrationAccountId: iaResourceId
    }
  }
}


// =========================
// Outputs
// =========================
output storageAccountName string = stg.name
output serviceBusNamespace string = sb.name
output logicAppName string = la.name
output appInsightsName string = insights.name
output integrationAccountName string = effectiveIaName
output sftpConnectionId string = connSftp.id
output blobConnectionId string = connBlob.id
output serviceBusConnectionId string = connSb.id
output integrationAccountConnectionId string = enableB2B ? connIa.id : 'disabled'
