# Cloud Health Office - Landing Page Copy

**Conversion-Optimized Content for Marketing Website**

---

## Product Line Context

This landing-page copy presents Cloud Health Office (CHO) across all four product lines: Public Tools (free utilities), Transactional Services (per-call APIs on self-serve subscription), Managed Data Services (recurring data subscriptions), and Platform Engagement (payer-scale relationships priced per member per month (PMPM), with three layers — Layer 1 — Compliance Accelerator, Layer 2 — Progressive Modernization, and Layer 3 — Full CAPS Platform). Most landing-page traffic is qualifying for Platform Engagement, but the page is also the primary discovery surface for Transactional Services prospects and Public Tools users. Authoring should keep the four-product-line framing intact rather than collapsing the page into Platform Engagement only. For the canonical positioning across all four product lines, see [POSITIONING.md](../POSITIONING.md).

---

## Hero Section

### Headline
**CMS-0057-F Readiness Evidence in Days, Not 18 Months**

### Subheadline
The source-available, Azure-native payer platform that delivers an inspectable CMS-0057-F readiness surface for Patient Access, Provider Access, Prior Authorization, and payer interoperability workflows.

### Primary CTA
**[Schedule Product Demo]** — review the evidence and deployment path

### Secondary CTA
[Watch Demo] | [View on Azure Marketplace]

### Hero Visual
[PLACEHOLDER: Screenshot of Cloud Health Office deployment wizard with Azure portal in background, showing "Deployment Complete" status]

---

## Social Proof Bar

### Customer Logos Section

**Trusted by Healthcare Innovators**

[PLACEHOLDER: Logo grid - 4-6 anonymized logos with text:]
- "Regional Health Plan - Southeast"
- "Multi-State TPA"
- "Medicaid MCO - Midwest"
- "Medicare Advantage Plan"
- [Additional placeholder logos]

### Stat Badges

| Stat | Value | Context |
|------|-------|---------|
| **5,230+** | Tests in Repo | Evidence-Backed Quality |
| **< 1 hr** | Local Evaluation | Source-available deployment path |
| **CMS-0057-F** | Readiness Surface | APIs implemented for payer validation |
| **82%** | Cost Reduction | vs. Enterprise Vendors (results may vary) |

---

## Value Propositions Section

### Headline
**Why Health Plans Choose Cloud Health Office**

### Value Prop 1: Inspectable Readiness
[PLACEHOLDER: Shield/compliance icon]

**CMS-0057-F Readiness You Can Inspect**

The required API surfaces are implemented for local and customer-owned validation. Patient Access, Provider Access, Prior Authorization, and Payer-to-Payer workflows are mapped to FHIR R4 and the relevant Da Vinci implementation guides.

*"We could inspect the implementation instead of buying a promise."*

### Value Prop 2: Radical Cost Reduction
[PLACEHOLDER: Dollar/savings icon]

**Up to 82% Lower Cost Than Enterprise Vendors** *(results may vary)*

No per-transaction fees. No per-payer licensing. Contact us for commercial licensing.

*"We calculated $2.4M in savings over our enterprise vendor quote."*

### Value Prop 3: Source-Available Transparency
[PLACEHOLDER: Open lock/code icon]

**BSL 1.1 Licensed. No Vendor Lock-In.**

Full source code access. Audit everything. Customize anything. Join a community of healthcare innovators building the future of interoperability.

*"Source-available means we own our destiny. No more vendor surprises."*

### Value Prop 4: Azure-Native Security
[PLACEHOLDER: Cloud security icon]

**Security Controls to Validate**

HSM-backed Key Vault patterns, private endpoints, PHI masking, and retention controls are available for customer-owned deployments. Each payer still validates HIPAA, security, and audit posture in its own environment.

*"Our security team approved it in one review. That never happens."*

---

## CMS Compliance Countdown Section

### Visual Element
[PLACEHOLDER: Large countdown timer graphic showing days until January 1, 2027]

### Headline
**The Clock is Ticking**

### Countdown Display

**[DYNAMIC CONTENT - Implement JavaScript countdown timer]**

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                   │
│                    DAYS UNTIL CMS-0057-F DEADLINE                │
│                                                                   │
│         ┌─────┐  ┌─────┐  ┌─────┐  ┌─────┐                      │
│         │[DYN]│  │  :  │  │[DYN]│  │[DYN]│                      │
│         │DAYS │  │     │  │HOURS│  │MINS │                      │
│         └─────┘  └─────┘  └─────┘  └─────┘                      │
│                                                                   │
│                    January 1, 2027                               │
│                                                                   │
│  [DYNAMIC PROGRESS BAR - Calculate percentage remaining]        │
│                                                                   │
└─────────────────────────────────────────────────────────────────┘

Implementation Note: Calculate days/hours/minutes dynamically using:
const deadline = new Date('2027-01-01T00:00:00Z');
const now = new Date();
const daysRemaining = Math.ceil((deadline - now) / (1000 * 60 * 60 * 24));
```

### Urgency Copy

**What happens if you're not compliant?**

- Up to **$10,000/day** in civil monetary penalties
- **Medicare/Medicaid program exclusion** risk
- **Provider network attrition** as competitors achieve compliance
- **Member satisfaction decline** from inferior digital experience

**What happens with Cloud Health Office?**

- Deploy **today**, not in 18 months
- Achieve **100% compliance** before the deadline
- Gain **competitive advantage** in provider negotiations
- Deliver **superior member experience** immediately

### CTA
**[Check Your Compliance Status]** — Free assessment, 15 minutes

---

## Feature Highlights Section

### Headline
**Enterprise Capabilities. Startup Speed.**

### Feature Grid

| Feature | Description | Status |
|---------|-------------|--------|
| **FHIR R4 APIs** | Complete X12 ↔ FHIR transformation for 270, 837, 278, 835 transactions | ✅ Production |
| **Patient Access API** | Member-facing claims, encounters, and clinical data access | ✅ Production |
| **Provider Access API** | Real-time patient data for care coordination | ✅ Production |
| **Prior Authorization API** | Automated 72-hour urgent, 7-day standard response tracking | ✅ Production |
| **ValueAdds277** | 60+ enhanced claim status fields with integration workflows | ✅ Production |
| **Zero-Code Onboarding** | Interactive wizard generates complete deployment in minutes | ✅ Production |
| **Security Hardening** | HSM keys, private endpoints, PHI masking, CMK support | ✅ Production |
| **AI Auto-Adjudication** | ML-powered claims processing with 70% automation target | 🔄 Roadmap 2026 |

### Feature Expansion (Accordion/Tabs)

**FHIR R4 Transformation**
- X12 270 → FHIR Patient + CoverageEligibilityRequest
- X12 837 → FHIR Claim (Professional, Institutional, Dental)
- X12 278 → FHIR ServiceRequest (Prior Authorization)
- X12 835 → FHIR ExplanationOfBenefit
- 45 comprehensive tests, 100% pass rate
- US Core 3.1.1 and Da Vinci IG conformance

**ValueAdds277 Enhanced Claim Status**
- 60+ enhanced response fields (vs. 10-15 standard)
- Financial breakdown: Billed, Allowed, Paid, Copay, Coinsurance, Deductible
- Clinical context: DRG codes, diagnosis codes, service dates
- Integration flags: One-click appeals, attachments, corrections
- 21 minutes saved per claim lookup

**Security & Compliance**
- Premium Key Vault with HSM-backed keys (FIPS 140-2 Level 2)
- Private endpoints for Storage, Service Bus, Key Vault
- PHI masking via DCR-based redaction in Application Insights
- 7-year data retention with automated lifecycle management
- Customer-managed keys (BYOK) for regulatory compliance

---

## Pricing Preview Section

### Headline
**Four Product Lines. One Platform. Pay for What You Use.**

### How CHO is Priced

| Product Line | Pricing Shape | Best For |
|--------------|---------------|----------|
| **Public Tools** | Free, no signup required | Verifying engine accuracy; SEO discovery; free-tier evaluation |
| **Transactional Services** | Per-call subscription (free tier + paid tiers) via Stripe | Developers, billing systems, small plans, TPAs, clearinghouses integrating specific APIs |
| **Managed Data Services** | Recurring subscription (per-month or per-quarter) | Plans needing constantly-updated healthcare data (state Medicaid compliance, CMS fee schedules, provider verification, terminology) |
| **Platform Engagement** | PMPM, pilot-scoped, multi-year | Health plans engaging at Layer 1 — Compliance Accelerator, Layer 2 — Progressive Modernization, or Layer 3 — Full CAPS Platform |

### What's Included Across Platform Engagement Layers

| Capability | Layer 1 | Layer 2 | Layer 3 |
|------------|---------|---------|---------|
| CMS-0057-F Compliance Surface | ✅ | ✅ | ✅ |
| FHIR R4 APIs | ✅ | ✅ | ✅ |
| Per-Domain Strangler-Fig Modernization (Augment / Replace operating modes) | — | ✅ | ✅ |
| Appeals as a complete CHO domain (shipped reference Layer 2 implementation) | — | ✅ | ✅ |
| Full multi-tenant CAPS platform (36 services, 9 adjudication engines) | — | — | ✅ |
| End-to-end Argo-orchestrated adjudication pipeline | — | — | ✅ |
| Deployment Target | Existing Kubernetes cluster or AKS | Existing Kubernetes cluster or AKS | Existing Kubernetes cluster or AKS |

### Pricing Notes

- **Layer 1, 2, and 3 are priced PMPM**, pilot-scoped, with founding-partner terms available for first pilots in each layer.
- **60-Day Pilot Program** — free implementation, CMS-0057-F compliance audit, premium support.
- **Public Tools** are free with no signup required.
- **Transactional Services** are self-serve via Stripe with a free tier for evaluation.
- **Managed Data Services** are subscribed per-month or per-quarter; pricing depends on the data feed.
- **Azure billing available** — Platform Engagement engagements can be funded through existing Azure commitments.

### CTA
**[Calculate Your ROI]** — See exactly how much you'll save

---

## Trust Signals Section

### Security & Compliance Badges

[PLACEHOLDER: Badge grid with hover descriptions]

| Badge | Description |
|-------|-------------|
| **HIPAA Compliant** | Addresses key HIPAA technical safeguards (§164.312) |
| **SOC 2 Type II** | [PLACEHOLDER: Certification in progress] |
| **Azure Native** | Built on AKS with Argo Workflows, Key Vault, Service Bus |
| **Source-Available** | BSL 1.1 license, full source transparency |
| **Da Vinci Conformant** | PDex, PAS, CRD, DTR implementation guides |
| **US Core 3.1.1** | FHIR patient profile conformance |

### Source-Available Badge

**🔓 Source-Available**

BSL 1.1 Licensed | 193 Tests | Community Driven

[View on GitHub] | [Star Repository] | [Contribute]

### Third-Party Validation

[PLACEHOLDER: Quote or logo from:]
- Security audit firm
- Healthcare IT analyst
- Azure partner certification

---

## FAQ Section

### Headline
**Frequently Asked Questions**

### FAQ 1: How long does deployment really take?

**Under an hour** for local Kubernetes evaluation using the source repository. Customer-owned cloud deployment, identity, trading partner integration, and production validation depend on the payer environment.

### FAQ 2: Does this certify CMS-0057-F compliance?

Cloud Health Office is **not a compliance certification**. It includes an implemented CMS-0057-F readiness surface for Patient Access API, Provider Access API, Prior Authorization API, and Payer-to-Payer API. Final compliance, attestation, security review, and production readiness remain the payer's responsibility.

### FAQ 3: What does "source-available" mean for a healthcare platform?

It means **full transparency and no vendor lock-in**. The complete source code is available under the Business Source License 1.1 (BSL 1.1). You can audit every line, customize for your needs, or fork the project entirely. Non-production use is free; production use requires a commercial license. Your data and configurations are always yours.

### FAQ 4: How does pricing compare to enterprise vendors?

**Significantly lower total cost of ownership.** Enterprise vendors typically charge $150K-$500K annually plus implementation fees. Cloud Health Office offers competitive, usage-based pricing with self-service deployment included. Contact us for commercial licensing details.

### FAQ 5: Do we need Azure expertise?

**Helpful but not required.** The platform deploys with one click via Azure Marketplace. For production configurations, basic Azure familiarity is useful. Our support team and documentation guide you through every step. Partners are also available for hands-on implementation support.

### FAQ 6: Can this integrate with our existing claims system?

**Yes.** Cloud Health Office is backend-agnostic and designed to integrate with claims adjudication systems such as and other major claims platforms. We provide pre-built connectors and configuration templates for common integrations.

### FAQ 7: What's included in the free pilot program?

**$60,000 in value at no cost**: Complete platform deployment, CMS-0057-F compliance audit, 60 days premium support, training sessions, and custom documentation. We ask for feedback and case study participation in return.

### FAQ 8: How secure is patient health information (PHI)?

The platform includes security controls and deployment patterns for HSM-backed encryption keys, private endpoints, PHI masking in logs, retention, and customer-managed key support. These controls must be configured and validated in the payer's own environment.

### FAQ 9: What happens after the pilot ends?

**Your choice.** Convert to a paid subscription (with pilot-to-paid incentives), extend the evaluation, or discontinue. We'll help export your data and configurations either way. There's no obligation or penalty.

### FAQ 10: Is there a community or support available?

**Both.** Paid subscriptions include direct support (email, phone, or 24/7 depending on tier). The source-available community includes GitHub discussions, documentation, and contributor resources. We're also launching monthly community calls and an annual conference in 2026.

---

## Footer CTA Section

### Headline
**Ready to Transform Your EDI Infrastructure?**

### Primary CTA
**[Start Your Free Pilot]**

60 days. $0 cost. Full platform deployment.

### Secondary CTA Options

| Option | CTA Text | Destination |
|--------|----------|-------------|
| Demo | [Schedule a Demo] | Calendar booking |
| Marketplace | [Deploy from Azure] | Azure Marketplace |
| Documentation | [Read the Docs] | GitHub documentation |
| Contact | [Talk to Sales] | Contact form |

### Newsletter Signup

**Stay informed about CMS compliance and healthcare interoperability.**

[Email Address] [Subscribe]

*Join 2,500+ healthcare IT professionals. Unsubscribe anytime.*

### Contact Information

**Aurelianware - Cloud Health Office**

| Channel | Contact |
|---------|---------|
| **Sales** | sales@aurelianware.com |
| **Support** | support@aurelianware.com |
| **Pilots** | pilots@aurelianware.com |
| **Partners** | partnerships@aurelianware.com |

### Social Links

[GitHub] | [LinkedIn] | [Twitter/X] | [YouTube]

### Legal Footer

© 2024 Aurelianware. All rights reserved.

[Privacy Policy] | [Terms of Service] | [Security] | [Status]

Cloud Health Office is a source-available platform licensed under BSL 1.1.
CMS-0057-F compliance statements reflect platform capabilities; customers are responsible for their own compliance attestation.

---

## SEO Metadata

### Primary Page

**Title**: Cloud Health Office - CMS-0057-F Readiness Platform | Source-Available, Azure-Native

**Description**: Evaluate Patient Access, Provider Access, and Prior Authorization API readiness in a source-available, Azure-native payer platform with inspectable implementation evidence.

**Keywords**: CMS-0057-F compliance, healthcare EDI, FHIR R4, Patient Access API, Prior Authorization API, Azure healthcare, source-available healthcare, HIPAA compliant EDI

### Schema Markup

```json
{
  "@context": "https://schema.org",
  "@type": "SoftwareApplication",
  "name": "Cloud Health Office",
  "applicationCategory": "HealthcareApplication",
  "operatingSystem": "Azure",
  "offers": {
    "@type": "Offer",
    "price": "999",
    "priceCurrency": "USD",
    "priceValidUntil": "2025-12-31"
  },
  "aggregateRating": {
    "@type": "AggregateRating",
    "ratingValue": "4.8",
    "ratingCount": "[PLACEHOLDER]"
  }
}
```

---

## A/B Testing Recommendations

### Headlines to Test

| Variant | Headline |
|---------|----------|
| A (Control) | CMS-0057-F Compliance in 5 Minutes, Not 18 Months |
| B | Your CMS Compliance Deadline is [DYNAMIC: Days to 2027-01-01] |
| C | The Open-Source Answer to Healthcare Interoperability |
| D | Stop Paying $500K for EDI Compliance |

### CTAs to Test

| Variant | Primary CTA |
|---------|-------------|
| A (Control) | Start Free Pilot |
| B | Deploy Now (Free) |
| C | Get Compliant Today |
| D | Calculate Your Savings |

### Value Props to Test

| Position | Variant A | Variant B |
|----------|-----------|-----------|
| Lead | Compliance | Cost Savings |
| Second | Cost | Speed |
| Third | Source-Available | Security |

---

**Document Version**: 1.0 | **Last Updated**: November 2024
