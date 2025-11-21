# Troubleshooting Guide

This guide provides solutions to common issues encountered when developing, deploying, and operating the Cloud Health Office system.

## Table of Contents
- [Development Issues](#development-issues)
- [Deployment Issues](#deployment-issues)
- [Runtime Issues](#runtime-issues)
- [Integration Issues](#integration-issues)
- [Performance Issues](#performance-issues)
- [Monitoring and Diagnostics](#monitoring-and-diagnostics)

## Development Issues

### JSON Validation Errors

#### Issue: Invalid JSON Syntax

**Error Message:**
```
jq: parse error: Expected separator between values at line 45, column 3
```

**Cause:** Missing comma, extra comma, or malformed JSON structure

**Solution:**
```bash
# Identify the specific line with error
jq . logicapps/workflows/ingest275/workflow.json

# Common issues to check:
# 1. Missing comma between object properties
# 2. Trailing comma before closing brace }
# 3. Unescaped quotes in strings
# 4. Mismatched brackets [], braces {}, or parentheses ()

# Use online JSON validator for detailed error location
# https://jsonlint.com/
```

**Prevention:**
- Use an editor with JSON syntax highlighting (VS Code, vim with plugins)
- Enable auto-formatting on save
- Run `jq` validation before committing

#### Issue: Missing Required Keys

**Error Message:**
```
ERROR: Missing required keys in workflow.json (definition/kind/parameters)
```

**Cause:** Workflow JSON missing mandatory structure

**Solution:**
```bash
# Verify structure
jq 'has("definition"), has("kind"), has("parameters")' workflow.json

# Expected output: true, true, true

# If false, add missing keys:
{
  "definition": {
    "$schema": "...",
    "actions": {},
    "triggers": {},
    "parameters": {}
  },
  "kind": "Stateful",
  "parameters": {}
}
```

#### Issue: Workflow Kind Incorrect

**Error Message:**
```
WARNING: kind is not Stateful/Stateless
```

**Cause:** Logic Apps Standard requires workflows to be Stateful or Stateless

**Solution:**
```bash
# Check current kind
jq '.kind' workflow.json

# Update to Stateful (required for all current workflows)
jq '.kind = "Stateful"' workflow.json > temp.json && mv temp.json workflow.json

# Verify
jq '.kind' workflow.json
# Output: "Stateful"
```

### Bicep Compilation Errors

#### Issue: Parameter Validation Failure

**Error Message:**
```
ERROR: The value of parameter 'baseName' must be between 3 and 20 characters
```

**Cause:** Parameter value doesn't meet defined constraints

**Solution:**
```bash
# Check parameter constraints in main.bicep
grep -A 5 "param baseName" infra/main.bicep

# Ensure value meets requirements:
# - Length: 3-20 characters
# - Format: alphanumeric and hyphens only
# - No spaces or special characters

# Valid examples:
# - "hipaa-attachments"
# - "payer-uat"
# - "prod-la-01"

# Invalid examples:
# - "hi" (too short)
# - "very-long-base-name-exceeding-twenty-chars" (too long)
# - "test_name" (underscore not allowed)
```

#### Issue: Resource Already Exists

**Error Message:**
```
ERROR: The resource 'hipaa-attachments-la' already exists in location 'eastus'
```

**Cause:** Resource name conflict or attempting to recreate existing resource

**Solution:**
```bash
# Check if resource exists
az webapp show --name "hipaa-attachments-la" --resource-group "rg-name"

# Options:
# 1. Use different baseName parameter
# 2. Delete existing resource (if safe)
# 3. Update existing resource instead of creating new

# Update existing deployment
az deployment group create \
  --resource-group "rg-name" \
  --template-file infra/main.bicep \
  --parameters baseName="hipaa-attachments" \
  --mode Incremental  # Default, updates existing resources
```

#### Issue: Expected Warnings

**Warning Message:**
```
Warning use-parent-property: Use a reference to the parent resource instead of repeating name/type
```

**Cause:** Bicep linter suggestion for Service Bus topics

**Action:** **SAFE TO IGNORE** - This is a known cosmetic warning that doesn't affect deployment

### PowerShell Script Errors

#### Issue: Execution Policy Restriction

**Error Message:**
```
File cannot be loaded because running scripts is disabled on this system
```

**Cause:** PowerShell execution policy blocks script execution

**Solution:**
```powershell
# Check current policy
Get-ExecutionPolicy

# Set policy for current process only (temporary)
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

# Run script
./test-workflows.ps1

# Policy reverts when closing PowerShell session
```

**Alternative:** Run with bypass flag
```bash
pwsh -ExecutionPolicy Bypass -File ./test-workflows.ps1
```

#### Issue: Parameter Validation Error

**Error Message:**
```
Cannot validate argument on parameter 'ResourceGroup'. The argument is null or empty.
```

**Cause:** Required parameter not provided

**Solution:**
```powershell
# Check required parameters
Get-Help ./test-workflows.ps1 -Detailed

# Provide required parameters
pwsh -c "./test-workflows.ps1 -ResourceGroup 'rg-name' -LogicAppName 'la-name'"

# Or set default values in script
```

### Git and Version Control Issues

#### Issue: Merge Conflicts

**Error Message:**
```
CONFLICT (content): Merge conflict in logicapps/workflows/ingest275/workflow.json
```

**Solution:**
```bash
# Check conflict markers
git status
git diff

# Option 1: Keep your changes
git checkout --ours logicapps/workflows/ingest275/workflow.json
git add logicapps/workflows/ingest275/workflow.json

# Option 2: Keep incoming changes
git checkout --theirs logicapps/workflows/ingest275/workflow.json
git add logicapps/workflows/ingest275/workflow.json

# Option 3: Manually resolve
# Edit file to resolve conflicts (remove <<<, ===, >>> markers)
vim logicapps/workflows/ingest275/workflow.json
git add logicapps/workflows/ingest275/workflow.json

# Complete merge
git commit -m "Merge and resolve conflicts"
```

#### Issue: Accidentally Committed Large Files

**Error Message:**
```
remote: error: File workflows.zip is 125.00 MB; this exceeds GitHub's file size limit
```

**Solution:**
```bash
# Remove file from git history
git rm --cached workflows.zip

# Add to .gitignore
echo "workflows.zip" >> .gitignore
git add .gitignore

# Commit changes
git commit -m "Remove large file and update .gitignore"

# If already pushed, use filter-branch or BFG Repo-Cleaner
# (consult with team first as this rewrites history)
```

## Deployment Issues

### OIDC Authentication Failures

#### Issue: Application Not Found

**Error Message:**
```
ERROR: AADSTS700016: Application with identifier 'xxx' was not found in the directory
```

**Cause:** Service principal or federated credential not configured correctly

**Solution:**
```bash
# Verify application exists
APP_ID="<your-app-id>"
az ad app show --id "$APP_ID"

# If not found, create application
az ad app create --display-name "hipaa-attachments-dev-github"

# Create service principal
az ad sp create --id "$APP_ID"

# Verify federated credential
az ad app federated-credential list --id "$APP_ID"

# Expected output should show GitHub repo configuration
```

#### Issue: Invalid Subject Claim

**Error Message:**
```
ERROR: AADSTS700224: The subject claim does not match the expected value
```

**Cause:** Federated credential subject doesn't match GitHub repo/branch

**Solution:**
```bash
# Check current federated credential
az ad app federated-credential list --id "$APP_ID"

# Subject should match:
# - For main branch: "repo:aurelianware/hipaa-attachments:ref:refs/heads/main"
# - For PR: "repo:aurelianware/hipaa-attachments:pull_request"
# - For environment: "repo:aurelianware/hipaa-attachments:environment:production"

# Update federated credential if needed
az ad app federated-credential update \
  --id "$APP_ID" \
  --federated-credential-id "<credential-id>" \
  --parameters "{
    \"name\": \"github-main\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:aurelianware/hipaa-attachments:ref:refs/heads/main\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"
```

### Infrastructure Deployment Failures

#### Issue: Insufficient Quota

**Error Message:**
```
ERROR: Operation could not be completed as it results in exceeding approved quota
```

**Cause:** Subscription has reached resource limits

**Solution:**
```bash
# Check current quota usage
az vm list-usage --location eastus --output table

# Request quota increase through Azure Portal:
# 1. Go to Subscriptions → Usage + quotas
# 2. Select region (eastus)
# 3. Find resource type (e.g., "Standard WS1 App Service Plan")
# 4. Click "Request increase"
# 5. Fill out form with business justification

# Temporary workaround: Use different region or SKU
az deployment group create \
  --resource-group "rg-name" \
  --template-file infra/main.bicep \
  --parameters baseName="hipaa" location="westus"
```

#### Issue: Resource Provider Not Registered

**Error Message:**
```
ERROR: The subscription is not registered to use namespace 'Microsoft.ServiceBus'
```

**Cause:** Required resource provider not enabled

**Solution:**
```bash
# Register resource provider
az provider register --namespace Microsoft.ServiceBus
az provider register --namespace Microsoft.Logic
az provider register --namespace Microsoft.Web

# Check registration status
az provider show --namespace Microsoft.ServiceBus --query "registrationState"

# Wait for "Registered" status (may take a few minutes)
az provider list --query "[?registrationState=='Registering']" --output table
```

### Logic App Deployment Issues

#### Issue: ZIP Deployment Fails

**Error Message:**
```
ERROR: Failed to deploy workflows.zip
```

**Cause:** Incorrect ZIP structure or Logic App not running

**Solution:**
```bash
# Verify ZIP structure
unzip -l workflows.zip | head -20

# Correct structure should show:
# workflows/
#   ingest275/
#     workflow.json
#   ingest278/
#     workflow.json
#   replay278/
#     workflow.json
#   rfai277/
#     workflow.json

# Recreate ZIP with correct structure
cd logicapps
zip -r ../workflows.zip workflows/
cd ..

# Verify Logic App is running
az webapp show \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --query "state"

# Start if stopped
az webapp start \
  --resource-group "rg-name" \
  --name "logic-app-name"

# Retry deployment
az webapp deploy \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --src-path workflows.zip \
  --type zip
```

#### Issue: Workflows Not Appearing

**Symptom:** Workflows deployed but don't show in Logic App

**Solution:**
```bash
# Check deployment logs in Kudu
# Navigate to: https://{logic-app-name}.scm.azurewebsites.net

# Or check via CLI
az webapp log tail \
  --resource-group "rg-name" \
  --name "logic-app-name"

# Verify workflows directory in Logic App
# Use Kudu console to check /home/site/wwwroot/

# Restart Logic App
az webapp restart \
  --resource-group "rg-name" \
  --name "logic-app-name"
```

## Runtime Issues

### Workflow Execution Failures

#### Issue: Workflow Runs Show "Failed"

**Investigation Steps:**
```bash
# View workflow runs in Azure Portal
# Navigate to: Logic App → Workflows → [workflow-name] → Runs

# Check run details:
# 1. Click on failed run
# 2. Review trigger inputs
# 3. Check each action's inputs/outputs
# 4. Look for error messages

# Common failure patterns:
# - Action timeout
# - Authentication failure
# - Invalid input data
# - External service unavailable
```

**Query Application Insights:**
```kusto
// Failed workflow runs in last 24 hours
traces
| where timestamp > ago(24h)
| where severityLevel >= 3
| where message contains "workflow"
| project timestamp, message, severityLevel
| order by timestamp desc

// Specific workflow errors
exceptions
| where timestamp > ago(24h)
| where outerMessage contains "ingest275"
| project timestamp, outerMessage, problemId
```

#### Issue: QNXT API Timeouts

**Error Message in Workflow:**
```
Action 'Call_QNXT_Claim_Linkage_API' failed: The operation has timed out
```

**Solution:**
```bash
# Check QNXT API availability
curl -I https://qnxt-api-uat.example.com/health

# Review retry configuration in workflow
jq '.definition.actions.Call_QNXT_Claim_Linkage_API.inputs.retryPolicy' \
  logicapps/workflows/ingest275/workflow.json

# Increase timeout or retry attempts:
{
  "retryPolicy": {
    "type": "exponential",
    "count": 4,
    "interval": "PT15S",
    "maximumInterval": "PT60S"
  }
}

# Monitor QNXT API performance in Application Insights
```

**Application Insights Query:**
```kusto
dependencies
| where name contains "QNXT"
| summarize 
    avg(duration), 
    percentile(duration, 95),
    failureRate = 100.0 * sum(toint(success == false)) / count()
  by bin(timestamp, 1h)
| render timechart
```

#### Issue: X12 Decode Failures

**Error Message:**
```
X12 message decode failed: Invalid interchange control number
```

**Causes:**
- Trading partner agreement mismatch
- Invalid ISA/GS identifiers
- Schema validation failure

**Solution:**
```bash
# Verify trading partner configuration
# Azure Portal → Integration Account → Partners

# Check agreement settings
# Azure Portal → Integration Account → Agreements

# Verify ISA/GS segments in EDI file
cat test-file.edi | grep "^ISA"
cat test-file.edi | grep "^GS"

# Expected format:
# ISA*00*          *00*          *ZZ*030240928      *ZZ*{config.payerId}          *...
#     ^^                          ^^  ^^^^^^^^^^      ^^  ^^^^^
#     |                           |   |               |   |
#     Auth qualifier              |   Sender ID       |   Receiver ID
#                                 Sender qualifier    Receiver qualifier

# Ensure IDs match Integration Account configuration:
# - Availity: 030240928
# - Health Plan: {config.payerId}
```

**Validate Schema:**
```bash
# Check schema is uploaded
# Azure Portal → Integration Account → Schemas

# Required schemas:
# - X12_005010X210_275 (Attachment Request)
# - X12_005010X212_277 (Status Notification)
# - X12_005010X217_278 (Services Review)
```

### Service Bus Issues

#### Issue: Messages Stuck in Dead-Letter Queue

**Investigation:**
```bash
# List messages in DLQ
az servicebus topic subscription show \
  --resource-group "rg-name" \
  --namespace-name "namespace" \
  --topic-name "attachments-in" \
  --name "\$Default" \
  --query "deadLetterMessageCount"

# Common reasons for DLQ:
# 1. Max delivery count exceeded
# 2. Message expired (TTL)
# 3. Filter evaluation error
# 4. Session lock lost
```

**Solution:**
```bash
# Peek messages in DLQ to see errors
# Use Service Bus Explorer in Azure Portal

# Or use Azure CLI with Service Bus extension
# Install: az extension add --name servicebus

# Common fixes:
# 1. Increase max delivery count
# 2. Extend message TTL
# 3. Fix message format
# 4. Update subscription filters

# Resubmit messages after fixing issue
# Use Service Bus Explorer → Dead-letter → Resubmit
```

#### Issue: Service Bus Connection Failures

**Error Message:**
```
Action failed: Unable to connect to Service Bus namespace
```

**Solution:**
```bash
# Check Service Bus namespace status
az servicebus namespace show \
  --resource-group "rg-name" \
  --name "namespace" \
  --query "{name:name, status:status}"

# Verify managed identity has permissions
PRINCIPAL_ID=$(az webapp identity show \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --query principalId -o tsv)

# Check role assignment
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --scope "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ServiceBus/namespaces/{namespace}"

# Assign role if missing
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Azure Service Bus Data Sender" \
  --scope "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ServiceBus/namespaces/{namespace}"

# Reconnect API connection in Logic App
# Portal → Logic App → API connections → servicebus → Edit API connection
```

### SFTP Integration Issues

#### Issue: SFTP Connection Timeout

**Error Message:**
```
SFTP connection timed out or refused
```

**Solution:**
```bash
# Test SFTP connectivity manually
sftp -o ConnectTimeout=30 user@sftp-host

# Check firewall/NSG rules
# Ensure Logic App can reach SFTP host on port 22

# Verify credentials in API connection
# Portal → Logic App → API connections → sftp-ssh

# Update connection:
# 1. Check hostname is correct
# 2. Verify username
# 3. Re-upload SSH private key
# 4. Test connection

# Check SFTP server logs for authentication failures
```

#### Issue: File Not Found or Path Error

**Error Message:**
```
The specified path does not exist: /inbound/attachments/
```

**Solution:**
```bash
# Verify path exists on SFTP server
sftp user@sftp-host
ls -la /inbound/
ls -la /inbound/attachments/

# Check workflow parameter
jq '.parameters.sftp_inbound_folder.value' \
  logicapps/workflows/ingest275/workflow.json

# Update parameter if needed
# Portal → Logic App → Configuration → Application settings
# Update: sftp_inbound_folder=/correct/path/

# Ensure path includes leading slash
# ✓ Correct: /inbound/attachments
# ✗ Wrong: inbound/attachments
```

### Data Lake Storage Issues

#### Issue: Blob Storage Permission Denied

**Error Message:**
```
Authorization failed: This request is not authorized to perform this operation
```

**Solution:**
```bash
# Check managed identity has Storage Blob Data Contributor role
LOGIC_APP_PRINCIPAL=$(az webapp identity show \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --query principalId -o tsv)

# List current role assignments
az role assignment list \
  --assignee "$LOGIC_APP_PRINCIPAL" \
  --scope "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{storage}"

# Assign role if missing
az role assignment create \
  --assignee "$LOGIC_APP_PRINCIPAL" \
  --role "Storage Blob Data Contributor" \
  --scope "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{storage}"

# Wait 5-10 minutes for permission propagation
# Then retry workflow
```

#### Issue: Container Not Found

**Error Message:**
```
The specified container does not exist: hipaa-attachments
```

**Solution:**
```bash
# List containers
az storage container list \
  --account-name "storageaccount" \
  --auth-mode login

# Create container if missing
az storage container create \
  --name "hipaa-attachments" \
  --account-name "storageaccount" \
  --auth-mode login

# Verify hierarchical namespace is enabled (Data Lake Gen2)
az storage account show \
  --name "storageaccount" \
  --resource-group "rg-name" \
  --query "isHnsEnabled"

# Should return: true
```

## Integration Issues

### QNXT API Integration

#### Issue: Authentication Failures

**Error Message:**
```
401 Unauthorized: Invalid or expired token
```

**Solution:**
```bash
# Verify OAuth token configuration
# Check workflow parameters for token endpoint and credentials

# Test token acquisition manually
curl -X POST https://qnxt-auth.example.com/oauth/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials&client_id=xxx&client_secret=xxx"

# Update Logic App configuration with fresh credentials
# Store client secret in Azure Key Vault (recommended)
```

#### Issue: Invalid Request Data

**Error Message:**
```
400 Bad Request: Missing required field 'claimNumber'
```

**Solution:**
```bash
# Review QNXT API documentation for required fields

# Check workflow action inputs
jq '.definition.actions.Call_QNXT_Claim_Linkage_API.inputs' \
  logicapps/workflows/ingest275/workflow.json

# Verify metadata extraction is working
# Check previous action outputs in workflow run history

# Common required fields:
# - claimNumber
# - memberId
# - providerNpi
# - attachmentReference
# - receivedDate
# - blobPath
```

### Integration Account Issues

#### Issue: Schema Upload Fails

**Error Message:**
```
Schema validation failed: Invalid XSD
```

**Solution:**
```bash
# Validate XSD schema locally
xmllint --schema X12_005010X210_275.xsd --noout X12_005010X210_275.xsd

# Check schema file format:
# - Must be valid XSD
# - Encoding: UTF-8
# - No BOM (Byte Order Mark)

# Re-download schema from:
# - Microsoft HIPAA Accelerator
# - Washington Publishing Company
# - X12.org official schemas

# Upload via Azure CLI
az logic integration-account schema create \
  --resource-group "rg-name" \
  --integration-account-name "ia-name" \
  --schema-name "X12_005010X210_275" \
  --schema-type "Xml" \
  --content @X12_005010X210_275.xsd
```

#### Issue: Agreement Not Found

**Error Message:**
```
No agreement found for sender 030240928 and receiver {config.payerId}
```

**Solution:**
```bash
# List existing agreements
az logic integration-account agreement list \
  --resource-group "rg-name" \
  --integration-account-name "ia-name"

# Verify trading partner IDs
# Portal → Integration Account → Agreements
# Check ISA sender/receiver IDs match EDI file

# Create missing agreement
# See DEPLOYMENT.md for agreement creation steps

# Or use PowerShell script
pwsh -c "./configure-x12-agreements.ps1 -ResourceGroup 'rg' -IntegrationAccountName 'ia'"
```

## Performance Issues

### Slow Workflow Execution

**Symptoms:**
- Workflows taking longer than expected
- Timeouts occurring
- High latency

**Investigation:**
```kusto
// Workflow duration over time
traces
| where message contains "workflow_completed"
| extend duration = todouble(customDimensions["duration"])
| summarize avg(duration), percentile(duration, 95) by bin(timestamp, 1h)
| render timechart

// Identify slow actions
dependencies
| where timestamp > ago(24h)
| summarize avg(duration), percentile(duration, 95) by name
| order by avg_duration desc
```

**Solutions:**
1. **Optimize API Calls:**
   - Cache frequently accessed data
   - Use parallel execution where possible
   - Reduce payload sizes

2. **Increase Logic App SKU:**
   ```bash
   # Upgrade to higher SKU (WS1 → WS2)
   az webapp update \
     --resource-group "rg-name" \
     --name "logic-app-name" \
     --plan "new-app-service-plan"
   ```

3. **Optimize X12 Processing:**
   - Ensure schemas are optimized
   - Consider batch processing for high volumes

### High Service Bus Queue Depth

**Symptoms:**
- Messages piling up in topics
- Processing delays

**Investigation:**
```bash
# Check queue depth
az servicebus topic show \
  --resource-group "rg-name" \
  --namespace-name "namespace" \
  --name "attachments-in" \
  --query "messageCount"

# Monitor over time
watch -n 10 'az servicebus topic show --resource-group "rg" --namespace-name "ns" --name "topic" --query messageCount'
```

**Solutions:**
1. **Scale Logic App:**
   - Increase concurrent runs
   - Add more workflow instances

2. **Optimize Processing:**
   - Reduce per-message processing time
   - Implement batch processing

3. **Check for Bottlenecks:**
   - QNXT API slow response
   - X12 decode taking too long
   - Network latency

## Monitoring and Diagnostics

### Application Insights Queries

#### Failed Requests
```kusto
requests
| where timestamp > ago(24h)
| where success == false
| project timestamp, name, resultCode, duration, operation_Id
| order by timestamp desc
```

#### Exception Analysis
```kusto
exceptions
| where timestamp > ago(24h)
| summarize count() by type, outerMessage
| order by count_ desc
```

#### Performance Metrics
```kusto
dependencies
| where timestamp > ago(24h)
| summarize 
    avg(duration),
    percentile(duration, 50),
    percentile(duration, 95),
    percentile(duration, 99)
  by name
| order by avg_duration desc
```

#### Custom Events
```kusto
customEvents
| where timestamp > ago(24h)
| where name in ("workflow_started", "workflow_completed", "x12_decoded")
| summarize count() by name, bin(timestamp, 1h)
| render timechart
```

### Health Checks

#### Quick Health Check Script
```bash
#!/bin/bash
RG="payer-attachments-uat-rg"
LA="hipaa-attachments-uat-la"

echo "=== Health Check ==="

# Logic App Status
echo -n "Logic App Status: "
az webapp show --resource-group "$RG" --name "$LA" --query "state" -o tsv

# Workflow List
echo -n "Workflows Count: "
az rest --method get \
  --uri "https://management.azure.com/subscriptions/{sub}/resourceGroups/$RG/providers/Microsoft.Web/sites/$LA/workflows?api-version=2022-03-01" \
  --query "value | length(@)"

# Service Bus Topics
echo "Service Bus Topics:"
az servicebus topic list \
  --resource-group "$RG" \
  --namespace-name "${LA%-la}-svc" \
  --query "[].{name:name, messageCount:messageCount}" \
  --output table

# API Connections
echo "API Connection Status:"
az resource list \
  --resource-group "$RG" \
  --resource-type "Microsoft.Web/connections" \
  --query "[].{name:name, status:properties.statuses[0].status}" \
  --output table

echo "=== Health Check Complete ==="
```

### Enable Detailed Logging

```bash
# Enable verbose logging
az webapp log config \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --application-logging true \
  --level verbose \
  --web-server-logging filesystem

# Stream logs in real-time
az webapp log tail \
  --resource-group "rg-name" \
  --name "logic-app-name"

# Download logs
az webapp log download \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --log-file webapp-logs.zip
```

### Diagnostic Settings

```bash
# Enable diagnostic settings to send logs to Log Analytics
az monitor diagnostic-settings create \
  --name "diagnostics" \
  --resource "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{logic-app}" \
  --workspace "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.OperationalInsights/workspaces/{workspace}" \
  --logs '[{"category": "WorkflowRuntime", "enabled": true}]' \
  --metrics '[{"category": "AllMetrics", "enabled": true}]'
```

## Getting Additional Help

### Information to Gather

When opening a support ticket, include:

1. **Environment Details:**
   - Environment (DEV/UAT/PROD)
   - Azure region
   - Resource group name
   - Logic App name

2. **Error Information:**
   - Complete error message
   - Error code (if available)
   - Timestamp of occurrence
   - Workflow run ID

3. **Logs:**
   - Workflow run history (screenshot or export)
   - Application Insights correlation ID
   - Azure Activity Log entries

4. **What You've Tried:**
   - Troubleshooting steps already performed
   - Any workarounds attempted
   - Results of those attempts

### Support Channels

1. **Internal Documentation:**
   - [ARCHITECTURE.md](ARCHITECTURE.md)
   - [DEPLOYMENT.md](DEPLOYMENT.md)
   - [SECURITY.md](SECURITY.md)

2. **GitHub Issues:**
   - Search existing issues first
   - Create new issue with template
   - Include all diagnostic information

3. **Azure Support:**
   - For infrastructure issues
   - For quota increases
   - For service-specific problems

4. **Application Insights:**
   - Review telemetry data
   - Check for patterns
   - Use correlation IDs to trace requests

---

For architecture details, see [ARCHITECTURE.md](ARCHITECTURE.md)  
For deployment procedures, see [DEPLOYMENT.md](DEPLOYMENT.md)  
For security guidelines, see [SECURITY.md](SECURITY.md)
