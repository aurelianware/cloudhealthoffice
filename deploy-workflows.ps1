# Deploy Logic App Standard workflows
# This script uploads the workflow definitions to the deployed Logic App Standard

param(
    [Parameter(Mandatory=$true)]
    [string]$ResourceGroupName,
    
    [Parameter(Mandatory=$true)]
    [string]$LogicAppName
)

Write-Host "Deploying Logic App workflows to $LogicAppName..."

# Create a temporary directory for deployment
$tempDir = "$PSScriptRoot/logicapp-deploy-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
#$tempDir = "$env:TEMP\logicapp-deploy-$(Get-Date -Format 'yyyyMMdd-HHmmss')"


try {
    # Copy the workflows folder structure
    $sourceFolder = ".\logicapps"
    $destFolder = "$tempDir\workflows"
    
    Write-Host "Copying workflow files to temp directory..."
    Copy-Item -Path $sourceFolder -Destination $tempDir -Recurse -Force
    
    # Create host.json if it doesn't exist (required for Logic Apps Standard)
    $hostJsonPath = "$tempDir\host.json"
    if (!(Test-Path $hostJsonPath)) {
        $hostJson = @{
            version = "2.0"
            extensionBundle = @{
                id = "Microsoft.Azure.Functions.ExtensionBundle.Workflows"
                version = "[1.*, 2.0.0)"
            }
        }
        $hostJson | ConvertTo-Json -Depth 10 | Set-Content -Path $hostJsonPath
        Write-Host "Created host.json"
    }
    
    # Create local.settings.json for Logic Apps Standard
    $localSettingsPath = "$tempDir\local.settings.json"
    $localSettings = @{
        IsEncrypted = $false
        Values = @{
            AzureWebJobsStorage = "UseDevelopmentStorage=true"
            FUNCTIONS_WORKER_RUNTIME = "node"
        }
    }
    $localSettings | ConvertTo-Json -Depth 10 | Set-Content -Path $localSettingsPath
    Write-Host "Created local.settings.json"
    
    # Create ZIP file for deployment
    $zipPath = "$tempDir\deployment.zip"
    Write-Host "Creating deployment package..."
    
    # Use PowerShell's Compress-Archive
    $zipPath = "$tempDir/deployment.zip"
    Compress-Archive -Path "$tempDir/*" -DestinationPath $zipPath -Force
    
    if (-Not (Test-Path $zipPath)) {
        Write-Error "❌ ZIP file not found: $zipPath"
        exit 1
    }
    Write-Host "Uploading to Logic App Standard..."
    
    # Deploy using Azure CLI
    az webapp deploy --resource-group $ResourceGroupName --name $LogicAppName --src-path $zipPath --type zip       
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "✅ Successfully deployed workflows to Logic App: $LogicAppName" -ForegroundColor Green
        Write-Host "🌐 Logic App URL: https://$LogicAppName.azurewebsites.net" -ForegroundColor Cyan
    } else {
        Write-Error "❌ Failed to deploy workflows"
        exit 1
    }
    
} finally {
    # Clean up temporary directory
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force
        Write-Host "Cleaned up temporary files"
    }
}

Write-Host ""
Write-Host "Deployment Summary:" -ForegroundColor Yellow
Write-Host "- Resource Group: $ResourceGroupName" -ForegroundColor White
Write-Host "- Logic App Name: $LogicAppName" -ForegroundColor White
Write-Host "- Workflows deployed: ingest275, rfai277" -ForegroundColor White
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "1. Configure API connections (SFTP, Service Bus) in the Azure portal" -ForegroundColor White
Write-Host "2. Set up workflow parameters and environment variables" -ForegroundColor White
Write-Host "3. Test the workflows with sample data" -ForegroundColor White