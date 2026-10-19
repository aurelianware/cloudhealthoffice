// =========================
// Eligibility Service Infrastructure
// Azure Container Apps + Dapr + Event Grid (documents live in MongoDB / Azure DocumentDB, ADR 017)
// =========================

@description('Base name for all resources')
param baseName string

@description('Location for the resources')
param location string = resourceGroup().location

@description('Container image to deploy')
param containerImage string = 'ghcr.io/cloudhealthoffice/eligibility-service:latest'

@description('Container CPU cores')
param containerCpu string = '0.5'

@description('Container memory (Gi)')
param containerMemory string = '1.0Gi'

@description('Minimum replicas')
@minValue(0)
@maxValue(30)
param minReplicas int = 1

@description('Maximum replicas')
@minValue(1)
@maxValue(30)
param maxReplicas int = 10

@description('Enable Dapr')
param enableDapr bool = true

@description('MongoDB / Azure DocumentDB connection string (must include retrywrites=false for Azure DocumentDB)')
@secure()
param mongoConnectionString string

@description('Eligibility cache TTL in seconds (default 24 hours)')
param cacheTtlSeconds int = 86400

@description('Application Insights connection string')
param appInsightsConnectionString string = ''

@description('CORS allowed origins (comma-separated list)')
param corsAllowedOrigins string = 'https://${baseName}-eligibility-svc.azurecontainerapps.io'

// =========================
// Variables
// =========================
var containerAppEnvName = '${baseName}-eligibility-env'
var containerAppName = '${baseName}-eligibility-svc'
var eventGridTopicName = '${baseName}-eligibility-events'
var logAnalyticsName = '${baseName}-eligibility-logs'

// =========================
// Log Analytics Workspace
// =========================
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// =========================
// Container Apps Environment
// =========================
resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    daprAIConnectionString: appInsightsConnectionString
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

// =========================
// Dapr Components
// =========================

// Dapr Pub/Sub Component - Note: Event Grid is accessed directly via SDK with managed identity
// This component is disabled in favor of direct Event Grid SDK usage for better security
// If you need Dapr pubsub, uncomment and configure for Service Bus instead
/*
resource daprPubSub 'Microsoft.App/managedEnvironments/daprComponents@2024-03-01' = if (enableDapr) {
  parent: containerAppEnv
  name: 'eligibility-pubsub'
  properties: {
    componentType: 'pubsub.azure.servicebus.topics'
    version: 'v1'
    metadata: [
      {
        name: 'namespaceName'
        value: '<your-service-bus-namespace>'
      }
    ]
    scopes: [
      containerAppName
    ]
  }
}
*/

// =========================
// Event Grid Topic
// =========================
resource eventGridTopic 'Microsoft.EventGrid/topics@2024-06-01-preview' = {
  name: eventGridTopicName
  location: location
  properties: {
    inputSchema: 'CloudEventSchemaV1_0'
    publicNetworkAccess: 'Enabled'
  }
}

// =========================
// Container App
// =========================
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 3000
        transport: 'http'
        traffic: [
          {
            weight: 100
            latestRevision: true
          }
        ]
        // Note: CORS is handled by the application using CORS_ALLOWED_ORIGINS env var
        // Do not configure corsPolicy here to avoid double CORS headers
      }
      dapr: enableDapr ? {
        enabled: true
        appId: 'eligibility-service'
        appPort: 3000
        appProtocol: 'http'
        enableApiLogging: true
      } : null
      // The Container App's system-assigned managed identity has the
      // Event Grid Data Sender role (see eventGridRoleAssignment). The only
      // secret is the MongoDB connection string.
      secrets: [
        {
          name: 'mongo-connection-string'
          value: mongoConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'eligibility-service'
          image: containerImage
          resources: {
            cpu: json(containerCpu)
            memory: containerMemory
          }
          env: [
            {
              name: 'PORT'
              value: '3000'
            }
            {
              name: 'MongoDb__ConnectionString'
              secretRef: 'mongo-connection-string'
            }
            {
              name: 'BatchEligibility__MongoDb__ConnectionString'
              secretRef: 'mongo-connection-string'
            }
            {
              name: 'EVENT_GRID_ENDPOINT'
              value: eventGridTopic.properties.endpoint
            }
            // EVENT_GRID_KEY is not set - using managed identity authentication
            {
              name: 'CACHE_ENABLED'
              value: 'true'
            }
            {
              name: 'CACHE_ACTIVE_TTL'
              value: string(cacheTtlSeconds)
            }
            {
              name: 'CACHE_INACTIVE_TTL'
              value: '3600'
            }
            {
              name: 'CACHE_MAX_AGE'
              value: '43200'
            }
            {
              name: 'DAPR_ENABLED'
              value: string(enableDapr)
            }
            {
              name: 'DAPR_APP_ID'
              value: 'eligibility-service'
            }
            {
              name: 'DAPR_PUBSUB'
              value: 'eligibility-pubsub'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'CORS_ALLOWED_ORIGINS'
              value: corsAllowedOrigins
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/livez'
                port: 3000
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/readyz'
                port: 3000
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-rule'
            http: {
              metadata: {
                concurrentRequests: '100'
              }
            }
          }
        ]
      }
    }
  }
}

// =========================
// Role Assignments
// =========================

// Event Grid Data Sender role for Container App
resource eventGridRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(eventGridTopic.id, containerApp.id, 'd5a91429-5739-47e2-a06b-3470a27159e7')
  scope: eventGridTopic
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'd5a91429-5739-47e2-a06b-3470a27159e7') // Event Grid Data Sender
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// =========================
// Outputs
// =========================
output containerAppName string = containerApp.name
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output containerAppUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output eventGridTopicName string = eventGridTopic.name
output eventGridTopicEndpoint string = eventGridTopic.properties.endpoint
output containerAppPrincipalId string = containerApp.identity.principalId

// API Endpoints
output x12EligibilityEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/api/eligibility/x12'
output fhirEligibilityEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/api/eligibility/fhir'
output healthEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/health'
