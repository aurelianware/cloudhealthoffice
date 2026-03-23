# Production Deployment Guide - Cloud Health Office

## Overview

This guide covers the simplified, production-only deployment pipeline for
Cloud Health Office. The deployment has been streamlined to focus on a single
production environment with automated infrastructure provisioning and secure
defaults.

## Key Features

### 🔒 Automated Infrastructure Provisioning

- **App Registration**: Automatically created if missing with multi-tenant
  support
- **Service Principal**: Automatically created with required RBAC roles
- **Key Vault**: Deployment Key Vault for storing secrets (SFTP credentials,
  API keys)

### 🔐 Secure Defaults

- **Three-tier secret fallback**: Azure Key Vault → GitHub Secrets →
  Secure placeholders
- **No deployment failures**: Pipeline continues with secure defaults if
  secrets are missing
- **OIDC authentication**: No long-lived credentials in GitHub Secrets

### 🌐 Static Web App Deployment

- **Automatic retry logic**: Up to 10 attempts with 10-15 second intervals
- **Token retrieval fallback**: Uses GitHub Secrets or retrieves from Azure CLI
- **Deployment verification**: Automatic health checks after deployment

### ✅ Production-Only Focus

- **Simplified workflow**: Single production deployment on main branch
- **No environment drift**: DEV/UAT workflows disabled
- **Single resource group**: `rg-cloudhealthoffice-prod`

## Prerequisites

### Required GitHub Secrets

**Minimum Required (OIDC Authentication)**:

- `AZURE_CLIENT_ID` - Application (Client) ID from Azure AD app registration
- `AZURE_TENANT_ID` - Azure AD Tenant ID
- `AZURE_SUBSCRIPTION_ID` - Azure Subscription ID

**Optional (If not using Key Vault)**:

- `SFTP_HOST` - SFTP server hostname
- `SFTP_USERNAME` - SFTP username
- `SFTP_PASSWORD` - SFTP password

**Static Web App (Optional)**:

- `AZURE_STATIC_WEB_APPS_API_TOKEN_KIND_WAVE_053FF9E1E` - Deployment token
  (auto-retrieved if missing)

### Required GitHub Variables

**With Secure Defaults** (optional):

- `AZURE_RG_NAME` - Default: `rg-cloudhealthoffice-prod`
- `BASE_NAME` - Default: `cloudhealthoffice`
- `AZURE_LOCATION` - Default: `eastus`
- `AZURE_CONNECTOR_LOCATION` - Default: `eastus`
- `IA_NAME` - Default: `prod-integration-account`
- `SERVICE_BUS_NAME` - Default: `cloudhealthoffice-sb`
- `STORAGE_SKU` - Default: `Standard_LRS`

## Deployment Process

### Step 1: Initial Setup (One-Time)

If you don't have Azure credentials configured, run these scripts manually:

```bash
# 1. Login to Azure
az login

# 2. Set subscription
az account set --subscription "YOUR_SUBSCRIPTION_ID"

# 3. Create app registration
./scripts/ensure-app-registration.sh "cloudhealthoffice-prod"

# Output will include:
# - Application (Client) ID
# - Tenant ID
# Save these for GitHub Secrets

# 4. Create service principal with RBAC roles
./scripts/ensure-service-principal.sh "APPLICATION_CLIENT_ID"

# This will:
# - Create service principal
# - Assign Contributor role on resource group
# - Assign Website Contributor on Static Web App (if exists)
# - Assign Key Vault Secrets User on Key Vault (if exists)
```

### Step 2: Configure GitHub Secrets

Add the following secrets in GitHub repository settings (Settings → Secrets
and variables → Actions):

1. Go to `https://github.com/aurelianware/cloudhealthoffice/settings/secrets/actions`
2. Add secrets:
   - `AZURE_CLIENT_ID`: Application ID from Step 1.3
   - `AZURE_TENANT_ID`: Tenant ID from Step 1.3
   - `AZURE_SUBSCRIPTION_ID`: Your Azure Subscription ID

### Step 3: Trigger Deployment

#### Option 1: Push to main branch

```bash
git push origin main
```

#### Option 2: Manual workflow dispatch

1. Go to Actions tab in GitHub
2. Select "Deploy Production - Cloud Health Office"
3. Click "Run workflow"
4. Select branch: `main`
5. Click "Run workflow"

### Step 4: Approve Deployment

1. Wait for pre-approval security checks to complete
2. Navigate to deployment run in Actions tab
3. Review security scan results
4. Click "Review deployments"
5. Select "PROD-approval"
6. Click "Approve and deploy"

### Step 5: Monitor Deployment

The deployment includes these stages:

1. **Setup Infrastructure** (5-10 minutes)
   - Ensures app registration exists
   - Ensures service principal exists with RBAC roles

2. **Pre-Approval Checks** (2-3 minutes)
   - Security scans
   - Validation checks

3. **Approval Gate**
   - Manual approval required (wait for reviewer)

4. **Deploy** (15-30 minutes)
   - Retrieve secrets (Key Vault → GitHub Secrets → defaults)
   - Deploy infrastructure via Bicep
   - Deploy Argo Workflow manifests to AKS
   - Deploy Static Web App
   - Run health checks

## Automated Infrastructure

### App Registration

**Created if missing**:

- Display Name: `cloudhealthoffice-prod`
- Sign-in Audience: Multi-tenant (`AzureADMultipleOrgs`)
- Redirect URIs:
  - `https://cloudhealthoffice.com/.auth/login/aad/callback`
  - `https://kind-wave-053ff9e1e.azurestaticapps.net/.auth/login/aad/callback`
  - `http://localhost:3000/.auth/login/aad/callback` (dev)
- API Permissions:
  - Microsoft Graph: User.Read, openid, profile, email
- Federated Credentials:
  - GitHub Actions (main branch): `repo:aurelianware/cloudhealthoffice:ref:refs/heads/main`
  - GitHub Actions (pull requests): `repo:aurelianware/cloudhealthoffice:pull_request`

### Service Principal

**Created if missing**:

- Based on app registration above
- RBAC Roles:
  - **Contributor** on resource group `rg-cloudhealthoffice-prod`
  - **Website Contributor** on Static Web App (if exists)
  - **Key Vault Secrets User** on deployment Key Vault (if exists)

### Deployment Key Vault

**Deployed by infrastructure**:

- Name: `cloudhealthoffice-deploy-kv`
- SKU: Premium (HIPAA-compliant with HSM-backed keys)
- Soft Delete: Enabled (90-day retention)
- Purge Protection: Enabled
- Public Network Access: Enabled (for GitHub Actions)
- RBAC Authorization: Enabled

**Deployment Secrets** (Runtime credentials):

- `sftp-host`: SFTP server hostname (e.g., `sftp.clearinghouse.com`)
- `sftp-username`: SFTP username for clearinghouse access
- `sftp-password`: SFTP password (securely stored)

**Configuration Values** (Infrastructure settings):

These are automatically managed by the provisioning scripts
(`ensure-app-registration.sh` and `ensure-service-principal.sh`):

- `app-registration-name`: Azure AD app registration display name
  (default: `cloudhealthoffice-prod`)
- `github-repository`: GitHub repository for OIDC federated credentials
  (default: `aurelianware/cloudhealthoffice`)
- `oidc-issuer`: OIDC token issuer for GitHub Actions
  (default: `https://token.actions.githubusercontent.com`)
- `redirect-uri-production`: Production domain redirect URI
  (default: `https://cloudhealthoffice.com/.auth/login/aad/callback`)
- `redirect-uri-azure`: Azure Static Web App redirect URI
  (default: `https://kind-wave-053ff9e1e.azurestaticapps.net/.auth/login/aad/callback`)
- `redirect-uri-local`: Local development redirect URI
  (default: `http://localhost:3000/.auth/login/aad/callback`)
- `resource-group-name`: Azure resource group name
  (default: `rg-cloudhealthoffice-prod`)
- `base-name`: Base name for all Azure resources
  (default: `cloudhealthoffice`)

**How it works**:

1. First run: Scripts detect Key Vault and populate with defaults
2. Subsequent runs: Scripts read values from Key Vault
3. Override: Update values in Key Vault to customize configuration
4. No code changes needed to adjust settings

## Secret Management Strategy

### Three-Tier Fallback

```text
Priority 1: Azure Key Vault (cloudhealthoffice-deploy-kv)
    ↓ (if not found or empty)
Priority 2: GitHub Secrets
    ↓ (if not configured)
Priority 3: Secure Defaults (placeholders for non-production)
```

### Migrating to Key Vault

#### Step 1: Deploy Key Vault

(automatic in infrastructure deployment)

#### Step 2: Populate secrets

```bash
# Set Key Vault name
KV_NAME="cloudhealthoffice-deploy-kv"

# Add SFTP secrets
az keyvault secret set --vault-name "$KV_NAME" --name sftp-host \
  --value "sftp.clearinghouse.com"
az keyvault secret set --vault-name "$KV_NAME" --name sftp-username \
  --value "your-username"
az keyvault secret set --vault-name "$KV_NAME" --name sftp-password \
  --value "your-secure-password"
```

#### Step 3: View current configuration values

```bash
# List all secrets (names only)
az keyvault secret list --vault-name "$KV_NAME" --query "[].name" -o table

# View specific secret value
az keyvault secret show --vault-name "$KV_NAME" --name sftp-host \
  --query "value" -o tsv

# View all configuration values (non-sensitive)
az keyvault secret show --vault-name "$KV_NAME" --name app-registration-name \
  --query "value" -o tsv
az keyvault secret show --vault-name "$KV_NAME" --name resource-group-name \
  --query "value" -o tsv
az keyvault secret show --vault-name "$KV_NAME" --name base-name \
  --query "value" -o tsv
```

#### Step 4: Update configuration values (if needed)

```bash
# Override redirect URI for production custom domain
az keyvault secret set --vault-name "$KV_NAME" \
  --name redirect-uri-production \
  --value "https://portal.yourcompany.com/.auth/login/aad/callback"

# Update resource group name
az keyvault secret set --vault-name "$KV_NAME" \
  --name resource-group-name \
  --value "rg-yourcompany-prod"

# Update Static Web App redirect URI
az keyvault secret set --vault-name "$KV_NAME" \
  --name redirect-uri-azure \
  --value "https://your-static-app.azurestaticapps.net/.auth/login/aad/callback"
```

**Step 5: Grant service principal access** (automatic in setup-infrastructure job)

**Step 6: Remove GitHub Secrets** (optional)
Once Key Vault is populated and tested, you can remove SFTP secrets from
GitHub Secrets.

## Static Web App Deployment

### Multi-Tenant Authentication

The Static Web App is configured for multi-tenant Azure AD authentication:

```json
{
  "auth": {
    "identityProviders": {
      "azureActiveDirectory": {
        "registration": {
          "openIdIssuer": "https://login.microsoftonline.com/common/v2.0",
          "clientIdSettingName": "AZURE_AD_CLIENT_ID",
          "clientSecretSettingName": "AZURE_AD_CLIENT_SECRET"
        },
        "login": {
          "loginParameters": ["domain_hint=organizations"]
        }
      }
    }
  }
}
```

**Benefits**:

- Single app registration for all customers
- Supports organizational accounts from any Azure AD tenant
- No separate B2C tenant needed
- Better for SaaS/multi-customer scenarios

### Configuration

After deployment, configure the Static Web App in Azure Portal:

1. Navigate to Static Web App: `cloudhealthoffice-swa`
2. Go to Configuration → Application settings
3. Add settings:
   - `AZURE_AD_CLIENT_ID`: Application ID from app registration
   - `AZURE_AD_CLIENT_SECRET`: Generate client secret in app registration

## Deployed Resources

### Resource Group: rg-cloudhealthoffice-prod

| Resource | Type | Purpose |
| -------- | ---- | ------- |
| `cloudhealthoffice-deploy-kv` | Key Vault | Deployment secrets storage |
| `stagingXXXXXXXX` | Storage Account | HIPAA attachments (ADLS Gen2) |
| `cloudhealthoffice-sb` | Service Bus | EDI transaction messaging |
| AKS cluster (`cho-aks`) | Azure Kubernetes Service | Argo Workflows EDI orchestration |
| `cloudhealthoffice-swa` | Static Web App | Portal frontend |
| `cloudhealthoffice-ai` | Application Insights | Monitoring and telemetry |
| C# EDI microservices (on AKS) | Kubernetes Deployments | X12 EDI parsing/generation |

## Troubleshooting

### Deployment Failures

#### Issue: App registration creation failed

```text
Error: Insufficient privileges to complete the operation
```

**Solution**: Ensure you have permissions to create app registrations in
Azure AD

```bash
# Check current user permissions
az ad signed-in-user show --query "userPrincipalName"

# Required role: Application Administrator or Global Administrator
```

---

#### Issue: Service principal creation failed

```text
Error: The service principal does not exist
```

**Solution**: Create service principal manually

```bash
az ad sp create --id "YOUR_APP_CLIENT_ID"
```

---

#### Issue: Key Vault secrets not found

```text
Warning: Key Vault 'cloudhealthoffice-deploy-kv' not found
```

**Solution**: This is expected on first deployment. The workflow will use
GitHub Secrets or defaults. After infrastructure deployment completes,
populate Key Vault with secrets.

---

#### Issue: Static Web App deployment token not found

```text
Error: Unable to retrieve deployment token
```

**Solution**: Add GitHub Secret
`AZURE_STATIC_WEB_APPS_API_TOKEN_KIND_WAVE_053FF9E1E`

```bash
# Get deployment token from Azure
az staticwebapp secrets list \
  --name "cloudhealthoffice-swa" \
  --resource-group "rg-cloudhealthoffice-prod" \
  --query "properties.apiKey" -o tsv
```

---

#### Issue: Static Web App returns HTTP 404 after deployment

**Solution**: This is normal for new deployments. The site typically needs
2-5 minutes to propagate. The deployment verification includes automatic
retries.

## Health Checks

### Post-Deployment Verification

The workflow automatically performs health checks:

1. **AKS Cluster Health**: Verifies cluster nodes are Ready
2. **Argo Workflow Templates**: Checks for workflow templates (ingest275, ingest278,
   replay278, rfai277) in `infrastructure/argo-workflows/`
3. **Application Insights**: Verifies telemetry connection
4. **Storage Account**: Confirms ADLS Gen2 storage exists
5. **Service Bus**: Validates namespace and topics exist

### Manual Verification

```bash
# Set variables
RG_NAME="rg-cloudhealthoffice-prod"
BASE_NAME="cloudhealthoffice"

# Check AKS cluster and Argo Workflows
kubectl get nodes
kubectl get workflows -n cloudhealthoffice

# Check Static Web App
az staticwebapp show -n "${BASE_NAME}-swa" -g "$RG_NAME" \
  --query "defaultHostname" -o tsv

# Check Key Vault
az keyvault show -n "${BASE_NAME}-deploy-kv" \
  --query "properties.vaultUri" -o tsv

# Check Service Bus
az servicebus namespace show -g "$RG_NAME" -n "${BASE_NAME}-sb" \
  --query "provisioningState" -o tsv
```

## Rollback

### Automated Rollback Options

#### Option 1: Re-run previous successful deployment

1. Go to Actions tab
2. Find last successful deployment run
3. Click "Re-run all jobs"

#### Option 2: Revert to last known good commit

```bash
# Find last good commit
git log --oneline

# Revert to specific commit
git revert <bad-commit-sha>
git push origin main
```

#### Option 3: Manual rollback via workflow_dispatch

1. Go to Actions → Deploy Production
2. Click "Run workflow"
3. Select a previous commit SHA or tag

### Manual Rollback

If automation fails, use Azure Portal:

1. Navigate to resource group `rg-cloudhealthoffice-prod`
2. Select "Deployments" in left menu
3. Find last successful deployment
4. Click "Redeploy"

## Key Vault Secrets Reference

### Complete Secrets List

| Secret Name | Type | Purpose | Default Value | Required |
| ----------- | ---- | ------- | ------------- | -------- |
| `sftp-host` | Runtime | SFTP server hostname | `sftp.example.com` | Yes* |
| `sftp-username` | Runtime | SFTP username | `logicapp` | Yes* |
| `sftp-password` | Runtime | SFTP password | `changeme...` | Yes* |
| `app-registration-name` | Config | Azure AD app name | `...prod` | Auto |
| `github-repository` | Config | GitHub repo | `aurelianware/...` | Auto |
| `oidc-issuer` | Config | OIDC issuer | `https://...` | Auto |
| `redirect-uri-production` | Config | Prod redirect | `https://...` | Auto |
| `redirect-uri-azure` | Config | Azure redirect | `https://...` | Auto |
| `redirect-uri-local` | Config | Local redirect | `http://...` | Auto |
| `resource-group-name` | Config | Resource group | `rg-...prod` | Auto |
| `base-name` | Config | Base name | `cloudhealthoffice` | Auto |

\* Required for production use. Pipeline will use defaults if not set.  
Auto = Automatically populated by provisioning scripts on first run.

### How to Populate Required Secrets

```bash
KV_NAME="cloudhealthoffice-deploy-kv"

# Production SFTP credentials (required)
az keyvault secret set --vault-name "$KV_NAME" --name sftp-host \
  --value "production.sftp.clearinghouse.com"
az keyvault secret set --vault-name "$KV_NAME" --name sftp-username \
  --value "prod-user"
az keyvault secret set --vault-name "$KV_NAME" --name sftp-password \
  --value "your-secure-password"
```

### Viewing All Secrets

```bash
# List all secret names
az keyvault secret list --vault-name "$KV_NAME" \
  --query "[].{Name:name, Enabled:attributes.enabled}" -o table

# View secret value (example)
az keyvault secret show --vault-name "$KV_NAME" --name sftp-host \
  --query "{Name:name, Value:value}" -o table
```

## Security Considerations

### HIPAA Compliance

- All storage encrypted at rest (TLS 1.2 minimum)
- Premium Key Vault with HSM-backed keys
- Soft delete and purge protection enabled
- Audit logging to Application Insights
- RBAC-based access control

### Secret Rotation

Key Vault facilitates easier secret rotation:

1. Update secret in Key Vault
2. No workflow changes required
3. Secrets automatically picked up on next deployment

### Least Privilege

Service principal only has required roles:

- **Contributor** (infrastructure deployment only)
- **Website Contributor** (Static Web App deployment only)
- **Key Vault Secrets User** (read secrets only, cannot manage)

## Support

### Documentation

- Main README: [README.md](../README.md)
- Deployment Secrets: [DEPLOYMENT-SECRETS-SETUP.md](../DEPLOYMENT-SECRETS-SETUP.md)
- Architecture: [ARCHITECTURE.md](../ARCHITECTURE.md)

### Getting Help

1. Review deployment logs in GitHub Actions
2. Check Application Insights for runtime errors
3. Review Azure Portal resource health
4. Check [TROUBLESHOOTING.md](../TROUBLESHOOTING.md)

### Escalation

- Infrastructure issues: DevOps team
- Argo Workflows / AKS: Application team
- Security concerns: Security team
- HIPAA compliance: Compliance officer
