// Azure Cosmos DB for MongoDB account intended for development and
// Million Claim Challenge persistence experiments.
//
// Free tier must be selected when the account is created. Keeping shared
// database throughput at 1,000 RU/s and storage below 25 GB keeps this module
// within the lifetime free-tier allowance.

@description('Base name used to derive a globally unique Cosmos DB account name')
param baseName string

@description('Azure region for the Cosmos DB account')
param location string = resourceGroup().location

@description('Cosmos DB for MongoDB account name')
@minLength(3)
@maxLength(44)
param accountName string = take(toLower('${baseName}-mongo-${uniqueString(subscription().id, resourceGroup().id)}'), 44)

@description('MongoDB database name')
param databaseName string = 'cloudhealthoffice'

@description('Shared database throughput. The first 1,000 RU/s receives the free-tier discount; higher benchmark values are billable.')
@minValue(1000)
@maxValue(100000)
param throughput int = 1000

resource cosmosMongoAccount 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: accountName
  location: location
  kind: 'MongoDB'
  tags: {
    ManagedBy: 'Bicep'
    Purpose: 'MccCosmosMongo'
    Tier: 'Free'
  }
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
    }
    capabilities: [
      {
        name: 'EnableMongo'
      }
    ]
    apiProperties: {
      // MongoDB.Driver 3.x requires wire version 9 or newer. Cosmos DB for
      // MongoDB 4.2 reports wire version 8, so use 5.0 for compatibility.
      serverVersion: '5.0'
    }
    enableFreeTier: true
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

resource mongoDatabase 'Microsoft.DocumentDB/databaseAccounts/mongodbDatabases@2024-11-15' = {
  parent: cosmosMongoAccount
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
    options: {
      throughput: throughput
    }
  }
}

output cosmosMongoAccountName string = cosmosMongoAccount.name
output cosmosMongoEndpoint string = cosmosMongoAccount.properties.documentEndpoint
output cosmosMongoDatabaseName string = mongoDatabase.name
output freeTierEnabled bool = cosmosMongoAccount.properties.enableFreeTier
output sharedThroughput int = throughput
