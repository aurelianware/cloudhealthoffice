# Deployment Secrets and Environment Configuration Setup

This guide provides comprehensive instructions for configuring GitHub Secrets and Repository Variables required for deploying the HIPAA Attachments Logic Apps solution.

> **🚀 Quick Validation:** Run `./validate-github-secrets.sh` to verify your configuration!

## Table of Contents
- [Overview](#overview)
- [Quick Reference](#quick-reference)
- [GitHub Secrets Setup](#github-secrets-setup)
- [Repository Variables Setup](#repository-variables-setup)
- [Step-by-Step Setup for New Team Members](#step-by-step-setup-for-new-team-members)
- [Validation](#validation)
- [Troubleshooting](#troubleshooting)
- [Security Best Practices](#security-best-practices)

## Overview

The HIPAA Attachments deployment uses GitHub Actions workflows with OIDC (OpenID Connect) authentication to securely deploy to Azure environments. This eliminates the need to store Azure credentials as secrets while maintaining secure access.

### Architecture
- **OIDC Authentication**: Federated identity credentials for secure, passwordless authentication
- **Environment-Specific Secrets**: Separate secrets for DEV, UAT, and PROD environments
- **Repository Variables**: Shared configuration across workflows

## Quick Reference

### Required Secrets by Environment

| Environment | Azure Client ID Secret Name | Azure Tenant ID Secret Name | Azure Subscription ID Secret Name | SFTP Host | SFTP Username | SFTP Password |
|-------------|----------------------------|-----------------------------|-----------------------------------|-----------|---------------|---------------|
| DEV         | `AZURE_CLIENT_ID`          | `AZURE_TENANT_ID`           | `AZURE_SUBSCRIPTION_ID`           | -         | -             | -             |
| UAT         | `AZURE_CLIENT_ID_UAT`      | `AZURE_TENANT_ID_UAT`       | `AZURE_SUBSCRIPTION_ID_UAT`       | -         | -             | -             |
| PROD        | `AZURE_CLIENT_ID`          | `AZURE_TENANT_ID`           | `AZURE_SUBSCRIPTION_ID`           | `SFTP_HOST` | `SFTP_USERNAME` | `SFTP_PASSWORD` |

### Required Repository Variables (PROD)

| Variable Name | Example Value | Purpose |
|---------------|---------------|---------|
| `AZURE_RG_NAME` | `hipaa-attachments-prod-rg` | Resource group name |
| `AZURE_LOCATION` | `eastus` | Primary Azure region |
| `AZURE_CONNECTOR_LOCATION` | `eastus` | API connector region |
| `BASE_NAME` | `hipaa-attachments-prod` | Base name for resources |
| `IA_NAME` | `prod-integration-account` | Integration Account name |
| `SERVICE_BUS_NAME` | `hipaa-attachments-prod-sb` | Service Bus namespace name |
| `STORAGE_SKU` | `Standard_LRS` | Storage account SKU |

## GitHub Secrets Setup

### Prerequisites

Before configuring GitHub Secrets, you need:

1. **Azure AD Application** created for GitHub Actions
2. **Federated Credentials** configured for OIDC
3. **Contributor Role** assigned to the application
4. **Application IDs** from Azure

### Step 1: Create Azure AD Application with OIDC

Run this script for each environment (DEV, UAT, PROD):

```bash
# Set variables for your environment
ENV="prod"  # Change to: dev, uat, or prod
APP_NAME="hipaa-attachments-$ENV-github"
REPO_OWNER="aurelianware"  # TODO: Update to your GitHub organization/username
REPO_NAME="hipaa-attachments"  # TODO: Update to your repository name
SUBSCRIPTION_ID="<your-azure-subscription-id>"

# Login to Azure
az login

# Set the subscription
az account set --subscription "$SUBSCRIPTION_ID"

# Create Azure AD application
echo "Creating Azure AD application: $APP_NAME"
az ad app create --display-name "$APP_NAME"

# Get application ID
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv)
echo "Application ID: $APP_ID"

# Create service principal
echo "Creating service principal..."
az ad sp create --id "$APP_ID"

# Get object ID
SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query "id" -o tsv)
echo "Service Principal Object ID: $SP_OBJECT_ID"

# Determine the branch pattern for federated credential
if [ "$ENV" = "dev" ]; then
  BRANCH_PATTERN="repo:$REPO_OWNER/$REPO_NAME:ref:refs/heads/main"
elif [ "$ENV" = "uat" ]; then
  BRANCH_PATTERN="repo:$REPO_OWNER/$REPO_NAME:ref:refs/heads/release/*"
elif [ "$ENV" = "prod" ]; then
  BRANCH_PATTERN="repo:$REPO_OWNER/$REPO_NAME:ref:refs/heads/main"
else
  echo "Error: Invalid environment. Use dev, uat, or prod"
  exit 1
fi

# Create federated credential for OIDC
echo "Creating federated credential for $ENV environment..."
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-$ENV\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"$BRANCH_PATTERN\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"

# Also create credential for workflow_dispatch (manual triggers)
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-$ENV-dispatch\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:$REPO_OWNER/$REPO_NAME:environment:${ENV^^}\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"

# Assign Contributor role at subscription level
echo "Assigning Contributor role..."
az role assignment create \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID"

# Get Tenant ID
TENANT_ID=$(az account show --query tenantId -o tsv)

# Output the values needed for GitHub Secrets
echo ""
echo "=========================================="
echo "GitHub Secrets for $ENV Environment"
echo "=========================================="

if [ "$ENV" = "dev" ]; then
  echo "AZURE_CLIENT_ID: $APP_ID"
  echo "AZURE_TENANT_ID: $TENANT_ID"
  echo "AZURE_SUBSCRIPTION_ID: $SUBSCRIPTION_ID"
elif [ "$ENV" = "uat" ]; then
  echo "AZURE_CLIENT_ID_UAT: $APP_ID"
  echo "AZURE_TENANT_ID_UAT: $TENANT_ID"
  echo "AZURE_SUBSCRIPTION_ID_UAT: $SUBSCRIPTION_ID"
else
  echo "AZURE_CLIENT_ID: $APP_ID"
  echo "AZURE_TENANT_ID: $TENANT_ID"
  echo "AZURE_SUBSCRIPTION_ID: $SUBSCRIPTION_ID"
fi

echo ""
echo "⚠️  Save these values securely. You'll need them for GitHub configuration."
```

### Step 2: Configure GitHub Secrets

#### Understanding GitHub Secrets Scopes

**IMPORTANT:** GitHub allows secrets to be configured at two levels:

1. **Environment-Level Secrets (RECOMMENDED)** - Scoped to specific environments (DEV/UAT/PROD)
   - More secure: Different values per environment
   - Better access control: Can require approvals for PROD
   - Follows security best practices
   - **This is the recommended approach for this project**

2. **Repository-Level Secrets** - Shared across all workflows
   - Less secure: Same values used everywhere
   - No environment-specific protection
   - Only use if you need the same value in all environments

**Why Environment-Level Secrets?**

Since the workflows use `environment: DEV`, `environment: UAT`, and `environment: PROD` (see workflow files), and each environment should have different Azure credentials for security isolation, you **MUST** configure secrets at the environment level.

**Navigation:**
- **Environment secrets**: Settings → Environments → [Select DEV/UAT/PROD] → Add secret
- **Repository secrets**: Settings → Secrets and variables → Actions → New repository secret

⚠️ **Environment secrets override repository secrets with the same name.** If you configure both, the environment-scoped value will be used.

#### For DEV Environment

1. Navigate to your GitHub repository
2. Go to **Settings** → **Environments** → **DEV**
   - If the DEV environment doesn't exist, create it first: Settings → Environments → New environment
3. Scroll to **Environment secrets** section
4. Click **"Add secret"**
5. Add the following secrets one by one:

```
Name: AZURE_CLIENT_ID
Value: <app-id-from-azure-ad-output>

Name: AZURE_TENANT_ID
Value: <tenant-id-from-azure-ad-output>

Name: AZURE_SUBSCRIPTION_ID
Value: <subscription-id-from-azure>
```

#### For UAT Environment

1. Navigate to your GitHub repository
2. Go to **Settings** → **Environments** → **UAT**
   - If the UAT environment doesn't exist, create it first: Settings → Environments → New environment
3. Scroll to **Environment secrets** section
4. Click **"Add secret"**
5. Add these secrets with UAT suffix:

```
Name: AZURE_CLIENT_ID_UAT
Value: <uat-app-id-from-azure-ad-output>

Name: AZURE_TENANT_ID_UAT
Value: <uat-tenant-id-from-azure-ad-output>

Name: AZURE_SUBSCRIPTION_ID_UAT
Value: <uat-subscription-id-from-azure>
```

#### For PROD Environment

1. Navigate to your GitHub repository
2. Go to **Settings** → **Environments** → **PROD**
   - If the PROD environment doesn't exist, create it first: Settings → Environments → New environment
3. Scroll to **Environment secrets** section
4. Click **"Add secret"**
5. Add these secrets for production:

```
Name: AZURE_CLIENT_ID
Value: <prod-app-id-from-azure-ad-output>

Name: AZURE_TENANT_ID
Value: <prod-tenant-id-from-azure-ad-output>

Name: AZURE_SUBSCRIPTION_ID
Value: <prod-subscription-id-from-azure>

Name: SFTP_HOST
Value: <availity-sftp-hostname>

Name: SFTP_USERNAME
Value: <availity-sftp-username>

Name: SFTP_PASSWORD
Value: <availity-sftp-password>
```

**Important Notes:**
- ✅ **Use environment-level secrets** (Settings → Environments → [ENV] → Add secret)
- ❌ **Do NOT use repository-level secrets** for environment-specific values
- SFTP credentials are only required for PROD environment
- DEV and UAT environments use mock/test SFTP endpoints or skip SFTP configuration
- Each environment (DEV/UAT/PROD) should have different Azure AD applications for security isolation
- Never commit these values to source control
- Keep a secure backup in your organization's password vault

## Repository Variables Setup

Repository Variables are non-sensitive configuration values that can be shared across workflows.

### Step 1: Navigate to Variables Section

1. Go to your GitHub repository
2. Navigate to **Settings** → **Secrets and variables** → **Actions**
3. Click the **"Variables"** tab
4. Click **"New repository variable"**

### Step 2: Configure PROD Variables

Add the following variables for production deployment:

```
Name: AZURE_RG_NAME
Value: hipaa-attachments-prod-rg
Description: Production resource group name

Name: AZURE_LOCATION
Value: eastus
Description: Primary Azure region for resources

Name: AZURE_CONNECTOR_LOCATION
Value: eastus
Description: Region for API connectors (must match AZURE_LOCATION)

Name: BASE_NAME
Value: hipaa-attachments-prod
Description: Base name prefix for all Azure resources

Name: IA_NAME
Value: prod-integration-account
Description: Integration Account name for X12 processing

Name: SERVICE_BUS_NAME
Value: hipaa-attachments-prod-sb
Description: Service Bus namespace name

Name: STORAGE_SKU
Value: Standard_LRS
Description: Storage account replication type (Standard_LRS, Standard_GRS, etc.)
```

### Variable Naming Conventions

- Use UPPERCASE with underscores for all variables
- Include environment suffix if needed (e.g., `_DEV`, `_UAT`, `_PROD`)
- Be descriptive but concise
- Follow Azure resource naming conventions

### Optional Variables for DEV/UAT

DEV and UAT environments use hardcoded values in their workflow files for simplicity. If you need to override them, you can create environment-specific variables:

```
# DEV Environment
AZURE_LOCATION_DEV: westus
BASE_NAME_DEV: hipaa-attachments-dev

# UAT Environment
AZURE_LOCATION_UAT: westus
BASE_NAME_UAT: hipaa-attachments-uat
```

## Step-by-Step Setup for New Team Members

### Complete Setup Checklist

Follow this checklist when setting up a new team member or environment:

#### ✅ Prerequisites
- [ ] Azure subscription access with Contributor role
- [ ] GitHub repository access with admin permissions
- [ ] Azure CLI installed locally (`az --version` ≥ 2.77.0)
- [ ] Access to organization password vault for SFTP credentials

#### ✅ Azure Setup (per environment)
- [ ] Login to Azure: `az login`
- [ ] Set subscription: `az account set --subscription "<subscription-id>"`
- [ ] Run Azure AD app creation script (see Step 1 above)
- [ ] Verify service principal created: `az ad sp list --display-name "hipaa-attachments-*-github"`
- [ ] Verify federated credentials: `az ad app federated-credential list --id "<app-id>"`
- [ ] Verify role assignment: `az role assignment list --assignee "<app-id>"`
- [ ] Save output values securely

#### ✅ GitHub Secrets Configuration
- [ ] Navigate to repository Settings → Secrets and variables → Actions
- [ ] Add AZURE_CLIENT_ID (or _UAT variant)
- [ ] Add AZURE_TENANT_ID (or _UAT variant)
- [ ] Add AZURE_SUBSCRIPTION_ID (or _UAT variant)
- [ ] Add SFTP_HOST (PROD only)
- [ ] Add SFTP_USERNAME (PROD only)
- [ ] Add SFTP_PASSWORD (PROD only)
- [ ] Verify all secrets are listed (no values visible)

#### ✅ GitHub Variables Configuration
- [ ] Navigate to repository Settings → Secrets and variables → Actions → Variables tab
- [ ] Add AZURE_RG_NAME
- [ ] Add AZURE_LOCATION
- [ ] Add AZURE_CONNECTOR_LOCATION
- [ ] Add BASE_NAME
- [ ] Add IA_NAME
- [ ] Add SERVICE_BUS_NAME
- [ ] Add STORAGE_SKU
- [ ] Verify all variables are listed with correct values

#### ✅ Validation
- [ ] Run secrets validation (see Validation section)
- [ ] Trigger a test workflow run
- [ ] Monitor workflow for authentication success
- [ ] Verify resources are deployed to Azure
- [ ] Check Application Insights for any errors

## Validation

### Quick Validation Script

The repository includes a ready-to-use validation script that checks all secrets and variables:

```bash
# Run the validation script
./validate-github-secrets.sh
```

**Prerequisites:**
- GitHub CLI (`gh`) installed and authenticated
- Read access to the repository

**Script Output:**
- ✅ Lists all configured secrets and variables
- ❌ Identifies missing configuration items
- 📊 Provides validation summary
- 🔗 Links to setup documentation if issues found

### Manual Validation

If you prefer to validate manually or need to create a custom validation script, here's the basic approach:

```bash
# Example validation script (also available as validate-github-secrets.sh)
#!/bin/bash

REPO_OWNER="aurelianware"      # TODO: Update to your GitHub organization/username
REPO_NAME="hipaa-attachments"  # TODO: Update to your repository name

echo "Validating GitHub Secrets Configuration"
echo "========================================"
echo ""

# Check if gh CLI is installed
if ! command -v gh &> /dev/null; then
    echo "❌ GitHub CLI (gh) is not installed"
    echo "   Install: https://cli.github.com/"
    exit 1
fi

# Check if authenticated
if ! gh auth status &> /dev/null; then
    echo "❌ Not authenticated with GitHub CLI"
    echo "   Run: gh auth login"
    exit 1
fi

echo "Checking DEV environment secrets..."
REQUIRED_DEV_SECRETS=("AZURE_CLIENT_ID" "AZURE_TENANT_ID" "AZURE_SUBSCRIPTION_ID")

for secret in "${REQUIRED_DEV_SECRETS[@]}"; do
    if gh secret list -R "$REPO_OWNER/$REPO_NAME" | grep -q "^$secret"; then
        echo "✅ $secret"
    else
        echo "❌ $secret (missing)"
    fi
done

echo ""
echo "Checking UAT environment secrets..."
REQUIRED_UAT_SECRETS=("AZURE_CLIENT_ID_UAT" "AZURE_TENANT_ID_UAT" "AZURE_SUBSCRIPTION_ID_UAT")

for secret in "${REQUIRED_UAT_SECRETS[@]}"; do
    if gh secret list -R "$REPO_OWNER/$REPO_NAME" | grep -q "^$secret"; then
        echo "✅ $secret"
    else
        echo "❌ $secret (missing)"
    fi
done

echo ""
echo "Checking PROD environment secrets..."
REQUIRED_PROD_SECRETS=("AZURE_CLIENT_ID" "AZURE_TENANT_ID" "AZURE_SUBSCRIPTION_ID" "SFTP_HOST" "SFTP_USERNAME" "SFTP_PASSWORD")

for secret in "${REQUIRED_PROD_SECRETS[@]}"; do
    if gh secret list -R "$REPO_OWNER/$REPO_NAME" | grep -q "^$secret"; then
        echo "✅ $secret"
    else
        echo "❌ $secret (missing)"
    fi
done

echo ""
echo "Checking repository variables..."
REQUIRED_VARS=("AZURE_RG_NAME" "AZURE_LOCATION" "AZURE_CONNECTOR_LOCATION" "BASE_NAME" "IA_NAME" "SERVICE_BUS_NAME" "STORAGE_SKU")

for var in "${REQUIRED_VARS[@]}"; do
    if gh variable list -R "$REPO_OWNER/$REPO_NAME" | grep -q "^$var"; then
        echo "✅ $var"
    else
        echo "❌ $var (missing)"
    fi
done

echo ""
echo "Validation complete!"
```

Make the script executable and run it:

```bash
chmod +x validate-github-secrets.sh
./validate-github-secrets.sh
```

### Validate Azure OIDC Configuration

Use this script to validate OIDC federated credentials:

```bash
# Validate OIDC setup for an environment
ENV="prod"  # Change to: dev, uat, or prod
APP_NAME="hipaa-attachments-$ENV-github"

echo "Validating OIDC Configuration for $ENV"
echo "========================================"

# Get App ID
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv)

if [ -z "$APP_ID" ]; then
    echo "❌ Azure AD Application not found: $APP_NAME"
    exit 1
fi

echo "✅ Application found: $APP_ID"

# Check federated credentials
echo ""
echo "Federated Credentials:"
az ad app federated-credential list --id "$APP_ID" --query "[].{Name:name, Subject:subject, Issuer:issuer}" -o table

# Check role assignments
echo ""
echo "Role Assignments:"
az role assignment list --assignee "$APP_ID" --query "[].{Role:roleDefinitionName, Scope:scope}" -o table

echo ""
echo "Validation complete!"
```

### Test Deployment

The best validation is to run a test deployment:

1. **Test DEV Deployment:**
   ```bash
   # Trigger via GitHub CLI
   gh workflow run deploy-dev.yml -R <owner>/<repo>  # TODO: Replace <owner>/<repo> with your repository
   ```

2. **Test UAT Deployment:**
   ```bash
   # Push to release branch
   git checkout -b release/test-secrets-validation
   git push origin release/test-secrets-validation
   ```

3. **Test PROD Deployment:**
   ```bash
   # Trigger manually via GitHub Actions UI
   # Go to Actions → Deploy → Run workflow
   ```

Monitor the workflow runs for authentication success in the "Azure Login (OIDC)" step.

## Troubleshooting

### Common Issues

#### 1. "Failed to get OIDC token" Error

**Symptom:** Workflow fails with authentication error
```
Error: Azure login failed. Please check the credentials.
Failed to get OIDC token from GitHub
```

**Solutions:**
- Verify federated credential subject matches the branch pattern
- Check that the GitHub environment matches the credential configuration
- Ensure the workflow has `id-token: write` permission
- Verify the repository name in federated credential is correct

**Validation:**
```bash
APP_ID="<your-app-id>"
az ad app federated-credential list --id "$APP_ID" --query "[].{Name:name, Subject:subject}" -o table
```

#### 2. "Insufficient Permissions" Error

**Symptom:** Workflow fails when creating resources
```
Error: The client does not have authorization to perform action 'Microsoft.Resources/deployments/write'
```

**Solutions:**
- Verify Contributor role is assigned: `az role assignment list --assignee "<app-id>"`
- Check role assignment scope includes the target subscription/resource group
- Wait 5-10 minutes for role assignment to propagate

**Fix:**
```bash
APP_ID="<your-app-id>"
SUBSCRIPTION_ID="<your-subscription-id>"

az role assignment create \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID"
```

#### 3. Missing Secrets/Variables Error

**Symptom:** Workflow shows empty values or fails validation
```
Error: Required secret AZURE_CLIENT_ID not found
```

**Solutions:**
- Check secret spelling exactly matches (case-sensitive)
- Verify environment-specific suffixes (_UAT, _PROD)
- Ensure secret is created at repository level, not organization
- Re-add the secret if it appears in list but doesn't work

**Validation:**
```bash
# TODO: Replace <owner>/<repo> with your repository name
gh secret list -R <owner>/<repo>
gh variable list -R <owner>/<repo>
```

#### 4. SFTP Connection Failure (PROD)

**Symptom:** PROD deployment fails during SFTP connection test
```
Error: Unable to connect to SFTP server
```

**Solutions:**
- Verify SFTP_HOST includes correct hostname (no protocol prefix)
- Check SFTP_USERNAME and SFTP_PASSWORD are correct
- Confirm SFTP server allows connections from GitHub Actions IP ranges
- Test connection manually: `sftp <username>@<host>`

#### 5. Wrong Azure Subscription Deployed

**Symptom:** Resources created in wrong subscription

**Solutions:**
- Verify AZURE_SUBSCRIPTION_ID matches intended subscription
- Check multiple environments don't share the same subscription ID
- Use `az account list` to confirm subscription IDs

#### 6. Federated Credential Subject Mismatch

**Symptom:** Authentication works for some branches but not others

**Solutions:**
- DEV: Should use `ref:refs/heads/main`
- UAT: Should use `ref:refs/heads/release/*`
- PROD: Should use `ref:refs/heads/main` + environment match
- Create additional credentials for `workflow_dispatch` triggers

**Fix:**
```bash
APP_ID="<your-app-id>"
REPO_OWNER="aurelianware"  # TODO: Update to your GitHub organization/username
REPO_NAME="hipaa-attachments"  # TODO: Update to your repository name

# Add workflow_dispatch credential
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters "{
    \"name\": \"github-prod-dispatch\",
    \"issuer\": \"https://token.actions.githubusercontent.com\",
    \"subject\": \"repo:$REPO_OWNER/$REPO_NAME:environment:PROD\",
    \"audiences\": [\"api://AzureADTokenExchange\"]
  }"
```

### Getting Help

If you encounter issues not covered here:

1. **Check workflow logs:** Go to Actions tab → Select failed run → Expand failed step
2. **Review Azure Activity Log:** Azure Portal → Subscription → Activity Log
3. **Check Application Insights:** Look for authentication/deployment errors
4. **Review TROUBLESHOOTING.md:** See detailed debugging steps
5. **Ask in Team Channel:** Share error message and environment details

## Security Best Practices

### Secrets Management

1. **Never commit secrets to source control**
   - Use `.gitignore` for any local credential files
   - Review commits before pushing to catch accidental leaks
   - Use git-secrets or similar tools to prevent secret commits

2. **Rotate secrets regularly**
   - Schedule quarterly rotation for SFTP credentials
   - Rotate Azure AD application credentials annually
   - Update GitHub secrets immediately after rotation

3. **Use separate credentials per environment**
   - DEV, UAT, and PROD must have different Azure AD applications
   - Never reuse SFTP credentials across environments
   - Different subscriptions provide better isolation

4. **Limit access to secrets**
   - Only repository administrators should access secrets
   - Use GitHub environment protection rules
   - Require approvals for PROD deployments

5. **Audit secret usage**
   - Review workflow run logs for unexpected secret access
   - Monitor Azure AD application sign-in logs
   - Set up alerts for authentication failures

### OIDC Best Practices

1. **Use federated credentials instead of service principal secrets**
   - No secret storage required
   - Automatic token rotation
   - Better audit trail

2. **Scope role assignments appropriately**
   - Use resource group scope instead of subscription when possible
   - Apply least-privilege principle
   - Create custom roles for specific scenarios

3. **Configure multiple subject patterns**
   - Branch-specific: `ref:refs/heads/main`
   - Wildcard: `ref:refs/heads/release/*`
   - Environment: `repo:owner/name:environment:PROD`

4. **Monitor federated credential usage**
   - Review Azure AD sign-in logs
   - Set up alerts for authentication failures
   - Verify IP addresses of sign-ins match GitHub Actions

### Compliance Considerations

For HIPAA compliance:

1. **Encrypt secrets at rest:** GitHub Secrets are encrypted by default
2. **Use TLS for all connections:** OIDC and Azure APIs use TLS 1.2+
3. **Audit all access:** Enable Azure Activity Log and GitHub audit log
4. **Implement change control:** Require PR reviews for workflow changes
5. **Document access:** Keep record of who has access to secrets

### Additional Resources

- [GitHub Actions Security Hardening](https://docs.github.com/en/actions/security-guides/security-hardening-for-github-actions)
- [Azure OIDC with GitHub Actions](https://docs.microsoft.com/en-us/azure/developer/github/connect-from-azure)
- [Logic Apps Security Best Practices](https://docs.microsoft.com/en-us/azure/logic-apps/logic-apps-securing-a-logic-app)
- [HIPAA Compliance on Azure](https://docs.microsoft.com/en-us/azure/compliance/offerings/offering-hipaa-us)

---

## Azure Key Vault Secret Migration

### Overview

For production PHI workloads, migrate from GitHub Secrets to Azure Key Vault for centralized secret management with enhanced security features:
- HSM-backed keys (FIPS 140-2 Level 2)
- Soft delete and purge protection
- RBAC authorization with managed identities
- Comprehensive audit logging
- Private endpoint network isolation

### Migration Strategy

#### Phase 1: Deploy Key Vault Infrastructure

```bash
# Deploy Key Vault module
az deployment group create \
  --resource-group "pchp-attachments-prod-rg" \
  --template-file infra/modules/keyvault.bicep \
  --parameters keyVaultName="hipaa-attachments-prod-kv" \
                location="eastus" \
                skuName="premium" \
                enableRbacAuthorization=true \
                enableSoftDelete=true \
                softDeleteRetentionInDays=90 \
                enablePurgeProtection=true \
                publicNetworkAccess="Disabled"
```

#### Phase 2: Migrate Secrets to Key Vault

```bash
# Set variables
KV_NAME="hipaa-attachments-prod-kv"

# Add SFTP credentials
az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "sftp-host" \
  --value "${SFTP_HOST}"

az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "sftp-username" \
  --value "${SFTP_USERNAME}"

az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "sftp-password" \
  --value "${SFTP_PASSWORD}"

# Add QNXT API credentials
az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "qnxt-api-base-url" \
  --value "https://qnxt-api-prod.example.com"

az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "qnxt-api-client-id" \
  --value "${QNXT_CLIENT_ID}"

az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "qnxt-api-client-secret" \
  --value "${QNXT_CLIENT_SECRET}"

# Verify secrets
az keyvault secret list --vault-name "$KV_NAME" --output table
```

#### Phase 3: Configure Logic App Access

```bash
# Get Logic App managed identity principal ID
LOGIC_APP_NAME="hipaa-attachments-prod-la"
PRINCIPAL_ID=$(az webapp identity show \
  --resource-group "pchp-attachments-prod-rg" \
  --name "$LOGIC_APP_NAME" \
  --query principalId -o tsv)

# Assign "Key Vault Secrets User" role
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Key Vault Secrets User" \
  --scope "/subscriptions/{subscription-id}/resourceGroups/pchp-attachments-prod-rg/providers/Microsoft.KeyVault/vaults/$KV_NAME"

# Verify role assignment
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --scope "/subscriptions/{subscription-id}/resourceGroups/pchp-attachments-prod-rg/providers/Microsoft.KeyVault/vaults/$KV_NAME" \
  --output table
```

#### Phase 4: Update Logic App Workflows

Update workflow JSON files to reference Key Vault secrets using `@keyvault()` expression:

**Before (GitHub Secret):**
```json
{
  "type": "Http",
  "inputs": {
    "uri": "@parameters('qnxt_base_url')/api/claims",
    "authentication": {
      "type": "ClientCredentials",
      "secret": "@parameters('qnxt_client_secret')"
    }
  }
}
```

**After (Key Vault):**
```json
{
  "type": "Http",
  "inputs": {
    "uri": "@keyvault('https://hipaa-attachments-prod-kv.vault.azure.net/secrets/qnxt-api-base-url')/api/claims",
    "authentication": {
      "type": "ClientCredentials",
      "secret": "@keyvault('https://hipaa-attachments-prod-kv.vault.azure.net/secrets/qnxt-api-client-secret')"
    }
  }
}
```

#### Phase 5: Validation and Testing

```bash
# Test Key Vault access from Logic App
# Trigger a workflow and verify it retrieves secrets successfully

# Check Application Insights for Key Vault access
az monitor app-insights query \
  --app "hipaa-attachments-prod-ai" \
  --resource-group "pchp-attachments-prod-rg" \
  --analytics-query "dependencies | where timestamp > ago(1h) | where target contains 'vault.azure.net' | project timestamp, name, target, resultCode | order by timestamp desc" \
  --output table

# Expected: All Key Vault calls return 200 OK
```

#### Phase 6: Cleanup (After Validation)

```bash
# Remove secrets from GitHub (keep OIDC secrets for deployment)
# Navigate to: Repository → Settings → Secrets and variables → Actions
# Delete: SFTP_HOST, SFTP_USERNAME, SFTP_PASSWORD
# Keep: AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID

# Update deployment workflows to remove secret parameters
# Remove from workflow YAML files:
# - SFTP_HOST: ${{ secrets.SFTP_HOST }}
# - SFTP_USERNAME: ${{ secrets.SFTP_USERNAME }}
# - SFTP_PASSWORD: ${{ secrets.SFTP_PASSWORD }}
```

### Secret Rotation Procedures

#### Automated Rotation (Recommended)

```bash
# Create rotation script
cat > rotate-secrets.sh <<'EOF'
#!/bin/bash
set -euo pipefail

KV_NAME="hipaa-attachments-prod-kv"

# Rotate SFTP password
NEW_SFTP_PASSWORD=$(openssl rand -base64 32)
az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "sftp-password" \
  --value "$NEW_SFTP_PASSWORD"

# Update SFTP server with new password (implementation specific)

# Rotate QNXT API client secret
NEW_CLIENT_SECRET=$(openssl rand -base64 32)
az keyvault secret set \
  --vault-name "$KV_NAME" \
  --name "qnxt-api-client-secret" \
  --value "$NEW_CLIENT_SECRET"

# Update QNXT API OAuth client (implementation specific)

echo "✅ Secrets rotated successfully"
EOF

chmod +x rotate-secrets.sh
```

#### Rotation Schedule

| Secret Type | Frequency | Owner | Automation |
|-------------|-----------|-------|------------|
| SFTP Password | Quarterly | Security Team | Automated script |
| QNXT API Client Secret | Quarterly | Integration Team | Automated script |
| OAuth Tokens | Daily (automatic) | Azure AD | Built-in Azure AD |

### Monitoring and Compliance

#### Audit Log Queries

```kusto
// Query Key Vault secret access
AzureDiagnostics
| where ResourceType == "VAULTS"
| where OperationName == "SecretGet"
| where TimeGenerated > ago(30d)
| project TimeGenerated, CallerIPAddress, identity_claim_appid_g, ResultType
| order by TimeGenerated desc

// Alert on failed access attempts
AzureDiagnostics
| where ResourceType == "VAULTS"
| where ResultType != "Success"
| where TimeGenerated > ago(1h)
| project TimeGenerated, OperationName, CallerIPAddress, ResultType, ResultSignature
```

#### Compliance Reporting

- **Audit Logs**: 365-day retention in Log Analytics
- **Secret Versions**: All versions retained indefinitely
- **Access Reviews**: Quarterly review of RBAC permissions
- **Rotation History**: Tracked via Key Vault secret versions

### Troubleshooting

#### Issue: Logic App cannot access Key Vault

**Solutions:**
1. Verify managed identity is enabled on Logic App
2. Check RBAC role assignment exists
3. Ensure Key Vault network settings allow Logic App access
4. Verify Key Vault secret names match workflow references

```bash
# Verify managed identity
az webapp identity show \
  --resource-group "pchp-attachments-prod-rg" \
  --name "hipaa-attachments-prod-la"

# Check role assignments
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --output table
```

#### Issue: Key Vault reference not resolving

**Solutions:**
1. Verify Key Vault URI format: `@keyvault('https://{vault}.vault.azure.net/secrets/{secret-name}')`
2. Check secret exists: `az keyvault secret show --vault-name "{vault}" --name "{secret}"`
3. Ensure no typos in secret name
4. Verify Logic App has network access to Key Vault (private endpoint configuration)

### Security Best Practices

1. **Use Private Endpoints**: Isolate Key Vault from public internet
2. **Enable Soft Delete**: 90-day recovery window for deleted secrets
3. **Enable Purge Protection**: Prevent permanent deletion during retention
4. **Use RBAC Authorization**: Prefer RBAC over access policies
5. **Rotate Secrets Regularly**: Implement automated rotation procedures
6. **Monitor Access**: Set up alerts for failed authentication attempts
7. **Audit Regularly**: Review access logs monthly

For comprehensive security guidance, see:
- **[SECURITY-HARDENING.md](SECURITY-HARDENING.md)** - Complete security implementation guide
- **[docs/HIPAA-COMPLIANCE-MATRIX.md](docs/HIPAA-COMPLIANCE-MATRIX.md)** - Regulatory compliance mapping

---

**Document Version:** 1.1  
**Last Updated:** 2024-11-19  
**Maintained By:** HIPAA Attachments Team
