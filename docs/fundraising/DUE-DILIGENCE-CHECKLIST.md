# Cloud Health Office - Due Diligence Preparation Checklist

**Investor Due Diligence Materials & Preparation**

---

## Overview

This checklist ensures Cloud Health Office is prepared for investor due diligence across legal, financial, technical, commercial, and team dimensions. Complete these items before first investor meetings to accelerate the fundraising process.

Cloud Health Office operates four product lines with distinct unit economics — **Public Tools** (free, funnel-top), **Transactional Services** (per-call APIs with consumer-grade signup; API key provisioning and Stripe checkout not fully wired yet), **Managed Data Services** (recurring healthcare-data subscriptions), and **Platform Engagement** (payer-scale PMPM relationships across three layers — Layer 1 — Compliance Accelerator, Layer 2 — Progressive Modernization, Layer 3 — Full CAPS Platform). Each is independently diligenceable. Diligence items below apply across product lines unless explicitly scoped; commercial and technical items most often refer to Platform Engagement (the most architecturally complex line). Canonical breakdown: [`docs/POSITIONING.md`](../POSITIONING.md) and [`docs/sales-materials/FINANCIAL-MODEL.md`](../sales-materials/FINANCIAL-MODEL.md).

---

## Critical Disclosures (Read First)

The following disclosures derive from [`docs/POSITIONING.md`](../POSITIONING.md)
§Layer 3 "what it honestly is today." They are surfaced here as
explicit DD line-items to prevent either party from navigating DD
without confronting them directly.

- [ ] **Reference customer**: no production reference customer yet;
      first pilot deployment in motion. Pilot partner name, status,
      and timeline available under NDA.
- [ ] **Test coverage on core services**: claims-service ≈24% line
      coverage, provider-service ≈12%, sponsor-service ≈13%. These
      are the lowest-covered services in the repo and the most
      operationally critical for Layer 3 at scale. Hardening plan
      and timeline available.
- [ ] **IFhirDataAdapter wiring**: the interface exists and the
      appeal adapter is real; several domain adapters are still
      mock implementations. Replacement with typed HTTP clients to
      the live domain services is in-flight.
- [ ] **Portal polish**: Blazor portal is functional for
      operational workflows today; not yet at enterprise-demo-day
      aesthetic maturity.
- [ ] **Correspondence service**: disposition letters following
      appeal decisions require a correspondence-service not yet
      shipped. Appeal decisions produce structured Kafka events
      today that the future service will consume; nothing blocks
      adding it in a dedicated PR sequence.
- [ ] **Scale testing**: platform has not been run against a
      top-tier payer's claim volume (10M+ claims/year). Architecture
      is designed for it; proof requires a pilot at that scale.

Each item above is also surfaced in the Technical and/or Commercial
DD sections below with supporting artifacts. Investors should
confirm each disclosure independently in addition to working the
full checklist.

---

## Legal Due Diligence

### Corporate Structure

- [ ] **Incorporation Documents**
  - Certificate of Incorporation (Delaware C-Corp recommended)
  - Articles of Incorporation
  - Bylaws
  - Board resolutions and consents
  - Minutes from board meetings
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Cap Table Documentation**
  - Current cap table spreadsheet
  - Stock purchase agreements
  - Option grants and option pool details
  - SAFE/convertible note agreements (if any)
  - 409A valuation (if available)
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Founder Agreements**
  - Founder stock vesting schedules
  - Founder IP assignment agreements
  - Non-compete/non-solicitation agreements
  - Employment agreements with founders
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

### Intellectual Property

- [ ] **IP Assignment Agreements**
  - All contributors signed IP assignment
  - Contractor IP assignment agreements
  - Employee invention assignment agreements
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete
  - **Location**: [Link to agreements folder]

- [ ] **Source-Available License Review**
  - BSL 1.1 license compliance confirmed
  - Third-party dependency license audit
  - No GPL or copyleft contamination
  - License compatibility matrix
  - **Reference**: [LICENSE](../LICENSE)
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Patent/Trademark Status**
  - Trademark applications (Cloud Health Office)
  - Patent strategy documentation (if any)
  - Prior art research (if claiming patents)
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

### Contracts & Agreements

- [ ] **Customer Contracts**
  - Master Service Agreement (MSA) template
  - Business Associate Agreement (BAA) template
  - Service Level Agreement (SLA) terms
  - Data Processing Agreement (DPA) template
  - Pilot program agreement template
  - **Reference**: [contracts/](../sales-materials/contracts/)
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Employment & Contractor Agreements**
  - Standard employment agreement
  - Offer letter templates
  - Contractor/consulting agreement templates
  - Employee handbook (if applicable)
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Vendor & Partner Agreements**
  - Azure partnership documentation
  - Third-party service agreements
  - Clearinghouse partnership agreements (Clearinghouse, etc.)
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

---

## Financial Due Diligence

### Financial Model & Projections

- [ ] **3-Year Financial Model**
  - Revenue projections by tier
  - Customer acquisition projections
  - Operating expense breakdown
  - Cash flow analysis
  - Path to profitability
  - **Reference**: [FINANCIAL-MODEL.md](../sales-materials/FINANCIAL-MODEL.md)
  - **Status**: ✅ Complete

- [ ] **Unit Economics Analysis**
  - Customer Acquisition Cost (CAC) breakdown
  - Lifetime Value (LTV) calculations
  - LTV:CAC ratio analysis
  - CAC payback period
  - Gross margin by tier
  - **Reference**: [FINANCIAL-MODEL.md](../sales-materials/FINANCIAL-MODEL.md)
  - **Status**: ✅ Complete

- [ ] **Pricing Justification**
  - Competitive pricing analysis
  - Value-based pricing rationale
  - Discount policy documentation
  - Volume pricing structure
  - **Reference**: [SALES-PRODUCT-OVERVIEW.md](../sales-materials/SALES-PRODUCT-OVERVIEW.md)
  - **Status**: ✅ Complete

### Historical Financials

- [ ] **Bank Statements**
  - Last 3-6 months bank statements
  - Cash position summary
  - Transaction history
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Burn Rate Analysis**
  - Monthly burn rate calculation
  - Runway estimation
  - Expense categorization
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Revenue Documentation**
  - Revenue recognition policy
  - Invoice history (if any)
  - Deferred revenue schedule
  - MRR/ARR tracking
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

---

## Technical Due Diligence

### Architecture & Infrastructure

- [ ] **Architecture Documentation**
  - System architecture overview
  - Component diagrams
  - Data flow documentation
  - Infrastructure design
  - **Reference**: [ARCHITECTURE.md](../ARCHITECTURE.md)
  - **Status**: ✅ Complete

- [ ] **Technology Stack**
  - Frontend: React, Azure Static Web Apps (planned)
  - Backend: C#, .NET, Argo Workflows on AKS
  - Infrastructure: Azure (native), Bicep IaC
  - Data: Azure Data Lake Gen2, Service Bus
  - **Reference**: [ARCHITECTURE.md](../ARCHITECTURE.md)
  - **Status**: ✅ Complete

- [ ] **Scalability Assessment**
  - Load testing results
  - Performance benchmarks
  - Scaling strategy documentation
  - Infrastructure cost projections at scale
  - **Reference**: [ARCHITECTURE.md](../ARCHITECTURE.md) (Scalability section)
  - **Status**: ✅ Documented

### Security & Compliance

- [ ] **HIPAA Compliance Documentation**
  - HIPAA compliance matrix
  - Technical safeguards implementation
  - Administrative safeguards
  - Physical safeguards (cloud)
  - **Reference**: [HIPAA-COMPLIANCE-MATRIX.md](../docs/HIPAA-COMPLIANCE-MATRIX.md)
  - **Status**: ✅ Complete

- [ ] **Security Audit Results**
  - Security hardening documentation
  - Vulnerability scan results
  - Penetration testing (if conducted)
  - Security questionnaire responses
  - **Reference**: [SECURITY-HARDENING.md](../SECURITY-HARDENING.md)
  - **Status**: ✅ Complete (Self-Assessment)

- [ ] **Third-Party Security Certifications**
  - HITRUST assessment (planned/in progress)
  - SOC 2 Type II (planned/in progress)
  - ISO 27001 (planned/in progress)
  - **Status**: 🔄 Planning Phase

### Codebase Health

- [ ] **Test Coverage**
  - Unit test coverage report
  - Integration test coverage
  - End-to-end test suite
  - Current: ~2,800 test methods across 44 test projects
  - **Status**: ✅ Complete

- [ ] **CI/CD Pipeline**
  - GitHub Actions workflows
  - Automated testing
  - Deployment automation
  - Code quality gates
  - **Reference**: [.github/workflows/](../.github/workflows/)
  - **Status**: ✅ Complete

- [ ] **Code Quality Metrics**
  - Linting configuration
  - Code review process
  - Technical debt assessment
  - Dependency management
  - **Status**: ✅ Complete

### Technology Roadmap

- [ ] **2026 Product Roadmap**
  - Quarterly milestones
  - Feature prioritization
  - Resource allocation
  - Key deliverables
  - **Reference**: [ROADMAP-2026.md](../ROADMAP-2026.md)
  - **Status**: ✅ Complete

---

## Commercial Due Diligence

### Market Analysis

- [ ] **Market Size & Opportunity**
  - TAM/SAM/SOM analysis
  - Market growth projections
  - Regulatory catalyst analysis
  - **Reference**: [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md)
  - **Status**: ✅ Complete

- [ ] **Competitive Analysis**
  - Competitive landscape overview
  - Feature comparison matrix
  - Pricing comparison
  - Positioning strategy
  - **Reference**: [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md)
  - **Status**: ✅ Complete

### Sales & Pipeline

- [ ] **Pipeline Report**
  - Qualified leads list
  - Active pilot programs
  - Opportunity stages
  - Expected close dates
  - Pipeline value
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Customer References**
  - Pilot participant contacts
  - Reference call availability
  - Testimonial quotes
  - Case study drafts
  - **Reference**: [PILOT-PROGRAM.md](../sales-materials/PILOT-PROGRAM.md)
  - **Status**: 🔄 In Progress (Pilot Program Active)

### Go-to-Market Strategy

- [ ] **Go-to-Market Plan**
  - Target customer segments
  - Sales motion description
  - Channel strategy
  - Marketing strategy
  - **Reference**: [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md)
  - **Status**: ✅ Complete

- [ ] **Partner Agreements**
  - Microsoft for Startups status
  - Azure Marketplace listing (planned)
  - Implementation partner agreements
  - Clearinghouse partnerships
  - **Reference**: [PARTNER-TARGET-LIST.md](./PARTNER-TARGET-LIST.md)
  - **Status**: 🔄 In Progress

---

## Team Due Diligence

### Founder Backgrounds

- [ ] **Founder Profiles**
  - Detailed biographies
  - LinkedIn profiles
  - Relevant experience highlights
  - Previous startup experience
  - Domain expertise evidence
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Reference Checks**
  - Professional references prepared
  - Previous employer contacts
  - Co-founder relationship history
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

### Advisory Board

- [ ] **Advisor Commitments**
  - Advisory agreement templates
  - Advisor equity grants
  - Advisor meeting schedule
  - Value-add documentation
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Advisor Profiles**
  - Healthcare industry advisors
  - Technical advisors
  - Regulatory/compliance advisors
  - Sales/GTM advisors
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

### Organization & Hiring

- [ ] **Organization Chart**
  - Current team structure
  - Reporting relationships
  - Contractor/consultant roles
  - **Status**: ⬜ Not Started / 🔄 In Progress / ✅ Complete

- [ ] **Key Hire Plans**
  - Prioritized hire list
  - Job descriptions
  - Compensation benchmarks
  - Hiring timeline
  - **Reference**: [ROADMAP-2026.md](../ROADMAP-2026.md) (Hiring section)
  - **Status**: ✅ Documented

---

## Data Room Organization

### Recommended Folder Structure

```
Cloud Health Office Data Room/
├── 1-Company Overview/
│   ├── Executive Summary.pdf
│   ├── Pitch Deck.pdf
│   └── One-Pager.pdf
├── 2-Legal/
│   ├── 2.1-Corporate/
│   │   ├── Certificate of Incorporation.pdf
│   │   ├── Bylaws.pdf
│   │   └── Board Resolutions/
│   ├── 2.2-Cap Table/
│   │   ├── Cap Table.xlsx
│   │   └── Stock Agreements/
│   ├── 2.3-IP/
│   │   ├── IP Assignment Agreements/
│   │   └── License Audit Report.pdf
│   └── 2.4-Contracts/
│       ├── MSA Template.pdf
│       ├── BAA Template.pdf
│       └── Employment Agreements/
├── 3-Financial/
│   ├── Financial Model.xlsx
│   ├── Unit Economics Analysis.pdf
│   ├── Bank Statements/
│   └── Revenue Documentation/
├── 4-Technical/
│   ├── Architecture Documentation.pdf
│   ├── Security Audit Results.pdf
│   ├── HIPAA Compliance Matrix.pdf
│   └── Roadmap.pdf
├── 5-Commercial/
│   ├── Market Analysis.pdf
│   ├── Competitive Analysis.pdf
│   ├── Pipeline Report.xlsx
│   └── Customer References/
└── 6-Team/
    ├── Founder Bios.pdf
    ├── Org Chart.pdf
    └── Advisor Profiles.pdf
```

### Data Room Platforms

**Recommended Options:**
- DocSend (good for early stage)
- Google Drive with sharing controls
- Notion (for living documents)
- Carta (integrates with cap table)

---

## Pre-Meeting Checklist

### Before First Investor Meeting

- [ ] One-pager ready for email forwarding
- [ ] Pitch deck finalized (15 slides)
- [ ] Demo environment ready
- [ ] Key metrics memorized
- [ ] Anticipated objections prepared
- [ ] Team bios updated
- [ ] Data room organized (basic)

### Before Partner Meeting

- [ ] Full data room populated
- [ ] Financial model reviewed
- [ ] Cap table current
- [ ] Customer references available
- [ ] Technical deep-dive ready
- [ ] Legal review complete

### Before Term Sheet Negotiation

- [ ] All DD items complete
- [ ] Legal counsel engaged
- [ ] Valuation comparables research
- [ ] Terms negotiation strategy
- [ ] Board composition preferences

---

## Common DD Questions Preparation

### Business Model Questions

| Question | Prepared Answer Location |
|----------|-------------------------|
| How do you make money? | [FINANCIAL-MODEL.md](../sales-materials/FINANCIAL-MODEL.md) |
| What are your unit economics? | [FINANCIAL-MODEL.md](../sales-materials/FINANCIAL-MODEL.md) |
| What's your pricing strategy? | [SALES-PRODUCT-OVERVIEW.md](../sales-materials/SALES-PRODUCT-OVERVIEW.md) |
| How does the source-available licensing model work? | [README.md](../README.md), [LICENSE](../LICENSE) |

### Market Questions

| Question | Prepared Answer Location |
|----------|-------------------------|
| How big is the market? | [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md) |
| Who are your competitors? | [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md) |
| What's the regulatory landscape? | [CMS-0057-F-COMPLIANCE.md](../docs/CMS-0057-F-COMPLIANCE.md) |
| Why now? | [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md) |

### Technical Questions

| Question | Prepared Answer Location |
|----------|-------------------------|
| How does the architecture work? | [ARCHITECTURE.md](../ARCHITECTURE.md) |
| How do you handle security? | [SECURITY-HARDENING.md](../SECURITY-HARDENING.md) |
| What's your HIPAA compliance status? | [HIPAA-COMPLIANCE-MATRIX.md](../docs/HIPAA-COMPLIANCE-MATRIX.md) |
| How do you scale? | [ARCHITECTURE.md](../ARCHITECTURE.md) |

### Team Questions

| Question | Prepared Answer Location |
|----------|-------------------------|
| What's your team's background? | [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md) |
| What key hires do you need? | [ROADMAP-2026.md](../ROADMAP-2026.md) |
| Do you have advisors? | Advisory section (above) |

---

## DD Timeline & Milestones

### Typical DD Timeline (4-8 weeks)

| Week | Activity | Deliverables |
|------|----------|--------------|
| **1** | Initial meeting, basic DD request | One-pager, pitch deck |
| **2** | Follow-up questions, data room access | Basic data room |
| **3** | Technical DD, demo | Architecture docs, demo |
| **4** | Financial DD, references | Financial model, references |
| **5** | Legal DD | Cap table, agreements |
| **6** | Partner meeting | Full presentation |
| **7** | Term sheet drafting | — |
| **8** | Negotiation, close | Signed term sheet |

---

## Resources & References

### Repository Documentation

- [ARCHITECTURE.md](../ARCHITECTURE.md) - Technical architecture
- [FINANCIAL-MODEL.md](../sales-materials/FINANCIAL-MODEL.md) - Financial projections
- [PITCH-DECK-CONTENT.md](../sales-materials/PITCH-DECK-CONTENT.md) - Full pitch deck
- [ROADMAP-2026.md](../ROADMAP-2026.md) - Product roadmap
- [SECURITY-HARDENING.md](../SECURITY-HARDENING.md) - Security documentation
- [HIPAA-COMPLIANCE-MATRIX.md](../docs/HIPAA-COMPLIANCE-MATRIX.md) - Compliance matrix

### Fundraising Materials

- [VC-TARGET-LIST.md](./VC-TARGET-LIST.md) - Target investors
- [INVESTOR-ONE-PAGER.md](./INVESTOR-ONE-PAGER.md) - Investment summary
- [INVESTOR-MEETING-SCRIPT.md](./INVESTOR-MEETING-SCRIPT.md) - Meeting script
- [WARM-INTRO-REQUEST.md](./WARM-INTRO-REQUEST.md) - Introduction templates

---

**Last Updated**: November 2024  
**Owner**: Aurelianware Fundraising Team  
**Review Frequency**: Weekly during active fundraising
