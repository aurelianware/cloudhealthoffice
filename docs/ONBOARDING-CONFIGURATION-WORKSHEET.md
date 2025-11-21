# HIPAA Attachments Platform - Onboarding Configuration Worksheet

## Document Information

**Version:** 1.0  
**Last Updated:** 2025-11-21  
**Purpose:** Capture technical and business configuration for new prospect onboarding  
**Deadline:** Use this worksheet during manual onboarding sessions via Zoom/Google Meet

---

## Table of Contents

1. [Company Information](#1-company-information)
2. [Azure Environment Setup](#2-azure-environment-setup)
3. [Trading Partner Configuration](#3-trading-partner-configuration)
4. [Module Selection & Add-Ons](#4-module-selection--add-ons)
5. [Technical Configuration](#5-technical-configuration)
6. [Timeline & Milestones](#6-timeline--milestones)
7. [Success Criteria & KPIs](#7-success-criteria--kpis)
8. [Security & Compliance](#8-security--compliance)
9. [Support & Contacts](#9-support--contacts)
10. [Validation Checklist](#10-validation-checklist)
11. [Next Steps](#11-next-steps)

---

## 1. Company Information

### Organization Details

| Field | Response | Notes |
|-------|----------|-------|
| **Organization Name** | | Full legal name |
| **Payer Type** | ☐ Commercial ☐ Medicare ☐ Medicaid ☐ Managed Care | Select all that apply |
| **Organization Size** | ☐ Small (<100K members) ☐ Medium (100K-1M) ☐ Large (>1M) | Member count |
| **Service Area** | ☐ Single State ☐ Multi-State ☐ Nationwide | Geographic coverage |
| **State(s) Served** | | List all states |
| **Tax ID (EIN)** | | For contract/billing |
| **Website** | | Company website |
| **Physical Address** | | Headquarters address |

### Logo & Branding

| Field | Response | Notes |
|-------|----------|-------|
| **Logo URL** | | 234x60px PNG, <20KB |
| **Primary Brand Color** | | Hex code (e.g., #0066CC) |
| **Provider Portal Name** | | If custom portal branding needed |

### Current Systems

| System | In Use? | Version/Vendor | Integration Method |
|--------|---------|----------------|-------------------|
| **Core Admin System** | ☐ Yes ☐ No | | QNXT, HealthRules, etc. |
| **Claims Processing** | ☐ Yes ☐ No | | |
| **EDI Gateway** | ☐ Yes ☐ No | | |
| **Member Portal** | ☐ Yes ☐ No | | |
| **Provider Portal** | ☐ Yes ☐ No | | |
| **Document Management** | ☐ Yes ☐ No | | |

---

## 2. Azure Environment Setup

### Azure Subscription

| Field | Response | Notes |
|-------|----------|-------|
| **Azure Subscription ID** | | Existing or need new? |
| **Subscription Name** | | |
| **Tenant ID** | | Azure AD tenant |
| **Preferred Region** | ☐ East US ☐ West US ☐ Central US ☐ Other: _____ | Closest to operations |
| **Secondary Region** (DR) | ☐ Yes ☐ No | Region: _____ |

### Resource Naming Convention

| Field | Response | Notes |
|-------|----------|-------|
| **Base Name Prefix** | | e.g., "pchp", "bcbs-tx" |
| **Environment Suffixes** | DEV: _____ UAT: _____ PROD: _____ | Standard: dev, uat, prod |
| **Resource Group Pattern** | | e.g., {prefix}-{env}-rg |

### Azure Resources Required

Select the infrastructure tier:

- [ ] **Standard Tier** (Recommended for most)
  - Logic App Standard (WS1 SKU)
  - Storage Account (Standard_LRS, 1TB)
  - Service Bus (Standard tier)
  - Application Insights (Standard)
  - Integration Account (Basic tier)
  - **Estimated Monthly Cost:** $500-800

- [ ] **Premium Tier** (High-volume/HIPAA-critical)
  - Logic App Standard (WS2 SKU)
  - Storage Account (Standard_GRS, 5TB)
  - Service Bus (Premium tier)
  - Application Insights (Premium)
  - Integration Account (Standard tier)
  - Azure Key Vault (Premium HSM-backed)
  - Private Endpoints + VNet Integration
  - **Estimated Monthly Cost:** $2,000-3,000

- [ ] **Enterprise Tier** (Multi-region/High-availability)
  - All Premium features
  - Multi-region deployment
  - Customer-managed keys (CMK)
  - Dedicated VNets per environment
  - **Estimated Monthly Cost:** $5,000+

### Security Requirements

| Feature | Required? | Notes |
|---------|-----------|-------|
| **Azure Key Vault** | ☐ Yes ☐ No | HSM-backed for HIPAA |
| **Private Endpoints** | ☐ Yes ☐ No | VNet isolation |
| **Customer-Managed Keys (CMK)** | ☐ Yes ☐ No | BYOK encryption |
| **Multi-Factor Authentication** | ☐ Yes ☐ No | For Azure access |
| **Conditional Access Policies** | ☐ Yes ☐ No | IP restrictions, etc. |
| **Azure DDoS Protection** | ☐ Yes ☐ No | Standard or Basic |

---

## 3. Trading Partner Configuration

### Availity Integration

| Field | Response | Notes |
|-------|----------|-------|
| **Availity Customer ID** | | Primary ID |
| **Trading Partner ID** | | For EDI routing |
| **Availity Portal Access** | ☐ Yes ☐ No | Login credentials available? |
| **Environment** | ☐ Test ☐ Production | Start with test |

### X12 EDI Configuration

#### ISA Segment (Interchange Control)

| Field | Response | Notes |
|-------|----------|-------|
| **Sender ID (ISA06)** | | Your organization ID |
| **Sender Qualifier (ISA05)** | ☐ ZZ (Mutually Defined) ☐ 30 (Tax ID) | Standard: ZZ |
| **Receiver ID (ISA08)** | | Availity ID: 030240928 |
| **Receiver Qualifier (ISA07)** | ☐ ZZ ☐ 30 | Standard: ZZ |
| **Interchange Version (ISA12)** | | Standard: 00501 (5010) |

#### GS Segment (Functional Group)

| Field | Response | Notes |
|-------|----------|-------|
| **Application Sender Code (GS02)** | | Usually same as ISA06 |
| **Application Receiver Code (GS03)** | | Usually same as ISA08 |
| **Version/Release (GS08)** | | Standard: 005010 |

### SFTP Configuration

#### Inbound (From Availity to Your System)

| Field | Response | Notes |
|-------|----------|-------|
| **SFTP Hostname** | | Provided by Availity |
| **Port** | | Standard: 22 |
| **Username** | | |
| **Authentication Method** | ☐ SSH Key ☐ Password | SSH key recommended |
| **Inbound Folder** | | e.g., /inbound/attachments |
| **File Naming Pattern** | | e.g., 275_*.edi |
| **Polling Frequency** | | e.g., Every 5 minutes |

#### Outbound (From Your System to Availity)

| Field | Response | Notes |
|-------|----------|-------|
| **SFTP Hostname** | | May be same as inbound |
| **Port** | | Standard: 22 |
| **Username** | | May be same or different |
| **Authentication Method** | ☐ SSH Key ☐ Password | SSH key recommended |
| **Outbound Folder** | | e.g., /outbound/rfai |
| **File Naming Pattern** | | e.g., 277_*.edi |

---

## 4. Module Selection & Add-Ons

### Core Modules

Select all modules to enable:

#### ✅ **HIPAA 275 Attachments** (Included)
- [ ] Enabled
- **Description:** Process clinical and administrative attachments from Availity SFTP, archive to Data Lake, update core system
- **Estimated Volume:** _____ files/month
- **Peak Volume:** _____ files/day
- **Average File Size:** _____ KB/MB

#### ☑️ **HIPAA 277 RFAI (Request for Additional Information)**
- [ ] Enabled
- **Description:** Generate and send 277 RFAI responses back to Availity via SFTP
- **Estimated Volume:** _____ requests/month
- **Turnaround Time Required:** _____ hours/days

#### ☑️ **HIPAA 278 Health Care Services Review**
- [ ] Enabled
- **Description:** Process 278 authorization requests and responses
- **Estimated Volume:** _____ transactions/month
- **Use Cases:** ☐ Prior Auth ☐ Referrals ☐ Admission Review

#### ☑️ **278 Replay Endpoint**
- [ ] Enabled
- **Description:** HTTP endpoint for deterministic replay of 278 transactions for debugging
- **Authentication:** ☐ Azure AD ☐ API Key
- **Access Control:** Who needs access? _____

### Value-Added Modules

#### 📊 **Appeals Processing**
- [ ] Enabled
- **Description:** Automated appeals detection, registration, and tracking
- **Core Admin API Endpoint:** _____________________
- **Authentication Type:** ☐ OAuth ☐ API Key ☐ Certificate
- **Estimated Appeals Volume:** _____ /month

**Appeals Configuration:**
| Feature | Enabled? | Details |
|---------|----------|---------|
| Auto-detect appeals from attachments | ☐ Yes ☐ No | Parse 275 for appeal indicators |
| Register with core system | ☐ Yes ☐ No | API endpoint: _____ |
| Member notification | ☐ Yes ☐ No | Email/SMS configuration |
| Timely filing calculation | ☐ Yes ☐ No | State-specific rules |

#### 📈 **Enhanced Claim Status (ECS)**
- [ ] Enabled (Standard)
- [ ] Enabled (Plus with ValueAdds277 - +$10,000/year)
- **Description:** Advanced claim status with extended data and cross-module integration
- **Core Admin API Endpoint:** _____________________

**ECS Configuration:**
| Feature | Standard | Plus | Selected |
|---------|----------|------|----------|
| Basic claim status | ✅ | ✅ | |
| Financial summary (3-4 fields) | ✅ | ✅ | |
| Complete financials (8+ fields) | ❌ | ✅ | ☐ |
| Clinical info (DRG, diagnosis) | ❌ | ✅ | ☐ |
| Demographics (20+ fields) | ❌ | ✅ | ☐ |
| Remittance tracking | Limited | ✅ | ☐ |
| Service line details (10+ fields) | ❌ | ✅ | ☐ |
| Integration flags (6 modules) | ❌ | ✅ | ☐ |

**Query Methods Enabled:**
- [ ] Service Date Range
- [ ] Member ID + DOS
- [ ] Check Number
- [ ] Claim History (all claims for member)

#### 🏥 **Authorization Management (278)**
- [ ] Enabled
- **Description:** Process authorization requests, generate responses, track approvals/denials
- **Core Admin API Endpoint:** _____________________
- **Estimated Volume:** _____ authorizations/month

**Authorization Types:**
- [ ] Prior Authorization
- [ ] Referrals
- [ ] Admission Review
- [ ] Concurrent Review
- [ ] Retrospective Review

#### 🔍 **Authorization Inquiry (X12 215)**
- [ ] Enabled
- **Description:** Query authorization status by authorization number or member/DOS
- **Core Admin API Endpoint:** _____________________

#### ✔️ **270/271 Eligibility Verification**
- [ ] Enabled
- **Description:** Real-time eligibility verification and benefit inquiries
- **Integration Type:** ☐ Real-time Web ☐ B2B Batch ☐ EDI File
- **Core Admin API Endpoint:** _____________________
- **Response Time SLA:** _____ seconds

#### 📝 **276/277 Claim Status**
- [ ] Enabled
- **Description:** Claim status inquiries and responses
- **Integration Type:** ☐ Real-time ☐ Batch
- **Core Admin API Endpoint:** _____________________

#### 💬 **Secure Messaging**
- [ ] Enabled
- **Description:** Secure provider-payer communication
- **Message Storage:** ☐ Azure Blob ☐ External System
- **Retention Period:** _____ days/years

#### 🔄 **Claim Corrections**
- [ ] Enabled
- **Description:** Automated claim correction workflows
- **Core Admin API Endpoint:** _____________________

### Custom Requirements

**Do you have any custom integration requirements not covered above?**

| Requirement | Description | Priority | Estimated Effort |
|-------------|-------------|----------|------------------|
| 1. | | ☐ High ☐ Medium ☐ Low | |
| 2. | | ☐ High ☐ Medium ☐ Low | |
| 3. | | ☐ High ☐ Medium ☐ Low | |

---

## 5. Technical Configuration

### Core Administration System Integration

| Field | Response | Notes |
|-------|----------|-------|
| **System Name** | ☐ QNXT ☐ HealthRules ☐ Custom ☐ Other: _____ | |
| **Version** | | |
| **API Type** | ☐ REST ☐ SOAP ☐ GraphQL ☐ Other: _____ | |
| **Base URL (Test)** | | |
| **Base URL (Production)** | | |
| **Authentication Method** | ☐ OAuth 2.0 ☐ API Key ☐ Certificate ☐ Basic Auth | |
| **Rate Limits** | | Requests per second/minute |
| **Timeout Settings** | | Recommended timeout in seconds |

### Authentication & Security

#### OAuth 2.0 Configuration (if applicable)

| Field | Response | Notes |
|-------|----------|-------|
| **Grant Type** | ☐ Client Credentials ☐ Authorization Code ☐ Other | |
| **Token Endpoint** | | |
| **Client ID** | | Store in Key Vault |
| **Client Secret** | | Store in Key Vault |
| **Scope** | | Required OAuth scopes |
| **Token Lifetime** | | In seconds |

#### API Key Configuration (if applicable)

| Field | Response | Notes |
|-------|----------|-------|
| **Header Name** | | e.g., "X-API-Key" |
| **API Key** | | Store in Key Vault |
| **Key Rotation Schedule** | | e.g., Every 90 days |

### Data Lake Storage Configuration

| Field | Response | Notes |
|-------|----------|-------|
| **Storage Account Name** | | Azure naming rules apply |
| **Container Name** | | e.g., hipaa-attachments |
| **Folder Structure** | | Default: {type}/yyyy/MM/dd/ |
| **Retention Policy** | | HIPAA: 7 years minimum |
| **Lifecycle Management** | | ☐ Hot→Cool(30d)→Archive(90d) |
| **Soft Delete Enabled** | ☐ Yes ☐ No | Retention: _____ days |
| **Blob Versioning** | ☐ Yes ☐ No | For audit trail |

### Service Bus Configuration

| Field | Response | Notes |
|-------|----------|-------|
| **Namespace Name** | | Azure naming rules apply |
| **Topic: attachments-in** | ☐ Enabled | Processed 275 events |
| **Topic: rfai-requests** | ☐ Enabled | 277 RFAI requests |
| **Topic: edi-278** | ☐ Enabled | 278 transactions |
| **Message TTL** | | Default: 14 days |
| **Max Message Size** | | Standard: 256KB, Premium: 1MB |
| **Dead Letter Queue** | ☐ Enabled | For failed messages |

### Integration Account Configuration

| Field | Response | Notes |
|-------|----------|-------|
| **Integration Account Name** | | Azure naming rules apply |
| **SKU** | ☐ Basic ☐ Standard | Standard for production |
| **X12 Schemas** | ☐ 275 ☐ 277 ☐ 278 ☐ 270/271 ☐ 276/277 | Select all needed |

### Monitoring & Alerting

#### Application Insights

| Field | Response | Notes |
|-------|----------|-------|
| **Workspace Name** | | Log Analytics workspace |
| **Retention Period** | | HIPAA: 365 days minimum |
| **Daily Cap** | | GB/day, or unlimited |
| **Sampling Rate** | | 100% for HIPAA |

#### Alert Rules

| Alert | Enabled? | Threshold | Recipients |
|-------|----------|-----------|------------|
| Workflow failures | ☐ Yes | >5 in 15min | |
| SFTP connection failures | ☐ Yes | >3 consecutive | |
| API timeouts | ☐ Yes | >10% in 5min | |
| Storage quota | ☐ Yes | >80% capacity | |
| Service Bus dead letters | ☐ Yes | >10 messages | |
| QNXT API errors | ☐ Yes | >5 in 15min | |
| High latency | ☐ Yes | >30s p95 | |

---

## 6. Timeline & Milestones

### Project Timeline

| Phase | Start Date | End Date | Duration | Owner |
|-------|------------|----------|----------|-------|
| **Kickoff & Requirements** | | | | |
| **Azure Environment Setup** | | | | |
| **Configuration Development** | | | | |
| **DEV Deployment** | | | | |
| **Integration Testing** | | | | |
| **UAT Deployment** | | | | |
| **User Acceptance Testing** | | | | |
| **Production Deployment** | | | | |
| **Hypercare (2 weeks)** | | | | |
| **Go-Live** | | | | |

### Key Milestones

| Milestone | Target Date | Status | Dependencies |
|-----------|-------------|--------|--------------|
| Azure subscription provisioned | | ☐ Not Started ☐ In Progress ☐ Complete | |
| Service principal/OIDC configured | | ☐ Not Started ☐ In Progress ☐ Complete | Azure subscription |
| Configuration file validated | | ☐ Not Started ☐ In Progress ☐ Complete | Requirements gathered |
| DEV infrastructure deployed | | ☐ Not Started ☐ In Progress ☐ Complete | OIDC configured |
| SFTP connectivity confirmed | | ☐ Not Started ☐ In Progress ☐ Complete | Availity credentials |
| Test 275 file processed | | ☐ Not Started ☐ In Progress ☐ Complete | DEV deployed |
| Core admin API integration tested | | ☐ Not Started ☐ In Progress ☐ Complete | DEV deployed |
| UAT environment deployed | | ☐ Not Started ☐ In Progress ☐ Complete | DEV testing complete |
| UAT sign-off received | | ☐ Not Started ☐ In Progress ☐ Complete | UAT testing complete |
| PROD environment deployed | | ☐ Not Started ☐ In Progress ☐ Complete | UAT sign-off |
| Go-Live | | ☐ Not Started ☐ In Progress ☐ Complete | PROD deployed |

### Critical Path Items

**Items that could delay the project:**

1. ☐ Azure subscription approval process
2. ☐ Availity trading partner setup
3. ☐ Core admin system API availability
4. ☐ Security/compliance review
5. ☐ Testing environment access
6. ☐ Production deployment approval
7. ☐ Other: _____________________

---

## 7. Success Criteria & KPIs

### Technical Success Criteria

| Criterion | Target | Measurement Method | Priority |
|-----------|--------|-------------------|----------|
| **System Uptime** | 99.9% | Application Insights | High |
| **File Processing Success Rate** | >99% | Workflow run history | High |
| **Average Processing Time** | <5 minutes | End-to-end latency | High |
| **API Response Time (p95)** | <3 seconds | Application Insights | Medium |
| **Failed Messages** | <1% | Service Bus metrics | High |
| **Storage Growth** | Within budget | Storage metrics | Medium |
| **Zero Data Loss** | 100% | Audit logs | High |

### Business Success Criteria

| Criterion | Target | Measurement Method | Priority |
|-----------|--------|-------------------|----------|
| **Provider Satisfaction** | >4.5/5 | Survey after 90 days | High |
| **Call Center Volume Reduction** | >25% | Call center reports | Medium |
| **Appeals Processing Time** | <10 days | Workflow metrics | High |
| **Attachment Processing Time** | <24 hours | SLA tracking | High |
| **Error Rate** | <2% | Error logs | High |
| **Training Completion** | 100% staff | Training records | Medium |

### Volume Projections

| Metric | Month 1 | Month 3 | Month 6 | Month 12 |
|--------|---------|---------|---------|----------|
| **275 Files Processed** | | | | |
| **277 RFAI Generated** | | | | |
| **278 Transactions** | | | | |
| **Appeals Registered** | | | | |
| **ECS Queries** | | | | |
| **Storage Used (GB)** | | | | |
| **API Calls/Day** | | | | |

### ROI Metrics

| Metric | Current State | Target State | Estimated Savings |
|--------|---------------|--------------|-------------------|
| **Manual Processing Time** | _____ hrs/month | _____ hrs/month | $_____ /month |
| **Call Center Volume** | _____ calls/month | _____ calls/month | $_____ /month |
| **Appeals Turnaround** | _____ days | _____ days | $_____ /month |
| **Staff Productivity** | _____ claims/day | _____ claims/day | $_____ /month |
| **Error Correction Costs** | $_____ /month | $_____ /month | $_____ /month |
| **Total Monthly Savings** | | | **$_____ /month** |

---

## 8. Security & Compliance

### HIPAA Compliance Requirements

| Requirement | Implemented? | Details |
|-------------|--------------|---------|
| **Encryption at Rest** | ☐ Yes ☐ N/A | Azure Storage Service Encryption |
| **Encryption in Transit** | ☐ Yes ☐ N/A | TLS 1.2+ required |
| **Access Logging** | ☐ Yes ☐ N/A | 365-day retention |
| **PHI Masking in Logs** | ☐ Yes ☐ N/A | Application Insights DCR |
| **Multi-Factor Authentication** | ☐ Yes ☐ N/A | Azure AD MFA |
| **Role-Based Access Control** | ☐ Yes ☐ N/A | RBAC configured |
| **Data Retention Policy** | ☐ Yes ☐ N/A | 7-year minimum |
| **Breach Notification Plan** | ☐ Yes ☐ N/A | 60-day timeline |
| **Business Associate Agreement** | ☐ Yes ☐ N/A | Signed BAA required |
| **Security Risk Assessment** | ☐ Yes ☐ N/A | Annual requirement |

### Compliance Documentation

| Document | Status | Last Updated | Next Review |
|----------|--------|--------------|-------------|
| **HIPAA Security Risk Assessment** | ☐ Complete ☐ In Progress ☐ Not Started | | |
| **Business Associate Agreement** | ☐ Complete ☐ In Progress ☐ Not Started | | |
| **Privacy Impact Assessment** | ☐ Complete ☐ In Progress ☐ Not Started | | |
| **Incident Response Plan** | ☐ Complete ☐ In Progress ☐ Not Started | | |
| **Disaster Recovery Plan** | ☐ Complete ☐ In Progress ☐ Not Started | | |
| **Change Management Procedures** | ☐ Complete ☐ In Progress ☐ Not Started | | |

### Audit Requirements

| Audit Type | Frequency | Last Audit | Next Audit | Auditor |
|------------|-----------|------------|------------|---------|
| **HIPAA Security** | Annual | | | |
| **SOC 2 Type II** | Annual | | | |
| **Internal IT Audit** | Quarterly | | | |
| **Vulnerability Scan** | Monthly | | | |
| **Penetration Testing** | Annual | | | |

---

## 9. Support & Contacts

### Primary Contacts

#### Customer Organization

| Role | Name | Email | Phone | Availability |
|------|------|-------|-------|--------------|
| **Executive Sponsor** | | | | |
| **Project Manager** | | | | |
| **Technical Lead** | | | | |
| **Security Officer** | | | | |
| **Compliance Officer** | | | | |
| **Account Manager** | | | | |
| **Escalation Contact** | | | | |

#### Implementation Team

| Role | Name | Email | Phone | Responsibilities |
|------|------|-------|-------|-----------------|
| **Implementation Lead** | | | | Overall delivery |
| **Solution Architect** | | | | Technical design |
| **DevOps Engineer** | | | | Azure deployment |
| **Integration Specialist** | | | | API integration |
| **QA Lead** | | | | Testing strategy |

### Support Model

#### Support Tiers

**During Implementation (Weeks 1-12):**
- **Hours:** Business hours (9am-5pm ET)
- **Response Time:** <4 hours for critical issues
- **Channels:** Email, Slack, Zoom

**Post Go-Live Hypercare (Weeks 13-14):**
- **Hours:** Extended (7am-7pm ET)
- **Response Time:** <2 hours for critical issues
- **Channels:** Email, Slack, Phone, Zoom

**Ongoing Support (After Week 14):**
- [ ] **Standard Support** (Business hours, <8 hour response) - Included
- [ ] **Premium Support** (24/7, <1 hour response) - +$5,000/month
- [ ] **Dedicated Support Engineer** - +$15,000/month

### Communication Plan

| Communication Type | Frequency | Attendees | Format |
|-------------------|-----------|-----------|--------|
| **Kickoff Meeting** | Once | All stakeholders | Zoom (2 hours) |
| **Weekly Status** | Weekly | PM, Tech Lead | Zoom (30 min) |
| **Technical Deep Dive** | As needed | Tech team | Zoom (1 hour) |
| **UAT Review** | Weekly during UAT | QA, Business users | Zoom (1 hour) |
| **Go-Live Planning** | Week before | All stakeholders | Zoom (1 hour) |
| **Post-Implementation Review** | 30 days after Go-Live | All stakeholders | Zoom (1 hour) |

---

## 10. Validation Checklist

### Pre-Deployment Validation

**Before deploying to any environment, verify:**

- [ ] All sections of this worksheet are complete
- [ ] Azure subscription is provisioned and accessible
- [ ] Service principal/OIDC federated credentials are configured
- [ ] GitHub Secrets are configured for target environment
- [ ] Availity trading partner credentials are available
- [ ] Core admin system API endpoints are accessible
- [ ] X12 EDI configuration is validated
- [ ] SFTP connectivity is tested
- [ ] Integration Account schemas are uploaded
- [ ] Key Vault is provisioned (if required)
- [ ] Private endpoints are configured (if required)
- [ ] Security requirements are reviewed and approved
- [ ] HIPAA compliance requirements are documented
- [ ] Business Associate Agreement is signed
- [ ] Project timeline is agreed upon by all parties
- [ ] Success criteria and KPIs are defined and measurable
- [ ] Support model and contacts are finalized
- [ ] Testing strategy is documented
- [ ] Rollback procedures are defined
- [ ] Disaster recovery plan is documented

### Configuration File Validation

**After creating the payer configuration JSON:**

- [ ] Configuration file follows the schema: `config/schemas/availity-integration-config.schema.json`
- [ ] All required fields are populated
- [ ] Module configurations match selections in this worksheet
- [ ] API endpoints are correct for test and production environments
- [ ] Authentication credentials reference Key Vault secrets
- [ ] SFTP settings match Availity documentation
- [ ] X12 identifiers are correctly formatted
- [ ] Trading partner configuration is complete
- [ ] Infrastructure settings match selected tier
- [ ] Monitoring and alerting are configured
- [ ] Configuration passes JSON schema validation: `node dist/scripts/cli/payer-generator-cli.js validate <config-file>`

### Testing Validation

**Before UAT sign-off:**

- [ ] DEV environment is fully functional
- [ ] Sample 275 file processes successfully end-to-end
- [ ] Core admin system API integration is working
- [ ] Service Bus messages are being published correctly
- [ ] Data Lake storage is archiving files properly
- [ ] Application Insights is capturing telemetry
- [ ] Alert rules are functioning correctly
- [ ] SFTP connectivity is stable
- [ ] X12 decoding is successful
- [ ] Error handling scenarios are tested
- [ ] Performance meets SLA requirements
- [ ] Security controls are validated
- [ ] PHI masking is working in logs
- [ ] Backup and restore procedures are tested

### Production Readiness Validation

**Before PROD deployment:**

- [ ] UAT testing is complete and signed off
- [ ] All critical defects are resolved
- [ ] Performance testing validates volume projections
- [ ] Security audit is complete
- [ ] Disaster recovery procedures are documented and tested
- [ ] Runbook for operations team is complete
- [ ] Training for support staff is complete
- [ ] Monitoring dashboards are configured
- [ ] On-call rotation is defined
- [ ] Incident escalation procedures are documented
- [ ] Change management approval is obtained
- [ ] Rollback plan is ready
- [ ] Go-Live checklist is prepared
- [ ] Communication plan for Go-Live is distributed

---

## 11. Next Steps

### Immediate Actions (Week 1)

1. **Complete This Worksheet**
   - [ ] Fill out all sections during onboarding call
   - [ ] Identify any missing information
   - [ ] Schedule follow-up for outstanding items

2. **Azure Provisioning**
   - [ ] Create Azure subscription (if needed)
   - [ ] Set up Azure AD tenant
   - [ ] Configure OIDC federated credentials
   - [ ] Create service principals for DEV/UAT/PROD

3. **Availity Setup**
   - [ ] Confirm trading partner agreement
   - [ ] Obtain SFTP credentials
   - [ ] Request test environment access
   - [ ] Download sample EDI files

4. **Configuration File Creation**
   - [ ] Generate configuration template: `node dist/scripts/cli/payer-generator-cli.js template -o <payer-id>-config.json`
   - [ ] Populate with information from this worksheet
   - [ ] Validate configuration: `node dist/scripts/cli/payer-generator-cli.js validate <payer-id>-config.json`

### Short-Term Actions (Weeks 2-4)

5. **DEV Environment Deployment**
   - [ ] Generate deployment package: `node dist/scripts/cli/payer-generator-cli.js generate -c <payer-id>-config.json`
   - [ ] Deploy infrastructure via Bicep
   - [ ] Deploy Logic App workflows
   - [ ] Configure API connections
   - [ ] Upload Integration Account schemas

6. **Integration Testing**
   - [ ] Test SFTP connectivity
   - [ ] Process sample 275 file
   - [ ] Validate core admin API integration
   - [ ] Test Service Bus messaging
   - [ ] Verify Data Lake archiving
   - [ ] Review Application Insights telemetry

7. **UAT Planning**
   - [ ] Define test scenarios
   - [ ] Prepare test data
   - [ ] Identify UAT participants
   - [ ] Schedule UAT sessions
   - [ ] Create UAT sign-off template

### Medium-Term Actions (Weeks 5-8)

8. **UAT Execution**
   - [ ] Deploy UAT environment
   - [ ] Execute test scenarios
   - [ ] Log and track defects
   - [ ] Retest fixed defects
   - [ ] Obtain UAT sign-off

9. **Production Preparation**
   - [ ] Final security review
   - [ ] Performance testing
   - [ ] Disaster recovery testing
   - [ ] Operations training
   - [ ] Runbook creation
   - [ ] Go-Live planning

### Long-Term Actions (Weeks 9-12)

10. **Production Deployment**
    - [ ] Deploy PROD environment
    - [ ] Production smoke testing
    - [ ] Monitor closely for first 48 hours
    - [ ] Hypercare support (2 weeks)
    - [ ] Post-implementation review
    - [ ] Transition to ongoing support

### Dependencies and Blockers

**Track items that could delay the project:**

| Item | Owner | Due Date | Status | Blocker? |
|------|-------|----------|--------|----------|
| Azure subscription approval | | | | ☐ Yes ☐ No |
| Availity trading partner setup | | | | ☐ Yes ☐ No |
| Core admin API access | | | | ☐ Yes ☐ No |
| Security review | | | | ☐ Yes ☐ No |
| BAA signing | | | | ☐ Yes ☐ No |
| | | | | ☐ Yes ☐ No |
| | | | | ☐ Yes ☐ No |

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-11-21 | Copilot Agent | Initial creation - Comprehensive onboarding worksheet |

---

## Appendix A: Reference Documentation

### Key Documentation Links

1. **[README.md](../README.md)** - System overview and architecture
2. **[ARCHITECTURE.md](../ARCHITECTURE.md)** - Detailed architecture and data flows
3. **[DEPLOYMENT.md](../DEPLOYMENT.md)** - Step-by-step deployment procedures
4. **[CONTRIBUTING.md](../CONTRIBUTING.md)** - Development workflow and setup
5. **[SECURITY.md](../SECURITY.md)** - HIPAA compliance and security practices
6. **[TROUBLESHOOTING.md](../TROUBLESHOOTING.md)** - Common issues and solutions
7. **[UNIFIED-CONFIG-SCHEMA.md](UNIFIED-CONFIG-SCHEMA.md)** - Complete configuration schema reference
8. **[CONFIG-TO-WORKFLOW-GENERATOR.md](CONFIG-TO-WORKFLOW-GENERATOR.md)** - Code generation documentation
9. **[GITHUB-ACTIONS-SETUP.md](../GITHUB-ACTIONS-SETUP.md)** - CI/CD pipeline setup
10. **[DEPLOYMENT-SECRETS-SETUP.md](../DEPLOYMENT-SECRETS-SETUP.md)** - Secrets and environment configuration

### Configuration Schema

- **Schema File:** `config/schemas/availity-integration-config.schema.json`
- **Validation Tool:** `node dist/scripts/cli/payer-generator-cli.js validate <config-file>`

### Sample Configurations

**Coming Soon:** Example configuration files for common payer types:
- Commercial payer (e.g., Blue Cross Blue Shield)
- Medicaid MCO (e.g., State Medicaid)
- Medicare Advantage (e.g., Medicare managed care)

### Templates

**Coming Soon:** Additional templates for:
- Test plan template
- UAT sign-off template
- Go-Live checklist
- Operations runbook template
- Incident response template

---

## Appendix B: Glossary

| Term | Definition |
|------|------------|
| **275** | HIPAA X12 transaction for Additional Information to Support a Health Care Claim or Encounter |
| **277** | HIPAA X12 transaction for Health Care Claim Status Notification (including RFAI) |
| **278** | HIPAA X12 transaction for Health Care Services Review Information (Prior Authorization) |
| **Availity** | Healthcare EDI clearinghouse and portal provider |
| **BAA** | Business Associate Agreement (HIPAA requirement) |
| **CMK** | Customer-Managed Keys (encryption keys controlled by customer) |
| **ECS** | Enhanced Claim Status (Availity's advanced claim status product) |
| **EDI** | Electronic Data Interchange |
| **GS Segment** | Functional Group Header in X12 EDI |
| **ISA Segment** | Interchange Control Header in X12 EDI |
| **Integration Account** | Azure Logic Apps component for B2B messaging and EDI processing |
| **Logic App Standard** | Azure serverless workflow automation platform (new model) |
| **OIDC** | OpenID Connect (modern authentication protocol) |
| **PHI** | Protected Health Information (HIPAA-regulated data) |
| **QNXT** | Core administration system used by many health plans |
| **RFAI** | Request for Additional Information (via 277 transaction) |
| **Service Bus** | Azure message broker for reliable asynchronous messaging |
| **SFTP** | Secure File Transfer Protocol |
| **Trading Partner** | Organization you exchange EDI transactions with |
| **UAT** | User Acceptance Testing |
| **ValueAdds277** | Availity's enhanced 277 response with 60+ additional fields |
| **VNet** | Virtual Network (Azure network isolation) |
| **X12** | EDI standard used in US healthcare |

---

## Appendix C: Cost Estimation

### Standard Tier Monthly Costs

| Resource | SKU/Tier | Estimated Cost | Notes |
|----------|----------|----------------|-------|
| Logic App Standard | WS1 (1 vCPU, 3.5GB RAM) | $175 | Includes 1M executions |
| Storage Account | Standard_LRS, 1TB | $20 | Data Lake Gen2 |
| Service Bus | Standard, 1M messages | $10 | Topics/subscriptions |
| Application Insights | Standard, 5GB/day | $150 | 365-day retention |
| Integration Account | Basic | $100 | X12 processing |
| Key Vault | Standard | $5 | Secret storage |
| **Total Standard Tier** | | **~$460/month** | |

### Premium Tier Monthly Costs

| Resource | SKU/Tier | Estimated Cost | Notes |
|----------|----------|----------------|-------|
| Logic App Standard | WS2 (2 vCPU, 7GB RAM) | $350 | High performance |
| Storage Account | Standard_GRS, 5TB | $200 | Geo-redundant |
| Service Bus | Premium, 1 messaging unit | $667 | Dedicated resources |
| Application Insights | Premium, 10GB/day | $300 | Enhanced analytics |
| Integration Account | Standard | $800 | Advanced B2B |
| Key Vault | Premium (HSM-backed) | $500 | FIPS 140-2 Level 2 |
| Private Endpoints | 3 endpoints | $22 | VNet isolation |
| VNet Integration | Logic App + Storage | $50 | Network security |
| **Total Premium Tier** | | **~$2,889/month** | |

### Volume-Based Cost Adjustments

**Additional costs based on transaction volume:**

| Volume Tier | Executions/Month | Additional Cost | Notes |
|-------------|------------------|-----------------|-------|
| Low | <100K | Included | Base pricing |
| Medium | 100K-1M | +$50 | Additional workflow runs |
| High | 1M-10M | +$200 | Scale out required |
| Very High | >10M | +$500+ | Multi-instance deployment |

**Storage costs scale with volume:**
- 1TB = $20/month (Standard) or $50/month (Premium)
- 5TB = $100/month (Standard) or $250/month (Premium)
- 10TB+ = Custom pricing

---

## Appendix D: Integration Patterns

### Common Integration Patterns

#### Pattern 1: SFTP Polling + Data Lake Archive + API Push
**Use Case:** 275 Attachments, 278 Authorizations
```
Availity SFTP → Logic App → Data Lake → X12 Decode → Extract Metadata → 
Core Admin API → Service Bus → Downstream Workflows
```

#### Pattern 2: Service Bus Subscribe + Generate EDI + SFTP Send
**Use Case:** 277 RFAI
```
Core Admin System → Service Bus → Logic App → Generate X12 → 
Availity SFTP Outbound → Archive to Data Lake
```

#### Pattern 3: HTTP API + Real-Time Response
**Use Case:** 270/271 Eligibility, 276/277 Claim Status
```
Provider Portal → HTTP POST → Logic App → Core Admin API → 
Transform Response → HTTP Response
```

#### Pattern 4: Event-Driven Processing
**Use Case:** Appeals, Cross-Module Integration
```
Attachment Event → Service Bus → Logic App → Detect Appeal → 
Register Appeal → Update Claim → Notify Member
```

### Error Handling Patterns

**Retry Policy:**
```json
{
  "type": "exponential",
  "count": 4,
  "interval": "PT15S",
  "maximumInterval": "PT1H",
  "minimumInterval": "PT5S"
}
```

**Dead Letter Queue:**
- Failed messages route to DLQ after max retries
- Alert sent to operations team
- Manual review and replay

---

**END OF WORKSHEET**

---

**Instructions for Use:**

1. **Before the Call:** Send this worksheet to the prospect 2-3 days in advance
2. **During the Call:** Share screen and fill out collaboratively via Zoom/Google Meet
3. **After the Call:** 
   - Convert to configuration JSON using the generator tool
   - Validate configuration
   - Schedule follow-up for any missing information
   - Begin Azure provisioning process
4. **Store Completed Worksheet:** Save to project folder and track in project management system
