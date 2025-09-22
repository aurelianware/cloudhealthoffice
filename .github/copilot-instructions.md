# Copilot Instructions for HIPAA Attachments Repository

**ALWAYS follow these instructions first and only fallback to search or additional context gathering if the information here is incomplete or found to be in error.**

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

## Working Effectively

**Bootstrap, validate, and deploy the repository - NEVER CANCEL these operations:**

### Essential Commands (Run in Order)
```bash
# 1. Validate prerequisites - TIMEOUT: 60 seconds
az --version          # Azure CLI 2.77.0+
az bicep version      # Bicep CLI 0.37.4+
jq --version          # jq 1.7+
pwsh --version        # PowerShell 7.4+

# 2. Run complete validation pipeline - TIMEOUT: 300 seconds (5 minutes)
# This is your primary build command - validates all components
find "logicapps/workflows" -type f -name "workflow.json" -exec jq . {} \; >/dev/null
az bicep build --file "infra/main.bicep" --outfile /tmp/arm.json
cd logicapps && zip -r ../workflows.zip workflows/ && cd ..

# 3. Test individual components - TIMEOUT: 180 seconds each
az bicep build --file "infra/main.bicep" --outfile /tmp/test.json    # Bicep compilation test
find logicapps/workflows -name "workflow.json" -exec jq . {} \;      # JSON validation test

# 4. PowerShell script execution - TIMEOUT: 60 seconds
pwsh -ExecutionPolicy Bypass -File fix_repo_structure.ps1            # Structure normalization
pwsh -ExecutionPolicy Bypass -File bootstrap_repo.ps1 <params>       # Repository scaffolding (requires parameters)
```

**Expected Times (NEVER CANCEL before these minimums):**
- Prerequisites check: 5 seconds
- Complete validation: 2-6 minutes  
- Bicep compilation: 4-15 seconds
- JSON validation: 1-5 seconds
- PowerShell scripts: 5-30 seconds

### Deployment via GitHub Actions
```bash
# Deploy using workflow dispatch (manual trigger)
# Go to: GitHub repo → Actions → "Deploy HIPAA Attachments (Matrix: DEV/UAT/PROD + DEV Lint)"
# NEVER CANCEL: Total deployment time 15-30 minutes for all environments
# Per environment: 5-8 minutes
# Lint stage: 30 seconds
```

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
**Always run before making workflow changes - NEVER CANCEL:**
```bash
# Validate JSON syntax and structure - TIMEOUT: 60 seconds
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
**Expected time:** < 5 seconds - NEVER CANCEL, set timeout to 60+ seconds  
**Must pass:** This validation is mandatory before any deployment

### Bicep Infrastructure Validation
**Always run before infrastructure changes - NEVER CANCEL:**
```bash
# Validate and compile Bicep template - TIMEOUT: 180 seconds
az bicep build --file "infra/main.bicep" --outfile /tmp/arm.json
test -s /tmp/arm.json && echo "✓ Bicep compilation successful"
```
**Expected time:** 4-15 seconds - NEVER CANCEL, set timeout to 180+ seconds  
**Warnings:** May show linter warnings about parent properties (safe to ignore)  
**Must pass:** Compilation failure will block deployment

### Workflow Packaging
**Required for Logic App deployment - NEVER CANCEL:**
```bash
# Create ZIP package for workflows (preserves folder structure) - TIMEOUT: 60 seconds
WF_PATH="logicapps/workflows"
PARENT_DIR="$(dirname "$WF_PATH")"
BASE_NAME="$(basename "$WF_PATH")"
cd "$PARENT_DIR"
zip -r ../workflows.zip "$BASE_NAME"
cd ..
echo "Workflow package created: workflows.zip"
unzip -l workflows.zip | head -10  # Verify contents
```
**Expected time:** < 5 seconds - NEVER CANCEL, set timeout to 60+ seconds  
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
   - **Time:** ~30 seconds - NEVER CANCEL, set timeout to 120+ seconds

2. **Matrix Deploy (DEV/UAT/PROD)**:
   - Azure OIDC authentication per environment
   - Resource group creation (`pchp-attachments-{env}-rg`)
   - Bicep validation and ARM what-if preview
   - Infrastructure deployment via `azure/arm-deploy@v2`
   - Workflow ZIP packaging and deployment via `az webapp deploy`
   - Logic App restart (optional)
   - **Time per environment:** ~5-8 minutes - NEVER CANCEL, set timeout to 20+ minutes

### Environment Configuration
Each environment requires these GitHub secrets:
- `AZURE_CLIENT_ID_{ENV}`: OIDC client ID
- `AZURE_TENANT_ID_{ENV}`: Azure AD tenant ID  
- `AZURE_SUBSCRIPTION_ID_{ENV}`: Target subscription ID

### Manual Validation Steps
For manual testing of changes - NEVER CANCEL these operations:
```bash
# 1. Validate all workflows - TIMEOUT: 60 seconds
find logicapps/workflows -name "workflow.json" -exec jq . {} \;

# 2. Test Bicep compilation - TIMEOUT: 180 seconds
az bicep build --file infra/main.bicep --outfile /tmp/test.json

# 3. Preview infrastructure changes (requires Azure login) - TIMEOUT: 300 seconds
az deployment group what-if \
  --resource-group "test-rg" \
  --template-file "infra/main.bicep" \
  --parameters baseName="test-hipaa" \
  --no-pretty-print

# 4. Package workflows for deployment - TIMEOUT: 60 seconds
cd logicapps && zip -r ../workflows.zip workflows/
```

## Validation Scenarios

**MANDATORY**: After making changes, always validate functionality through these complete end-to-end scenarios:

### Scenario 1: Complete Build and Validation Pipeline
**NEVER CANCEL - Expected time: 2-3 minutes**
```bash
# TIMEOUT: 300 seconds - Run complete validation pipeline
cd /path/to/hipaa-attachments

# Step 1: Validate prerequisites (5 seconds)
az --version && az bicep version && jq --version && pwsh --version

# Step 2: JSON workflow validation (5 seconds)
find "logicapps/workflows" -type f -name "workflow.json" -print0 | \
while IFS= read -r -d '' f; do
  echo "Checking $f"
  jq . "$f" >/dev/null && \
  jq -e 'has("definition") and has("kind") and has("parameters")' "$f" >/dev/null && \
  echo "✓ Valid: $f"
done

# Step 3: Bicep validation (15 seconds)
az bicep build --file "infra/main.bicep" --outfile /tmp/arm.json
test -s /tmp/arm.json && echo "✓ Bicep compilation successful"

# Step 4: Workflow packaging (5 seconds)
WF_PATH="logicapps/workflows"
PARENT_DIR="$(dirname "$WF_PATH")"
BASE_NAME="$(basename "$WF_PATH")"
cd "$PARENT_DIR"
zip -r ../workflows.zip "$BASE_NAME"
cd ..
unzip -l workflows.zip | head -5
echo "✓ Complete validation pipeline passed"
```

### Scenario 2: PowerShell Script Validation
**NEVER CANCEL - Expected time: 10-15 seconds**
```bash
# TIMEOUT: 60 seconds - Validate PowerShell capabilities
pwsh -ExecutionPolicy Bypass -Command "
  Write-Host '✓ PowerShell execution working';
  Get-ChildItem ./infra/*.bicep;
  Get-ChildItem ./logicapps/workflows/*/workflow.json
"
```

### Scenario 3: GitHub Actions Lint Simulation
**NEVER CANCEL - Expected time: 30-45 seconds**
```bash
# TIMEOUT: 120 seconds - Simulate GitHub Actions lint workflow
WF_PATH="logicapps/workflows"

# Validate workflow folder exists
[ -d "$WF_PATH" ] && echo "✓ Workflows folder exists" || exit 1

# Find workflow files
find "$WF_PATH" -maxdepth 2 -type f -name "workflow.json" -print

# Lint JSON structure (matching GitHub Actions logic)
failed=0
while IFS= read -r -d '' f; do
  echo "Linting $f"
  jq . "$f" >/dev/null 2>&1 || { echo "::error file=$f::Invalid JSON"; failed=1; }
  jq -e 'has("definition") and has("kind") and has("parameters")' "$f" >/dev/null || { echo "::error file=$f::Missing required keys"; failed=1; }
  jq -e '.kind=="Stateful" or .kind=="Stateless"' "$f" >/dev/null || echo "::warning file=$f::kind verification"
done < <(find "$WF_PATH" -type f -name "workflow.json" -print0)

[ $failed -eq 0 ] && echo "✓ GitHub Actions lint simulation passed"
```

**Critical Validation Rules:**
- **NEVER CANCEL** any of these scenarios - they may take up to 5 minutes total
- **MUST PASS**: All three scenarios must complete successfully before deployment
- **Manual verification**: Check that all ✓ success messages appear
- **Error handling**: Any scenario that fails indicates changes broke the build

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

**ALWAYS follow this exact sequence - NEVER SKIP validation steps:**

1. **Before making changes**: Run JSON and Bicep validation commands (TIMEOUT: 300 seconds)
2. **After making changes**: Run complete validation scenarios 1-3 above (TIMEOUT: 600 seconds) 
3. **Test locally**: Use manual validation steps with proper timeouts
4. **Deploy via GitHub Actions**: Use workflow dispatch with appropriate environment
5. **Monitor deployment**: Check GitHub Actions logs and Azure portal (deployments may take 5-8 minutes per environment)
6. **Verify functionality**: Test Logic App workflows in Azure portal

**CRITICAL TIMING REQUIREMENTS:**
- Build operations: NEVER CANCEL before 5 minutes minimum
- Validation scenarios: NEVER CANCEL before 10 minutes minimum  
- GitHub Actions deployment: NEVER CANCEL before 20 minutes minimum
- Always set timeouts to 2x expected time to account for Azure service delays

## Trust These Instructions

These instructions are comprehensive and tested. Only search for additional information if:
- You encounter an error not documented in "Common Issues"
- You need to add new Azure resources beyond the current scope
- You need to modify the CI/CD pipeline structure

Always follow the validation steps before making changes to prevent deployment failures.

## Common Tasks and Quick Reference

The following are validated outputs from frequently run commands. Reference these instead of running bash commands to save time.

### Repository Structure
```
/home/runner/work/hipaa-attachments/hipaa-attachments/
├── .github/workflows/           # CI/CD pipelines
│   ├── deploy_logicapps_workflows_matrix_with_lint.yml  # Main deployment (15-30 min)
│   └── sanity.yml              # Basic health check (30 sec)
├── infra/
│   └── main.bicep              # Azure infrastructure template (4-15 sec to compile)
├── logicapps/workflows/        # Logic App workflows (5 sec to package)
│   ├── ingest275/workflow.json # 275 ingestion workflow (~8KB)
│   └── rfai277/workflow.json   # 277 RFAI workflow (~5KB)
├── bootstrap_repo.ps1          # Repository scaffolding script (requires parameters)
├── fix_repo_structure.ps1      # Structure normalization script (5-30 sec)
└── README.md                   # Architecture and deployment guide
```

### Workflow File Structure
```json
// Both workflow.json files contain:
{
  "definition": { /* Logic App definition */ },
  "kind": "Stateful",  // or "Stateless"
  "parameters": { /* Connection and configuration parameters */ }
}
```

### Prerequisites Versions (Validated)
```
azure-cli: 2.77.0
Bicep CLI: 0.37.4 (27cc8db2ed)
jq: 1.7
PowerShell: 7.4.11
```

### GitHub Actions Environments
- **DEV**: `pchp-attachments-dev-rg` in eastus
- **UAT**: `pchp-attachments-uat-rg` in eastus  
- **PROD**: `pchp-attachments-prod-rg` in eastus

Each requires: `AZURE_CLIENT_ID_{ENV}`, `AZURE_TENANT_ID_{ENV}`, `AZURE_SUBSCRIPTION_ID_{ENV}` secrets

### Key File Locations for Changes
- **Infrastructure changes**: `infra/main.bicep`
- **Workflow logic**: `logicapps/workflows/*/workflow.json`
- **CI/CD pipeline**: `.github/workflows/deploy_logicapps_workflows_matrix_with_lint.yml`
- **Repository structure**: Use `fix_repo_structure.ps1` after changes