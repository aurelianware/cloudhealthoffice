# GitHub Actions Setup Guide

This comprehensive guide covers all aspects of setting up GitHub Actions for automated deployment of the HIPAA Attachments solution.

## Table of Contents
- [Prerequisites](#prerequisites)
- [Azure OIDC Authentication Setup](#azure-oidc-authentication-setup)
- [GitHub Secrets Configuration](#github-secrets-configuration)
- [GitHub Variables Configuration](#github-variables-configuration)
- [GitHub Environments Setup](#github-environments-setup)
- [Workflow Permissions](#workflow-permissions)
- [Testing the Setup](#testing-the-setup)
- [Troubleshooting](#troubleshooting)

## Prerequisites

Before configuring GitHub Actions, ensure you have:

- [ ] **Azure Subscription** with Contributor access or higher
- [ ] **GitHub Repository** admin access
- [ ] **Azure CLI** installed locally (`az --version` ≥ 2.77.0)
- [ ] **Owner or User Access Administrator** role in Azure (for creating service principals)
- [ ] **Permissions to create federated credentials** in Azure AD

### Required Azure Roles

| Role | Scope | Purpose |
|------|-------|---------|
| **Contributor** | Subscription or Resource Group | Deploy and manage Azure resources |
| **User Access Administrator** | Subscription or Resource Group | Assign RBAC roles to managed identities |

## Azure OIDC Authentication Setup

OpenID Connect (OIDC) authentication allows GitHub Actions to authenticate with Azure without storing credentials as secrets. This is the **recommended and most secure** authentication method.

### Why OIDC?

✅ **No long-lived credentials** stored in GitHub  
✅ **Automatic token rotation** and expiration  
✅ **Fine-grained access control** per environment  
✅ **Audit trail** in Azure AD  
✅ **Compliance** with security best practices  

### Step 1: Create Azure AD Application

Create a separate Azure AD application for each environment (DEV, UAT, PROD).

#### Using Azure Portal

1. Navigate to **Azure Active Directory** → **App registrations**
2. Click **+ New registration**
3. Configure:
   - **Name**: `hipaa-attachments-{ENV}-github` (e.g., `hipaa-attachments-prod-github`)
   - **Supported account types**: Single tenant
   - **Redirect URI**: Leave empty
4. Click **Register**
5. **Save the Application (client) ID** - you'll need this for GitHub Secrets

#### Using Azure CLI

```bash
# Set environment-specific variables
ENV="prod"  # Change to "dev", "uat", or "prod"
APP_NAME="hipaa-attachments-${ENV}-github"
REPO_OWNER="aurelianware"
REPO_NAME="hipaa-attachments"

# Create Azure AD application
az ad app create --display-name "$APP_NAME"

# Get application ID
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv)
echo "Application (Client) ID: $APP_ID"

# Create service principal
az ad sp create --id "$APP_ID"
```

**⚠️ Important**: Save the Application (Client) ID immediately. You'll need it for:
- GitHub Secrets configuration
- Federated credential setup
- Role assignments

### Step 2: Configure Federated Credentials

Federated credentials allow GitHub Actions to authenticate using short-lived tokens.

#### Branch-Based Deployment

For **main branch** deployments (typical for DEV and PROD):

```bash
# Using variables from Step 1
ENV="prod"
APP_ID="<your-app-id>"
REPO_OWNER="aurelianware"
REPO_NAME="hipaa-attachments"
BRANCH="main"

# Create federated credential
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-${ENV}-main\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:${REPO_OWNER}/${REPO_NAME}:ref:refs/heads/${BRANCH}\",
    \"description\": \"GitHub Actions deployment from ${BRANCH} branch to ${ENV}\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"
```

#### Release Branch Deployment

For **release/* branch** deployments (typical for UAT):

```bash
ENV="uat"
APP_ID="<your-app-id>"

# Create federated credential for release branches
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-${ENV}-release\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:${REPO_OWNER}/${REPO_NAME}:ref:refs/heads/release/*\",
    \"description\": \"GitHub Actions deployment from release branches to ${ENV}\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"
```

#### Environment-Based Deployment

For **workflow_dispatch** with environment protection:

```bash
# Add federated credential for GitHub environment
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-${ENV}-environment\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:${REPO_OWNER}/${REPO_NAME}:environment:${ENV^^}\",
    \"description\": \"GitHub Actions deployment with environment protection to ${ENV}\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"
```

**📝 Note**: The `subject` field must **exactly match** the GitHub repository structure and branch/environment name.

### Step 3: Assign Azure Roles

Grant the service principal necessary permissions to deploy resources.

#### Subscription-Level Access (Recommended for Multiple Resource Groups)

```bash
SUBSCRIPTION_ID="<your-subscription-id>"
APP_ID="<your-app-id>"

# Assign Contributor role at subscription level
az role assignment create \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID"

# Optional: Assign User Access Administrator for managed identity role assignments
az role assignment create \
  --assignee "$APP_ID" \
  --role "User Access Administrator" \
  --scope "/subscriptions/$SUBSCRIPTION_ID"
```

#### Resource Group-Level Access (More Restrictive)

```bash
RESOURCE_GROUP="pchp-attachments-prod-rg"
APP_ID="<your-app-id>"

# Assign Contributor role at resource group level
az role assignment create \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"
```

### Step 4: Verify OIDC Setup

```bash
# List federated credentials
az ad app federated-credential list --id "$APP_ID" --query "[].{Name:name, Subject:subject}" -o table

# Verify service principal
az ad sp show --id "$APP_ID" --query "{DisplayName:displayName, AppId:appId, ObjectId:id}" -o table

# Verify role assignments
az role assignment list --assignee "$APP_ID" --output table
```

**Expected Output Example:**
```
Name                      Subject
------------------------  --------------------------------------------------------
github-prod-main          repo:aurelianware/hipaa-attachments:ref:refs/heads/main
github-prod-environment   repo:aurelianware/hipaa-attachments:environment:PROD
```

### Step 5: Gather Required IDs

Collect these IDs for GitHub Secrets configuration:

```bash
# Application (Client) ID
echo "AZURE_CLIENT_ID: $APP_ID"

# Tenant ID
TENANT_ID=$(az account show --query tenantId -o tsv)
echo "AZURE_TENANT_ID: $TENANT_ID"

# Subscription ID
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
echo "AZURE_SUBSCRIPTION_ID: $SUBSCRIPTION_ID"
```

**💾 Save these values** - you'll need them in the next section.

## GitHub Secrets Configuration

Secrets are encrypted environment variables used by GitHub Actions workflows.

### Required Secrets Per Environment

Each environment (DEV, UAT, PROD) requires these three secrets:

| Secret Name | Description | Example Value |
|-------------|-------------|---------------|
| `AZURE_CLIENT_ID_{ENV}` | Azure AD Application (Client) ID | `12345678-1234-1234-1234-123456789abc` |
| `AZURE_TENANT_ID_{ENV}` | Azure AD Tenant ID | `87654321-4321-4321-4321-cba987654321` |
| `AZURE_SUBSCRIPTION_ID_{ENV}` | Azure Subscription ID | `abcdef12-3456-7890-abcd-ef1234567890` |

### Adding Secrets via GitHub UI

1. **Navigate to Repository Settings**
   - Go to your GitHub repository: `https://github.com/aurelianware/hipaa-attachments`
   - Click **Settings** (top menu)
   - Click **Secrets and variables** → **Actions** (left sidebar)

2. **Create New Secret**
   - Click **New repository secret**
   - Enter **Name**: `AZURE_CLIENT_ID_PROD`
   - Enter **Value**: `<paste-your-client-id>`
   - Click **Add secret**

3. **Repeat for All Secrets**

   **DEV Environment:**
   ```
   Name: AZURE_CLIENT_ID_DEV
   Value: <dev-app-client-id>
   
   Name: AZURE_TENANT_ID_DEV
   Value: <azure-tenant-id>
   
   Name: AZURE_SUBSCRIPTION_ID_DEV
   Value: <dev-subscription-id>
   ```

   **UAT Environment:**
   ```
   Name: AZURE_CLIENT_ID_UAT
   Value: <uat-app-client-id>
   
   Name: AZURE_TENANT_ID_UAT
   Value: <azure-tenant-id>
   
   Name: AZURE_SUBSCRIPTION_ID_UAT
   Value: <uat-subscription-id>
   ```

   **PROD Environment:**
   ```
   Name: AZURE_CLIENT_ID_PROD
   Value: <prod-app-client-id>
   
   Name: AZURE_TENANT_ID_PROD
   Value: <azure-tenant-id>
   
   Name: AZURE_SUBSCRIPTION_ID_PROD
   Value: <prod-subscription-id>
   ```

### Adding Secrets via GitHub CLI

```bash
# Install GitHub CLI if not already installed
# https://cli.github.com/

# Authenticate with GitHub
gh auth login

# Set repository
REPO_OWNER="aurelianware"
REPO_NAME="hipaa-attachments"

# Add DEV secrets
gh secret set AZURE_CLIENT_ID_DEV --repo "$REPO_OWNER/$REPO_NAME" --body "<dev-client-id>"
gh secret set AZURE_TENANT_ID_DEV --repo "$REPO_OWNER/$REPO_NAME" --body "<tenant-id>"
gh secret set AZURE_SUBSCRIPTION_ID_DEV --repo "$REPO_OWNER/$REPO_NAME" --body "<dev-subscription-id>"

# Add UAT secrets
gh secret set AZURE_CLIENT_ID_UAT --repo "$REPO_OWNER/$REPO_NAME" --body "<uat-client-id>"
gh secret set AZURE_TENANT_ID_UAT --repo "$REPO_OWNER/$REPO_NAME" --body "<tenant-id>"
gh secret set AZURE_SUBSCRIPTION_ID_UAT --repo "$REPO_OWNER/$REPO_NAME" --body "<uat-subscription-id>"

# Add PROD secrets
gh secret set AZURE_CLIENT_ID_PROD --repo "$REPO_OWNER/$REPO_NAME" --body "<prod-client-id>"
gh secret set AZURE_TENANT_ID_PROD --repo "$REPO_OWNER/$REPO_NAME" --body "<tenant-id>"
gh secret set AZURE_SUBSCRIPTION_ID_PROD --repo "$REPO_OWNER/$REPO_NAME" --body "<prod-subscription-id>"
```

### Verify Secrets Are Set

```bash
# List secrets (values are hidden)
gh secret list --repo "$REPO_OWNER/$REPO_NAME"
```

**Expected Output:**
```
AZURE_CLIENT_ID_DEV       Updated 2024-11-16
AZURE_CLIENT_ID_PROD      Updated 2024-11-16
AZURE_CLIENT_ID_UAT       Updated 2024-11-16
AZURE_SUBSCRIPTION_ID_DEV Updated 2024-11-16
AZURE_SUBSCRIPTION_ID_PROD Updated 2024-11-16
AZURE_SUBSCRIPTION_ID_UAT Updated 2024-11-16
AZURE_TENANT_ID_DEV       Updated 2024-11-16
AZURE_TENANT_ID_PROD      Updated 2024-11-16
AZURE_TENANT_ID_UAT       Updated 2024-11-16
```

### Optional: SFTP Secrets

If your deployment includes SFTP configuration:

```bash
# SFTP connection details (environment-specific)
gh secret set SFTP_HOST --repo "$REPO_OWNER/$REPO_NAME" --body "sftp.availity.com"
gh secret set SFTP_USERNAME --repo "$REPO_OWNER/$REPO_NAME" --body "service-account"
gh secret set SFTP_PASSWORD --repo "$REPO_OWNER/$REPO_NAME" --body "<sftp-password>"
```

**🔒 Security Best Practice**: Use SSH keys instead of passwords when possible.

## GitHub Variables Configuration

Variables are non-sensitive configuration values accessible by workflows.

### Required Variables Per Environment

| Variable Name | Description | DEV Example | UAT Example | PROD Example |
|---------------|-------------|-------------|-------------|--------------|
| `AZURE_RG_NAME` | Resource group name | `rg-hipaa-attachments-dev` | `hipaa-attachments-uat-rg` | `pchp-attachments-prod-rg` |
| `AZURE_LOCATION` | Azure region | `eastus` | `eastus` | `eastus` |
| `AZURE_CONNECTOR_LOCATION` | API connection region | `eastus` | `eastus` | `eastus` |
| `BASE_NAME` | Resource name prefix | `hipaa-attachments-dev` | `hipaa-attachments-uat` | `hipaa-attachments-prod` |
| `IA_NAME` | Integration Account name | `dev-integration-account` | `uat-integration-account` | `prod-integration-account` |
| `SERVICE_BUS_NAME` | Service Bus namespace | `hipaa-attachments-dev-svc` | `hipaa-attachments-uat-svc` | `hipaa-attachments-prod-svc` |
| `STORAGE_SKU` | Storage account SKU | `Standard_LRS` | `Standard_GRS` | `Standard_GRS` |

### Adding Variables via GitHub UI

1. Navigate to **Settings** → **Secrets and variables** → **Actions**
2. Click the **Variables** tab
3. Click **New repository variable**
4. Enter **Name** and **Value**
5. Click **Add variable**

### Adding Variables via GitHub CLI

```bash
# Add DEV variables
gh variable set AZURE_RG_NAME --repo "$REPO_OWNER/$REPO_NAME" --body "rg-hipaa-attachments-dev"
gh variable set AZURE_LOCATION --repo "$REPO_OWNER/$REPO_NAME" --body "eastus"
gh variable set BASE_NAME --repo "$REPO_OWNER/$REPO_NAME" --body "hipaa-attachments-dev"
gh variable set IA_NAME --repo "$REPO_OWNER/$REPO_NAME" --body "dev-integration-account"
gh variable set SERVICE_BUS_NAME --repo "$REPO_OWNER/$REPO_NAME" --body "hipaa-attachments-dev-svc"
gh variable set STORAGE_SKU --repo "$REPO_OWNER/$REPO_NAME" --body "Standard_LRS"
gh variable set AZURE_CONNECTOR_LOCATION --repo "$REPO_OWNER/$REPO_NAME" --body "eastus"
```

### Environment-Specific Variables

For environment-specific configuration, use GitHub Environments:

1. Navigate to **Settings** → **Environments**
2. Click **New environment** or select existing (DEV, UAT, PROD)
3. Add **Environment variables** specific to that environment

## GitHub Environments Setup

GitHub Environments provide deployment protection and environment-specific configuration.

### Creating Environments

1. **Navigate to Repository Settings**
   - Go to **Settings** → **Environments**

2. **Create DEV Environment**
   - Click **New environment**
   - Name: `DEV`
   - Click **Configure environment**
   - **Deployment protection rules**: None (for auto-deployment)
   - Click **Save protection rules**

3. **Create UAT Environment**
   - Click **New environment**
   - Name: `UAT`
   - Click **Configure environment**
   - **Deployment protection rules**:
     - ☑️ Required reviewers (optional: add 1-2 reviewers)
     - ☑️ Wait timer: 0 minutes (or add delay)
   - **Environment secrets**: Add UAT-specific secrets if needed
   - Click **Save protection rules**

4. **Create PROD Environment**
   - Click **New environment**
   - Name: `PROD`
   - Click **Configure environment**
   - **Deployment protection rules**:
     - ☑️ **Required reviewers**: Add 2+ reviewers for production approval
     - ☑️ **Wait timer**: 15 minutes (cooling-off period)
   - **Branch protection**: Limit to `main` branch only
   - **Environment secrets**: Add PROD-specific secrets if needed
   - Click **Save protection rules**

### Environment Variables

Add environment-specific variables to each environment:

**DEV Environment Variables:**
```
AZURE_RG_NAME = rg-hipaa-attachments-dev
BASE_NAME = hipaa-attachments-dev
```

**UAT Environment Variables:**
```
AZURE_RG_NAME = hipaa-attachments-uat-rg
BASE_NAME = hipaa-attachments-uat
```

**PROD Environment Variables:**
```
AZURE_RG_NAME = pchp-attachments-prod-rg
BASE_NAME = hipaa-attachments-prod
```

## Workflow Permissions

Configure workflow permissions for secure deployment.

### Repository Permissions

1. Navigate to **Settings** → **Actions** → **General**
2. Scroll to **Workflow permissions**
3. Select: **Read and write permissions** (required for status updates)
4. ☑️ Allow GitHub Actions to create and approve pull requests
5. Click **Save**

### OIDC Token Permissions

Workflows using OIDC require these permissions in the workflow YAML:

```yaml
permissions:
  id-token: write  # Required for OIDC authentication
  contents: read   # Required to checkout code
```

**These are already configured** in `deploy.yml`, `deploy-dev.yml`, and `deploy-uat.yml`.

## Testing the Setup

### Test DEV Deployment

1. **Manual Trigger**
   - Go to **Actions** → **Deploy DEV - HIPAA Attachments**
   - Click **Run workflow**
   - Select branch: `main`
   - Click **Run workflow**

2. **Monitor Execution**
   - Watch workflow progress in real-time
   - Check each job: `validate` → `deploy-infrastructure` → `deploy-logic-apps` → `healthcheck`

3. **Expected Result**
   - ✅ All jobs complete successfully
   - ✅ Resources deployed to DEV resource group
   - ✅ Logic App shows as "Running"
   - ✅ Health checks pass

### Test UAT Deployment

1. **Create Release Branch**
   ```bash
   git checkout -b release/test-v1.0.0
   git push origin release/test-v1.0.0
   ```

2. **Automatic Trigger**
   - Workflow automatically starts on push to `release/*` branch
   - Monitor in **Actions** tab

3. **Approval Process** (if configured)
   - Designated reviewers receive notification
   - Review deployment plan
   - Approve or reject

### Test OIDC Authentication

Run this test workflow to verify OIDC is working:

```bash
# Using Azure CLI with GitHub OIDC token
gh workflow run debug-oidc.yml
```

Check the logs for:
- ✅ Successfully authenticated with Azure
- ✅ Subscription ID matches expected value
- ✅ Service principal has required permissions

## Troubleshooting

### Common Issues and Solutions

#### Issue: "AADSTS700016: Application with identifier 'xxx' was not found"

**Cause**: Federated credential subject doesn't match GitHub repository/branch.

**Solution**:
```bash
# Verify federated credential subject
az ad app federated-credential list --id "$APP_ID"

# Update subject if incorrect
az ad app federated-credential update \
  --id "$APP_ID" \
  --federated-credential-id "<credential-id>" \
  --parameters "{ \"subject\": \"repo:aurelianware/hipaa-attachments:ref:refs/heads/main\" }"
```

#### Issue: "Error: Unable to get ACTIONS_ID_TOKEN_REQUEST_URL env variable"

**Cause**: Workflow missing `id-token: write` permission.

**Solution**: Add to workflow YAML:
```yaml
permissions:
  id-token: write
  contents: read
```

#### Issue: "AuthorizationFailed: The client 'xxx' with object id 'xxx' does not have authorization to perform action"

**Cause**: Service principal lacks necessary Azure role.

**Solution**:
```bash
# Assign Contributor role
az role assignment create \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID"
```

#### Issue: Secrets Not Found

**Cause**: Secret names don't match workflow references.

**Solution**:
```bash
# Verify secret names exactly match
gh secret list --repo "$REPO_OWNER/$REPO_NAME"

# Check workflow file for exact secret names
grep "secrets\." .github/workflows/deploy.yml
```

#### Issue: Workflow Doesn't Trigger

**Cause**: Branch name doesn't match trigger condition.

**Solution**: Check workflow `on:` section:
```yaml
on:
  push:
    branches:
      - "main"  # Must match exactly
  # or
    branches:
      - "release/*"  # Pattern match
```

### Debug Commands

```bash
# Test Azure CLI authentication locally
az login
az account show

# Verify service principal access
az ad sp show --id "$APP_ID"
az role assignment list --assignee "$APP_ID" --all

# Check GitHub secrets (names only, values are hidden)
gh secret list --repo "$REPO_OWNER/$REPO_NAME"

# Check GitHub variables
gh variable list --repo "$REPO_OWNER/$REPO_NAME"

# Validate Bicep template
az bicep build --file infra/main.bicep
```

### Getting Help

If issues persist:

1. **Review Workflow Logs**
   - Go to **Actions** tab
   - Click on failed workflow run
   - Expand each step to see detailed logs

2. **Check Azure Activity Log**
   - Navigate to Azure Portal → Resource Group
   - Click **Activity log**
   - Filter by failed operations

3. **Verify OIDC Setup**
   - Run `debug-oidc.yml` workflow
   - Review authentication logs

4. **Consult Documentation**
   - [DEPLOYMENT.md](DEPLOYMENT.md) - Deployment procedures
   - [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Detailed troubleshooting
   - [SECURITY.md](SECURITY.md) - Security guidelines

## Security Best Practices

### Secret Rotation

Rotate OIDC credentials periodically:

```bash
# Create new federated credential with different name
az ad app federated-credential create --id "$APP_ID" --parameters "{...}"

# Test with new credential
# Once verified, delete old credential
az ad app federated-credential delete --id "$APP_ID" --federated-credential-id "<old-id>"
```

### Principle of Least Privilege

- Grant minimum required permissions
- Use resource group-scoped roles when possible
- Separate service principals per environment
- Regularly review role assignments

### Audit and Monitoring

```bash
# Review recent authentications
az ad sp show --id "$APP_ID" --query "signInActivity"

# Check role assignment history
az monitor activity-log list \
  --caller "$APP_ID" \
  --start-time $(date -u -d '7 days ago' '+%Y-%m-%dT%H:%M:%SZ')
```

### Secret Hygiene

- ✅ Never commit secrets to code
- ✅ Use OIDC instead of service principal keys when possible
- ✅ Rotate secrets regularly (quarterly)
- ✅ Use Azure Key Vault for application secrets
- ✅ Enable secret scanning in GitHub
- ✅ Audit secret access regularly

---

## Quick Reference

### Required Secrets Checklist

- [ ] `AZURE_CLIENT_ID_DEV`
- [ ] `AZURE_TENANT_ID_DEV`
- [ ] `AZURE_SUBSCRIPTION_ID_DEV`
- [ ] `AZURE_CLIENT_ID_UAT`
- [ ] `AZURE_TENANT_ID_UAT`
- [ ] `AZURE_SUBSCRIPTION_ID_UAT`
- [ ] `AZURE_CLIENT_ID_PROD`
- [ ] `AZURE_TENANT_ID_PROD`
- [ ] `AZURE_SUBSCRIPTION_ID_PROD`

### Setup Verification Commands

```bash
# Verify Azure AD App
az ad app show --id "$APP_ID"

# Verify federated credentials
az ad app federated-credential list --id "$APP_ID"

# Verify role assignments
az role assignment list --assignee "$APP_ID"

# Verify GitHub secrets
gh secret list --repo "aurelianware/hipaa-attachments"

# Verify GitHub variables
gh variable list --repo "aurelianware/hipaa-attachments"
```

### Useful Links

- [Azure OIDC Documentation](https://docs.microsoft.com/azure/active-directory/develop/workload-identity-federation)
- [GitHub Actions Secrets](https://docs.github.com/actions/security-guides/encrypted-secrets)
- [GitHub Environments](https://docs.github.com/actions/deployment/targeting-different-environments/using-environments-for-deployment)
- [Azure RBAC Roles](https://docs.microsoft.com/azure/role-based-access-control/built-in-roles)

---

**Next Steps**: After completing this setup, proceed to [DEPLOYMENT.md § Quick Start](DEPLOYMENT.md#quick-start) for deployment procedures.
