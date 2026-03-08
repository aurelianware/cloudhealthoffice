// main.bicep - Generated placeholder for Regional Blue Cross Blue Shield
// TODO: Complete infrastructure template

@description('Base name for resources')
param baseName string = 'blues02'

@description('Azure region')
param location string = 'centralus'

@description('Environment name')
param environment string = 'prod'

@description('Resource tags')
param tags object = {
  "Project": "EDI-Integration",
  "Payer": "BLUES02",
  "Environment": "Production",
  "CostCenter": "EDI-Operations",
  "Compliance": "HIPAA"
}

// Add resource definitions here
