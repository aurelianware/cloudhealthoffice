// =========================
// Parameters
// =========================
param location string = resourceGroup().location        // core resources region
param connectorLocation string = 'eastus'              // managed API connections region
param baseName string
param sftpHost string
param sftpUsername string
@secure()
param sftpPassword string
param storageSku string = 'Standard_LRS'

// Integration Account controls (create or reuse in THIS RG)
@allowed([
  'Free'
  'Basic'
  'Standard'
])
param iaSku string = 'Free'
param useExistingIa bool = false
param iaName string //= 'prod-integration-account'



// Toggle B2B (X12) managed connection
param enableB2B bool = true

// Toggle managed API connections (legacy Logic App connectors, not needed for AKS-only deployments)
param enableManagedApiConnections bool = false
 
// SFTPconnection params
//param sftpHost string = 'sftp.example.com'
//@secure()
//param sftpPassword string = ''          // if key-based auth, change connection param block

// Blob connection params (defaults resolved from storage created here)
param blobAccountName string = ''       // if empty, uses stg.name
@secure()
param blobAccountKey string = ''        // if empty, uses stg.listKeys().keys[0].value

// Service Bus connection string (SAS) - optional; if empty, we generate from auth rule
@secure()
param serviceBusConnectionString string = ''
param serviceBusName string

// ECS (Enhanced Claim Status) parameters
param enableEcs bool = true
param backendBaseUrl string = 'https://claims-backend-api.example.com'
@secure()
param backendApiToken string = ''

// Deployment Key Vault parameters
param enableDeploymentKeyVault bool = true
param deploymentKeyVaultName string = '${baseName}-deploy-kv'

// Secret Provider parameters (application secrets — distinct from deployment KV)
@description('Application secret provider backend')
@allowed([
  'none'
  'azurekeyvault'
])
param secretProvider string = 'none'

@description('SKU for the application Key Vault (Premium provides HSM-backed keys)')
@allowed([
  'standard'
  'premium'
])
param keyVaultSku string = 'premium'

@description('AKS kubelet identity principal ID for Key Vault RBAC (required when secretProvider == azurekeyvault)')
param aksKubeletIdentityPrincipalId string = ''

@description('AKS subnet ID for Key Vault network rules (required when secretProvider == azurekeyvault)')
param aksSubnetId string = ''

@description('Log Analytics workspace ID for Key Vault diagnostic settings')
param logAnalyticsWorkspaceId string = ''

// =========================
 // Variables
var enableKeyVault = (secretProvider == 'azurekeyvault')
var appKeyVaultName = '${baseName}-app-kv'
var storageAccountName = 'staging${uniqueString(resourceGroup().id)}'
var effectiveBlobAccountName = empty(blobAccountName) ? stg.name : blobAccountName
var effectiveBlobAccountKey  = empty(blobAccountKey)  ? stg.listKeys().keys[0].value : blobAccountKey

// IA name resolved whether creating new or reusing existing
var effectiveIaName = useExistingIa ? iaExisting.name : iaNew.name

// =========================
// Deployment Key Vault (for storing deployment secrets)
// =========================
module deploymentKeyVault 'modules/deployment-keyvault.bicep' = if (enableDeploymentKeyVault) {
  name: 'deployment-keyvault'
  params: {
    keyVaultName: deploymentKeyVaultName
    location: location
    environment: 'PROD'
    skuName: 'premium'
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'  // For GitHub Actions access
    networkAclsDefaultAction: 'Allow'
    logAnalyticsWorkspaceId: ''  // Will be configured post-deployment if needed
  }
}


// =========================
// Application Key Vault (secret provider for microservices)
// =========================
module appKeyVault 'modules/app-keyvault.bicep' = if (enableKeyVault) {
  name: 'app-keyvault'
  params: {
    name: appKeyVaultName
    location: location
    skuName: keyVaultSku
    aksKubeletIdentityPrincipalId: aksKubeletIdentityPrincipalId
    aksSubnetId: aksSubnetId
    logAnalyticsWorkspaceId: logAnalyticsWorkspaceId
    tags: {
      Environment: 'Production'
      Compliance: 'HIPAA'
      CostCenter: 'Security'
      ManagedBy: 'Bicep'
      Purpose: 'ApplicationSecrets'
    }
  }
}

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
  name: serviceBusName
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

resource sbTopicEdi278SubAuth 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: sbTopicEdi278
  name: 'auth-processor'
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT5M'
  }
}

resource sbTopicAppealsAuth 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'appeals-auth'
  properties: {}
}

resource sbTopicAuthStatuses 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'auth-statuses'
  properties: {}
}

resource sbTopicDeadLetter 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'dead-letter'
  properties: {}
}

// Prior Auth API Service Bus Topics
resource sbTopicPriorAuthRequests 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'prior-auth-requests'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
  }
}

resource sbTopicPriorAuthResponses 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'prior-auth-responses'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
  }
}

resource sbTopicPriorAuthSlaTimer 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'prior-auth-sla-timer'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
  }
}

resource sbTopicPriorAuthSlaTimerSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: sbTopicPriorAuthSlaTimer
  name: 'sla-monitor'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
  }
}

// EDI 837 Claims topic for ClaimRiskScorer
resource sbTopicEdi837Claims 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'edi-837-claims'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
  }
}

resource sbTopicEdi837ClaimsSubRiskScorer 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: sbTopicEdi837Claims
  name: 'claim-risk-scorer'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
  }
}

// ─── Claim version events (capability 5.5 — adjudication pipeline) ───
//
// Canonical lifecycle topic for the claims-service adjudication pipeline.
// 5.5 ships the producer (ClaimSubmissionService dual-emit + orchestrator
// adjudicated emission) and one subscription (adjudication-orchestrator).
// Future capabilities add their own subscriptions filtered by the
// MessageType application property:
//   5.10 remittance generation       → MessageType=ClaimVersionAdjudicated
//   5.12 adjustment workflow         → MessageType=ClaimVersionAdjusted
//
// Native Service Bus duplicate detection is enabled so the deterministic
// MessageId values the producer sets ("submitted:{ClaimVersionId}",
// "adjudicated:{ClaimVersionId}") catch retries cleanly.
resource sbTopicClaimVersionEvents 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: sb
  name: 'claim-version-events'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
  }
}

resource sbTopicClaimVersionEventsSubAdjudication 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: sbTopicClaimVersionEvents
  name: 'adjudication-orchestrator'
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT5M'
    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
  }
}

// Correlation filter — only ClaimVersionSubmitted messages drive the
// adjudication pipeline. Adjudicated/Paid/etc. messages emitted onto the
// same topic by the orchestrator and other future producers are routed
// to other subscriptions (5.10/5.12). Replacing the default $Default rule
// with this named correlation filter happens automatically when the
// rules collection is declared.
resource sbTopicClaimVersionEventsSubAdjudicationRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  parent: sbTopicClaimVersionEventsSubAdjudication
  name: 'submitted-only'
  properties: {
    filterType: 'CorrelationFilter'
    correlationFilter: {
      properties: {
        MessageType: 'ClaimVersionSubmitted'
      }
    }
  }
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
// ClaimRiskScorer Azure Function (Python)
// =========================
param enableClaimRiskScorer bool = true

resource claimRiskScorerPlan 'Microsoft.Web/serverfarms@2022-03-01' = if (enableClaimRiskScorer) {
  name: '${baseName}-claimrisk-plan'
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
  properties: {
    reserved: true  // Required for Linux
  }
}

resource claimRiskScorerFunc 'Microsoft.Web/sites@2022-03-01' = if (enableClaimRiskScorer) {
  name: '${baseName}-claimrisk-func'
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: claimRiskScorerPlan.id
    siteConfig: {
      linuxFxVersion: 'PYTHON|3.11'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: stg.name }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'APPINSIGHTS_INSTRUMENTATIONKEY', value: insights.properties.InstrumentationKey }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'python' }
        { name: 'ServiceBusConnection', value: serviceBusConnectionStringGenerated }
        { name: 'MODEL_PATH', value: '/home/site/wwwroot/ml/claim-fraud-v1.pt' }
      ]
    }
  }
  identity: {
    type: 'SystemAssigned'
  }
}

// Role assignment for ClaimRiskScorer to access Storage Account using Managed Identity
// Storage Blob Data Owner role - allows full blob access
resource claimRiskScorerStorageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableClaimRiskScorer) {
  name: guid(stg.id, claimRiskScorerFunc.id, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  scope: stg
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
    principalId: claimRiskScorerFunc.identity.principalId
    principalType: 'ServicePrincipal'
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
// These are legacy Logic App connectors — only deployed when enableManagedApiConnections is true.
// Not needed for AKS-only deployments using Argo Workflows.
// =========================
resource connSftp 'Microsoft.Web/connections@2016-06-01' = if (enableManagedApiConnections) {
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
    }
  }
}

resource connBlob 'Microsoft.Web/connections@2016-06-01' = if (enableManagedApiConnections) {
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

resource connSb 'Microsoft.Web/connections@2016-06-01' = if (enableManagedApiConnections) {
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

resource connIa 'Microsoft.Web/connections@2016-06-01' = if (enableManagedApiConnections && enableB2B) {
  name: 'integrationaccount'
  location: connectorLocation
  properties: {
    displayName: 'integrationaccount'
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', connectorLocation, 'x12')
    }
    parameterValues: {
      integrationAccountId: iaResourceId
    }
  }
}


// =========================
// ECS Module
// =========================
module ecs 'modules/ecs-api.bicep' = if (enableEcs) {
  name: 'ecs-api-module'
  params: {
    baseName: baseName
    appInsightsKey: insights.properties.InstrumentationKey
    appInsightsConnectionString: insights.properties.ConnectionString
    backendBaseUrl: backendBaseUrl
    backendApiToken: backendApiToken
    enableEcs: enableEcs
  }
}

// =========================
// Azure Monitor Workbooks Module
// =========================
module workbooks 'modules/workbooks.bicep' = {
  name: 'workbooks-module'
  params: {
    baseName: baseName
    location: location
    appInsightsId: insights.id
  }
}

// =========================
// Cosmos DB Module (for Prior Auth and Provider Directory APIs)
// =========================
param enableCosmosDb bool = true
param cosmosDbThroughput int = 400

module cosmosDb 'modules/cosmos-db.bicep' = if (enableCosmosDb) {
  name: 'cosmos-db-module'
  params: {
    baseName: baseName
    location: location
    throughput: cosmosDbThroughput
    enableServerless: false
  }
}

// =========================
// Cosmos DB for MongoDB lifetime free tier
// =========================
// Separate from the NoSQL API account above. This is opt-in because Azure
// permits only one free-tier Cosmos DB account per subscription.
param enableCosmosMongoFreeTier bool = false
param cosmosMongoDatabaseName string = 'cloudhealthoffice'

module cosmosMongoFreeTier 'modules/cosmos-mongodb-free-tier.bicep' = if (enableCosmosMongoFreeTier) {
  name: 'cosmos-mongodb-free-tier-module'
  params: {
    baseName: baseName
    location: location
    databaseName: cosmosMongoDatabaseName
    throughput: 1000
  }
}

// Cosmos DB managed API connection (legacy Logic App connector)
resource connCosmosDb 'Microsoft.Web/connections@2016-06-01' = if (enableManagedApiConnections && enableCosmosDb) {
  name: 'documentdb'
  location: connectorLocation
  properties: {
    displayName: 'documentdb'
    api: {
      id: subscriptionResourceId('Microsoft.Web/locations/managedApis', connectorLocation, 'documentdb')
    }
    parameterValues: {
      databaseAccount: cosmosDb.outputs.cosmosAccountName
      accessKey: cosmosDb.outputs.cosmosPrimaryKey
    }
  }
}

// =========================
// Azure Static Web App (for marketing site)
// =========================
resource staticWebApp 'Microsoft.Web/staticSites@2023-01-01' = {
  name: '${baseName}-swa'
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    repositoryUrl: ''  // Will be configured via GitHub Actions deployment
    branch: ''
    buildProperties: {
      skipGithubActionWorkflowGeneration: true
    }
  }
}

// =========================
// Outputs
// =========================
output storageAccountName string = stg.name
output serviceBusNamespace string = sb.name
output appInsightsName string = insights.name
output integrationAccountName string = effectiveIaName
output sftpConnectionId string = enableManagedApiConnections ? connSftp.id : 'disabled'
output blobConnectionId string = enableManagedApiConnections ? connBlob.id : 'disabled'
output serviceBusConnectionId string = enableManagedApiConnections ? connSb.id : 'disabled'
output integrationAccountConnectionId string = enableManagedApiConnections && enableB2B ? connIa.id : 'disabled'
output ecsEndpointUrl string = enableEcs && ecs != null ? ecs.outputs.ecsEndpointInfo.fullUrl : 'disabled'
output ecsWorkflowName string = enableEcs && ecs != null ? ecs.outputs.ecsWorkflowConfig.workflowName : 'disabled'
output staticWebAppName string = staticWebApp.name
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output staticWebAppId string = staticWebApp.id

// Workbook outputs
output ediTransactionMetricsWorkbookId string = workbooks.outputs.ediTransactionMetricsWorkbookId
output payerIntegrationHealthWorkbookId string = workbooks.outputs.payerIntegrationHealthWorkbookId
output hipaaComplianceWorkbookId string = workbooks.outputs.hipaaComplianceWorkbookId
output cms0057fComplianceWorkbookId string = workbooks.outputs.cms0057fComplianceWorkbookId
output ediTransactionMetricsUrl string = workbooks.outputs.ediTransactionMetricsUrl
output payerIntegrationHealthUrl string = workbooks.outputs.payerIntegrationHealthUrl
output hipaaComplianceUrl string = workbooks.outputs.hipaaComplianceUrl
output cms0057fComplianceUrl string = workbooks.outputs.cms0057fComplianceUrl

// Cosmos DB outputs
output cosmosDbAccountName string = enableCosmosDb ? cosmosDb.outputs.cosmosAccountName : 'disabled'
output cosmosDbEndpoint string = enableCosmosDb ? cosmosDb.outputs.cosmosAccountEndpoint : 'disabled'
output cosmosDbDatabaseName string = enableCosmosDb ? cosmosDb.outputs.cosmosDatabaseName : 'disabled'
output priorAuthContainerName string = enableCosmosDb ? cosmosDb.outputs.priorAuthContainerName : 'disabled'
output providerDirectoryContainerName string = enableCosmosDb ? cosmosDb.outputs.providerDirectoryContainerName : 'disabled'
output cosmosDbConnectionId string = enableManagedApiConnections && enableCosmosDb ? connCosmosDb.id : 'disabled'

// Cosmos DB for MongoDB free-tier outputs (connection strings are deliberately
// excluded; retrieve them directly into the target secret store).
output cosmosMongoFreeTierAccountName string = enableCosmosMongoFreeTier ? cosmosMongoFreeTier!.outputs.cosmosMongoAccountName : 'disabled'
output cosmosMongoFreeTierEndpoint string = enableCosmosMongoFreeTier ? cosmosMongoFreeTier!.outputs.cosmosMongoEndpoint : 'disabled'
output cosmosMongoFreeTierDatabaseName string = enableCosmosMongoFreeTier ? cosmosMongoFreeTier!.outputs.cosmosMongoDatabaseName : 'disabled'

// ClaimRiskScorer outputs
output claimRiskScorerFunctionName string = enableClaimRiskScorer ? claimRiskScorerFunc.name : 'disabled'
output claimRiskScorerFunctionUrl string = enableClaimRiskScorer ? 'https://${claimRiskScorerFunc.properties.defaultHostName}' : 'disabled'
output edi837ClaimsTopicName string = sbTopicEdi837Claims.name
output claimVersionEventsTopicName string = sbTopicClaimVersionEvents.name

// Deployment Key Vault outputs
output deploymentKeyVaultName string = enableDeploymentKeyVault ? deploymentKeyVault.outputs.keyVaultName : 'disabled'
output deploymentKeyVaultId string = enableDeploymentKeyVault ? deploymentKeyVault.outputs.keyVaultId : 'disabled'
output deploymentKeyVaultUri string = enableDeploymentKeyVault ? deploymentKeyVault.outputs.keyVaultUri : 'disabled'

// Application Key Vault outputs (secret provider)
output appKeyVaultName string = enableKeyVault ? appKeyVault.outputs.keyVaultName : 'disabled'
output appKeyVaultId string = enableKeyVault ? appKeyVault.outputs.keyVaultId : 'disabled'
output appKeyVaultUri string = enableKeyVault ? appKeyVault.outputs.keyVaultUri : 'disabled'
