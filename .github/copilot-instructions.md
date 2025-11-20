# HIPAA Attachments - Logic Apps & Infrastructure

## Project Overview

This repository implements an Azure Logic Apps solution for processing HIPAA-compliant medical attachments:
- **HIPAA 275 Ingestion**: Polls Availity SFTP for 275 attachment files, archives to Data Lake, processes X12, publishes to Service Bus, updates QNXT
- **HIPAA 277 RFAI**: Consumes RFAI requests from Service Bus, generates X12 277, sends via SFTP
- **HIPAA 278 Processing**: Processes Health Care Services Review Information transactions
- **278 Replay Endpoint**: HTTP endpoint for deterministic replay of 278 transactions for debugging

**Infrastructure**: Azure Data Lake Storage Gen2 (hierarchical namespace), Service Bus (topics: attachments-in, rfai-requests, edi-278), Logic App Standard (WS1 SKU), Application Insights

Always reference these instructions first and only fallback to search or bash commands when you encounter unexpected information that does not match the info here.

## Documentation

**For detailed guidance, always consult these comprehensive documentation files:**

- **[CONTRIBUTING.md](../CONTRIBUTING.md)** - Development workflow, setup instructions, validation procedures, and code review standards
- **[ARCHITECTURE.md](../ARCHITECTURE.md)** - System architecture, component details, data flows (275/277/278), and design decisions
- **[DEPLOYMENT.md](../DEPLOYMENT.md)** - Step-by-step deployment procedures for DEV/UAT/PROD environments
- **[TROUBLESHOOTING.md](../TROUBLESHOOTING.md)** - Common issues and solutions for development, deployment, and runtime problems
- **[SECURITY.md](../SECURITY.md)** - HIPAA compliance requirements, encryption, access control, and secure development practices

**Quick References:**
- New developer onboarding: Start with [CONTRIBUTING.md](../CONTRIBUTING.md)
- Understanding system design: See [ARCHITECTURE.md](../ARCHITECTURE.md)
- Deploying changes: Follow [DEPLOYMENT.md](../DEPLOYMENT.md)
- Fixing issues: Check [TROUBLESHOOTING.md](../TROUBLESHOOTING.md)
- Security questions: Review [SECURITY.md](../SECURITY.md)

## Working Effectively

### Prerequisites and Setup
- Ensure Azure CLI is installed: `az --version` (requires 2.77.0+)
- Ensure Bicep CLI is available: `az bicep version` (requires 0.37.0+)
- Ensure PowerShell is available: `pwsh --version`
- Ensure JSON tools: `jq --version`
- Ensure ZIP utility: `which zip`

### Build and Validate Repository
- **Bicep Infrastructure Validation**: `az bicep build --file infra/main.bicep --outfile /tmp/arm.json` 
  - **TIMING**: Takes ~4 seconds. Use timeout of 30+ seconds.
  - Expect warnings about use-parent-property for Service Bus topics - this is normal.
  - SUCCESS: Creates /tmp/arm.json (4-5KB) without errors.

- **Logic Apps Workflow Validation**: `find logicapps/workflows -name "workflow.json" -exec jq -e 'has("definition") and has("kind") and has("parameters")' {} \;`
  - **TIMING**: Takes <1 second.
  - SUCCESS: All 4 workflows (ingest275, ingest278, replay278, rfai277) return `true`.

- **GitHub Actions Lint Process** (exactly as CI runs):
```bash
WF_PATH="logicapps/workflows"
find "$WF_PATH" -maxdepth 2 -type f -name "workflow.json" -print
failed=0
while IFS= read -r -d '' f; do
  echo "Checking $f"
  if ! jq . "$f" >/dev/null 2>&1; then
    echo "::error file=$f::Invalid JSON"; failed=1; continue
  fi
  if ! jq -e 'has("definition") and has("kind") and has("parameters")' "$f" >/dev/null; then
    echo "::error file=$f::Missing required keys"; failed=1; continue
  fi
  if ! jq -e '.kind=="Stateful" or .kind=="Stateless"' "$f" >/dev/null; then
    echo "::warning file=$f::kind is not Stateful/Stateless (verify)"
  fi
done < <(find "$WF_PATH" -type f -name "workflow.json" -print0)
exit $failed
```
  - **TIMING**: Takes <1 second. Use timeout of 10+ seconds.

### Repository Structure Management
- **Fix Repository Structure**: `pwsh -c "./fix_repo_structure.ps1 -RepoRoot ."`
  - **TIMING**: Takes <1 second. Use timeout of 30+ seconds.
  - Normalizes repo structure to expected Logic Apps deployment layout.
  - Run this if workflows are in unexpected locations.

### Deployment Preparation
- **Create Deployment ZIP**: 
```bash
WF_PATH="logicapps/workflows"
PARENT_DIR="$(dirname "$WF_PATH")"
BASE_NAME="$(basename "$WF_PATH")"
pushd "$PARENT_DIR" > /dev/null
zip -r ../workflows.zip "$BASE_NAME"
popd > /dev/null
ls -l workflows.zip
```
  - **TIMING**: Takes <1 second.
  - Creates workflows.zip (~14KB) containing all 4 workflows (ingest275, ingest278, replay278, rfai277).

- **Bicep What-If Analysis**: `az deployment group what-if --resource-group <rg> --template-file infra/main.bicep --parameters baseName=<name> --no-pretty-print`
  - **TIMING**: Takes ~4 seconds with valid Azure credentials.
  - Use for deployment validation without actual deployment.

## Validation Scenarios

### After Making Infrastructure Changes
1. **ALWAYS validate Bicep syntax**: `az bicep build --file infra/main.bicep --outfile /tmp/arm.json`
2. **Test parameter validation**: `az deployment group validate --resource-group fake-rg --template-file infra/main.bicep --parameters baseName=test-name` (expect auth failure but template validation)

### After Making Logic Apps Workflow Changes  
1. **ALWAYS validate JSON syntax**: `jq . logicapps/workflows/*/workflow.json`
2. **ALWAYS validate workflow structure**: Run the complete lint process above
3. **Test ZIP creation**: Ensure workflows.zip contains all 4 workflow subdirectories
4. **Manual validation scenarios**:
   - Verify all workflows are `"kind": "Stateful"`
   - Check that `definition`, `kind`, and `parameters` keys exist
   - Validate SFTP trigger configuration in ingest275/workflow.json
   - Validate Service Bus trigger configuration in rfai277/workflow.json
   - Validate SFTP trigger configuration in ingest278/workflow.json
   - Validate HTTP trigger configuration in replay278/workflow.json

### Before Committing Changes
- **ALWAYS run repository structure check**: `pwsh -c "./fix_repo_structure.ps1 -RepoRoot ."`
- **ALWAYS validate all workflows**: Run complete lint process
- **Check for YAML syntax in GitHub Actions**: Use basic YAML validation if modifying .github/workflows/

## Testing

### Workflow Testing Script
Use `test-workflows.ps1` for comprehensive workflow testing:

```powershell
# Test inbound 275 processing (Availity → Health Plan)
pwsh -c "./test-workflows.ps1 -TestInbound275"

# Test outbound 277 RFAI generation (Health Plan → Availity)
pwsh -c "./test-workflows.ps1 -TestOutbound277"

# Test complete end-to-end workflow
pwsh -c "./test-workflows.ps1 -TestFullWorkflow"

# Specify custom resource group and Logic App
pwsh -c "./test-workflows.ps1 -TestInbound275 -ResourceGroup 'my-rg' -LogicAppName 'my-la'"
```

**Trading Partners**:
- Sender: Availity (030240928)
- Receiver: Health Plan-QNXT ({config.payerId})

**Test Files**:
- `test-x12-275-availity-to-pchp.edi`: Sample 275 EDI file for testing
- `test-qnxt-response-payload.json`: Sample QNXT API response
- `test-plan-trading-partners.md`: Detailed test plan
- `testing-status-report.md`: Testing status and results

### CI/CD Automated Checks (pr-lint.yml)

All pull requests trigger automated validation:

1. **JSON Workflow Validation**: Validates all `workflow.json` files for syntax and required keys
2. **GitHub Actions Lint** (actionlint): Validates workflow YAML syntax and best practices
3. **YAML Lint** (yamllint): Validates YAML files in `.github/workflows/`
4. **Bicep Build**: Compiles all `.bicep` files to verify syntax (infra/main.bicep and attachments_logicapps_package/main.bicep)
5. **PowerShell Script Validation**: Checks all `.ps1` files for syntax errors

**To run locally before pushing**:
```bash
# JSON validation
find logicapps/workflows -name "workflow.json" -exec jq . {} \;

# Bicep validation
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# PowerShell syntax check
pwsh -Command "Get-Content './test-workflows.ps1' | Out-Null"
```

## Deployment

### Manual Deployment Commands
```bash
# Create resource group
az group create -n payer-attachments-rg -l eastus

# Deploy infrastructure
az deployment group create -g payer-attachments-rg -f infra/main.bicep -p baseName=payer-attachments

# Deploy workflows (after infrastructure)
az webapp deploy --resource-group payer-attachments-rg --name payer-attachments-la --src-path workflows.zip --type zip

# Restart Logic App
az webapp restart -g payer-attachments-rg -n payer-attachments-la
```

### GitHub Actions Deployment
- **Workflow**: "Deploy HIPAA Attachments (Matrix: DEV/UAT/PROD + DEV Lint)"
- **NEVER CANCEL**: Complete deployment takes 5-15 minutes per environment. Set timeout to 30+ minutes.
- **Required Secrets**: AZURE_CLIENT_ID_*, AZURE_TENANT_ID_*, AZURE_SUBSCRIPTION_ID_* for each environment
- **Deployment includes**: Bicep validation → ARM What-If → Infrastructure deployment → Workflow ZIP deployment

## Critical Timing and Timeout Guidelines

**NEVER CANCEL any deployment or long-running commands**:
- **Bicep validation**: 4 seconds (set timeout: 30+ seconds)  
- **JSON lint**: <1 second (set timeout: 10+ seconds)
- **ZIP creation**: <1 second (set timeout: 10+ seconds)
- **PowerShell repo fix**: 1 second (set timeout: 30+ seconds)
- **Azure deployment**: 5-15 minutes (set timeout: 30+ minutes)
- **Complete CI pipeline**: 10-20 minutes per environment (set timeout: 60+ minutes)

## Key Projects and Structure

### Repository Layout
```
.
├── .github/workflows/          # GitHub Actions deployment pipelines
│   ├── deploy_logicapps_workflows_matrix_with_lint.yml  # Main deployment
│   └── sanity.yml             # Simple health check
├── infra/
│   └── main.bicep            # Azure infrastructure template
├── logicapps/workflows/
│   ├── ingest275/workflow.json    # HIPAA 275 ingestion (SFTP→Data Lake→QNXT)
│   ├── ingest278/workflow.json    # HIPAA 278 Health Care Services Review processing
│   ├── replay278/workflow.json    # HTTP endpoint for deterministic 278 replay
│   └── rfai277/workflow.json      # HIPAA 277 RFAI outbound (SB→X12→SFTP)  
├── bootstrap_repo.ps1        # Creates new repo from template
├── fix_repo_structure.ps1    # Normalizes repo structure for deployment
└── README.md                # Architecture and deployment documentation
```

### Infrastructure Components (infra/main.bicep)
- **Azure Data Lake Storage Gen2**: Hierarchical namespace enabled for HIPAA attachments
- **Service Bus Namespace**: Topics for `attachments-in`, `rfai-requests`, and `edi-278`  
- **Logic App Standard**: Workflow runtime with WS1 SKU
- **Application Insights**: Telemetry and monitoring
- **Expected warnings**: Service Bus topic parent property linting warnings (safe to ignore)

### Workflow Details
- **ingest275**: Monitors SFTP → Archives to Data Lake → Processes X12 275 → Publishes to Service Bus → Updates QNXT
- **ingest278**: Processes X12 278 Health Care Services Review Information → Archives to Data Lake → Publishes to Service Bus topic `edi-278`
- **replay278**: HTTP endpoint for deterministic replay of 278 transactions (accepts blobUrl, queues message to `edi-278` topic)
- **rfai277**: Consumes RFAI requests from Service Bus → Generates X12 277 → Sends via SFTP
- **All workflows are Stateful** requiring Logic Apps Standard runtime

### Common File Outputs Reference

#### Repository Root (`ls -la`)
```
.git/
.github/
README.md
bootstrap_repo.ps1  
fix_repo_structure.ps1
infra/
logicapp_275_ingestion_template/
logicapps/
parkland_attachments_logicapps_package/
```

#### Workflow Files (`find logicapps/workflows -name "workflow.json"`)
```
logicapps/workflows/ingest275/workflow.json
logicapps/workflows/ingest278/workflow.json
logicapps/workflows/replay278/workflow.json
logicapps/workflows/rfai277/workflow.json
```

### Post-Deployment Configuration Requirements
After successful deployment, manual configuration required:
1. **API Connections**: Create connections for sftp-ssh, azureblob, servicebus, integrationaccount
2. **Integration Account**: Configure X12 schemas and agreements (275: X12_005010X210_275, 277: X12_005010X212_277, 278: X12_005010X217_278)
3. **Logic App Parameters**: Set SFTP folders, QNXT endpoints, X12 identifiers  
4. **Role Assignments**: Grant Logic App managed identity access to Storage & Service Bus
5. **Replay278 Endpoint**: Configure HTTP trigger authentication and test with blobUrl parameter

## Conventions and Best Practices

### File Naming and Structure
- **Workflow files**: Always named `workflow.json` within their workflow directory
- **Bicep files**: Use `main.bicep` for primary templates
- **PowerShell scripts**: Use kebab-case with `.ps1` extension (e.g., `fix-repo-structure.ps1`, `test-workflows.ps1`)
- **Data Lake paths**: Use date partitioning `hipaa-attachments/raw/{type}/yyyy/MM/dd/`

### Code Style
- **JSON**: All workflow.json files must have `definition`, `kind`, and `parameters` keys
- **Bicep**: Accept warnings about Service Bus topic parent properties (known issue)
- **PowerShell**: Follow standard PowerShell conventions with approved verbs
- **YAML**: Line length restrictions disabled for workflow files, truthy values follow GitHub Actions standards

### Common Pitfalls
- **Timeouts**: Always set appropriate timeouts for Azure operations (30+ seconds for Bicep, 30+ minutes for deployments)
- **ZIP Structure**: Workflow ZIP must contain `workflows/` as top-level directory, not individual workflow folders
- **Stateful Workflows**: All 4 workflows MUST be `"kind": "Stateful"` - Logic Apps Standard requirement
- **Service Bus Topics**: Remember to include all 3 topics: `attachments-in`, `rfai-requests`, `edi-278`
- **X12 Schemas**: Each transaction type needs its own schema version (275: X210, 277: X212, 278: X217)

### Environment-Specific Notes
- **DEV**: Uses `deploy-dev.yml` workflow, eastus region
- **UAT**: Auto-deploys on `release/*` branches via `deploy-uat.yml`, eastus region
- **PROD**: Uses `deploy.yml` workflow, manual trigger only
- **Resource Naming**: Pattern is `{baseName}-{resource-type}` (e.g., `hipaa-attachments-la`, `hipaa-attachments-svc`)

### Security and Compliance
- **HIPAA Compliance**: All storage uses encryption at rest, SFTP connections use SSH keys
- **Managed Identity**: Logic Apps use system-assigned managed identity for Azure resource access
- **OIDC Authentication**: GitHub Actions use OpenID Connect for Azure authentication (no secrets stored)
- **Secrets Management**: Never commit API keys, connection strings, or credentials to repository
- **For detailed security guidelines**: See [SECURITY.md](../SECURITY.md)

## Common Development Tasks and Copilot Prompts

### Getting Started
**"I'm new to this repository. How do I get started?"**
- Response should reference [CONTRIBUTING.md](../CONTRIBUTING.md) for prerequisites, setup, and workflow

**"What does this system do?"**
- Response should reference [ARCHITECTURE.md](../ARCHITECTURE.md) for high-level overview and component descriptions

### Validation Tasks
**"Validate all workflow JSON files"**
```bash
find logicapps/workflows -name "workflow.json" -exec jq -e 'has("definition") and has("kind") and has("parameters")' {} \;
```

**"Validate Bicep templates"**
```bash
az bicep build --file infra/main.bicep --outfile /tmp/arm.json
```

**"Run all validation checks before committing"**
```bash
# JSON validation
find logicapps/workflows -name "workflow.json" -exec jq . {} \;
# Bicep validation
az bicep build --file infra/main.bicep --outfile /tmp/arm.json
# PowerShell validation
pwsh -Command "Get-Content './test-workflows.ps1' | Out-Null"
```

### Deployment Tasks
**"How do I deploy to DEV/UAT/PROD?"**
- Response should reference [DEPLOYMENT.md](../DEPLOYMENT.md) for environment-specific procedures

**"Create a workflow deployment package"**
```bash
cd logicapps && zip -r ../workflows.zip workflows/ && cd ..
unzip -l workflows.zip  # Verify structure
```

**"Deploy infrastructure to UAT"**
```bash
az deployment group create \
  --resource-group "payer-attachments-uat-rg" \
  --template-file infra/main.bicep \
  --parameters baseName="hipaa-attachments-uat"
```

### Troubleshooting Tasks
**"Workflow is failing, how do I debug?"**
- Response should reference [TROUBLESHOOTING.md](../TROUBLESHOOTING.md) for debugging steps
- Check workflow run history in Azure Portal
- Query Application Insights for errors
- Review action inputs/outputs

**"X12 decode is failing"**
- Check Integration Account trading partner configuration
- Verify ISA/GS identifiers match (Availity: 030240928, Health Plan: {config.payerId})
- Ensure correct schema is uploaded (275: X12_005010X210_275)
- See [TROUBLESHOOTING.md](../TROUBLESHOOTING.md#x12-decode-failures)

**"OIDC authentication is failing in GitHub Actions"**
- Verify federated credentials are configured correctly
- Check subject matches repo/branch pattern
- Ensure service principal has Contributor role
- See [TROUBLESHOOTING.md](../TROUBLESHOOTING.md#oidc-authentication-failures)

### Architecture Questions
**"How does the 275 attachment ingestion flow work?"**
- Reference [ARCHITECTURE.md](../ARCHITECTURE.md#275-attachment-ingestion-flow)
- Flow: SFTP → Data Lake → X12 Decode → Extract Metadata → QNXT API → Service Bus

**"What are the Service Bus topics and what are they used for?"**
- `attachments-in`: Processed 275 attachment events
- `rfai-requests`: 277 RFAI outbound requests
- `edi-278`: 278 Health Care Services Review transactions
- See [ARCHITECTURE.md](../ARCHITECTURE.md#service-bus-namespace)

**"What's the data partitioning structure in Data Lake?"**
- Path: `hipaa-attachments/raw/{type}/yyyy/MM/dd/`
- Example: `hipaa-attachments/raw/275/2024/01/15/file.edi`
- See [ARCHITECTURE.md](../ARCHITECTURE.md#azure-data-lake-storage-gen2)

### Security Questions
**"How do I handle secrets and credentials?"**
- Never commit secrets to code
- Use Azure Key Vault for secret storage
- Use GitHub Secrets for CI/CD
- Use managed identity instead of connection strings
- See [SECURITY.md](../SECURITY.md#secrets-management)

**"What are the HIPAA compliance requirements?"**
- Encryption at rest and in transit (TLS 1.2+)
- Access logging and monitoring
- Minimum necessary principle
- See [SECURITY.md](../SECURITY.md#hipaa-compliance-overview)

**"How do I configure managed identity permissions?"**
```bash
# Get principal ID
PRINCIPAL_ID=$(az webapp identity show --resource-group "rg" --name "la" --query principalId -o tsv)
# Assign Storage Blob Data Contributor role
az role assignment create --assignee "$PRINCIPAL_ID" --role "Storage Blob Data Contributor" --scope "{storage-scope}"
```

### Testing Tasks
**"How do I test workflows?"**
```powershell
# Test 275 ingestion
pwsh -c "./test-workflows.ps1 -TestInbound275 -ResourceGroup 'rg' -LogicAppName 'la'"
# Test 277 RFAI
pwsh -c "./test-workflows.ps1 -TestOutbound277 -ResourceGroup 'rg' -LogicAppName 'la'"
```

**"How do I test the 278 replay endpoint?"**
```bash
curl -X POST "https://{logic-app}.azurewebsites.net/api/replay278/..." \
  -H "Content-Type: application/json" \
  -d '{"blobUrl": "hipaa-attachments/raw/278/2024/01/15/test.edi"}'
```

### Code Review Tasks
**"Review this workflow JSON for issues"**
- Check for required keys: `definition`, `kind`, `parameters`
- Verify `kind` is `"Stateful"`
- Validate JSON syntax with `jq`
- Review action names are descriptive
- Check error handling and retry policies
- See [CONTRIBUTING.md](../CONTRIBUTING.md#code-review-standards)

**"Security review checklist for this PR"**
- No hardcoded secrets or credentials
- No PHI logged in plain text
- Input validation present
- Error messages don't expose sensitive information
- Managed identity used (not connection strings)
- See [SECURITY.md](../SECURITY.md#code-review-checklist)

### Workflow Modifications
**"Add a new Logic Apps workflow"**
1. Create directory under `logicapps/workflows/`
2. Create `workflow.json` with required structure
3. Set `"kind": "Stateful"`
4. Define trigger and actions
5. Validate JSON syntax
6. Test and deploy
- See [CONTRIBUTING.md](../CONTRIBUTING.md#making-changes)

**"Update Service Bus topic configuration"**
1. Update topic in `infra/main.bicep`
2. Update workflow parameters
3. Test message publishing/consumption
4. See [ARCHITECTURE.md](../ARCHITECTURE.md#service-bus-namespace)

### Performance and Monitoring
**"Query Application Insights for failed workflows"**
```kusto
traces
| where timestamp > ago(24h)
| where severityLevel >= 3
| where message contains "workflow"
| project timestamp, message, severityLevel
```

**"Check QNXT API performance"**
```kusto
dependencies
| where name contains "QNXT"
| summarize avg(duration), percentile(duration, 95) by bin(timestamp, 1h)
```

**"Monitor Service Bus queue depth"**
```bash
az servicebus topic show --resource-group "rg" --namespace-name "ns" --name "topic" --query "messageCount"
```
