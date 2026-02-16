# Deploy HIPAA Infrastructure with New Integration Account
# Run this AFTER deleting the existing dev-integration account

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroupName = "rg-hipaa-logic-apps",
    
    [Parameter(Mandatory=$false)]
    [string]$TemplateFile = "infra\main.bicep"
)

Write-Host "🚀 Deploying HIPAA Infrastructure with New Integration Account" -ForegroundColor Cyan
Write-Host ""

# Check if the template file exists
if (-not (Test-Path $TemplateFile)) {
    Write-Host "❌ Template file not found: $TemplateFile" -ForegroundColor Red
    exit 1
}

Write-Host "📁 Using template: $TemplateFile" -ForegroundColor White
Write-Host "🎯 Target resource group: $ResourceGroupName" -ForegroundColor White
Write-Host ""

# Check if Azure CLI is available
try {
    $azVersion = az --version 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI not found"
    }
    Write-Host "✅ Azure CLI detected" -ForegroundColor Green
}
catch {
    Write-Host "❌ Azure CLI is not installed or not in PATH" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please install Azure CLI first:" -ForegroundColor Yellow
    Write-Host "  winget install Microsoft.AzureCLI" -ForegroundColor White
    Write-Host ""
    Write-Host "Or use Azure Cloud Shell in the portal:" -ForegroundColor Yellow
    Write-Host "  https://portal.azure.com" -ForegroundColor Blue
    exit 1
}

# Deploy the template
Write-Host "🔨 Starting deployment..." -ForegroundColor Cyan
try {
    az deployment group create `
        --resource-group $ResourceGroupName `
        --template-file $TemplateFile `
        --verbose
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "✅ Deployment completed successfully!" -ForegroundColor Green
        Write-Host ""
        Write-Host "📋 Resources created:" -ForegroundColor Cyan
        Write-Host "  • Integration Account: hipaa-attachments-ia" -ForegroundColor White
        Write-Host "  • Storage Account: (with Data Lake Gen2)" -ForegroundColor White
        Write-Host "  • Service Bus: hipaa-attachments-svc" -ForegroundColor White
        Write-Host "  • Logic App: hipaa-attachments-la" -ForegroundColor White
        Write-Host ""
        Write-Host "🔧 Next Steps:" -ForegroundColor Yellow
        Write-Host "  1. Configure Trading Partners in Integration Account" -ForegroundColor White
        Write-Host "  2. Create X12 Agreements (275 Receive, 277 Send)" -ForegroundColor White
        Write-Host "  3. Test Logic App workflows" -ForegroundColor White
    } else {
        Write-Host ""
        Write-Host "❌ Deployment failed. Check the error messages above." -ForegroundColor Red
    }
}
catch {
    Write-Host ""
    Write-Host "❌ Deployment failed with error: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "🌐 Azure Portal Link:" -ForegroundColor Cyan
Write-Host "https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroupName" -ForegroundColor Blue