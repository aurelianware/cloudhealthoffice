# Deployment Guide

This guide provides step-by-step instructions for deploying the HIPAA Attachments system to Azure environments (DEV/UAT/PROD).

## Table of Contents
- [Prerequisites](#prerequisites)
- [GitHub Configuration](#github-configuration)
- [Environment Protection Rules and Approval Gates](#environment-protection-rules-and-approval-gates)
- [Pre-Deployment Validation](#pre-deployment-validation)
- [Environment Deployment](#environment-deployment)
- [Post-Deployment Configuration](#post-deployment-configuration)
- [Verification and Testing](#verification-and-testing)
- [Rollback Procedures](#rollback-procedures)
- [Troubleshooting](#troubleshooting)

## Prerequisites

### Required Tools

Ensure all required tools are installed and properly configured:

```bash
# Verify Azure CLI (minimum 2.77.0)
az --version

# Verify Bicep CLI (minimum 0.37.0)
az bicep version

# Verify PowerShell (minimum 7.4)
pwsh --version

# Verify jq (minimum 1.7)
jq --version

# Verify zip utility
which zip
```

**If any tool is missing, see [CONTRIBUTING.md](CONTRIBUTING.md#prerequisites) for installation instructions.**

### Azure Prerequisites

#### Subscription Access
- [ ] Azure subscription with Contributor role or higher
- [ ] Access to create resource groups
- [ ] Permission to create managed identities
- [ ] Access to configure RBAC role assignments

#### Service Principals / OIDC Setup

For GitHub Actions deployment, you need OIDC federated credentials configured:

1. **Create Azure AD Application** (one per environment)
2. **Configure Federated Credentials** for GitHub
3. **Assign Contributor Role** to subscription or resource group
4. **Gather Required IDs:**
   - Application (Client) ID
   - Tenant ID
   - Subscription ID

**Create OIDC Application (Azure CLI):**
```bash
# Set variables
ENV="dev"  # or uat, prod
APP_NAME="hipaa-attachments-$ENV-github"
REPO_OWNER="aurelianware"
REPO_NAME="hipaa-attachments"
SUBSCRIPTION_ID="<your-subscription-id>"

# Create Azure AD application
az ad app create --display-name "$APP_NAME"

# Get application ID
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv)

# Create service principal
az ad sp create --id "$APP_ID"

# Get object ID
SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query "id" -o tsv)

# Create federated credential for main branch (adjust for environment)
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-$ENV\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:$REPO_OWNER/$REPO_NAME:ref:refs/heads/main\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"

# Assign Contributor role
az role assignment create \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID"

# Output IDs (save these for GitHub secrets)
echo "AZURE_CLIENT_ID_${ENV^^}: $APP_ID"
echo "AZURE_TENANT_ID_${ENV^^}: $(az account show --query tenantId -o tsv)"
echo "AZURE_SUBSCRIPTION_ID_${ENV^^}: $SUBSCRIPTION_ID"
```

### Required Permissions

The deployment identity needs these permissions:

| Permission | Scope | Purpose |
|------------|-------|---------|
| Contributor | Resource Group | Create and manage resources |
| User Access Administrator | Resource Group | Assign managed identity roles |
| Storage Blob Data Contributor | Storage Account | Grant Logic App access |
| Azure Service Bus Data Sender | Service Bus | Grant Logic App access |

## GitHub Configuration

### Repository Secrets

Configure these secrets for each environment in GitHub Settings → Secrets and variables → Actions:

#### DEV Environment
```
AZURE_CLIENT_ID_DEV         = <app-id-from-azure>
AZURE_TENANT_ID_DEV         = <tenant-id-from-azure>
AZURE_SUBSCRIPTION_ID_DEV   = <subscription-id>
```

#### UAT Environment
```
AZURE_CLIENT_ID_UAT         = <app-id-from-azure>
AZURE_TENANT_ID_UAT         = <tenant-id-from-azure>
AZURE_SUBSCRIPTION_ID_UAT   = <subscription-id>
```

#### PROD Environment
```
AZURE_CLIENT_ID_PROD        = <app-id-from-azure>
AZURE_TENANT_ID_PROD        = <tenant-id-from-azure>
AZURE_SUBSCRIPTION_ID_PROD  = <subscription-id>
```

**To configure secrets:**
1. Go to GitHub repository → Settings → Secrets and variables → Actions
2. Click "New repository secret"
3. Enter name (e.g., `AZURE_CLIENT_ID_DEV`)
4. Paste value from Azure setup
5. Click "Add secret"
6. Repeat for all secrets

### Repository Variables (Optional)

Configure environment-specific variables:

```
AZURE_REGION_DEV   = eastus
AZURE_REGION_UAT   = eastus
AZURE_REGION_PROD  = eastus

BASE_NAME_DEV      = hipaa-attachments-dev
BASE_NAME_UAT      = hipaa-attachments-uat
BASE_NAME_PROD     = hipaa-attachments-prod
```

## Environment Protection Rules and Approval Gates

This section describes how to configure GitHub Environment protection rules to require manual approvals before deployments to UAT and PROD environments.

### Overview

The deployment workflows use GitHub Environments with protection rules to implement a gated release strategy:

- **UAT-approval**: Approval gate before UAT deployment
- **UAT**: Actual UAT environment for resource deployment
- **PROD-approval**: Approval gate before PROD deployment
- **PROD**: Actual PROD environment for resource deployment

### Quick Reference

**For Approvers:**
1. You'll receive a GitHub notification when approval is needed
2. Click "Review pending deployments" in the notification or workflow run
3. Review the deployment details (branch, commit, changes)
4. Click "Approve and deploy" or "Reject" with optional comment
5. Deployment proceeds if approved, stops if rejected

**For Deployers:**
1. Push to `release/*` branch (UAT) or trigger manual workflow (PROD)
2. Workflow starts and waits at approval gate
3. Configured reviewers are notified automatically
4. Monitor workflow run in Actions tab
5. Once approved, deployment continues automatically

### Why Use Approval Gates?

Approval gates provide:
- ✅ **Risk Mitigation**: Prevent accidental deployments to production
- ✅ **Compliance**: Meet audit requirements for change management
- ✅ **Quality Control**: Ensure proper testing before production release
- ✅ **Accountability**: Track who approved each deployment

### Step-by-Step Configuration

#### Step 1: Create GitHub Environments

1. Navigate to your GitHub repository
2. Click **Settings** → **Environments**
3. Create the following environments (click "New environment" for each):

   **For UAT:**
   - Environment name: `UAT-approval`
   - Environment name: `UAT`

   **For PROD:**
   - Environment name: `PROD-approval`
   - Environment name: `PROD`

#### Step 2: Configure UAT-approval Environment

1. Click on **UAT-approval** environment
2. Under "Deployment protection rules":
   - ✅ Check **Required reviewers**
   - Add reviewers (UAT leads, QA team members)
   - Minimum: 1-2 reviewers recommended
3. Optional settings:
   - **Wait timer**: Set to 0 minutes (approval required immediately)
   - **Deployment branches**: Select "Selected branches" → Add `release/*` pattern
4. Click **Save protection rules**

#### Step 3: Configure UAT Environment

1. Click on **UAT** environment
2. Under "Environment secrets", add UAT-specific secrets:
   - `AZURE_CLIENT_ID_UAT`
   - `AZURE_TENANT_ID_UAT`
   - `AZURE_SUBSCRIPTION_ID_UAT`
3. Under "Environment variables", add UAT-specific variables:
   - `AZURE_RG_NAME` = `hipaa-attachments-uat-rg`
   - `AZURE_LOCATION` = `eastus`
   - `BASE_NAME` = `hipaa-attachments-uat`
4. Optional protection rules:
   - **Deployment branches**: Select "Selected branches" → Add `release/*` pattern
5. Click **Save protection rules**

#### Step 4: Configure PROD-approval Environment

1. Click on **PROD-approval** environment
2. Under "Deployment protection rules":
   - ✅ Check **Required reviewers**
   - Add reviewers (Production approvers, DevOps leads, Compliance team)
   - Minimum: 2-3 reviewers recommended for production
3. Optional settings:
   - **Wait timer**: Set to 0 minutes (or add a delay if needed)
   - **Deployment branches**: Select "Protected branches only" (main branch)
4. Click **Save protection rules**

#### Step 5: Configure PROD Environment

1. Click on **PROD** environment
2. Under "Environment secrets", add PROD-specific secrets:
   - `AZURE_CLIENT_ID` (or `AZURE_CLIENT_ID_PROD`)
   - `AZURE_TENANT_ID` (or `AZURE_TENANT_ID_PROD`)
   - `AZURE_SUBSCRIPTION_ID` (or `AZURE_SUBSCRIPTION_ID_PROD`)
   - `SFTP_HOST`
   - `SFTP_USERNAME`
   - `SFTP_PASSWORD`
3. Under "Environment variables", add PROD-specific variables:
   - `AZURE_RG_NAME` = `pchp-attachments-prod-rg`
   - `AZURE_LOCATION` = `eastus`
   - `BASE_NAME` = `hipaa-attachments-prod`
   - `IA_NAME` = `prod-integration-account`
   - `SERVICE_BUS_NAME` = `hipaa-attachments-prod-sb`
   - `STORAGE_SKU` = `Standard_GRS`
   - `AZURE_CONNECTOR_LOCATION` = `eastus`
4. Protection rules:
   - ✅ **Deployment branches**: Select "Protected branches only"
5. Click **Save protection rules**

### Approval Workflow

#### UAT Deployment Approval

When a deployment to UAT is triggered (push to `release/*` branch):

1. **Workflow starts** and reaches `approval-gate` job
2. **GitHub sends notification** to configured reviewers
3. **Reviewers receive email/notification** with deployment details:
   - Branch name
   - Commit SHA
   - Triggered by (GitHub user)
   - Link to workflow run
4. **Reviewer clicks "Review pending deployments"**
5. **Reviewer reviews**:
   - Code changes (via commit link)
   - Branch name and author
   - Previous test results
6. **Reviewer approves or rejects**:
   - ✅ **Approve**: Deployment proceeds to UAT
   - ❌ **Reject**: Deployment is cancelled
7. **If approved**: Workflow continues with infrastructure and Logic App deployment
8. **If rejected**: Workflow stops, no changes deployed

#### PROD Deployment Approval

When a deployment to PROD is triggered (manual workflow dispatch or push to main):

1. **Workflow starts** and reaches `approval-gate` job
2. **GitHub sends notification** to configured reviewers (typically 2-3 people)
3. **ALL required reviewers must approve** (if configured)
4. **Reviewer reviews**:
   - UAT test results
   - Change management ticket
   - Release notes
   - Stakeholder sign-off
5. **Reviewer approves or rejects**:
   - ✅ **Approve**: Deployment proceeds to PROD
   - ❌ **Reject**: Deployment is cancelled, issue must be investigated
6. **If approved**: Workflow continues with production deployment
7. **Post-deployment**: Health checks run automatically

### Best Practices

#### Reviewer Selection

**UAT Reviewers:**
- QA Team Lead
- Application Owner
- Senior Developer

**PROD Reviewers:**
- DevOps Manager
- Application Owner
- Compliance Officer
- Operations Manager

#### Approval Guidelines

Before approving a deployment, reviewers should verify:

**For UAT:**
- ✅ All PR checks passed
- ✅ Code review completed
- ✅ Unit tests pass
- ✅ DEV environment tested successfully
- ✅ No high-severity issues in PR

**For PROD:**
- ✅ UAT deployment successful
- ✅ UAT testing completed and signed off
- ✅ Change management ticket approved
- ✅ Rollback plan documented
- ✅ Deployment window scheduled
- ✅ Stakeholders notified
- ✅ No open critical bugs
- ✅ Backup verified

#### Emergency Deployments

For urgent production hotfixes:

1. Create emergency change ticket
2. Get expedited approval from on-call manager
3. Document reason for emergency deployment
4. Follow standard approval process (but expedited)
5. Conduct post-deployment review

### Monitoring Approvals

#### View Pending Approvals

1. Go to repository → **Actions** tab
2. Look for runs with "Waiting" status
3. Click on the run
4. You'll see "Review pending deployments" button

#### Approval History

1. Go to repository → **Settings** → **Environments**
2. Click on environment (e.g., PROD-approval)
3. Click **Deployment history**
4. View all deployments with approval status and reviewer names

### Troubleshooting

#### Issue: No reviewers are notified

**Solutions:**
1. Verify reviewers are added in Environment settings
2. Check reviewers have notification enabled in GitHub settings
3. Ensure reviewers have repository access
4. Check GitHub email delivery settings

#### Issue: Approval button not showing

**Solutions:**
1. Verify you are a configured reviewer for the environment
2. Check if deployment is waiting on a different gate
3. Refresh the page
4. Verify environment protection rules are saved

#### Issue: Deployment proceeds without approval

**Solutions:**
1. Check if "Required reviewers" is enabled for the environment
2. Verify environment name matches workflow YAML
3. Check if user has bypass permission (repository admins)
4. Review environment protection rule configuration

### Security Considerations

#### Access Control

- Limit reviewers to trusted team members
- Use teams instead of individuals for better management
- Regularly review and update reviewer list
- Audit approval logs monthly

#### Bypass Protection

Repository administrators can bypass environment protection rules. To prevent accidental bypass:

1. Limit admin access to repository
2. Use branch protection rules in addition to environment rules
3. Enable audit logging
4. Review deployment history regularly

### Example Approval Notification

When a reviewer receives an approval request:

```
📧 Pending deployments for {owner}/hipaa-attachments

Deployment to PROD-approval is waiting for your review

Details:
- Workflow: Deploy
- Branch: main
- Triggered by: john.doe
- Commit: abc1234 - "Fix HIPAA 275 processing issue"

Review deployment: [Review pending deployments]
```

Reviewer clicks the link and sees:
- Commit details
- Changed files
- Test results
- Option to Approve or Reject with comment

## Pre-Deployment Validation

**ALWAYS validate before deploying to any environment.**

### 1. Validate JSON Workflows

```bash
cd /path/to/hipaa-attachments

# Validate all workflow.json files
WF_PATH="logicapps/workflows"
failed=0

find "$WF_PATH" -type f -name "workflow.json" -print0 | \
while IFS= read -r -d '' f; do
  echo "Checking $f"
  
  # Check JSON syntax
  if ! jq . "$f" >/dev/null 2>&1; then
    echo "ERROR: Invalid JSON in $f"
    failed=1
    continue
  fi
  
  # Check required keys
  if ! jq -e 'has("definition") and has("kind") and has("parameters")' "$f" >/dev/null; then
    echo "ERROR: Missing required keys in $f"
    failed=1
    continue
  fi
  
  # Verify kind is Stateful
  if ! jq -e '.kind == "Stateful"' "$f" >/dev/null; then
    echo "WARNING: Workflow kind is not Stateful in $f"
  fi
  
  echo "✓ Valid: $f"
done

if [ $failed -eq 0 ]; then
  echo "✅ All workflow files validated successfully"
else
  echo "❌ Validation failed"
  exit 1
fi
```

**Expected Output:**
```
Checking logicapps/workflows/ingest275/workflow.json
✓ Valid: logicapps/workflows/ingest275/workflow.json
Checking logicapps/workflows/ingest278/workflow.json
✓ Valid: logicapps/workflows/ingest278/workflow.json
Checking logicapps/workflows/replay278/workflow.json
✓ Valid: logicapps/workflows/replay278/workflow.json
Checking logicapps/workflows/rfai277/workflow.json
✓ Valid: logicapps/workflows/rfai277/workflow.json
✅ All workflow files validated successfully
```

### 2. Validate Bicep Templates

```bash
# Compile main infrastructure template
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# Check output file was created
if [ -s /tmp/arm.json ]; then
  echo "✅ Bicep validation successful"
  echo "ARM template size: $(wc -c < /tmp/arm.json) bytes"
else
  echo "❌ Bicep validation failed"
  exit 1
fi
```

**Expected Output:**
```
✅ Bicep validation successful
ARM template size: 4523 bytes
```

**Expected Warnings (safe to ignore):**
```
Warning use-parent-property: Use a reference to the parent resource instead of repeating name/type
```

### 3. Validate PowerShell Scripts

```bash
# Validate all PowerShell scripts
for script in *.ps1; do
  echo "Validating $script"
  pwsh -Command "Get-Content './$script' | Out-Null"
  if [ $? -eq 0 ]; then
    echo "✓ Valid: $script"
  else
    echo "✗ Invalid: $script"
    exit 1
  fi
done

echo "✅ All PowerShell scripts validated"
```

### 4. Create Workflow Package

```bash
# Create deployment ZIP
cd logicapps
zip -r ../workflows.zip workflows/

# Verify ZIP structure
echo "Verifying ZIP structure..."
unzip -l ../workflows.zip | grep workflow.json

# Expected to see 4 workflow.json files
WORKFLOW_COUNT=$(unzip -l ../workflows.zip | grep -c workflow.json)
if [ "$WORKFLOW_COUNT" -eq 4 ]; then
  echo "✅ Workflow package validated (4 workflows)"
else
  echo "❌ Workflow package invalid (expected 4, found $WORKFLOW_COUNT)"
  exit 1
fi

cd ..
```

**Expected Output:**
```
  adding: workflows/ (stored 0%)
  adding: workflows/ingest275/ (stored 0%)
  adding: workflows/ingest275/workflow.json (deflated 85%)
  adding: workflows/ingest278/ (stored 0%)
  adding: workflows/ingest278/workflow.json (deflated 84%)
  adding: workflows/replay278/ (stored 0%)
  adding: workflows/replay278/workflow.json (deflated 82%)
  adding: workflows/rfai277/ (stored 0%)
  adding: workflows/rfai277/workflow.json (deflated 83%)
✅ Workflow package validated (4 workflows)
```

### 5. Run Repository Structure Check

```bash
# Normalize repository structure
pwsh -c "./fix_repo_structure.ps1 -RepoRoot ."

# Verify output
echo "✅ Repository structure validated"
```

## Environment Deployment

### DEV Environment

**Trigger:** Manual workflow dispatch or push to `main` branch

**Configuration:**
- Resource Group: `pchp-attachments-dev-rg`
- Base Name: `hipaa-attachments-dev`
- Location: `eastus`
- Branch: `main`

#### Manual Deployment (CLI)

```bash
# Set variables
RG_NAME="pchp-attachments-dev-rg"
LOCATION="eastus"
BASE_NAME="hipaa-attachments-dev"

# Login to Azure
az login

# Set subscription
az account set --subscription "$AZURE_SUBSCRIPTION_ID_DEV"

# Create resource group
az group create \
  --name "$RG_NAME" \
  --location "$LOCATION"

# Deploy infrastructure
az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file infra/main.bicep \
  --parameters baseName="$BASE_NAME" \
  --parameters location="$LOCATION" \
  --verbose

# Get Logic App name
LOGIC_APP_NAME="${BASE_NAME}-la"

# Deploy workflows
az webapp deploy \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --src-path workflows.zip \
  --type zip

# Restart Logic App
az webapp restart \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"

echo "✅ DEV deployment complete"
```

#### GitHub Actions Deployment

1. Go to GitHub → Actions → "Deploy DEV Environment"
2. Click "Run workflow"
3. Select branch: `main`
4. Provide parameters (or use defaults)
5. Click "Run workflow"

**Monitor deployment:**
- Check GitHub Actions logs for progress
- Estimated time: 5-10 minutes

### UAT Environment

**Trigger:** Automatic on push to `release/*` branches

**Configuration:**
- Resource Group: `pchp-attachments-uat-rg`
- Base Name: `hipaa-attachments-uat`
- Location: `eastus`
- Branch: `release/*`

#### Automatic Deployment

```bash
# Create release branch
git checkout -b release/v1.0.0

# Make final changes if needed
git add .
git commit -m "Prepare v1.0.0 release"

# Push to trigger UAT deployment
git push origin release/v1.0.0
```

**The UAT deployment workflow automatically:**
1. Validates JSON workflows and Bicep templates
2. Runs ARM What-If analysis
3. Deploys infrastructure via Bicep
4. Packages and deploys Logic App workflows
5. Restarts Logic App
6. Performs health checks

**Monitor deployment:**
- GitHub Actions tab shows workflow progress
- Estimated time: 10-15 minutes

#### Manual UAT Deployment (if needed)

```bash
RG_NAME="pchp-attachments-uat-rg"
LOCATION="eastus"
BASE_NAME="hipaa-attachments-uat"

az group create --name "$RG_NAME" --location "$LOCATION"

az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file infra/main.bicep \
  --parameters baseName="$BASE_NAME" \
  --parameters location="$LOCATION"

LOGIC_APP_NAME="${BASE_NAME}-la"

az webapp deploy \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --src-path workflows.zip \
  --type zip

az webapp restart \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"
```

### PROD Environment

**Trigger:** Manual workflow dispatch only (requires approval)

**Configuration:**
- Resource Group: `pchp-attachments-prod-rg`
- Base Name: `hipaa-attachments-prod`
- Location: `eastus`
- Branch: `main` (after UAT validation)

#### Production Deployment Checklist

Before deploying to production:

- [ ] UAT deployment successful and tested
- [ ] All validation checks pass
- [ ] Security review completed
- [ ] Change management ticket approved
- [ ] Backup of current production verified
- [ ] Rollback plan documented
- [ ] Stakeholders notified of deployment window
- [ ] Maintenance window scheduled (if needed)

#### GitHub Actions Deployment

1. Ensure all UAT tests pass
2. Merge release branch to main (if applicable)
3. Go to GitHub → Actions → "Deploy PROD Environment"
4. Click "Run workflow"
5. Select branch: `main`
6. Review deployment plan
7. Click "Run workflow"
8. **Approval required** - designated approvers will review
9. Monitor deployment progress

**Production deployment includes:**
- Pre-deployment health check
- ARM What-If analysis review
- Infrastructure deployment
- Workflow deployment with zero downtime
- Post-deployment verification
- Automated health checks

#### Manual PROD Deployment

```bash
RG_NAME="pchp-attachments-prod-rg"
LOCATION="eastus"
BASE_NAME="hipaa-attachments-prod"

# IMPORTANT: Review What-If before deploying
az deployment group what-if \
  --resource-group "$RG_NAME" \
  --template-file infra/main.bicep \
  --parameters baseName="$BASE_NAME" \
  --parameters location="$LOCATION"

# After reviewing What-If output, proceed with deployment
read -p "Proceed with deployment? (yes/no): " confirm
if [ "$confirm" != "yes" ]; then
  echo "Deployment cancelled"
  exit 0
fi

az group create --name "$RG_NAME" --location "$LOCATION"

az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file infra/main.bicep \
  --parameters baseName="$BASE_NAME" \
  --parameters location="$LOCATION"

LOGIC_APP_NAME="${BASE_NAME}-la"

az webapp deploy \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --src-path workflows.zip \
  --type zip

az webapp restart \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"

echo "✅ PROD deployment complete"
```

## Post-Deployment Configuration

After successful deployment, complete these manual configuration steps:

### 1. Configure API Connections

Logic Apps require API connections to be authenticated:

```bash
# Navigate to Logic App in Azure Portal
# URL format:
# https://portal.azure.com/#@/resource/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{logic-app}/logicApp
```

**Configure these connections:**

#### SFTP-SSH Connection
1. Go to Logic App → Development Tools → API connections
2. Click "sftp-ssh" connection
3. Configure:
   - **Host**: Availity SFTP hostname
   - **Port**: 22
   - **Username**: Service account username
   - **SSH Private Key**: Upload private key file
   - **Accept any SSH host key**: False
4. Test connection
5. Save

#### Azure Blob Storage Connection
1. Click "azureblob" connection
2. Configure:
   - **Authentication Type**: Managed Identity
   - **Storage Account Name**: `{baseName}storage`
3. Test connection
4. Save

#### Service Bus Connection
1. Click "servicebus" connection
2. Configure:
   - **Authentication Type**: Managed Identity
   - **Namespace**: `{baseName}-svc`
3. Test connection
4. Save

#### Integration Account Connection
1. Click "integrationaccount" connection
2. Configure:
   - **Integration Account**: `{baseName}-ia`
3. Test connection
4. Save

### 2. Set Up Integration Account

#### Upload X12 Schemas

```bash
# Download HIPAA X12 schemas (if not already available)
# - X12_005010X210_275.xsd (Attachment Request)
# - X12_005010X212_277.xsd (Status Notification)
# - X12_005010X217_278.xsd (Services Review)
```

**Via Azure Portal:**
1. Navigate to Integration Account → Schemas
2. Click "+ Add"
3. Upload schema files:
   - `X12_005010X210_275.xsd`
   - `X12_005010X212_277.xsd`
   - `X12_005010X217_278.xsd`
4. Verify schemas uploaded successfully

**Via Azure CLI:**
```bash
RG_NAME="pchp-attachments-uat-rg"
IA_NAME="hipaa-attachments-uat-ia"

az logic integration-account schema create \
  --resource-group "$RG_NAME" \
  --integration-account-name "$IA_NAME" \
  --schema-name "X12_005010X210_275" \
  --schema-type "Xml" \
  --content @schemas/X12_005010X210_275.xsd
```

#### Configure Trading Partners

**Via Azure Portal:**
1. Navigate to Integration Account → Partners
2. Add Availity partner:
   - Name: `Availity`
   - Qualifier: `ZZ`
   - Value: `030240928`
3. Add PCHP partner:
   - Name: `PCHP-QNXT`
   - Qualifier: `ZZ`
   - Value: `66917`

**Via PowerShell:**
```powershell
# Use provided script
pwsh -c "./configure-hipaa-trading-partners.ps1 -ResourceGroup 'pchp-attachments-uat-rg' -IntegrationAccountName 'hipaa-attachments-uat-ia'"
```

#### Create X12 Agreements

**1. Availity-to-PCHP-275-Receive Agreement:**
1. Navigate to Integration Account → Agreements
2. Click "+ Add"
3. Configure:
   - **Name**: `Availity-to-PCHP-275-Receive`
   - **Agreement Type**: X12
   - **Host Partner**: PCHP-QNXT
   - **Host Identity**: Qualifier=ZZ, Value=66917
   - **Guest Partner**: Availity
   - **Guest Identity**: Qualifier=ZZ, Value=030240928
4. Receive Settings:
   - Schema: X12_005010X210_275
   - Transaction Set: 275
   - Version: 005010X210
5. Save

**2. PCHP-to-Availity-277-Send Agreement:**
1. Click "+ Add"
2. Configure:
   - **Name**: `PCHP-to-Availity-277-Send`
   - **Agreement Type**: X12
   - **Host Partner**: PCHP-QNXT
   - **Host Identity**: Qualifier=ZZ, Value=66917
   - **Guest Partner**: Availity
   - **Guest Identity**: Qualifier=ZZ, Value=030240928
3. Send Settings:
   - Schema: X12_005010X212_277
   - Transaction Set: 277
   - Version: 005010X212
4. Save

**3. PCHP-278-Processing Agreement:**
1. Click "+ Add"
2. Configure:
   - **Name**: `PCHP-278-Processing`
   - **Agreement Type**: X12
   - **Host Partner**: PCHP-QNXT
   - **Host Identity**: Qualifier=ZZ, Value=66917
   - **Guest Partner**: PCHP-QNXT (internal)
   - **Guest Identity**: Qualifier=ZZ, Value=66917
3. Receive Settings:
   - Schema: X12_005010X217_278
   - Transaction Set: 278
   - Version: 005010X217
4. Save

**Via PowerShell:**
```powershell
pwsh -c "./configure-x12-agreements.ps1 -ResourceGroup 'pchp-attachments-uat-rg' -IntegrationAccountName 'hipaa-attachments-uat-ia'"
```

### 3. Configure Logic App Parameters

Update workflow parameters via Azure Portal or ARM template:

**Via Portal:**
1. Navigate to Logic App → Configuration → Application settings
2. Add/update these settings:

```
sftp_inbound_folder=/inbound/attachments
blob_raw_folder=hipaa-attachments/raw/275
blob_raw_folder_278=hipaa-attachments/raw/278
sb_topic=attachments-in
sb_topic_rfai=rfai-requests
sb_topic_edi278=edi-278
qnxt_base_url=https://qnxt-api-uat.example.com
x12_sender_id_availity=030240928
x12_receiver_id_pchp=66917
x12_messagetype_275=X12_005010X210_275
x12_messagetype_277=X12_005010X212_277
x12_messagetype_278=X12_005010X217_278
```

3. Save and restart Logic App

### 4. Assign Managed Identity Permissions

Grant Logic App managed identity access to resources:

```bash
RG_NAME="pchp-attachments-uat-rg"
BASE_NAME="hipaa-attachments-uat"
LOGIC_APP_NAME="${BASE_NAME}-la"

# Get Logic App managed identity principal ID
PRINCIPAL_ID=$(az webapp identity show \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --query principalId -o tsv)

echo "Logic App Principal ID: $PRINCIPAL_ID"

# Assign Storage Blob Data Contributor role
STORAGE_ACCOUNT="${BASE_NAME}storage"
STORAGE_ID=$(az storage account show \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RG_NAME" \
  --query id -o tsv)

az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Storage Blob Data Contributor" \
  --scope "$STORAGE_ID"

echo "✓ Storage Blob Data Contributor role assigned"

# Assign Service Bus Data Sender role
SB_NAMESPACE="${BASE_NAME}-svc"
SB_ID=$(az servicebus namespace show \
  --name "$SB_NAMESPACE" \
  --resource-group "$RG_NAME" \
  --query id -o tsv)

az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Azure Service Bus Data Sender" \
  --scope "$SB_ID"

echo "✓ Azure Service Bus Data Sender role assigned"

# Verify role assignments
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --output table

echo "✅ All role assignments complete"
```

### 5. Configure Replay278 Endpoint

The replay278 workflow provides an HTTP endpoint for transaction replay:

1. Navigate to Logic App → Workflows → replay278
2. Click on workflow to open
3. Go to "Trigger history" or "Overview"
4. Copy the HTTP POST URL (looks like):
   ```
   https://{logic-app-name}.azurewebsites.net/api/replay278/triggers/HTTP_Replay_278_Request/invoke?api-version=...&sig=...
   ```
5. **IMPORTANT:** Configure authentication:
   - Add API key or OAuth authentication
   - Do NOT leave anonymous in production
6. Test endpoint:
   ```bash
   curl -X POST "https://{url}" \
     -H "Content-Type: application/json" \
     -d '{
       "blobUrl": "hipaa-attachments/raw/278/2024/01/15/test.edi",
       "fileName": "replay-test"
     }'
   ```

### 6. Verify Service Bus Topics

```bash
# List topics
az servicebus topic list \
  --resource-group "$RG_NAME" \
  --namespace-name "$SB_NAMESPACE" \
  --output table

# Expected topics:
# - attachments-in
# - rfai-requests
# - edi-278

# Check topic properties
az servicebus topic show \
  --resource-group "$RG_NAME" \
  --namespace-name "$SB_NAMESPACE" \
  --name "attachments-in"
```

## Verification and Testing

### Post-Deployment Health Checks

Run these checks after deployment:

#### 1. Verify Infrastructure Resources

```bash
RG_NAME="pchp-attachments-uat-rg"

# List all resources
az resource list \
  --resource-group "$RG_NAME" \
  --output table

# Expected resources:
# - Logic App Standard
# - Storage Account (Data Lake Gen2)
# - Service Bus Namespace
# - Integration Account
# - Application Insights
```

#### 2. Verify Logic App Status

```bash
LOGIC_APP_NAME="hipaa-attachments-uat-la"

# Get Logic App status
az webapp show \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --query "{name:name, state:state, url:defaultHostName}" \
  --output table

# Expected: state=Running
```

#### 3. Test Workflows

```powershell
# Run test suite
pwsh -c "./test-workflows.ps1 -TestInbound275 -ResourceGroup '$RG_NAME' -LogicAppName '$LOGIC_APP_NAME'"

# Test 278 processing
pwsh -c "./test-workflows.ps1 -TestInbound278 -ResourceGroup '$RG_NAME' -LogicAppName '$LOGIC_APP_NAME'"
```

#### 4. Verify Application Insights

1. Navigate to Application Insights resource
2. Go to "Live Metrics"
3. Trigger a test workflow
4. Verify telemetry is being received
5. Check for errors or warnings

#### 5. Test API Connections

```bash
# Check API connection status
az resource list \
  --resource-group "$RG_NAME" \
  --resource-type "Microsoft.Web/connections" \
  --query "[].{name:name, status:properties.statuses[0].status}" \
  --output table

# All connections should show: Connected
```

### Functional Testing

#### Test 275 Ingestion

1. Upload test file to SFTP:
   ```bash
   sftp user@availity-sftp-host
   cd /inbound/attachments
   put test-x12-275-availity-to-pchp.edi
   exit
   ```

2. Monitor workflow execution:
   - Go to Logic App → Workflows → ingest275 → Runs
   - Check latest run status
   - Review run history details

3. Verify Data Lake storage:
   ```bash
   az storage blob list \
     --account-name "${BASE_NAME}storage" \
     --container-name "hipaa-attachments" \
     --prefix "raw/275/" \
     --output table
   ```

4. Check Service Bus message:
   ```bash
   # View messages in topic (requires Service Bus Explorer or code)
   ```

#### Test 278 Replay

```bash
# Test replay endpoint
REPLAY_URL="https://${LOGIC_APP_NAME}.azurewebsites.net/api/replay278/triggers/HTTP_Replay_278_Request/invoke?..."

curl -X POST "$REPLAY_URL" \
  -H "Content-Type: application/json" \
  -d '{
    "blobUrl": "hipaa-attachments/raw/278/2024/01/15/test.edi",
    "fileName": "replay-test-$(date +%s)"
  }'

# Expected response: 200 OK with success message
```

## Rollback Procedures

### Workflow Rollback

If a workflow deployment fails or causes issues:

#### Option 1: Redeploy Previous Version

```bash
# Get previous deployment
PREVIOUS_COMMIT="<previous-git-commit-sha>"

# Checkout previous version
git checkout "$PREVIOUS_COMMIT" -- logicapps/workflows/

# Package workflows
cd logicapps && zip -r ../workflows.zip workflows/ && cd ..

# Redeploy
az webapp deploy \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --src-path workflows.zip \
  --type zip

# Restart
az webapp restart \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"
```

#### Option 2: Disable Problem Workflow

```bash
# Navigate to Azure Portal → Logic App → Workflows
# Select problematic workflow
# Click "Disable"
# This prevents the workflow from triggering while you investigate
```

### Infrastructure Rollback

If infrastructure deployment causes issues:

#### Option 1: Redeploy Previous Template

```bash
# Checkout previous Bicep template
git checkout "$PREVIOUS_COMMIT" -- infra/main.bicep

# Review changes
az deployment group what-if \
  --resource-group "$RG_NAME" \
  --template-file infra/main.bicep \
  --parameters baseName="$BASE_NAME"

# Deploy previous version
az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file infra/main.bicep \
  --parameters baseName="$BASE_NAME"
```

#### Option 2: Delete and Recreate Resource Group (DRASTIC)

**WARNING: This deletes ALL resources. Only use in non-production or as last resort.**

```bash
# Export data first (if needed)
# ...

# Delete resource group
az group delete --name "$RG_NAME" --yes

# Wait for deletion to complete

# Redeploy from known good state
git checkout "$KNOWN_GOOD_COMMIT"

# Run full deployment
# (follow deployment steps above)
```

## Troubleshooting

### Common Deployment Issues

#### Issue: Bicep Deployment Fails

**Symptoms:**
```
ERROR: The template deployment failed with error: 'The resource operation completed with terminal provisioning state 'Failed'.'
```

**Solutions:**
1. Check Azure Activity Log for detailed error
2. Verify all parameters are correct
3. Ensure subscription has available quota
4. Check for resource name conflicts
5. Review deployment logs in Azure Portal

**Detailed Diagnostics:**
```bash
# Get deployment details
az deployment group show \
  --resource-group "$RG_NAME" \
  --name "<deployment-name>" \
  --query "properties.error"

# Check activity log
az monitor activity-log list \
  --resource-group "$RG_NAME" \
  --start-time $(date -u -d '1 hour ago' '+%Y-%m-%dT%H:%M:%SZ') \
  --query "[?level=='Error']"
```

#### Issue: Logic App Deployment Fails

**Symptoms:**
- Workflow ZIP upload fails
- Logic App won't restart
- Workflows don't appear

**Solutions:**
1. Verify ZIP structure (must have `workflows/` at root)
2. Check Logic App is in Running state
3. Ensure ZIP file size is reasonable (<50MB)
4. Review deployment logs in Kudu (https://{logic-app}.scm.azurewebsites.net)

**Verify ZIP:**
```bash
unzip -l workflows.zip | head -20
# Should show: workflows/ingest275/workflow.json, etc.
```

#### Issue: API Connections Not Working

**Symptoms:**
- Workflows fail with "Unauthorized" errors
- Connections show "Needs Authentication"

**Solutions:**
1. Navigate to API connections in portal
2. Re-authenticate each connection
3. Verify managed identity has required permissions
4. Check connection settings match resources

**Test Connection:**
```bash
# Check connection status
az resource show \
  --ids "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/connections/{connection-name}" \
  --query "properties.statuses[0]"
```

#### Issue: OIDC Authentication Fails

**Symptoms:**
```
ERROR: AADSTS700016: Application with identifier 'xxx' was not found
```

**Solutions:**
1. Verify federated credential is created
2. Check subject matches GitHub repo/branch
3. Ensure service principal has Contributor role
4. Verify tenant ID and subscription ID are correct

**Verify OIDC Setup:**
```bash
# List federated credentials
az ad app federated-credential list --id "$APP_ID"

# Check service principal
az ad sp show --id "$APP_ID"

# Verify role assignments
az role assignment list --assignee "$APP_ID"
```

#### Issue: Workflows Not Triggering

**Symptoms:**
- No runs showing in Logic App
- Files uploaded but not processed

**Solutions:**
1. Check trigger configuration (SFTP path, polling interval)
2. Verify API connections are authenticated
3. Check Logic App is running (not stopped)
4. Review Application Insights for errors
5. Verify SFTP credentials and path

**Debug Triggers:**
```bash
# Enable verbose logging
az webapp log config \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --application-logging true \
  --level verbose

# Tail logs
az webapp log tail \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"
```

### Getting Help

If issues persist:

1. Review [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for detailed solutions
2. Check Application Insights for errors and exceptions
3. Review Azure Activity Log for resource-level errors
4. Check GitHub Actions logs for CI/CD issues
5. Open a support ticket with:
   - Environment (DEV/UAT/PROD)
   - Error messages and logs
   - Steps to reproduce
   - What you've already tried

---

For architecture details, see [ARCHITECTURE.md](ARCHITECTURE.md)  
For development workflow, see [CONTRIBUTING.md](CONTRIBUTING.md)  
For security guidelines, see [SECURITY.md](SECURITY.md)
