// ================================================
// Azure Monitor Workbooks Module
// ================================================
// Deploys real-time monitoring dashboards for HIPAA X12 EDI transactions
// Tracks transaction latency, error rates, and volume per payer integration
// All workbooks include PHI redaction and HIPAA compliance controls

param location string = resourceGroup().location
param baseName string
param appInsightsId string

// Tags for resource organization
param tags object = {
  Environment: 'Production'
  Application: 'CloudHealthOffice'
  Component: 'Monitoring'
  ManagedBy: 'Bicep'
}

// ================================================
// Variables
// ================================================

var ediTransactionMetricsName = '${baseName}-edi-metrics'
var payerHealthName = '${baseName}-payer-health'
var hipaaComplianceName = '${baseName}-hipaa-compliance'
var cms0057fComplianceName = '${baseName}-cms0057f-compliance'

// Load workbook templates from JSON files
var ediTransactionMetricsTemplate = loadTextContent('workbooks/edi-transaction-metrics.json')
var payerIntegrationHealthTemplate = loadTextContent('workbooks/payer-integration-health.json')
var hipaaComplianceTemplate = loadTextContent('workbooks/hipaa-compliance-monitoring.json')
var cms0057fComplianceTemplate = loadTextContent('workbooks/cms-0057f-compliance-dashboard.json')

// ================================================
// EDI Transaction Metrics Workbook
// ================================================
// Real-time monitoring of all EDI transaction types (275, 277, 278)
// Tracks volume, latency, success rates, and error patterns

resource ediTransactionMetricsWorkbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: guid(resourceGroup().id, ediTransactionMetricsName)
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'EDI Transaction Metrics'
    description: 'Real-time monitoring of HIPAA X12 transaction processing - latency, volume, error rates with PHI redaction'
    serializedData: ediTransactionMetricsTemplate
    sourceId: appInsightsId
    category: 'workbook'
    version: '1.0'
  }
}

// ================================================
// Payer Integration Health Workbook
// ================================================
// Per-payer health monitoring with multi-tenant support
// Tracks integration status, backend connectivity, and payer-specific metrics

resource payerIntegrationHealthWorkbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: guid(resourceGroup().id, payerHealthName)
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'Payer Integration Health'
    description: 'Per-payer monitoring of EDI transaction health and integration status with health scoring'
    serializedData: payerIntegrationHealthTemplate
    sourceId: appInsightsId
    category: 'workbook'
    version: '1.0'
  }
}

// ================================================
// HIPAA Compliance Monitoring Workbook
// ================================================
// Validates PHI redaction and tracks security audit events
// Monitors encryption, authentication, and data retention compliance

resource hipaaComplianceWorkbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: guid(resourceGroup().id, hipaaComplianceName)
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'HIPAA Compliance Monitoring'
    description: 'Real-time validation of PHI redaction and HIPAA compliance controls with security audit logging'
    serializedData: hipaaComplianceTemplate
    sourceId: appInsightsId
    category: 'workbook'
    version: '1.0'
  }
}

// ================================================
// CMS-0057-F Compliance Dashboard Workbook
// ================================================
// Monitors CMS Prior Authorization Rule compliance metrics:
// - Patient Access API enablement percentage
// - Prior Authorization response times (72hr urgent, 7-day standard)
// - Error rates for 270/271, 278, 837 transactions
// - PHI redaction audit log from Key Vault
// - Export to PDF and scheduled email alerts support

resource cms0057fComplianceWorkbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: guid(resourceGroup().id, cms0057fComplianceName)
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'CMS-0057-F Compliance Dashboard'
    description: 'CMS Prior Authorization Rule compliance monitoring - Patient Access API, Prior Auth response times, transaction error rates, PHI audit logging'
    serializedData: cms0057fComplianceTemplate
    sourceId: appInsightsId
    category: 'workbook'
    version: '1.0'
  }
}

// ================================================
// Outputs
// ================================================

output ediTransactionMetricsWorkbookId string = ediTransactionMetricsWorkbook.id
output ediTransactionMetricsWorkbookName string = ediTransactionMetricsWorkbook.properties.displayName

output payerIntegrationHealthWorkbookId string = payerIntegrationHealthWorkbook.id
output payerIntegrationHealthWorkbookName string = payerIntegrationHealthWorkbook.properties.displayName

output hipaaComplianceWorkbookId string = hipaaComplianceWorkbook.id
output hipaaComplianceWorkbookName string = hipaaComplianceWorkbook.properties.displayName

output cms0057fComplianceWorkbookId string = cms0057fComplianceWorkbook.id
output cms0057fComplianceWorkbookName string = cms0057fComplianceWorkbook.properties.displayName

output workbookResourceGroup string = resourceGroup().name
output workbookLocation string = location

// Workbook URLs for direct access
output ediTransactionMetricsUrl string = 'https://portal.azure.com/#@/resource${ediTransactionMetricsWorkbook.id}/workbook'
output payerIntegrationHealthUrl string = 'https://portal.azure.com/#@/resource${payerIntegrationHealthWorkbook.id}/workbook'
output hipaaComplianceUrl string = 'https://portal.azure.com/#@/resource${hipaaComplianceWorkbook.id}/workbook'
output cms0057fComplianceUrl string = 'https://portal.azure.com/#@/resource${cms0057fComplianceWorkbook.id}/workbook'
