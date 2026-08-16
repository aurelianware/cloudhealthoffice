@description('Region of the existing Container Apps environment.')
param location string = 'westus3'

@description('Existing Azure Container Registry name.')
param acrName string = 'clouhealthoffice'

@description('Existing Key Vault name.')
param keyVaultName string = 'cho-kv'

@description('Existing Cosmos DB for MongoDB account name.')
param cosmosMongoAccountName string = 'cho-mcc-mongo-vwckcgcxziggo'

@description('Resource group containing the existing Container Apps environment.')
param managedEnvironmentResourceGroup string = 'cdo-prod-rg'

@description('Existing Container Apps environment shared with CloudDentalOffice.')
param managedEnvironmentName string = 'cdo-env'

@description('Container image containing benefit-plan-service.')
param benefitPlanImage string

@description('Redis image mirrored into the existing ACR.')
param redisImage string = '${acrName}.azurecr.io/third-party/redis:7.4-alpine'

@secure()
@description('Shared service credential accepted by the estimate API.')
param estimateApiKey string

@secure()
@description('Password for the internal Redis instance.')
param redisPassword string

@description('Mongo database used by benefit-plan-service.')
param mongoDatabaseName string = 'cloudhealthoffice'

var benefitPlanAppName = 'benefit-plan-estimate'
var redisAppName = 'estimate-redis'
var identityName = 'cho-estimate-identity'

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  scope: resourceGroup(managedEnvironmentResourceGroup)
  name: managedEnvironmentName
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource cosmosMongo 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosMongoAccountName
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, identity.id, 'estimate-acr-pull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, 'estimate-key-vault-secrets-user')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'estimate-api-key'
  properties: {
    value: estimateApiKey
  }
}

resource redisPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'estimate-redis-password'
  properties: {
    value: redisPassword
  }
}

resource mongoConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'estimate-mongodb-connection-string'
  properties: {
    value: cosmosMongo.listConnectionStrings().connectionStrings[0].connectionString
  }
}

resource redisConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'estimate-redis-connection-string'
  properties: {
    value: '${redisAppName}:6379,password=${redisPassword},ssl=False,abortConnect=False'
  }
}

resource redis 'Microsoft.App/containerApps@2024-03-01' = {
  name: redisAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: acr.properties.loginServer
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'redis-password'
          keyVaultUrl: redisPasswordSecret.properties.secretUri
          identity: identity.id
        }
      ]
      ingress: {
        external: false
        targetPort: 6379
        exposedPort: 6379
        transport: 'tcp'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'redis'
          image: redisImage
          command: [ '/bin/sh' ]
          args: [ '-c', 'exec redis-server --save 60 1 --appendonly yes --requirepass "$REDIS_PASSWORD"' ]
          env: [
            {
              name: 'REDIS_PASSWORD'
              secretRef: 'redis-password'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [ acrPull, keyVaultSecretsUser ]
}

resource benefitPlan 'Microsoft.App/containerApps@2024-03-01' = {
  name: benefitPlanAppName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: acr.properties.loginServer
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'estimate-api-key'
          keyVaultUrl: apiKeySecret.properties.secretUri
          identity: identity.id
        }
        {
          name: 'mongodb-connection'
          keyVaultUrl: mongoConnectionSecret.properties.secretUri
          identity: identity.id
        }
        {
          name: 'redis-connection'
          keyVaultUrl: redisConnectionSecret.properties.secretUri
          identity: identity.id
        }
      ]
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'benefit-plan-service'
          image: benefitPlanImage
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'EstimateApi__Enabled', value: 'true' }
            { name: 'EstimateApi__EstimateOnly', value: 'true' }
            { name: 'EstimateApi__ApiKey', secretRef: 'estimate-api-key' }
            { name: 'MongoDb__ConnectionString', secretRef: 'mongodb-connection' }
            { name: 'MongoDb__DatabaseName', value: mongoDatabaseName }
            { name: 'Redis__ConnectionString', secretRef: 'redis-connection' }
            { name: 'Services__ClaimsServiceUrl', value: 'http://127.0.0.1:9/' }
            { name: 'Observability__EnableConsole', value: 'true' }
            { name: 'Observability__OtlpEndpoint', value: '' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
              initialDelaySeconds: 20
              periodSeconds: 20
              timeoutSeconds: 5
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080, scheme: 'HTTP' }
              initialDelaySeconds: 10
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 6
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
        rules: [
          {
            name: 'http-concurrency'
            http: { metadata: { concurrentRequests: '20' } }
          }
        ]
      }
    }
  }
  dependsOn: [ redis, acrPull, keyVaultSecretsUser ]
}

output benefitPlanFqdn string = benefitPlan.properties.configuration.ingress.fqdn
output estimateUrl string = 'https://${benefitPlan.properties.configuration.ingress.fqdn}/api/v1/adjudication/estimate'
output environmentName string = environment.name
