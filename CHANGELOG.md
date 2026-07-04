# Changelog

All notable changes to Cloud Health Office will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### v5.0 - Planned

**Enhanced Provider Management & Multi-Market Expansion**

- Enhanced practice management features for small-to-medium practices
- Advanced provider network analytics and reporting
- Expanded core system integrations (Epic Tapestry, additional CAPS)
- Mobile provider app (React Native) for iOS and Android
- Provider-facing scheduling and patient communication tools
- Enhanced claims scrubbing with AI-powered validation
- Multi-location practice support with centralized billing

---

## [4.4.0] - May 2026

### Claims Phase 1 — Closure

Closes the Cloud Health Office Claims-domain Phase 1 effort spanning 14 capabilities (5.1a–5.12b). Phase 1 delivers a full claim lifecycle end-to-end: submit → adjudicate (7-stage pipeline: Scrubbing 100 / Network 200 / BenefitCalculation 300 / NCCI 400 / CoB 500 / AI Examination 600 / Persistence 999) → pay (operator-initiated batched 835) → adjust (re-adjudication via predecessor chain) → reverse (operator-initiated batched negative 835).

**No new functionality.** This release is documentation-driven: capability matrix, end-to-end narrative, architectural-pattern index, Phase 2 backlog catalog, CMS-0057-F readiness assessment, canonical V1 API surface reference, and a portfolio module-status register.

#### Capabilities closed (PRs)

- **5.1a** ([#725](https://github.com/aurelianware/cloudhealthoffice/pull/725)) — Claim Identity & Versioning (versioning fields + Mongo event chain)
- **5.1b** ([#743](https://github.com/aurelianware/cloudhealthoffice/pull/743)) — Cosmos partition-key migration to `/tenantId`
- **5.2** ([#728](https://github.com/aurelianware/cloudhealthoffice/pull/728)) — Adapter pattern foundation
- **5.3** ([#729](https://github.com/aurelianware/cloudhealthoffice/pull/729)) — Claim Submission API (canonical V1 surface)
- **5.4** ([#734](https://github.com/aurelianware/cloudhealthoffice/pull/734)) — Pre-adjudication scrubbing + claims-scrubbing-service decommission
- **5.5** ([#731](https://github.com/aurelianware/cloudhealthoffice/pull/731), [#732](https://github.com/aurelianware/cloudhealthoffice/pull/732)) — Adjudication pipeline foundation
- **5.6** ([#733](https://github.com/aurelianware/cloudhealthoffice/pull/733)) — Network & credentialing enforcement
- **5.7** ([#736](https://github.com/aurelianware/cloudhealthoffice/pull/736)) — NCCI / MUE edits enforcement + projection bypass extension
- **5.8** ([#737](https://github.com/aurelianware/cloudhealthoffice/pull/737)) — Coordination of Benefits + Phase 2 hook stub
- **5.9** ([#738](https://github.com/aurelianware/cloudhealthoffice/pull/738)) — AI-Backed Examination pipeline stage
- **5.10** ([#740](https://github.com/aurelianware/cloudhealthoffice/pull/740)) — Operator-initiated batched 835 remittance + cross-service finalize
- **5.11** ([#739](https://github.com/aurelianware/cloudhealthoffice/pull/739)) — FHIR ExplanationOfBenefit projection
- **5.12a** ([#741](https://github.com/aurelianware/cloudhealthoffice/pull/741)) — Adjustment Workflow chain + re-adjudication
- **5.12b** ([#742](https://github.com/aurelianware/cloudhealthoffice/pull/742)) — ReversalRun batched 835 reversal + lifecycle wiring

#### Documentation surfaces shipped (this release)

- `docs/architecture/claims-phase-1-closer.md` — Phase 1 closer narrative (capability matrix, end-to-end lifecycle, 14-pattern architectural index, diligence-readiness checklist)
- `docs/roadmap/claims-phase-2-backlog.md` — Phase 2 backlog (48 items across 10 categories: inbound EDI, FHIR completeness, CMS-0057-F, COB priorEob, AI examiner, cross-service event-stream depth, operational, trading-partner transmission, reference-data workflows, infrastructure follow-ups)
- `docs/compliance/claims-cms-0057-f-readiness.md` — CMS-0057-F readiness posture (Phase 1 shipped vs Phase 2 required vs January 2027 mandate)
- `docs/api/claims-v1-surface.md` — canonical V1 API surface (8 controllers / 47 verbs across claims-service + payment-service customer-facing surfaces)
- `docs/status/MODULE-STATUS.md` — portfolio module-status register (initialized at Claims Phase 1 close; format mirrorable for future service-level closures)

#### Closer pattern established

Claims 5.13 establishes the **closer pattern** for service-level / domain-level Phase 1 / Phase 2 closures across Cloud Health Office. Future Provider Phase 2, BenefitPlan Phase 2, and other domain closures can mirror the structure: capability matrix → operational narrative → pattern index → phase boundary → diligence-readiness posture, with separate registries for backlog, compliance posture, and API surface.

#### No code changes

OpenAPI / Swagger surfaces continue to be served by both claims-service (via shared `AddChoInfrastructure`) and payment-service (via direct `AddSwaggerGen`) in development environments. payment-service Swagger pattern parity migration and XML-doc-driven Swagger surface enrichment are tracked as Phase 2 follow-ups.

#### Outstanding follow-ups (post-close)

- Legacy `Claims` Cosmos container deletion (~30-day retention window from 5.1b cutover; Bicep PR)
- Phase 2 sequencing per `docs/roadmap/claims-phase-2-backlog.md`. Primary near-term driver: CMS-0057-F unauthenticated patient access (January 2027 mandate).

## [4.3.0] - March 2026

### Capitation Service — PMPM Provider Payments

New microservice enabling per-member-per-month (PMPM) capitation payments from health plans to capitated providers. Structurally mirrors premium-billing-service (which collects premiums FROM sponsors) but pays TO providers.

**New Service: `capitation-service`**
- **CapitationContract** — provider agreements with 12-tier age-sex rate schedules, risk adjustment (HCC/RAF), quality withhold percentages, incentive pools, per-member and aggregate stop-loss thresholds
- **CapitationRunService** — monthly batch orchestration that fetches PCP panel rosters from coverage-service, risk scores from risk-adjustment-service, calculates proration for mid-month adds/terms, applies withholds, and generates provider payment statements
- **CapitationStatement** — provider-facing payment detail with member-level line items (base PMPM, risk score, adjusted PMPM, proration factor, gross/withhold/net), retroactive adjustments, and RecalculateTotals()
- **CapitationDisbursementService** — EFT payment lifecycle supporting NACHA ACH credits, Stripe Connect transfers, and paper checks, with ACH return handling (R01-R29 codes) and auto-retry logic
- **CapitationEraService** — X12 005010X221A1 835 ERA generation for capitation payments (CLP02=22, CLP06=CP, no SVC service lines, CAS CO-45 for withholds, PLB with WO/72/L6/FB adjustment codes)
- **NachaCreditFileService** — NACHA credit file generation (transaction codes 22/32 for checking/savings credits, SEC code CCD, service class 220, entry description CAPITATION)
- **StripeConnectService** — Stripe Transfer API integration for Connected Account payouts with webhook processing (transfer.created, transfer.reversed, payout.paid, payout.failed)
- Dual Cosmos DB / MongoDB repositories (8 files) with tenant isolation
- Kubernetes deployment manifest, Dockerfile, docker-compose entry (port 5012)

**Supporting Service Changes:**
- **coverage-service** — PcpNpi, PcpName, PcpAssignmentDate, PcpAssignmentMethod, PreviousPcpNpi fields on Coverage model; new PcpAssignmentMethod enum (AutoAssigned, MemberSelected, PlanDefault); `GET /api/v1/coverage/by-pcp/{npi}` endpoint for panel roster queries; compound indexes on (TenantId, PcpNpi, Status)
- **provider-service** — ProviderBankAccount model with EFT/Stripe Connect/check disbursement support, W-9/1099 compliance fields; DisbursementMethod, BankAccountType, TaxIdType enums; `GET/PUT /api/providers/npi/{npi}/bank-account` endpoints

**Portal — Capitation Management (3 new pages):**
- Capitation Contracts — data grid with contract#/provider/type/LOB/status/tiers/withhold, inline rate tier editor, activate/terminate actions
- Capitation Runs — create/execute runs with period selector, run list with provider count/member-months/net payable/duration, drill into statements
- Capitation Statements — filterable list, member-level breakdown with age/gender/PMPM/risk score/proration/withhold, approve/hold/void workflows, batch "Pay Approved" disbursement
- ICapitationService API client (16 methods), Capitation navigation group in sidebar

**Seed Data:**
- `seed-capitation.sh` — 3 demo contracts, 20 member PCP assignments, completed capitation run
- `seed-capitation-pcp-assignments.js` — mongosh script for Coverage.PcpNpi updates

**Tests: 176 new (163 unit + 13 smoke)**
- CapitationRunService, CapitationDisbursementService, CapitationEraService (28 X12 835 tests), NachaCreditFileService, StripeConnectService, all 4 controllers, TenantMiddleware, CosmosSerializer
- WebApplicationFactory smoke tests for full HTTP pipeline

### Metrics

| Metric                | Previous | Current  |
|-----------------------|----------|----------|
| Portal pages          | 47       | 50       |
| Microservices         | 23       | 24       |
| Service interfaces    | 20       | 21       |
| C# application lines  | ~74,800  | ~86,800  |
| Total code lines      | ~192,000 | ~204,000 |
| Automated tests       | 797      | 973      |

---

## [4.2.0] - March 2026

### Portal — Operations Depth

**New Pages:**
- Work Queues — claims examiner workflow with pend queue management by reason (NCCI, missing auth, provider not contracted, COB, medical review), priority tracking, and examiner assignment
- Appeals — search-first appeal tracking with regulatory deadline monitoring (MA 30-day standard, 72-hour expedited), appeal detail dialog with full lifecycle review
- Correspondence — outbound letter queue management (adverse determinations, EOBs, RFAIs, welcome letters) with RFAI response tracking and deadline monitoring
- Enrollment Operations — daily 834 file processing dashboard with transaction counts, adds/terms, and rejection detail

**Enhanced Pages:**
- Dashboard — added operational alerts (work queue count, pending RFAIs, appeals due), EDI transaction volume summary, and system health indicators
- Member Detail — added Accumulators tab with plan year deductible and OOP max progress bars, service-specific accumulator tracking, and recent claim activity affecting accumulators
- Claims — consolidated ClaimsNew into primary Claims page with advanced search (Claim ID, Member ID, Provider, status, date range)
- Settings — added Operating Mode tab showing per-engine Augment/Replace configuration with mode descriptions

**Portal Architecture:**
- Navigation reorganized into 6 collapsible groups (Operations, Members & Providers, Configuration, Finance, Monitoring, Admin)
- All PHI pages changed from [AllowAnonymous] to [Authorize]
- Search-first pattern enforced on all pages displaying member, claim, or authorization data (HIPAA minimum necessary)
- 5 new service interfaces and implementations (WorkQueue, Appeals, Correspondence, EnrollmentOperations, OperatingMode)
- Dashboard metrics corrected (approval rate and claims trend math)

### Engine & Infrastructure
- ClaimsScrubEngine — C# port of TypeScript validation rules with 20+ rules across 6 categories, wired into AdjudicationController
- OperatingMode engine — per-engine, per-tenant Augment/Replace toggle with AugmentResult<T> and discrepancy logging
- Seed scripts corrected to lowercase database name (cloudhealthoffice)
- Tenant onboarding checklist (TENANT_ONBOARDING_CHECKLIST.md) with Azure AD multi-tenant admin consent flow documentation
- Parameterized seed-demo-data.js (1,345 lines) for any-tenant seeding
- Parameterized seed-tenant.js for tenant provisioning

### Documentation
- README updated with accurate platform metrics and new sections
- Architecture diagram (SVG, Sentinel theme) replacing ASCII art
- Adjudication pipeline diagram showing 8-stage processing with latency
- Operating mode diagram illustrating Augment/Replace architecture
- Channel Partners section for implementation firm distribution model
- Adoption path rewritten to reference Operating Mode by name
- Codebase Scale section with line-count breakdown by language

### Metrics

| Metric                | Previous | Current  |
|-----------------------|----------|----------|
| Portal pages          | 43       | 47       |
| Calculation engines   | 7        | 9        |
| Service interfaces    | 15       | 20       |
| Portal Razor lines    | 14,622   | 16,279   |
| C# application lines  | ~72,900  | ~74,800  |
| Total code lines      | ~160,000 | ~192,000 |
| Total lines (w/ docs) | ~240,000 | ~303,000 |
| Automated tests       | 1,018    | 1,295    |

---

## [4.1.0] - February 16, 2026

### 📚 FHIR API Prominence & Developer Experience

**Commercial positioning and developer discoverability improvements**

Based on comprehensive repository assessment, this release transforms FHIR APIs from buried technical features into prominently featured commercial products.

#### OpenAPI Specifications
- **Patient Access API**: Full OpenAPI 3.1 spec for CMS-9115-F Patient Access API (FHIR R4, US Core 3.1.1+, CARIN BB)
- **Claims Scrubbing API**: Commercial pre-validation API with ROI metrics (95%+ first-pass rates)
- **Provider Access API**: Fixed filename typo (provider-accerss-api.yaml → provider-access-api.yaml)
- **Interactive Documentation**: Swagger UI embedded viewers for all APIs

#### Quickstart Guides (3 new guides)
- **CMS-0057-F Compliance** (15 min): Deploy → Test → Verify compliance before Jan 1, 2027 deadline
- **Patient Access API** (30 min): OAuth setup → Authentication → Build member portal (JS/Python/C# examples)
- **Claims Scrubbing API** (20 min): EHR integration patterns, batch validation, ROI calculator

#### Portal Redesign
- **Homepage**: CMS-0057-F deadline banner (Jan 1, 2027 urgency messaging)
- **Featured Section**: FHIR APIs now 2x grid space in prime dashboard position
- **New Page**: Dedicated API documentation hub (`api-docs.html`) with interactive viewers
- **Navigation**: Added "FHIR APIs" to main menu
- **Commercial Card**: Claims Scrubbing positioned alongside core compliance APIs

#### Impact
- **Before**: No OpenAPI specs, no quickstarts, APIs buried in src/fhir/
- **After**: 3 OpenAPI specs, 3 quickstart guides, prominent portal presence, 15-min onboarding
- **Files Changed**: 16 files, 2,895 insertions
- **Time to First API Call**: Reduced from hours to < 30 minutes

#### Documentation
- [Full Release Notes](docs/releases/v4.1.0-FHIR-API-PROMINENCE.md)
- [OpenAPI Specifications](api/openapi/)
- [Quickstart Guides](api/quickstarts/)

---

## [4.0.0] - February 11, 2026

### 🔒 Zero-Vulnerability Security Hardening

**100% vulnerability elimination** from 86 high-severity issues to absolute zero.

#### Security Fixes
- **CVE-2024-43485**: Fixed System.Formats.Asn1 RCE (8.0.0 → 8.0.1)
- **CVE-2024-21907**: Fixed Newtonsoft.Json deserialization attack (10.0.2 → 13.0.3)
- **Directory.Build.props**: Global transitive dependency enforcement
- **59 package updates**: Azure.Identity, Azure.Core, Microsoft.Azure.Cosmos, MudBlazor, Stripe.net, Swashbuckle, and 50+ more

#### Multi-Tenant SaaS Isolation
- **TenantContextService**: Maps Azure AD tenant → CHO tenant via subscription lookup
- **TenantHttpMessageHandler**: Injects `X-Tenant-ID` header on all backend API calls
- **Portal Isolation**: Prevents cross-tenant data leakage (CRITICAL security fix)
- **Dynamic UI**: Shows actual tenant name with demo/production badges
- **Logout Functionality**: Proper Microsoft Identity sign-out flow

#### Cloud Portability Infrastructure (Feature Branch)
- **CloudHealthOffice.Infrastructure Package**: Cloud-agnostic `IDocumentStore<T>` interface
- **Azure Implementation**: `CosmosDocumentStore<T>` (current production)
- **DigitalOcean Implementation**: `MongoDocumentStore<T>` (65% cost savings)
- **Reference Implementation**: member-service compiles with multi-cloud support
- **GitHub Actions Workflow**: 3-click toggles for Azure/DigitalOcean deployment
- **Status**: Available in `feature/multi-cloud-infrastructure` branch for testing

#### SFTP Trading Partner Integration
- **New Tenant**: clouddentaloffice (dental claims EDI)
- **Endpoint**: 20.115.193.245:22 (pending DNS: sftp.cloudhealthoffice.com)
- **Folder Structure**: /dental-claims/inbound/837/, /outbound/835/, /outbound/277/
- **Credentials**: Stored in Azure Key Vault

#### Infrastructure Updates
- **Azure Permissions**: Added Application Administrator & User Access Administrator roles
- **Deployment Gates**: Pre-approval checks in GitHub Actions
- **PII/PHI Scanner**: Configured to allow test data patterns
- **Logic Apps Migration**: Disabled deployment (moved to Argo workflows)

#### Package Highlights
- Azure.Identity: 1.12.1 → 1.13.1
- Azure.Core: 1.42.0 → 1.44.1
- Microsoft.Azure.Cosmos: 3.42.0 → 3.45.0
- MudBlazor: 7.20.0 → 8.4.0
- Stripe.net: 46.4.0 → 47.0.0
- Swashbuckle.AspNetCore: 6.5.0 → 10.1.2

#### Breaking Changes
- **Logic Apps Deployment**: Disabled in deploy.yml (use Argo workflows)
- **Multi-Tenant Headers**: Portal now sends `X-Tenant-ID` on all API calls (all services already compliant)

#### Known Issues
- DNS Configuration: sftp.cloudhealthoffice.com not yet pointed to 20.115.193.245
- Mock Data Fallback: Portal shows mock data when backend unavailable (configurable via `Portal.UseMockDataFallback`)
- Stripe.net Warning: NU1603 - Package 46.4.0 not found, resolved to 47.0.0 (non-breaking)

**Production Readiness**: ✅ Multi-Tenant Isolation | ✅ Security Hardening | ✅ HIPAA Controls | ✅ Zero Vulnerabilities

**Documentation**: [RELEASE-v4.0.0.md](RELEASE-v4.0.0.md), [MULTI-CLOUD-SETUP.md](MULTI-CLOUD-SETUP.md), [MULTI-CLOUD-DEPLOYMENT-GUIDE.md](MULTI-CLOUD-DEPLOYMENT-GUIDE.md)

---

## [3.0.0] - February 2026

### Dual-Market Healthcare Integration Platform

Cloud Health Office v3.0.0 is production-ready for both **health payers** (legacy system augmentation) and **healthcare providers** (practice management with direct EDI). This release delivers multi-cloud independence, CMS-0057-F compliance, and commercial launch readiness.

### Added

#### Multi-Cloud & Cloud Independence (December 2025)
- **Kubernetes/Helm Deployment**: Deploy Cloud Health Office to AKS, EKS, GKE, or any Kubernetes cluster
- **Argo Workflows Migration**: Cloud-native workflow orchestration replacing Azure Logic Apps
- **Apache Kafka Integration**: Cloud-agnostic messaging replacing Azure Service Bus
- **HashiCorp Vault Support**: Open-source secrets management as alternative to Azure Key Vault
- **Multi-Cloud Deployment Guide**: Comprehensive documentation for deploying across cloud providers

**Documentation**: [MULTI-CLOUD-DEPLOYMENT.md](./docs/MULTI-CLOUD-DEPLOYMENT.md), [ARGO-MIGRATION-GUIDE.md](./docs/ARGO-MIGRATION-GUIDE.md)

#### Argo Workflows for X12 EDI Processing (December 2025)
- **X12 275 Attachment Ingest Workflow**: Kubernetes-native SFTP polling and processing
- **X12 278 Authorization Request Workflow**: Cloud-agnostic prior auth handling
- **X12 277 RFAI Response Workflow**: Event-driven response generation via Kafka
- **X12 278 Replay Workflow**: Deterministic replay from Kafka offsets
- **Container Images**: X12 parser, encoder, SFTP fetcher, metadata extractor, Kafka publisher
- **Argo Events Configuration**: SFTP polling and Kafka event sources with sensors

**Documentation**: [ARGO-OPERATIONS.md](./docs/ARGO-OPERATIONS.md)

#### Azure Marketplace Readiness (December 2025)
- **Managed Application Plan**: ARM template deploying full Cloud Health Office stack
- **SaaS Plan with Meter-Based Billing**: Per-transaction pricing (837, 278, 275, FHIR API calls)
- **3-Tier Pricing**: Starter, Professional, Enterprise — [Contact sales](mailto:sales@cloudhealthoffice.com) for pricing
- **Partner Center Metadata**: Complete offer listing and marketing assets
- **Legal Documents**: Privacy policy, SLA (99.5%-99.95% uptime), support terms
- **Marketplace Icons**: Sentinel-branded SVG assets for all required sizes

**Documentation**: [marketplace/README.md](./marketplace/README.md)

#### Commercial Launch Materials (December 2025)
- **Sales Product Overview**: 2-page executive summary with competitive positioning
- **ROI Calculator**: TCO analysis and 5-year savings projections
- **Case Study Template**: Reusable template for pilot customer success stories
- **Financial Model**: 3-year projections with unit economics
- **Pitch Deck Content**: 15-slide framework for investor/customer presentations
- **Pilot Program**: 60-day structured pilot with success criteria
- **Sales Email Templates**: 5 targeted outreach templates
- **Marketing Landing Page Copy**: Conversion-optimized content

**Documentation**: [sales-materials/README.md](./sales-materials/)

#### VC Fundraising Strategy (December 2025)
- **VC Target List**: 12+ prioritized healthcare and SaaS VCs with investment thesis fit
- **Investor One-Pager**: Single-page investment summary
- **Due Diligence Checklist**: Legal, financial, technical, commercial preparation
- **Strategic Partner Targets**: 50+ partners including Microsoft, SIs, technology vendors
- **Investor Meeting Script**: 30-minute pitch framework
- **Warm Intro Templates**: 4 introduction request templates
- **Alternative Funding**: Grants (SBIR), RBF, venture debt, strategic investors
- **PR Strategy**: Thought leadership, podcasts, conferences, LinkedIn

**Documentation**: [fundraising/README.md](./fundraising/)

#### Microservices Architecture (December 2025)
- **Eligibility Service**: Azure Container Apps + Dapr with dual X12 270/271 and FHIR interface
- **ClaimRiskScorer Azure Function**: ML-powered fraud/abuse scoring (0-100) with PyTorch
- **Provider Directory API Logic App**: FHIR endpoints with NPPES NPI integration
- **Prior Auth API Logic App**: Da Vinci PAS CDex flow with 72-hour SLA tracking
- **Cosmos DB Integration**: PriorAuthorizations and ProviderDirectory containers

**Documentation**: [services/eligibility-service/README.md](./services/eligibility-service/)

#### CMS-0057-F Compliance Dashboard (December 2025)
- **Azure Monitor Workbook**: Real-time compliance metrics visualization
- **Patient Access API Tracking**: Enablement percentage with daily trends
- **Prior Auth SLA Monitoring**: 72-hour urgent and 7-day standard response tracking
- **Error Rate Analysis**: Transaction-level error tracking for 270/271, 278, 837
- **PHI Audit Trail**: Security operations monitoring via Application Insights

**Documentation**: [docs/AZURE-MONITOR-DASHBOARDS.md](./docs/AZURE-MONITOR-DASHBOARDS.md)

#### Migration Wizard (December 2025)
- **Blazor Web App**: `/tools/migration-wizard` for legacy system migration
- **Claims Backend SOAP Integration**: Paginated export via Open Access APIs
- **Cosmos DB Export**: Batch upsert for Members, ProviderDirectory, BenefitPlans
- **Mapping Report Generator**: 95%+ auto-match with field-level validation
- **One-Click API Cutover**: Routing key flip via Azure API Management
- **Azure Key Vault Integration**: Secure credential management

**Documentation**: [tools/migration-wizard/README.md](./tools/migration-wizard/)

#### 2026 Product Roadmap (December 2025)
- **Quarterly Milestones**: Q1-Q4 2026 with CMS compliance timeline
- **Microservice Releases**: eligibility-service v2.0, prior-auth-service v2.0, claims-service v1.0, remittance-service v1.0
- **Community Targets**: 500→7,500 GitHub stars, 15→150 contributors
- **OKRs**: Measurable success criteria for compliance, adoption, community, and AI

**Documentation**: [ROADMAP-2026.md](./ROADMAP-2026.md)

#### CMS-0057-F Whitepaper (December 2025)
- **Executive Whitepaper**: 7-page document for payer CIOs/CTOs
- **ROI Analysis**: 522% Year 1 ROI, 4.2-month payback period
- **TCO Comparison**: $16.7M legacy vs $2.6M Cloud Health Office (5-year)
- **Implementation Roadmap**: 12-16 week phased timeline
- **Mermaid Visualizations**: Gantt charts, TCO comparison, cost breakdown

**Documentation**: [docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.md](./docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.md)

#### Community Governance (December 2025)
- **CONTRIBUTING.md**: Enhanced with DCO and CLA instructions
- **CODE_OF_CONDUCT.md**: Contributor Covenant 2.1
- **GOVERNANCE.md**: Steering committee election process
- **Issue Templates**: Feature request and bug report YAML forms
- **PR Automation**: Auto-labeling and reviewer assignment workflows

#### Platform Improvements (December 2025)
- **Vendor-Agnostic Refactoring**: Removed 1,295 vendor-specific references across 185 files
- **Container Build Workflow Fix**: Corrected image tags for vulnerability scanning
- **patient_access_api Workflow Fix**: Added missing `kind` and `parameters` keys

### Changed

- Updated README.md with Kubernetes deployment badge and dual architecture options
- Updated ARCHITECTURE.md with deployment options section
- Updated ROADMAP.md to reflect multi-cloud strategy progress (40% complete)
- Helm charts updated with HashiCorp Vault integration settings

### Fixed

- Container build workflow image tag mismatch for Trivy scanner
- patient_access_api workflow.json missing required keys
- PHI compliance issues with HTTPS enforcement for Vault URLs

### Security

- Storage Account networkAcls defaultAction set to "Deny" for HIPAA compliance
- Key Vault networkAcls defaultAction set to "Deny" for HIPAA compliance
- Managed Identity exclusively used for Cosmos DB/Event Grid access (no keys)
- All 424 tests pass with zero security vulnerabilities

### PRs Merged (17 since v2.0.0)

| PR | Title | Category |
|----|-------|----------|
| [#116](https://github.com/aurelianware/cloudhealthoffice/pull/116) | Remove vendor-specific references | Platform |
| [#115](https://github.com/aurelianware/cloudhealthoffice/pull/115) | Add multi-cloud deployment documentation and HashiCorp Vault integration | Multi-Cloud |
| [#114](https://github.com/aurelianware/cloudhealthoffice/pull/114) | Fix image tag mismatch in container build workflow | CI/CD |
| [#113](https://github.com/aurelianware/cloudhealthoffice/pull/113) | Migrate X12 EDI processing to Argo Workflows and Kafka | Multi-Cloud |
| [#112](https://github.com/aurelianware/cloudhealthoffice/pull/112) | Add VC fundraising strategy and materials | Commercial |
| [#111](https://github.com/aurelianware/cloudhealthoffice/pull/111) | Add comprehensive commercial launch materials | Commercial |
| [#110](https://github.com/aurelianware/cloudhealthoffice/pull/110) | Enhance CMS-0057-F whitepaper with ROI analysis and visualizations | Documentation |
| [#109](https://github.com/aurelianware/cloudhealthoffice/pull/109) | Add CMS-0057-F compliance whitepaper for payer executives | Documentation |
| [#108](https://github.com/aurelianware/cloudhealthoffice/pull/108) | Add 2026 product roadmap with CMS compliance milestones | Roadmap |
| [#107](https://github.com/aurelianware/cloudhealthoffice/pull/107) | Add community governance files, issue templates, and PR automation | Governance |
| [#106](https://github.com/aurelianware/cloudhealthoffice/pull/106) | Add Blazor migration wizard for legacy platforms to Cloud Health Office | Tools |
| [#105](https://github.com/aurelianware/cloudhealthoffice/pull/105) | Add Azure Marketplace offer structure with managed app and SaaS plans | Marketplace |
| [#104](https://github.com/aurelianware/cloudhealthoffice/pull/104) | Add ClaimRiskScorer Azure Function for 837 fraud/abuse risk scoring | Microservices |
| [#103](https://github.com/aurelianware/cloudhealthoffice/pull/103) | Add eligibility-service with dual X12 270/271 and FHIR interface | Microservices |
| [#102](https://github.com/aurelianware/cloudhealthoffice/pull/102) | Add CMS-0057-F Compliance Dashboard workbook for Azure Monitor | Compliance |
| [#101](https://github.com/aurelianware/cloudhealthoffice/pull/101) | Fix patient_access_api workflow missing required keys | Bug Fix |
| [#100](https://github.com/aurelianware/cloudhealthoffice/pull/100) | Add ProviderDirectoryApi and PriorAuthApi Logic Apps with NPPES integration | Microservices |

---

## [2.0.0] - 2025-11-28

### FHIR Frontier Forge

Complete CMS-0057-F compliance with production-ready FHIR R4 APIs, delivered 18 months ahead of the January 1, 2027 deadline.

### Added

#### V2 Release Notes Infrastructure (November 2024)
- **Release Notes Portal**: New `site/release-notes.html` with delivered features, sandbox testing, and early adopter signup
- **Documentation Updates**: Enhanced CMS-0057-F compliance documentation with post-FHIR implementation status
- **Site Navigation**: Added release notes links across all platform landing pages
- **V2 Announcements**: Updated site/index.html with v2 banners and CMS-0057-F/FHIR API announcements

**Documentation**: [Release Notes](./site/release-notes.html), [CMS-0057-F Compliance](./docs/CMS-0057-F-COMPLIANCE.md)

#### Complete FHIR R4 API Coverage (November 2024)
- **X12 837 → FHIR Claim**: Professional, Institutional, and Dental claims with Da Vinci PDex profiles
- **X12 278 → FHIR ServiceRequest**: Prior authorization with Da Vinci PAS/CRD compliance
- **X12 835 → FHIR ExplanationOfBenefit**: Remittance advice with complete adjudication details
- **X12 275 → FHIR DocumentReference**: Clinical attachments and supporting documentation
- **CMS-0057-F Compliance Checker**: Automated validation of data classes and timeline requirements
- **Azure FHIR Validator**: Profile validation integration with Azure API for FHIR
- **US Core + Da Vinci IGs**: Full PDex, PAS, CRD, DTR implementation guide conformance
- **45 Comprehensive Tests**: All FHIR mappers validated with 100% pass rate
- **Zero External Dependencies**: Secure core mappers with no runtime vulnerabilities

**Compliance Status**: Ready for January 1, 2027 CMS-0057-F deadline  
**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md), [CMS-0057-F-COMPLIANCE.md](./docs/CMS-0057-F-COMPLIANCE.md)

#### Provider Access API (November 2024)
- **Real-Time Patient Data Access**: FHIR R4 API for providers with patient authorization
- **SMART on FHIR Scopes**: `user/*.read`, `system/*.read` for provider/system access
- **NPI-Based Authorization**: Provider identity verification and access control
- **Consent Management**: Patient authorization tracking and revocation support

**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md#provider-access-api)

#### Payer-to-Payer Data Exchange (November 2024)
- **Bulk FHIR Export**: `$export` operation for efficient data exchange
- **5-Year Historical Data**: Configurable retention via Azure Data Lake lifecycle policies
- **Enrollment-Triggered Transfers**: Automated data exchange on member transitions
- **USCDI v1/v2 Coverage**: Complete data class support for interoperability

**Documentation**: [CMS-0057-F-COMPLIANCE.md](./docs/CMS-0057-F-COMPLIANCE.md#payer-to-payer-api)

#### Config-to-Workflow Generator (November 2024)
- **Zero-Code Payer Onboarding System**: Transform JSON configuration into complete deployment artifacts
- **Interactive Configuration Wizard**: Guided setup experience completing in <5 minutes
- **TypeScript-Based Generator**: 700+ lines of automation code with comprehensive validation
- **30+ Handlebars Template Helpers**: String, array, conditional, JSON, math, date, type checking utilities
- **Workflow Templates**: Automatic generation of Logic App workflow.json files
- **Infrastructure Templates**: Bicep templates with parameters and deployment scripts
- **Documentation Generation**: Payer-specific DEPLOYMENT.md, CONFIGURATION.md, TESTING.md
- **Example Configurations**: Medicaid MCO and Regional Blues templates included
- **23-Test Comprehensive Suite**: All passing with 100% validation coverage
- **CLI Tool**: Command-line interface with generate, validate, template, list commands

**Documentation**: [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md), [IMPLEMENTATION-SUMMARY.md](./IMPLEMENTATION-SUMMARY.md)

#### FHIR R4 Integration (November 2024)
- **X12 270 → FHIR R4 Mapping**: Transform eligibility inquiries to Patient & CoverageEligibilityRequest
- **CMS Patient Access API Compliance**: Ready for CMS-9115-F requirements (14 months ahead of roadmap)
- **US Core Implementation**: US Core Patient profile v3.1.1 compliant
- **Standards Support**: HIPAA X12 270 (005010X279A1), HL7 FHIR R4 (v4.0.1)
- **Zero External Dependencies**: Core mapper with no runtime vulnerabilities
- **19 Comprehensive Tests**: 100% pass rate, covers all mapping scenarios
- **Production-Ready Security**: Secure examples using native fetch and Azure Managed Identity
- **Service Type Mapping**: 100+ X12 service type codes supported
- **Subscriber & Dependent Support**: Complete demographics handling

**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md), [FHIR-SECURITY-NOTES.md](./docs/FHIR-SECURITY-NOTES.md), [FHIR-IMPLEMENTATION-SUMMARY.md](./FHIR-IMPLEMENTATION-SUMMARY.md)

#### ValueAdds277 Enhanced Claim Status (November 2024)
- **60+ Enhanced Response Fields**: Comprehensive claim intelligence beyond basic status
- **Financial Fields (8)**: BILLED, ALLOWED, PAID, COPAY, COINSURANCE, DEDUCTIBLE, DISCOUNT, PATIENT_RESPONSIBILITY
- **Clinical Fields (4)**: Diagnosis codes, procedure codes, service dates, place of service
- **Demographics (4 objects)**: Patient, subscriber, billing provider, rendering provider details
- **Remittance Fields (4)**: Check/EFT details, payment date, trace numbers
- **Service Line Details**: 10+ fields per service line with configurable granularity
- **Integration Flags (6)**: Cross-module workflows for Appeals, Attachments, Corrections, Messaging, Chat, Remittance
- **Unified Configuration**: Complete valueAdds277 configuration in payer config schema
- **Premium Product Capability**: $10k/year additional revenue per payer
- **Provider ROI**: 7-21 minutes saved per claim lookup ($69,600/year for 1,000 lookups/month)

**Documentation**: [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md), [ECS-INTEGRATION.md](./docs/ECS-INTEGRATION.md)

#### Security Hardening (November 2024)
- **Premium Key Vault Infrastructure**: HSM-backed keys with FIPS 140-2 Level 2 compliance
- **Private Endpoints**: Complete network isolation for Storage, Service Bus, Key Vault
- **VNet Integration**: Logic Apps deployed in private virtual network
- **PHI Masking**: DCR-based transformation rules for Application Insights
- **Customer-Managed Keys**: Optional BYOK for regulatory requirements
- **Data Lifecycle Management**: 7-year retention with automated tier transitions (Hot → Cool → Archive)
- **Storage Cost Optimization**: 94% reduction ($463/mo → $29/mo) with lifecycle policies
- **HTTP Endpoint Authentication**: Azure AD Easy Auth for replay278 endpoint
- **Audit Logging**: 365-day retention with compliance queries
- **4 Bicep Modules**: keyvault.bicep, networking.bicep, private-endpoints.bicep, cmk.bicep (649 lines)
- **HIPAA Compliance**: 100% technical safeguards (§ 164.312) documented and implemented

**Security Score**: 9/10 (Target achieved)

**Documentation**: [SECURITY-HARDENING.md](./SECURITY-HARDENING.md), [HIPAA-COMPLIANCE-MATRIX.md](./docs/HIPAA-COMPLIANCE-MATRIX.md), [SECURITY-IMPLEMENTATION-SUMMARY.md](./SECURITY-IMPLEMENTATION-SUMMARY.md)

#### Gated Release Strategy (November 2024)
- **Pre-Approval Security Validation**: TruffleHog secret detection, PII/PHI scanning, artifact validation
- **UAT Approval Workflow**: 1-2 required approvers, triggers on `release/*` branches
- **PROD Approval Workflow**: 2-3 required approvers, manual dispatch from `main` only
- **Security Context for Approvers**: Scan results visible before approval decision
- **Automated Audit Logging**: Complete deployment history with compliance queries
- **Communication Strategy**: Stakeholder notification matrix with pre/post-deployment templates
- **Emergency Procedures**: Hotfix approval process with 30-minute SLA
- **Rollback Automation**: Automatic rollback-on-failure for UAT, documented procedures for PROD
- **Health Checks**: Post-deployment validation of Logic Apps, Storage, Service Bus, Application Insights
- **Metrics & Reporting**: Deployment success rate, approval times, rollback incidents

**Documentation**: [DEPLOYMENT-GATES-GUIDE.md](./DEPLOYMENT-GATES-GUIDE.md), [GATED-RELEASE-IMPLEMENTATION-SUMMARY.md](./GATED-RELEASE-IMPLEMENTATION-SUMMARY.md)

#### Onboarding Enhancements (November 2024)
- **Interactive Configuration Wizard**: Step-by-step guided configuration with validation (scripts/cli/interactive-wizard.ts)
- **Synthetic 837 Claim Generator**: PHI-safe test data for 837P and 837I claims (scripts/utils/generate-837-claims.ts)
- **Azure Deploy Button Template**: One-click sandbox deployment via azuredeploy.json
- **E2E Test Suite**: Comprehensive health checks with JSON reporting (scripts/test-e2e.ps1)
- **CI/CD PHI Validation**: 18 automated tests prevent PHI exposure (.github/workflows/phi-validation.yml)
- **Troubleshooting FAQ**: 60+ solutions across 9 categories (TROUBLESHOOTING-FAQ.md)
- **Documentation Suite**: QUICKSTART.md, enhanced ONBOARDING.md with 3 deployment options

**Onboarding Time Reduction**: 96% (2-4 hours → <5 minutes)
**Configuration Error Reduction**: 87.5% (40% error rate → <5%)
**Test Coverage Increase**: 41% (44 tests → 62 tests)

**Documentation**: [QUICKSTART.md](./QUICKSTART.md), [ONBOARDING.md](./ONBOARDING.md), [ONBOARDING-ENHANCEMENTS.md](./ONBOARDING-ENHANCEMENTS.md)

#### Sentinel Branding (November 2024)
- **Complete Visual Identity**: Sentinel logo with holographic/neon circuit veins aesthetic
- **Branding Guidelines**: Comprehensive standards document (BRANDING-GUIDELINES.md)
- **Absolute Black Design**: Primary color palette with neon cyan (#00ffff) and green (#00ff88)
- **Segoe UI Bold Typography**: Consistent font usage across all materials
- **Landing Page Transformation**: Complete redesign with Sentinel aesthetic
- **Repository-Wide Enforcement**: Updated all references and documentation

**Documentation**: [BRANDING-GUIDELINES.md](./docs/BRANDING-GUIDELINES.md), [BRANDING-IMPLEMENTATION-SUMMARY.md](./BRANDING-IMPLEMENTATION-SUMMARY.md)

### Changed

- Enhanced README.md with comprehensive features section and new capabilities
- Expanded QUICKSTART.md with post-v1.0.0 feature details
- Updated DEPLOYMENT.md with security hardening deployment section
- Enhanced DEPLOYMENT-SECRETS-SETUP.md with Key Vault migration procedures

### Fixed

- Null safety improvements in configuration validator
- JSON validation for all generated artifacts
- Workflow structure validation for Logic Apps Standard requirements

## [1.0.0] - 2025-11-21

### The Sentinel Has Awakened

This is the first production release of Cloud Health Office — the source-available, Azure-native, HIPAA-engineered platform that ends decades of payer EDI pain.

### Added

#### Core Platform
- **Multi-Tenant SaaS Architecture**: Configuration-driven platform supporting unlimited health plans
- **CLI Onboarding Wizard**: Complete deployment from worksheet to production in <45 minutes
- **Zero-Code Payer Onboarding**: Add new payers via JSON configuration without custom development
- **Backend-Agnostic Design**: Works with any claims system (core admin systems, custom platforms, modern cloud solutions)

#### EDI Transaction Processing
- **275 Attachments**: Clinical and administrative attachment processing with file validation
- **277 RFAI**: Request for Additional Information outbound workflow
- **278 Authorizations**: Prior authorization requests (inpatient, outpatient, referrals)
- **278 Authorization Inquiry (X215)**: Real-time status checks for existing authorizations
- **278 Replay Endpoint**: HTTP endpoint for deterministic 278 transaction replay
- **837 Claims**: Professional, Institutional, and Dental claims submission support
- **270/271 Eligibility**: Real-time eligibility verification with 6 search methods
- **276/277 Claim Status**: Claim status inquiries with date range filtering
- **Appeals Processing**: Appeals submission and tracking with 8 sub-statuses
- **ECS (Enhanced Claim Status)**: Advanced claim status with extended data and 4 query methods

#### Clearinghouse Integration
- **Clearinghouse Integration**: Native SFTP and API connectivity
- **Change Healthcare Support**: Ready for integration
- **Optum 360 Support**: Ready for integration
- **Inovalon Support**: Ready for integration
- **Direct Payer Endpoints**: Configuration-driven connectivity

#### Security & Compliance
- **Zero-Trust Architecture**: Private-endpoint-only, no public IPs
- **Azure Key Vault Premium**: HSM-backed keys (FIPS 140-2 Level 2)
- **Private Endpoints**: VNet integration for Storage, Service Bus, Key Vault
- **PHI Masking**: DCR-based redaction in Application Insights
- **HIPAA Compliance**: 100% technical safeguards addressed
- **Automated Secret Rotation**: API keys and credentials rotate automatically
- **7-Year Data Retention**: Automated lifecycle management with tier transitions
- **Audit Logging**: 365-day retention in Log Analytics

#### Infrastructure as Code
- **Complete Bicep Templates**: All Azure resources defined in source
- **Logic Apps Standard Workflows**: 15+ production-ready workflows
- **Modular Security Components**: Key Vault, networking, private endpoints, CMK
- **Multi-Environment Support**: DEV/UAT/PROD configurations
- **GitHub Actions Pipelines**: Automated deployment with approval gates

#### Developer Experience
- **Configuration Schema**: JSON Schema Draft-07 with 200+ validation rules
- **TypeScript Interfaces**: Type-safe configuration handling
- **OpenAPI Specifications**: Complete API documentation
- **Example Configurations**: Medicaid MCO and Regional Blues templates
- **Comprehensive Documentation**: 20+ detailed guides

#### claims backend Integration (First in Source-Available)
- **Real-Time Correlation APIs**: Link attachments to claims
- **Appeals Registration**: Direct integration with claims backend Appeals API
- **Authorization Processing**: Complete authorization lifecycle management
- **Eligibility Verification**: Member eligibility checks with retry logic
- **Retry Logic**: 4 retries @ 15-second intervals for API calls

#### Monitoring & Observability
- **Application Insights Integration**: Telemetry and distributed tracing
- **PHI-Safe Logging**: Automated masking of sensitive data
- **Custom Metrics**: Authorization decisions, claim status, appeal tracking
- **Health Checks**: Automated verification post-deployment
- **Dead-Letter Queues**: Failed message handling and replay

### Key Highlights

- **Onboarding time reduction**: 6–18 months (legacy) → <1 hour
- **Professional services cost elimination**: $500k–$2M → $0 (bring-your-own-subscription)
- **First production-grade claims backend REST correlation** in source-available healthcare IT
- **Complete source code transparency**: No black boxes, fully auditable
- **Azure Marketplace ready**: Prepared for Managed Application publishing

### Platform Specifications

- **Deployment Target**: Azure (Logic Apps Standard, Data Lake Gen2, Service Bus)
- **Runtime**: Logic Apps Standard (WS1+ SKU)
- **Storage**: Azure Data Lake Storage Gen2 with hierarchical namespace
- **Messaging**: Service Bus Standard tier with topics
- **Security**: Premium Key Vault with HSM-backed keys
- **Monitoring**: Application Insights with PHI masking
- **Language**: TypeScript (generator), Bicep (infrastructure), JSON (workflows)

### Documentation

Complete documentation suite includes:
- **CONTRIBUTING.md**: Development workflow and setup
- **ARCHITECTURE.md**: System architecture and data flows
- **DEPLOYMENT.md**: Step-by-step deployment procedures
- **SECURITY.md**: HIPAA compliance and security practices
- **TROUBLESHOOTING.md**: Common issues and solutions
- **BRANDING-GUIDELINES.md**: Sentinel brand identity standards

### Breaking Changes

N/A - First release

### Security

- All dependencies audited and up-to-date
- No known vulnerabilities in production dependencies
- HIPAA compliance validated for all PHI handling paths
- Security hardening guide included in SECURITY.md

### Known Limitations

- Azure-only deployment (AWS/GCP support planned for Q1 2025)
- Integration Account X12 schemas must be manually imported post-deployment
- API connections require manual authentication configuration
- Azure AD Easy Auth configuration required for replay endpoints

### Migration Guide

N/A - First release

### Contributors

Special thanks to all contributors who made this release possible.

### License

BSL 1.1 - see [LICENSE](LICENSE) file

---

## The Sentinel Has Awakened

The monolith has landed.  
Legacy EDI integration is now optional.

**Just emerged from the void.**

Star ★ the repo if you believe payers deserve better than 1990s technology in 2025.

---

[4.2.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.2.0
[4.1.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.1.0
[4.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.0.0
[3.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v3.0.0
[2.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v2.0.0
[1.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v1.0.0
