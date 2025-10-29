# HIPAA Attachments - Logic Apps & Infrastructure

## Project Overview

This repository implements an Azure Logic Apps solution for processing HIPAA-compliant medical attachments:
- **HIPAA 275 Ingestion**: Polls Availity SFTP for 275 attachment files, archives to Data Lake, processes X12, publishes to Service Bus, updates QNXT
- **HIPAA 277 RFAI**: Consumes RFAI requests from Service Bus, generates X12 277, sends via SFTP
- **HIPAA 278 Processing**: Processes Health Care Services Review Information transactions
- **278 Replay Endpoint**: HTTP endpoint for deterministic replay of 278 transactions for debugging

**Infrastructure**: Azure Data Lake Storage Gen2 (hierarchical namespace), Service Bus (topics: attachments-in, rfai-requests, edi-278), Logic App Standard (WS1 SKU), Application Insights

Always reference these instructions first and only fallback to search or bash commands when you encounter unexpected information that does not match the info here.

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

## Deployment

### Manual Deployment Commands
```bash
# Create resource group
az group create -n pchp-attachments-rg -l eastus

# Deploy infrastructure
az deployment group create -g pchp-attachments-rg -f infra/main.bicep -p baseName=pchp-attachments

# Deploy workflows (after infrastructure)
az webapp deploy --resource-group pchp-attachments-rg --name pchp-attachments-la --src-path workflows.zip --type zip

# Restart Logic App
az webapp restart -g pchp-attachments-rg -n pchp-attachments-la
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
