# Manual Customer Deployment Guide
## Cloud Health Office Platform - Phase 1 First Customer

This guide outlines the step-by-step process for manually deploying the Cloud Health Office Platform for the first paying customer.

---

## Table of Contents

1. [Pre-Deployment Checklist](#pre-deployment-checklist)
2. [Discovery Phase (Weeks 1-2)](#discovery-phase-weeks-1-2)
3. [Environment Setup (Weeks 3-4)](#environment-setup-weeks-3-4)
4. [Integration Development (Weeks 5-7)](#integration-development-weeks-5-7)
5. [Testing & Validation (Weeks 8-10)](#testing--validation-weeks-8-10)
6. [Production Deployment (Week 11)](#production-deployment-week-11)
7. [Training & Go-Live (Week 12)](#training--go-live-week-12)
8. [Post-Go-Live Support (Week 13+)](#post-go-live-support-week-13)

---

## Pre-Deployment Checklist

### Contract & Legal
- [ ] Master Services Agreement signed
- [ ] Business Associate Agreement (BAA) signed
- [ ] Statement of Work executed
- [ ] Security & compliance exhibits reviewed
- [ ] Payment received (50% implementation fee)

### Customer Information Gathered
- [ ] Customer organization name and contact info
- [ ] Payer ID for X12 transactions
- [ ] Availity SFTP credentials (host, username, password, folders)
- [ ] Claims system details (vendor, version, API documentation)
- [ ] Claims system API credentials (endpoint, auth method)
- [ ] Volume estimates (monthly attachments)
- [ ] Technical contacts (IT, compliance, operations)
- [ ] Training attendees list

### Azure Subscription Setup
- [ ] Azure subscription created for customer (or use shared environment)
- [ ] Resource group naming convention agreed upon
- [ ] Azure region selected (default: eastus)
- [ ] Cost tracking tags defined
- [ ] Billing alerts configured

### Tools & Access
- [ ] Azure CLI installed and authenticated
- [ ] PowerShell 7+ installed
- [ ] Git access to repository
- [ ] Access to customer's test environment
- [ ] VPN or network access if required
- [ ] Project management tool (e.g., Azure DevOps, Jira)

---

## Discovery Phase (Weeks 1-2)

### Week 1: Requirements Gathering

#### Day 1-2: Kickoff Meeting (2 hours)

**Attendees:** Customer stakeholders, project manager, technical lead, operations team

**Agenda:**
1. Introductions and roles
2. Project overview and timeline
3. Success criteria and acceptance criteria
4. Communication plan and escalation paths
5. Risk identification and mitigation

**Deliverables:**
- Project charter
- RACI matrix
- Communication plan
- Risk register

**Action Items:**
- Schedule weekly status calls (same day/time each week)
- Create shared project workspace (SharePoint, Teams, etc.)
- Distribute contact list and escalation procedures

#### Day 3-5: Technical Discovery

**Activities:**
1. **Availity SFTP Setup**
   - Test SFTP connection with provided credentials
   - Identify inbound folder structure
   - Document file naming conventions
   - Test file download and permissions

2. **Claims System API Discovery**
   - Review API documentation
   - Test API connectivity and authentication
   - Identify required endpoints:
     - Claim lookup/validation
     - Attachment upload/link
     - Status update (if applicable)
   - Document API rate limits and throttling
   - Map X12 fields to claims system fields

3. **Business Rules & Validation**
   - Define valid claim statuses for attachment processing
   - Identify required data fields (claim number, member ID, etc.)
   - Document error handling requirements
   - Define retry policies and failure scenarios

**Deliverables:**
- Technical specification document
- API integration design
- Field mapping spreadsheet
- Error handling flowchart

#### Week 2: Configuration Planning

**Activities:**
1. **Create Customer Configuration File**
   - Copy template from `config/templates/payer-config-template.json`
   - Fill in customer-specific values:
     ```json
     {
       "payerInfo": {
         "name": "Customer Health Plan",
         "payerId": "CUSTOMER001",
         "x12Id": "1234567890",
         "availitySenderId": "030240928"
       },
       "modules": {
         "attachments275": {
           "enabled": true,
           "sftpHost": "sftp.availity.net",
           "sftpUsername": "customer_user",
           "sftpInboundFolder": "/inbound/275",
           "pollingIntervalMinutes": 15
         }
       },
       "backendSystem": {
         "type": "QNXT",
         "apiEndpoint": "https://api.customer.com/claims",
         "authMethod": "OAuth2",
         "fieldMappings": {
           "claimNumber": "ST03",
           "memberId": "NM109",
           "providerNPI": "REF02"
         }
       }
     }
     ```

2. **Azure Infrastructure Planning**
   - Define resource naming: `customer-hipaa-[resource-type]`
   - Select Azure regions (primary + DR if required)
   - Size Logic Apps SKU (WS1 for production, B1 for dev/test)
   - Estimate Azure costs (typically $500-1000/month)

3. **Security & Compliance Planning**
   - Identify required security controls
   - Plan Key Vault secret structure
   - Define private endpoint requirements
   - Document PHI masking rules

**Deliverables:**
- Customer configuration file (`config/customers/customer-name.json`)
- Infrastructure design document
- Azure cost estimate
- Security implementation plan

---

## Environment Setup (Weeks 3-4)

### Week 3: Development Environment

#### Step 1: Create Azure Resources (DEV)

```bash
# Set variables
CUSTOMER_NAME="customername"
ENV="dev"
LOCATION="eastus"
RG_NAME="${CUSTOMER_NAME}-hipaa-${ENV}-rg"
BASE_NAME="${CUSTOMER_NAME}-hipaa-${ENV}"

# Create resource group
az group create \
  --name $RG_NAME \
  --location $LOCATION \
  --tags customer=$CUSTOMER_NAME environment=$ENV project=hipaa-attachments

# Deploy infrastructure using Bicep
cd /home/runner/work/hipaa-attachments/hipaa-attachments
az deployment group create \
  --resource-group $RG_NAME \
  --template-file infra/main.bicep \
  --parameters baseName=$BASE_NAME \
  --parameters location=$LOCATION
```

**Expected Resources:**
- Logic App Standard: `${BASE_NAME}-la`
- Storage Account (Data Lake): `${BASE_NAME}dlgen2`
- Service Bus Namespace: `${BASE_NAME}-svc`
- Service Bus Topics: `attachments-in`, `rfai-requests`, `edi-278`
- Application Insights: `${BASE_NAME}-ai`
- Key Vault: `${BASE_NAME}-kv`

#### Step 2: Configure Key Vault Secrets

```bash
# Get Key Vault name
KV_NAME="${BASE_NAME}-kv"

# Add SFTP credentials
az keyvault secret set \
  --vault-name $KV_NAME \
  --name "AvitySftpUsername" \
  --value "[customer-sftp-username]"

az keyvault secret set \
  --vault-name $KV_NAME \
  --name "AvilitySftpPassword" \
  --value "[customer-sftp-password]"

# Add claims system API credentials
az keyvault secret set \
  --vault-name $KV_NAME \
  --name "ClaimsSystemApiKey" \
  --value "[customer-api-key]"

# Add any other secrets as needed
```

#### Step 3: Grant Logic App Access to Key Vault

```bash
# Get Logic App managed identity principal ID
LA_NAME="${BASE_NAME}-la"
PRINCIPAL_ID=$(az webapp identity show \
  --resource-group $RG_NAME \
  --name $LA_NAME \
  --query principalId -o tsv)

# Grant Key Vault secrets user role
az keyvault set-policy \
  --name $KV_NAME \
  --object-id $PRINCIPAL_ID \
  --secret-permissions get list
```

#### Step 4: Configure API Connections

```bash
# Create SFTP connection
# (Manual step via Azure Portal for now - future: automate with ARM/Bicep)

# Navigate to: Azure Portal → API Connections → Create
# Connection Type: sftp-ssh
# Connection Name: availity-sftp
# Host: sftp.availity.net
# Username: @Microsoft.KeyVault(SecretUri=https://${KV_NAME}.vault.azure.net/secrets/AvilitySftpUsername)
# Password: @Microsoft.KeyVault(SecretUri=https://${KV_NAME}.vault.azure.net/secrets/AvilitySftpPassword)
```

### Week 4: Integration Account Setup

#### Step 1: Create Integration Account

```bash
# Create Integration Account
IA_NAME="${BASE_NAME}-ia"

az integration-account create \
  --resource-group $RG_NAME \
  --name $IA_NAME \
  --location $LOCATION \
  --sku Standard
```

#### Step 2: Upload X12 Schemas

```bash
# Upload 275 schema
az integration-account schema create \
  --resource-group $RG_NAME \
  --integration-account-name $IA_NAME \
  --name "X12_005010X210_275" \
  --file-path ./X12_005010X210_275.xsd \
  --schema-type Xml

# Upload 277 schema
az integration-account schema create \
  --resource-group $RG_NAME \
  --integration-account-name $IA_NAME \
  --name "X12_005010X212_277" \
  --file-path ./X12_005010X212_277.xsd \
  --schema-type Xml

# Upload 278 schema
az integration-account schema create \
  --resource-group $RG_NAME \
  --integration-account-name $IA_NAME \
  --name "X12_005010X217_278" \
  --file-path ./X12_005010X217_278.xsd \
  --schema-type Xml
```

#### Step 3: Configure Trading Partners

```powershell
# Use provided PowerShell script
pwsh -File ./configure-hipaa-trading-partners.ps1 `
  -ResourceGroup $RG_NAME `
  -IntegrationAccountName $IA_NAME `
  -PayerId "[customer-payer-id]" `
  -PayerQualifier "ZZ"
```

**Verify:**
- Partner 1: Availity (030240928)
- Partner 2: Customer Payer ([customer-payer-id])
- Agreement: Availity-to-Customer (receive 275)
- Agreement: Customer-to-Availity (send 277)

---

## Integration Development (Weeks 5-7)

### Week 5: Backend API Integration

#### Step 1: Build Claims System Connector

Create custom connector for customer's claims system:

```typescript
// Located in: scripts/templates/connectors/qnxt-connector.ts
// (Customize based on customer's specific API)

export class CustomerClaimsConnector {
  private apiEndpoint: string;
  private apiKey: string;

  async validateClaim(claimNumber: string): Promise<ClaimValidation> {
    // Call customer API to validate claim exists
    const response = await fetch(`${this.apiEndpoint}/claims/${claimNumber}`, {
      headers: {
        'Authorization': `Bearer ${this.apiKey}`,
        'Content-Type': 'application/json'
      }
    });
    
    if (!response.ok) {
      throw new Error(`Claim validation failed: ${response.status}`);
    }
    
    return response.json();
  }

  async linkAttachment(claimNumber: string, attachmentData: AttachmentMetadata): Promise<void> {
    // Call customer API to link attachment to claim
    const response = await fetch(`${this.apiEndpoint}/claims/${claimNumber}/attachments`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${this.apiKey}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(attachmentData)
    });
    
    if (!response.ok) {
      throw new Error(`Attachment linking failed: ${response.status}`);
    }
  }
}
```

#### Step 2: Update Logic App Workflows

Modify `logicapps/workflows/ingest275/workflow.json`:

1. **Update SFTP Trigger:**
   ```json
   {
     "inputs": {
       "host": {
         "connection": {
           "name": "@parameters('$connections')['availity-sftp']['connectionId']"
         }
       },
       "method": "get",
       "path": "/inbound/275",
       "queries": {
         "checkBothDirectories": false,
         "pollPeriod": 900
       }
     }
   }
   ```

2. **Update Claims API Action:**
   - Replace QNXT endpoint with customer endpoint
   - Update field mappings based on customer config
   - Add custom headers/authentication

3. **Update Service Bus Topic:**
   - Ensure topic name matches customer config
   - Add customer-specific metadata fields

#### Step 3: Deploy Workflows

```bash
# Package workflows
cd logicapps
zip -r ../workflows.zip workflows/

# Deploy to Logic App
az webapp deploy \
  --resource-group $RG_NAME \
  --name $LA_NAME \
  --src-path ../workflows.zip \
  --type zip

# Restart Logic App
az webapp restart \
  --resource-group $RG_NAME \
  --name $LA_NAME
```

### Week 6-7: Configuration & Refinement

**Activities:**
- Fine-tune retry policies based on customer SLAs
- Implement custom business rules (claim status validation, etc.)
- Configure Application Insights alerts
- Set up monitoring dashboards
- Document operational procedures

**Deliverables:**
- Deployed DEV environment
- Custom connector code
- Updated workflow definitions
- Operations runbook

---

## Testing & Validation (Weeks 8-10)

### Week 8: Unit & Integration Testing

#### Test Scenarios:

**Test 1: Happy Path - Valid 275 File**
```bash
# Upload test file to SFTP
sftp customer_user@sftp.availity.net
cd /inbound/275
put test-x12-275-availity-to-pchp.edi

# Expected Results:
# 1. File downloaded within 15 minutes
# 2. Archived to Data Lake: hipaa-attachments/raw/275/YYYY/MM/DD/
# 3. X12 decoded successfully
# 4. Claim validated in claims system
# 5. Attachment linked to claim
# 6. Event published to Service Bus
# 7. File deleted from SFTP
```

**Test 2: Invalid Claim Number**
```bash
# Upload test file with non-existent claim number
# Expected Results:
# 1. File downloaded and archived
# 2. X12 decoded successfully
# 3. Claim validation fails (404)
# 4. Error logged in Application Insights
# 5. Message sent to dead-letter queue
# 6. Alert triggered
```

**Test 3: Malformed EDI File**
```bash
# Upload corrupted EDI file
# Expected Results:
# 1. File downloaded and archived
# 2. X12 decode fails
# 3. Error logged with file details
# 4. Dead-letter queue message
# 5. Alert triggered
```

**Test 4: Claims System API Down**
```bash
# Simulate API outage
# Expected Results:
# 1. File processed through decode
# 2. API call fails with retry (4 attempts)
# 3. Final failure logged
# 4. Dead-letter queue message
# 5. Alert triggered
```

#### Week 9: User Acceptance Testing (UAT)

**UAT Test Plan:**

| Test Case | Description | Expected Result | Pass/Fail | Notes |
|-----------|-------------|-----------------|-----------|-------|
| UAT-01 | Process 10 real 275 files | All linked to claims correctly | | |
| UAT-02 | Verify Data Lake archival | Files stored in correct folders | | |
| UAT-03 | Validate claim data accuracy | All fields mapped correctly | | |
| UAT-04 | Test error handling | Errors logged and alerted | | |
| UAT-05 | Verify PHI masking | No PHI in Application Insights | | |
| UAT-06 | Test Service Bus events | Events published with correct data | | |
| UAT-07 | Validate audit trail | Complete audit log for all transactions | | |
| UAT-08 | Test monitoring dashboard | Real-time metrics displayed | | |

**UAT Participants:**
- Customer operations team (2-3 people)
- Customer IT team (1-2 people)
- Implementation team (1 person for support)

**UAT Duration:** 1 week

**UAT Sign-Off Criteria:**
- 95%+ test cases passed
- No critical or high-severity bugs
- Performance meets SLA (processing < 5 minutes)
- Customer approval documented

### Week 10: Performance & Security Testing

**Performance Testing:**
```bash
# Load test: Process 100 files simultaneously
# Expected: All processed within 2 hours, no failures

# Stress test: Process 500 files over 8 hours
# Expected: Consistent processing time, no degradation
```

**Security Testing:**
- Penetration test (engage third-party or use Azure Security Center)
- Vulnerability scan
- PHI masking validation
- Key Vault access audit
- Network security verification

**Deliverables:**
- UAT sign-off document
- Performance test results
- Security assessment report
- Bug fix log and resolution

---

## Production Deployment (Week 11)

### Production Environment Setup

#### Step 1: Create Production Resources

```bash
# Set production variables
ENV="prod"
RG_NAME="${CUSTOMER_NAME}-hipaa-${ENV}-rg"
BASE_NAME="${CUSTOMER_NAME}-hipaa-${ENV}"

# Deploy infrastructure (same steps as DEV, different parameters)
az group create --name $RG_NAME --location $LOCATION
az deployment group create \
  --resource-group $RG_NAME \
  --template-file infra/main.bicep \
  --parameters baseName=$BASE_NAME \
  --parameters location=$LOCATION \
  --parameters sku=WS1  # Production SKU
```

#### Step 2: Production Configuration

**Production-Specific Settings:**
- Enable private endpoints for all PHI resources
- Configure geo-redundant storage
- Enable Azure Backup for Logic Apps configuration
- Set up production monitoring and alerting
- Configure log retention (365 days for audit logs)

#### Step 3: Deploy to Production

```bash
# Deploy workflows (same as DEV)
az webapp deploy \
  --resource-group $RG_NAME \
  --name $LA_NAME \
  --src-path workflows.zip \
  --type zip

# Verify deployment
az webapp show \
  --resource-group $RG_NAME \
  --name $LA_NAME \
  --query state -o tsv  # Should show "Running"
```

#### Step 4: Production Smoke Test

```bash
# Upload single test file to production SFTP
# Monitor processing through Application Insights
# Verify successful completion
# Validate data in claims system
```

**Go-Live Checklist:**
- [ ] All production resources deployed
- [ ] Production workflows deployed and verified
- [ ] Key Vault secrets configured
- [ ] API connections authenticated
- [ ] Integration Account configured with prod partners
- [ ] Monitoring and alerting configured
- [ ] Backup and DR procedures documented
- [ ] Operations runbook reviewed
- [ ] Support escalation paths defined
- [ ] Customer sign-off received

---

## Training & Go-Live (Week 12)

### Training Sessions

#### Session 1: Operations Training (2 hours)

**Audience:** Operations staff who will monitor the platform

**Topics:**
1. Platform overview and architecture
2. How to monitor workflow runs (Azure Portal)
3. Application Insights dashboard walkthrough
4. How to handle errors and retries
5. When to escalate to support
6. Daily/weekly operational tasks

**Materials:**
- Operations runbook
- Monitoring dashboard guide
- Error handling flowchart
- Escalation procedures

#### Session 2: Technical Training (2 hours)

**Audience:** IT staff who will support the platform

**Topics:**
1. Azure infrastructure overview
2. Logic Apps workflow deep dive
3. Troubleshooting common issues
4. How to access logs and diagnostics
5. Security and compliance features
6. Backup and disaster recovery

**Materials:**
- Technical architecture document
- Troubleshooting guide
- Security documentation
- DR procedures

### Go-Live Preparation

**Day Before Go-Live:**
- [ ] Final smoke test in production
- [ ] All training completed
- [ ] Operations runbook distributed
- [ ] Support contacts shared with customer
- [ ] Availability confirmed for go-live support
- [ ] Rollback plan documented and reviewed

**Go-Live Day:**
- [ ] 8:00 AM: Go-live kickoff call
- [ ] 8:30 AM: Begin processing production files
- [ ] 9:00 AM: First successful processing confirmed
- [ ] 12:00 PM: Status check call
- [ ] 4:00 PM: End-of-day review call
- [ ] 5:00 PM: Day 1 summary email

**First Week:**
- Daily status calls (30 minutes)
- Real-time monitoring by implementation team
- 2-hour response time for any issues
- Daily metrics report to customer

---

## Post-Go-Live Support (Week 13+)

### Week 13-14: Intensive Support

**Activities:**
- Daily monitoring of all workflow runs
- Daily status calls with customer
- Proactive issue identification and resolution
- Performance tuning based on actual volume
- Documentation updates based on learnings

### Week 15-16: Transition to Steady State

**Activities:**
- Reduce status calls to weekly
- Transition monitoring to customer operations team
- Address any remaining issues or enhancements
- Collect customer feedback
- Plan for future optimizations

### Ongoing Support Model

**Monthly Activities:**
- Monthly metrics review (attachments processed, success rate, processing time)
- Quarterly business review (ROI, performance, roadmap)
- Platform updates and feature releases
- Security patches and compliance updates

**Support Channels:**
- Email: support@hipaa-attachments.com (24/7)
- Phone: 1-888-555-HIPAA (Monday-Friday, 8am-6pm ET)
- Critical issues: 2-hour response time
- Non-critical: 1 business day response time

---

## Key Success Metrics

### Deployment Success Criteria:
- ✅ On-time delivery (90 days or less)
- ✅ On-budget (within 10% of estimate)
- ✅ UAT sign-off achieved
- ✅ Production go-live successful
- ✅ Customer satisfaction score ≥ 4/5

### Operational Success Criteria (30 days post go-live):
- ✅ Processing time < 5 minutes per attachment (95th percentile)
- ✅ Success rate > 95%
- ✅ Uptime > 99.9%
- ✅ Zero security incidents
- ✅ Customer ROI tracking positive

### Customer Health Indicators:
- Platform utilization (% of attachments processed)
- Error rate trends
- Customer support ticket volume
- Customer satisfaction scores
- Renewal likelihood

---

## Lessons Learned Template

After go-live, document lessons learned:

**What Went Well:**
- [List successes and best practices]

**What Could Be Improved:**
- [List challenges and areas for improvement]

**Action Items for Future Deployments:**
- [List specific improvements to implement]

**Template Artifacts to Update:**
- [List templates, scripts, or docs to update based on learnings]

---

## Appendix

### A. Pre-Deployment Checklist (Printable)
[One-page checklist for project kickoff]

### B. Configuration Template
[Sample customer configuration file]

### C. Testing Scripts
[Automated test scripts for validation]

### D. Operations Runbook
[Day-to-day operational procedures]

### E. Troubleshooting Guide
[Common issues and resolutions]

### F. Customer Handoff Checklist
[Final deliverables and documentation]

---

## Support Contacts

**Implementation Team:**
- Project Manager: [Name] - [Email] - [Phone]
- Technical Lead: [Name] - [Email] - [Phone]
- Backend Integration: [Name] - [Email] - [Phone]

**Customer Success:**
- Customer Success Manager: [Name] - [Email] - [Phone]

**Technical Support:**
- Email: support@hipaa-attachments.com
- Phone: 1-888-555-HIPAA (4472)
- Portal: https://support.hipaa-attachments.com

---

*This deployment guide is a living document. Update based on lessons learned from each customer deployment.*
