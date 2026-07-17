# Secrets Migration Guide: GitHub Secrets → Azure Key Vault

**Last Updated:** 2026-02-02  
**Objective:** Migrate runtime deployment secrets from GitHub Secrets to Azure Key Vault for enhanced security and HIPAA compliance

---

## 📋 Overview

This guide walks through migrating deployment secrets (SFTP credentials, API keys) from GitHub Secrets to Azure Key Vault while maintaining operational continuity.

### Benefits of Migration

✅ **Better Security:** Key Vault provides HSM-backed encryption, audit logging, and RBAC  
✅ **HIPAA Compliance:** Premium Key Vault meets HIPAA requirements for PHI-adjacent credentials  
✅ **Easier Rotation:** Update secrets in one place without updating GitHub Secrets  
✅ **Environment Isolation:** DEV/UAT/PROD secrets in separate Key Vaults  
✅ **Audit Trail:** Track who accessed which secrets and when  
✅ **Clear Separation:** GitHub Secrets only for GitHub → Azure authentication  
✅ **Managed Identity:** AKS workloads can access secrets without storing credentials (via Workload Identity)

---

## 🎯 What to Migrate vs. What to Keep

### ✅ Keep in GitHub Secrets
- `AZURE_CLIENT_ID` - Service Principal for OIDC authentication
- `AZURE_TENANT_ID` - Azure AD Tenant ID
- `AZURE_SUBSCRIPTION_ID` - Azure Subscription ID
- Third-party tokens: `CODECOV_TOKEN`, `SNYK_TOKEN`, etc.

**Reason:** Required for initial Azure authentication before Key Vault access is possible

### 🔄 Migrate to Key Vault
- `SFTP_HOST` → `sftp-host`
- `SFTP_USERNAME` → `sftp-username`
- `SFTP_PASSWORD` → `sftp-password`
- Future: API keys, connection strings, integration credentials

**Reason:** Runtime configuration with sensitive data, benefit from Key Vault security features

---

## 🚀 Step-by-Step Migration

### Phase 1: Verify Prerequisites

#### 1.1 Verify OIDC Authentication is Working

```bash
# Ensure GitHub Actions can authenticate to Azure via OIDC
# Check recent workflow runs - they should show successful Azure login
```

**Files to check:**
- `.github/workflows/deploy.yml` - Should have `azure/login@v2` with OIDC
- Federated credential configured in Azure AD App Registration

**Reference:** See `docs/FEDERATED-CREDENTIALS-SETUP.md`

#### 1.2 Verify Service Principal Permissions

Your Service Principal needs these permissions:

```bash
# Check current role assignments
az role assignment list \
  --assignee <AZURE_CLIENT_ID> \
  --scope /subscriptions/<AZURE_SUBSCRIPTION_ID> \
  --output table
```

**Required Roles:**
- `Contributor` on Resource Group (for infrastructure deployment)
- `Storage Blob Data Contributor` on Storage Account (for AKS workloads)
- `Key Vault Secrets User` on Key Vault (to retrieve secrets) ← **Add this if missing**

#### 1.3 Add Key Vault Secrets User Role

```bash
# Get your Service Principal Object ID
SP_OBJECT_ID=$(az ad sp show --id <AZURE_CLIENT_ID> --query id -o tsv)

# Grant Key Vault access (replace <KEY_VAULT_NAME> with actual name)
az role assignment create \
  --assignee $SP_OBJECT_ID \
  --role "Key Vault Secrets User" \
  --scope /subscriptions/<AZURE_SUBSCRIPTION_ID>/resourceGroups/<RESOURCE_GROUP>/providers/Microsoft.KeyVault/vaults/<KEY_VAULT_NAME>
```

**Expected output:** Role assignment confirmation

---

### Phase 2: Deploy Key Vault for Deployment Secrets

#### 2.1 Review Key Vault Bicep Module

The repository already has a HIPAA-oriented Key Vault module at `infra/modules/deployment-keyvault.bicep`.

**Check current configuration:**
```bash
cat infra/modules/deployment-keyvault.bicep
```

**Key features:**
- ✅ Premium SKU (HSM-backed keys)
- ✅ RBAC authorization enabled
- ✅ Soft delete (90-day retention)
- ✅ Purge protection enabled
- ✅ Audit logging configured
- ✅ Network ACLs (deny by default)

#### 2.2 Deploy Deployment Key Vault (if not already deployed)

**Option A: Update existing `infra/main.bicep`**

If your main.bicep doesn't already deploy a Key Vault for deployment secrets:

```bicep
// Add to infra/main.bicep
module deploymentKeyVault 'modules/keyvault.bicep' = {
  name: 'deployment-keyvault'
  params: {
    keyVaultName: '${baseName}-deploy-kv'
    location: location
    skuName: 'premium'
    enableRbacAuthorization: true
    publicNetworkAccess: 'Enabled'  // For GitHub Actions access
    networkAclsDefaultAction: 'Allow'  // Adjust based on security requirements
    // NOTE: This assumes infra/main.bicep defines a `logAnalyticsWorkspace` module
    // with a `workspaceId` output. If you do not yet deploy Log Analytics, either:
    //   - Add a Log Analytics workspace module and expose `workspaceId` as an output, or
    //   - Remove/comment out the `logAnalyticsWorkspaceId` parameter below
    logAnalyticsWorkspaceId: logAnalyticsWorkspace.outputs.workspaceId
    tags: {
      Environment: environment
      Purpose: 'DeploymentSecrets'
      Compliance: 'HIPAA'
    }
  }
}

output deploymentKeyVaultName string = deploymentKeyVault.outputs.keyVaultName
output deploymentKeyVaultUri string = deploymentKeyVault.outputs.keyVaultUri
```

**Option B: Use standalone Bicep module**

Deploy Key Vault separately if you prefer:

```bash
# Deploy Key Vault for deployment secrets
az deployment group create \
  --resource-group cloud-health-office-prod-rg \
  --template-file infra/modules/deployment-keyvault.bicep \
  --parameters keyVaultName=cloud-health-office-prod-deploy-kv \
               location=westus \
               skuName=premium \
               enableRbacAuthorization=true \
               publicNetworkAccess=Enabled \
               networkAclsDefaultAction=Allow
```

**Verify deployment:**
```bash
az keyvault show --name cloud-health-office-prod-deploy-kv
```

---

### Phase 3: Populate Key Vault with Secrets

#### 3.1 Create Setup Script

Use the provided script to populate Key Vault:

```bash
# Script: scripts/setup-deployment-keyvault.sh
chmod +x scripts/setup-deployment-keyvault.sh

# Run the script (interactive mode - will prompt for secrets)
./scripts/setup-deployment-keyvault.sh \
  --vault-name cloud-health-office-prod-deploy-kv \
  --environment PROD
```

**The script will:**
1. Validate Azure CLI authentication
2. Check Key Vault exists and is accessible
3. Prompt for SFTP credentials (securely, without echoing)
4. Create secrets in Key Vault with proper naming
5. Verify secrets were created successfully

#### 3.2 Manual Secret Creation (Alternative)

If you prefer manual creation:

```bash
# Set Key Vault name
KV_NAME="cloud-health-office-prod-deploy-kv"

# Add SFTP Host
az keyvault secret set \
  --vault-name $KV_NAME \
  --name sftp-host \
  --value "sftp.clearinghouse.example.com"

# Add SFTP Username
az keyvault secret set \
  --vault-name $KV_NAME \
  --name sftp-username \
  --value "payer-health-plan-001"

# Add SFTP Password (use secure input)
read -s -p "Enter SFTP Password: " SFTP_PASS
az keyvault secret set \
  --vault-name $KV_NAME \
  --name sftp-password \
  --value "$SFTP_PASS"
unset SFTP_PASS
```

#### 3.3 Verify Secrets in Key Vault

```bash
# List all secrets
az keyvault secret list --vault-name $KV_NAME --query "[].name" -o table

# Verify secret exists (without showing value)
az keyvault secret show --vault-name $KV_NAME --name sftp-host --query "name"

# Test retrieval (for validation only - don't log this)
az keyvault secret show --vault-name $KV_NAME --name sftp-host --query "value" -o tsv
```

**Expected secrets:**
- `sftp-host`
- `sftp-username`
- `sftp-password`

---

### Phase 4: Update GitHub Workflows

#### 4.1 Update PROD Deployment Workflow

**File:** `.github/workflows/deploy.yml`

**Before (using GitHub Secrets directly):**
```yaml
- name: Deploy Infrastructure
  uses: azure/arm-deploy@v2
  with:
    parameters: sftpHost="${{ secrets.SFTP_HOST }}" sftpUsername="${{ secrets.SFTP_USERNAME }}" sftpPassword="${{ secrets.SFTP_PASSWORD }}"
```

**After (retrieving from Key Vault):**
```yaml
- name: Azure Login (OIDC)
  uses: azure/login@v2
  with:
    client-id: ${{ secrets.AZURE_CLIENT_ID }}
    tenant-id: ${{ secrets.AZURE_TENANT_ID }}
    subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

- name: Get Secrets from Key Vault
  id: get-secrets
  shell: bash
  run: |
    set -euo pipefail
    KV_NAME="${{ vars.BASE_NAME }}-deploy-kv"
    
    echo "Retrieving secrets from Key Vault: $KV_NAME"
    
    # Retrieve secrets
    SFTP_HOST=$(az keyvault secret show --vault-name $KV_NAME --name sftp-host --query value -o tsv)
    SFTP_USERNAME=$(az keyvault secret show --vault-name $KV_NAME --name sftp-username --query value -o tsv)
    SFTP_PASSWORD=$(az keyvault secret show --vault-name $KV_NAME --name sftp-password --query value -o tsv)
    
    # Mask secrets in logs
    echo "::add-mask::$SFTP_HOST"
    echo "::add-mask::$SFTP_USERNAME"
    echo "::add-mask::$SFTP_PASSWORD"
    
    # Export as environment variables
    {
      echo "SFTP_HOST=$SFTP_HOST"
      echo "SFTP_USERNAME=$SFTP_USERNAME"
      echo "SFTP_PASSWORD=$SFTP_PASSWORD"
    } >> "$GITHUB_ENV"
    
    echo "✓ Secrets retrieved successfully"

- name: Deploy Infrastructure
  uses: azure/arm-deploy@v2
  with:
    parameters: sftpHost="${{ env.SFTP_HOST }}" sftpUsername="${{ env.SFTP_USERNAME }}" sftpPassword="${{ env.SFTP_PASSWORD }}"
```

**Key changes:**
1. Added "Get Secrets from Key Vault" step after Azure login
2. Secrets retrieved using `az keyvault secret show`
3. Secrets masked with `echo "::add-mask::"` to prevent logging
4. Secrets exported to `$GITHUB_ENV` for use in subsequent steps
5. Parameters now reference `env.*` instead of `secrets.*`

#### 4.2 Add GitHub Variable for Key Vault Name

**In GitHub Repository Settings → Environments → PROD:**

Add new variable:
```
Name:  KV_DEPLOY_NAME
Value: cloud-health-office-prod-deploy-kv
```

**Or use pattern-based naming:**
```yaml
# In workflow, derive Key Vault name from base name
KV_NAME="${{ vars.BASE_NAME }}-deploy-kv"
```

#### 4.3 Update DEV and UAT Workflows

Apply similar changes to:
- `.github/workflows/deploy-dev.yml`
- `.github/workflows/deploy-uat.yml`

**Key Vault names:**
- DEV: `cloud-health-office-dev-deploy-kv`
- UAT: `cloud-health-office-uat-deploy-kv`
- PROD: `cloud-health-office-prod-deploy-kv`

---

### Phase 5: Testing & Validation

#### 5.1 Test Workflow with Key Vault Retrieval

**Option A: Workflow Dispatch (Recommended)**

1. Go to **Actions** tab in GitHub
2. Select **Deploy PROD - Cloud Health Office** workflow
3. Click **Run workflow** → **Run workflow**
4. Monitor the "Get Secrets from Key Vault" step
5. Verify secrets are retrieved (masked in logs)
6. Verify deployment succeeds

**Option B: Validation Script**

Use the validation script to test Key Vault access:

```bash
# Test Key Vault access from local machine (simulates GitHub Actions)
./scripts/validate-keyvault-access.sh \
  --vault-name cloud-health-office-prod-deploy-kv \
  --test-secret sftp-host
```

**Expected output:**
```
✓ Azure CLI authenticated
✓ Key Vault 'cloud-health-office-prod-deploy-kv' accessible
✓ Secret 'sftp-host' exists and can be retrieved
✓ All validation checks passed
```

#### 5.2 Verify Secrets are Masked in Logs

Check workflow run logs:

1. Navigate to completed workflow run
2. Expand "Get Secrets from Key Vault" step
3. Verify secret values are shown as `***` (masked)
4. Verify "✓ Secrets retrieved successfully" message appears

**Example log output:**
```
Retrieving secrets from Key Vault: cloud-health-office-prod-deploy-kv
✓ Secrets retrieved successfully
```

❌ **If you see actual secret values in logs:** Workflow is NOT properly masking secrets. Re-check `echo "::add-mask::"` commands.

#### 5.3 Test Full Deployment Flow

1. Make a small, safe change (e.g., update a comment in a workflow file)
2. Commit to a feature branch
3. Open a Pull Request
4. Merge to `main` (or use `workflow_dispatch`)
5. Monitor PROD deployment
6. Verify AKS/Argo Workflows receive correct SFTP credentials via Kubernetes secrets
7. Test SFTP connection from Argo Workflow pods (check Application Insights)

**Validation points:**
- [ ] Azure login succeeds (OIDC)
- [ ] Key Vault secrets retrieved successfully
- [ ] Secrets are masked in logs
- [ ] Infrastructure deployment succeeds
- [ ] Argo Workflow pods can connect to SFTP (runtime test)
- [ ] No errors in Application Insights related to missing credentials

---

### Phase 6: Remove Old GitHub Secrets (After 30-Day Validation Period)

⚠️ **WAIT 30 DAYS** after successful migration before removing old secrets. This provides a rollback window.

#### 6.1 Backup Current GitHub Secrets

Before deletion, document current values:

```bash
# Create encrypted backup (use strong passphrase)
cat > secrets-backup-encrypted.txt <<EOF
SFTP_HOST: <value from GitHub Secrets>
SFTP_USERNAME: <value from GitHub Secrets>
SFTP_PASSWORD: <value from GitHub Secrets>
EOF

# Encrypt backup file
gpg --symmetric --cipher-algo AES256 secrets-backup-encrypted.txt
rm secrets-backup-encrypted.txt

# Store secrets-backup-encrypted.txt.gpg in secure location (e.g., password manager)
```

#### 6.2 Remove Migrated Secrets from GitHub

**In GitHub Repository Settings → Secrets and variables → Actions → Secrets:**

Delete the following secrets (only after 30-day validation period):
- ❌ `SFTP_HOST` (now in Key Vault as `sftp-host`)
- ❌ `SFTP_USERNAME` (now in Key Vault as `sftp-username`)
- ❌ `SFTP_PASSWORD` (now in Key Vault as `sftp-password`)

**Keep these secrets:**
- ✅ `AZURE_CLIENT_ID`
- ✅ `AZURE_TENANT_ID`
- ✅ `AZURE_SUBSCRIPTION_ID`
- ✅ `AZURE_CLIENT_ID_UAT`
- ✅ `AZURE_TENANT_ID_UAT`
- ✅ `AZURE_SUBSCRIPTION_ID_UAT`
- ✅ Third-party integration tokens (`CODECOV_TOKEN`, `SNYK_TOKEN`, etc.)

---

## 🔄 Rollback Procedures

If migration causes issues, follow these rollback steps:

### Immediate Rollback (Revert Workflow Changes)

#### 1. Revert workflow to use GitHub Secrets

```bash
# Revert deploy.yml to previous commit
git revert <commit-hash-of-workflow-update>
git push origin main
```

#### 2. Re-add secrets to GitHub (from backup)

1. Go to **Settings → Secrets and variables → Actions**
2. Add back: `SFTP_HOST`, `SFTP_USERNAME`, `SFTP_PASSWORD`
3. Trigger deployment to verify

#### 3. Investigate failure

- Check Service Principal has "Key Vault Secrets User" role
- Verify Key Vault network ACLs allow GitHub Actions IPs
- Check Key Vault secret names match workflow (e.g., `sftp-host` not `SFTP_HOST`)
- Review Application Insights for errors

### Long-Term Fix

Once root cause is identified:

1. Fix the underlying issue (permissions, naming, network access)
2. Test with workflow_dispatch
3. Re-apply workflow changes
4. Document lessons learned

---

## 🔒 Security Best Practices

### Secret Rotation

**SFTP Credentials:** Rotate every 90 days (HIPAA requirement)

```bash
# Update secret in Key Vault
az keyvault secret set \
  --vault-name cloud-health-office-prod-deploy-kv \
  --name sftp-password \
  --value "<new-password>"

# No workflow changes needed - next deployment will use new value
```

**Rotation Checklist:**
- [ ] Update secret in Key Vault
- [ ] Coordinate with clearinghouse for SFTP password change
- [ ] Test connection from DEV environment first
- [ ] Update SFTP credentials in clearinghouse system
- [ ] Verify Argo Workflow pods can connect with new credentials
- [ ] Document rotation date in change log

### Access Reviews

**Quarterly Review:**
- Who has access to Key Vault? (check RBAC assignments)
- Are all Service Principals still needed?
- Review audit logs for unusual access patterns

```bash
# List all RBAC assignments on Key Vault
az role assignment list \
  --scope /subscriptions/<SUB_ID>/resourceGroups/<RG>/providers/Microsoft.KeyVault/vaults/<KV_NAME> \
  --output table
```

### Audit Logging

Key Vault audit logs are automatically sent to Log Analytics Workspace (configured in `keyvault.bicep`).

**Query audit logs:**
```kql
// Log Analytics query - see who accessed which secrets
AzureDiagnostics
| where ResourceProvider == "MICROSOFT.KEYVAULT"
| where OperationName == "SecretGet"
| project TimeGenerated, identity_claim_appid_g, requestUri_s, CallerIPAddress
| order by TimeGenerated desc
```

**Set up alerts:**
- Alert on failed secret access attempts
- Alert on secret access from unexpected IP addresses
- Alert on secret modifications

---

## 📊 Migration Checklist

Use this checklist to track migration progress:

### Prerequisites
- [ ] OIDC authentication working (GitHub → Azure)
- [ ] Service Principal has "Key Vault Secrets User" role
- [ ] Reviewed `docs/SECRETS-INVENTORY.md`

### Key Vault Deployment
- [ ] Deployed Key Vault for deployment secrets (or verified existing)
- [ ] Verified Key Vault accessible from Azure CLI
- [ ] Key Vault diagnostic logging enabled

### Secret Population
- [ ] Created `sftp-host` secret in Key Vault
- [ ] Created `sftp-username` secret in Key Vault
- [ ] Created `sftp-password` secret in Key Vault
- [ ] Verified secrets retrievable via Azure CLI

### Workflow Updates
- [ ] Updated `deploy.yml` to retrieve from Key Vault
- [ ] Updated `deploy-dev.yml` (if applicable)
- [ ] Updated `deploy-uat.yml` (if applicable)
- [ ] Added `echo "::add-mask::"` to mask secrets
- [ ] Added GitHub variable for Key Vault name (or use pattern-based)

### Testing
- [ ] Tested workflow_dispatch deployment in DEV
- [ ] Verified secrets masked in workflow logs
- [ ] Tested full deployment in DEV
- [ ] Tested Argo Workflow SFTP connection
- [ ] Tested deployment in UAT
- [ ] Tested deployment in PROD

### Cleanup (After 30 Days)
- [ ] Created encrypted backup of old GitHub Secrets
- [ ] Removed `SFTP_HOST` from GitHub Secrets
- [ ] Removed `SFTP_USERNAME` from GitHub Secrets
- [ ] Removed `SFTP_PASSWORD` from GitHub Secrets
- [ ] Verified deployments still work without old secrets

### Documentation
- [ ] Updated `DEPLOYMENT-SECRETS-SETUP.md`
- [ ] Updated `README.md` with Key Vault references
- [ ] Updated `docs/FEDERATED-CREDENTIALS-SETUP.md`
- [ ] Documented Key Vault naming convention

---

## 🆘 Troubleshooting

### Issue: "Key Vault not found"

**Symptoms:** Workflow fails with error "Vault not found: cloud-health-office-prod-deploy-kv"

**Solutions:**
1. Verify Key Vault name is correct (check for typos)
2. Ensure Key Vault is deployed in the same subscription
3. Check Service Principal has access to the subscription

```bash
# List all Key Vaults in subscription
az keyvault list --query "[].name" -o table

# Check if specific Key Vault exists
az keyvault show --name cloud-health-office-prod-deploy-kv
```

### Issue: "Access denied" when retrieving secrets

**Symptoms:** Workflow fails with "The user, group or application does not have secrets get permission"

**Solutions:**
1. Verify Service Principal has "Key Vault Secrets User" role
2. Check RBAC assignments on Key Vault
3. Ensure RBAC authorization is enabled on Key Vault (not access policies)

```bash
# Check role assignments
az role assignment list \
  --assignee <AZURE_CLIENT_ID> \
  --scope /subscriptions/<SUB_ID>/resourceGroups/<RG>/providers/Microsoft.KeyVault/vaults/<KV_NAME>

# Add role if missing
az role assignment create \
  --assignee <AZURE_CLIENT_ID> \
  --role "Key Vault Secrets User" \
  --scope /subscriptions/<SUB_ID>/resourceGroups/<RG>/providers/Microsoft.KeyVault/vaults/<KV_NAME>
```

### Issue: Secrets not masked in logs

**Symptoms:** Secret values appear in plain text in workflow logs

**Solutions:**
1. Ensure `echo "::add-mask::$SECRET_VAR"` is called **before** using the secret
2. Mask secrets immediately after retrieval
3. Don't echo secrets in any step (even debugging)

**Correct pattern:**
```bash
SECRET=$(az keyvault secret show ...)
echo "::add-mask::$SECRET"  # Mask FIRST
echo "Retrieved secret"      # Then log (without value)
```

### Issue: Network connectivity to Key Vault

**Symptoms:** Timeout or "network unreachable" when accessing Key Vault

**Solutions:**
1. Verify `publicNetworkAccess: 'Enabled'` in Key Vault configuration
2. Check `networkAclsDefaultAction: 'Allow'` or add GitHub Actions IP ranges
3. Consider private endpoints if using VNet integration

```bash
# Check Key Vault network settings
az keyvault show --name <KV_NAME> --query "{publicNetworkAccess:properties.publicNetworkAccess, defaultAction:properties.networkAcls.defaultAction}"
```

---

## 📚 Related Documentation

- [Secrets Inventory](./SECRETS-INVENTORY.md) - Complete categorization of all secrets
- [Federated Credentials Setup](./FEDERATED-CREDENTIALS-SETUP.md) - OIDC configuration
- [Deployment Secrets Setup](../DEPLOYMENT-SECRETS-SETUP.md) - Original setup guide
- [Key Vault Bicep Module](../infra/modules/deployment-keyvault.bicep) - Infrastructure as Code
- [Azure Key Vault Best Practices](https://learn.microsoft.com/en-us/azure/key-vault/general/best-practices)

---

## 📞 Support

**For migration assistance:**
- DevOps Team: Review RBAC permissions and Key Vault access
- Security Team: Review compliance requirements and audit logging
- Application Team: Test Argo Workflow connectivity after migration

**Escalation Path:**
1. Review troubleshooting section above
2. Check Application Insights for errors
3. Contact DevOps team for Key Vault access issues
4. Contact Security team for RBAC policy questions

---

**Maintained by:** Cloud Health Office DevOps Team  
**Last Migration:** 2026-02-02 (PROD environment)  
**Next Review:** 2026-05-02 (Quarterly review)
