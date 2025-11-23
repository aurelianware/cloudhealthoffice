# Cloud Health Office - Troubleshooting FAQ

Common issues and solutions for Cloud Health Office deployment and operation.

## Table of Contents

- [Installation & Setup](#installation--setup)
- [Azure Deployment](#azure-deployment)
- [Workflow Issues](#workflow-issues)
- [SFTP & EDI](#sftp--edi)
- [Service Bus](#service-bus)
- [Integration Account](#integration-account)
- [Security & Compliance](#security--compliance)
- [Performance](#performance)
- [Monitoring](#monitoring)

---

## Installation & Setup

### Issue: `npm install` fails with permission errors

**Symptoms**: Permission denied errors during npm install

**Solution**:
```bash
# Option 1: Use sudo (not recommended)
sudo npm install

# Option 2: Fix npm permissions (recommended)
mkdir ~/.npm-global
npm config set prefix '~/.npm-global'
echo 'export PATH=~/.npm-global/bin:$PATH' >> ~/.bashrc
source ~/.bashrc

# Then retry
npm install
```

### Issue: TypeScript compilation fails

**Symptoms**: `npm run build` shows TypeScript errors

**Solution**:
```bash
# Clean and rebuild
npm run clean
rm -rf node_modules package-lock.json
npm install
npm run build

# If still failing, check TypeScript version
npx tsc --version  # Should be 5.x
```

### Issue: CLI commands not found

**Symptoms**: `payer-generator: command not found`

**Solution**:
```bash
# Build the project first
npm run build

# Then use the full path
node dist/scripts/cli/payer-generator-cli.js --help

# Or install globally
npm link
payer-generator --help
```

---

## Azure Deployment

### Issue: Azure Deploy button fails

**Symptoms**: Template deployment fails in Azure Portal

**Common Causes & Solutions**:

1. **Resource name conflicts**
   ```bash
   # Use a unique baseName
   # Instead of: "test"
   # Use: "test-<your-org>-<timestamp>"
   ```

2. **Insufficient permissions**
   ```bash
   # Verify you have Contributor role
   az role assignment list --assignee <your-email> --query "[?roleDefinitionName=='Contributor']"
   ```

3. **Quota exceeded**
   ```bash
   # Check service quotas
   az vm list-usage --location eastus --output table
   
   # Request quota increase if needed
   # https://portal.azure.com/#view/Microsoft_Azure_Support/HelpAndSupportBlade
   ```

### Issue: Logic App won't start

**Symptoms**: Logic App state is "Stopped" or workflows don't appear

**Solution**:
```bash
# Check Logic App status
az webapp show -g <resource-group> -n <logic-app-name> --query state

# Restart Logic App
az webapp restart -g <resource-group> -n <logic-app-name>

# Check application settings
az webapp config appsettings list -g <resource-group> -n <logic-app-name>

# Verify storage connection string is set
az webapp config appsettings list -g <rg> -n <la> --query "[?name=='AzureWebJobsStorage']"
```

### Issue: Deployment times out

**Symptoms**: Deployment runs for >30 minutes and fails

**Solution**:
```bash
# Use smaller SKUs for testing
# In azuredeploy.json, set:
# - Logic App: WS1 (not WS2 or WS3)
# - Service Bus: Standard (not Premium)
# - Integration Account: Free (not Standard)

# Deploy infrastructure first, then workflows separately
az deployment group create -g <rg> -f azuredeploy.json -p baseName=<name>
# Wait for completion, then:
./deploy-workflows.ps1 -ResourceGroup <rg> -LogicAppName <la>
```

---

## Workflow Issues

### Issue: Workflows not visible in Azure Portal

**Symptoms**: Logic App deployed but no workflows shown

**Solution**:
```bash
# Wait 1-2 minutes for indexing
# Then refresh the portal

# Check if workflows.zip was deployed
az webapp deployment list -g <rg> -n <la>

# List workflows via CLI
az logicapp list-workflows -g <rg> --name <la>

# Redeploy workflows
cd logicapps
zip -r ../workflows.zip workflows/
cd ..
az webapp deploy -g <rg> -n <la> --src-path workflows.zip --type zip

# Restart Logic App
az webapp restart -g <rg> -n <la>
```

### Issue: Workflow run fails immediately

**Symptoms**: Workflow starts but fails within seconds

**Solution**:
```bash
# Check workflow run history
az logicapp run list -g <rg> --name <la> --workflow-name <workflow> --output table

# Get detailed error
az logicapp run show -g <rg> --name <la> --workflow-name <workflow> --run-name <run-id>

# Common issues:
# 1. Missing API connection
# 2. Invalid connection credentials
# 3. Missing managed identity permissions

# Fix managed identity permissions:
# For Storage
az role assignment create --assignee <logic-app-principal-id> \
  --role "Storage Blob Data Contributor" \
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<storage>

# For Service Bus
az role assignment create --assignee <logic-app-principal-id> \
  --role "Azure Service Bus Data Sender" \
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.ServiceBus/namespaces/<sb>
```

### Issue: Trigger not firing

**Symptoms**: SFTP/Service Bus trigger workflow never runs

**Solution**:

**For SFTP triggers**:
```bash
# Check SFTP connection
az webapp config connection-string list -g <rg> -n <la>

# Test SFTP credentials manually
sftp <username>@<sftp-host>

# Verify folder path exists
# Default: /inbound/attachments

# Check trigger settings in workflow.json
# Verify polling interval (default 1 minute)
```

**For Service Bus triggers**:
```bash
# Verify topic exists
az servicebus topic show --namespace-name <sb> -g <rg> --name attachments-in

# Check for messages in topic
az servicebus topic show --namespace-name <sb> -g <rg> --name attachments-in \
  --query "countDetails"

# Verify subscription exists
az servicebus topic subscription show --namespace-name <sb> -g <rg> \
  --topic-name attachments-in --name <subscription>

# Check managed identity has Data Receiver role
az role assignment list --assignee <principal-id> --scope <sb-resource-id>
```

---

## SFTP & EDI

### Issue: SFTP connection fails

**Symptoms**: "Connection refused" or "Authentication failed"

**Solution**:
```bash
# Test SFTP connectivity
sftp -v <username>@<host>

# For key-based auth
sftp -i <private-key> <username>@<host>

# Check if host allows Azure IPs
# Get Logic App outbound IPs
az webapp show -g <rg> -n <la> --query "outboundIpAddresses"

# Provide IPs to SFTP host for allowlist

# For Availity specifically
# Contact Availity support to allowlist Azure IPs
# Availity support: 1-800-AVAILITY
```

### Issue: X12 decode fails

**Symptoms**: "Invalid X12 format" or "Schema not found"

**Solution**:
```bash
# Verify X12 schema uploaded to Integration Account
az logic integration-account show -g <rg> --name <ia-name>

# List schemas
az logic integration-account schema list -g <rg> --integration-account <ia-name>

# Upload missing schema
az logic integration-account schema create -g <rg> \
  --integration-account <ia-name> \
  --name X12_005010X212_277 \
  --schema-type Xml \
  --content-type application/xml \
  --file ./X12_005010X212_277.xsd

# Verify trading partner agreement
az logic integration-account agreement list -g <rg> --integration-account <ia-name>
```

### Issue: 275 attachment file not processed

**Symptoms**: File uploaded to SFTP but not processed

**Checklist**:
1. ✓ File in correct folder? (`/inbound/attachments`)
2. ✓ File has `.edi` extension?
3. ✓ SFTP trigger enabled?
4. ✓ Workflow state is "Enabled"?
5. ✓ Valid X12 format?

**Debug Steps**:
```powershell
# Check workflow run history
./test-workflows.ps1 -TestInbound275 -ResourceGroup <rg> -LogicAppName <la>

# Manual trigger test
# 1. Upload test file to SFTP
# 2. Wait 1-2 minutes (polling interval)
# 3. Check Application Insights for errors
```

---

## Service Bus

### Issue: Messages stuck in dead-letter queue

**Symptoms**: Messages not processing, appearing in DLQ

**Solution**:
```bash
# Check dead-letter queue
az servicebus topic subscription show --namespace-name <sb> -g <rg> \
  --topic-name <topic> --name <subscription> \
  --query "countDetails.deadLetterMessageCount"

# Peek messages in DLQ
# Use Azure Portal Service Bus Explorer or:
az servicebus topic subscription rule list --namespace-name <sb> -g <rg> \
  --topic-name <topic> --subscription-name <subscription>

# Common causes:
# 1. Message expired (TTL exceeded)
# 2. Max delivery count exceeded
# 3. Message processing threw exception

# Fix: Update subscription settings
az servicebus topic subscription update --namespace-name <sb> -g <rg> \
  --topic-name <topic> --name <subscription> \
  --max-delivery-count 20 \
  --lock-duration PT5M
```

### Issue: Service Bus connection unauthorized

**Symptoms**: "Unauthorized" or "Forbidden" errors

**Solution**:
```bash
# Get Logic App managed identity principal ID
az webapp show -g <rg> -n <la> --query "identity.principalId" -o tsv

# Assign Service Bus Data Sender role
az role assignment create \
  --assignee <principal-id> \
  --role "Azure Service Bus Data Sender" \
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.ServiceBus/namespaces/<sb>

# Assign Data Receiver role for consumers
az role assignment create \
  --assignee <principal-id> \
  --role "Azure Service Bus Data Receiver" \
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.ServiceBus/namespaces/<sb>

# Wait 5-10 minutes for RBAC propagation
```

---

## Integration Account

### Issue: Integration Account SKU limit reached

**Symptoms**: "Free tier allows only 1 Integration Account per region"

**Solution**:
```bash
# Option 1: Use existing Integration Account
# In deployment, set useExistingIa=true

# Option 2: Delete unused Integration Accounts
az logic integration-account list -g <rg>
az logic integration-account delete -g <rg> --name <unused-ia>

# Option 3: Upgrade to Basic SKU ($400/month)
az logic integration-account update -g <rg> --name <ia> --sku Basic

# Option 4: Deploy in different region
# Edit azuredeploy.json, change location parameter
```

### Issue: Schema validation fails

**Symptoms**: "Schema validation error" in workflow run

**Solution**:
1. **Verify schema uploaded**
   ```bash
   az logic integration-account schema list -g <rg> --integration-account <ia>
   ```

2. **Check schema matches transaction type**
   - 275 → X12_005010X210_275
   - 277 → X12_005010X212_277
   - 278 → X12_005010X217_278

3. **Validate EDI file format**
   ```bash
   # Use online validator or:
   ./scripts/validate-edi-x12.ps1 -FilePath ./test-file.edi
   ```

4. **Re-upload schema if needed**
   ```bash
   az logic integration-account schema create -g <rg> \
     --integration-account <ia> \
     --name X12_005010X212_277 \
     --schema-type Xml \
     --file ./X12_005010X212_277.xsd
   ```

---

## Security & Compliance

### Issue: PHI detected in logs

**Symptoms**: Application Insights shows unredacted PHI

**Solution**:
```typescript
// NEVER log raw data that might contain PHI
// BAD:
console.log('Patient data:', patientData);

// GOOD:
import { redactPHI } from './src/security/hipaaLogger';
console.log('Patient data:', redactPHI(patientData));

// For workflow actions, use:
import { createHIPAALogger } from './src/security/hipaaLogger';
const logger = createHIPAALogger(userId, ipAddress);
logger.logDataAccess('Patient', patientId, 'VIEW');
```

**Scan for violations**:
```bash
# Run PHI scanner
pwsh -c "./scripts/scan-for-phi-pii.ps1 -Path ./logicapps/workflows"

# Run logging validation tests
npm test -- logging-validation.test.ts
```

### Issue: Key Vault access denied

**Symptoms**: "The user, group or application does not have secrets get permission"

**Solution**:
```bash
# Grant managed identity access to Key Vault
az keyvault set-policy --name <keyvault> \
  --object-id <logic-app-principal-id> \
  --secret-permissions get list

# Verify policy
az keyvault show --name <keyvault> --query "properties.accessPolicies"
```

---

## Performance

### Issue: High latency processing 275 attachments

**Symptoms**: Processing takes >30 seconds per file

**Solution**:
```bash
# Check Application Insights performance
# Look for slow dependencies:
# 1. SFTP download
# 2. X12 decode
# 3. QNXT API call
# 4. Blob upload

# Scale Logic App
az webapp update -g <rg> -n <la> --set properties.reserved=true
az webapp plan update -g <rg> -n <asp> --sku WS2

# Enable caching for QNXT API
# Configure connection pooling
# Consider QNXT API rate limits
```

### Issue: Service Bus throttling

**Symptoms**: "Quota exceeded" errors

**Solution**:
```bash
# Check Service Bus metrics
az monitor metrics list \
  --resource /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.ServiceBus/namespaces/<sb> \
  --metric "ThrottledRequests"

# Upgrade Service Bus SKU
az servicebus namespace update -g <rg> --name <sb> --sku Premium

# Or reduce message volume/rate
```

---

## Monitoring

### Issue: Application Insights not showing data

**Symptoms**: No telemetry in Application Insights

**Solution**:
```bash
# Verify Application Insights configured
az webapp config appsettings list -g <rg> -n <la> \
  --query "[?name=='APPLICATIONINSIGHTS_CONNECTION_STRING']"

# Check instrumentation key
az monitor app-insights component show -g <rg> --app <ai-name> \
  --query "instrumentationKey"

# Verify sampling rate
# In workflow or config, check samplingPercentage: 100

# Wait 3-5 minutes for data to appear
# Then refresh portal

# Enable detailed logging
az webapp log config -g <rg> -n <la> \
  --application-logging filesystem \
  --level verbose

# Stream logs
az webapp log tail -g <rg> -n <la>
```

### Issue: Alerts not firing

**Symptoms**: Expected alerts not received

**Solution**:
```bash
# Verify alert rule created
az monitor metrics alert list -g <rg>

# Check alert rule condition
az monitor metrics alert show -g <rg> --name <alert-name>

# Test alert manually
# Trigger condition (e.g., force workflow failure)

# Verify action group configured
az monitor action-group list -g <rg>

# Check email/SMS endpoint
az monitor action-group show -g <rg> --name <ag-name>
```

---

## Getting Additional Help

If these solutions don't resolve your issue:

1. **Check logs**:
   ```bash
   az webapp log tail -g <rg> -n <la>
   ```

2. **Run health check**:
   ```powershell
   ./scripts/test-e2e.ps1 -ResourceGroup <rg> -LogicAppName <la> -ServiceBusNamespace <sb>
   ```

3. **Review documentation**:
   - [ONBOARDING.md](./ONBOARDING.md)
   - [ARCHITECTURE.md](./ARCHITECTURE.md)
   - [SECURITY.md](./SECURITY.md)

4. **Community support**:
   - [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
   - [GitHub Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions)

5. **Report a bug**:
   - Include error messages, logs, and steps to reproduce
   - Redact any PHI or sensitive data
   - Provide environment details (Azure region, SKUs, etc.)

---

*Last updated: 2025-01-23*
