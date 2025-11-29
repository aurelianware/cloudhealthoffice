// =========================
// Eligibility Service Infrastructure
// Azure Container Apps + Dapr + Cosmos DB + Event Grid
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

@description('Cosmos DB throughput (RU/s)')
@minValue(400)
@maxValue(100000)
param cosmosDbThroughput int = 400

@description('Eligibility cache TTL in seconds (default 24 hours)')
param cacheTtlSeconds int = 86400

@description('Application Insights connection string')
param appInsightsConnectionString string = ''

// =========================
// Variables
// =========================
var containerAppEnvName = '${baseName}-eligibility-env'
var containerAppName = '${baseName}-eligibility-svc'
var cosmosAccountName = '${baseName}-eligibility-cosmos'
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

// Dapr State Store Component (using Cosmos DB)
resource daprStateStore 'Microsoft.App/managedEnvironments/daprComponents@2024-03-01' = if (enableDapr) {
  parent: containerAppEnv
  name: 'eligibility-state'
  properties: {
    componentType: 'state.azure.cosmosdb'
    version: 'v1'
    metadata: [
      {
        name: 'url'
        value: cosmosAccount.properties.documentEndpoint
      }
      {
        name: 'database'
        value: 'eligibility-db'
      }
      {
        name: 'collection'
        value: 'dapr-state'
      }
      {
        name: 'masterKey'
        secretRef: 'cosmos-key'
      }
    ]
    secrets: [
      {
        name: 'cosmos-key'
        value: cosmosAccount.listKeys().primaryMasterKey
      }
    ]
    scopes: [
      containerAppName
    ]
  }
}

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
// Cosmos DB Account
// =========================
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
      maxIntervalInSeconds: 5
      maxStalenessPrefix: 100
    }
    capabilities: []
    enableFreeTier: false
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

// =========================
// Cosmos DB Database
// =========================
resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'eligibility-db'
  properties: {
    resource: {
      id: 'eligibility-db'
    }
    options: {
      throughput: cosmosDbThroughput
    }
  }
}

// =========================
// Cosmos DB Containers
// =========================

// Eligibility Cache Container with TTL
resource eligibilityCacheContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'eligibility-cache'
  properties: {
    resource: {
      id: 'eligibility-cache'
      partitionKey: {
        paths: ['/memberId']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/*'
          }
        ]
        excludedPaths: [
          {
            path: '/originalRequest/*'
          }
          {
            path: '/response/*'
          }
          {
            path: '/"_etag"/?'
          }
        ]
        compositeIndexes: [
          [
            {
              path: '/payerId'
              order: 'ascending'
            }
            {
              path: '/createdAt'
              order: 'descending'
            }
          ]
          [
            {
              path: '/cacheKey'
              order: 'ascending'
            }
          ]
        ]
      }
      // Enable TTL for automatic cache expiration
      defaultTtl: cacheTtlSeconds
      uniqueKeyPolicy: {
        uniqueKeys: [
          {
            paths: ['/cacheKey']
          }
        ]
      }
    }
  }
}

// Eligibility Rules Container
resource eligibilityRulesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'eligibility-rules'
  properties: {
    resource: {
      id: 'eligibility-rules'
      partitionKey: {
        paths: ['/planCode']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/*'
          }
        ]
        excludedPaths: [
          {
            path: '/"_etag"/?'
          }
        ]
        compositeIndexes: [
          [
            {
              path: '/planCode'
              order: 'ascending'
            }
            {
              path: '/serviceTypeCode'
              order: 'ascending'
            }
            {
              path: '/priority'
              order: 'ascending'
            }
          ]
        ]
      }
      defaultTtl: -1 // No TTL for rules
    }
  }
}

// Dapr State Container
resource daprStateContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = if (enableDapr) {
  parent: cosmosDatabase
  name: 'dapr-state'
  properties: {
    resource: {
      id: 'dapr-state'
      partitionKey: {
        paths: ['/partitionKey']
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/*'
          }
        ]
        excludedPaths: [
          {
            path: '/"_etag"/?'
          }
        ]
      }
      defaultTtl: -1
    }
  }
}

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
        corsPolicy: {
          allowedOrigins: ['*']
          allowedMethods: ['GET', 'POST', 'OPTIONS']
          allowedHeaders: ['*']
        }
      }
      dapr: enableDapr ? {
        enabled: true
        appId: 'eligibility-service'
        appPort: 3000
        appProtocol: 'http'
        enableApiLogging: true
      } : null
      secrets: [
        {
          name: 'cosmos-endpoint'
          value: cosmosAccount.properties.documentEndpoint
        }
        {
          name: 'cosmos-key'
          value: cosmosAccount.listKeys().primaryMasterKey
        }
        {
          name: 'eventgrid-endpoint'
          value: eventGridTopic.properties.endpoint
        }
        {
          name: 'eventgrid-key'
          value: eventGridTopic.listKeys().key1
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
              name: 'COSMOS_ENDPOINT'
              secretRef: 'cosmos-endpoint'
            }
            {
              name: 'COSMOS_KEY'
              secretRef: 'cosmos-key'
            }
            {
              name: 'COSMOS_DATABASE'
              value: 'eligibility-db'
            }
            {
              name: 'COSMOS_CONTAINER'
              value: 'eligibility-cache'
            }
            {
              name: 'EVENT_GRID_ENDPOINT'
              secretRef: 'eventgrid-endpoint'
            }
            {
              name: 'EVENT_GRID_KEY'
              secretRef: 'eventgrid-key'
            }
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
              name: 'DAPR_STATE_STORE'
              value: 'eligibility-state'
            }
            {
              name: 'DAPR_PUBSUB'
              value: 'eligibility-pubsub'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
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

// Cosmos DB Data Contributor role for Container App
resource cosmosRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, containerApp.id, '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: containerApp.identity.principalId
    scope: cosmosAccount.id
  }
}

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
output cosmosAccountName string = cosmosAccount.name
output cosmosAccountEndpoint string = cosmosAccount.properties.documentEndpoint
output cosmosDatabaseName string = cosmosDatabase.name
output eligibilityCacheContainerName string = eligibilityCacheContainer.name
output eligibilityRulesContainerName string = eligibilityRulesContainer.name
output eventGridTopicName string = eventGridTopic.name
output eventGridTopicEndpoint string = eventGridTopic.properties.endpoint
output containerAppPrincipalId string = containerApp.identity.principalId

// API Endpoints
output x12EligibilityEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/api/eligibility/x12'
output fhirEligibilityEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/api/eligibility/fhir'
output healthEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/health'
