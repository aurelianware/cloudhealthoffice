# Test HIPAA X12 275/277 Workflows with Trading Partners
# Availity (030240928) ↔ Health Plan-QNXT ({config.payerId})

param(
    [Parameter(Mandatory=$false)]
    [switch]$TestInbound275 = $false,
    
    [Parameter(Mandatory=$false)]
    [switch]$TestOutbound277 = $false,
    
    [Parameter(Mandatory=$false)]
    [switch]$TestFullWorkflow = $false,
    
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    
    [Parameter(Mandatory=$false)]
    [string]$LogicAppName = "hipaa-attachments-la",
    
    [Parameter(Mandatory=$false)]
    [string]$ServiceBusNamespace = "hipaa-attachments-svc",
    
    [Parameter(Mandatory=$false)]
    [string]$StorageAccount = "hipaa7v2rrsoo6tac2"
)

Write-Host "🧪 HIPAA X12 275/277 Trading Partners Testing" -ForegroundColor Cyan
Write-Host ""
Write-Host "📋 Test Configuration:" -ForegroundColor White
Write-Host "  • Sender: Availity (030240928)" -ForegroundColor Green
Write-Host "  • Receiver: Health Plan-QNXT ({config.payerId})" -ForegroundColor Green
Write-Host "  • Logic App: $LogicAppName" -ForegroundColor Blue
Write-Host "  • Service Bus: $ServiceBusNamespace" -ForegroundColor Blue
Write-Host ""

# Verify Azure CLI and login
try {
    az account show --output none 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "⚠️ Please login to Azure: az login" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "✅ Azure CLI authenticated" -ForegroundColor Green
}
catch {
    Write-Host "❌ Azure CLI not available" -ForegroundColor Red
    exit 1
}

# Function to test Logic App trigger
function Test-LogicAppTrigger {
    param(
        [string]$WorkflowName,
        [string]$TestData
    )
    
    Write-Host "🔍 Testing Logic App workflow: $WorkflowName" -ForegroundColor Cyan
    
    try {
        # Get Logic App workflow run history
        $runs = az logicapp run list --resource-group $ResourceGroup --name $LogicAppName --workflow-name $WorkflowName --query "[0:5].{Name:name, Status:status, StartTime:startTime}" --output table
        
        Write-Host "Recent workflow runs:" -ForegroundColor White
        Write-Host $runs
        
        # Get workflow status
        $workflow = az logicapp workflow show --resource-group $ResourceGroup --name $LogicAppName --workflow-name $WorkflowName --query "{Name:name, State:state, CreatedTime:createdTime}" --output table
        
        Write-Host "Workflow configuration:" -ForegroundColor White
        Write-Host $workflow
        
    }
    catch {
        Write-Host "❌ Failed to get workflow information: $_" -ForegroundColor Red
    }
}

# Function to send test message to Service Bus
function Send-TestMessage {
    param(
        [string]$TopicName,
        [string]$MessageBody,
        [hashtable]$Properties = @{}
    )
    
    Write-Host "📤 Sending test message to Service Bus topic: $TopicName" -ForegroundColor Cyan
    
    try {
        # Create message with properties
        $message = @{
            contentData = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($MessageBody))
            userProperties = $Properties
        } | ConvertTo-Json -Depth 3
        
        # Send message (Note: Azure CLI doesn't directly support Service Bus message sending)
        Write-Host "Message prepared for sending:" -ForegroundColor White
        Write-Host $message -ForegroundColor Gray
        
        Write-Host "⚠️ Manual step required: Send this message via Azure Portal or SDK" -ForegroundColor Yellow
        
    }
    catch {
        Write-Host "❌ Failed to prepare message: $_" -ForegroundColor Red
    }
}

# Test Case 1: X12 275 Inbound Processing
if ($TestInbound275 -or $TestFullWorkflow) {
    Write-Host ""
    Write-Host "🧪 Test Case 1: X12 275 Inbound Processing" -ForegroundColor Yellow
    Write-Host "=============================================" -ForegroundColor Yellow
    
    # Check if test file exists
    if (Test-Path "test-x12-275-availity-to-pchp.edi") {
        $testContent = Get-Content "test-x12-275-availity-to-pchp.edi" -Raw
        Write-Host "✅ Test X12 275 file found" -ForegroundColor Green
        Write-Host "Sample content:" -ForegroundColor White
        Write-Host ($testContent.Substring(0, [Math]::Min(200, $testContent.Length)) + "...") -ForegroundColor Gray
    } else {
        Write-Host "❌ Test X12 275 file not found: test-x12-275-availity-to-pchp.edi" -ForegroundColor Red
    }
    
    # Test ingest275 workflow
    Test-LogicAppTrigger -WorkflowName "ingest275" -TestData $testContent
    
    Write-Host ""
    Write-Host "📋 Verification Steps for 275 Processing:" -ForegroundColor Cyan
    Write-Host "1. Check Data Lake storage for raw file in /raw/275/" -ForegroundColor White
    Write-Host "2. Verify X12 decode operation with trading partners:" -ForegroundColor White
    Write-Host "   • Sender ID: 030240928 (Availity)" -ForegroundColor Green
    Write-Host "   • Receiver ID: {config.payerId} (Health Plan-QNXT)" -ForegroundColor Green
    Write-Host "3. Confirm Service Bus message in attachments-in topic" -ForegroundColor White
    Write-Host "4. Validate metadata extraction:" -ForegroundColor White
    Write-Host "   • Claim Number: CLM20250924001" -ForegroundColor Blue
    Write-Host "   • Member ID: 123456789" -ForegroundColor Blue
    Write-Host "   • RFAI Reference: RFAI20250924001" -ForegroundColor Blue
}

# Test Case 2: X12 277 Outbound Generation  
if ($TestOutbound277 -or $TestFullWorkflow) {
    Write-Host ""
    Write-Host "🧪 Test Case 2: X12 277 Outbound Generation" -ForegroundColor Yellow
    Write-Host "=============================================" -ForegroundColor Yellow
    
    # Check if test payload exists
    if (Test-Path "test-qnxt-response-payload.json") {
        $testPayload = Get-Content "test-qnxt-response-payload.json" -Raw
        Write-Host "✅ Test QNXT response payload found" -ForegroundColor Green
        
        # Parse and display key fields
        $payload = $testPayload | ConvertFrom-Json
        Write-Host "Payload details:" -ForegroundColor White
        Write-Host "  • Claim: $($payload.claimNumber)" -ForegroundColor Blue
        Write-Host "  • Member: $($payload.memberId)" -ForegroundColor Blue
        Write-Host "  • RFAI: $($payload.rfaiReference)" -ForegroundColor Blue
        Write-Host "  • Status: $($payload.status)" -ForegroundColor Blue
    } else {
        Write-Host "❌ Test QNXT payload file not found: test-qnxt-response-payload.json" -ForegroundColor Red
    }
    
    # Test rfai277 workflow
    Test-LogicAppTrigger -WorkflowName "rfai277" -TestData $testPayload
    
    # Simulate sending message to Service Bus
    Send-TestMessage -TopicName "rfai-requests" -MessageBody $testPayload -Properties @{
        "messageType" = "qnxt-response"
        "claimNumber" = "CLM20250924001" 
        "rfaiReference" = "RFAI20250924001"
    }
    
    Write-Host ""
    Write-Host "📋 Verification Steps for 277 Processing:" -ForegroundColor Cyan
    Write-Host "1. Verify rfai277 workflow triggered from Service Bus" -ForegroundColor White
    Write-Host "2. Confirm X12 277 encoding with trading partners:" -ForegroundColor White
    Write-Host "   • Sender ID: {config.payerId} (Health Plan-QNXT)" -ForegroundColor Green
    Write-Host "   • Receiver ID: 030240928 (Availity)" -ForegroundColor Green
    Write-Host "3. Validate 277 message structure and content" -ForegroundColor White
    Write-Host "4. Check outbound transmission to Availity endpoint" -ForegroundColor White
}

# Infrastructure verification
Write-Host ""
Write-Host "🔍 Infrastructure Verification" -ForegroundColor Yellow
Write-Host "==============================" -ForegroundColor Yellow

# Check Logic Apps status
Write-Host "Logic Apps status:" -ForegroundColor Cyan
try {
    $logicAppStatus = az logicapp show --resource-group $ResourceGroup --name $LogicAppName --query "{Name:name, State:state, Location:location}" --output table
    Write-Host $logicAppStatus
}
catch {
    Write-Host "❌ Failed to get Logic App status" -ForegroundColor Red
}

# Check Integration Account
Write-Host ""
Write-Host "Integration Account trading partners:" -ForegroundColor Cyan
try {
    $partners = az logic integration-account partner list --resource-group $ResourceGroup --integration-account-name "hipaa-attachments-ia" --query "[].{Name:name, ID:content.b2b.businessIdentities[0].value, Qualifier:content.b2b.businessIdentities[0].qualifier}" --output table
    Write-Host $partners
}
catch {
    Write-Host "❌ Failed to get trading partners" -ForegroundColor Red
}

# Check Service Bus topics
Write-Host ""
Write-Host "Service Bus topics:" -ForegroundColor Cyan
try {
    $topics = az servicebus topic list --resource-group $ResourceGroup --namespace-name $ServiceBusNamespace --query "[].{Name:name, Status:status, MessageCount:countDetails.activeMessageCount}" --output table
    Write-Host $topics
}
catch {
    Write-Host "❌ Failed to get Service Bus topics" -ForegroundColor Red
}

Write-Host ""
Write-Host "🌐 Azure Portal Links for Monitoring:" -ForegroundColor Cyan
Write-Host "• Logic Apps: https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$LogicAppName" -ForegroundColor Blue
Write-Host "• Integration Account: https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroup/providers/Microsoft.Logic/integrationAccounts/hipaa-attachments-ia" -ForegroundColor Blue
Write-Host "• Service Bus: https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/$ResourceGroup/providers/Microsoft.ServiceBus/namespaces/$ServiceBusNamespace" -ForegroundColor Blue

Write-Host ""
if ($TestFullWorkflow) {
    Write-Host "✅ Full workflow testing completed!" -ForegroundColor Green
} else {
    Write-Host "ℹ️ Run with -TestFullWorkflow to execute all test cases" -ForegroundColor Blue
}

Write-Host ""
Write-Host "🔧 Next Steps:" -ForegroundColor Yellow
Write-Host "1. Create X12 agreements in Integration Account (if not done)" -ForegroundColor White
Write-Host "2. Configure SFTP connection for inbound 275 files" -ForegroundColor White  
Write-Host "3. Set up QNXT API endpoint configuration" -ForegroundColor White
Write-Host "4. Configure outbound 277 transmission to Availity" -ForegroundColor White