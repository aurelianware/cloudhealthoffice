# Copilot Instructions for HIPAA Attachments Repository

## Repository Overview

This repository implements a **HIPAA 275 Attachments Processing System** using Azure Logic Apps Standard and Infrastructure-as-Code. The solution processes electronic attachments from Availity SFTP, archives them in Azure Data Lake Storage Gen2 with date-based partitioning, decodes X12 275 messages, and integrates with QNXT claims system via APIs.

**Repository Type:** Azure Logic Apps Standard + Infrastructure (Bicep)  
**Primary Technologies:** Azure Logic Apps, Bicep, JSON workflows, PowerShell, GitHub Actions  
**Target Runtime:** Azure cloud services  
**Repository Size:** Small (~20 files, mainly configuration and infrastructure)

## Key Components

### Infrastructure (`infra/main.bicep`)
- **Azure Data Lake Storage Gen2**: Hierarchical namespace enabled for partitioned storage
- **Service Bus**: Topics for attachment processing (`attachments-in`) and RFAI requests (`rfai-requests`)
- **Logic App Standard**: Serverless workflow execution environment
- **Application Insights**: Telemetry and monitoring integration

### Workflows (`logicapps/workflows/`)
- **`ingest275/workflow.json`**: Main 275 ingestion workflow (SFTP → Blob → X12 decode → Service Bus → QNXT)
- **`rfai277/workflow.json`**: Outbound 277 RFAI workflow (Service Bus → X12 encode → SFTP)

Both workflows use API connections for SFTP, Blob Storage, Service Bus, and Integration Account.

## Build and Validation Commands

### Prerequisites
Always ensure these tools are available before any build operations:
```bash
# Required tools (pre-installed in GitHub Actions)
az --version          # Azure CLI 2.77.0+
az bicep version      # Bicep CLI 0.37.4+
jq --version          # jq 1.7+
pwsh --version        # PowerShell 7.4+
```

### JSON Workflow Validation
**Always run before making workflow changes:**
```bash
# Validate JSON syntax and structure
find "logicapps/workflows" -type f -name "workflow.json" -print0 | \
while IFS= read -r -d '' f; do
  echo "Checking $f"
  if ! jq . "$f" >/dev/null 2>&1; then
    echo "::error::Invalid JSON in $f"
    exit 1
  fi
  if ! jq -e 'has("definition") and has("kind") and has("parameters")' "$f" >/dev/null; then
    echo "::error::Missing required keys in $f (definition/kind/parameters)"
    exit 1
  fi
  echo "✓ Valid workflow JSON: $f"
done
```
**Expected time:** < 5 seconds  
**Must pass:** This validation is mandatory before any deployment

### Bicep Infrastructure Validation
**Always run before infrastructure changes:**
```bash
# Validate and compile Bicep template
az bicep build --file "infra/main.bicep" --outfile /tmp/arm.json
test -s /tmp/arm.json && echo "✓ Bicep compilation successful"
```
**Expected time:** 10-15 seconds  
**Warnings:** May show linter warnings about parent properties (safe to ignore)  
**Must pass:** Compilation failure will block deployment

### Workflow Packaging
**Required for Logic App deployment:**
```bash
# Create ZIP package for workflows (preserves folder structure)
WF_PATH="logicapps/workflows"
PARENT_DIR="$(dirname "$WF_PATH")"
BASE_NAME="$(basename "$WF_PATH")"
cd "$PARENT_DIR"
zip -r ../workflows.zip "$BASE_NAME"
cd ..
echo "Workflow package created: workflows.zip"
unzip -l workflows.zip | head -10  # Verify contents
```
**Expected time:** < 5 seconds  
**Critical:** ZIP must contain `workflows/` as top-level directory for Logic App Standard deployment

## Project Layout and Architecture

### Repository Structure
```
/
├── .github/workflows/           # CI/CD pipelines
│   ├── deploy_logicapps_workflows_matrix_with_lint.yml  # Main deployment workflow
│   └── sanity.yml              # Basic health check
├── infra/
│   └── main.bicep              # Azure infrastructure template
├── logicapps/workflows/        # Logic App workflows (deployed as ZIP)
│   ├── ingest275/workflow.json # 275 ingestion workflow
│   └── rfai277/workflow.json   # 277 RFAI workflow
├── bootstrap_repo.ps1          # Repository scaffolding script
├── fix_repo_structure.ps1      # Structure normalization script
└── README.md                   # Architecture and deployment guide
```

### Configuration Files
- **Bicep parameters**: Set in `infra/main.bicep` (location, baseName, storageSku)
- **Workflow parameters**: Defined in each `workflow.json` file (connections, endpoints, folders)
- **GitHub Actions**: Environment-specific secrets for Azure authentication (OIDC)

### Key Dependencies
- **Azure Integration Account**: Required for X12 275/277 schema processing
- **API Connections**: SFTP (Availity), Blob Storage, Service Bus, Integration Account
- **QNXT API**: External claims system integration endpoint
- **Service Bus Topics**: `attachments-in`, `rfai-requests` with dead-letter queues

## CI/CD and Validation Pipeline

### GitHub Actions Workflow
**File:** `.github/workflows/deploy_logicapps_workflows_matrix_with_lint.yml`

**Trigger:** Manual dispatch (`workflow_dispatch`) with parameters:
- `baseName`: Resource prefix (default: "hipaa-attachments")
- `bicepPath`: Infrastructure template path (default: "infra/main.bicep")
- `workflowsPath`: Workflows folder (default: "logicapps/workflows")

**Pipeline Stages:**
1. **Lint (DEV environment)**:
   - JSON validation of all `workflow.json` files
   - Structure validation (definition/kind/parameters keys)
   - **Time:** ~30 seconds

2. **Matrix Deploy (DEV/UAT/PROD)**:
   - Azure OIDC authentication per environment
   - Resource group creation (`pchp-attachments-{env}-rg`)
   - Bicep validation and ARM what-if preview
   - Infrastructure deployment via `azure/arm-deploy@v2`
   - Workflow ZIP packaging and deployment via `az webapp deploy`
   - Logic App restart (optional)
   - **Time per environment:** ~5-8 minutes

### Environment Configuration
Each environment requires these GitHub secrets:
- `AZURE_CLIENT_ID_{ENV}`: OIDC client ID
- `AZURE_TENANT_ID_{ENV}`: Azure AD tenant ID  
- `AZURE_SUBSCRIPTION_ID_{ENV}`: Target subscription ID

### Manual Validation Steps
For manual testing of changes:
```bash
# 1. Validate all workflows
find logicapps/workflows -name "workflow.json" -exec jq . {} \;

# 2. Test Bicep compilation
az bicep build --file infra/main.bicep --outfile /tmp/test.json

# 3. Preview infrastructure changes (requires Azure login)
az deployment group what-if \
  --resource-group "test-rg" \
  --template-file "infra/main.bicep" \
  --parameters baseName="test-hipaa" \
  --no-pretty-print

# 4. Package workflows for deployment
cd logicapps && zip -r ../workflows.zip workflows/
```

## Data Lake Storage Structure

Files are organized in Azure Data Lake Storage Gen2 with this partitioning:
```
hipaa-attachments/
└── raw/
    └── 275/
        └── {yyyy}/          # Year (e.g., 2024)
            └── {MM}/        # Month (01-12)
                └── {dd}/    # Day (01-31)
                    └── {filename}_{timestamp}.edi
```
This structure supports efficient querying and lifecycle management.

## Common Issues and Workarounds

### JSON Validation Failures
**Issue:** Invalid JSON syntax in workflow files  
**Fix:** Use `jq . file.json` to identify syntax errors  
**Prevention:** Always validate JSON after manual edits

### Bicep Compilation Warnings
**Issue:** "use-parent-property" warnings for Service Bus topics  
**Impact:** Cosmetic only, does not affect functionality  
**Action:** Safe to ignore; warnings do not block deployment

### ZIP Packaging Issues
**Issue:** Logic App deployment fails with "workflow not found"  
**Cause:** Incorrect ZIP structure (missing top-level `workflows/` folder)  
**Fix:** Ensure ZIP contains `workflows/ingest275/` and `workflows/rfai277/` paths

### PowerShell Script Execution
**Issue:** Execution policy restrictions  
**Fix:** Run with `-ExecutionPolicy Bypass` flag  
**Example:** `pwsh -ExecutionPolicy Bypass -File bootstrap_repo.ps1`

## Development Workflow

1. **Before making changes**: Run JSON and Bicep validation commands
2. **Test locally**: Use manual validation steps above
3. **Deploy via GitHub Actions**: Use workflow dispatch with appropriate environment
4. **Monitor deployment**: Check GitHub Actions logs and Azure portal
5. **Verify functionality**: Test Logic App workflows in Azure portal

## Trust These Instructions

These instructions are comprehensive and tested. Only search for additional information if:
- You encounter an error not documented in "Common Issues"
- You need to add new Azure resources beyond the current scope
- You need to modify the CI/CD pipeline structure

Always follow the validation steps before making changes to prevent deployment failures.