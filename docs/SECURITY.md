# Security and HIPAA Compliance Guide

## 🚨 Reporting Security Vulnerabilities

**Please do not report security vulnerabilities through public GitHub issues.**

Report vulnerabilities via email to: **security@cloudhealthoffice.com**

You should receive a response within 48 hours. Include:
- Type of vulnerability and affected components
- Steps to reproduce
- Proof-of-concept code (if applicable)
- Potential impact

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.x     | ✅ Security patches |
| < 1.0   | ❌ Not supported   |

---

This document outlines security requirements, HIPAA compliance measures, and secure development practices for the Cloud Health Office system.

## Table of Contents
- [HIPAA Compliance Overview](#hipaa-compliance-overview)
- [Data Classification](#data-classification)
- [Encryption Requirements](#encryption-requirements)
- [Access Control](#access-control)
- [Secrets Management](#secrets-management)
- [Audit Logging](#audit-logging)
- [Incident Response](#incident-response)
- [Secure Development](#secure-development)
- [Code Review Checklist](#code-review-checklist)
- [Compliance Validation](#compliance-validation)

## HIPAA Compliance Overview

### Regulatory Requirements

The Cloud Health Office system processes Protected Health Information (PHI) and must comply with:

- **HIPAA Privacy Rule**: Governs use and disclosure of PHI
- **HIPAA Security Rule**: Requires safeguards for electronic PHI (ePHI)
- **HIPAA Breach Notification Rule**: Requires notification of breaches affecting 500+ individuals

### Applicable Standards

| Standard | Description | Implementation |
|----------|-------------|----------------|
| **Administrative Safeguards** | Policies and procedures | Security policies, training, incident response |
| **Physical Safeguards** | Physical access controls | Azure datacenter security, device encryption |
| **Technical Safeguards** | Technology-based protection | Encryption, access controls, audit logs |

### Covered Information

**PHI Elements Processed:**
- Member/Patient Identifiers
- Claim Numbers
- Provider NPIs (National Provider Identifiers)
- Attachment references to medical records
- Service dates and procedure codes

**Data Handling Requirements:**
- Encrypted in transit and at rest
- Access logged and monitored
- Minimum necessary principle applied
- Secure disposal when no longer needed

### Business Associate Agreements (BAA)

**Required BAAs:**
- ✅ Microsoft Azure (BAA in place for all Azure services)
- ✅ Clearinghouse (trading partner - verify BAA exists)
- ❓ Any third-party monitoring or logging services

**BAA Checklist:**
- [ ] Signed agreement on file
- [ ] Annual review completed
- [ ] Breach notification procedures documented
- [ ] Subcontractor list maintained

## Data Classification

### Classification Levels

| Level | Description | Examples | Handling |
|-------|-------------|----------|----------|
| **Public** | No restrictions | Documentation, architecture diagrams | Standard git practices |
| **Internal** | Company confidential | Infrastructure code, deployment scripts | Private repository |
| **PHI** | HIPAA protected | Member IDs, claim numbers, medical data | Encrypted, logged, access controlled |
| **Credentials** | Authentication secrets | API keys, passwords, certificates | Key Vault, never in code |

### PHI Identification

**Always treat as PHI:**
- Member/Patient ID numbers
- Social Security Numbers (if present)
- Claim numbers that can be linked to individuals
- Provider NPIs when linked to patient records
- Medical record numbers
- Attachment file contents

**Not PHI (but sensitive):**
- API endpoint URLs (without PHI in path)
- Resource group names
- Non-identifying configuration values

### Data Handling Guidelines

#### DO:
- ✅ Encrypt all PHI in transit (TLS 1.2+)
- ✅ Encrypt all PHI at rest (Azure SSE)
- ✅ Log access to PHI (who, what, when)
- ✅ Minimize PHI in logs (mask/redact)
- ✅ Use minimum necessary PHI for each purpose
- ✅ Securely delete PHI when no longer needed

#### DON'T:
- ❌ Store PHI in plain text
- ❌ Log full member IDs or claim numbers
- ❌ Include PHI in error messages
- ❌ Email PHI without encryption
- ❌ Store PHI on local development machines
- ❌ Share PHI in chat or documentation

## Encryption Requirements

### Data in Transit

**All network communications must use encryption:**

#### SFTP (Clearinghouse Integration)
```
Protocol: SSH File Transfer Protocol
Minimum Version: SSH-2
Encryption: AES-256 or stronger
Key Exchange: Diffie-Hellman Group 14 or higher
Authentication: SSH key-based (minimum 2048-bit RSA)
```

**Configuration:**
```bash
# Verify SFTP connection security
sftp -v user@sftp-host 2>&1 | grep -E 'kex|cipher|mac'

# Expected output includes:
# kex: diffie-hellman-group14-sha256
# cipher: aes256-ctr
# mac: hmac-sha2-256
```

#### HTTPS (claims backend API, Azure Services)
```
Protocol: HTTPS
Minimum TLS Version: 1.2
Cipher Suites: Strong ciphers only (no RC4, DES, 3DES)
Certificate Validation: Required
```

**Enforcement:**
```bash
# Enable HTTPS only for Logic App
az webapp update \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --https-only true

# Set minimum TLS version
az webapp config set \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --min-tls-version "1.2"
```

#### Service Bus (AMQP)
```
Protocol: AMQP over TLS
Encryption: Automatic with Service Bus
Authentication: Managed Identity (preferred) or SAS tokens
```

### Data at Rest

**All stored PHI must be encrypted:**

#### Azure Data Lake Storage
```
Encryption: Azure Storage Service Encryption (SSE)
Key Management: Microsoft-managed keys (default)
Algorithm: AES-256
Scope: All data automatically encrypted
```

**Verification:**
```bash
# Verify encryption is enabled
az storage account show \
  --name "storageaccount" \
  --resource-group "rg-name" \
  --query "encryption"

# Expected output:
# {
#   "keySource": "Microsoft.Storage",
#   "services": {
#     "blob": { "enabled": true },
#     "file": { "enabled": true }
#   }
# }
```

#### Service Bus
```
Encryption: Automatic encryption at rest
Key Management: Microsoft-managed keys
Algorithm: AES-256
```

#### Application Insights
```
Encryption: All logs encrypted at rest
Key Management: Microsoft-managed keys
Data Retention: 90 days (default)
```

### Key Management

**Current Implementation:**
- Microsoft-managed keys for all encryption
- Keys automatically rotated by Azure
- No key management overhead

**Enhanced Security (Optional):**
- Customer-managed keys (CMK) in Azure Key Vault
- Bring Your Own Key (BYOK) support
- Keys rotated according to policy
- Access to keys logged and audited

**Implementing CMK:**
```bash
# Create Key Vault
az keyvault create \
  --name "hipaa-keyvault" \
  --resource-group "rg-name" \
  --location "eastus" \
  --enabled-for-disk-encryption true

# Create encryption key
az keyvault key create \
  --vault-name "hipaa-keyvault" \
  --name "storage-encryption-key" \
  --protection software \
  --size 2048

# Configure storage account to use CMK
az storage account update \
  --name "storageaccount" \
  --resource-group "rg-name" \
  --encryption-key-source "Microsoft.Keyvault" \
  --encryption-key-vault "https://hipaa-keyvault.vault.azure.net/" \
  --encryption-key-name "storage-encryption-key"
```

## Access Control

### Identity and Access Management

#### Managed Identity (Preferred)

**Logic App uses system-assigned managed identity:**

```bash
# Enable managed identity
az webapp identity assign \
  --resource-group "rg-name" \
  --name "logic-app-name"

# Get principal ID
PRINCIPAL_ID=$(az webapp identity show \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --query principalId -o tsv)
```

**Benefits:**
- No credentials to manage or rotate
- Automatic credential lifecycle
- Azure AD authentication and authorization
- Granular RBAC permissions

#### Role-Based Access Control (RBAC)

**Required Role Assignments:**

| Resource | Role | Purpose |
|----------|------|---------|
| Data Lake Storage | Storage Blob Data Contributor | Read/write files |
| Service Bus | Azure Service Bus Data Sender | Send messages to topics |
| Integration Account | Integration Account Contributor | Access X12 schemas/agreements |
| Key Vault (if used) | Key Vault Secrets User | Read secrets for API connections |

**Assign Roles:**
```bash
# Storage access
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Storage Blob Data Contributor" \
  --scope "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{storage}"

# Service Bus access
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Azure Service Bus Data Sender" \
  --scope "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ServiceBus/namespaces/{namespace}"

# Verify assignments
az role assignment list \
  --assignee "$PRINCIPAL_ID" \
  --output table
```

### Principle of Least Privilege

**Guidelines:**
- Grant minimum permissions needed
- Use specific scopes (resource-level, not subscription-level)
- Avoid wildcards in permissions
- Regular access reviews (quarterly)

**Example: Restrictive vs Permissive**
```bash
# ❌ Too permissive - Contributor on entire subscription
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Contributor" \
  --scope "/subscriptions/{sub}"

# ✅ Restrictive - Specific role on specific resource
az role assignment create \
  --assignee "$PRINCIPAL_ID" \
  --role "Storage Blob Data Contributor" \
  --scope "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{storage}"
```

### Network Security

#### Current Configuration
- Public endpoints for all services
- HTTPS/TLS encryption for all traffic
- Azure AD authentication required

#### Enhanced Security (Recommended for Production)

**Virtual Network Integration:**
```bash
# Create VNet
az network vnet create \
  --resource-group "rg-name" \
  --name "hipaa-vnet" \
  --address-prefix "10.0.0.0/16" \
  --subnet-name "logic-apps-subnet" \
  --subnet-prefix "10.0.1.0/24"

# Enable VNet integration for Logic App
az webapp vnet-integration add \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --vnet "hipaa-vnet" \
  --subnet "logic-apps-subnet"
```

**Private Endpoints:**
```bash
# Create private endpoint for Storage
az network private-endpoint create \
  --resource-group "rg-name" \
  --name "storage-private-endpoint" \
  --vnet-name "hipaa-vnet" \
  --subnet "private-endpoints-subnet" \
  --private-connection-resource-id "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{storage}" \
  --connection-name "storage-connection" \
  --group-id "blob"

# Disable public access to storage
az storage account update \
  --name "storageaccount" \
  --resource-group "rg-name" \
  --public-network-access Disabled
```

**Network Security Groups (NSG):**
```bash
# Create NSG
az network nsg create \
  --resource-group "rg-name" \
  --name "hipaa-nsg"

# Allow only HTTPS
az network nsg rule create \
  --resource-group "rg-name" \
  --nsg-name "hipaa-nsg" \
  --name "AllowHTTPS" \
  --priority 100 \
  --direction Inbound \
  --access Allow \
  --protocol Tcp \
  --destination-port-ranges 443

# Deny all other inbound
az network nsg rule create \
  --resource-group "rg-name" \
  --nsg-name "hipaa-nsg" \
  --name "DenyAllInbound" \
  --priority 4096 \
  --direction Inbound \
  --access Deny \
  --protocol '*' \
  --destination-port-ranges '*'
```

### User Access Management

**Developer Access:**
- Use Azure AD accounts (no shared accounts)
- Require Multi-Factor Authentication (MFA)
- Grant access via Azure AD groups
- Regular access reviews

**Access Review Process:**
1. **Quarterly**: Review all role assignments
2. **Identify**: Unused or excessive permissions
3. **Revoke**: Access no longer needed
4. **Document**: Changes and justifications

## Secrets Management

### Azure Key Vault Integration

**Store all secrets in Key Vault:**

```bash
# Create Key Vault
az keyvault create \
  --name "hipaa-keyvault" \
  --resource-group "rg-name" \
  --location "eastus" \
  --enable-rbac-authorization false

# Store SFTP password
az keyvault secret set \
  --vault-name "hipaa-keyvault" \
  --name "sftp-password" \
  --value "actual-password"

# Store claims backend API client secret
az keyvault secret set \
  --vault-name "hipaa-keyvault" \
  --name "backend-client-secret" \
  --value "actual-secret"

# Grant Logic App access to secrets
az keyvault set-policy \
  --name "hipaa-keyvault" \
  --object-id "$PRINCIPAL_ID" \
  --secret-permissions get list
```

**Reference in Logic App:**
```json
{
  "parameters": {
    "sftpPassword": {
      "type": "securestring",
      "value": "@keyvault('https://hipaa-keyvault.vault.azure.net/secrets/sftp-password')"
    },
    "backendClientSecret": {
      "type": "securestring",
      "value": "@keyvault('https://hipaa-keyvault.vault.azure.net/secrets/backend-client-secret')"
    }
  }
}
```

### Secrets in Code


**Git Secrets Scanning:**
```bash
# Install git-secrets
brew install git-secrets  # macOS
# or
apt-get install git-secrets  # Linux

# Setup for repository
cd /path/to/hipaa-attachments
git secrets --install
git secrets --register-aws
git secrets --add '[aA][pP][iI]_?[kK][eE][yY].*['\''"][0-9a-zA-Z]{32,45}['\''"]'

# Scan repository
git secrets --scan
```

### GitHub Secrets

**For CI/CD pipelines, use GitHub Secrets:**

```bash
# Set secret via GitHub CLI
gh secret set AZURE_CLIENT_ID_DEV --body "$CLIENT_ID"
gh secret set AZURE_TENANT_ID_DEV --body "$TENANT_ID"
gh secret set AZURE_SUBSCRIPTION_ID_DEV --body "$SUBSCRIPTION_ID"

# Or via GitHub UI:
# Repository → Settings → Secrets and variables → Actions → New repository secret
```

**DO NOT:**
- ❌ Print secrets in logs (`echo $SECRET`)
- ❌ Store secrets in variables without masking
- ❌ Include secrets in error messages
- ❌ Pass secrets as command-line arguments

**DO:**
- ✅ Use GitHub secrets or Key Vault references
- ✅ Mask secrets in logs (GitHub does this automatically)
- ✅ Use `[SecureString]` parameters in PowerShell
- ✅ Clear sensitive variables after use

## Audit Logging

### Azure Activity Log

**Automatically logs:**
- Resource creation/modification/deletion
- Role assignments and changes
- Policy assignments
- Diagnostic settings changes

**Retention:** 90 days (default), extend to 365+ days for compliance

**Query Activity Log:**
```bash
# Recent errors
az monitor activity-log list \
  --resource-group "rg-name" \
  --start-time "2024-01-01T00:00:00Z" \
  --query "[?level=='Error']" \
  --output table

# Role assignments
az monitor activity-log list \
  --resource-group "rg-name" \
  --caller "user@example.com" \
  --query "[?contains(operationName.value, 'roleAssignments')]"
```

### Application Insights

**Logged automatically:**
- All workflow runs (start, complete, failure)
- API calls (claims backend, SFTP, Service Bus)
- Exceptions and errors
- Performance metrics
- Custom events

**Enable Detailed Logging:**
```bash
az webapp log config \
  --resource-group "rg-name" \
  --name "logic-app-name" \
  --application-logging true \
  --level verbose
```

**Log Retention:**
```bash
# Set retention to 365 days
az monitor app-insights component update \
  --app "app-insights-name" \
  --resource-group "rg-name" \
  --retention-time 365
```

### PHI Access Logging

**Requirements:**
- Log all access to PHI
- Include: who, what, when, why
- Retain logs for 6 years (HIPAA requirement)
- Protect logs from tampering

**Implementation:**
```kusto
// Query PHI access in Application Insights
customEvents
| where timestamp > ago(30d)
| where name in ("file_accessed", "claim_linked", "attachment_processed")
| extend 
    userId = tostring(customDimensions["userId"]),
    resourceId = tostring(customDimensions["resourceId"]),
    action = tostring(customDimensions["action"])
| project timestamp, userId, action, resourceId
| order by timestamp desc
```

**Custom Logging in Workflows:**
```json
{
  "type": "Compose",
  "inputs": {
    "eventType": "phi_access",
    "userId": "@{workflow()['run']['identity']['principalId']}",
    "resourceType": "claim",
    "resourceId": "@{body('Extract_Metadata')['claimNumber']}",
    "action": "linked_attachment",
    "timestamp": "@{utcNow()}"
  },
  "runAfter": {
    "Call_CLAIMS_BACKEND_API": ["Succeeded"]
  }
}
```

### Monitoring and Alerts

**Critical Alerts:**
```bash
# Alert on authentication failures
az monitor metrics alert create \
  --name "authentication-failures" \
  --resource-group "rg-name" \
  --scopes "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{logic-app}" \
  --condition "count statusCode == 401 > 10" \
  --window-size 5m \
  --evaluation-frequency 1m

# Alert on encryption failures
az monitor metrics alert create \
  --name "encryption-errors" \
  --resource-group "rg-name" \
  --scopes "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{storage}" \
  --condition "count errors contains 'encryption' > 0" \
  --window-size 5m
```

## Incident Response

### Incident Classification

| Severity | Description | Examples | Response Time |
|----------|-------------|----------|---------------|
| **Critical** | Confirmed PHI breach | Unauthorized access to PHI, data exfiltration | Immediate (< 1 hour) |
| **High** | Potential PHI exposure | Misconfigured permissions, logs exposed | < 4 hours |
| **Medium** | Security vulnerability | Outdated library, weak configuration | < 24 hours |
| **Low** | Security hygiene | Password policy violation, unused access | < 1 week |

### Breach Response Procedure

**If PHI breach suspected:**

1. **Immediate Actions (< 1 hour):**
   - Contain the incident (disable affected resources)
   - Preserve evidence (logs, snapshots)
   - Notify security team and management

2. **Investigation (< 24 hours):**
   - Determine scope (what PHI, how many records)
   - Identify root cause
   - Document timeline
   - Assess risk of harm

3. **Notification (< 60 days):**
   - Notify affected individuals if ≥500 people
   - Notify HHS (OCR) if required
   - Notify media if ≥500 people in jurisdiction
   - Document all notifications

4. **Remediation:**
   - Fix root cause
   - Implement preventive controls
   - Update policies and procedures
   - Conduct training

5. **Post-Incident Review:**
   - Lessons learned
   - Update incident response plan
   - Improve monitoring and detection

### Incident Response Contacts

**Internal:**
- Security Team: security@cloudhealthoffice.com
- Compliance Officer: compliance@cloudhealthoffice.com
- Legal: legal@cloudhealthoffice.com

**External:**
- Azure Support: 1-800-642-7676
- HHS OCR: https://ocrportal.hhs.gov/

### Evidence Preservation

**In case of incident:**
```bash
# Snapshot current state
az resource list --resource-group "rg-name" --output json > resources-snapshot.json

# Export Activity Log
az monitor activity-log list \
  --resource-group "rg-name" \
  --start-time "2024-01-01" \
  --query "[].{time:eventTimestamp, user:caller, operation:operationName.value, status:status.value}" \
  --output json > activity-log-export.json

# Export Application Insights logs
# Use Azure Portal: Analytics → Export → Export to CSV

# Take storage account snapshots
az storage blob snapshot \
  --account-name "storageaccount" \
  --container-name "hipaa-attachments" \
  --name "sensitive-file.edi"
```

## Secure Development

### Secure Coding Practices

#### Input Validation
```javascript
// ❌ WRONG - No validation
const claimNumber = inputs.claimNumber;

// ✅ CORRECT - Validate input
const claimNumber = inputs.claimNumber;
if (!claimNumber || claimNumber.length > 50 || !/^[A-Z0-9]+$/.test(claimNumber)) {
  throw new Error('Invalid claim number format');
}
```

#### Output Encoding
```javascript
// ❌ WRONG - Logging sensitive data
console.log(`Processing claim: ${claimNumber} for member: ${memberId}`);

// ✅ CORRECT - Mask sensitive data
console.log(`Processing claim: ${claimNumber.substring(0, 3)}*** for member: ***${memberId.substring(memberId.length - 4)}`);
```

#### Error Handling
```javascript
// ❌ WRONG - Exposing internals
catch (error) {
  return {
    status: 500,
    message: error.message,
    stack: error.stack,
    query: sqlQuery
  };
}

// ✅ CORRECT - Generic error message
catch (error) {
  logger.error('Database operation failed', { correlationId, error });
  return {
    status: 500,
    message: 'An error occurred processing your request',
    correlationId: correlationId
  };
}
```

### Dependency Management

**Scan for vulnerabilities:**
```bash
# Scan npm dependencies (if using Node.js functions)
npm audit

# Fix vulnerabilities
npm audit fix

# For high/critical issues, update immediately
npm update package-name
```

**GitHub Dependabot:**
- Automatically enabled for repository
- Creates PRs for security updates
- Review and merge promptly

**Third-Party Libraries:**
- Use only trusted, well-maintained libraries
- Review security advisories before adding dependencies
- Keep dependencies up to date
- Use tools listed in ecosystem categories supported by the gh-advisory-database tool

### Code Review Security Checklist

**Before approving PR, verify:**

- [ ] No hardcoded secrets or credentials
- [ ] No PHI logged in plain text
- [ ] Input validation present for all user inputs
- [ ] Error messages don't expose sensitive information
- [ ] Encryption used for sensitive data
- [ ] Managed identity used (not connection strings)
- [ ] Principle of least privilege applied
- [ ] Audit logging implemented for PHI access
- [ ] Dependencies scanned for vulnerabilities
- [ ] Unit tests include security scenarios

### Security Testing

**Pre-deployment:**
```bash
# Static code analysis (if applicable)
# For PowerShell:
Invoke-ScriptAnalyzer -Path . -Recurse

# For Bicep:
az bicep build --file infra/main.bicep

# Dependency scanning
# (Automatic via GitHub Dependabot)

# Secrets scanning
git secrets --scan
```

**Post-deployment:**
```bash
# Test authentication
curl -I https://logic-app.azurewebsites.net/api/replay278
# Should return 401 Unauthorized (not anonymous)

# Test HTTPS enforcement
curl -I http://logic-app.azurewebsites.net
# Should redirect to HTTPS

# Test API connections with invalid credentials
# Should fail with proper error handling
```

## Code Review Checklist

### Security-Focused Review

**Authentication & Authorization:**
- [ ] Managed identity used for Azure resource access
- [ ] API connections properly authenticated
- [ ] No hardcoded credentials
- [ ] Proper RBAC roles assigned

**Data Protection:**
- [ ] PHI encrypted in transit (TLS 1.2+)
- [ ] PHI encrypted at rest
- [ ] No PHI in logs or error messages
- [ ] PHI masked/redacted when displayed

**Input Validation:**
- [ ] All inputs validated (type, length, format)
- [ ] No SQL injection vulnerabilities
- [ ] No command injection vulnerabilities
- [ ] No path traversal vulnerabilities

**Error Handling:**
- [ ] Generic error messages to users
- [ ] Detailed errors logged securely
- [ ] No stack traces exposed
- [ ] Correlation IDs for tracing

**Logging & Auditing:**
- [ ] PHI access logged
- [ ] Authentication attempts logged
- [ ] Error conditions logged
- [ ] Sufficient detail for investigation

**Network Security:**
- [ ] HTTPS only
- [ ] Minimum TLS 1.2
- [ ] Strong cipher suites
- [ ] Certificate validation enabled

## Compliance Validation

### Regular Security Assessments

**Monthly:**
- Review access logs for anomalies
- Check for failed authentication attempts
- Verify encryption is enabled
- Review new vulnerabilities

**Quarterly:**
- Access review (remove unused access)
- Review role assignments
- Update security documentation
- Conduct security awareness training

**Annually:**
- Full security assessment
- Penetration testing
- HIPAA compliance audit
- Business Associate Agreement review
- Disaster recovery testing

### Compliance Checklist

**HIPAA Security Rule Compliance:**

- [ ] **Access Controls:**
  - [ ] Unique user IDs
  - [ ] Emergency access procedure
  - [ ] Automatic logoff
  - [ ] Encryption and decryption

- [ ] **Audit Controls:**
  - [ ] Logging mechanisms in place
  - [ ] Logs reviewed regularly
  - [ ] Logs protected from tampering

- [ ] **Integrity Controls:**
  - [ ] Data integrity validation
  - [ ] Corruption detection
  - [ ] Backup procedures

- [ ] **Transmission Security:**
  - [ ] Encryption in transit
  - [ ] Secure protocols (SFTP, HTTPS)
  - [ ] Certificate validation

- [ ] **Person/Entity Authentication:**
  - [ ] Azure AD authentication
  - [ ] MFA enabled
  - [ ] Service principal authentication

### Documentation Requirements

**Required Documentation:**
- [x] System architecture (ARCHITECTURE.md)
- [x] Security policies (this document)
- [x] Deployment procedures (DEPLOYMENT.md)
- [ ] Risk assessment
- [ ] Business Associate Agreements
- [ ] Incident response plan
- [ ] Disaster recovery plan
- [ ] Security training records
- [ ] Audit logs and reviews

### Attestation

**Annual Attestation Process:**
1. Review all security controls
2. Verify compliance with policies
3. Document any exceptions
4. Management sign-off
5. Store for 6 years

## ECS Integration Security Notes

### claims backend API Token Management

**CRITICAL**: The ECS Summary Search workflow requires a claims backend API token for backend integration.

**DO NOT:**
- ❌ Commit API tokens, secrets, or passwords to version control
- ❌ Include token values in Bicep outputs or module definitions
- ❌ Use default values for `SecureString` parameters in Logic App workflows
- ❌ Store tokens in plain text in app settings

**DO:**
- ✅ Store claims backend API tokens in Azure Key Vault
- ✅ Reference Key Vault secrets via app settings: `@Microsoft.KeyVault(SecretUri=...)`
- ✅ Use Managed Identity for authentication where possible
- ✅ Rotate tokens regularly (every 90 days minimum)
- ✅ Audit token access and usage

**Configuration Example:**
```bash
# Store token in Key Vault
az keyvault secret set \
  --vault-name "${KV_NAME}" \
  --name "claims-backend-api-token" \
  --value "${claims backend_TOKEN}"

# Configure Logic App to reference Key Vault
SECRET_URI=$(az keyvault secret show \
  --vault-name "${KV_NAME}" \
  --name "claims-backend-api-token" \
  --query id -o tsv)

az webapp config appsettings set \
  --resource-group "${RG_NAME}" \
  --name "${LOGIC_APP_NAME}" \
  --settings "ECS_CLAIMS_BACKEND_API_TOKEN=@Microsoft.KeyVault(SecretUri=${SECRET_URI})"
```

See [DEPLOYMENT.md](DEPLOYMENT.md#7-configure-ecs-enhanced-claim-status-integration) for complete setup instructions.

---

For architecture details, see [ARCHITECTURE.md](ARCHITECTURE.md)  
For deployment procedures, see [DEPLOYMENT.md](DEPLOYMENT.md)  
For troubleshooting, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md)  

**Last Updated:** 2024-01-15  
**Next Review:** 2024-04-15
