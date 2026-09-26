// Provider eligibility API beside CloudDentalOffice.
//
// Deploys src/services/provider-eligibility-api into the existing Container
// Apps environment shared with CloudDentalOffice, following the estimate-only
// app (estimate-container-app.bicep). Differences: ingress is internal only —
// the app is reachable from CDO inside the environment, never from the
// internet — and it needs no database.

@description('Region of the existing Container Apps environment.')
param location string = 'westus3'

@description('Existing Azure Container Registry name.')
param acrName string = 'clouhealthoffice'

@description('Existing Key Vault name.')
param keyVaultName string = 'cho-kv'

@description('Resource group containing the existing Container Apps environment.')
param managedEnvironmentResourceGroup string = 'cdo-prod-rg'

@description('Existing Container Apps environment shared with CloudDentalOffice.')
param managedEnvironmentName string = 'cdo-env'

@description('Container image containing provider-eligibility-api.')
param providerEligibilityImage string

@allowed([ 'test', 'production' ])
@description('Stedi key mode. Test-mode keys return Stedi mock responses only; real patients need a production key.')
param stediEnvironment string = 'test'

@secure()
@description('Stedi API key matching stediEnvironment.')
param stediApiKey string

@description('Client label recorded in logs for CloudDentalOffice calls.')
param cdoClientName string = 'cloud-dental-office'

@secure()
@description('Service credential CloudDentalOffice sends in X-Api-Key.')
param cdoClientApiKey string

@description('CloudDentalOffice tenant ids this credential may act for.')
param cdoTenantIds array

var appName = 'provider-eligibility'
var identityName = 'cho-provider-eligibility-identity'

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

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, identity.id, 'provider-eligibility-acr-pull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, 'provider-eligibility-key-vault-secrets-user')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource stediApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'provider-eligibility-stedi-api-key'
  properties: {
    value: stediApiKey
  }
}

resource cdoClientApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'provider-eligibility-cdo-api-key'
  properties: {
    value: cdoClientApiKey
  }
}

var tenantEnv = [for (tenant, i) in cdoTenantIds: {
  name: 'ProviderApi__Clients__0__Tenants__${i}'
  value: tenant
}]

resource providerEligibility 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
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
          name: 'stedi-api-key'
          keyVaultUrl: stediApiKeySecret.properties.secretUri
          identity: identity.id
        }
        {
          name: 'cdo-api-key'
          keyVaultUrl: cdoClientApiKeySecret.properties.secretUri
          identity: identity.id
        }
      ]
      ingress: {
        external: false
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'provider-eligibility-api'
          image: providerEligibilityImage
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'HealthcareTransactions__DefaultGateway', value: 'Stedi' }
            { name: 'HealthcareTransactions__Gateways__Stedi__Environment', value: stediEnvironment }
            { name: 'HealthcareTransactions__Gateways__Stedi__ApiKey', secretRef: 'stedi-api-key' }
            { name: 'ProviderApi__Clients__0__Name', value: cdoClientName }
            { name: 'ProviderApi__Clients__0__ApiKey', secretRef: 'cdo-api-key' }
            { name: 'Observability__EnableConsole', value: 'false' }
            { name: 'Observability__OtlpEndpoint', value: '' }
          ], tenantEnv)
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080, scheme: 'HTTP' }
              initialDelaySeconds: 10
              periodSeconds: 20
              timeoutSeconds: 5
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080, scheme: 'HTTP' }
              initialDelaySeconds: 5
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 6
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
  dependsOn: [ acrPull, keyVaultSecretsUser ]
}

output providerEligibilityFqdn string = providerEligibility.properties.configuration.ingress.fqdn
output eligibilityCheckUrl string = 'https://${providerEligibility.properties.configuration.ingress.fqdn}/api/v1/eligibility/check'
output environmentName string = environment.name
