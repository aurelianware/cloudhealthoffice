// ============================================================
// Cloud Health Office – Azure Infrastructure
// Provisions: ACR, AKS, Cosmos DB (MongoDB), ACA (marketing site)
// ============================================================

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Short base name used to name all resources')
param baseName string = 'cho'

@description('AKS node VM size')
param aksNodeSize string = 'Standard_DC2s_v3'

@description('Number of AKS system nodes')
@minValue(2)
param aksNodeCount int = 2

@description('Environment tag (prod, staging, etc.)')
param environment string = 'prod'

var acrName = '${baseName}acr${uniqueString(resourceGroup().id)}'
var aksName = '${baseName}-aks'
var acaEnvName = '${baseName}-aca-env'
var acaSiteName = '${baseName}-site'
var logAnalyticsName = '${baseName}-logs'

// ─── Log Analytics ────────────────────────────────────────────
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: { environment: environment }
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ─── Azure Container Registry ────────────────────────────────
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  tags: { environment: environment }
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

// ─── AKS Cluster ─────────────────────────────────────────────
resource aks 'Microsoft.ContainerService/managedClusters@2024-02-01' = {
  name: aksName
  location: location
  tags: { environment: environment }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: aksName
    agentPoolProfiles: [
      {
        name: 'system'
        count: aksNodeCount
        vmSize: aksNodeSize
        osType: 'Linux'
        osDiskSizeGB: 60
        mode: 'System'
        enableAutoScaling: true
        minCount: 2
        maxCount: 6
      }
    ]
    networkProfile: {
      networkPlugin: 'azure'
      loadBalancerSku: 'standard'
    }
    // Container Insights addon omitted – enable after Microsoft.OperationsManagement provider is registered
  }
}

// Grant AKS kubelet identity permission to pull from ACR
resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, aks.id, 'acrpull')
  scope: acr
  properties: {
    // AcrPull built-in role ID
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
  }
}

// ─── Container Apps Environment ───────────────────────────────
resource acaEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: acaEnvName
  location: location
  tags: { environment: environment }
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// ─── Container App – Marketing Site ──────────────────────────
resource acaSite 'Microsoft.App/containerApps@2024-03-01' = {
  name: acaSiteName
  location: location
  tags: { environment: environment }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: acaEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 80
        transport: 'http'
        allowInsecure: false
      }
      // Registry auth added by CI/CD after first ACR image push
    }
    template: {
      containers: [
        {
          name: 'site'
          // Placeholder image – CI/CD will update this to the real ACR image after first build
          image: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 5
        rules: [
          {
            name: 'http-scale'
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

// Grant ACA system identity AcrPull on ACR (used once CI/CD pushes real image)
resource acaAcrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, acaSite.id, 'acrpull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: acaSite.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ─── Outputs ─────────────────────────────────────────────────
@description('ACR login server (e.g. choacrXXXX.azurecr.io)')
output acrLoginServer string = acr.properties.loginServer

@description('ACR resource name')
output acrName string = acr.name

@description('AKS cluster name')
output aksName string = aks.name

@description('Azure Container App FQDN for the marketing site')
output acaSiteFqdn string = acaSite.properties.configuration.ingress.fqdn

@description('Log Analytics workspace ID')
output logAnalyticsWorkspaceId string = logAnalytics.id
