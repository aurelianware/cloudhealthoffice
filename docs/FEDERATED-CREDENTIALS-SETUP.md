# Federated Credentials Setup for GitHub Actions Deployment

## Overview

This guide provides comprehensive instructions for setting up Azure federated credentials (OIDC) to enable secure GitHub Actions deployment of the Cloud Health Office static website to Azure Static Web Apps.

**Purpose**: Configure passwordless authentication between GitHub Actions and Azure using OpenID Connect (OIDC) federated credentials, eliminating the need to store long-lived Azure credentials as GitHub secrets.

**Scope**: This guide focuses specifically on the **deployment authentication** for GitHub Actions workflows, which is separate from the **user authentication** configured for the static website application.

> **🔐 Important:** This guide covers OIDC credentials for GitHub → Azure authentication. For runtime secrets (SFTP credentials, API keys), see [Secrets Management Guide](../DEPLOYMENT-SECRETS-SETUP.md) and [Key Vault Migration Guide](./SECRETS-MIGRATION-GUIDE.md).

## Why Federated Credentials?

Azure federated credentials using OIDC provide several security advantages over traditional service principal secrets:

✅ **No Long-Lived Secrets** - Tokens are short-lived (typically 1 hour) and automatically rotated  
✅ **Reduced Attack Surface** - No credentials stored in GitHub secrets that could be compromised  
✅ **Audit Trail** - All authentication attempts logged in Azure AD  
✅ **Automatic Expiration** - Tokens expire automatically, limiting exposure window  
✅ **GitHub-Native** - Built into GitHub Actions with `id-token: write` permission  

## Deployment App vs User Authentication App

It's important to understand the distinction between two different Azure AD applications:

### Deployment App Registration (This Guide)

**Purpose**: Enables GitHub Actions workflows to deploy code to Azure resources

**Authentication Flow**: GitHub Actions → OIDC Token → Azure AD → Azure Resources

**Required Permissions**:
- **Website Contributor** role on Static Web App resource
- **Contributor** role on resource group (for deployments)
- **Key Vault Secrets User** role on deployment Key Vault (for retrieving runtime secrets)

**Federated Credential Subject Pattern**:
```
repo:aurelianware/cloudhealthoffice:ref:refs/heads/main
```

**GitHub Secrets Required** (ONLY these - runtime secrets go in Key Vault):
- `AZURE_CLIENT_ID` - The deployment app's Application ID
- `AZURE_TENANT_ID` - Your Azure AD tenant ID
- `AZURE_SUBSCRIPTION_ID` - Your Azure subscription ID

**Runtime Secrets** (stored in Azure Key Vault, not GitHub):
- `SFTP_HOST`, `SFTP_USERNAME`, `SFTP_PASSWORD` - Stored in Key Vault for enhanced security
- See [Key Vault Migration Guide](./SECRETS-MIGRATION-GUIDE.md) for setup

### User Authentication App Registration (Separate)

**Purpose**: Enables end users to log in to the static website portal

**Authentication Flow**: User Browser → OAuth 2.0 → Azure AD → Website Session

**Required Permissions**:
- Typically just `User.Read` for Microsoft Graph API
- No Azure resource permissions needed

**Configuration**: Handled separately in Azure Static Web Apps authentication settings

**Note**: User authentication credentials (for portal login) should be configured in Azure Static Web Apps settings, NOT in GitHub Secrets or Key Vault. See Azure Static Web Apps documentation for multi-tenant Azure AD setup.

---

## Prerequisites

Before you begin, ensure you have:

- [ ] **Azure Subscription** with Owner or User Access Administrator role
- [ ] **GitHub Repository** admin access (`aurelianware/cloudhealthoffice`)
- [ ] **Azure CLI** installed locally (`az --version` ≥ 2.50.0)
- [ ] **Permissions to create App Registrations** in Azure AD
- [ ] **Permissions to assign Azure roles** (Contributor, Website Contributor, Key Vault Secrets User)

## Step-by-Step Setup Guide

### Step 1: Create Azure AD Application for Deployment

Create a dedicated Azure AD application that GitHub Actions will use to authenticate.

#### Using Azure Portal

1. **Navigate to Azure Active Directory**
   - Go to [Azure Portal](https://portal.azure.com)
   - Search for "Azure Active Directory"
   - Click **App registrations** in left sidebar

2. **Create New Registration**
   - Click **+ New registration**
   - Configure as follows:
     - **Name**: `cloudhealthoffice-static-site-deployment`
     - **Supported account types**: Accounts in this organizational directory only (Single tenant)
     - **Redirect URI**: Leave empty (not needed for service authentication)
   - Click **Register**

3. **Save Application (Client) ID**
   - On the app's **Overview** page, copy the **Application (client) ID**
   - Save this value - you'll need it for GitHub secrets as `AZURE_CLIENT_ID`

4. **Save Directory (Tenant) ID**
   - Also on the **Overview** page, copy the **Directory (tenant) ID**
   - Save this value - you'll need it for GitHub secrets as `AZURE_TENANT_ID`

#### Using Azure CLI

```bash
# Create the application
az ad app create \
  --display-name "cloudhealthoffice-static-site-deployment"

# Get the Application ID
APP_ID=$(az ad app list \
  --display-name "cloudhealthoffice-static-site-deployment" \
  --query "[0].appId" -o tsv)

echo "Application (Client) ID: $APP_ID"

# Create service principal
az ad sp create --id "$APP_ID"

# Get Tenant ID
TENANT_ID=$(az account show --query tenantId -o tsv)
echo "Directory (Tenant) ID: $TENANT_ID"

# Get Subscription ID
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
echo "Subscription ID: $SUBSCRIPTION_ID"
```

**💾 Save these values** for later use:
- Application (Client) ID: `$APP_ID`
- Directory (Tenant) ID: `$TENANT_ID`
- Subscription ID: `$SUBSCRIPTION_ID`

### Step 2: Configure Federated Credential

Configure the federated credential to trust GitHub Actions tokens from your repository.

#### Using Azure Portal

1. **Navigate to Certificates & Secrets**
   - In your app registration, click **Certificates & secrets** in left sidebar
   - Click the **Federated credentials** tab
   - Click **+ Add credential**

2. **Configure Federated Credential**
   - **Federated credential scenario**: GitHub Actions deploying Azure resources
   - **Organization**: `aurelianware`
   - **Repository**: `cloudhealthoffice`
   - **Entity type**: Branch
   - **GitHub branch name**: `main`
   - **Name**: `cloudhealthoffice-main-branch`
   - **Description**: `GitHub Actions deployment from main branch`
   - Click **Add**

#### Using Azure CLI

```bash
# Set variables
APP_ID="<your-app-id-from-step-1>"
REPO_OWNER="aurelianware"
REPO_NAME="cloudhealthoffice"
BRANCH="main"

# Create federated credential for main branch
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters '{
    "name": "cloudhealthoffice-main-branch",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:'"$REPO_OWNER"'/'"$REPO_NAME"':ref:refs/heads/'"$BRANCH"'",
    "description": "GitHub Actions deployment from main branch",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

#### Verify Federated Credential

```bash
# List federated credentials to verify
az ad app federated-credential list \
  --id "$APP_ID" \
  --query "[].{Name:name, Subject:subject}" -o table
```

**Expected Output**:
```
Name                            Subject
------------------------------  ------------------------------------------------------
cloudhealthoffice-main-branch   repo:aurelianware/cloudhealthoffice:ref:refs/heads/main
```

**⚠️ Critical**: The `subject` field must **exactly match** the pattern:
```
repo:aurelianware/cloudhealthoffice:ref:refs/heads/main
```

### Step 3: Assign Azure Roles

Grant the deployment application permission to manage Azure resources.

#### Find Your Static Web App Resource

First, identify your Static Web App resource:

```bash
# List Static Web Apps
az staticwebapp list --query "[].{Name:name, ResourceGroup:resourceGroup, Location:location}" -o table

# Or if you know the resource group
RESOURCE_GROUP="<your-resource-group-name>"
az staticwebapp list --resource-group "$RESOURCE_GROUP" -o table
```

#### Assign Website Contributor Role

```bash
# Set variables
APP_ID="<your-app-id>"
RESOURCE_GROUP="<your-resource-group-name>"
SUBSCRIPTION_ID="<your-subscription-id>"

# Assign Contributor role at resource group level
az role assignment create \
  --assignee "$APP_ID" \
  --role "Contributor" \
  --scope "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"

# Verify role assignment
az role assignment list \
  --assignee "$APP_ID" \
  --output table
```

**Expected Output**:
```
Principal                             Role         Scope
------------------------------------ ------------ --------------------------------------------------
cloudhealthoffice-static-site-dep... Contributor  /subscriptions/.../resourceGroups/...
```

### Step 4: Configure GitHub Secrets

Add the required secrets to your GitHub repository.

#### Using GitHub Web Interface

1. **Navigate to Repository Settings**
   - Go to `https://github.com/aurelianware/cloudhealthoffice`
   - Click **Settings** tab
   - Click **Secrets and variables** → **Actions** in left sidebar

2. **Add Repository Secrets**
   
   Click **New repository secret** for each of the following:

   **Secret 1: AZURE_CLIENT_ID**
   ```
   Name: AZURE_CLIENT_ID
   Value: <your-application-client-id>
   ```

   **Secret 2: AZURE_TENANT_ID**
   ```
   Name: AZURE_TENANT_ID
   Value: <your-directory-tenant-id>
   ```

   **Secret 3: AZURE_SUBSCRIPTION_ID**
   ```
   Name: AZURE_SUBSCRIPTION_ID
   Value: <your-subscription-id>
   ```

#### Using GitHub CLI

```bash
# Install GitHub CLI if not already installed
# https://cli.github.com/

# Authenticate with GitHub
gh auth login

# Set repository
REPO="aurelianware/cloudhealthoffice"

# Add secrets
gh secret set AZURE_CLIENT_ID --repo "$REPO" --body "$APP_ID"
gh secret set AZURE_TENANT_ID --repo "$REPO" --body "$TENANT_ID"
gh secret set AZURE_SUBSCRIPTION_ID --repo "$REPO" --body "$SUBSCRIPTION_ID"

# Verify secrets are set (values are masked)
gh secret list --repo "$REPO"
```

**Expected Output**:
```
AZURE_CLIENT_ID        Updated 2026-02-02
AZURE_SUBSCRIPTION_ID  Updated 2026-02-02
AZURE_TENANT_ID        Updated 2026-02-02
```

### Step 5: Configure GitHub Variables

Add the required variables to your GitHub repository.

#### Using GitHub Web Interface

1. **Navigate to Variables**
   - In **Settings** → **Secrets and variables** → **Actions**
   - Click the **Variables** tab

2. **Add Repository Variables**

   Click **New repository variable** for each:

   **Variable 1: AZURE_RG_NAME**
   ```
   Name: AZURE_RG_NAME
   Value: <your-resource-group-name>
   ```

   **Variable 2: BASE_NAME**
   ```
   Name: BASE_NAME
   Value: <your-base-name>
   ```
   (This should match your resource naming convention, e.g., `cloudhealthoffice`)

#### Using GitHub CLI

```bash
# Add variables
gh variable set AZURE_RG_NAME --repo "$REPO" --body "<your-resource-group-name>"
gh variable set BASE_NAME --repo "$REPO" --body "<your-base-name>"

# Verify variables
gh variable list --repo "$REPO"
```

### Step 6: Test the Configuration

Test the federated credential setup by manually triggering the deployment workflow.

1. **Navigate to GitHub Actions**
   - Go to `https://github.com/aurelianware/cloudhealthoffice/actions`
   - Click on **Deploy Static Site with Custom Domain** workflow

2. **Run Workflow Manually**
   - Click **Run workflow** button
   - Select branch: `main`
   - Click **Run workflow**

3. **Monitor Workflow Execution**
   - Watch the workflow run in real-time
   - Check the **Validate Required Secrets** step - should show all ✅
   - Check the **Azure Login (OIDC)** step - should succeed without errors

4. **Verify Successful Authentication**
   
   In the workflow logs, you should see:
   ```
   ✅ AZURE_CLIENT_ID is configured
   ✅ AZURE_TENANT_ID is configured
   ✅ AZURE_SUBSCRIPTION_ID is configured
   ✅ AZURE_RG_NAME = <your-resource-group>
   ✅ BASE_NAME = <your-base-name>
   ✅ Validation PASSED
   ```

   And in the Azure Login step:
   ```
   Login successful
   ```

## Troubleshooting Common Issues

### Issue 1: "AADSTS700016: Application not found in directory"

**Symptoms**: Azure Login step fails with error:
```
AADSTS700016: Application with identifier '<client-id>' was not found in the directory
```

**Causes**:
- Application ID (Client ID) is incorrect
- Application was created in a different Azure AD tenant
- Service principal was not created

**Solutions**:

1. **Verify Application Exists**:
   ```bash
   # Check if app exists
   az ad app show --id "$APP_ID"
   ```

2. **Verify Tenant ID**:
   ```bash
   # Get your current tenant
   az account show --query tenantId -o tsv
   
   # Compare with AZURE_TENANT_ID secret
   ```

3. **Create Service Principal** (if missing):
   ```bash
   az ad sp create --id "$APP_ID"
   ```

### Issue 2: "AADSTS70021: No matching federated identity record found"

**Symptoms**: Azure Login step fails with:
```
AADSTS70021: No matching federated identity record found for presented assertion subject
```

**Causes**:
- Federated credential subject doesn't match the GitHub repository/branch
- Workflow is running from a different branch than configured
- Typo in repository owner or name

**Solutions**:

1. **Verify Federated Credential Subject**:
   ```bash
   az ad app federated-credential list --id "$APP_ID" \
     --query "[].subject" -o tsv
   ```

   **Expected**: `repo:aurelianware/cloudhealthoffice:ref:refs/heads/main`

2. **Check Workflow Branch**:
   - Ensure workflow is triggered from `main` branch
   - Check workflow file for branch filters:
     ```yaml
     on:
       push:
         branches: ["main"]
     ```

3. **Update Federated Credential** (if incorrect):
   ```bash
   # Delete old credential
   CRED_ID=$(az ad app federated-credential list --id "$APP_ID" \
     --query "[0].id" -o tsv)
   az ad app federated-credential delete --id "$APP_ID" \
     --federated-credential-id "$CRED_ID"
   
   # Create new credential with correct subject
   az ad app federated-credential create --id "$APP_ID" \
     --parameters '{
       "name": "cloudhealthoffice-main-branch",
       "issuer": "https://token.actions.githubusercontent.com",
       "subject": "repo:aurelianware/cloudhealthoffice:ref:refs/heads/main",
       "audiences": ["api://AzureADTokenExchange"]
     }'
   ```

### Issue 3: "AuthorizationFailed: The client does not have authorization to perform action"

**Symptoms**: Deployment fails after successful login:
```
AuthorizationFailed: The client '<client-id>' with object id '<object-id>' 
does not have authorization to perform action 'Microsoft.Web/staticSites/read'
```

**Causes**:
- Service principal lacks required Azure roles
- Role assignment hasn't propagated yet (can take 5-10 minutes)
- Wrong scope for role assignment

**Solutions**:

1. **Verify Role Assignments**:
   ```bash
   az role assignment list --assignee "$APP_ID" --output table
   ```

   **Expected**: At least "Contributor" role on resource group or subscription

2. **Assign Missing Roles**:
   ```bash
   # Assign Contributor role
   az role assignment create \
     --assignee "$APP_ID" \
     --role "Contributor" \
     --scope "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"
   ```

3. **Wait for Propagation**:
   - Azure role assignments can take 5-10 minutes to propagate
   - Re-run the workflow after waiting

### Issue 4: "Unable to get ACTIONS_ID_TOKEN_REQUEST_URL env variable"

**Symptoms**: Workflow fails before Azure Login:
```
Error: Unable to get ACTIONS_ID_TOKEN_REQUEST_URL env variable
```

**Causes**:
- Workflow file missing `id-token: write` permission
- GitHub Actions OIDC provider is not enabled (rare)

**Solutions**:

1. **Verify Workflow Permissions**:
   
   Check `.github/workflows/deploy-static-site.yml` contains:
   ```yaml
   permissions:
     id-token: write
     contents: read
   ```

2. **Add Missing Permission** (if not present):
   ```yaml
   permissions:
     id-token: write  # Required for OIDC
     contents: read
     pull-requests: write  # Optional, for PR comments
   ```

### Issue 5: Secrets Not Found in Workflow

**Symptoms**: Validation step shows:
```
❌ AZURE_CLIENT_ID is not set or empty
```

**Causes**:
- Secret names don't match workflow references
- Secrets were set in wrong environment (not repository-level)
- Secret values are actually empty strings

**Solutions**:

1. **Verify Secret Names**:
   ```bash
   # List all secrets
   gh secret list --repo "aurelianware/cloudhealthoffice"
   ```

   **Expected**: Exact names `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`

2. **Check Workflow References**:
   ```bash
   # Verify workflow uses correct secret names
   grep "secrets\." .github/workflows/deploy-static-site.yml
   ```

3. **Re-create Secrets** (if incorrect):
   ```bash
   # Delete and re-create
   gh secret delete AZURE_CLIENT_ID --repo "aurelianware/cloudhealthoffice"
   gh secret set AZURE_CLIENT_ID --repo "aurelianware/cloudhealthoffice" --body "$APP_ID"
   ```

### Issue 6: Multiple Federated Credentials Conflict

**Symptoms**: Authentication works intermittently or from unexpected branches

**Causes**:
- Multiple federated credentials configured for same or overlapping subjects
- Old credentials not cleaned up after changes

**Solutions**:

1. **List All Federated Credentials**:
   ```bash
   az ad app federated-credential list --id "$APP_ID" -o table
   ```

2. **Remove Unnecessary Credentials**:
   ```bash
   # Delete specific credential by ID
   az ad app federated-credential delete \
     --id "$APP_ID" \
     --federated-credential-id "<credential-id>"
   ```

3. **Keep Only Main Branch Credential**:
   - For production static site deployment, typically only need `main` branch
   - Remove any test or development credentials

## Validation Checklist

Use this checklist to verify your setup is complete:

- [ ] Azure AD application created (`cloudhealthoffice-static-site-deployment`)
- [ ] Service principal created from application
- [ ] Federated credential configured with subject: `repo:aurelianware/cloudhealthoffice:ref:refs/heads/main`
- [ ] Federated credential issuer is `https://token.actions.githubusercontent.com`
- [ ] Federated credential audiences includes `api://AzureADTokenExchange`
- [ ] Contributor role assigned to service principal on resource group
- [ ] GitHub secret `AZURE_CLIENT_ID` set to Application (Client) ID
- [ ] GitHub secret `AZURE_TENANT_ID` set to Directory (Tenant) ID
- [ ] GitHub secret `AZURE_SUBSCRIPTION_ID` set to Subscription ID
- [ ] GitHub variable `AZURE_RG_NAME` set to resource group name
- [ ] GitHub variable `BASE_NAME` set to resource naming prefix
- [ ] Workflow file has `id-token: write` permission
- [ ] Test workflow run succeeds with authentication

## Security Best Practices

### Principle of Least Privilege

- **Resource Group Scope**: Assign roles at resource group level, not subscription
  ```bash
  # Good: Resource group scope
  --scope "/subscriptions/$SUB_ID/resourceGroups/$RG_NAME"
  
  # Avoid: Subscription-wide scope (unless necessary)
  --scope "/subscriptions/$SUB_ID"
  ```

- **Specific Roles**: Use specific roles like `Website Contributor` when possible instead of broad `Contributor`

### Regular Auditing

```bash
# Review role assignments quarterly
az role assignment list --assignee "$APP_ID" --all --output table

# Check last sign-in activity
az ad sp show --id "$APP_ID" --query "signInActivity"
```

### Federated Credential Hygiene

- **Delete Unused Credentials**: Remove test or old branch credentials
- **Descriptive Names**: Use clear, descriptive names like `cloudhealthoffice-main-branch`
- **Document Changes**: Keep track of credential changes in deployment docs

### Monitoring

Enable Azure AD sign-in logs to monitor authentication:

1. Go to **Azure AD** → **Monitoring** → **Sign-in logs**
2. Filter by application: `cloudhealthoffice-static-site-deployment`
3. Review for:
   - Failed authentication attempts
   - Unexpected source IPs
   - Authentication from unexpected repositories/branches

## Advanced Configuration

### Multiple Branch Support

If you need to deploy from multiple branches (e.g., `main` and `develop`):

```bash
# Add credential for develop branch
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters '{
    "name": "cloudhealthoffice-develop-branch",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:aurelianware/cloudhealthoffice:ref:refs/heads/develop",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

### Pull Request Deployments

For preview deployments from pull requests:

```bash
# Add credential for pull requests
az ad app federated-credential create \
  --id "$APP_ID" \
  --parameters '{
    "name": "cloudhealthoffice-pull-requests",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:aurelianware/cloudhealthoffice:pull_request",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

Update workflow trigger:
```yaml
on:
  push:
    branches: ["main"]
  pull_request:
    branches: ["main"]
```

### Environment-Specific Credentials

For separate dev/staging/prod environments:

```bash
# Create separate app for each environment
az ad app create --display-name "cloudhealthoffice-static-site-prod"
az ad app create --display-name "cloudhealthoffice-static-site-staging"

# Configure environment-specific federated credentials
# Use GitHub environments feature
```

## Additional Resources

### Documentation

- [Azure Federated Credentials Overview](https://docs.microsoft.com/azure/active-directory/develop/workload-identity-federation)
- [GitHub Actions OIDC](https://docs.github.com/actions/deployment/security-hardening-your-deployments/about-security-hardening-with-openid-connect)
- [Azure Static Web Apps Deployment](https://docs.microsoft.com/azure/static-web-apps/github-actions-workflow)

### Related Guides

- [GITHUB-ACTIONS-SETUP.md](../GITHUB-ACTIONS-SETUP.md) - General GitHub Actions configuration
- [DEPLOYMENT.md](../DEPLOYMENT.md) - Deployment procedures
- [TROUBLESHOOTING.md](../TROUBLESHOOTING.md) - General troubleshooting guide

### Support

For issues not covered in this guide:

1. **Review Workflow Logs**: GitHub Actions → Failed Workflow → Expand steps
2. **Check Azure Activity Log**: Azure Portal → Resource Group → Activity Log
3. **Run Validation Script**: `./scripts/validate-deployment-auth.sh`
4. **Consult Repository Issues**: Check for similar issues at `https://github.com/aurelianware/cloudhealthoffice/issues`

---

**Last Updated**: 2026-02-02  
**Applies To**: Cloud Health Office v3.0.0+  
**Workflow**: `.github/workflows/deploy-static-site.yml`
