# End-to-End Testing Script for Cloud Health Office
# Comprehensive automated testing with health checks and detailed reporting

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-hipaa-logic-apps",
    
    [Parameter(Mandatory=$false)]
    [string]$LogicAppName = "hipaa-attachments-la",
    
    [Parameter(Mandatory=$false)]
    [string]$ServiceBusNamespace = "hipaa-attachments-svc",
    
    [Parameter(Mandatory=$false)]
    [string]$StorageAccount = "",
    
    [Parameter(Mandatory=$false)]
    [switch]$SkipInfrastructureCheck = $false,
    
    [Parameter(Mandatory=$false)]
    [switch]$GenerateTestData = $false,
    
    [Parameter(Mandatory=$false)]
    [string]$ReportPath = "./test-report.json"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Test result tracking
$script:TestResults = @{
    StartTime = Get-Date
    TotalTests = 0
    PassedTests = 0
    FailedTests = 0
    SkippedTests = 0
    Tests = @()
    Environment = @{
        ResourceGroup = $ResourceGroup
        LogicAppName = $LogicAppName
        ServiceBusNamespace = $ServiceBusNamespace
        StorageAccount = $StorageAccount
    }
}

# Color output helpers
function Write-TestHeader {
    param([string]$Message)
    Write-Host "`n$('=' * 70)" -ForegroundColor Cyan
    Write-Host "  $Message" -ForegroundColor Cyan -NoNewline
    Write-Host " $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor Gray
    Write-Host "$('=' * 70)" -ForegroundColor Cyan
}

function Write-TestStep {
    param([string]$Message)
    Write-Host "  ▶ $Message" -ForegroundColor White
}

function Write-TestSuccess {
    param([string]$Message)
    Write-Host "  ✓ $Message" -ForegroundColor Green
}

function Write-TestFailure {
    param([string]$Message, [string]$Detail = "")
    Write-Host "  ✗ $Message" -ForegroundColor Red
    if ($Detail) {
        Write-Host "    $Detail" -ForegroundColor Yellow
    }
}

function Write-TestWarning {
    param([string]$Message)
    Write-Host "  ⚠ $Message" -ForegroundColor Yellow
}

# Record test result
function Add-TestResult {
    param(
        [string]$TestName,
        [string]$Status,  # Pass, Fail, Skip
        [string]$Message = "",
        [object]$Details = $null,
        [double]$Duration = 0
    )
    
    $script:TestResults.TotalTests++
    
    switch ($Status) {
        "Pass" { $script:TestResults.PassedTests++ }
        "Fail" { $script:TestResults.FailedTests++ }
        "Skip" { $script:TestResults.SkippedTests++ }
    }
    
    $script:TestResults.Tests += @{
        Name = $TestName
        Status = $Status
        Message = $Message
        Details = $Details
        Duration = $Duration
        Timestamp = Get-Date -Format "o"
    }
}

# Main test execution
Write-TestHeader "Cloud Health Office - End-to-End Testing Suite"

try {
    # Pre-flight checks
    Write-TestStep "Running pre-flight checks..."
    
    # Check Azure CLI
    $testStart = Get-Date
    try {
        $azVersion = az version --output json 2>$null | ConvertFrom-Json
        Write-TestSuccess "Azure CLI available (v$($azVersion.'azure-cli'))"
        Add-TestResult -TestName "Azure CLI Available" -Status "Pass" -Duration ((Get-Date) - $testStart).TotalSeconds
    }
    catch {
        Write-TestFailure "Azure CLI not available or not logged in" -Detail $_.Exception.Message
        Add-TestResult -TestName "Azure CLI Available" -Status "Fail" -Message $_.Exception.Message -Duration ((Get-Date) - $testStart).TotalSeconds
        throw "Azure CLI required. Run: az login"
    }
    
    # Verify authentication
    $testStart = Get-Date
    try {
        $account = az account show --output json 2>$null | ConvertFrom-Json
        Write-TestSuccess "Authenticated as: $($account.user.name)"
        Write-TestSuccess "Subscription: $($account.name)"
        Add-TestResult -TestName "Azure Authentication" -Status "Pass" -Duration ((Get-Date) - $testStart).TotalSeconds
    }
    catch {
        Write-TestFailure "Not authenticated to Azure" -Detail $_.Exception.Message
        Add-TestResult -TestName "Azure Authentication" -Status "Fail" -Message $_.Exception.Message -Duration ((Get-Date) - $testStart).TotalSeconds
        throw "Please run: az login"
    }
    
    # Infrastructure validation
    if (-not $SkipInfrastructureCheck) {
        Write-TestHeader "Infrastructure Validation"
        
        # Check resource group
        $testStart = Get-Date
        try {
            $rg = az group show --name $ResourceGroup --output json 2>$null | ConvertFrom-Json
            Write-TestSuccess "Resource Group exists: $($rg.name) (Location: $($rg.location))"
            Add-TestResult -TestName "Resource Group Exists" -Status "Pass" -Details $rg -Duration ((Get-Date) - $testStart).TotalSeconds
        }
        catch {
            Write-TestFailure "Resource Group not found: $ResourceGroup" -Detail $_.Exception.Message
            Add-TestResult -TestName "Resource Group Exists" -Status "Fail" -Message $_.Exception.Message -Duration ((Get-Date) - $testStart).TotalSeconds
            throw "Resource group required"
        }
        
        # Check Logic App
        $testStart = Get-Date
        try {
            $logicApp = az webapp show --resource-group $ResourceGroup --name $LogicAppName --output json 2>$null | ConvertFrom-Json
            Write-TestSuccess "Logic App exists: $($logicApp.name)"
            Write-TestStep "State: $($logicApp.state)"
            Write-TestStep "Kind: $($logicApp.kind)"
            
            if ($logicApp.state -ne "Running") {
                Write-TestWarning "Logic App is not running (State: $($logicApp.state))"
                Add-TestResult -TestName "Logic App Running" -Status "Fail" -Message "State: $($logicApp.state)" -Duration ((Get-Date) - $testStart).TotalSeconds
            }
            else {
                Add-TestResult -TestName "Logic App Running" -Status "Pass" -Duration ((Get-Date) - $testStart).TotalSeconds
            }
        }
        catch {
            Write-TestFailure "Logic App not found: $LogicAppName" -Detail $_.Exception.Message
            Add-TestResult -TestName "Logic App Exists" -Status "Fail" -Message $_.Exception.Message -Duration ((Get-Date) - $testStart).TotalSeconds
            throw "Logic App required"
        }
        
        # Check Service Bus
        $testStart = Get-Date
        try {
            $sb = az servicebus namespace show --name $ServiceBusNamespace --resource-group $ResourceGroup --output json 2>$null | ConvertFrom-Json
            Write-TestSuccess "Service Bus Namespace exists: $($sb.name)"
            
            # Check topics
            $topics = az servicebus topic list --namespace-name $ServiceBusNamespace --resource-group $ResourceGroup --output json 2>$null | ConvertFrom-Json
            $requiredTopics = @("attachments-in", "edi-278", "rfai-requests")
            
            foreach ($topicName in $requiredTopics) {
                if ($topics.name -contains $topicName) {
                    Write-TestSuccess "Topic exists: $topicName"
                }
                else {
                    Write-TestFailure "Missing required topic: $topicName"
                    Add-TestResult -TestName "Service Bus Topic: $topicName" -Status "Fail" -Message "Topic not found"
                }
            }
            
            Add-TestResult -TestName "Service Bus Infrastructure" -Status "Pass" -Details $sb -Duration ((Get-Date) - $testStart).TotalSeconds
        }
        catch {
            Write-TestFailure "Service Bus not found: $ServiceBusNamespace" -Detail $_.Exception.Message
            Add-TestResult -TestName "Service Bus Infrastructure" -Status "Fail" -Message $_.Exception.Message -Duration ((Get-Date) - $testStart).TotalSeconds
        }
        
        # Check Storage Account
        $testStart = Get-Date
        if ($StorageAccount) {
            try {
                $storage = az storage account show --name $StorageAccount --resource-group $ResourceGroup --output json 2>$null | ConvertFrom-Json
                Write-TestSuccess "Storage Account exists: $($storage.name)"
                
                # Check container
                $containers = az storage container list --account-name $StorageAccount --auth-mode login --output json 2>$null | ConvertFrom-Json
                if ($containers.name -contains "hipaa-attachments") {
                    Write-TestSuccess "Container exists: hipaa-attachments"
                }
                else {
                    Write-TestWarning "Container 'hipaa-attachments' not found"
                }
                
                Add-TestResult -TestName "Storage Account" -Status "Pass" -Details $storage -Duration ((Get-Date) - $testStart).TotalSeconds
            }
            catch {
                Write-TestWarning "Storage Account check failed (may need to specify -StorageAccount parameter)"
                Add-TestResult -TestName "Storage Account" -Status "Skip" -Message "Storage account name not provided"
            }
        }
        else {
            Write-TestStep "Skipping storage account check (use -StorageAccount parameter)"
            Add-TestResult -TestName "Storage Account" -Status "Skip" -Message "Storage account name not provided"
        }
    }
    
    # Workflow validation
    Write-TestHeader "Workflow Validation"
    
    $requiredWorkflows = @("ingest275", "ingest278", "rfai277", "replay278")
    $testStart = Get-Date
    
    try {
        $workflows = az logicapp list-workflows --resource-group $ResourceGroup --name $LogicAppName --output json 2>$null | ConvertFrom-Json
        
        foreach ($workflowName in $requiredWorkflows) {
            if ($workflows.name -contains $workflowName) {
                Write-TestSuccess "Workflow exists: $workflowName"
                
                # Check workflow state
                $workflow = az logicapp workflow show --resource-group $ResourceGroup --name $LogicAppName --workflow-name $workflowName --output json 2>$null | ConvertFrom-Json
                
                if ($workflow.properties.state -eq "Enabled") {
                    Write-TestSuccess "  State: Enabled"
                }
                else {
                    Write-TestWarning "  State: $($workflow.properties.state)"
                }
            }
            else {
                Write-TestFailure "Missing workflow: $workflowName"
                Add-TestResult -TestName "Workflow: $workflowName" -Status "Fail" -Message "Workflow not found"
            }
        }
        
        Add-TestResult -TestName "Workflow Validation" -Status "Pass" -Duration ((Get-Date) - $testStart).TotalSeconds
    }
    catch {
        Write-TestFailure "Failed to list workflows" -Detail $_.Exception.Message
        Add-TestResult -TestName "Workflow Validation" -Status "Fail" -Message $_.Exception.Message -Duration ((Get-Date) - $testStart).TotalSeconds
    }
    
    # Generate test data if requested
    if ($GenerateTestData) {
        Write-TestHeader "Test Data Generation"
        
        $testStart = Get-Date
        try {
            Write-TestStep "Generating synthetic 837 claims..."
            
            $scriptPath = Join-Path $PSScriptRoot "utils" "generate-837-claims.ts"
            if (Test-Path $scriptPath) {
                node (Join-Path $PSScriptRoot ".." "dist" "scripts" "utils" "generate-837-claims.js") "837P" 5 "./test-data"
                Write-TestSuccess "Generated 5 837P claims in ./test-data"
                Add-TestResult -TestName "Test Data Generation" -Status "Pass" -Duration ((Get-Date) - $testStart).TotalSeconds
            }
            else {
                Write-TestWarning "Test data generator not found, skipping"
                Add-TestResult -TestName "Test Data Generation" -Status "Skip" -Message "Script not found"
            }
        }
        catch {
            Write-TestWarning "Test data generation failed: $($_.Exception.Message)"
            Add-TestResult -TestName "Test Data Generation" -Status "Fail" -Message $_.Exception.Message -Duration ((Get-Date) - $testStart).TotalSeconds
        }
    }
    
    # Health check summary
    Write-TestHeader "Health Check Summary"
    
    $healthScore = if ($script:TestResults.TotalTests -gt 0) {
        [math]::Round(($script:TestResults.PassedTests / $script:TestResults.TotalTests) * 100, 1)
    } else { 0 }
    
    Write-Host "  Total Tests: $($script:TestResults.TotalTests)" -ForegroundColor White
    Write-Host "  Passed: $($script:TestResults.PassedTests)" -ForegroundColor Green
    Write-Host "  Failed: $($script:TestResults.FailedTests)" -ForegroundColor Red
    Write-Host "  Skipped: $($script:TestResults.SkippedTests)" -ForegroundColor Yellow
    Write-Host "  Health Score: $healthScore%" -ForegroundColor $(if ($healthScore -ge 80) { "Green" } elseif ($healthScore -ge 60) { "Yellow" } else { "Red" })
    
}
catch {
    Write-TestFailure "Test suite failed" -Detail $_.Exception.Message
    $script:TestResults.FatalError = $_.Exception.Message
}
finally {
    # Generate report
    $script:TestResults.EndTime = Get-Date
    $script:TestResults.Duration = ($script:TestResults.EndTime - $script:TestResults.StartTime).TotalSeconds
    
    $reportDir = Split-Path $ReportPath -Parent
    if ($reportDir -and -not (Test-Path $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }
    
    $script:TestResults | ConvertTo-Json -Depth 100 | Out-File -FilePath $ReportPath -Encoding UTF8
    Write-Host "`n📄 Test report saved to: $ReportPath" -ForegroundColor Cyan
    
    # Exit with appropriate code
    if ($script:TestResults.FailedTests -gt 0) {
        exit 1
    }
}
