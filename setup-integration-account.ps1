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
    [string]$IntegrationAccountName = "hipaa-attachments-ia-wus"
)

Write-Host "🔧 Integration Account Setup for HIPAA Processing" -ForegroundColor Cyan
Write-Host ""

if ($DeleteExisting) {
    Write-Host "⚠️ Deleting existing Integration Account: $IntegrationAccountName" -ForegroundColor Yellow
    try {
        az logic integration-account delete --name $IntegrationAccountName --resource-group $ResourceGroup --yes
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

# Clearinghouse Partner
$clearinghouseContent = @{
    b2b = @{
        businessIdentities = @(
            @{
                qualifier = "ZZ"
                value = "030240928"
            }
        )
    }
} | ConvertTo-Json -Depth 10

az logic integration-account partner create `
    --resource-group $ResourceGroup `
    --integration-account $IntegrationAccountName `
    --name "Clearinghouse" `
    --partner-type "B2B" `
    --content $clearinghouseContent

# Your Organization Partner
$orgContent = @{
    b2b = @{
        businessIdentities = @(
            @{
                qualifier = "ZZ"
                value = "{config.payerId}"
            }
        )
    }
} | ConvertTo-Json -Depth 10

az logic integration-account partner create `
    --resource-group $ResourceGroup `
    --integration-account $IntegrationAccountName `
    --name "Health Plan" `
    --partner-type "B2B" `
    --content $orgContent

Write-Host "✅ Trading Partners created" -ForegroundColor Green

# Note: X12 Agreements are complex to create via CLI
# They will need to be created in Azure Portal or via ARM template

Write-Host ""
Write-Host "📋 Next Steps:" -ForegroundColor Yellow
Write-Host "1. ✅ Integration Account created: $IntegrationAccountName" -ForegroundColor White
Write-Host "2. ✅ Trading Partners configured: Clearinghouse (030240928), YourOrganization ({config.payerId})" -ForegroundColor White
Write-Host "3. 🔧 Create X12 Agreements in Azure Portal:" -ForegroundColor White
Write-Host "   - X12 275 Receive Agreement (Clearinghouse -> You)" -ForegroundColor White
Write-Host "   - X12 277 Send Agreement (You -> Clearinghouse)" -ForegroundColor White
Write-Host "4. 🔗 Update Logic Apps to reference new Integration Account" -ForegroundColor White

Write-Host ""
Write-Host "🌐 Integration Account URL:" -ForegroundColor Cyan
Write-Host "https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroup/providers/Microsoft.Logic/integrationAccounts/$IntegrationAccountName" -ForegroundColor Blue

Write-Host ""
Write-Host "🎯 Integration Account setup complete!" -ForegroundColor Green


#$SchemaName = "Companion_275AttachmentEnvelope"
#$SchemaType = "Xml"
#$SchemaFile = "Companion_275AttachmentEnvelope.xsd"

#az logic integration-account schema create `
#    --resource-group $ResourceGroup `
#    --integration-account-name $IntegrationAccountName `
#    --name $SchemaName `
#    --schema-type $SchemaType `
#    --content @$SchemaFile