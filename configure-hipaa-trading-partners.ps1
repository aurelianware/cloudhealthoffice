# Configure Trading Partners for HIPAA X12 275/277 EDI Processing
# Availity (sender) → Parkland Community Health Plan (PCHP) / QNXT (receiver)

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    
    [Parameter(Mandatory=$false)]
    [string]$IntegrationAccountName = "hipaa-attachments-ia",
    
    [Parameter(Mandatory=$false)]
    [string]$AvailityID = "030240928",
    
    [Parameter(Mandatory=$false)]
    [string]$PCHPID = "66917"
)

Write-Host "🏥 Configuring HIPAA Trading Partners for X12 275/277 Processing" -ForegroundColor Cyan
Write-Host ""
Write-Host "📋 Configuration Details:" -ForegroundColor White
Write-Host "  • Sender: Availity (ID: $AvailityID)" -ForegroundColor Green
Write-Host "  • Receiver: Parkland Community Health Plan - QNXT (ID: $PCHPID)" -ForegroundColor Green
Write-Host "  • Message Types: X12 275 (Attachment Request), X12 277 (Response)" -ForegroundColor Blue
Write-Host ""

# Check Azure CLI
try {
    az --version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI not available" }
    Write-Host "✅ Azure CLI available" -ForegroundColor Green
}
catch {
    Write-Host "❌ Azure CLI not found. Please install: winget install Microsoft.AzureCLI" -ForegroundColor Red
    exit 1
}

# Verify Integration Account exists
Write-Host "🔍 Verifying Integration Account..." -ForegroundColor Cyan
try {
    $iaCheck = az logic integration-account show --name $IntegrationAccountName --resource-group $ResourceGroup --query "name" --output tsv 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Integration Account not found"
    }
    Write-Host "✅ Integration Account verified: $iaCheck" -ForegroundColor Green
}
catch {
    Write-Host "❌ Integration Account '$IntegrationAccountName' not found in resource group '$ResourceGroup'" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "🤝 Creating Trading Partners..." -ForegroundColor Cyan

# 1. Create Availity Trading Partner (Sender)
Write-Host "  Creating Availity partner..." -ForegroundColor White

$availityPartner = @{
    name = "Availity"
    properties = @{
        partnerType = "B2B"
        metadata = @{
            description = "Availity - EDI clearinghouse for healthcare transactions"
            role = "Sender"
            messageTypes = "X12 275 (Attachment Request)"
        }
        content = @{
            b2b = @{
                businessIdentities = @(
                    @{
                        qualifier = "ZZ"  # ZZ = Mutually Defined (X12)
                        value = $AvailityID
                    }
                )
            }
        }
    }
} | ConvertTo-Json -Depth 10 -Compress

$availityPartner | Out-File -FilePath "temp-availity-partner.json" -Encoding UTF8

try {
    az logic integration-account partner create `
        --resource-group $ResourceGroup `
        --integration-account $IntegrationAccountName `
        --name "Availity" `
        --partner-content "@temp-availity-partner.json" | Out-Null
    
    Write-Host "    ✅ Availity partner created (ID: $AvailityID)" -ForegroundColor Green
}
catch {
    Write-Host "    ❌ Failed to create Availity partner: $_" -ForegroundColor Red
}

# 2. Create PCHP/QNXT Trading Partner (Receiver)
Write-Host "  Creating PCHP/QNXT partner..." -ForegroundColor White

$pchpPartner = @{
    name = "PCHP-QNXT"
    properties = @{
        partnerType = "B2B"
        metadata = @{
            description = "Parkland Community Health Plan - QNXT Backend System"
            role = "Receiver"
            messageTypes = "X12 277 (Response to Attachment Request)"
            system = "QNXT"
        }
        content = @{
            b2b = @{
                businessIdentities = @(
                    @{
                        qualifier = "ZZ"  # ZZ = Mutually Defined (X12)
                        value = $PCHPID
                    }
                )
            }
        }
    }
} | ConvertTo-Json -Depth 10 -Compress

$pchpPartner | Out-File -FilePath "temp-pchp-partner.json" -Encoding UTF8

try {
    az logic integration-account partner create `
        --resource-group $ResourceGroup `
        --integration-account $IntegrationAccountName `
        --name "PCHP-QNXT" `
        --partner-content "@temp-pchp-partner.json" | Out-Null
    
    Write-Host "    ✅ PCHP-QNXT partner created (ID: $PCHPID)" -ForegroundColor Green
}
catch {
    Write-Host "    ❌ Failed to create PCHP-QNXT partner: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "📋 Trading Partners Configuration Summary:" -ForegroundColor Yellow
Write-Host ""

# Verify partners were created
$partners = az logic integration-account partner list --resource-group $ResourceGroup --integration-account $IntegrationAccountName --query "[].{Name:name, Qualifier:properties.content.b2b.businessIdentities[0].qualifier, Value:properties.content.b2b.businessIdentities[0].value}" --output table

Write-Host $partners

Write-Host ""
Write-Host "🔧 Next Steps - X12 Agreements:" -ForegroundColor Cyan
Write-Host ""
Write-Host "You now need to create TWO X12 agreements in the Azure Portal:" -ForegroundColor White
Write-Host ""

Write-Host "1️⃣ X12 275 RECEIVE Agreement (Availity → PCHP):" -ForegroundColor Yellow
Write-Host "   • Name: 'Availity-to-PCHP-275-Receive'" -ForegroundColor White
Write-Host "   • Host Partner: PCHP-QNXT" -ForegroundColor White
Write-Host "   • Guest Partner: Availity" -ForegroundColor White
Write-Host "   • Message Type: X12 275 (Attachment Request)" -ForegroundColor White
Write-Host "   • Direction: Receive (Inbound from Availity)" -ForegroundColor White
Write-Host ""

Write-Host "2️⃣ X12 277 SEND Agreement (PCHP → Availity):" -ForegroundColor Yellow
Write-Host "   • Name: 'PCHP-to-Availity-277-Send'" -ForegroundColor White
Write-Host "   • Host Partner: PCHP-QNXT" -ForegroundColor White
Write-Host "   • Guest Partner: Availity" -ForegroundColor White
Write-Host "   • Message Type: X12 277 (Response)" -ForegroundColor White
Write-Host "   • Direction: Send (Outbound to Availity)" -ForegroundColor White
Write-Host ""

Write-Host "🌐 Configure agreements in Azure Portal:" -ForegroundColor Cyan
Write-Host "https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroup/providers/Microsoft.Logic/integrationAccounts/$IntegrationAccountName/agreements" -ForegroundColor Blue

Write-Host ""
Write-Host "💡 Key Configuration Notes:" -ForegroundColor Yellow
Write-Host "   • Use ZZ qualifier for both partners (mutually defined)" -ForegroundColor White
Write-Host "   • 275 messages contain attachment requests from Availity" -ForegroundColor White
Write-Host "   • 277 messages contain responses back to Availity" -ForegroundColor White
Write-Host "   • QNXT system processes the attachments behind PCHP" -ForegroundColor White

# Clean up temporary files
Remove-Item "temp-availity-partner.json" -ErrorAction SilentlyContinue
Remove-Item "temp-pchp-partner.json" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "✅ Trading Partners configuration complete!" -ForegroundColor Green
Write-Host "🔗 Now proceed to create X12 agreements in the Azure Portal" -ForegroundColor Cyan