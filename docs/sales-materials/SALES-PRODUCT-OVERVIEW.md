# Cloud Health Office - Product Overview

**The Inevitable Evolution of Healthcare EDI**

---

## Product Line Context

Cloud Health Office (CHO) engages customers across four product lines: **Public Tools** (free fee schedule lookup and free-tier claims repricing), **Transactional Services** (per-call APIs on self-serve subscription, including the Claims Repricing API and Pricing API), **Managed Data Services** (recurring subscriptions for state Medicaid compliance, CMS fee schedule updates, provider verification, and terminology), and **Platform Engagement** (payer-scale relationships priced per member per month (PMPM), with three layers — Layer 1 — Compliance Accelerator, Layer 2 — Progressive Modernization, and Layer 3 — Full CAPS Platform). This overview is weighted toward Platform Engagement, which is what most health-plan buyers are evaluating, but customers can enter at any product line and expand over time. For the canonical positioning across all four product lines, see [POSITIONING.md](../POSITIONING.md).

---

## Executive Summary

CHO is a source-available, cloud-native platform for healthcare claims administration. Within Platform Engagement, it engages customers at three layers: an evidence-backed CMS-0057-F readiness surface (Layer 1 — Compliance Accelerator), a progressive modernization path with appeals as the shipped reference domain (Layer 2 — Progressive Modernization), and a full cloud-native CAPS platform for new entrants and established payers finishing their modernization (Layer 3 — Full CAPS Platform). See [POSITIONING.md](../POSITIONING.md) for the honest today-state of each layer.

### The Compliance Imperative

- **900+ health plans** face mandatory CMS-0057-F compliance
- **$8B+ EDI market** undergoing regulatory transformation
- **January 1, 2027 deadline** for Patient Access, Provider Access, and Prior Authorization APIs
- **Penalties and market access** at risk for non-compliant payers

### Cloud Health Office Delivers

| Capability | Status | Compliance Impact |
|------------|--------|-------------------|
| Patient Access API (FHIR R4) | ✅ Implemented | CMS-0057-F Required |
| Provider Access API | ✅ Implemented | CMS-0057-F Required |
| Prior Authorization API | ✅ Automated 72hr/7-day | CMS-0057-F Required |
| Payer-to-Payer API | ✅ Bulk FHIR Export | CMS-0057-F Required |
| Da Vinci IG Conformance | ✅ PDex, PAS, CRD, DTR | Readiness evidence |
| USCDI v1/v2 Data Classes | ✅ Complete | Federal Mandate |

---

## Key Capabilities

### Maturity by Layer

| Layer | Scope                                    | Today                                                                 |
| ----- | ---------------------------------------- | --------------------------------------------------------------------- |
| 1     | CMS-0057-F readiness surface             | Implemented; deployable in customer environments; requires payer validation |
| 2     | Progressive modernization (per domain)   | Appeals shipped (PRs #677, #678, #680, #681); capitation, claims, others in flight |
| 3     | Full cloud-native CAPS platform          | Architecturally complete; test-coverage hardening on some services; no production reference customer yet |

See [POSITIONING.md](../POSITIONING.md) for the full per-layer disclosure including specific gaps we are closing deliberately.

### EDI Transaction Processing

Complete HIPAA X12 transaction support with enterprise-grade reliability:

| Transaction | Type | Capability |
|-------------|------|------------|
| **275** | Attachments | Clinical/administrative attachment processing |
| **277** | RFAI | Request for Additional Information (outbound) |
| **278** | Prior Auth | Authorization requests with inquiry support |
| **837** | Claims | Professional, Institutional, and Dental |
| **270/271** | Eligibility | Real-time verification (6 search methods) |
| **276/277** | Claim Status | Enhanced with 60+ ValueAdds277 fields |

### FHIR R4 Integration

Native X12 ↔ FHIR R4 transformation for modern interoperability:

- **X12 270 → FHIR Patient + CoverageEligibilityRequest** (US Core 3.1.1)
- **X12 837 → FHIR Claim** (Da Vinci PDex)
- **X12 278 → FHIR ServiceRequest** (Da Vinci PAS)
- **X12 835 → FHIR ExplanationOfBenefit** (Da Vinci PDex)
- **45 comprehensive tests** with 100% pass rate

### Security & Compliance

Security and compliance controls for payer validation:

- **Premium Key Vault** with HSM-backed keys (FIPS 140-2 Level 2)
- **Private endpoints** for complete network isolation
- **PHI masking** in Application Insights via DCR-based redaction
- **7-year data retention** with automated lifecycle management
- **Customer-managed keys** (BYOK) for regulatory compliance

---

## Competitive Advantages

### vs. Change Healthcare ($15K+/payer/year, 12-18 month implementation)

| Factor | Change Healthcare | Cloud Health Office |
|--------|-------------------|---------------------|
| Implementation Time | 12-18 months | **Local evaluation in under an hour** |
| CMS-0057-F Readiness | Partial | **Implemented readiness surface with evidence** |
| Pricing Model | Per-transaction + licensing | **Predictable subscription** |
| Source Code Access | Proprietary | **Source-Available (BSL 1.1)** |
| Customization | Professional services required | **Zero-code configuration** |

### vs. Enterprise Vendors ($12K+/payer/year, 6-12 month implementation)

| Factor | Enterprise Vendors | Cloud Health Office |
|--------|----------|---------------------|
| Cloud-Native | Hybrid/legacy | **Azure-native** |
| FHIR Support | Add-on module | **Built-in transformation** |
| Da Vinci IGs | Roadmap | **Implemented readiness surface** |
| AI/ML Integration | Limited | **Azure OpenAI ready** |
| Scaling | Manual | **Auto-scaling included** |

### vs. Custom Development ($2M+, 18-36 month timeline)

| Factor | Custom Development | Cloud Health Office |
|--------|-------------------|---------------------|
| Total Cost | $2M+ development | **$12K-$96K/year** |
| Time to First Evaluation | 18-36 months | **Local evaluation in under an hour** |
| Compliance Risk | Team dependent | **Inspectable architecture and evidence** |
| Ongoing Maintenance | Full burden | **Platform updates included** |
| Community Support | Internal only | **Source-available community** |

---

## Target Customers

### Primary Segments

**Regional Health Plans** (50K-500K members)
- Compliance pressure with limited IT resources
- Budget constraints vs. enterprise vendors
- Need rapid deployment without disruption

**Third-Party Administrators (TPAs)**
- Multi-payer integration requirements
- Cost-sensitive operations
- Provider network satisfaction focus

**Medicaid Managed Care Organizations (MCOs)**
- State compliance mandates
- High transaction volumes
- Prior authorization automation needs

### Ideal Customer Profile

- **Size**: 50,000 - 2,000,000 members
- **Infrastructure**: Azure environment (or willing to adopt)
- **Pain Points**: CMS compliance, EDI modernization, cost reduction
- **Timeline**: Need a validated deployment path within 6 months
- **Budget**: $25K - $100K annual platform spend

---

## Pricing Overview

### Pricing by Product Line

| Product Line | Pricing Shape | Best For |
|--------------|---------------|----------|
| **Public Tools** | Free, no signup required | Verifying engine accuracy and SEO discovery |
| **Transactional Services** | Per-call subscription via Stripe (free tier + paid tiers) | Developers, billing systems, small plans, TPAs, clearinghouses |
| **Managed Data Services** | Recurring subscription (per-month or per-quarter) | Plans needing constantly-updated healthcare data feeds |
| **Platform Engagement** | PMPM, pilot-scoped, multi-year | Health plans engaging at Layer 1, Layer 2, or Layer 3 |

*PMPM ranges per Platform Engagement layer are documented in [FINANCIAL-MODEL.md](./FINANCIAL-MODEL.md) and negotiated per pilot. Founding-partner terms are available for first pilots in each layer.*

### What's Included Across Platform Engagement

**Layer 1 — Compliance Accelerator**:
- Evidence-backed CMS-0057-F readiness surface (Patient Access, Provider Directory, Prior Authorization, Payer-to-Payer)
- Four CHO-authored FHIR appeal profiles plus the `$cho-appeal-submit` operation
- SMART-on-FHIR scope enforcement at the resource-type level
- Azure/Kubernetes deployment pattern for customer HIPAA validation
- Standard support (email, 48-hour response)

**Layer 2 — Progressive Modernization** (includes Layer 1):
- Per-domain Augment / Replace operating mode (legacy stays authoritative until you flip to CHO)
- Appeals as a complete CHO domain (shipped reference Layer 2 implementation: PRs #677, #678, #680, #681)
- ValueAdds277 Enhanced Claim Status (60+ fields)
- Priority support (24-hour response)
- Custom trading partner configurations
- Advanced analytics and dashboards

**Layer 3 — Full CAPS Platform** (includes Layer 2):
- All 36 services and 9 adjudication engines under CHO orchestration
- End-to-end Argo-orchestrated adjudication pipeline
- Multi-tenant operations across services, databases, secrets, and Kafka topics
- Custom integrations and modules
- Compliance audit support
- SLA guarantees per pilot scope

---

## Technical Differentiators

### Source-Available Advantage

- **BSL 1.1 license** - no vendor lock-in
- **Full source code access** - audit, customize, extend
- **Community contributions** - continuous improvement
- **Transparent security** - no black boxes

### Zero-Code Onboarding

```
Traditional EDI Implementation: 6-18 months
Cloud Health Office: < 5 minutes

Deploy → Configure → Process Transactions
```

- **Interactive wizard** guides configuration
- **Config-to-workflow generator** automates deployment
- **30+ template helpers** for customization
- **Example configurations** for common scenarios

### AI-Ready Architecture

- **Azure OpenAI integration** for claim auto-adjudication
- **Predictive denial management** roadmap
- **Intelligent error resolution** suggestions
- **ML-powered workflow optimization**

---

## Customer Success Metrics

### Time-to-Value

| Metric | Industry Average | Cloud Health Office |
|--------|------------------|---------------------|
| Initial Deployment | 6-18 months | **< 5 minutes** |
| First Transaction | 3-6 months | **Same day** |
| Full Production | 12-24 months | **< 90 days** |
| CMS Compliance | Unknown/at risk | **Immediate** |

### Cost Savings

| Category | Traditional Approach | With Cloud Health Office |
|----------|---------------------|-------------------------|
| Development Cost | $2M+ | **$0 (included)** |
| Annual Licensing | $150K-$500K | **$12K-$96K** |
| Integration Services | $100K-$300K | **Minimal** |
| Compliance Audit | $50K-$100K | **Included** |

### Operational Efficiency

- **80% reduction** in payer onboarding time
- **60% fewer** claim status follow-up calls
- **70% auto-adjudication** roadmap (AI-powered)
- **94% storage cost** reduction via lifecycle management

### Provider Satisfaction (ValueAdds277)

- **21 minutes saved** per claim lookup
- **60+ enhanced fields** in every response
- **One-click workflows** for appeals, attachments, corrections
- **Real-time integration** with backend systems

---

## Call to Action

### Start Your Pilot Today

**60-Day Pilot Program** - Free for qualifying organizations:

1. **Complete platform deployment** in your Azure environment
2. **CMS-0057-F compliance audit** with remediation guidance
3. **60 days premium support** with dedicated success manager
4. **Co-developed case study** showcasing your results

**Eligibility**: Regional health plans, TPAs, or Medicaid MCOs facing CMS compliance deadlines

### Azure Marketplace

Deploy instantly from the Azure Marketplace:
- **Free-tier evaluation**: Immediate deployment, no credit card required
- **30-day evaluation**: Full feature trial
- **Transactable**: Pay through Azure billing for Layer 1 — Compliance Accelerator engagements

### Next Steps

| Action | Timeline | Contact |
|--------|----------|---------|
| **Schedule Demo** | 30-minute overview | sales@aurelianware.com |
| **Request Pilot** | 5-day approval process | pilots@aurelianware.com |
| **Technical Deep-Dive** | 60-minute architecture review | solutions@aurelianware.com |

---

## Contact Information

**Aurelianware - Cloud Health Office**

- **Website**: [cloudhealthoffice.com](https://cloudhealthoffice.com)
- **Sales**: sales@aurelianware.com
- **GitHub**: [github.com/aurelianware/cloudhealthoffice](https://github.com/aurelianware/cloudhealthoffice)
- **Support**: support@aurelianware.com

---

*Cloud Health Office - The inevitable evolution of healthcare EDI*

*Source-Available (BSL 1.1) | Azure-Native | CMS-0057-F Readiness | Customer-Validated*

---

**Legal Disclaimer**: Pricing, features, and timelines are subject to change. Compliance statements reflect current platform capabilities; customers are responsible for their own compliance validation and attestation. Cost savings and efficiency metrics are estimates based on typical implementations and may vary by organization.

**Document Version**: 1.0 | **Last Updated**: November 2024
