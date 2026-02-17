# Configure Trading Partners for HIPAA X12 275/277 EDI Processing
# Clearinghouse (sender) → Health Plan (Health Plan) / claims backend (receiver)

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    
    [Parameter(Mandatory=$false)]
    [string]$IntegrationAccountName = "hipaa-attachments-ia",
    
    [Parameter(Mandatory=$false)]
    [string]$ClearinghouseID = "030240928",
    
    [Parameter(Mandatory=$false)]
    [string]$HPID = "{config.payerId}"
)

Write-Host "🏥 Configuring HIPAA Trading Partners for X12 275/277 Processing" -ForegroundColor Cyan
Write-Host ""
Write-Host "📋 Configuration Details:" -ForegroundColor White
Write-Host "  • Sender: Clearinghouse (ID: $ClearinghouseID)" -ForegroundColor Green
Write-Host "  • Receiver: Health Plan - claims backend (ID: $HPID)" -ForegroundColor Green
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

# 1. Create Clearinghouse Trading Partner (Sender)
Write-Host "  Creating clearinghouse partner..." -ForegroundColor White

$clearinghousePartner = @{
    name = "Clearinghouse"
    properties = @{
        partnerType = "B2B"
        metadata = @{
            description = "Clearinghouse - EDI clearinghouse for healthcare transactions"
            role = "Sender"
            messageTypes = "X12 275 (Attachment Request)"
        }
        content = @{
            b2b = @{
                businessIdentities = @(
                    @{
                        qualifier = "ZZ"  # ZZ = Mutually Defined (X12)
                        value = $ClearinghouseID
                    }
                )
            }
        }
    }
} | ConvertTo-Json -Depth 10 -Compress

$clearinghousePartner | Out-File -FilePath "temp-clearinghouse-partner.json" -Encoding UTF8

try {
    az logic integration-account partner create `
        --resource-group $ResourceGroup `
        --integration-account $IntegrationAccountName `
        --name "Clearinghouse" `
        --partner-content "@temp-clearinghouse-partner.json" | Out-Null
    
    Write-Host "    ✅ clearinghouse partner created (ID: $ClearinghouseID)" -ForegroundColor Green
}
catch {
    Write-Host "    ❌ Failed to create clearinghouse partner: $_" -ForegroundColor Red
}

# 2. Create Health Plan Backend Trading Partner (Receiver)
Write-Host "  Creating Health Plan Backend partner..." -ForegroundColor White

$payerPartner = @{
    name = "Health Plan Backend"
    properties = @{
        partnerType = "B2B"
        metadata = @{
            description = "Health Plan - claims backend Backend System"
            role = "Receiver"
            messageTypes = "X12 277 (Response to Attachment Request)"
            system = "claims backend"
        }
        content = @{
            b2b = @{
                businessIdentities = @(
                    @{
                        qualifier = "ZZ"  # ZZ = Mutually Defined (X12)
                        value = $HPID
                    }
                )
            }
        }
    }
} | ConvertTo-Json -Depth 10 -Compress

$payerPartner | Out-File -FilePath "temp-payer-partner.json" -Encoding UTF8

try {
    az logic integration-account partner create `
        --resource-group $ResourceGroup `
        --integration-account $IntegrationAccountName `
        --name "Health Plan Backend" `
        --partner-content "@temp-payer-partner.json" | Out-Null
    
    Write-Host "    ✅ Health Plan Backend partner created (ID: $HID)" -ForegroundColor Green
}
catch {
    Write-Host "    ❌ Failed to create Health Plan Backend partner: $_" -ForegroundColor Red
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

Write-Host "1️⃣ X12 275 RECEIVE Agreement (Clearinghouse → Health Plan):" -ForegroundColor Yellow
Write-Host "   • Name: 'Clearinghouse-to-Health Plan-275-Receive'" -ForegroundColor White
Write-Host "   • Host Partner: Health Plan Backend" -ForegroundColor White
Write-Host "   • Guest Partner: Clearinghouse" -ForegroundColor White
Write-Host "   • Message Type: X12 275 (Attachment Request)" -ForegroundColor White
Write-Host "   • Direction: Receive (Inbound from the clearinghouse)" -ForegroundColor White
Write-Host ""

Write-Host "2️⃣ X12 277 SEND Agreement (Health Plan → Clearinghouse):" -ForegroundColor Yellow
Write-Host "   • Name: 'Health Plan-to-Clearinghouse-277-Send'" -ForegroundColor White
Write-Host "   • Host Partner: Health Plan Backend" -ForegroundColor White
Write-Host "   • Guest Partner: Clearinghouse" -ForegroundColor White
Write-Host "   • Message Type: X12 277 (Response)" -ForegroundColor White
Write-Host "   • Direction: Send (Outbound to the clearinghouse)" -ForegroundColor White
Write-Host ""

Write-Host "🌐 Configure agreements in Azure Portal:" -ForegroundColor Cyan
Write-Host "https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroup/providers/Microsoft.Logic/integrationAccounts/$IntegrationAccountName/agreements" -ForegroundColor Blue

Write-Host ""
Write-Host "💡 Key Configuration Notes:" -ForegroundColor Yellow
Write-Host "   • Use ZZ qualifier for both partners (mutually defined)" -ForegroundColor White
Write-Host "   • 275 messages contain attachment requests from the clearinghouse" -ForegroundColor White
Write-Host "   • 277 messages contain responses back to the clearinghouse" -ForegroundColor White
Write-Host "   • claims backend system processes the attachments behind Health Plan" -ForegroundColor White

# Clean up temporary files
Remove-Item "temp-clearinghouse-partner.json" -ErrorAction SilentlyContinue
Remove-Item "temp-payer-partner.json" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "✅ Trading Partners configuration complete!" -ForegroundColor Green
Write-Host "🔗 Now proceed to create X12 agreements in the Azure Portal" -ForegroundColor Cyan