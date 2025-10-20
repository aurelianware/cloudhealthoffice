# Integration Account Setup Script for HIPAA Processing (Free Tier)
# Since Free tier only allows 1 Integration Account per subscription per region,
# you'll need to delete the existing 'dev-integration' account first

param(
    [Parameter(Mandatory=$false)]
    [switch]$DeleteExisting = $false,
    
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    
    [Parameter(Mandatory=$false)]
    [string]$Location = "westus",
    
    [Parameter(Mandatory=$false)]
    [string]$IntegrationAccountName = "hipaa-attachments-ia"
)

Write-Host "🔧 Integration Account Setup for HIPAA Processing" -ForegroundColor Cyan
Write-Host ""

if ($DeleteExisting) {
    Write-Host "⚠️ Deleting existing Integration Account: dev-integration" -ForegroundColor Yellow
    try {
        az logic integration-account delete --name "dev-integration" --resource-group "development" --yes
        Write-Host "✅ Existing Integration Account deleted" -ForegroundColor Green
    }
    catch {
        Write-Host "❌ Failed to delete existing Integration Account: $_" -ForegroundColor Red
        Write-Host "ℹ️ You may need to delete it manually in the Azure Portal" -ForegroundColor Yellow
    }
}

Write-Host "🏗️ Creating new Integration Account: $IntegrationAccountName" -ForegroundColor Cyan

# Create Integration Account
Write-Host "Creating Integration Account..." -ForegroundColor White
az logic integration-account create `
    --name $IntegrationAccountName `
    --resource-group $ResourceGroup `
    --location $Location `
    --sku "Free"

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Failed to create Integration Account" -ForegroundColor Red
    exit 1
}

Write-Host "✅ Integration Account created successfully" -ForegroundColor Green

# Create Trading Partners
Write-Host "🤝 Creating Trading Partners..." -ForegroundColor Cyan

# Availity Partner
$availityPartner = @{
    name = "Availity"
    properties = @{
        partnerType = "B2B"
        content = @{
            b2b = @{
                businessIdentities = @(
                    @{
                        qualifier = "ZZ"
                        value = "030240928"
                    }
                )
            }
        }
    }
} | ConvertTo-Json -Depth 10

$availityPartner | Out-File -FilePath "availity-partner.json" -Encoding UTF8

az logic integration-account partner create `
    --resource-group $ResourceGroup `
    --integration-account $IntegrationAccountName `
    --name "Availity" `
    --partner-content "@availity-partner.json"

# Your Organization Partner  
$orgPartner = @{
    name = "YourOrganization"
    properties = @{
        partnerType = "B2B"
        content = @{
            b2b = @{
                businessIdentities = @(
                    @{
                        qualifier = "ZZ"
                        value = "66917"
                    }
                )
            }
        }
    }
} | ConvertTo-Json -Depth 10

$orgPartner | Out-File -FilePath "org-partner.json" -Encoding UTF8

az logic integration-account partner create `
    --resource-group $ResourceGroup `
    --integration-account $IntegrationAccountName `
    --name "YourOrganization" `
    --partner-content "@org-partner.json"

Write-Host "✅ Trading Partners created" -ForegroundColor Green

# Note: X12 Agreements are complex to create via CLI
# They will need to be created in Azure Portal or via ARM template

Write-Host ""
Write-Host "📋 Next Steps:" -ForegroundColor Yellow
Write-Host "1. ✅ Integration Account created: $IntegrationAccountName" -ForegroundColor White
Write-Host "2. ✅ Trading Partners configured: Availity (030240928), YourOrganization (66917)" -ForegroundColor White
Write-Host "3. 🔧 Create X12 Agreements in Azure Portal:" -ForegroundColor White
Write-Host "   - X12 275 Receive Agreement (Availity -> You)" -ForegroundColor White
Write-Host "   - X12 277 Send Agreement (You -> Availity)" -ForegroundColor White
Write-Host "4. 🔗 Update Logic Apps to reference new Integration Account" -ForegroundColor White

Write-Host ""
Write-Host "🌐 Integration Account URL:" -ForegroundColor Cyan
Write-Host "https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroup/providers/Microsoft.Logic/integrationAccounts/$IntegrationAccountName" -ForegroundColor Blue

# Clean up temporary files
Remove-Item "availity-partner.json" -ErrorAction SilentlyContinue
Remove-Item "org-partner.json" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "🎯 Integration Account setup complete!" -ForegroundColor Green