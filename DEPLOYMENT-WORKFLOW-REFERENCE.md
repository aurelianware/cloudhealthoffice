# Deployment Workflow Quick Reference

This document provides a quick reference for the complete deployment workflow, mapping GitHub Actions steps to documentation sections.

## 📋 Overview

The HIPAA Attachments deployment uses a comprehensive automated workflow that:
1. Validates infrastructure templates and workflow definitions
2. Compiles Bicep to ARM templates
3. Performs What-If analysis for safety
4. Deploys Azure infrastructure
5. Deploys Logic App workflows
6. Performs health checks
7. Provides rollback on failure

## 🔄 Deployment Flow

```
┌─────────────────────────────────────────────────────────────┐
│                    Pre-Deployment Phase                      │
├─────────────────────────────────────────────────────────────┤
│ 1. GitHub Secrets/Variables Setup (One-time)                │
│    → See: GITHUB-ACTIONS-SETUP.md                           │
│                                                               │
│ 2. Workflow JSON Validation                                  │
│    → Validates: definition, kind, parameters keys            │
│    → See: DEPLOYMENT.md § Pre-Deployment Validation          │
│                                                               │
│ 3. Bicep Template Validation                                 │
│    → Compiles: infra/main.bicep → ARM template               │
│    → See: DEPLOYMENT.md § Bicep Compilation                  │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                  Infrastructure Deployment                   │
├─────────────────────────────────────────────────────────────┤
│ 4. Azure Authentication (OIDC)                               │
│    → Uses: AZURE_CLIENT_ID, TENANT_ID, SUBSCRIPTION_ID      │
│    → See: GITHUB-ACTIONS-SETUP.md § OIDC Setup              │
│                                                               │
│ 5. ARM What-If Analysis                                      │
│    → Preview: Resource creation/modification/deletion        │
│    → See: DEPLOYMENT.md § ARM What-If Analysis               │
│                                                               │
│ 6. Resource Group Creation                                   │
│    → Creates/ensures: Resource group exists                  │
│                                                               │
│ 7. Infrastructure Deployment                                 │
│    → Deploys: Storage, Service Bus, Logic App, App Insights  │
│    → Time: ~5-10 minutes                                     │
│    → See: DEPLOYMENT.md § ARM Template Deployment            │
│                                                               │
│ 8. Integration Account Setup                                 │
│    → Configures: X12 schemas, trading partners, agreements   │
│    → See: DEPLOYMENT.md § Post-Deployment Configuration      │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                   Workflow Deployment                        │
├─────────────────────────────────────────────────────────────┤
│ 9. Package Logic App Workflows                               │
│    → Creates: workflows.zip (6 workflows)                    │
│    → Structure: workflows/{workflow-name}/workflow.json      │
│    → See: DEPLOYMENT.md § Logic App Workflow Deployment      │
│                                                               │
│ 10. Deploy Workflow Package                                  │
│     → Uploads: workflows.zip to Logic App                    │
│     → Time: ~1-2 minutes                                     │
│                                                               │
│ 11. Restart Logic App                                        │
│     → Ensures: New workflows are loaded                      │
│     → Time: ~30 seconds                                      │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                   Verification Phase                         │
├─────────────────────────────────────────────────────────────┤
│ 12. Post-Deployment Health Checks                            │
│     → Verifies: Logic App running                            │
│     → Checks: Workflows enabled                              │
│     → Validates: Infrastructure resources                    │
│     → See: DEPLOYMENT.md § Verification and Testing          │
│                                                               │
│ 13. Success Summary                                          │
│     → Reports: Deployed resources                            │
│     → Lists: Next configuration steps                        │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│               Failure Handling (if needed)                   │
├─────────────────────────────────────────────────────────────┤
│ 14. Rollback on Failure                                      │
│     → Collects: Diagnostic information                       │
│     → Provides: Rollback guidance                            │
│     → See: DEPLOYMENT.md § Rollback Procedures               │
└─────────────────────────────────────────────────────────────┘
```

## 📖 Documentation Map

| Task | Primary Document | Section |
|------|------------------|---------|
| **Initial Setup** | GITHUB-ACTIONS-SETUP.md | All sections |
| **Pre-requisites** | DEPLOYMENT.md | Prerequisites |
| **Validate Templates** | DEPLOYMENT.md | Pre-Deployment Validation |
| **Bicep Compilation** | DEPLOYMENT.md | Bicep Compilation and ARM Deployment |
| **ARM What-If** | DEPLOYMENT.md | ARM What-If Analysis |
| **Deploy Infrastructure** | DEPLOYMENT.md | ARM Template Deployment |
| **Deploy Workflows** | DEPLOYMENT.md | Logic App Workflow Deployment |
| **Post-Configuration** | DEPLOYMENT.md | Post-Deployment Configuration |
| **Verify Deployment** | DEPLOYMENT.md | Verification and Testing |
| **Rollback** | DEPLOYMENT.md | Rollback Procedures |
| **Troubleshoot** | DEPLOYMENT.md, TROUBLESHOOTING.md | Troubleshooting sections |

## 🔑 Key Workflow Files

### Production Deployment
- **File**: `.github/workflows/deploy.yml`
- **Trigger**: Manual (workflow_dispatch) or push to main/release branches
- **Environment**: PROD
- **Approval**: Required (configured in GitHub Environment settings)

### UAT Deployment
- **File**: `.github/workflows/deploy-uat.yml`
- **Trigger**: Automatic on push to `release/*` branches
- **Environment**: UAT
- **Jobs**: validate → deploy-infrastructure → deploy-logic-apps → healthcheck

### DEV Deployment
- **File**: `.github/workflows/deploy-dev.yml`
- **Trigger**: Manual or push to `main/*` branches
- **Environment**: DEV
- **Jobs**: validate → deploy-infrastructure → deploy-logic-apps → healthcheck

## ⚙️ Required GitHub Secrets

### Per Environment (DEV/UAT/PROD)

```yaml
secrets:
  AZURE_CLIENT_ID_{ENV}:        # Azure AD Application ID
  AZURE_TENANT_ID_{ENV}:        # Azure AD Tenant ID
  AZURE_SUBSCRIPTION_ID_{ENV}:  # Azure Subscription ID
  SFTP_HOST:                    # SFTP server hostname
  SFTP_USERNAME:                # SFTP username
  SFTP_PASSWORD:                # SFTP password (secure)
```

**Setup Guide**: [GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md#github-secrets-configuration)

## 📊 Required GitHub Variables

### Repository Variables

```yaml
variables:
  AZURE_RG_NAME:              # Resource group name
  AZURE_LOCATION:             # Azure region (e.g., eastus)
  AZURE_CONNECTOR_LOCATION:   # API connector region
  BASE_NAME:                  # Resource name prefix
  IA_NAME:                    # Integration Account name
  SERVICE_BUS_NAME:           # Service Bus namespace name
  STORAGE_SKU:                # Storage account SKU
```

**Setup Guide**: [GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md#github-variables-configuration)

## 🚀 Quick Start Commands

### Setup (One-Time)

```bash
# 1. Create Azure AD application and configure OIDC
# See: GITHUB-ACTIONS-SETUP.md § Azure OIDC Authentication Setup

# 2. Configure GitHub Secrets
gh secret set AZURE_CLIENT_ID_PROD --body "<client-id>"
gh secret set AZURE_TENANT_ID_PROD --body "<tenant-id>"
gh secret set AZURE_SUBSCRIPTION_ID_PROD --body "<subscription-id>"

# 3. Configure GitHub Variables
gh variable set AZURE_RG_NAME --body "pchp-attachments-prod-rg"
gh variable set BASE_NAME --body "hipaa-attachments-prod"
```

### Validate Before Deployment

```bash
# Validate Bicep templates
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# Validate workflow JSON files
find logicapps/workflows -name "workflow.json" -exec jq . {} \;

# Run repository structure check
pwsh -c "./fix_repo_structure.ps1 -RepoRoot ."
```

### Deploy to DEV

```bash
# Via GitHub Actions (recommended)
# 1. Go to: Actions → Deploy DEV - HIPAA Attachments
# 2. Click: Run workflow
# 3. Select: main branch
# 4. Click: Run workflow

# Via Azure CLI (manual)
az group create -n rg-hipaa-attachments-dev -l eastus
az deployment group create \
  -g rg-hipaa-attachments-dev \
  -f infra/main.bicep \
  -p baseName=hipaa-attachments-dev
```

### Deploy to UAT

```bash
# Automatic trigger on release branch
git checkout -b release/v1.0.0
git push origin release/v1.0.0
# → Automatically triggers deploy-uat.yml workflow
```

### Deploy to PROD

```bash
# Via GitHub Actions with approval
# 1. Go to: Actions → Deploy (PROD)
# 2. Click: Run workflow
# 3. Select: main branch
# 4. Click: Run workflow
# 5. Wait for approval from designated reviewers
# 6. Monitor deployment progress
```

## 🔍 Monitoring Deployment

### GitHub Actions

```bash
# Monitor workflow run
# 1. Go to: Actions tab
# 2. Click on running workflow
# 3. Expand each job to see detailed logs
# 4. Check for errors or warnings
```

### Azure Portal

```bash
# View deployment progress
# 1. Navigate to: Resource Group → Deployments
# 2. Click on latest deployment
# 3. Review "Template" and "Operations" tabs
# 4. Check for failed operations
```

### Azure CLI

```bash
# List active deployments
az deployment group list \
  --resource-group <resource-group> \
  --query "[].{Name:name, State:properties.provisioningState}" \
  --output table

# Show deployment details
az deployment group show \
  --resource-group <resource-group> \
  --name <deployment-name>
```

## ⏱️ Deployment Timeline

| Stage | Duration | Cumulative |
|-------|----------|------------|
| **Validation** | 30 sec | 30 sec |
| **OIDC Auth** | 10 sec | 40 sec |
| **What-If Analysis** | 20 sec | 1 min |
| **Infrastructure Deploy** | 5-10 min | 6-11 min |
| **Integration Account Setup** | 1-2 min | 7-13 min |
| **Workflow Package** | 10 sec | 7-13 min |
| **Workflow Deploy** | 1-2 min | 8-15 min |
| **Health Checks** | 30 sec | 8-16 min |
| **Total** | **8-16 minutes** | |

## ❌ Common Issues and Solutions

### Issue: OIDC Authentication Failed

**Error**: `AADSTS700016: Application not found`

**Solution**:
1. Check federated credential subject matches repository
2. Verify `AZURE_CLIENT_ID_*` secret is correct
3. See: [GITHUB-ACTIONS-SETUP.md § Troubleshooting](GITHUB-ACTIONS-SETUP.md#troubleshooting)

### Issue: Bicep Compilation Failed

**Error**: `Template deployment failed`

**Solution**:
1. Run local validation: `az bicep build --file infra/main.bicep`
2. Check parameter types and values
3. Review error messages for missing properties
4. See: [DEPLOYMENT.md § Troubleshooting](DEPLOYMENT.md#troubleshooting)

### Issue: Workflow Deployment Failed

**Error**: `Could not deploy workflows.zip`

**Solution**:
1. Verify ZIP structure: `unzip -l workflows.zip`
2. Check Logic App is running
3. Restart Logic App and retry
4. See: [DEPLOYMENT.md § Logic App Workflow Deployment](DEPLOYMENT.md#logic-app-workflow-deployment)

### Issue: Workflows Not Triggering

**Error**: No runs showing in Logic App

**Solution**:
1. Check API connections are authenticated
2. Verify SFTP credentials and path
3. Enable workflow manually in portal
4. Check Application Insights for errors
5. See: [DEPLOYMENT.md § Troubleshooting](DEPLOYMENT.md#troubleshooting)

## 🔄 Rollback Quick Reference

| Scenario | Command | Time | Risk |
|----------|---------|------|------|
| **Workflow Issue** | Redeploy previous workflows.zip | 2-3 min | Low |
| **Single Workflow** | Disable in portal | <1 min | None |
| **Infrastructure** | Redeploy previous Bicep | 5-10 min | Low |
| **Complete Failure** | Full ARM rollback | 10-15 min | Medium |

**Detailed Procedures**: [DEPLOYMENT.md § Rollback Procedures](DEPLOYMENT.md#rollback-procedures)

## 📚 Additional Resources

### Core Documentation
- **[GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md)** - Complete GitHub Actions configuration
- **[DEPLOYMENT.md](DEPLOYMENT.md)** - Comprehensive deployment guide
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - System architecture and design
- **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** - Detailed troubleshooting guide
- **[SECURITY.md](SECURITY.md)** - HIPAA compliance and security

### Workflow Files
- `.github/workflows/deploy.yml` - Production deployment
- `.github/workflows/deploy-uat.yml` - UAT deployment
- `.github/workflows/deploy-dev.yml` - DEV deployment
- `.github/workflows/pr-lint.yml` - PR validation

### Infrastructure
- `infra/main.bicep` - Main infrastructure template
- `logicapps/workflows/*/workflow.json` - Logic App workflow definitions

### Scripts
- `fix_repo_structure.ps1` - Repository structure normalization
- `test-workflows.ps1` - Workflow testing framework
- `configure-hipaa-trading-partners.ps1` - Trading partner setup
- `configure-x12-agreements.ps1` - X12 agreement configuration

## 🆘 Getting Help

### Documentation
1. Check relevant documentation file first
2. Review troubleshooting sections
3. Search for error messages in documentation

### GitHub
1. Check GitHub Actions logs
2. Review recent commits for changes
3. Check open/closed issues

### Azure
1. Review Azure Activity Log
2. Check deployment operation details
3. Review Application Insights for errors
4. Create Azure support ticket if needed

### Team
1. Post in team channel
2. Tag relevant team members
3. Include error messages and logs
4. Provide steps to reproduce

---

**Last Updated**: 2024-11-16  
**Version**: 1.0  
**Maintainer**: Platform Team
