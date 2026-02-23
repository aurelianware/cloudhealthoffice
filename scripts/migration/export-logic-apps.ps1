<#
.SYNOPSIS
    Export Logic Apps definitions and generate comparison report for migration

.DESCRIPTION
    This script exports current Azure Logic App workflow definitions,
    identifies custom connectors, and generates a migration comparison report.

.PARAMETER ResourceGroup
    Azure resource group containing the Logic Apps

.PARAMETER LogicAppName
    Name of the Logic App Standard instance

.PARAMETER OutputPath
    Directory to save exported definitions

.EXAMPLE
    ./export-logic-apps.ps1 -ResourceGroup "payer-attachments-rg" -LogicAppName "payer-la" -OutputPath "./export"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,
    
    [Parameter(Mandatory = $true)]
    [string]$LogicAppName,
    
    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "./export"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Cloud Health Office Logic Apps Export Tool ===" -ForegroundColor Cyan
Write-Host "Resource Group: $ResourceGroup"
Write-Host "Logic App: $LogicAppName"
Write-Host "Output Path: $OutputPath"
Write-Host ""

# Create output directory
if (!(Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
    Write-Host "Created output directory: $OutputPath"
}

# Check Azure CLI
try {
    $azVersion = az version --output json | ConvertFrom-Json
    Write-Host "Azure CLI version: $($azVersion.'azure-cli')" -ForegroundColor Green
}
catch {
    Write-Error "Azure CLI not installed or not logged in. Run 'az login' first."
    exit 1
}

# Get Logic App details
Write-Host "`nFetching Logic App details..." -ForegroundColor Yellow
$logicApp = az webapp show --resource-group $ResourceGroup --name $LogicAppName --output json | ConvertFrom-Json

if (!$logicApp) {
    Write-Error "Logic App '$LogicAppName' not found in resource group '$ResourceGroup'"
    exit 1
}

Write-Host "Logic App ID: $($logicApp.id)"
Write-Host "State: $($logicApp.state)"
Write-Host "Default Hostname: $($logicApp.defaultHostName)"

# Export Logic App configuration
$configPath = Join-Path $OutputPath "logicapp-config.json"
$logicApp | ConvertTo-Json -Depth 10 | Out-File $configPath
Write-Host "Exported Logic App config to: $configPath"

# List workflows
Write-Host "`nListing workflows..." -ForegroundColor Yellow
$workflowsPath = Join-Path $OutputPath "workflows"
New-Item -ItemType Directory -Path $workflowsPath -Force | Out-Null

# Known workflows to export
$workflows = @("ingest275", "ingest278", "rfai277", "replay278")

foreach ($workflow in $workflows) {
    Write-Host "Exporting workflow: $workflow"
    
    try {
        # Get workflow definition via REST API
        $token = az account get-access-token --query accessToken -o tsv
        $uri = "https://management.azure.com$($logicApp.id)/hostruntime/runtime/webhooks/workflow/api/management/workflows/$workflow`?api-version=2018-11-01"
        
        $headers = @{
            "Authorization" = "Bearer $token"
            "Content-Type" = "application/json"
        }
        
        $workflowDef = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get
        
        $workflowPath = Join-Path $workflowsPath "$workflow.json"
        $workflowDef | ConvertTo-Json -Depth 20 | Out-File $workflowPath
        Write-Host "  Exported to: $workflowPath" -ForegroundColor Green
    }
    catch {
        Write-Warning "  Could not export workflow '$workflow': $_"
    }
}

# Get API connections
Write-Host "`nListing API connections..." -ForegroundColor Yellow
$connections = az resource list --resource-group $ResourceGroup --resource-type "Microsoft.Web/connections" --output json | ConvertFrom-Json

$connectionsPath = Join-Path $OutputPath "connections.json"
$connections | ConvertTo-Json -Depth 10 | Out-File $connectionsPath
Write-Host "Exported $($connections.Count) connections to: $connectionsPath"

# Identify connectors
Write-Host "`nIdentified connectors:" -ForegroundColor Yellow
$connectorTypes = $connections | ForEach-Object { $_.name }
$connectorTypes | ForEach-Object { Write-Host "  - $_" }

# Generate migration mapping report
Write-Host "`nGenerating migration mapping report..." -ForegroundColor Yellow

$report = @"
# Cloud Health Office - Logic Apps Migration Report

Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Resource Group: $ResourceGroup
Logic App: $LogicAppName

## Workflows to Migrate

| Logic App Workflow | Argo Workflow | Status |
|--------------------|---------------|--------|
| ingest275 | x12-275-ingest.yaml | 🔄 Ready |
| ingest278 | x12-278-ingest.yaml | 🔄 Ready |
| rfai277 | x12-277-rfai.yaml | 🔄 Ready |
| replay278 | x12-278-replay.yaml | 🔄 Ready |

## Connectors to Migrate

| Azure Connector | Kubernetes Replacement | Notes |
|-----------------|------------------------|-------|
| sftp-ssh | sftp-fetcher container | Custom paramiko-based |
| azureblob | S3/MinIO via AWS CLI | S3-compatible storage |
| servicebus | kafka-publisher container | Kafka topics |
| integrationaccount | x12-parser container | pyx12 library |
| applicationinsights | Prometheus metrics | OpenTelemetry |

## Configuration Migration

| Logic App Parameter | ConfigMap/Secret | Key |
|--------------------|------------------|-----|
| sftp_inbound_folder | trading-partners-config | sftp-folder |
| blob_raw_folder | kafka-topics-config | raw-275-folder |
| sb_topic | kafka-topics-config | topic-attachments-in |
| backend_base_url | backend-config | api-url |
| CLAIMS_BACKEND_API_TOKEN | claims-backend-api-secret | token |
| x12_sender_id | trading-partners-config | clearinghouse-id |
| x12_receiver_id | trading-partners-config | healthplan-id |

## Action Items

- [ ] Create Kubernetes secrets with actual credentials
- [ ] Configure trading partner IDs in ConfigMap
- [ ] Test SFTP connectivity from cluster
- [ ] Validate Kafka topic creation
- [ ] Run parallel processing tests
- [ ] Compare output parity

## Files Generated

- logicapp-config.json - Logic App resource configuration
- workflows/ - Workflow definitions
- connections.json - API connection resources
"@

$reportPath = Join-Path $OutputPath "migration-report.md"
$report | Out-File $reportPath
Write-Host "Migration report saved to: $reportPath" -ForegroundColor Green

Write-Host "`n=== Export Complete ===" -ForegroundColor Cyan
Write-Host "Review the exported files in: $OutputPath"
Write-Host "Follow the migration steps in: docs/ARGO-MIGRATION-GUIDE.md"
