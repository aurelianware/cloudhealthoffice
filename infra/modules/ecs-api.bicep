// =========================
// ECS API Module
// Enhanced Claim Status API Infrastructure
// =========================

@description('Base name for ECS resources')
param baseName string

@description('Logic App name (parent resource)')
param logicAppName string

@description('Application Insights instrumentation key')
param appInsightsKey string

@description('Application Insights connection string')
param appInsightsConnectionString string

@description('QNXT API base URL')
param qnxtBaseUrl string = 'https://qnxt-api.example.com'

@secure()
@description('QNXT API authentication token')
param qnxtApiToken string = ''

@description('Enable ECS workflow deployment')
param enableEcs bool = true

// =========================
// Variables
// =========================
var ecsWorkflowName = 'ecs_summary_search'

// =========================
// Outputs - ECS Configuration Settings
// =========================

// ECS workflow configuration to be used in Logic App settings
output ecsWorkflowConfig object = {
  workflowName: ecsWorkflowName
  qnxtBaseUrl: qnxtBaseUrl
  enabled: enableEcs
}

// App Settings for ECS integration
output ecsAppSettings array = [
  {
    name: 'ECS_QNXT_BASE_URL'
    value: qnxtBaseUrl
  }
  // Note: QNXT API token should be configured via Key Vault reference
  // Example: '@Microsoft.KeyVault(SecretUri=https://keyvault.vault.azure.net/secrets/qnxt-token/)'
  {
    name: 'ECS_QNXT_API_TOKEN'
    value: '' // Placeholder - configure via Key Vault or secure parameters
  }
  {
    name: 'ECS_WORKFLOW_ENABLED'
    value: string(enableEcs)
  }
]

// ECS-specific Application Insights configuration
output ecsMonitoringConfig object = {
  instrumentationKey: appInsightsKey
  connectionString: appInsightsConnectionString
  enableDetailedLogging: true
  logSearchRequests: true
  logSearchResults: true
}

// ECS API endpoint information
output ecsEndpointInfo object = {
  workflowName: ecsWorkflowName
  triggerName: 'HTTP_ECS_Summary_Search_Request'
  endpointPath: '/api/${ecsWorkflowName}/triggers/HTTP_ECS_Summary_Search_Request/invoke'
  baseUrl: 'https://${logicAppName}.azurewebsites.net'
  fullUrl: 'https://${logicAppName}.azurewebsites.net/api/${ecsWorkflowName}/triggers/HTTP_ECS_Summary_Search_Request/invoke'
}

// ECS workflow parameters (for Logic App configuration)
// Note: Secure parameters should reference Key Vault
output ecsWorkflowParameters object = {
  qnxt_base_url: {
    type: 'String'
    value: qnxtBaseUrl
  }
  // qnxt_api_token should be configured via Key Vault reference
  // not exposed in outputs due to security concerns
}

// Resource tags for ECS-related resources
output ecsTags object = {
  Component: 'ECS'
  WorkflowType: 'SummarySearch'
  IntegrationType: 'QNXT'
  Environment: contains(baseName, 'prod') ? 'Production' : contains(baseName, 'uat') ? 'UAT' : 'Development'
}

// =========================
// Notes
// =========================
// This module provides configuration outputs for the ECS Summary Search workflow.
// The actual workflow deployment happens via the workflows.zip package deployment.
// 
// Usage in main.bicep:
//   module ecs 'modules/ecs-api.bicep' = {
//     name: 'ecs-api-module'
//     params: {
//       baseName: baseName
//       location: location
//       logicAppName: la.name
//       appInsightsKey: insights.properties.InstrumentationKey
//       appInsightsConnectionString: insights.properties.ConnectionString
//       qnxtBaseUrl: qnxtBaseUrl
//       qnxtApiToken: qnxtApiToken
//     }
//   }
//
// Then add ECS app settings to Logic App:
//   resource laAppSettings 'Microsoft.Web/sites/config@2022-03-01' = {
//     parent: la
//     name: 'appsettings'
//     properties: {
//       ...existingSettings
//       ...array_to_object(ecs.outputs.ecsAppSettings)
//     }
//   }
