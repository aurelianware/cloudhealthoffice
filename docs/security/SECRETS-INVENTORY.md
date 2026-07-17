# GitHub Secrets Inventory & Categorization

**Last Updated:** 2026-04-11  
**Purpose:** Clear categorization of all GitHub Secrets for Cloud Health Office deployment

---

## Overview

After multiple deployment attempts, GitHub Secrets have accumulated across different environments and use cases. This document provides a comprehensive inventory and clarifies which secrets are used for:

1. **GitHub Actions → Azure Deployment** (OIDC federated credentials)
2. **Runtime Configuration** (SFTP, API keys, connection strings)
3. **User Authentication** (Azure AD multi-tenant for portal login)

---

## ✅ GitHub Secrets to Keep (Deployment Authentication Only)

These secrets enable **GitHub Actions to authenticate to Azure** via OIDC (OpenID Connect) federated credentials. They should **remain in GitHub Secrets** as they are required before any Azure resource access.

### Production (PROD)
```
AZURE_CLIENT_ID          - Service Principal Application (Client) ID for GitHub Actions
AZURE_TENANT_ID          - Azure AD Tenant ID
AZURE_SUBSCRIPTION_ID    - Azure Subscription ID for PROD deployments
```

### UAT Environment
```
AZURE_CLIENT_ID_UAT          - Service Principal Application (Client) ID for GitHub Actions (UAT)
AZURE_TENANT_ID_UAT          - Azure AD Tenant ID (may be same as PROD)
AZURE_SUBSCRIPTION_ID_UAT    - Azure Subscription ID for UAT deployments
```

### DEV Environment
```
AZURE_CLIENT_ID          - Reuses PROD credentials (or separate if needed)
AZURE_TENANT_ID          - Reuses PROD credentials
AZURE_SUBSCRIPTION_ID    - Reuses PROD credentials
```

**Purpose:** These enable the workflow to execute `azure/login@v2` action with OIDC authentication.

**Why Keep in GitHub Secrets:**
- Required **before** any Azure resources can be accessed
- Cannot retrieve from Key Vault because Azure login must happen first
- Low security risk (they only grant deployment permissions when combined with federated credential subject claim)
- No PHI or sensitive data

**RBAC Roles Assigned:**
- Contributor (for infrastructure deployment)
- Key Vault Secrets User (for retrieving runtime secrets from Key Vault)
- Storage Blob Data Contributor (for AKS/Argo Workflows deployment)

---

## 🔄 Secrets to Migrate to Azure Key Vault

These secrets are **runtime configuration** values that should be stored in Azure Key Vault for enhanced security, audit logging, and easier rotation.

### SFTP Credentials (Clearinghouse Integration)
```
SFTP_HOST         → Migrate to Key Vault as: sftp-host
SFTP_USERNAME     → Migrate to Key Vault as: sftp-username
SFTP_PASSWORD     → Migrate to Key Vault as: sftp-password
```

**Current Usage:** PROD deployment workflow (`deploy.yml`) for configuring AKS/Argo Workflows SFTP integration via Kubernetes secrets

**Security Benefit:**
- ✅ Audit logging (who accessed, when)
- ✅ Secret rotation without updating GitHub Secrets
- ✅ HIPAA compliance (encryption at rest with Premium SKU)
- ✅ Managed identity access (no hardcoded credentials)

**Migration Path:**
1. Add secrets to Key Vault via `setup-deployment-keyvault.sh` script
2. Update `deploy.yml` to retrieve from Key Vault after Azure login
3. Verify deployment works with Key Vault retrieval
4. Remove from GitHub Secrets (keep backup for 30 days)

### Integration Account Credentials
```
# Future secrets to migrate (if/when added)
INTEGRATION_ACCOUNT_KEY    → Migrate to Key Vault as: integration-account-key
X12_SENDER_QUALIFIER       → Migrate to Key Vault as: x12-sender-qualifier
X12_RECEIVER_QUALIFIER     → Migrate to Key Vault as: x12-receiver-qualifier
```

**Note:** Currently configured via Bicep templates. If dynamic credentials are needed, migrate to Key Vault.

### Claims Backend API Credentials
```
# Future secrets to migrate (if/when added)
CLAIMS_API_KEY             → Migrate to Key Vault as: claims-api-key
CLAIMS_API_ENDPOINT        → Can remain as GitHub Variable (non-sensitive)
```

---

## 🌐 Secrets for Static Web App (Azure Portal Configuration)

These secrets are for **user authentication** (multi-tenant Azure AD) and should be configured in the **Azure Static Web Apps** resource, **NOT in GitHub Secrets**.

### Azure AD Multi-Tenant App (User Login)
```
AZURE_AD_CLIENT_ID         - Multi-tenant Azure AD app for user login
AZURE_AD_CLIENT_SECRET     - Client secret for Azure AD app
AZURE_AD_TENANT_ID         - Tenant ID (or "common" for multi-tenant)
```

**Configure In:** Azure Portal → Static Web Apps → Configuration → Application Settings

**Why Not in GitHub Secrets:**
- These are for **user authentication**, not deployment
- Static Web App runtime needs these values
- Managed through Azure Portal for easier rotation
- Different lifecycle than deployment secrets

**Reference Documentation:** See `docs/FEDERATED-CREDENTIALS-SETUP.md` section "Multi-Tenant Azure AD for User Login"

---

## 🔧 Third-Party Integration Secrets

### CodeCov (Code Coverage Reporting)
```
CODECOV_TOKEN              - Keep in GitHub Secrets (workflow-specific)
```

**Purpose:** Upload code coverage reports to CodeCov.io  
**Keep in GitHub Secrets:** This is a workflow integration token, not Azure-related

### Snyk (Security Scanning)
```
SNYK_TOKEN                 - Keep in GitHub Secrets (workflow-specific)
```

**Purpose:** Security vulnerability scanning  
**Keep in GitHub Secrets:** This is a workflow integration token, not Azure-related

### Azure Static Web Apps Deployment Token
```
AZURE_STATIC_WEB_APPS_API_TOKEN_KIND_WAVE_053FF9E1E  - Keep in GitHub Secrets
```

**Purpose:** Deployment token for Azure Static Web Apps  
**Keep in GitHub Secrets:** Generated by Azure, used by deployment workflow

### GitHub Token
```
GITHUB_TOKEN               - Automatically provided by GitHub Actions (no action needed)
```

**Purpose:** GitHub API access for workflows  
**Auto-provided:** GitHub Actions automatically provides this token

---

## 🤖 AI / LLM Provider Secrets

These secrets are **runtime configuration** consumed by AI-enabled services and should live exclusively in Azure Key Vault. They never belong in GitHub Secrets because they are production API keys with PHI-adjacent access and must rotate on a compliance-oriented cadence.

### Anthropic (Claude) — Claims Examiner Service
```
Anthropic--ApiKey    → Key Vault secret name (maps to config key Anthropic:ApiKey)
```

**Consumed by:** `src/services/claims-examiner-service` — advisory AI examiner for pended NCCI claims. Binds via `builder.Configuration.GetSection("Anthropic").Get<AnthropicOptions>()` after `AddAzureKeyVaultConfiguration` runs, so the `--` → `:` prefix mapping in `AzureKeyVaultConfigurationExtensions.cs` lands it at `Anthropic:ApiKey` automatically.

**Scope in v1:** pend-resolution only for NCCI NE001 pair edits where a `-59`/`X{EPSU}` modifier is a legal override path. Nothing auto-applies — every recommendation routes to the human work queue.

**Never in GitHub Secrets:**
- Production API key with per-token rate limits and billing attribution
- Rotation required independently of any deployment credential
- PHI-adjacent: prompts include procedure codes, modifiers, and provider RFAI history (no member identifiers in v1, but sensitivity tier justifies Key Vault regardless)

**Populate via:**
```bash
./scripts/populate-keyvault-secrets.sh \
  --vault-name cho-app-kv \
  --file scripts/secrets-manifest.example.env
```
(manifest already includes the `Anthropic--ApiKey` placeholder)

**Rotation cadence:** 90 days, aligned with the Key Vault expiry default in `populate-keyvault-secrets.sh`.

---

## 📋 Summary Table

| Secret Name | Current Location | Recommended Location | Reason |
|------------|------------------|---------------------|---------|
| `AZURE_CLIENT_ID` | GitHub Secrets | ✅ Keep in GitHub | OIDC authentication (required before Azure access) |
| `AZURE_TENANT_ID` | GitHub Secrets | ✅ Keep in GitHub | OIDC authentication |
| `AZURE_SUBSCRIPTION_ID` | GitHub Secrets | ✅ Keep in GitHub | OIDC authentication |
| `AZURE_CLIENT_ID_UAT` | GitHub Secrets | ✅ Keep in GitHub | OIDC authentication (UAT) |
| `AZURE_TENANT_ID_UAT` | GitHub Secrets | ✅ Keep in GitHub | OIDC authentication (UAT) |
| `AZURE_SUBSCRIPTION_ID_UAT` | GitHub Secrets | ✅ Keep in GitHub | OIDC authentication (UAT) |
| `SFTP_HOST` | GitHub Secrets | 🔄 **Migrate to Key Vault** | Runtime config, HIPAA compliance |
| `SFTP_USERNAME` | GitHub Secrets | 🔄 **Migrate to Key Vault** | Runtime config, HIPAA compliance |
| `SFTP_PASSWORD` | GitHub Secrets | 🔄 **Migrate to Key Vault** | Sensitive credential, rotation |
| `AZURE_AD_CLIENT_ID` (user login) | N/A | 🌐 Azure Static Web Apps Config | User auth, not deployment |
| `AZURE_AD_CLIENT_SECRET` (user login) | N/A | 🌐 Azure Static Web Apps Config | User auth, not deployment |
| `CODECOV_TOKEN` | GitHub Secrets | ✅ Keep in GitHub | Third-party integration |
| `SNYK_TOKEN` | GitHub Secrets | ✅ Keep in GitHub | Third-party integration |
| `AZURE_STATIC_WEB_APPS_API_TOKEN_*` | GitHub Secrets | ✅ Keep in GitHub | Deployment token |
| `GITHUB_TOKEN` | Auto-provided | ✅ Auto-provided | GitHub Actions built-in |
| `Anthropic--ApiKey` | Key Vault | 🤖 **Key Vault only** | AI Claims Examiner, PHI-adjacent, 90-day rotation |

---

## 🎯 Post-Migration: Minimal GitHub Secrets

After completing the migration to Azure Key Vault, GitHub Secrets should contain **only**:

### Deployment Authentication (OIDC)
```
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
AZURE_CLIENT_ID_UAT
AZURE_TENANT_ID_UAT
AZURE_SUBSCRIPTION_ID_UAT
```

### Third-Party Integrations
```
CODECOV_TOKEN
SNYK_TOKEN
AZURE_STATIC_WEB_APPS_API_TOKEN_KIND_WAVE_053FF9E1E
```

**Total:** 9 secrets (down from potentially 15+ with runtime configs mixed in)

---

## 🔐 Key Vault Naming Convention

Deployment secrets are stored in environment-specific Key Vaults:

```
DEV:  cloud-health-office-dev-deploy-kv
UAT:  cloud-health-office-uat-deploy-kv
PROD: cloud-health-office-prod-deploy-kv
```

**Secret Naming Pattern:**
- Use lowercase with hyphens: `sftp-host`, `sftp-username`, `sftp-password`
- Prefix with service if needed: `claims-api-key`, `clearinghouse-sftp-host`
- Avoid underscores (align with Azure naming conventions)

**RBAC Access:**
- Service Principal (GitHub Actions): `Key Vault Secrets User` role
- DevOps team: `Key Vault Administrator` role
- AKS Workload Identity: `Key Vault Secrets User` role (for runtime access via pod-level managed identity)

---

## 🔍 Verification Checklist

- [ ] All OIDC secrets are configured in GitHub Secrets
- [ ] SFTP credentials migrated to Key Vault
- [ ] Service Principal has "Key Vault Secrets User" role on deployment Key Vault
- [ ] Workflow successfully retrieves secrets from Key Vault
- [ ] Old GitHub Secrets removed after successful migration (with 30-day backup period)
- [ ] Static Web App configuration updated with user auth credentials (if applicable)
- [ ] Documentation updated to reflect new secret management approach

---

## 📚 Related Documentation

- [Secrets Migration Guide](./SECRETS-MIGRATION-GUIDE.md) - Step-by-step migration instructions
- [Federated Credentials Setup](./FEDERATED-CREDENTIALS-SETUP.md) - OIDC configuration
- [Deployment Secrets Setup](../DEPLOYMENT-SECRETS-SETUP.md) - Secret configuration guide
- [Key Vault Module](../infra/modules/deployment-keyvault.bicep) - Infrastructure as Code

---

## 🔒 Security Best Practices

1. **Least Privilege:** Grant only necessary permissions to Service Principals
2. **Secret Rotation:** Rotate SFTP credentials every 90 days (HIPAA requirement)
3. **Audit Logging:** Enable Key Vault diagnostic logging (already configured in `keyvault.bicep`)
4. **Access Reviews:** Quarterly review of who has access to Key Vault
5. **Separation of Duties:** Deployment secrets in Key Vault, user auth in Static Web Apps config
6. **No Logging:** Use `echo "::add-mask::"` to mask secrets in workflow logs
7. **Backup:** Keep old GitHub Secrets for 30 days after migration for rollback

---

**Maintained by:** Cloud Health Office DevOps Team  
**Review Frequency:** Quarterly or after major deployment changes
