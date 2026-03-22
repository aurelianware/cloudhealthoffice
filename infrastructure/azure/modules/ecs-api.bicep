// =========================
// ECS API Module
// Enhanced Claim Status API Infrastructure
// =========================

@description('Base name for ECS resources')
param baseName string

@description('Application Insights instrumentation key')
param appInsightsKey string

@description('Application Insights connection string')
param appInsightsConnectionString string

@description('claims backend API base URL')
param backendBaseUrl string = 'https://claims-backend-api.example.com'

@secure()
@description('claims backend API authentication token')
param backendApiToken string = ''

@description('Enable ECS workflow deployment')
param enableEcs bool = true

@description('Kubernetes namespace where ECS service is deployed')
param aksNamespace string = 'default'

@description('Kubernetes cluster DNS suffix')
param aksClusterDnsSuffix string = 'svc.cluster.local'

// =========================
// Variables
// =========================
var ecsWorkflowName = 'ecs_summary_search'
var aksServiceHost = '${baseName}-ecs-svc.${aksNamespace}.${aksClusterDnsSuffix}'

// =========================
// Outputs - ECS Configuration Settings
// =========================

// ECS workflow configuration for AKS-hosted Argo Workflows
output ecsWorkflowConfig object = {
  workflowName: ecsWorkflowName
  backendBaseUrl: backendBaseUrl
  enabled: enableEcs
}

// App Settings for ECS integration
output ecsAppSettings array = [
  {
    name: 'ECS_BACKEND_BASE_URL'
    value: backendBaseUrl
  }
  // Note: ECS_CLAIMS_BACKEND_API_TOKEN must be configured separately via Key Vault reference
  // DO NOT include token values in Bicep outputs or app settings
  // Configure via: az webapp config appsettings set --settings "ECS_CLAIMS_BACKEND_API_TOKEN=@Microsoft.KeyVault(SecretUri=...)"
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

// ECS API endpoint information (AKS Argo Workflows)
// NOTE: These are internal cluster DNS names, only resolvable from within the AKS cluster.
// For external access, configure an ingress controller and use the ingress hostname instead.
output ecsEndpointInfo object = {
  workflowName: ecsWorkflowName
  triggerName: 'HTTP_ECS_Summary_Search_Request'
  endpointPath: '/api/${ecsWorkflowName}/triggers/HTTP_ECS_Summary_Search_Request/invoke'
  baseUrl: 'http://${aksServiceHost}'
  fullUrl: 'http://${aksServiceHost}/api/${ecsWorkflowName}/triggers/HTTP_ECS_Summary_Search_Request/invoke'
  isInternalOnly: true
}

// ECS workflow parameters (for AKS/Argo Workflows configuration)
// Note: Secure parameters should reference Key Vault
output ecsWorkflowParameters object = {
  backend_base_url: {
    type: 'String'
    value: backendBaseUrl
  }
  // claims_backend_api_token should be configured via Key Vault reference
  // not exposed in outputs due to security concerns
}

// Resource tags for ECS-related resources
output ecsTags object = {
  Component: 'ECS'
  WorkflowType: 'SummarySearch'
  IntegrationType: 'claims backend'
  Environment: contains(baseName, 'prod') ? 'Production' : contains(baseName, 'uat') ? 'UAT' : 'Development'
}

// =========================
// Notes
// =========================
// This module provides configuration outputs for the ECS Summary Search workflow.
// Workflows now run on AKS with Argo Workflows instead of Azure Logic Apps.
//
// Usage in main.bicep:
//   module ecs 'modules/ecs-api.bicep' = {
//     name: 'ecs-api-module'
//     params: {
//       baseName: baseName
//       appInsightsKey: insights.properties.InstrumentationKey
//       appInsightsConnectionString: insights.properties.ConnectionString
//       backendBaseUrl: backendBaseUrl
//       backendApiToken: backendApiToken
//     }
//   }
