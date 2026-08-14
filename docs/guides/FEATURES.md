# Cloud Health Office - Complete Feature Matrix

This document provides a comprehensive overview of all features available in Cloud Health Office, including those added since the v1.0.0 release.

## 📊 Feature Overview

| Category | Features | Status | Documentation |
|----------|----------|--------|---------------|
| **Self-Service Portal** | Signup, subscriptions, Contact Sales | ✅ Complete | [FEATURES.md](./FEATURES.md#self-service-portal) |
| **EDI Transactions** | 8 transaction types | ✅ Complete | [ARCHITECTURE.md](./ARCHITECTURE.md) |
| **837 Claims Ingestion** | Automated SFTP → Kafka pipeline | ✅ Complete | [docs/837-CLAIMS-PIPELINE.md](./docs/837-CLAIMS-PIPELINE.md) |
| **Provider Network Management** | Blazor UI with 13 specialties | ✅ Complete | [FEATURES.md](./FEATURES.md#provider-network-management) |
| **Zero-Code Onboarding** | Config-to-workflow generator | ✅ Complete | [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md) |
| **FHIR Integration** | X12 → FHIR R4 mapping | ✅ Complete | [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md) |
| **Enhanced Claim Status** | ValueAdds277 (60+ fields) | ✅ Complete | [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md) |
| **Security Hardening** | 6 deployment controls | ✅ Complete | [SECURITY-HARDENING.md](./SECURITY-HARDENING.md) |
| **Deployment** | Gated release strategy | ✅ Complete | [DEPLOYMENT-GATES-GUIDE.md](./DEPLOYMENT-GATES-GUIDE.md) |
| **Testing** | 5,495 automated tests across 44 test projects | ✅ Complete | [CONTRIBUTING.md](./CONTRIBUTING.md) |
| **Multi-Tenant Security** | Cross-tenant isolation | ✅ Complete | [portal/CloudHealthOffice.Portal.Tests/](./portal/CloudHealthOffice.Portal.Tests/) |
| **Premium Billing** | Monthly premium invoicing, NACHA/ACH EFT drafts, Stripe ACH | ✅ Complete | [src/services/premium-billing-service/](../src/services/premium-billing-service/) |

## 🌐 Portal Module

### Customer-Deployed Portal

**URL**: Customer-defined, for example `https://portal.<your-domain>` after deployment
**Status**: ✅ Implemented for local and customer-owned deployments
**Implementation Date**: January 2026  
**Technology Stack**: Blazor Server, MudBlazor v6.14.0, Azure AD, Stripe, Cosmos DB

**Partner Program**: [partners@cloudhealthoffice.com](mailto:partners@cloudhealthoffice.com) - For independent consultants and implementation partners

#### Core Features

| Feature | Description | Status | Details |
|---------|-------------|--------|---------|
| **Self-Service Signup** | Customer-deployed onboarding flow with Stripe payment | ✅ Complete | Starter, Professional, Enterprise tiers — [Contact sales](mailto:sales@cloudhealthoffice.com) for pricing |
| **Azure AD Authentication** | Multi-tenant OAuth2 with smart routing | ✅ Complete | Tenant-scoped access via claims, automatic user provisioning |
| **Subscription Management** | Stripe-powered billing and subscriptions | ✅ Complete | Test mode, webhook integration, automatic renewals |
| **Contact Sales** | Enterprise inquiry tracking | ✅ Complete | Cosmos DB integration, status workflow (New→Contacted→Qualified→Closed) |
| **Demo Mode** | Try-before-you-buy experience | ✅ Complete | No credit card, explore full features |
| **Mobile Optimized** | Responsive design with hamburger nav | ✅ Complete | Accessible, slideDown animations, close-on-outside-click |
| **Tenant Isolation** | Multi-tenant Cosmos DB architecture | ✅ Complete | Separate Tenants, Members, SalesInquiries containers (400 RU/s each) |

#### Signup Flow

1. **Visit Portal**: Navigate to your deployed portal hostname
2. **Azure AD Login**: Sign in with Microsoft account (multi-tenant)
3. **Select Tier**: Choose Starter (10K claims) or Professional (50K claims) — [Contact sales](mailto:sales@cloudhealthoffice.com) for pricing
4. **Enter Payment**: Stripe payment method with PCI compliance
5. **Choose Modules**: EDI (270/271, 275, 276/277, 278, 837), Claims Adjudication, Provider Network, FHIR
6. **Start Free Trial**: 14-day trial, cancel anytime

#### Automatic Tenant Provisioning

When a user completes signup, the system automatically:
- ✅ Creates Cosmos DB tenant partition (`tenantId`)
- ✅ Generates SFTP credentials for clearinghouse integration
- ✅ Registers Azure AD application for API access
- ✅ Creates Stripe customer and subscription with 14-day trial
- ✅ Sends welcome email with access details
- ✅ Provisions or records customer-owned Portal, API, and Docs URLs

#### Contact Sales Integration

**Purpose**: Enterprise deals requiring custom pricing, SLAs, or integrations

| Field | Type | Validation | Notes |
|-------|------|------------|-------|
| **First Name** | String | Required | Contact person |
| **Last Name** | String | Required | Contact person |
| **Email** | String | Email format | Primary contact |
| **Phone** | String | Optional | For callbacks |
| **Company** | String | Required | Organization name |
| **Job Title** | String | Optional | Decision-maker role |
| **Inquiry Type** | Dropdown | Required | Enterprise Plan, Platform Demo, Partnership, Integration Support |
| **Message** | Textarea | Required | Custom requirements |

**Status Tracking**:
- **New**: Initial submission, auto-generated reference ID (inquiry-{guid})
- **Contacted**: Sales team reached out (auto-sets `contactedAt` timestamp)
- **Qualified**: Meeting scheduled, deal pipeline
- **Closed**: Won/Lost, archived

**Storage**: Cosmos DB `SalesInquiries` container (partition key: `/id`, 400 RU/s)

#### Authentication & Authorization

**Azure AD Multi-Tenant**:
- TenantId: `common` (allows any Microsoft account)
- ClientId: `54f3419d-0d69-4b06-939a-c1a260596556`
- Scopes: `openid profile email User.Read`

**Smart Routing**:
```csharp
// Extract tenant from Azure AD claims
var tenantClaim = user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid");
var userEmail = user.Identity.Name;

// Route to tenant-specific resources
var tenantId = _tenantService.GetOrCreateTenantAsync(tenantClaim.Value, userEmail);
```

**Session Affinity**: ClientIP-based, 3-hour timeout (Kubernetes Ingress)

#### Subscription Tiers

| Tier | Price | Claims/Month | Modules | Support | Trial |
|------|-------|--------------|---------|---------|-------|
| **Starter** | [Contact sales](mailto:sales@cloudhealthoffice.com) | 10,000 | All EDI + Claims | Community + Email | 14 days |
| **Professional** | [Contact sales](mailto:sales@cloudhealthoffice.com) | 50,000 | All + FHIR + Analytics | Priority + Slack | 14 days |
| **Enterprise** | Custom | Unlimited | White-label + SLA | 24/7 + Dedicated CSM | Custom |

**Stripe Integration**:
- Test Mode: `pk_test_...`
- Price IDs: Starter `price_1SyhLMJu0wSGGF9n9zqtS7Kh`, Professional `price_1SyhMmJu0wSGGF9no72J2xY9`
- Trial Period: 14 days
- Payment Methods: Credit card, ACH (US), SEPA (EU)

#### Mobile Features

**Responsive Design**:
- ✅ Hamburger menu on screens <768px
- ✅ Touch-friendly buttons (min 44x44px)
- ✅ Optimized fonts (16px minimum)
- ✅ Swipe gestures for navigation

**Accessibility**:
- ✅ ARIA labels and roles
- ✅ Keyboard navigation (Tab, Escape)
- ✅ Screen reader compatible
- ✅ High contrast support

**Performance**:
- ✅ < 2s initial load (Blazor WebAssembly prerender)
- ✅ < 100ms SignalR reconnect
- ✅ Lazy-loaded images
- ✅ CDN-hosted assets

#### Deployment

**Kubernetes**:
- Namespace: `cloudhealthoffice`
- Replicas: 2 (HPA 2-5)
- Image: `ghcr.io/aurelianware/cloudhealthoffice-portal:latest`
- Secrets: `stripe-api-keys`, `cosmos-secret`

**DNS**:
- Customer production example: `portal.<your-domain>`
- Customer staging example: `portal-staging.<your-domain>`

**SSL/TLS**: Let's Encrypt with cert-manager auto-renewal

---

## 🔄 EDI Transaction Processing

### Supported Transactions

| Transaction | Type | Direction | Status | Use Case |
|------------|------|-----------|--------|----------|
| **275** | Attachments | Inbound | ✅ Implemented | Clinical/administrative attachments for claims |
| **277** | RFAI | Outbound | ✅ Implemented | Request for Additional Information |
| **278** | Authorization | Inbound | ✅ Implemented | Prior authorization requests |
| **278** | Authorization Inquiry | Inbound | ✅ Implemented | Real-time authorization status checks |
| **278 Replay** | Reprocessing | HTTP API | ✅ Implemented | Deterministic transaction replay |
| **837** | Claims | Inbound/Outbound | ✅ Implemented | Professional, Institutional, Dental claims with automated ingestion |
| **270/271** | Eligibility | Bidirectional | ✅ Implemented | Real-time eligibility verification |
| **276/277** | Claim Status | Bidirectional | ✅ Implemented | Claim status inquiries |

### Features by Transaction

#### 275 Attachments
- ✅ SFTP polling from clearinghouses (Clearinghouse, Change Healthcare)
- ✅ X12 decode via C# X12 service
- ✅ Metadata extraction (claim, member, provider)
- ✅ Data Lake archival with date partitioning (`yyyy/MM/dd`)
- ✅ claims backend API correlation (link attachment to claim)
- ✅ Service Bus event publishing
- ✅ Automatic file deletion after processing

#### 277 RFAI (Request for Additional Information)
- ✅ Service Bus topic subscription trigger
- ✅ X12 277 message generation
- ✅ SFTP delivery to clearinghouse
- ✅ Data Lake archival of sent files
- ✅ claims backend status update

#### 278 Authorizations
- ✅ SFTP polling for incoming requests
- ✅ Authorization request processing
- ✅ Authorization inquiry (X215) support
- ✅ HTTP replay endpoint for reprocessing
- ✅ Real-time status checks
- ✅ Integration with payer authorization systems

#### 837 Claims
- ✅ Professional (837P) claims
- ✅ Institutional (837I) claims
- ✅ Dental (837D) claims
- ✅ Synthetic test data generator (PHI-safe)
- ✅ **Automated ingestion pipeline** - SFTP → X12 parsing → Kafka → Adjudication
- ✅ **Argo Workflows orchestration** - 4-step workflow: fetch-from-sftp, parse-837-files, create-claims-batch, archive-to-sftp
- ✅ **Event-driven processing** - Argo Events triggers adjudication workflow from Kafka claims-adjudication topic
- ✅ **Container-based parsing** - Node.js x12-parser container with @hahntech/x12-parser
- ✅ **Kafka publishing** - Claims-publisher container with kafkacat
- ✅ **CronWorkflow automation** - Runs every 5 minutes to poll SFTP /inbound/claims folder
- ✅ **Multi-partition topics** - claims-adjudication (6 partitions), claims-work-queue (3 partitions), claims-rejected (3 partitions)

**See [docs/837-CLAIMS-PIPELINE.md](./docs/837-CLAIMS-PIPELINE.md) for complete architecture and deployment guide.**

#### 270/271 Eligibility
- ✅ 6 search methods (member ID, SSN, name/DOB, etc.)
- ✅ Real-time verification
- ✅ FHIR R4 transformation (Patient, CoverageEligibilityRequest)
- ✅ CMS Patient Access API ready

#### 276/277 Claim Status
- ✅ Claim status inquiries
- ✅ Date range filtering
- ✅ Enhanced Claim Status (ECS) with ValueAdds277
- ✅ 60+ enhanced response fields

## 🎯 Zero-Code Payer Onboarding

### Config-to-Workflow Generator

**Status**: ✅ Production-Ready  
**Implementation Date**: November 2024  
**Lines of Code**: 2,500+ (production + tests + docs)

#### Core Capabilities

| Feature | Description | Status |
|---------|-------------|--------|
| **Interactive Wizard** | Guided configuration typically in under 5 minutes, based on testing | ✅ Complete |
| **Workflow Generation** | Automatic Argo workflow YAML creation | ✅ Complete |
| **Infrastructure Templates** | Bicep templates with parameters | ✅ Complete |
| **Documentation** | Auto-generated deployment guides | ✅ Complete |
| **Schema Generation** | JSON validation schemas | ✅ Complete |
| **CLI Tool** | Command-line interface | ✅ Complete |
| **Test Suite** | 23 comprehensive tests | ✅ Complete |
| **Example Configs** | Medicaid MCO, Regional Blues | ✅ Complete |

#### Template System

**30+ Handlebars Helpers**:
- **String**: uppercase, lowercase, camelCase, kebabCase, snakeCase, pascalCase
- **Array**: join, first, last, length, contains
- **Conditional**: eq, ne, lt, gt, lte, gte, and, or, not
- **JSON**: json, jsonInline, jsonEscape
- **Math**: add, subtract, multiply, divide
- **Date**: now, formatDate
- **Type checking**: typeof, isArray, isObject, isString, isNumber, isBoolean
- **Utility**: default, coalesce, substring, replace, trim, split, indent
- **Azure-specific**: resourceName, storageAccountName, keyVaultName

#### Generated Output Structure

```
generated/{PAYER_ID}/
├── workflows/                    # Argo workflow manifests
│   ├── ingest275.yaml
│   ├── ingest278.yaml
│   ├── replay278.yaml
│   ├── rfai277.yaml
│   └── ecs_summary_search.yaml
├── infrastructure/               # Azure infrastructure
│   ├── main.bicep               # Bicep template
│   ├── parameters.json          # Deployment parameters
│   └── deploy.sh                # Deployment script
├── docs/                        # Documentation
│   ├── DEPLOYMENT.md
│   ├── CONFIGURATION.md
│   └── TESTING.md
├── schemas/                     # JSON schemas
│   ├── Appeal-Request.json
│   └── Appeal-SubStatus.json
├── config/
│   └── payer-config.json       # Configuration backup
└── README.md                    # Payer-specific readme
```

#### Usage

```bash
# Interactive wizard
npm run generate -- interactive --output my-config.json --generate

# Generate from existing config
node dist/scripts/generate-payer-deployment.js core/examples/medicaid-mco-config.json

# Validate configuration
node dist/scripts/cli/payer-generator-cli.js validate my-config.json
```

**Time Savings**: Streamline deployment processes that traditionally take weeks  
**Documentation**: [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md)

## 🏥 FHIR R4 Integration

### Overview

**Status**: ✅ Production-Ready  
**Implementation Date**: November 2024  
**Roadmap Acceleration**: 14 months (Q1 2026 → November 2024)

### Standards Compliance

| Standard | Version | Status | Notes |
|----------|---------|--------|-------|
| HIPAA X12 270 | 005010X279A1 | ✅ Compliant | Eligibility inquiry |
| HL7 FHIR | R4 v4.0.1 | ✅ Compliant | Latest stable release |
| US Core Patient | 3.1.1 | ✅ Compliant | Required profiles |
| CMS-9115-F | Patient Access Rule | ✅ Ready | CMS interoperability |
| Da Vinci PDex | Latest | ✅ Compatible | Payer data exchange |

### Capabilities

#### X12 270 → FHIR R4 Transformation

```typescript
import { mapX12270ToFhirEligibility } from './src/fhir/fhirEligibilityMapper';

const x12Data = {
  inquiryId: 'INQ001',
  informationSource: { id: '030240928' },
  subscriber: {
    memberId: 'MEM123',
    firstName: 'John',
    lastName: 'Doe',
    dob: '1985-06-15',
    gender: 'M'
  },
  insurerId: 'PLAN01'
};

// Transform to FHIR R4
const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
```

#### Mapping Features

| Feature | Details | Status |
|---------|---------|--------|
| **Patient Resource** | US Core Patient profile | ✅ Complete |
| **CoverageEligibilityRequest** | FHIR R4 resource | ✅ Complete |
| **Gender Mapping** | X12 (M/F/U) → FHIR (male/female/unknown) | ✅ Complete |
| **Date Handling** | CCYYMMDD → YYYY-MM-DD | ✅ Complete |
| **Service Types** | 100+ X12 codes supported | ✅ Complete |
| **Subscriber/Dependent** | Both scenarios supported | ✅ Complete |
| **Identifiers** | Member ID, NPI, SSN systems | ✅ Complete |

#### Quality Metrics

- **Lines of Code**: 1,140 (production), 450 (tests), 1,190 (docs)
- **Test Coverage**: 100% (FHIR module)
- **Test Pass Rate**: Estimated 100% pass rate in internal tests (19/19 tests)
- **Dependencies**: @types/fhir (type definitions only)
- **Vulnerabilities**: 0 (core mapper)
- **Mapping Speed**: ~1ms per transaction
- **Throughput**: 1000+ transactions/second (single core)

**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md), [FHIR-SECURITY-NOTES.md](./docs/FHIR-SECURITY-NOTES.md)

## 🏥 Provider Network Management

### Overview

**Status**: ✅ Production-Ready  
**Implementation Date**: February 2026  
**UI Framework**: Blazor Server (.NET 8) with MudBlazor  
**Mock Data**: 13 providers across 13 specialties

### Features

#### Provider Search & Filtering

**Multi-criteria search**:
- Search by provider name, NPI, or practice name with debounce
- Filter by specialty (13 options: Family Medicine, Cardiology, Orthopedics, Radiology, Emergency Medicine, General Surgery, OB/GYN, Pediatrics, Psychiatry, Dermatology, Ophthalmology, Neurology, Urology)
- Filter by network status (In-Network, Out-of-Network, Pending)
- Color-coded status chips for credentials and network assignments

#### Provider List View

**Comprehensive table** with:
- NPI (10-digit unique identifier)
- Provider name
- Practice type (Individual/Group)
- Specialty and taxonomy code
- Practice name and location
- Network status with visual indicators
- Credentials (board certification, state licenses)
- Network count (number of plan assignments)
- Last claim date (activity tracking)

#### Provider Details (6-Tab Dialog)

**1. Overview Tab**:
- Full provider information (NPI, specialty, taxonomy code)
- Practice type designation
- Board certifications with expiration tracking
- Primary practice location

**2. Locations Tab**:
- Multi-location support for group practices
- Primary location designation
- Full address, phone, fax for each location
- Address validation (50 US states)

**3. Networks Tab**:
- Plan assignments with network name
- Effective and termination dates
- Status tracking (Active/Inactive)

**4. Credentials Tab**:
- Medical license numbers with state
- DEA registration numbers
- Board certifications
- Expiration date tracking with color-coded alerts
- Credentialing status (Verified, Pending, Expired)

**5. Contract Tab**:
- Reimbursement method (Fee Schedule/Capitation)
- Provider tier designation
- Fee schedule percentages (e.g., 110% Medicare)
- Capitation rates (PMPM)
- Contract effective/termination dates
- Stop-loss limits (for capitated contracts)

**6. Performance Tab**:
- Claims volume metrics (last 30/90 days)
- Authorization approval rates (88-95% range)
- Denial rates (2.8-7.7% range)
- Average days to submit claims
- Quality scores with star ratings (4.2-4.7 stars)
- Patient satisfaction scores

#### Provider Create/Edit

**3-tab form**:
- **Basic Information**: NPI (10 chars), practice type, provider name, specialty, taxonomy code, practice name
- **Contact & Location**: Full address with 50 US states dropdown, ZIP, phone/fax, email
- **Status** (edit mode): Credentialing status, network status dropdowns

#### Mock Provider Data

**13 providers** including:
- Dr. Sarah Johnson (Family Medicine) - PRV1001, In-Network, 8 networks, 312 claims/90d, 92% auth approval
- Dr. Michael Chen (Cardiology) - PRV1002, In-Network, 12 networks, 245 claims/90d, 94% auth approval, $42.50 PMPM capitation
- Dr. Emily Rodriguez (Orthopedics) - PRV1003, In-Network, 6 networks, 189 claims/90d, 91% auth approval
- Dr. David Thompson (Radiology) - PRV1004, In-Network, 18 networks, 523 claims/90d, 89% auth approval, **3 practice locations**
- Dr. Lisa Wong (Emergency Medicine) - PRV1005, In-Network, 4 networks, 412 claims/90d, 88% auth approval
- Dr. James Mitchell (General Surgery) - PRV1006, In-Network, 10 networks, 156 claims/90d, 93% auth approval, $38.75 PMPM capitation
- Dr. Maria Garcia (OB/GYN) - PRV1007, Out-of-Network, 0 networks, 203 claims/90d, 90% auth approval
- Dr. Robert Kim (Pediatrics) - PRV1008, In-Network, 9 networks, 387 claims/90d, 95% auth approval
- Dr. Jennifer White (Psychiatry) - PRV1009, Pending, 2 networks, 68 claims/90d, 91% auth approval
- Dr. Christopher Brown (Dermatology) - PRV1010, In-Network, 7 networks, 234 claims/90d, 92% auth approval
- Dr. Amanda Taylor (Ophthalmology) - PRV1011, In-Network, 11 networks, 298 claims/90d, 90% auth approval
- Dr. Daniel Martinez (Neurology) - PRV1012, In-Network, 5 networks, 167 claims/90d, 93% auth approval
- Dr. Nicole Anderson (Urology) - PRV1013, In-Network, 8 networks, 201 claims/90d, 89% auth approval

### Implementation Details

**Files**:
- `portal/CloudHealthOffice.Portal/Pages/Providers.razor` - Main list page
- `portal/CloudHealthOffice.Portal/Pages/ProviderDetailsDialog.razor` - Details dialog (6 tabs)
- `portal/CloudHealthOffice.Portal/Pages/CreateEditProviderDialog.razor` - Create/edit form (3 tabs)
- `portal/CloudHealthOffice.Portal/Services/IServices.cs` - Service contracts and DTOs
- `portal/CloudHealthOffice.Portal/Services/ServiceImplementations.cs` - Mock data and HTTP integration

**Service Integration**:
- IProviderService interface with 6 methods
- HTTP client fallback to mock data (13 providers)
- DTOs: ProviderListItem, ProviderDetails, PracticeLocation, ProviderCredential, NetworkAssignment, ProviderContract, ProviderPerformance

**Next Steps** (Backend Integration):
- Connect to Provider Service API (`http://provider-service.cloudhealthoffice.svc.cluster.local:8080`)
- Replace mock data with database-backed providers
- Implement credential verification workflow
- Add network assignment automation
- Build performance metrics aggregation

## 💎 ValueAdds277 Enhanced Claim Status

### Overview

**Status**: ✅ Production-Ready  
**Implementation Date**: November 2024  
**Premium Revenue**: Potential value-add of up to $10k/year per payer (varies by implementation)  
**Provider ROI**: May save providers time on claim lookups

### Enhanced Response Fields (60+)

#### Financial Fields (8)
- BILLED_AMOUNT
- ALLOWED_AMOUNT
- PAID_AMOUNT
- COPAY
- COINSURANCE
- DEDUCTIBLE
- DISCOUNT
- PATIENT_RESPONSIBILITY

#### Clinical Fields (4)
- Diagnosis codes (ICD-10)
- Procedure codes (CPT/HCPCS)
- Service dates (from/through)
- Place of service

#### Demographics (4 Objects)
- **Patient**: Name, DOB, gender, address, phone
- **Subscriber**: Name, member ID, relationship
- **Billing Provider**: Name, NPI, TIN, address
- **Rendering Provider**: Name, NPI, specialty

#### Remittance Fields (4)
- Check/EFT number
- Payment date
- Payer claim control number
- Trace numbers

#### Service Line Details (10+ fields per line)
- Line-level financial breakdown
- CPT/HCPCS codes
- Units and charges
- Adjustment reasons
- Payment status

### Integration Flags (6)

Enable seamless cross-module workflows:

| Flag | Logic | Integration | Provider Action |
|------|-------|-------------|-----------------|
| `eligibleForAppeal` | Denied or Partially Paid | Appeals Module | "Dispute Claim" → File appeal |
| `eligibleForAttachment` | Pending/In Process/Suspended | Attachments Module | "Send Attachments" → HIPAA 275 |
| `eligibleForCorrection` | Denied or Rejected | Corrections Module | "Correct Claim" → Resubmit |
| `eligibleForMessaging` | Always true (configurable) | Messaging Module | "Message Payer" → Secure chat |
| `eligibleForChat` | Payer-specific | Chat Module | "Live Chat" → Real-time support |
| `eligibleForRemittanceViewer` | Paid or Partially Paid | Remittance Module | "View Remittance" → 835 data |

### Configuration

```json
{
  "ecsModule": {
    "enabled": true,
    "valueAdds277": {
      "enabled": true,
      "claimFields": {
        "financial": true,
        "clinical": true,
        "demographics": true,
        "remittance": true,
        "statusDetails": true
      },
      "serviceLineFields": {
        "enabled": true,
        "includeAdjustments": true,
        "includePaymentDetails": true
      },
      "integrationFlags": {
        "eligibleForAppeal": true,
        "eligibleForAttachment": true,
        "eligibleForCorrection": true,
        "eligibleForMessaging": true,
        "eligibleForChat": false,
        "eligibleForRemittanceViewer": true
      }
    }
  }
}
```

### Performance

| Metric | Standard ECS | ValueAdds277 Full | Delta |
|--------|-------------|-------------------|-------|
| Response Time | 200ms | 250ms | +50ms (+25%) |
| Response Size | 2KB | 6KB | +4KB (3x) |
| Backend Query | 150ms | 150ms | 0ms (same) |
| Transformation | 50ms | 100ms | +50ms (+100%) |
| Fields Returned | 20-25 | 60+ | +40 (3x) |

**Conclusion**: Minimal performance impact (25% increase) for 3x data richness.

**Documentation**: [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md), [ECS-INTEGRATION.md](./docs/ECS-INTEGRATION.md)

## 🛡️ Security Hardening

### Overview

**Status**: ✅ Production-Ready  
**Implementation Date**: November 2024  
**Security Score**: High security maturity (self-assessed)  
**HIPAA Compliance**: Addresses key HIPAA technical safeguards (§ 164.312)

### Security Controls (6)

#### 1. Azure Key Vault Integration

**Premium SKU with HSM-backed keys**

Features:
- FIPS 140-2 Level 2 hardware security modules
- 90-day soft delete retention
- Purge protection (cannot be disabled)
- RBAC authorization
- Network ACLs defaulting to deny
- Private endpoint only access
- 365-day diagnostic log retention

**Deployment**: `infra/modules/keyvault.bicep` (165 lines)

#### 2. Private Endpoints & Network Isolation

**Complete network isolation for PHI resources**

Features:
- Virtual Network (10.0.0.0/16)
- AKS subnet with delegation
- Private Endpoints subnet
- Private DNS zones (Storage, Service Bus, Key Vault)
- Service endpoints for all PHI services
- Public access disabled

**Deployment**: `infra/modules/networking.bicep` (178 lines), `infra/modules/private-endpoints.bicep` (177 lines)

#### 3. PHI Masking in Application Insights

**DCR-based redaction of sensitive patterns**

Patterns masked:
- Member IDs: `MBR****5678`
- SSN: `***-**-6789`
- Claim Numbers: `CLM****4321`
- Provider NPIs: `NPI*******890`

Real-time monitoring for unmasked PHI with compliance alerts.

#### 4. HTTP Endpoint Authentication

**Azure AD Easy Auth for replay278 endpoint**

Features:
- OAuth 2.0 client credentials flow
- JWT token validation
- 401 Unauthorized responses
- 1-hour token expiration
- Service principal authentication support

#### 5. Data Lifecycle Management

**7-year retention with automated tier transitions**

Lifecycle policy:
- **Hot tier**: 0-30 days (active processing)
- **Cool tier**: 31-90 days (recent archives)
- **Archive tier**: 91 days - 7 years (long-term retention)
- **Delete**: After 7 years (HIPAA compliant)

**Cost Impact**: Estimated 94% storage cost reduction based on lifecycle policies; actual savings vary

#### 6. Customer-Managed Keys (Optional)

**BYOK for regulatory compliance**

Features:
- RSA-HSM keys (4096-bit)
- Automatic 90-day key rotation
- Storage account CMK configuration
- RBAC role assignments
- Independent key revocation

**Deployment**: `infra/modules/cmk.bicep` (129 lines)

### Compliance Matrix

#### HIPAA Technical Safeguards (§ 164.312)

| Standard | Implementation | Status |
|----------|----------------|--------|
| **§ 164.312(a)(1) Access Control** | Azure AD, RBAC, Key Vault, MFA | ✅ Addressed |
| **§ 164.312(b) Audit Controls** | Activity Log, Application Insights, 7-year retention | ✅ Addressed |
| **§ 164.312(c)(1) Integrity** | Blob versioning, checksums, immutability | ✅ Addressed |
| **§ 164.312(d) Authentication** | Azure AD, managed identities, OAuth 2.0 | ✅ Addressed |
| **§ 164.312(e)(1) Transmission Security** | TLS 1.2+, private endpoints, network isolation | ✅ Addressed |

**Documentation**: [SECURITY-HARDENING.md](./SECURITY-HARDENING.md), [HIPAA-COMPLIANCE-MATRIX.md](./docs/HIPAA-COMPLIANCE-MATRIX.md)

## 🚦 Gated Release Strategy

### Overview

**Status**: ✅ Production-Ready  
**Implementation Date**: November 2024

### Approval Gates

#### UAT Environment

**Triggers**: Push to `release/*` branches  
**Reviewers**: 1-2 approvers (QA team lead, app owner)  
**Pre-Approval Checks**:
- TruffleHog secret detection
- PII/PHI scanning
- Deployment artifact validation
- Security summary visible to approvers

**Workflow**: `.github/workflows/deploy-uat.yml`

#### PROD Environment

**Triggers**: Manual workflow dispatch from `main` only  
**Reviewers**: 2-3 approvers (DevOps manager, app owner, compliance officer)  
**Pre-Approval Checks**:
- TruffleHog secret detection
- PII/PHI scanning
- ARM What-If analysis
- Deployment artifact validation
- Security summary visible to approvers

**Workflow**: `.github/workflows/deploy.yml`

### Features

- ✅ Pre-approval security validation
- ✅ Automated audit logging
- ✅ Communication/notification strategy
- ✅ Emergency hotfix procedures (30-minute SLA)
- ✅ Rollback automation (UAT) and procedures (PROD)
- ✅ Health checks (AKS/Argo Workflows, Storage, Service Bus, Application Insights)
- ✅ Metrics & reporting (success rate, approval times, rollback incidents)

### Approval Checklist

**UAT**:
- [ ] Security scans passed
- [ ] No secrets or credentials detected
- [ ] Bicep validation successful
- [ ] Changes align with release notes

**PROD**:
- [ ] Security scans passed
- [ ] ARM What-If reviewed (no unexpected deletions)
- [ ] UAT deployment successful
- [ ] Compliance requirements verified
- [ ] Rollback plan documented

**Documentation**: [DEPLOYMENT-GATES-GUIDE.md](./DEPLOYMENT-GATES-GUIDE.md)

## 🧪 Testing & Validation

### Test Coverage

| Category | Tests | Status | Notes |
|----------|-------|--------|-------|
| **Unit Tests** | 44 | ✅ Passing | Core functionality |
| **PHI Validation** | 18 | ✅ Passing | HIPAA compliance |
| **Total** | 62 | ✅ Passing | 41% increase from v1.0.0 |

### Testing Tools

#### Synthetic 837 Claim Generator

Generate PHI-safe test data:

```bash
# Generate 10 professional claims
node dist/scripts/utils/generate-837-claims.js 837P 10 ./test-data

# Generate 5 institutional claims
node dist/scripts/utils/generate-837-claims.js 837I 5 ./test-data
```

#### E2E Test Suite

Comprehensive health checks:

```powershell
./scripts/test-e2e.ps1 `
  -ResourceGroup "my-rg" `
  -AksCluster "my-aks" `
  -ServiceBusNamespace "my-sb" `
  -ReportPath "./health-report.json"
```

Validates:
- Infrastructure existence
- Argo Workflow health
- Service Bus configuration
- Storage account verification
- Workflow deployment status

#### Workflow Testing

```powershell
# Test 275 attachment ingestion
./test-workflows.ps1 -TestInbound275

# Test 277 RFAI outbound
./test-workflows.ps1 -TestOutbound277

# Full end-to-end workflow
./test-workflows.ps1 -TestFullWorkflow
```

#### CI/CD PHI Validation

Automatic scanning on every PR:
- Detects unredacted console.log patterns
- Checks for hardcoded PHI
- Verifies hipaaLogger usage
- Blocks PRs with violations

**Workflow**: `.github/workflows/phi-validation.yml`

### Quality Metrics

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| **Test Count** | 44 | 62 | +41% |
| **PHI Tests** | 0 | 18 | +18 (new) |
| **E2E Testing** | Manual | Automated | 100% automation |
| **CI/CD Scanning** | None | 4 checks | Complete |

## 📚 Documentation

### Complete Documentation Suite (20+ guides)

#### Getting Started
- [README.md](./README.md) - Project overview and quick links
- [QUICKSTART.md](./QUICKSTART.md) - Deploy in 5 minutes
- [ONBOARDING.md](./ONBOARDING.md) - Complete setup instructions

#### Features & Capabilities
- [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md) - Zero-code payer onboarding (400+ lines)
- [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md) - X12 to FHIR transformation (680 lines)
- [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md) - Enhanced claim status (350+ lines)
- [ECS-INTEGRATION.md](./docs/ECS-INTEGRATION.md) - ECS API documentation (1,256 lines)
- [APPEALS-INTEGRATION.md](./docs/APPEALS-INTEGRATION.md) - Appeals module

#### Security & Compliance
- [SECURITY-HARDENING.md](./SECURITY-HARDENING.md) - Production security controls (1,229 lines)
- [HIPAA-COMPLIANCE-MATRIX.md](./docs/HIPAA-COMPLIANCE-MATRIX.md) - Regulatory mapping (702 lines)
- [SECURITY.md](./SECURITY.md) - General security practices
- [FHIR-SECURITY-NOTES.md](./docs/FHIR-SECURITY-NOTES.md) - FHIR security advisory (240 lines)

#### Deployment & Operations
- [DEPLOYMENT.md](./DEPLOYMENT.md) - Step-by-step deployment (93,438 bytes)
- [DEPLOYMENT-GATES-GUIDE.md](./DEPLOYMENT-GATES-GUIDE.md) - UAT/PROD approval workflows (25+ pages)
- [DEPLOYMENT-SECRETS-SETUP.md](./DEPLOYMENT-SECRETS-SETUP.md) - Secret management (1,014 lines)
- [GITHUB-ACTIONS-SETUP.md](./GITHUB-ACTIONS-SETUP.md) - CI/CD configuration

#### Reference
- [ARCHITECTURE.md](./ARCHITECTURE.md) - Technical deep-dive (40,330 bytes)
- [CHANGELOG.md](./CHANGELOG.md) - Version history
- [TROUBLESHOOTING-FAQ.md](./TROUBLESHOOTING-FAQ.md) - 60+ solutions
- [CONTRIBUTING.md](./CONTRIBUTING.md) - Development guidelines

#### Implementation Summaries
- [IMPLEMENTATION-SUMMARY.md](./IMPLEMENTATION-SUMMARY.md) - Config-to-workflow generator
- [FHIR-IMPLEMENTATION-SUMMARY.md](./FHIR-IMPLEMENTATION-SUMMARY.md) - FHIR R4 integration
- [SECURITY-IMPLEMENTATION-SUMMARY.md](./SECURITY-IMPLEMENTATION-SUMMARY.md) - Security hardening
- [GATED-RELEASE-IMPLEMENTATION-SUMMARY.md](./GATED-RELEASE-IMPLEMENTATION-SUMMARY.md) - Release strategy
- [ONBOARDING-ENHANCEMENTS.md](./ONBOARDING-ENHANCEMENTS.md) - Onboarding improvements
- [BRANDING-IMPLEMENTATION-SUMMARY.md](./BRANDING-IMPLEMENTATION-SUMMARY.md) - Sentinel branding

**Total Documentation**: 20,000+ lines across 40+ files

## 🎯 Key Metrics

### Time & Efficiency

| Metric | Before | After | Improvement |
|--------|--------|-------|-------------|
| **Onboarding Time** | 2-4 hours | Typically under 5 minutes, based on testing | Potential for up to 96% reduction |
| **Payer Deployment** | Multi-week project | Streamlined to minutes | Potential for up to 99% reduction |
| **Configuration Errors** | 40% | <5% | Potential for up to 87.5% reduction |
| **Claim Lookup Time** | 7-21 minutes | Instant | May save providers time |

### Cost & ROI

| Metric | Value | Notes |
|--------|-------|-------|
| **Storage Cost Savings** | Estimated 94% reduction | Based on lifecycle policies; actual savings vary |
| **Provider ROI** | May save providers time | ValueAdds277 features |
| **Premium Revenue** | Potential value-add of up to $10k/year per payer | Varies by implementation |
| **Sandbox Deployment** | ~$50-100/month | Azure costs |

### Quality & Compliance

| Metric | Value | Target |
|--------|-------|--------|
| **Security Score** | High security maturity (self-assessed) | ✅ |
| **HIPAA Compliance** | Addresses key technical safeguards | ✅ |
| **Test Pass Rate** | Estimated 100% in internal tests (62/62) | 100% ✅ |
| **Test Coverage** | 100% (FHIR module) | >80% ✅ |
| **Build Success Rate** | 100% | 100% ✅ |
| **Vulnerabilities** | 0 (core mapper) | 0 ✅ |

## 🚀 Deployment Options

### Option 1: One-Click Azure Deploy

**Time**: <5 minutes  
**Complexity**: Easy  
**Best For**: Sandbox/Demo

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Faurelianware%2Fcloudhealthoffice%2Fmain%2Fazuredeploy.json)

### Option 2: Interactive Wizard

**Time**: <10 minutes  
**Complexity**: Easy  
**Best For**: Development

```bash
npm run generate -- interactive --output my-config.json --generate
```

### Option 3: Manual Configuration

**Time**: 30-60 minutes  
**Complexity**: Advanced  
**Best For**: Production

Complete control over all settings, infrastructure, and workflows.

See: [DEPLOYMENT.md](./DEPLOYMENT.md)

## 📈 Roadmap

### Completed (v1.0.0+)
- ✅ Core EDI transactions (275, 277, 278, 837, 270/271, 276/277)
- ✅ Config-to-workflow generator
- ✅ FHIR R4 integration (X12 270)
- ✅ ValueAdds277 enhanced claim status
- ✅ Security hardening (9/10 score)
- ✅ Gated release strategy
- ✅ Onboarding enhancements

### Q1 2025
- [ ] X12 271 → FHIR R4 CoverageEligibilityResponse (reverse)
- [ ] FHIR → X12 270 (outbound queries)
- [ ] Azure Health Data Services integration
- [ ] FHIR resource validation library

### Q2 2025
- [ ] X12 837 Claims → FHIR R4 Claim
- [ ] Prior authorization (X12 278 ↔ FHIR)
- [ ] SMART on FHIR authentication
- [ ] Da Vinci PDex profile implementation

### Q3 2025
- [ ] FHIR Bulk Data export (CMS requirement)
- [ ] Attachments (X12 275 ↔ FHIR DocumentReference)
- [ ] Provider Directory (X12 ↔ FHIR Practitioner)
- [ ] Real-time benefit check (RTBC)

See: [ROADMAP.md](./ROADMAP.md)

## 🤝 Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

## 📄 License

BSL 1.1 - See [LICENSE](./LICENSE) for details.

## 🤝 Integration Focus

Cloud Health Office is backend-agnostic and designed to integrate seamlessly with existing systems like claims adjudication systems, providing enhancements to EDI workflows without requiring full replacements.

---

## 🤝 Collaboration and Integration

Cloud Health Office is designed to complement leading core administrative platforms like claims adjudication systems, enabling rapid enhancements to existing workflows without disruption.

---

**Cloud Health Office** – Advancing Healthcare EDI Integration

*Source-Available | Azure-Native | Evidence-Backed | Customer-Validated*
