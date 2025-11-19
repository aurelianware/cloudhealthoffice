# Deployment Guide

This guide provides step-by-step instructions for deploying the HIPAA Attachments system to Azure environments (DEV/UAT/PROD).

## 🚀 Quick Start

**New to deployment?** Start here:
1. **GitHub Actions Setup**: See [GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md) for complete OIDC authentication and secrets configuration
2. **Secrets & Environment Configuration**: See [DEPLOYMENT-SECRETS-SETUP.md](DEPLOYMENT-SECRETS-SETUP.md) for detailed secrets setup and validation
3. **Validate Prerequisites**: Ensure all tools are installed (see [Prerequisites](#prerequisites))
4. **Follow Environment Deployment**: Choose your target environment (DEV/UAT/PROD)
5. **Run Post-Deployment Steps**: Configure API connections and Integration Account

## Table of Contents
- [Prerequisites](#prerequisites)
- [GitHub Configuration](#github-configuration)
- [Bicep Compilation and ARM Deployment](#bicep-compilation-and-arm-deployment)
- [Environment Protection Rules and Approval Gates](#environment-protection-rules-and-approval-gates)
- [Pre-Deployment Validation](#pre-deployment-validation)
- [Environment Deployment](#environment-deployment)
- [Logic App Workflow Deployment](#logic-app-workflow-deployment)
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

**📘 For complete GitHub Actions setup including OIDC authentication, secrets, and variables, see [GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md).**

This section provides a quick reference. For detailed instructions, federated credentials setup, and troubleshooting, refer to the comprehensive guide.

### Repository Secrets (Quick Reference)

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

**Need help?** See [GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md#github-secrets-configuration) for:
- Step-by-step instructions with screenshots
- GitHub CLI automation scripts
- Verification procedures
- Troubleshooting common issues

### Repository Variables (Quick Reference)

Configure environment-specific variables:

```
AZURE_REGION_DEV   = eastus
AZURE_REGION_UAT   = eastus
AZURE_REGION_PROD  = eastus

BASE_NAME_DEV      = hipaa-attachments-dev
BASE_NAME_UAT      = hipaa-attachments-uat
BASE_NAME_PROD     = hipaa-attachments-prod
```

**For complete variable setup**, see [GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md#github-variables-configuration).

## Bicep Compilation and ARM Deployment

This section covers the complete Bicep-to-ARM deployment workflow used by the GitHub Actions pipelines.

### Bicep Template Structure

The infrastructure is defined in `infra/main.bicep` which creates:

**Core Resources:**
- Azure Storage Account (Data Lake Gen2 with hierarchical namespace)
- Service Bus Namespace (Standard tier)
  - Topics: `attachments-in`, `rfai-requests`, `edi-278`, `appeals-auth`, `auth-statuses`, `dead-letter`
- Logic App Standard (WS1 SKU with Function App runtime)
- Application Insights (monitoring and telemetry)
- Integration Account (X12 processing)

**Managed API Connections:**
- SFTP with SSH (`sftpwithssh` connector)
- Azure Blob Storage (`azureblob` connector)
- Service Bus (`servicebus` connector)
- Integration Account X12 (`x12` connector)

### Bicep Compilation Process

#### Step 1: Install Bicep CLI

```bash
# Install or update Bicep CLI
az bicep install

# Verify version (minimum 0.37.0 required)
az bicep version
```

**Expected output:**
```
Bicep CLI version 0.37.4 (...)
```

#### Step 2: Compile Bicep to ARM Template

```bash
# Compile main infrastructure template
az bicep build \
  --file infra/main.bicep \
  --outfile /tmp/arm-template.json

# Check compilation success
if [ -s /tmp/arm-template.json ]; then
  echo "✓ Bicep compilation successful"
  echo "ARM template size: $(wc -c < /tmp/arm-template.json) bytes"
else
  echo "✗ Bicep compilation failed"
  exit 1
fi
```

**Expected output:**
```
✓ Bicep compilation successful
ARM template size: 14237 bytes
```

**Common warnings (safe to ignore):**
```
Warning use-parent-property: Use a reference to the parent resource instead of repeating name/type
  → This warning appears for Service Bus topics and is cosmetic only
  → Does not affect deployment or runtime behavior
```

#### Step 3: Validate ARM Template Syntax

```bash
# Validate template structure
az deployment group validate \
  --resource-group <resource-group-name> \
  --template-file infra/main.bicep \
  --parameters baseName=<base-name> \
               location=<azure-region> \
               sftpHost=<sftp-host> \
               sftpUsername=<username> \
               sftpPassword=<password> \
               serviceBusName=<service-bus-name> \
               iaName=<integration-account-name> \
               connectorLocation=<connector-region>
```

**Note**: This command requires authentication and validates against Azure API schemas, but does not deploy resources.

### ARM What-If Analysis

ARM What-If provides a **preview of changes** before actual deployment.

#### Purpose

- Shows what resources will be **created**, **modified**, or **deleted**
- Identifies configuration changes
- Helps prevent accidental resource deletions
- Required for production deployments (best practice)

#### Running What-If Analysis

```bash
# Run What-If analysis
az deployment group what-if \
  --resource-group <resource-group-name> \
  --template-file infra/main.bicep \
  --parameters baseName=<base-name> \
               location=<azure-region> \
               sftpHost=<sftp-host> \
               sftpUsername=<username> \
               sftpPassword=<password> \
               serviceBusName=<service-bus-name> \
               iaName=<integration-account-name> \
               connectorLocation=<connector-region> \
  --no-pretty-print
```

#### Interpreting What-If Output

**Resource will be created** (first deployment):
```
+ Resource Microsoft.Storage/storageAccounts
  Location: eastus
  SKU: Standard_LRS
```

**Resource will be modified** (configuration change):
```
~ Resource Microsoft.Web/sites 'hipaa-attachments-la'
  - properties.siteConfig.appSettings[0].value: "old-value"
  + properties.siteConfig.appSettings[0].value: "new-value"
```

**Resource will be deleted** (⚠️ WARNING):
```
- Resource Microsoft.Web/connections 'old-connection'
```

**No changes detected**:
```
Resource changes: 0 to create, 0 to modify, 0 to delete
```

#### What-If Best Practices

✅ **Always run What-If before production deployments**  
✅ **Review changes carefully**, especially deletions  
✅ **Save What-If output** for deployment records  
✅ **Use `--no-pretty-print`** for CI/CD logging  
❌ **Never skip What-If for PROD** environments  

### ARM Template Deployment

#### Deployment Methods

**Method 1: Azure CLI Direct Deployment**

```bash
# Deploy infrastructure
az deployment group create \
  --resource-group <resource-group-name> \
  --template-file infra/main.bicep \
  --parameters baseName=<base-name> \
               location=<azure-region> \
               sftpHost=<sftp-host> \
               sftpUsername=<username> \
               sftpPassword=<password> \
               serviceBusName=<service-bus-name> \
               iaName=<integration-account-name> \
               connectorLocation=<connector-region> \
  --name "hipaa-infra-deployment-$(date +%Y%m%d-%H%M%S)" \
  --verbose
```

**Method 2: GitHub Actions with azure/arm-deploy**

```yaml
- name: Deploy Infrastructure
  uses: azure/arm-deploy@v2
  with:
    scope: resourcegroup
    resourceGroupName: ${{ env.RESOURCE_GROUP }}
    template: infra/main.bicep
    parameters: >
      baseName=${{ env.BASE_NAME }}
      location=${{ env.LOCATION }}
      sftpHost=${{ secrets.SFTP_HOST }}
      sftpUsername=${{ secrets.SFTP_USERNAME }}
      sftpPassword=${{ secrets.SFTP_PASSWORD }}
      serviceBusName=${{ env.SERVICE_BUS_NAME }}
      iaName=${{ env.IA_NAME }}
      connectorLocation=${{ env.CONNECTOR_LOCATION }}
    deploymentName: hipaa-infra-${{ github.run_number }}
    failOnStdErr: true
```

#### Deployment Parameters

| Parameter | Required | Description | Example |
|-----------|----------|-------------|---------|
| `baseName` | Yes | Resource name prefix | `hipaa-attachments-prod` |
| `location` | Yes | Azure region for core resources | `eastus` |
| `connectorLocation` | Yes | Region for API connections | `eastus` |
| `serviceBusName` | Yes | Service Bus namespace name | `hipaa-attachments-prod-svc` |
| `iaName` | Yes | Integration Account name | `prod-integration-account` |
| `sftpHost` | Yes | SFTP server hostname | `sftp.availity.com` |
| `sftpUsername` | Yes | SFTP username | `service-account` |
| `sftpPassword` | Yes (secure) | SFTP password | `<secret-value>` |
| `storageSku` | No | Storage account SKU | `Standard_LRS` (default) |
| `iaSku` | No | Integration Account SKU | `Free` (default) |
| `useExistingIa` | No | Use existing Integration Account | `false` (default) |
| `enableB2B` | No | Enable X12 connector | `true` (default) |

**🔒 Security Note**: Always pass sensitive parameters as secrets, never hardcode in templates or commit to version control.

#### Monitoring Deployment Progress

```bash
# List active deployments
az deployment group list \
  --resource-group <resource-group-name> \
  --query "[].{Name:name, State:properties.provisioningState, Timestamp:properties.timestamp}" \
  --output table

# Show deployment details
az deployment group show \
  --resource-group <resource-group-name> \
  --name <deployment-name> \
  --output jsonc
```

#### Deployment Timeline

| Stage | Estimated Time | Description |
|-------|----------------|-------------|
| **Validation** | 5-10 seconds | Template syntax and parameter validation |
| **What-If Analysis** | 10-20 seconds | Calculate deployment changes |
| **Resource Creation** | 5-10 minutes | Deploy Azure resources |
| **Connection Setup** | 1-2 minutes | Configure API connections |
| **Total** | **6-13 minutes** | Complete infrastructure deployment |

### Handling Deployment Failures

#### View Failed Operations

```bash
# List failed operations in deployment
DEPLOY_NAME="hipaa-infra-deployment-20241116"
RG_NAME="pchp-attachments-prod-rg"

az deployment operation group list \
  --resource-group "$RG_NAME" \
  --name "$DEPLOY_NAME" \
  --query "[?properties.provisioningState=='Failed']" \
  --output table

# Show detailed error for specific operation
az deployment operation group show \
  --resource-group "$RG_NAME" \
  --name "$DEPLOY_NAME" \
  --operation-ids <operation-id> \
  --output jsonc
```

#### Common Deployment Errors

**Error: InvalidTemplateDeployment**
- **Cause**: Missing required parameter or invalid parameter value
- **Solution**: Check parameter names and types match template definition

**Error: ResourceQuotaExceeded**
- **Cause**: Subscription resource quota limit reached
- **Solution**: Request quota increase or delete unused resources

**Error: ResourceNameAlreadyExists**
- **Cause**: Resource name conflict (storage accounts must be globally unique)
- **Solution**: Use different `baseName` or delete existing conflicting resource

**Error: AuthorizationFailed**
- **Cause**: Insufficient permissions for deployment identity
- **Solution**: Grant Contributor role to service principal/managed identity

### Post-Deployment Verification

```bash
# List deployed resources
az resource list \
  --resource-group <resource-group-name> \
  --query "[].{Name:name, Type:type, State:provisioningState, Location:location}" \
  --output table

# Verify specific resources
az storage account show --name <storage-account> --query provisioningState
az servicebus namespace show --name <service-bus> --query provisioningState
az webapp show --name <logic-app> --query state
```

**Expected output (successful deployment):**
```
Name                          Type                                   State      Location
----------------------------  -------------------------------------  ---------  ----------
staging...                    Microsoft.Storage/storageAccounts      Succeeded  eastus
hipaa-attachments-prod-svc    Microsoft.ServiceBus/namespaces        Succeeded  eastus
hipaa-attachments-prod-la     Microsoft.Web/sites                    Succeeded  eastus
hipaa-attachments-prod-ai     Microsoft.Insights/components          Succeeded  eastus
prod-integration-account      Microsoft.Logic/integrationAccounts    Succeeded  eastus
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
- ✅ Security scans completed (TruffleHog, PII/PHI scan)
- ✅ No secrets or credentials in code
- ✅ Bicep validation passed

**For PROD:**
- ✅ UAT deployment successful
- ✅ UAT testing completed and signed off
- ✅ Change management ticket approved
- ✅ Rollback plan documented
- ✅ Deployment window scheduled
- ✅ Stakeholders notified
- ✅ No open critical bugs
- ✅ Backup verified
- ✅ Security scans passed (no critical vulnerabilities)
- ✅ ARM What-If analysis reviewed
- ✅ No unexpected resource deletions
- ✅ Compliance requirements met

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

### Deployment Audit Logging and Compliance

All deployments are automatically logged for compliance and audit purposes.

#### Automated Audit Trails

**GitHub Actions Audit:**
- Every deployment run is logged with:
  - Timestamp and duration
  - Triggered by (user/system)
  - Branch and commit SHA
  - Approval decisions with reviewer names
  - Deployment outcome (success/failure)
  - All job logs and outputs

**Access GitHub Deployment History:**
```bash
# Using GitHub CLI
gh run list --workflow=deploy.yml --limit 50

# View specific run details
gh run view <run-id> --log

# List all approvals
gh api /repos/{owner}/{repo}/actions/runs --jq '.workflow_runs[] | select(.conclusion != null) | {id: .id, status: .status, conclusion: .conclusion, created_at: .created_at, actor: .actor.login}'
```

**Azure Activity Log:**
```bash
# Query deployment activities
az monitor activity-log list \
  --resource-group "pchp-attachments-prod-rg" \
  --start-time "2024-01-01T00:00:00Z" \
  --query "[?contains(operationName.value, 'deployments')].{Time:eventTimestamp, Caller:caller, Operation:operationName.localizedValue, Status:status.localizedValue}" \
  --output table

# Query resource changes
az monitor activity-log list \
  --resource-group "pchp-attachments-prod-rg" \
  --start-time "2024-01-01T00:00:00Z" \
  --query "[?level=='Warning' || level=='Error'].{Time:eventTimestamp, Level:level, Operation:operationName.localizedValue, Status:status.localizedValue}" \
  --output table

# Export audit logs for compliance
az monitor activity-log list \
  --resource-group "pchp-attachments-prod-rg" \
  --start-time "2024-01-01T00:00:00Z" \
  --output json > deployment-audit-$(date +%Y%m%d).json
```

#### Deployment Metrics and Tracking

**Key Metrics to Monitor:**

1. **Approval Time**: Time from deployment trigger to approval
2. **Deployment Duration**: Time from approval to completion
3. **Deployment Frequency**: Number of deployments per environment per week
4. **Rollback Rate**: Percentage of deployments requiring rollback
5. **Approval Rejection Rate**: Percentage of rejected deployments

**Query Deployment Metrics:**
```bash
# GitHub CLI - Deployment frequency (last 30 days)
gh api /repos/{owner}/{repo}/actions/runs \
  --jq '.workflow_runs[] | select(.created_at > (now - 2592000 | strftime("%Y-%m-%dT%H:%M:%SZ"))) | {workflow: .name, status: .status, conclusion: .conclusion, created_at: .created_at}' | \
  jq -s 'group_by(.workflow) | map({workflow: .[0].workflow, count: length})'

# Average deployment duration
gh api /repos/{owner}/{repo}/actions/runs \
  --jq '.workflow_runs[] | select(.conclusion == "success") | {duration: (.updated_at | fromdateiso8601) - (.created_at | fromdateiso8601)}' | \
  jq -s 'add/length | . / 60 | "Average deployment time: \(.) minutes"'
```

**Application Insights - Deployment Correlation:**
```kusto
// Query deployments and correlate with errors
customEvents
| where timestamp > ago(30d)
| where name == "deployment_started" or name == "deployment_completed"
| extend 
    deploymentId = tostring(customDimensions["deploymentId"]),
    environment = tostring(customDimensions["environment"]),
    status = tostring(customDimensions["status"])
| summarize 
    DeploymentCount = count(),
    SuccessCount = countif(status == "success"),
    FailureCount = countif(status == "failed")
    by environment
| extend SuccessRate = (SuccessCount * 100.0) / DeploymentCount
```

#### Compliance Reporting

**Monthly Deployment Report:**

Create a monthly report including:
- Total deployments by environment
- Approval metrics (approved/rejected/time-to-approve)
- Rollback incidents and root causes
- Security scan results
- Incident response activities
- Change management tickets linked to deployments

**Generate Compliance Report:**
```bash
#!/bin/bash
# deployment-compliance-report.sh

MONTH=$(date -d "last month" +%Y-%m)
OUTPUT_DIR="compliance-reports"
mkdir -p "$OUTPUT_DIR"

echo "Generating deployment compliance report for $MONTH"

# GitHub deployment data
gh api "/repos/{owner}/{repo}/actions/runs?created=$MONTH-01..$MONTH-31" \
  --jq '.workflow_runs[] | {id, name, status, conclusion, created_at, actor: .actor.login}' \
  > "$OUTPUT_DIR/github-deployments-$MONTH.json"

# Azure activity logs
az monitor activity-log list \
  --resource-group "pchp-attachments-prod-rg" \
  --start-time "${MONTH}-01T00:00:00Z" \
  --end-time "${MONTH}-31T23:59:59Z" \
  --output json \
  > "$OUTPUT_DIR/azure-activity-$MONTH.json"

# Application Insights deployment events
az monitor app-insights query \
  --app "hipaa-attachments-prod-ai" \
  --analytics-query "customEvents | where timestamp between(datetime('${MONTH}-01') .. datetime('${MONTH}-31')) | where name startswith 'deployment'" \
  --output json \
  > "$OUTPUT_DIR/app-insights-deployments-$MONTH.json"

echo "✓ Compliance report generated in $OUTPUT_DIR/"
echo "  - GitHub deployments: github-deployments-$MONTH.json"
echo "  - Azure activity: azure-activity-$MONTH.json"
echo "  - App Insights: app-insights-deployments-$MONTH.json"
```

#### Audit Log Retention

**Retention Requirements:**
- **GitHub Actions logs**: 90 days (default), download for long-term storage
- **Azure Activity Logs**: 90 days (default), configure Log Analytics for extended retention
- **Application Insights**: 90 days (default), configure 365+ days for compliance
- **Deployment artifacts**: Retain indefinitely in artifact storage

**Configure Extended Retention:**
```bash
# Application Insights - Set 2 year retention
az monitor app-insights component update \
  --app "hipaa-attachments-prod-ai" \
  --resource-group "pchp-attachments-prod-rg" \
  --retention-time 730

# Log Analytics Workspace - Set 2 year retention
az monitor log-analytics workspace update \
  --resource-group "pchp-attachments-prod-rg" \
  --workspace-name "hipaa-logs-workspace" \
  --retention-time 730
```

### Communication and Notification Strategy

Effective communication is critical for successful gated deployments.

#### Stakeholder Notification Matrix

| Event | DEV | UAT | PROD | Notification Method |
|-------|-----|-----|------|---------------------|
| **Deployment Started** | DevOps team | QA team, DevOps | All stakeholders | Slack/Teams, GitHub |
| **Approval Needed** | N/A | UAT approvers | PROD approvers | Email, Slack/Teams |
| **Deployment Complete** | DevOps team | QA team, DevOps | All stakeholders | Slack/Teams |
| **Deployment Failed** | DevOps team | QA team, DevOps, Manager | All stakeholders, Exec | Email, Slack/Teams, SMS |
| **Rollback Initiated** | DevOps team | QA team, DevOps, Manager | All stakeholders, Exec | Email, Slack/Teams, SMS |

#### Pre-Deployment Communication Template

**UAT Deployment Notification:**
```markdown
Subject: UAT Deployment Scheduled - HIPAA Attachments Release X.Y.Z

Team,

A UAT deployment has been triggered and is awaiting approval.

**Details:**
- Release Version: X.Y.Z
- Triggered by: [Developer Name]
- Branch: release/vX.Y.Z
- Commit: [Short SHA] - [Commit Message]
- Scheduled Time: [Timestamp]

**Changes:**
- [Brief description of changes]
- [Link to release notes]
- [Link to PR]

**Approval Required:**
UAT approvers, please review and approve/reject the deployment:
[Link to GitHub Actions approval page]

**Testing Plan:**
After deployment, QA team will execute:
- [Test scenario 1]
- [Test scenario 2]
- [Test scenario 3]

Questions? Contact: [DevOps Team]
```

**PROD Deployment Notification:**
```markdown
Subject: PROD Deployment Scheduled - HIPAA Attachments Release X.Y.Z

Team,

A production deployment has been triggered and requires approval.

**Details:**
- Release Version: X.Y.Z
- Triggered by: [Release Manager]
- Branch: main
- Commit: [Short SHA] - [Commit Message]
- Deployment Window: [Start Time] - [End Time]

**Changes:**
[Detailed description of all changes included]

**UAT Validation:**
- UAT deployment: [Date/Time]
- UAT testing: Completed [Date]
- Issues found: [None / List of issues and resolutions]

**Approval Required:**
Production approvers must review and approve:
[Link to GitHub Actions approval page]

**Approval Checklist:**
- [ ] UAT testing completed successfully
- [ ] Change management ticket approved
- [ ] Rollback plan documented
- [ ] Stakeholders notified
- [ ] No critical open issues

**Rollback Plan:**
[Brief description of rollback procedure]
[Link to rollback documentation]

**Post-Deployment:**
- Health checks will run automatically
- Monitoring alerts configured
- On-call team notified

Questions? Contact: [Release Manager / DevOps Lead]
```

#### Post-Deployment Communication Template

**Successful Deployment:**
```markdown
Subject: ✅ [ENV] Deployment Complete - HIPAA Attachments Release X.Y.Z

Team,

The [ENV] deployment has completed successfully.

**Deployment Summary:**
- Release Version: X.Y.Z
- Environment: [UAT/PROD]
- Completed: [Timestamp]
- Duration: [X minutes]
- Approved by: [Approver Names]

**Resources Deployed:**
- Logic App: [Name]
- Workflows: [List]
- Infrastructure changes: [Summary]

**Health Check Results:**
✓ All health checks passed
✓ Logic App running
✓ Workflows enabled
✓ API connections active
✓ No errors in Application Insights

**Next Steps:**
[Environment-specific next steps]

**Monitoring:**
Application Insights: [Link]
Azure Portal: [Link]

Questions? Contact: [DevOps Team]
```

**Failed Deployment:**
```markdown
Subject: ⚠️ [ENV] Deployment FAILED - HIPAA Attachments Release X.Y.Z

Team,

The [ENV] deployment has failed. Rollback procedures have been initiated.

**Deployment Summary:**
- Release Version: X.Y.Z
- Environment: [UAT/PROD]
- Failed at: [Timestamp]
- Error: [Brief error description]

**Immediate Actions Taken:**
- [ ] Rollback initiated
- [ ] Incident created: [Incident ID]
- [ ] On-call team notified
- [ ] Previous version restored

**Impact:**
[Description of any service impact]

**Root Cause:**
[Under investigation / Known issue description]

**Resolution Plan:**
[Plan to fix and redeploy]

**Status:**
Current environment status: [Online/Degraded]
Expected resolution: [Timeframe]

For real-time updates: [Slack channel / Status page]

Questions? Contact: [Incident Commander / DevOps Lead]
```

#### Emergency Deployment Procedures

For critical hotfixes requiring expedited approval:

**Emergency Deployment Criteria:**
- Production system is down or severely degraded
- Security vulnerability requiring immediate patching
- Data integrity issue causing incorrect results
- HIPAA compliance violation

**Emergency Approval Process:**

1. **Initiate Emergency Deployment:**
   ```bash
   # Tag commit as emergency
   git tag -a emergency-vX.Y.Z-hotfix -m "Emergency: [Brief description]"
   git push origin emergency-vX.Y.Z-hotfix
   ```

2. **Notify Emergency Contacts:**
   - On-call DevOps Lead
   - Application Owner
   - Compliance Officer (if HIPAA-related)

3. **Expedited Approval:**
   - Requires 2 approvers from emergency contact list
   - Must document reason in approval comment
   - Maximum approval time: 30 minutes

4. **Post-Deployment:**
   - Immediate health check verification
   - Create post-mortem within 24 hours
   - Document lessons learned
   - Update runbooks if needed

**Emergency Contact List:**
```
Primary On-Call: [Name] - [Phone] - [Email]
Secondary On-Call: [Name] - [Phone] - [Email]
DevOps Manager: [Name] - [Phone] - [Email]
Application Owner: [Name] - [Phone] - [Email]
Compliance Officer: [Name] - [Phone] - [Email]
```

#### Integration with Ticketing Systems

**Link Deployments to Change Management:**

> **Note:** The following change ticket validation is a recommended enhancement not currently implemented in the workflows. Teams can add this validation as needed based on their change management requirements.

**Optional Workflow Enhancement:**

```yaml
# Example: Add to deployment workflow as a validation step
- name: Validate Change Ticket
  run: |
    TICKET_NUMBER="${{ github.event.head_commit.message }}" | grep -oP 'CHG\d+' || true
    if [ -z "$TICKET_NUMBER" ]; then
      echo "::error::No change ticket found in commit message"
      echo "Format: 'CHG12345: Description'"
      exit 1
    fi
    echo "Change ticket: $TICKET_NUMBER"
    # Optional: Validate ticket is approved
    # Call ticketing system API to verify status
```

**Recommended Commit Message Format:**
```
CHG12345: Deploy HIPAA 275 processing enhancements

- Added retry logic for QNXT API calls
- Updated X12 schema validation
- Fixed Service Bus connection handling

Approved-by: [Approver Name]
Tested-in: UAT
```

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

## Logic App Workflow Deployment

This section details the complete process for packaging and deploying Logic App workflows after infrastructure is in place.

### Workflow Package Structure

Logic App Standard requires workflows in a specific ZIP structure:

```
workflows.zip
└── workflows/
    ├── ingest275/
    │   └── workflow.json
    ├── ingest278/
    │   └── workflow.json
    ├── replay278/
    │   └── workflow.json
    ├── rfai277/
    │   └── workflow.json
    ├── process_appeals/
    │   └── workflow.json
    └── process_authorizations/
        └── workflow.json
```

**Critical Requirements:**
- ✅ Top-level directory must be named `workflows/`
- ✅ Each workflow in its own subdirectory
- ✅ Each subdirectory contains `workflow.json`
- ✅ All workflows must have `kind: "Stateful"` (requirement for Logic Apps Standard)
- ❌ Do NOT include `host.json`, `connections.json`, or `local.settings.json` from local development

### Creating Workflow Package

#### Method 1: Using Bash Script

```bash
# Navigate to logicapps directory
cd logicapps

# Create ZIP package with correct structure
zip -r ../workflows.zip workflows/

# Return to root
cd ..

# Verify package structure
echo "Verifying workflows.zip structure..."
unzip -l workflows.zip | head -30

# Count workflows (should see 6 workflow.json files)
WORKFLOW_COUNT=$(unzip -l workflows.zip | grep -c "workflow.json")
echo "Found $WORKFLOW_COUNT workflows"

if [ "$WORKFLOW_COUNT" -eq 6 ]; then
  echo "✅ Workflow package validated (6 workflows)"
else
  echo "⚠️  Expected 6 workflows, found $WORKFLOW_COUNT"
fi

# Check package size (should be ~15-20 KB)
ls -lh workflows.zip
```

**Expected output:**
```
Archive:  workflows.zip
  Length      Date    Time    Name
---------  ---------- -----   ----
        0  11-16-2024 14:23   workflows/
        0  11-16-2024 14:23   workflows/ingest275/
     5432  11-16-2024 14:23   workflows/ingest275/workflow.json
        0  11-16-2024 14:23   workflows/ingest278/
     4876  11-16-2024 14:23   workflows/ingest278/workflow.json
...
Found 6 workflows
✅ Workflow package validated (6 workflows)
-rw-r--r-- 1 user user 16K Nov 16 14:23 workflows.zip
```

#### Method 2: Using PowerShell

```powershell
# Create workflow package
$workflowPath = "logicapps/workflows"
$zipPath = "workflows.zip"

# Ensure we're in the right directory
Push-Location (Split-Path $workflowPath -Parent)

# Create ZIP
Compress-Archive -Path (Split-Path $workflowPath -Leaf) -DestinationPath "../$zipPath" -Force

Pop-Location

# Verify
if (Test-Path $zipPath) {
    Write-Host "✅ Workflow package created: $zipPath"
    Write-Host "Package size: $((Get-Item $zipPath).Length / 1KB) KB"
} else {
    Write-Error "❌ Failed to create workflow package"
    exit 1
}
```

### Deploying Workflows to Logic App

#### Prerequisites

Before deploying workflows:

- [ ] Infrastructure deployed successfully
- [ ] Logic App Standard is in "Running" state
- [ ] Logic App name is known (format: `{baseName}-la`)
- [ ] Resource group exists
- [ ] Azure CLI authenticated
- [ ] Workflow package (`workflows.zip`) created and validated

#### Deployment Process

**Step 1: Verify Logic App Status**

```bash
RG_NAME="pchp-attachments-prod-rg"
LOGIC_APP_NAME="hipaa-attachments-prod-la"

# Check Logic App state
STATUS=$(az webapp show \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --query "state" -o tsv)

echo "Logic App Status: $STATUS"

if [ "$STATUS" != "Running" ]; then
  echo "⚠️  Warning: Logic App is not running. Current state: $STATUS"
  echo "Attempting to start..."
  az webapp start --resource-group "$RG_NAME" --name "$LOGIC_APP_NAME"
  sleep 30
fi
```

**Step 2: Deploy Workflow Package**

```bash
# Deploy workflows ZIP to Logic App
az webapp deploy \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --src-path workflows.zip \
  --type zip \
  --async false

# Check deployment status
if [ $? -eq 0 ]; then
  echo "✅ Workflows deployed successfully"
else
  echo "❌ Workflow deployment failed"
  exit 1
fi
```

**Deployment Parameters:**
- `--src-path`: Path to workflows.zip file
- `--type zip`: Deployment type (must be "zip" for Logic Apps)
- `--async false`: Wait for deployment to complete (recommended)

**Step 3: Restart Logic App**

```bash
# Restart Logic App to ensure workflows are loaded
echo "Restarting Logic App to load new workflows..."
az webapp restart \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"

# Wait for restart
echo "Waiting 30 seconds for Logic App to restart..."
sleep 30

# Verify restart completed
STATUS=$(az webapp show \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --query "state" -o tsv)

echo "Logic App Status after restart: $STATUS"
```

**Step 4: Verify Workflow Deployment**

```bash
# List deployed workflows using REST API
SUBSCRIPTION_ID=$(az account show --query id -o tsv)

az rest --method GET \
  --uri "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG_NAME/providers/Microsoft.Web/sites/$LOGIC_APP_NAME/workflows?api-version=2022-03-01" \
  --query "value[].{Name:name, State:properties.state, Kind:kind}" \
  --output table

# Expected output:
# Name                       State      Kind
# -------------------------  ---------  ---------
# ingest275                  Enabled    Stateful
# ingest278                  Enabled    Stateful
# replay278                  Enabled    Stateful
# rfai277                    Enabled    Stateful
# process_appeals            Enabled    Stateful
# process_authorizations     Enabled    Stateful
```

### Workflow Deployment via GitHub Actions

The deployment workflows automatically handle workflow packaging and deployment:

```yaml
- name: Package Logic App workflows
  shell: bash
  run: |
    set -euo pipefail
    WF_PATH="logicapps/workflows"
    PARENT_DIR="$(dirname "$WF_PATH")"
    BASE_NAME="$(basename "$WF_PATH")"

    echo "Packaging workflows from: $WF_PATH"
    pushd "$PARENT_DIR" > /dev/null
    zip -r ../workflows.zip "$BASE_NAME"
    popd > /dev/null

    echo "✓ Workflow package created"
    unzip -l workflows.zip | head -20

- name: Deploy Logic App workflows
  uses: azure/cli@v2
  with:
    inlineScript: |
      az webapp deploy \
        --resource-group "${{ env.RESOURCE_GROUP }}" \
        --name "${{ env.LOGIC_APP_NAME }}" \
        --src-path workflows.zip \
        --type zip
      echo "✓ Workflows deployed"

- name: Restart Logic App
  uses: azure/cli@v2
  with:
    inlineScript: |
      az webapp restart \
        --resource-group "${{ env.RESOURCE_GROUP }}" \
        --name "${{ env.LOGIC_APP_NAME }}"
      echo "✓ Logic App restarted"
```

### Troubleshooting Workflow Deployment

#### Issue: "Could not find workflows.zip"

**Cause**: Package not created or incorrect path

**Solution**:
```bash
# Verify package exists
ls -l workflows.zip

# Check current directory
pwd

# Recreate package if needed
cd logicapps && zip -r ../workflows.zip workflows/ && cd ..
```

#### Issue: "Deployment timed out"

**Cause**: Large workflow package or slow network

**Solution**:
```bash
# Check package size (should be < 50 MB)
ls -lh workflows.zip

# Deploy with increased timeout
az webapp deploy \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --src-path workflows.zip \
  --type zip \
  --timeout 600  # 10 minutes
```

#### Issue: "Workflows not appearing in portal"

**Cause**: Logic App cache not refreshed

**Solution**:
```bash
# Force restart Logic App
az webapp stop --resource-group "$RG_NAME" --name "$LOGIC_APP_NAME"
sleep 10
az webapp start --resource-group "$RG_NAME" --name "$LOGIC_APP_NAME"
sleep 30

# Verify workflows via API
az rest --method GET \
  --uri "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG_NAME/providers/Microsoft.Web/sites/$LOGIC_APP_NAME/workflows?api-version=2022-03-01"
```

#### Issue: "Workflows show as 'Disabled'"

**Cause**: Workflow trigger configuration missing or API connections not authenticated

**Solution**:
1. Check API connections are authenticated (see [Post-Deployment Configuration](#post-deployment-configuration))
2. Enable workflow manually in portal
3. Review workflow run history for errors

### Workflow Deployment Timeline

| Step | Duration | Description |
|------|----------|-------------|
| **Package Creation** | 5-10 seconds | Create workflows.zip |
| **Upload to Logic App** | 30-60 seconds | Transfer and extract ZIP |
| **Logic App Restart** | 30-45 seconds | Reload workflow definitions |
| **Workflow Initialization** | 15-30 seconds | Initialize triggers and connections |
| **Total** | **~2-3 minutes** | Complete workflow deployment |

### Best Practices

✅ **Always validate workflow JSON** before packaging  
✅ **Test package structure** with `unzip -l`  
✅ **Restart Logic App** after deployment  
✅ **Verify workflow state** via API or portal  
✅ **Check Application Insights** for deployment errors  
✅ **Keep deployment packages** for rollback  
❌ **Never deploy during peak hours** without testing  
❌ **Never skip validation** steps  

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

### 0. Security Hardening Deployment (Recommended for Production)

For production PHI workloads, deploy comprehensive security controls to achieve HIPAA compliance:

#### Security Score Impact
- **Before**: 7/10 (Basic security)
- **After**: 9/10 (Production-ready for PHI)

#### Deploy Security Modules

```bash
# Set variables
RG_NAME="pchp-attachments-prod-rg"
BASE_NAME="hipaa-attachments-prod"
LOCATION="eastus"
SUBSCRIPTION_ID=$(az account show --query id -o tsv)

# 1. Deploy Azure Key Vault (Premium with HSM)
az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file infra/modules/keyvault.bicep \
  --parameters keyVaultName="${BASE_NAME}-kv" \
                location="$LOCATION" \
                skuName="premium" \
                enableRbacAuthorization=true \
                enableSoftDelete=true \
                softDeleteRetentionInDays=90 \
                enablePurgeProtection=true \
                publicNetworkAccess="Disabled"

echo "✅ Key Vault deployed with HSM-backed keys"

# 2. Deploy Networking (VNet and Private DNS Zones)
az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file infra/modules/networking.bicep \
  --parameters vnetName="${BASE_NAME}-vnet" \
                location="$LOCATION" \
                vnetAddressPrefix="10.0.0.0/16" \
                logicAppsSubnetPrefix="10.0.1.0/24" \
                privateEndpointsSubnetPrefix="10.0.2.0/24"

echo "✅ VNet and Private DNS zones deployed"

# 3. Get resource IDs for private endpoints
STORAGE_NAME=$(az storage account list -g "$RG_NAME" --query "[0].name" -o tsv)
STORAGE_ID=$(az storage account show --name "$STORAGE_NAME" -g "$RG_NAME" --query id -o tsv)
SERVICE_BUS_ID=$(az servicebus namespace show --name "${BASE_NAME}-svc" -g "$RG_NAME" --query id -o tsv)
KEY_VAULT_ID=$(az keyvault show --name "${BASE_NAME}-kv" -g "$RG_NAME" --query id -o tsv)

# Get subnet and DNS zone IDs
PRIVATE_SUBNET_ID=$(az network vnet subnet show \
  --resource-group "$RG_NAME" \
  --vnet-name "${BASE_NAME}-vnet" \
  --name "private-endpoints-subnet" \
  --query id -o tsv)

STORAGE_DNS_ZONE_ID=$(az network private-dns zone show \
  --resource-group "$RG_NAME" \
  --name "privatelink.blob.core.windows.net" \
  --query id -o tsv)

SERVICE_BUS_DNS_ZONE_ID=$(az network private-dns zone show \
  --resource-group "$RG_NAME" \
  --name "privatelink.servicebus.windows.net" \
  --query id -o tsv)

KEY_VAULT_DNS_ZONE_ID=$(az network private-dns zone show \
  --resource-group "$RG_NAME" \
  --name "privatelink.vaultcore.azure.net" \
  --query id -o tsv)

# 4. Deploy Private Endpoints
az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file infra/modules/private-endpoints.bicep \
  --parameters subnetId="$PRIVATE_SUBNET_ID" \
                storageAccountId="$STORAGE_ID" \
                storageAccountName="$STORAGE_NAME" \
                serviceBusId="$SERVICE_BUS_ID" \
                serviceBusName="${BASE_NAME}-svc" \
                keyVaultId="$KEY_VAULT_ID" \
                keyVaultName="${BASE_NAME}-kv" \
                storageDnsZoneId="$STORAGE_DNS_ZONE_ID" \
                serviceBusDnsZoneId="$SERVICE_BUS_DNS_ZONE_ID" \
                keyVaultDnsZoneId="$KEY_VAULT_DNS_ZONE_ID"

echo "✅ Private endpoints deployed - all resources isolated from public internet"

# 5. Enable VNet Integration for Logic App
LOGIC_APP_NAME="${BASE_NAME}-la"
az webapp vnet-integration add \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --vnet "${BASE_NAME}-vnet" \
  --subnet "logic-apps-subnet"

echo "✅ Logic App VNet integration enabled"

# 6. Disable Public Access
az storage account update --name "$STORAGE_NAME" -g "$RG_NAME" --public-network-access Disabled
az servicebus namespace update --name "${BASE_NAME}-svc" -g "$RG_NAME" --public-network-access Disabled

echo "✅ Public access disabled on all PHI resources"

# 7. Configure Key Vault RBAC for Logic App
PRINCIPAL_ID=$(az webapp identity show -g "$RG_NAME" --name "$LOGIC_APP_NAME" --query principalId -o tsv)
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Key Vault Secrets User" \
  --scope "$KEY_VAULT_ID"

echo "✅ Logic App granted Key Vault access via managed identity"

# 8. Apply Data Lifecycle Policies
cat > lifecycle-policy.json <<'EOF'
{
  "rules": [
    {
      "name": "move-to-cool-after-30-days",
      "enabled": true,
      "type": "Lifecycle",
      "definition": {
        "actions": {
          "baseBlob": {"tierToCool": {"daysAfterModificationGreaterThan": 30}}
        },
        "filters": {
          "blobTypes": ["blockBlob"],
          "prefixMatch": ["hipaa-attachments/raw/"]
        }
      }
    },
    {
      "name": "move-to-archive-after-90-days",
      "enabled": true,
      "type": "Lifecycle",
      "definition": {
        "actions": {
          "baseBlob": {"tierToArchive": {"daysAfterModificationGreaterThan": 90}}
        },
        "filters": {
          "blobTypes": ["blockBlob"],
          "prefixMatch": ["hipaa-attachments/raw/"]
        }
      }
    },
    {
      "name": "delete-after-7-years",
      "enabled": true,
      "type": "Lifecycle",
      "definition": {
        "actions": {
          "baseBlob": {"delete": {"daysAfterModificationGreaterThan": 2555}}
        },
        "filters": {
          "blobTypes": ["blockBlob"],
          "prefixMatch": ["hipaa-attachments/raw/"]
        }
      }
    }
  ]
}
EOF

az storage account management-policy create \
  --account-name "$STORAGE_NAME" \
  --resource-group "$RG_NAME" \
  --policy @lifecycle-policy.json

echo "✅ Data lifecycle policies applied (Cool→30d, Archive→90d, Delete→7yr)"

echo ""
echo "🎉 Security hardening deployment complete!"
echo ""
echo "Next steps:"
echo "1. Migrate secrets to Key Vault (see DEPLOYMENT-SECRETS-SETUP.md § Azure Key Vault Secret Migration)"
echo "2. Update Logic App workflows to use Key Vault references"
echo "3. Configure PHI masking in Application Insights"
echo "4. Enable Azure AD authentication for replay278 endpoint"
echo "5. Review SECURITY-HARDENING.md for additional security controls"
```

#### Verify Security Deployment

```bash
# Verify private endpoints
az network private-endpoint list -g "$RG_NAME" --query "[].{Name:name, State:provisioningState}" -o table

# Verify VNet integration
az webapp vnet-integration list -g "$RG_NAME" --name "$LOGIC_APP_NAME" -o table

# Verify Key Vault configuration
az keyvault show --name "${BASE_NAME}-kv" --query "{SKU:properties.sku.name, RBAC:properties.enableRbacAuthorization, SoftDelete:properties.enableSoftDelete, PurgeProtection:properties.enablePurgeProtection}"

# Verify public access disabled
az storage account show --name "$STORAGE_NAME" --query "publicNetworkAccess"
az servicebus namespace show --name "${BASE_NAME}-svc" --query "publicNetworkAccess"

# Expected: All show "Disabled"
```

#### Security Documentation References

For detailed security implementation guidance:
- **[SECURITY-HARDENING.md](SECURITY-HARDENING.md)** - 400+ line comprehensive security guide
- **[docs/HIPAA-COMPLIANCE-MATRIX.md](docs/HIPAA-COMPLIANCE-MATRIX.md)** - Complete HIPAA technical safeguards mapping
- **[DEPLOYMENT-SECRETS-SETUP.md](DEPLOYMENT-SECRETS-SETUP.md)** - Key Vault secret migration procedures

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

This section provides comprehensive rollback procedures for different failure scenarios.

### Rollback Strategy Overview

| Failure Type | Severity | Rollback Method | Estimated Time | Data Loss Risk |
|--------------|----------|-----------------|----------------|----------------|
| **Workflow Deployment** | Low | Redeploy previous workflows | 2-3 minutes | None |
| **Single Workflow Issue** | Low | Disable problematic workflow | < 1 minute | None |
| **Infrastructure Config** | Medium | Redeploy previous Bicep | 5-10 minutes | None |
| **Infrastructure Breaking** | High | ARM deployment rollback | 10-15 minutes | Low |
| **Complete Failure** | Critical | Full resource group restore | 15-30 minutes | Medium |

### Pre-Rollback Checklist

Before initiating rollback:

- [ ] **Identify the failure scope**: Workflow-only vs. infrastructure
- [ ] **Document the error**: Capture error messages and logs
- [ ] **Check recent changes**: Review what was deployed
- [ ] **Assess data risk**: Check if data/messages are in flight
- [ ] **Notify stakeholders**: Inform team of rollback action
- [ ] **Have known-good version**: Identify working commit SHA

### Workflow Rollback Procedures

#### Scenario 1: Workflow Deployment Failed (Safest)

**When to use:** Workflow ZIP deployment failed, infrastructure is intact

```bash
# Set variables
RG_NAME="pchp-attachments-prod-rg"
LOGIC_APP_NAME="hipaa-attachments-prod-la"

# Step 1: Verify infrastructure is healthy
echo "Checking Logic App status..."
az webapp show \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --query "{Name:name, State:state, HealthCheckStatus:healthCheckStatus}" \
  --output table

# Step 2: Review deployment history
echo "Checking recent deployments..."
az webapp deployment list \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --output table

# Step 3: No action needed - previous workflows still active
echo "✓ Infrastructure intact, previous workflows still running"
echo "Fix workflow issues and redeploy when ready"
```

#### Scenario 2: Workflow Causing Runtime Errors

**When to use:** New workflows deployed successfully but causing errors

**Step 1: Identify Previous Working Version**

```bash
# List recent commits
git log --oneline -10 logicapps/workflows/

# Example output:
# a1b2c3d (HEAD) Update ingest275 trigger config
# d4e5f6g Add retry logic to QNXT calls
# g7h8i9j Working version before changes  ← Use this one

PREVIOUS_COMMIT="g7h8i9j"  # Last known good commit
```

**Step 2: Checkout Previous Workflows**

```bash
# Create rollback branch for tracking
git checkout -b rollback/workflows-$(date +%Y%m%d-%H%M%S)

# Restore previous workflow versions
git checkout "$PREVIOUS_COMMIT" -- logicapps/workflows/

# Verify files were restored
git status
# Should show modified files in logicapps/workflows/
```

**Step 3: Package and Deploy Previous Version**

```bash
# Package workflows
cd logicapps
zip -r ../workflows-rollback.zip workflows/
cd ..

# Verify package
unzip -l workflows-rollback.zip | grep workflow.json
# Should show 6 workflow.json files

# Deploy rollback package
echo "Deploying previous workflow version..."
az webapp deploy \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --src-path workflows-rollback.zip \
  --type zip \
  --async false

# Restart Logic App
echo "Restarting Logic App..."
az webapp restart \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"

# Wait for restart
sleep 30

echo "✅ Workflow rollback complete"
```

**Step 4: Verify Rollback Success**

```bash
# Check Logic App status
STATUS=$(az webapp show \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --query "state" -o tsv)

echo "Logic App Status: $STATUS"

# List workflows and their state
SUBSCRIPTION_ID=$(az account show --query id -o tsv)

az rest --method GET \
  --uri "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RG_NAME/providers/Microsoft.Web/sites/$LOGIC_APP_NAME/workflows?api-version=2022-03-01" \
  --query "value[].{Name:name, State:properties.state}" \
  --output table

# Check Application Insights for recent errors
echo "Checking for errors in last 5 minutes..."
AI_NAME="${LOGIC_APP_NAME%-la}-ai"
az monitor app-insights query \
  --app "$AI_NAME" \
  --resource-group "$RG_NAME" \
  --analytics-query "traces | where timestamp > ago(5m) and severityLevel >= 3 | summarize count()" \
  --output table
```

#### Scenario 3: Disable Single Problematic Workflow

**When to use:** Only one workflow is problematic, others working fine

```bash
WORKFLOW_NAME="ingest275"  # Problem workflow

# Option A: Via Azure Portal
# 1. Navigate to Logic App → Workflows
# 2. Click on problematic workflow (e.g., "ingest275")
# 3. Click "Disable" button
# 4. Confirm disabling

# Option B: Via Azure CLI (requires REST API)
# Note: Direct disable via CLI is limited; portal is recommended

echo "Manual steps:"
echo "1. Open Azure Portal"
echo "2. Navigate to: $LOGIC_APP_NAME → Workflows → $WORKFLOW_NAME"
echo "3. Click 'Disable'"
echo "4. Review run history for root cause"
```

### Infrastructure Rollback Procedures

#### Scenario 4: Infrastructure Configuration Change Rollback

**When to use:** Bicep deployment changed configuration but didn't break resources

**Step 1: Identify Previous Working Template**

```bash
# List recent changes to infrastructure
git log --oneline -10 infra/

# Example output:
# x1y2z3a Update Service Bus topic config
# a2b3c4d Add new storage container
# d5e6f7g Stable infrastructure  ← Use this one

PREVIOUS_COMMIT="d5e6f7g"
```

**Step 2: Review What-If for Rollback**

```bash
# Checkout previous template to temp location
git show "$PREVIOUS_COMMIT:infra/main.bicep" > /tmp/previous-main.bicep

# Run What-If to see rollback changes
az deployment group what-if \
  --resource-group "$RG_NAME" \
  --template-file /tmp/previous-main.bicep \
  --parameters baseName="$BASE_NAME" \
               location="$LOCATION" \
               sftpHost="$SFTP_HOST" \
               sftpUsername="$SFTP_USERNAME" \
               sftpPassword="$SFTP_PASSWORD" \
               serviceBusName="$SERVICE_BUS_NAME" \
               iaName="$IA_NAME" \
               connectorLocation="$CONNECTOR_LOCATION" \
  --no-pretty-print

# Review output carefully!
# Look for any resource deletions (-)
```

**Step 3: Deploy Previous Infrastructure Version**

```bash
# Create rollback deployment
DEPLOY_NAME="rollback-infra-$(date +%Y%m%d-%H%M%S)"

az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file /tmp/previous-main.bicep \
  --parameters baseName="$BASE_NAME" \
               location="$LOCATION" \
               sftpHost="$SFTP_HOST" \
               sftpUsername="$SFTP_USERNAME" \
               sftpPassword="$SFTP_PASSWORD" \
               serviceBusName="$SERVICE_BUS_NAME" \
               iaName="$IA_NAME" \
               connectorLocation="$CONNECTOR_LOCATION" \
  --name "$DEPLOY_NAME" \
  --verbose

# Monitor deployment
az deployment group show \
  --resource-group "$RG_NAME" \
  --name "$DEPLOY_NAME" \
  --query "{Name:name, State:properties.provisioningState, Timestamp:properties.timestamp}"
```

**Step 4: Restart Logic App After Infrastructure Rollback**

```bash
# Restart to pick up infrastructure changes
az webapp restart \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME"

sleep 30

echo "✅ Infrastructure rollback complete"
```

#### Scenario 5: Complete Infrastructure Failure (Advanced)

**When to use:** Infrastructure deployment broke critical resources

**⚠️ WARNING: This is a complex rollback. Use only for critical failures.**

**Step 1: Export Current Resource State (if possible)**

```bash
# Export current deployment for reference
az group export \
  --resource-group "$RG_NAME" \
  --output json > "/tmp/failed-deployment-$(date +%Y%m%d-%H%M%S).json"

# Export Logic App configuration
az webapp config show \
  --resource-group "$RG_NAME" \
  --name "$LOGIC_APP_NAME" \
  --output json > "/tmp/logic-app-config-backup.json"
```

**Step 2: List Deployment History**

```bash
# Show recent deployments
az deployment group list \
  --resource-group "$RG_NAME" \
  --query "reverse(sort_by([].{Name:name, State:properties.provisioningState, Time:properties.timestamp}, &Time))" \
  --output table

# Identify last successful deployment
LAST_SUCCESS_DEPLOY="hipaa-infra-deployment-20241115"  # Example
```

**Step 3: Rollback to Last Successful Deployment**

```bash
# Get deployment details
az deployment group show \
  --resource-group "$RG_NAME" \
  --name "$LAST_SUCCESS_DEPLOY" \
  --query "{Template:properties.templateLink, Parameters:properties.parameters}" \
  --output jsonc > /tmp/last-success-deployment.json

# Export template from successful deployment
az deployment group export \
  --resource-group "$RG_NAME" \
  --name "$LAST_SUCCESS_DEPLOY" \
  --output json > /tmp/success-template.json

# Redeploy using successful template
az deployment group create \
  --resource-group "$RG_NAME" \
  --template-file /tmp/success-template.json \
  --name "rollback-to-$LAST_SUCCESS_DEPLOY-$(date +%Y%m%d-%H%M%S)"
```

#### Scenario 6: Complete Resource Group Restore (NUCLEAR OPTION)

**When to use:** Everything is broken, need clean slate

**⚠️ EXTREME WARNING:**
- This **DELETES ALL RESOURCES** in the resource group
- This **LOSES ALL DATA** that isn't backed up
- **ONLY USE IN DEV/UAT** environments
- **NEVER USE IN PRODUCTION** without explicit approval and backup verification

```bash
# FINAL CONFIRMATION
read -p "⚠️  This will DELETE ALL resources. Type 'DELETE-EVERYTHING' to confirm: " CONFIRM
if [ "$CONFIRM" != "DELETE-EVERYTHING" ]; then
  echo "Cancelled. Confirmation text did not match."
  exit 1
fi

# Backup before destruction
echo "Creating final backups..."

# Export resource group
az group export \
  --resource-group "$RG_NAME" \
  --output json > "/tmp/pre-delete-export-$(date +%Y%m%d-%H%M%S).json"

# List resources for record
az resource list \
  --resource-group "$RG_NAME" \
  --output table > "/tmp/pre-delete-resources-$(date +%Y%m%d-%H%M%S).txt"

# Delete resource group
echo "Deleting resource group: $RG_NAME"
az group delete \
  --name "$RG_NAME" \
  --yes \
  --no-wait

# Monitor deletion
echo "Monitoring deletion progress..."
while az group exists --name "$RG_NAME" | grep -q "true"; do
  echo "Still deleting... (waiting 30s)"
  sleep 30
done

echo "✅ Resource group deleted"

# Redeploy from known good state
echo "Redeploying from known good configuration..."
KNOWN_GOOD_COMMIT="<insert-commit-sha>"

git checkout "$KNOWN_GOOD_COMMIT"

# Run full deployment (follow Environment Deployment section)
az group create --name "$RG_NAME" --location "$LOCATION"

# Deploy infrastructure...
# Deploy workflows...
# Configure post-deployment...
```

### Rollback Verification Checklist

After any rollback, verify:

- [ ] **Infrastructure Resources**
  ```bash
  az resource list --resource-group "$RG_NAME" --output table
  ```

- [ ] **Logic App Running**
  ```bash
  az webapp show --resource-group "$RG_NAME" --name "$LOGIC_APP_NAME" --query state
  ```

- [ ] **Workflows Enabled**
  ```bash
  # Check via API or portal
  ```

- [ ] **No Recent Errors in App Insights**
  ```bash
  az monitor app-insights query --app "$AI_NAME" --analytics-query "traces | where timestamp > ago(10m) and severityLevel >= 3"
  ```

- [ ] **Service Bus Topics Exist**
  ```bash
  az servicebus topic list --resource-group "$RG_NAME" --namespace-name "$SB_NAME"
  ```

- [ ] **Storage Account Accessible**
  ```bash
  az storage account show --name "$STORAGE_NAME" --query provisioningState
  ```

### Post-Rollback Actions

1. **Document the Incident**
   - What failed
   - What was rolled back
   - Root cause (if known)
   - Time to resolution

2. **Review Logs**
   - Application Insights
   - Azure Activity Log
   - GitHub Actions logs

3. **Notify Stakeholders**
   - Inform team of rollback completion
   - Provide incident summary
   - Outline plan to prevent recurrence

4. **Plan Forward**
   - Fix root cause in dev/test
   - Add validation checks
   - Update deployment procedures if needed

### Emergency Contacts

**Deployment Issues:**
- GitHub Actions: Check repository settings → Actions
- Azure Support: Create support ticket in portal
- Team Lead: [Contact information]

**Data Recovery:**
- Backup verification: Check storage account archives
- Point-in-time restore: Available for critical data

### Rollback Testing

**Regularly test rollback procedures** in DEV environment:

```bash
# Monthly rollback drill
# 1. Deploy test change to DEV
# 2. Immediately roll back
# 3. Verify system functionality
# 4. Document any issues with procedure
# 5. Update this guide based on findings
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
