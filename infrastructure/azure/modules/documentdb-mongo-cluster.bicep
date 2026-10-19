// Azure DocumentDB (formerly Azure Cosmos DB for MongoDB vCore) cluster: the
// one persistence target for every CloudHealthOffice service (ADR 017). Services
// connect with an ordinary MongoDB connection string; nothing uses the Cosmos
// DB SQL API.
//
// The default tier is 'Free' (32 GiB, no HA, no backup, paused after 60 days
// without connections) for the demo environment, which is wiped and rebuilt
// rather than migrated. Use M30 or higher with highAvailabilityMode
// 'ZoneRedundantPreferred' for anything that holds data worth keeping.

@description('Base name used to derive the cluster name')
param baseName string

@description('Azure region for the cluster')
param location string = resourceGroup().location

@description('Cluster name (3-40 lowercase letters, digits and hyphens)')
@minLength(3)
@maxLength(40)
param clusterName string = take(toLower('${baseName}-docdb-${uniqueString(subscription().id, resourceGroup().id)}'), 40)

@description('Native administrator user name')
param administratorUserName string = 'choadmin'

@description('Native administrator password. Store the resulting connection string in Key Vault; never in app settings.')
@secure()
param administratorPassword string

@description('Compute tier: Free, M10, M20, M25 (dev/test, single node), M30 and up for production')
param computeTier string = 'Free'

@description('Storage per shard in GiB')
param storageSizeGb int = 32

@description('High availability: Disabled, SameZone or ZoneRedundantPreferred (requires M30 or higher)')
@allowed([
  'Disabled'
  'SameZone'
  'ZoneRedundantPreferred'
])
param highAvailabilityMode string = 'Disabled'

@description('MongoDB server version reported to drivers. MongoDB.Driver 3.x needs 4.2+ wire protocol; 7.0 matches the EphemeralMongo version the tests run against.')
param serverVersion string = '7.0'

@description('Public network access. Disable it once private endpoints are in place.')
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Enabled'

@description('Client IP ranges allowed through the public endpoint, as { name, startIpAddress, endIpAddress }.')
param firewallRules array = []

resource cluster 'Microsoft.DocumentDB/mongoClusters@2024-07-01' = {
  name: clusterName
  location: location
  tags: {
    ManagedBy: 'Bicep'
    Purpose: 'ChoPersistence'
    Tier: computeTier
  }
  properties: {
    administrator: {
      userName: administratorUserName
      password: administratorPassword
    }
    serverVersion: serverVersion
    compute: {
      tier: computeTier
    }
    storage: {
      sizeGb: storageSizeGb
    }
    sharding: {
      shardCount: 1
    }
    highAvailability: {
      targetMode: highAvailabilityMode
    }
    publicNetworkAccess: publicNetworkAccess
  }
}

resource rules 'Microsoft.DocumentDB/mongoClusters/firewallRules@2024-07-01' = [for rule in firewallRules: {
  parent: cluster
  name: rule.name
  properties: {
    startIpAddress: rule.startIpAddress
    endIpAddress: rule.endIpAddress
  }
}]

output clusterName string = cluster.name
output clusterId string = cluster.id
// The connection string carries the password, so it is not an output. Build it
// as mongodb+srv://<user>:<password>@<clusterName>.global.mongocluster.cosmos.azure.com/
//   ?tls=true&authMechanism=SCRAM-SHA-256&retrywrites=false&maxIdleTimeMS=120000
// and store it in Key Vault (MongoDb--ConnectionString).
