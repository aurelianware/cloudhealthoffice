# Self-Service Tenant Onboarding

This document describes the automated tenant onboarding system for Cloud Health Office.

## Overview

When a new health plan signs up through a local or customer-deployed portal, for example `https://portal.<your-domain>/signup`, the system automatically:

1. Creates tenant record in database
2. Provisions Cosmos DB containers
3. Creates SFTP directory structure  
4. Sets up Stripe billing subscription
5. Creates Azure AD B2C admin user
6. Generates API keys
7. Seeds default benefit plans
8. Sends welcome email with credentials
9. (Enterprise tier) Creates dedicated Kubernetes namespace

**Time to first claim: < 1 hour**

## User Flow

### 1. Signup Page (`/signup`)

User fills out registration form:
- Organization name
- Display name
- Contact information (name, email, phone)
- Subscription tier (Starter/Professional/Enterprise)
- Enabled modules (claims, eligibility, authorizations, etc.)

### 2. Tenant Creation

Portal calls Tenant Management Service API:
```bash
POST /api/v1/tenants
{
  "organizationName": "Blue Shield of California",
  "tenantName": "Blue Shield CA",
  "subscriptionTier": "professional",
  "contactInfo": { ... },
  "enabledModules": ["claims", "eligibility", "authorizations"]
}
```

### 3. Workflow Trigger

Portal triggers Argo workflow for automated provisioning:
```bash
POST /api/v1/workflows/cho-workflows/submit
{
  "resourceName": "tenant-onboarding",
  "parameters": {
    "tenant-id": "blue-shield-of-california-a1b2c3",
    "admin-email": "admin@blueshieldca.com",
    ...
  }
}
```

### 4. Automated Provisioning

Argo workflow executes:

**Step 1: Validation**
- Check if tenant already exists
- Validate email domain
- Check subscription limits

**Step 2: Database Setup** (parallel)
- Create Cosmos DB containers with `/tenantId` partition key:
  - Members
  - Coverage
  - Claims
  - BenefitPlans
  - Authorizations

**Step 3: SFTP Structure**
```
/tenants/{tenant-id}/
├── inbound/
│   ├── 837/  (claims)
│   ├── 835/  (ERA)
│   ├── 270/  (eligibility inquiry)
│   ├── 834/  (enrollment)
│   └── ...
├── outbound/
│   ├── 271/  (eligibility response)
│   ├── 277/  (claim status)
│   └── ...
├── archive/
└── errors/
```

**Step 4: Stripe Billing**
- Create Stripe customer
- Attach payment method (provided during signup)
- Create subscription based on tier
- Configure usage-based metering

**Step 5: User Creation**
- Create Azure AD B2C user account
- Generate temporary password
- Assign tenant-specific roles

**Step 6: Default Data**
- Seed default PPO benefit plan
- Import standard reference data (CPT, ICD-10 codes)
- Configure default clearinghouse settings

**Step 7: API Keys**
- Generate production API key
- Generate test/sandbox API key
- Store securely in Kubernetes secrets

**Step 8: Welcome Email**
```
To: admin@blueshieldca.com
Subject: Welcome to Cloud Health Office

Your account is ready!

Tenant ID: blue-shield-of-california-a1b2c3
Portal: https://portal.<your-domain>
API Endpoint: https://api.<your-domain>

Credentials:
- Email: admin@blueshieldca.com
- Password: [temporary password]

API Keys:
- Production: cho_prod_abc123xyz...
- Test: cho_test_def456uvw...

Next steps:
1. Log in and change password
2. Sign Business Associate Agreement (BAA)
3. Configure clearinghouse (Availity, Change Healthcare, etc.)
4. Set up trading partners
5. Upload first 834 enrollment file

Need help? support@cloudhealthoffice.com
```

**Step 9: Enterprise Namespace** (enterprise tier only)
- Create dedicated `cho-tenant-{tenant-id}` namespace
- Deploy isolated microservices
- Configure dedicated ingress
- Set up monitoring/alerting

**Step 10: Activation**
- Update tenant status to `active`
- Enable API access
- Start billing cycle

### 5. Confirmation Page

User sees success message with:
- Tenant ID
- Next steps checklist
- Estimated activation time (usually < 5 minutes)
- Link to getting started guide

## Deployment

Deploy the onboarding workflow:

```bash
./deploy-tenant-onboarding.sh
```

This script will:
1. Check prerequisites (kubectl, argo CLI)
2. Create required Kubernetes secrets
3. Deploy workflow template to `cho-workflows` namespace
4. Optionally run a test workflow

## Manual Workflow Execution

Test the workflow manually:

```bash
argo submit -n cho-workflows argo-workflows/tenant-onboarding.yaml \
  --parameter tenant-id="test-health-plan" \
  --parameter tenant-name="Test Health Plan" \
  --parameter organization-name="Test Health Insurance" \
  --parameter admin-email="admin@test.com" \
  --parameter admin-name="John Test" \
  --parameter subscription-tier="starter" \
  --parameter enabled-modules="claims,eligibility" \
  --watch
```

Monitor workflow:

```bash
# List workflows
argo list -n cho-workflows

# View logs
argo logs -n cho-workflows tenant-onboarding-xxxxx

# Get workflow details
argo get -n cho-workflows tenant-onboarding-xxxxx
```

## Required Secrets

The workflow requires these Kubernetes secrets in `cho-workflows` namespace:

### cosmos-db-credentials
```bash
kubectl create secret generic cosmos-db-credentials -n cho-workflows \
  --from-literal=account-name=cloudhealthoffice-cosmos \
  --from-literal=resource-group=prod-cloudhealthoffice-rg
```

### azure-service-principal
```bash
kubectl create secret generic azure-service-principal -n cho-workflows \
  --from-literal=client-id=<azure-app-id> \
  --from-literal=client-secret=<azure-app-secret> \
  --from-literal=tenant-id=<azure-tenant-id>
```

### stripe-api-keys
```bash
kubectl create secret generic stripe-api-keys -n cho-workflows \
  --from-literal=secret-key=sk_live_...
```

### tenant-service-credentials
```bash
kubectl create secret generic tenant-service-credentials -n cho-workflows \
  --from-literal=api-key=<generated-api-key>
```

## Monitoring

View onboarding workflows:
```bash
# Argo UI
kubectl port-forward -n cho-workflows svc/argo-workflows-server 2746:2746
# Open https://localhost:2746

# Kubernetes dashboard
kubectl -n cho-workflows get workflows --watch

# Logs
kubectl -n cho-workflows logs -l workflows.argoproj.io/workflow=tenant-onboarding
```

## Troubleshooting

### Workflow stuck in pending
```bash
# Check Argo controller logs
kubectl -n cho-workflows logs -l app=workflow-controller

# Check resource limits
kubectl describe pod -n cho-workflows <workflow-pod-name>
```

### Cosmos DB container creation failed
```bash
# Check Azure credentials
kubectl -n cho-workflows get secret azure-service-principal -o yaml

# Verify Azure CLI authentication
az cosmosdb sql container list \
  --account-name cloudhealthoffice-cosmos \
  --resource-group prod-cloudhealthoffice-rg \
  --database-name CloudHealthOffice
```

### Email not sent
- Verify SendGrid/Azure Communication Services configuration
- Check email service logs
- Ensure `SENDGRID_API_KEY` secret exists

### Tenant stuck in "pending" status
```bash
# Get workflow status
argo get -n cho-workflows <workflow-name>

# Manually activate tenant
curl -X PUT http://tenant-service.cloudhealthoffice/api/v1/tenants/<tenant-id> \
  -H "Content-Type: application/json" \
  -d '{"status": "active"}'
```

## Cleanup Failed Onboarding

If a workflow fails, clean up partial resources:

```bash
# Delete tenant record
curl -X DELETE http://tenant-service.cloudhealthoffice/api/v1/tenants/<tenant-id>

# Delete Cosmos containers (if created)
az cosmosdb sql container delete \
  --account-name cloudhealthoffice-cosmos \
  --resource-group prod-cloudhealthoffice-rg \
  --database-name CloudHealthOffice \
  --name Members \
  --yes

# Cancel Stripe subscription
curl -X POST http://tenant-service.cloudhealthoffice/api/v1/billing/tenants/<tenant-id>/cancel

# Delete Azure AD user
az ad user delete --id admin@tenant.com
```

## Metrics

Track onboarding success:

```bash
# Total signups today
kubectl -n cho-workflows get workflows \
  --selector=workflow-type=tenant-onboarding \
  --field-selector=metadata.creationTimestamp>$(date -u -d '1 day ago' +%Y-%m-%dT%H:%M:%SZ) \
  | wc -l

# Success rate
argo list -n cho-workflows --status Succeeded | wc -l
argo list -n cho-workflows --status Failed | wc -l
```

## Next Steps

1. **Email Integration**: Connect to SendGrid or Azure Communication Services
2. **Azure AD B2C**: Implement actual user creation (currently stubbed)
3. **SFTP Automation**: Implement actual SFTP folder creation
4. **BAA E-Signature**: Integrate with DocuSign for automated BAA signing
5. **Payment Method**: Add Stripe payment form to signup flow
6. **Admin Console**: Build tenant admin portal for post-signup configuration

---

## Azure AD Admin Consent

### What is Admin Consent?

When a user logs in to the portal for the first time, Azure AD requires them to grant permissions for the Cloud Health Office application to access their Azure AD profile and resources.

### Admin Consent Screen

Users will see a screen like this on first login:

```
Cloud Health Office needs permission to access resources in your organization

Permissions requested:
✓ Sign you in and read your profile
✓ Read your email address
✓ Access APIs on your behalf

By clicking Accept, you allow this app to use your data as specified in their privacy statement and terms of service.

[Accept] [Cancel]
```

### Granting Consent

**For Individual Users**:
1. Click **Accept** on the consent screen
2. You'll be redirected to the portal dashboard
3. This is a one-time action - subsequent logins won't require consent

**For Organization-Wide Consent** (Azure AD Administrators):
```bash
# Grant admin consent for all users in tenant
az ad app permission admin-consent \
  --id <cloud-health-office-app-id>
```

This allows all users in the organization to use the app without individual consent prompts.

### Troubleshooting Admin Consent

**Error: "Need admin approval"**
- **Cause**: User doesn't have sufficient permissions
- **Solution**: User must be Global Admin or have User Admin role
- **Workaround**: Ask your Azure AD admin to grant consent via Azure Portal:
  1. Go to Azure AD > Enterprise Applications
  2. Find "Cloud Health Office"
  3. Click Permissions > Grant admin consent

**Error: "AADSTS65004: User declined to consent"**
- **Cause**: User clicked "Cancel" on consent screen
- **Solution**: Try logging in again and click "Accept"

**Stuck in redirect loop after consent**:
- **Cause**: Cookie configuration issues
- **Solution**: Clear browser cookies and try again
- **Check**: Ensure SameSite=Lax in portal cookie settings

### Required Permissions

The application requests these Microsoft Graph API permissions:

| Permission | Type | Reason |
|------------|------|--------|
| `User.Read` | Delegated | Read user's profile information |
| `email` | Delegated | Access user's email address |
| `openid` | Delegated | Sign-in capability |
| `profile` | Delegated | Read basic profile information |

All are **low-privilege** delegated permissions that only access the logged-in user's data.

---

## SFTP Access

### Overview

Each tenant gets a dedicated SFTP account for file-based X12 EDI exchange.

### SFTP Credentials

Provided in welcome email:
- **Host**: `sftp.cloudhealthoffice.com`
- **Port**: `22`
- **Username**: `cho-{tenant-id}`
- **Password**: Generated 24-character password
- **SSH Key**: Download from portal (Settings > SFTP Access)

### Connection Methods

#### 1. Command Line (Linux/macOS)

**With SSH Key** (recommended):
```bash
# Download private key from portal
# Save as ~/.ssh/cloudhealthoffice_key

# Set permissions
chmod 600 ~/.ssh/cloudhealthoffice_key

# Connect
sftp -i ~/.ssh/cloudhealthoffice_key cho-{tenant-id}@sftp.cloudhealthoffice.com
```

**With Password**:
```bash
sftp cho-{tenant-id}@sftp.cloudhealthoffice.com
# Enter password when prompted
```

#### 2. FileZilla (Windows/macOS)

1. Open FileZilla
2. File > Site Manager > New Site
3. Configure:
   - Protocol: **SFTP - SSH File Transfer Protocol**
   - Host: `sftp.cloudhealthoffice.com`
   - Port: `22`
   - Logon Type: **Normal** (for password) or **Key file** (for SSH key)
   - User: `cho-{tenant-id}`
   - Password: (from welcome email)
   - Key file: (downloaded from portal)
4. Click **Connect**

#### 3. WinSCP (Windows)

1. Open WinSCP
2. New Site
3. Configure:
   - File protocol: **SFTP**
   - Host name: `sftp.cloudhealthoffice.com`
   - Port number: `22`
   - User name: `cho-{tenant-id}`
   - Password: (from email)
   - Advanced > SSH > Authentication > Private key file: (from portal)
4. Click **Login**

#### 4. Automated Scripts

**Python Example**:
```python
import paramiko

# Connect with SSH key
key = paramiko.Ed25519Key.from_private_key_file('/path/to/key')
transport = paramiko.Transport(('sftp.cloudhealthoffice.com', 22))
transport.connect(username='cho-{tenant-id}', pkey=key)
sftp = paramiko.SFTPClient.from_transport(transport)

# Upload claim file
sftp.put('/local/path/claim.edi', '/tenants/{tenant-id}/inbound/837/claim_20260208_001.edi')

# Download response
sftp.get('/tenants/{tenant-id}/outbound/277/response.edi', '/local/path/response.edi')

sftp.close()
transport.close()
```

**PowerShell Example**:
```powershell
# Load WinSCP .NET assembly
Add-Type -Path "C:\Program Files (x86)\WinSCP\WinSCPnet.dll"

$sessionOptions = New-Object WinSCP.SessionOptions -Property @{
    Protocol = [WinSCP.Protocol]::Sftp
    HostName = "sftp.cloudhealthoffice.com"
    UserName = "cho-{tenant-id}"
    SshPrivateKeyPath = "C:\path\to\key.ppk"
}

$session = New-Object WinSCP.Session
$session.Open($sessionOptions)

# Upload file
$session.PutFiles("C:\claims\claim.edi", "/tenants/{tenant-id}/inbound/837/").Check()

# Download results
$session.GetFiles("/tenants/{tenant-id}/outbound/277/*.edi", "C:\responses\").Check()

$session.Dispose()
```

### Directory Structure

```
/tenants/{tenant-id}/
├── inbound/
│   ├── 837/    ← Drop professional claims (837P) here
│   ├── 270/    ← Drop eligibility inquiries (270) here
│   ├── 834/    ← Drop enrollment files (834) here
│   ├── 276/    ← Drop claim status requests (276) here
│   └── 278/    ← Drop prior auth requests (278) here
├── outbound/
│   ├── 271/    ← Eligibility responses (271) appear here
│   ├── 277/    ← Claim status responses (277) appear here
│   ├── 278/    ← Prior auth responses (278) appear here
│   └── 835/    ← ERA payment files (835) appear here
└── archive/    ← Processed files moved here after 7 days
```

### File Naming Convention

**Required Format**:
```
{transaction-type}_{payer-id}_{YYYYMMDD}_{sequence}.edi
```

**Examples**:
- `837_BCBSCA_20260208_001.edi` - Professional claim to BCBS California
- `270_MEDICARE_20260208_001.edi` - Eligibility inquiry for Medicare
- `834_AETNA_20260208_001.edi` - Enrollment file for Aetna

**Rules**:
- Transaction type: 837, 270, 834, 276, 278
- Payer ID: Use trading partner ID configured in portal
- Date: YYYYMMDD format
- Sequence: 001, 002, 003... (increment for multiple files same day)
- Extension: Always `.edi`

### File Processing

1. **Upload**: Drop file in appropriate `/inbound/{type}/` folder
2. **Detection**: File processor detects upload within 60 seconds
3. **Validation**: X12 syntax validation and schema check
4. **Workflow**: Argo workflow triggered based on transaction type
5. **Processing**: 
   - 837 → Claims submission to clearinghouse
   - 270 → Eligibility check against payer
   - 834 → Member enrollment update
6. **Response**: Output file appears in `/outbound/{type}/` within 2-5 minutes
7. **Archive**: Original file moved to `/archive/` after 7 days

### Monitoring SFTP Activity

**Portal Dashboard** (Settings > SFTP Activity):
- Recent uploads (last 24 hours)
- File processing status
- Failed validations with error details
- Download history

**API Monitoring**:
```bash
# Get SFTP activity log
curl -H "X-API-Key: {api-key}" \
     -H "X-Tenant-ID: {tenant-id}" \
     https://api.<your-domain>/v1/sftp/activity

# Get failed uploads
curl -H "X-API-Key: {api-key}" \
     -H "X-Tenant-ID: {tenant-id}" \
     https://api.<your-domain>/v1/sftp/failures
```

### Security Best Practices

1. **Use SSH Keys**: More secure than passwords for automation
2. **Rotate Keys**: Generate new SSH key every 90 days
3. **Restrict Access**: Only grant SFTP access to necessary personnel
4. **Monitor Uploads**: Review SFTP activity dashboard weekly
5. **Validate Files**: Always validate X12 syntax before uploading
6. **Encrypt Locally**: Encrypt sensitive files before upload (optional)

### Troubleshooting SFTP

**Connection Refused**:
- Verify host: `sftp.cloudhealthoffice.com` (not `https://`)
- Verify port: `22` (not 21 for FTP)
- Check firewall allows outbound port 22

**Authentication Failed**:
- Verify username matches: `cho-{tenant-id}`
- Password: Use password from welcome email (not portal password)
- SSH key: Ensure using private key (not public key)
- Key format: Convert to OpenSSH format if using PuTTY .ppk

**Permission Denied**:
- You're locked to `/tenants/{tenant-id}/` (chroot jail)
- Cannot access other tenants' folders
- Cannot write to `/outbound/` (read-only)

**File Not Processing**:
- Check file naming convention
- Verify file is valid X12 EDI format
- Check `/errors/` folder for rejected files
- Review portal SFTP Activity log

---

## Clearinghouse Configuration

After onboarding, tenants must configure clearinghouse connections to submit claims.

### Supported Clearinghouses

#### 1. Availity (Most Common)

**Portal Setup** (Settings > Clearinghouse > Add Connection):
- Clearinghouse: **Availity**
- Sender ID: Your NPI or Tax ID
- Receiver ID: `AVAILITY`
- Interchange Control ID:
  - Test: `TEST`
  - Production: Assigned by Availity
- Application Control ID: Same as Interchange ID

**Availity Portal**:
1. Register at https://www.availity.com/provider
2. Complete practice verification
3. Request submitter ID
4. Add trading partners (payers)

#### 2. Change Healthcare

**Portal Setup**:
- Clearinghouse: **Change Healthcare**
- Sender ID: Assigned by Change Healthcare
- Receiver ID: `CHANGEHEALTHCARE`
- Connection Type: VAN or Direct
- Gateway ID: (provided by CHC)

**Change Healthcare Setup**:
1. Register at https://www.changehealthcare.com
2. Complete onboarding process
3. Receive Gateway credentials
4. Configure payer connections

#### 3. Waystar

**Portal Setup**:
- Clearinghouse: **Waystar**
- Sender ID: Your practice ID
- Receiver ID: `WAYSTAR`
- Environment:
  - Test: `sandbox.waystar.com`
  - Production: `provider.waystar.com`

#### 4. TriZetto

**Portal Setup**:
- Clearinghouse: **TriZetto**
- Sender ID: Your Gateway ID
- Receiver ID: `TRIZETTO`
- Portal: https://provider.trizetto.com

### Connection Test

After configuration, click **Test Connection** in portal:
- Sends test 270 eligibility inquiry
- Validates clearinghouse connectivity
- Confirms routing to payer
- Displays response time

### Trading Partner Setup

After clearinghouse connection, add payers:

**Portal** (Settings > Trading Partners > Add Partner):
1. Select Payer: Medicare, Medicaid, BCBS, Aetna, UHC, etc.
2. Enter Payer ID: (from clearinghouse documentation)
3. Configure:
   - Accept assignment: Yes/No
   - Electronic remittance: Yes/No
   - ERA enrollment ID: (if using ERA)
4. Click **Save**
5. Click **Test** to validate connection

### First Claim Timeline

1. **Configure clearinghouse**: 30 minutes
2. **Add trading partners**: 15 minutes
3. **Upload 834 enrollment**: 10 minutes (populates member database)
4. **Submit first 837 claim**: 5 minutes
5. **Receive 277 acknowledgment**: 2-5 minutes

**Total time to first claim: ~1 hour**

---

## Support

- **Documentation**: https://docs.cloudhealthoffice.com
- **Email**: support@cloudhealthoffice.com
- **Phone**: +1-800-555-HIPAA (business hours)
- **Community Forum**: https://community.cloudhealthoffice.com
- **Emergency**: support@cloudhealthoffice.com (24/7 monitoring)
