// =========================
// Cosmos DB Module for Prior Authorization
// =========================

@description('Base name for the Cosmos DB resources')
param baseName string

@description('Location for the Cosmos DB account')
param location string = resourceGroup().location

@description('Throughput for the database (RU/s)')
@minValue(400)
@maxValue(100000)
param throughput int = 400

@description('Enable serverless mode instead of provisioned throughput')
param enableServerless bool = false

// =========================
// Cosmos DB Account
// =========================
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2023-04-15' = {
  name: '${baseName}-cosmos'
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
    capabilities: enableServerless ? [
      {
        name: 'EnableServerless'
      }
    ] : []
    enableFreeTier: false
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

// =========================
// Database
// =========================
resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2023-04-15' = {
  parent: cosmosAccount
  name: 'cloudhealthoffice'
  properties: {
    resource: {
      id: 'cloudhealthoffice'
    }
    options: enableServerless ? {} : {
      throughput: throughput
    }
  }
}

// =========================
// Containers
// =========================

// PriorAuthorizations Container - stores prior auth requests and responses
resource priorAuthContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  parent: cosmosDatabase
  name: 'PriorAuthorizations'
  properties: {
    resource: {
      id: 'PriorAuthorizations'
      partitionKey: {
        paths: ['/patientId']
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
            path: '/originalBundle/*'
          }
          {
            path: '/"_etag"/?'
          }
        ]
        compositeIndexes: [
          [
            {
              path: '/patientId'
              order: 'ascending'
            }
            {
              path: '/createdAt'
              order: 'descending'
            }
          ]
          [
            {
              path: '/status'
              order: 'ascending'
            }
            {
              path: '/sla/decisionDueBy'
              order: 'ascending'
            }
          ]
        ]
      }
      defaultTtl: -1
      uniqueKeyPolicy: {
        uniqueKeys: [
          {
            paths: ['/requestId']
          }
        ]
      }
    }
  }
}

// ProviderDirectory Container - caches NPPES data
resource providerDirectoryContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  parent: cosmosDatabase
  name: 'ProviderDirectory'
  properties: {
    resource: {
      id: 'ProviderDirectory'
      partitionKey: {
        paths: ['/npi']
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
      defaultTtl: 86400 // 24 hours default cache
    }
  }
}

// Members Container - patient/member data (if not already exists)
resource membersContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  parent: cosmosDatabase
  name: 'Members'
  properties: {
    resource: {
      id: 'Members'
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
            path: '/"_etag"/?'
          }
        ]
      }
      defaultTtl: -1
    }
  }
}

// Claims Container
resource claimsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  parent: cosmosDatabase
  name: 'Claims'
  properties: {
    resource: {
      id: 'Claims'
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
            path: '/"_etag"/?'
          }
        ]
      }
      defaultTtl: -1
    }
  }
}

// Payments Container
resource paymentsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  parent: cosmosDatabase
  name: 'Payments'
  properties: {
    resource: {
      id: 'Payments'
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
            path: '/"_etag"/?'
          }
        ]
      }
      defaultTtl: -1
    }
  }
}

// Rate Limits Container
resource rateLimitContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  parent: cosmosDatabase
  name: 'rate_limits'
  properties: {
    resource: {
      id: 'rate_limits'
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
            path: '/"_etag"/?'
          }
        ]
      }
      defaultTtl: 3600 // 1 hour TTL for rate limit docs
    }
  }
}

// =========================
// Outputs
// =========================
output cosmosAccountName string = cosmosAccount.name
output cosmosAccountEndpoint string = cosmosAccount.properties.documentEndpoint
output cosmosDatabaseName string = cosmosDatabase.name
output priorAuthContainerName string = priorAuthContainer.name
output providerDirectoryContainerName string = providerDirectoryContainer.name
output cosmosAccountId string = cosmosAccount.id
@description('Cosmos DB primary key for API connections')
output cosmosPrimaryKey string = cosmosAccount.listKeys().primaryMasterKey
@description('Cosmos DB connection string - use Key Vault references in production')
output cosmosConnectionString string = cosmosAccount.listConnectionStrings().connectionStrings[0].connectionString
