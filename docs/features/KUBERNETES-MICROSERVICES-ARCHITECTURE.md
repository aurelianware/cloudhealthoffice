# Cloud Health Office - Kubernetes Microservices Architecture

## Overview

Complete migration of Cloud Health Office components from Azure Static Web Apps and standalone services into a unified Kubernetes-based microservices platform running on AKS.

## Current State

### Existing Components
- **Static Web App** (`site/`): Marketing site, login, portal pages hosted on Azure Static Web Apps
- **Eligibility Service** (`services/eligibility-service/`): X12 270/271 + FHIR eligibility checks (TypeScript/Node.js)
- **Migration Wizard** (`tools/migration-wizard/`): Blazor web app for legacy system migration
- **EDI Workflows**: X12 275/277/278/837 (Kubernetes CronJobs) ✅ Already in cluster

### Target State - EDI-Aligned Microservices
All components running in AKS cluster aligned with HIPAA X12 transaction flows:

**Core Services (834 Enrollment Foundation):**
- **Sponsor Service** (new) - Employer/group data populated by 834 transactions
- **Member Service** (new) - Subscriber/dependent data populated by 834 transactions  
- **Coverage Service** (new) - Links Member → Sponsor → Plan with effective dates (834)

**Claims & Authorization Services (837/835/278):**
- **Claims Service** (new) - Claims data populated by 837, updated by 835/277
- **Authorization Service** (new) - Prior auth requests/responses (278)
- **Claims Scrubbing Service** (existing) - Pre-submission validation

**Benefit & Provider Services:**
- **Benefit Plan Service** (in progress) - Insurance product definitions
- **Provider Directory Service** (new) - NPI lookup, credentialing, network participation
- **Eligibility Service** (existing) - Real-time 270/271 queries

**Supporting Services:**
- **Reference Data Service** (new) - CPT/HCPCS/ICD-10 codes (PostgreSQL)
- **Portal Backend API** (new) - User management, dashboards, reporting

**Infrastructure:**
- **Frontend**: Blazor Server
- **API Gateway**: Kong or Azure API Management  
- **Databases**: Cosmos DB (partition by tenantId), PostgreSQL (reference data), Redis (caching)
- **Authentication**: Azure AD B2C multi-tenant
- **Service Mesh**: Dapr for service-to-service communication

---

## Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────────┐
│  Internet / Users                                                    │
└────────────┬─────────────────────────────────────────────────────────┘
             │
             │ HTTPS
             ▼
┌────────────────────────────────────────────────────────────────────────┐
│  Azure Application Gateway / Load Balancer                            │
│  - SSL Termination                                                     │
│  - WAF (Web Application Firewall)                                     │
│  - Custom domain: portal.cloudhealthoffice.com                        │
└────────────┬───────────────────────────────────────────────────────────┘
             │
             ▼
┌────────────────────────────────────────────────────────────────────────┐
│  AKS Cluster (rg-hipaa-logic-apps)                                    │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │  INGRESS CONTROLLER (NGINX or Azure App Gateway Ingress)        │ │
│  │  - TLS termination                                               │ │
│  │  - Path-based routing                                            │ │
│  │  - Rate limiting                                                 │ │
│  └────────────┬─────────────────────────────────────────────────────┘ │
│               │                                                        │
│  ┌────────────┴─────────────────────────────────────┐                │
│  │                                                    │                │
│  ▼                                                    ▼                │
│  ┌──────────────────────────────┐    ┌──────────────────────────────┐ │
│  │  FRONTEND (cho-frontend)     │    │  API GATEWAY                 │ │
│  │  Namespace: cho-portal       │    │  Namespace: cho-portal       │ │
│  │  ──────────────────────────  │    │  ──────────────────────────  │ │
│  │  • Blazor Server/WASM        │    │  • Kong / NGINX              │ │
│  │    OR React SPA              │    │  • Auth middleware           │ │
│  │  • Static assets (CDN)       │    │  • Request validation        │ │
│  │  • Server-side rendering     │    │  • Rate limiting             │ │
│  │  • Real-time updates         │    │  • API versioning            │ │
│  │  Port: 80/443                │    │  Port: 8080                  │ │
│  └──────────────────────────────┘    └────────┬─────────────────────┘ │
│                                                │                       │
│                                                │ Routes to services    │
│               ┌────────────────────────────────┼──────────────────┐   │
│               │                                │                  │   │
│  ┌────────────▼───────────┐  ┌────────────────▼──────┐  ┌───────▼───────┐
│  │  Eligibility Service   │  │  Benefit Config      │  │  Provider     │ │
│  │  Namespace: cloudhealthoffice   │  │  Namespace: cloudhealthoffice │  │  Directory    │ │
│  │  ────────────────────  │  │  ──────────────────  │  │  cloudhealthoffice     │ │
│  │  • X12 270/271         │  │  • Plan management   │  │  ────────────  │ │
│  │  • FHIR Coverage...    │  │  • Benefit rules     │  │  • NPI lookup │ │
│  │  • Cosmos DB cache     │  │  • Copay/deductible  │  │  • CAQH sync  │ │
│  │  • Dapr state store    │  │  • Network tiers     │  │  • Credent... │ │
│  │  Port: 3000            │  │  Port: 3001          │  │  Port: 3002   │ │
│  └────────────────────────┘  └──────────────────────┘  └───────────────┘ │
│                                                                          │
│  ┌─────────────────────┐  ┌──────────────────────┐  ┌─────────────────┐ │
│  │  Reference Data     │  │  Claims Scrubbing    │  │  Portal Backend │ │
│  │  Namespace: cloudhealthoffice│  │  Namespace: cloudhealthoffice │  │  cloudhealthoffice       │ │
│  │  ─────────────────  │  │  ──────────────────  │  │  ───────────    │ │
│  │  • CPT codes        │  │  • NCCI edits        │  │  • User mgmt    │ │
│  │  • HCPCS codes      │  │  • DRG validation    │  │  • Dashboards   │ │
│  │  • ICD-10 codes     │  │  • Modifier checks   │  │  • Reporting    │ │
│  │  • LOINC codes      │  │  • Rules engine      │  │  • Settings     │ │
│  │  • PostgreSQL       │  │  Port: 3004          │  │  Port: 3005     │ │
│  │  Port: 3003         │  │                      │  │                 │ │
│  └─────────────────────┘  └──────────────────────┘  └─────────────────┘ │
│                                                                          │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  EDI WORKFLOWS (Existing)                                        │  │
│  │  Namespace: cho-workflows                                        │  │
│  │  ──────────────────────────────────────────────────────────────  │  │
│  │  • X12 275 Attachments (hourly)                                 │  │
│  │  • X12 277 Status (every 15 min)                                │  │
│  │  • X12 278 Prior Auth (every 2 hrs)                             │  │
│  │  • X12 837P/I/D Claims (daily)                                  │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  SFTP SERVER (Existing)                                          │  │
│  │  Namespace: cho-sftp                                             │  │
│  │  ──────────────────────────────────────────────────────────────  │  │
│  │  • Clearinghouse file exchange                                   │  │
│  │  • LoadBalancer: 20.115.193.245                                 │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │  DAPR SIDECARS (Service Mesh)                                    │  │
│  │  • State management (Cosmos DB, Redis)                           │  │
│  │  • Pub/Sub (Service Bus, Event Grid)                            │  │
│  │  • Secrets (Azure Key Vault)                                    │  │
│  │  • Service invocation                                            │  │
│  └──────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
             │                                 │
             │ Azure Services                  │
             ▼                                 ▼
┌────────────────────────┐    ┌────────────────────────────────────┐
│  Cosmos DB             │    │  PostgreSQL Flexible Server        │
│  • Eligibility cache   │    │  • CPT codes (44k+ codes)          │
│  • Benefit plans       │    │  • HCPCS codes (8k+ codes)         │
│  • Provider directory  │    │  • ICD-10 codes (70k+ codes)       │
│  • Audit logs          │    │  • LOINC codes (90k+ codes)        │
└────────────────────────┘    │  • Modifier definitions            │
                              │  • DRG grouper data                │
┌────────────────────────┐    └────────────────────────────────────┘
│  Azure Key Vault       │
│  • API secrets         │    ┌────────────────────────────────────┐
│  • DB connection strs  │    │  Redis Cache                       │
│  • SFTP credentials    │    │  • Session state                   │
│  • SSL certificates    │    │  • API response caching            │
└────────────────────────┘    │  • Rate limit counters             │
                              └────────────────────────────────────┘
┌────────────────────────┐
│  Azure Service Bus     │    ┌────────────────────────────────────┐
│  • EDI message queues  │    │  Application Insights              │
│  • Event notifications │    │  • Distributed tracing             │
│  • Dead letter queues  │    │  • Performance monitoring          │
└────────────────────────┘    │  • Log aggregation                 │
                              └────────────────────────────────────┘
```

---

## Namespace Organization

```yaml
cho-portal      # Frontend + API Gateway
cloudhealthoffice        # Core microservices
cho-workflows   # EDI batch jobs (existing)
cho-sftp        # SFTP server (existing)
cho-monitoring  # Prometheus, Grafana
cho-system      # Dapr, cert-manager, ingress
```

---

## Technology Stack

### Frontend Options

**Option 1: Blazor Server (Recommended)**
- ✅ Leverage existing Migration Wizard Blazor components
- ✅ Real-time SignalR for live updates
- ✅ C# end-to-end (same language as backend)
- ✅ Low JavaScript bundle size
- ❌ Requires WebSocket connection
- ❌ Server resources per connected user

**Option 2: Blazor WebAssembly**
- ✅ Client-side execution (reduced server load)
- ✅ Works offline after initial load
- ✅ Faster perceived performance
- ❌ Larger initial download (~2MB)
- ❌ Limited .NET runtime

**Option 3: React + TypeScript**
- ✅ Mature ecosystem, tons of libraries
- ✅ Lightweight, fast client-side
- ✅ Excellent DevEx with Vite
- ❌ Different language than backend
- ❌ Need separate API client library

**Decision: Start with Blazor Server** (can add WASM hosting mode later)

### Backend Services (EDI-Aligned)

| Service | Language | Framework | Database | Port | Populated By |
|---------|----------|-----------|----------|------|--------------|
| Sponsor | C# | ASP.NET Core | Cosmos DB | 3000 | 834 Enrollment |
| Member | C# | ASP.NET Core | Cosmos DB | 3001 | 834 Enrollment |
| Coverage | C# | ASP.NET Core | Cosmos DB | 3002 | 834 Enrollment |
| Benefit Plan | C# | ASP.NET Core | Cosmos DB | 3003 | Manual/Admin |
| Claims | C# | ASP.NET Core | Cosmos DB | 3004 | 837/835/277 |
| Authorization | C# | ASP.NET Core | Cosmos DB | 3005 | 278 Transactions |
| Eligibility | TypeScript | Node.js/Express | Cosmos DB | 3006 | 270/271 Real-time |
| Provider Directory | C# | ASP.NET Core | Cosmos DB | 3007 | Manual/CAQH |
| Reference Data | C# | ASP.NET Core | PostgreSQL | 3008 | CMS Bulk Import |
| Claims Scrubbing | TypeScript | Node.js/Express | Cosmos DB | 3009 | Pre-837 Validation |
| Portal Backend | C# | ASP.NET Core | Cosmos DB | 3010 | User Actions |

---

## EDI Transaction Flow Architecture

### 834 Enrollment Processing Flow
```
834 Transaction → Parse EDI
  ├─> Sponsor Service: Create/update employer group (INS/NM1/N3/N4/PER segments)
  ├─> Member Service: Create subscriber + dependents (INS/NM1/DMG/REF segments)
  └─> Coverage Service: Link member → sponsor → plan (HD/COB/DTP segments)
```

### 837 Claims Processing Flow
```
837 Transaction → Claims Service (store claim)
  ├─> Query Coverage Service (active coverage?)
  ├─> Query Provider Service (in-network?)
  ├─> Query Benefit Plan Service (copay/deductible rules)
  ├─> Query Authorization Service (prior auth approved?)
  ├─> Adjudication Engine
  └─> Generate 835 Remittance + 277 Status
```

### 270/271 Eligibility Check Flow
```
270 Request → Eligibility Service
  ├─> Query Coverage Service (find active coverage by member ID + DOB)
  ├─> Query Member Service (demographics)
  ├─> Query Benefit Plan Service (plan details, deductibles, copays)
  └─> Return 271 Response with coverage details
```

### 278 Prior Authorization Flow
```
278 Request → Authorization Service (store auth request)
  ├─> Query Coverage Service (coverage active?)
  ├─> Query Benefit Plan Service (prior auth required for CPT?)
  ├─> Medical Review (manual or automated)
  └─> Return 278 Response (approved/denied/pended)
```

---

## New Microservices Design (EDI-Aligned)

### 1. Sponsor Service (834 Foundation)

**Purpose**: Manage employers/groups that purchase health coverage (834 sponsor segments)

**Endpoints**:
```http
GET    /api/v1/sponsors                 # List all sponsors
GET    /api/v1/sponsors/{groupNumber}   # Get sponsor details
POST   /api/v1/sponsors                 # Create sponsor
PUT    /api/v1/sponsors/{groupNumber}   # Update sponsor
DELETE /api/v1/sponsors/{groupNumber}   # Deactivate sponsor

GET    /api/v1/sponsors/{groupNumber}/members  # Get all members under sponsor
GET    /api/v1/sponsors/{groupNumber}/coverage-summary # Coverage stats
```

**Data Model**:
```csharp
public class Sponsor
{
    public string TenantId { get; set; }           // Multi-tenant partition key
    public string GroupNumber { get; set; }        // REF*1L segment
    public string EmployerName { get; set; }       // NM1 segment
    public string TaxId { get; set; }              // REF*EI segment
    public string Address { get; set; }            // N3 segment
    public string City { get; set; }               // N4 segment
    public string State { get; set; }              // N4 segment
    public string ZipCode { get; set; }            // N4 segment
    public string ContactName { get; set; }        // PER segment
    public string ContactPhone { get; set; }       // PER segment
    public string ContactEmail { get; set; }       // PER segment
    public DateTime EffectiveDate { get; set; }    // DTP*348 segment
    public DateTime? TerminationDate { get; set; } // DTP*349 segment
    public SponsorStatus Status { get; set; }
    public BillingInfo BillingInfo { get; set; }
    public int TotalMembers { get; set; }          // Calculated
    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdatedDate { get; set; }
}

public enum SponsorStatus
{
    Active,
    Suspended,
    Terminated,
    PendingActivation
}

public class BillingInfo
{
    public decimal PremiumAmount { get; set; }
    public BillingFrequency Frequency { get; set; }
    public int BillingDay { get; set; }           // Day of month
    public string BillingAccountNumber { get; set; }
}

public enum BillingFrequency
{
    Monthly,
    Quarterly,
    Annual
}
```

**Key Features**:
- Multi-tenant (partition by TenantId)
- Populated by 834 enrollment transactions
- Tracks employer groups and contract dates
- Links to members via Coverage Service
- Billing and premium management

### 2. Member Service (834 Enrollment)

**Purpose**: Manage payer benefit plan configurations, copays, deductibles, coverage rules

**Endpoints**:
```http
GET    /api/v1/plans                    # List all plans
GET    /api/v1/plans/{id}               # Get plan details
POST   /api/v1/plans                    # Create plan
PUT    /api/v1/plans/{id}               # Update plan
DELETE /api/v1/plans/{id}               # Delete plan

GET    /api/v1/plans/{id}/benefits      # Get benefits for plan
POST   /api/v1/plans/{id}/benefits      # Add benefit
PUT    /api/v1/plans/{id}/benefits/{benefitId}  # Update benefit

GET    /api/v1/plans/{id}/network-tiers # Get network tiers (in/out of network)
```

**Data Model**:
```typescript
interface BenefitPlan {
  id: string;
  planId: string;
  planName: string;
  payer: string;
  effectiveDate: Date;
  terminationDate?: Date;
  planType: 'HMO' | 'PPO' | 'EPO' | 'POS';
  metalLevel?: 'Bronze' | 'Silver' | 'Gold' | 'Platinum';
  
  benefits: Benefit[];
  networkTiers: NetworkTier[];
  costSharing: CostSharing;
}

interface Benefit {
  serviceCategory: string; // Office visit, Emergency, etc.
  cptCodes: string[];
  inNetworkCopay: number;
  outNetworkCopay: number;
  deductibleApplies: boolean;
  priorAuthRequired: boolean;
  limitations: string;
}
```

### 2. Provider Directory Service

**Purpose**: Manage provider data, NPI lookups, credentialing status, network participation

**Endpoints**:
```http
GET    /api/v1/providers                # Search providers
GET    /api/v1/providers/{npi}          # Get provider by NPI
POST   /api/v1/providers                # Add provider
PUT    /api/v1/providers/{npi}          # Update provider
DELETE /api/v1/providers/{npi}          # Deactivate provider

GET    /api/v1/providers/{npi}/networks # Get network participation
POST   /api/v1/providers/{npi}/credential  # Update credential status

# CAQH integration
POST   /api/v1/providers/sync-caqh      # Sync from CAQH ProView
```

**Data Model**:
```typescript
interface Provider {
  npi: string;
  firstName: string;
  lastName: string;
  credentials: string; // MD, DO, NP, PA
  specialty: string;
  taxonomyCode: string;
  
  addresses: Address[];
  phoneNumbers: PhoneNumber[];
  
  networkParticipation: NetworkParticipation[];
  credentialingStatus: CredentialStatus;
  deaNumber?: string;
  stateLicenses: StateLicense[];
}
```

### 3. Reference Data Service

**Purpose**: Fast lookup of medical codes (CPT, HCPCS, ICD-10, modifiers, DRGs)

**Endpoints**:
```http
GET    /api/v1/codes/cpt                # Search CPT codes
GET    /api/v1/codes/cpt/{code}         # Get CPT details
GET    /api/v1/codes/hcpcs/{code}       # Get HCPCS details
GET    /api/v1/codes/icd10/{code}       # Get ICD-10 details
GET    /api/v1/codes/modifiers          # List modifiers
GET    /api/v1/codes/drg/{code}         # Get DRG grouper info

# Bulk operations
POST   /api/v1/codes/validate           # Validate multiple codes
POST   /api/v1/codes/import/cpt         # Import CPT codes from CMS
POST   /api/v1/codes/import/icd10       # Import ICD-10 from CMS
```

**Data Sources**:
- **CPT Codes**: CMS HCPCS file (~44,000 codes)
- **ICD-10**: CMS ICD-10-CM file (~70,000 codes)
- **HCPCS**: CMS HCPCS Level II (~8,000 codes)
- **Modifiers**: CMS Modifier List
- **DRGs**: CMS MS-DRG Definitions

**Performance**:
- PostgreSQL with full-text search indexes
- Redis cache for frequently accessed codes
- Response time: <50ms for single code lookup
- Bulk validation: 1000 codes in <500ms

### 4. Portal Backend Service

**Purpose**: User management, dashboards, reports, settings

**Endpoints**:
```http
GET    /api/v1/users/me                 # Current user profile
PUT    /api/v1/users/me                 # Update profile
GET    /api/v1/users/me/dashboard       # Dashboard data
GET    /api/v1/users/me/notifications   # User notifications

GET    /api/v1/reports/claims           # Claims report
GET    /api/v1/reports/authorizations   # Auth report
GET    /api/v1/reports/eligibility      # Eligibility report

GET    /api/v1/settings                 # System settings
PUT    /api/v1/settings                 # Update settings
```

---

## Deployment Strategy

### Phase 1: Infrastructure Setup (Week 1)
- [ ] Create new namespaces (cho-portal, cloudhealthoffice)
- [ ] Deploy Dapr runtime
- [ ] Deploy NGINX Ingress Controller
- [ ] Configure Azure AD B2C/Entra ID
- [ ] Set up Cosmos DB containers
- [ ] Set up PostgreSQL Flexible Server for reference data

### Phase 2: Backend Services (Week 2-3)
- [ ] Containerize existing Eligibility Service
- [ ] Build Benefit Plan Config Service (C# .NET 8)
- [ ] Build Provider Directory Service (C# .NET 8)
- [ ] Build Reference Data Service (C# .NET 8)
- [ ] Build Portal Backend Service (C# .NET 8)
- [ ] Import reference data (CPT, ICD-10, HCPCS)

### Phase 3: Frontend (Week 4)
- [ ] Create Blazor Server frontend
- [ ] Migrate portal pages from static site
- [ ] Implement authentication flow
- [ ] Build dashboard components
- [ ] Integrate with backend APIs

### Phase 4: Integration & Testing (Week 5)
- [ ] Connect EDI workflows to backend APIs
- [ ] End-to-end testing
- [ ] Performance testing
- [ ] Security testing
- [ ] Load testing

### Phase 5: Migration & Cutover (Week 6)
- [ ] Deploy to production
- [ ] DNS cutover
- [ ] Monitor and optimize
- [ ] Decommission static web app

---

## Cost Estimate

| Component | Monthly Cost |
|-----------|--------------|
| AKS Cluster (existing) | ~$150 |
| Cosmos DB (5 containers, 10K RU/s) | ~$600 |
| PostgreSQL Flexible Server (General Purpose 2 vCores) | ~$100 |
| Redis Cache (Basic C1) | ~$17 |
| Application Gateway | ~$150 |
| Azure Key Vault | ~$5 |
| **Total** | **~$1,022/month** |

*(Replaces Static Web App ~$50 + former Logic Apps ~$300)*

---

## Next Steps

1. **Create namespaces and RBAC**
2. **Deploy PostgreSQL and import reference data**
3. **Build and deploy first microservice (Benefit Config)**
4. **Create Blazor frontend scaffold**
5. **Integrate authentication**

Ready to start building? 🚀
