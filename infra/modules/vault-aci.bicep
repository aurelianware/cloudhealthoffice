// HashiCorp Vault on Azure Container Instances
// Alternative deployment for Azure-only environments
// For production, prefer Kubernetes deployment (see infra/vault/values.yaml)

@description('Name for the Vault container instance')
param vaultName string = 'vault-${uniqueString(resourceGroup().id)}'

@description('Azure region for deployment')
param location string = resourceGroup().location

@description('HashiCorp Vault version')
param vaultVersion string = '1.15.4'

@description('DNS name label for public access')
param dnsNameLabel string = 'cho-vault-${uniqueString(resourceGroup().id)}'

@description('Number of CPU cores')
param cpuCores int = 1

@description('Memory in GB')
param memoryInGb int = 2

@description('Storage account name for Vault data persistence')
param storageAccountName string

@description('File share name for Vault data')
param fileShareName string = 'vault-data'

@description('Environment tag')
param environment string = 'production'

// Storage account for Vault data persistence
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

// Container instance for HashiCorp Vault
resource vaultContainer 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: vaultName
  location: location
  tags: {
    Environment: environment
    Purpose: 'SecretManagement'
    ManagedBy: 'CloudHealthOffice'
  }
  properties: {
    osType: 'Linux'
    restartPolicy: 'Always'
    
    ipAddress: {
      type: 'Public'
      dnsNameLabel: dnsNameLabel
      ports: [
        {
          protocol: 'TCP'
          port: 8200
        }
        {
          protocol: 'TCP'
          port: 8201
        }
      ]
    }

    containers: [
      {
        name: 'vault'
        properties: {
          image: 'hashicorp/vault:${vaultVersion}'
          
          ports: [
            {
              port: 8200
              protocol: 'TCP'
            }
            {
              port: 8201
              protocol: 'TCP'
            }
          ]

          environmentVariables: [
            {
              name: 'VAULT_ADDR'
              value: 'http://127.0.0.1:8200'
            }
            {
              name: 'VAULT_API_ADDR'
              value: 'http://${dnsNameLabel}.${location}.azurecontainer.io:8200'
            }
            {
              name: 'VAULT_CLUSTER_ADDR'
              value: 'http://${dnsNameLabel}.${location}.azurecontainer.io:8201'
            }
          ]

          resources: {
            requests: {
              cpu: cpuCores
              memoryInGB: memoryInGb
            }
          }

          volumeMounts: [
            {
              name: 'vault-data'
              mountPath: '/vault/data'
            }
            {
              name: 'vault-config'
              mountPath: '/vault/config'
            }
            {
              name: 'vault-logs'
              mountPath: '/vault/logs'
            }
          ]

          command: [
            'vault'
            'server'
            '-config=/vault/config/vault-config.hcl'
          ]
        }
      }
    ]

    volumes: [
      {
        name: 'vault-data'
        azureFile: {
          shareName: fileShareName
          storageAccountName: storageAccountName
          storageAccountKey: storageAccount.listKeys().keys[0].value
        }
      }
      {
        name: 'vault-config'
        emptyDir: {}
      }
      {
        name: 'vault-logs'
        emptyDir: {}
      }
    ]

    initContainers: [
      {
        name: 'vault-config-init'
        properties: {
          image: 'busybox:latest'
          command: [
            'sh'
            '-c'
            '''
            cat > /vault/config/vault-config.hcl <<EOF
            ui = true

            listener "tcp" {
              address = "0.0.0.0:8200"
              tls_disable = 1
            }

            storage "file" {
              path = "/vault/data"
            }

            api_addr = "http://${HOSTNAME}:8200"
            cluster_addr = "http://${HOSTNAME}:8201"

            telemetry {
              disable_hostname = true
              prometheus_retention_time = "12h"
            }
            EOF
            echo "Vault configuration created successfully"
            '''
          ]
          volumeMounts: [
            {
              name: 'vault-config'
              mountPath: '/vault/config'
            }
          ]
        }
      }
    ]
  }
}

// Outputs
output vaultFqdn string = vaultContainer.properties.ipAddress.fqdn
output vaultIp string = vaultContainer.properties.ipAddress.ip
output vaultUrl string = 'http://${vaultContainer.properties.ipAddress.fqdn}:8200'
output vaultName string = vaultContainer.name
