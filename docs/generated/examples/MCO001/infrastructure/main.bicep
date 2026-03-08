// main.bicep - Generated placeholder for State Medicaid MCO
// TODO: Complete infrastructure template

@description('Base name for resources')
param baseName string = 'mco001'

@description('Azure region')
param location string = 'eastus'

@description('Environment name')
param environment string = 'dev'

@description('Resource tags')
param tags object = {
  "Project": "HIPAA-Attachments",
  "Payer": "MCO001",
  "Environment": "Dev",
  "CostCenter": "IT-Integration"
}

// Add resource definitions here
