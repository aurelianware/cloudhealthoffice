# Cloud Health Office — Investment Summary

---

> **Pre-Pilot Status (Read First)**
>
> Cloud Health Office is pre-pilot. We have no production reference
> customer yet; our first pilot deployment is in motion. The
> "Production Ready" labels in this document refer to Layer 1
> (CMS-0057-F compliance surface) and Layer 2 (appeals re-foundation,
> PRs #677, #678, #680, #681) — both of which are genuinely shipped
> and architecturally complete. Layer 3 (full CAPS platform) is
> architecturally complete with named gaps we are closing
> deliberately: no production reference customer, test coverage on
> claims / provider / sponsor services, IFhirDataAdapter wiring
> beyond appeals, portal polish, correspondence service, scale
> testing. Full disclosure in [POSITIONING.md](../POSITIONING.md)
> §Layer 3 "what it honestly is today."

---

## The Company

**Cloud Health Office** (CHO) is the first source-available, Azure-native platform for healthcare claims administration. We deliver Layer 1 CMS-0057-F compliance in weeks (vs. the 6-18 months of traditional implementations), with a Layer 2 progressive-modernization path (appeals shipped; see [POSITIONING.md](../POSITIONING.md)) and a Layer 3 full-CAPS platform for new entrants.

---

## Portfolio

CHO operates four product lines, each with distinct unit economics: **Public Tools** (free fee schedule lookup and free-tier claims repricing — funnel-top), **Transactional Services** (per-call APIs moving toward self-serve subscription; customer-surface signup/checkout is not yet fully wired, including the Claims Repricing API and Pricing API — recurring SaaS metrics as activation completes), **Managed Data Services** (recurring subscriptions for state Medicaid compliance, CMS fee schedule updates, provider verification, and terminology — high-margin recurring), and **Platform Engagement** (payer-scale relationships priced per member per month (PMPM), with three layers — Layer 1 — Compliance Accelerator, Layer 2 — Progressive Modernization, and Layer 3 — Full CAPS Platform — multi-year contract economics). The "$8.2B TAM" frame below is anchored in Platform Engagement; the recurring-revenue lines underneath are independently investable. Canonical breakdown: [POSITIONING.md](../POSITIONING.md) and [FINANCIAL-MODEL.md](../sales-materials/FINANCIAL-MODEL.md).

---

## The Problem

| Challenge | Impact |
|-----------|--------|
| **900+ health plans** face mandatory CMS compliance | January 1, 2027 deadline |
| **Traditional vendors** require 6-18 month implementations | $500K-$2M+ cost |
| **Build internally** takes 18-36 months | $2M+ development cost |
| **Non-compliance penalty** | Up to $10K/day + program exclusion |

---

## The Solution

| Capability | Status |
|------------|--------|
| Patient Access API (FHIR R4) | ✅ Production Ready (Layer 1) |
| Provider Access API | ✅ Production Ready (Layer 1) |
| Prior Authorization API (72hr/7-day) | ✅ Automated (Layer 1) |
| Payer-to-Payer API | ✅ Ready (Layer 1) |
| Complete X12 ↔ FHIR Transformation | ✅ 45 transformation tests (part of ~2,800-method suite across 44 test projects) |
| Configuration Wizard | **< 5 min per tenant** |

*Layer 1 = CMS-0057-F compliance surface. Layer 2 (appeals re-foundation) also production-ready. See [POSITIONING.md](../POSITIONING.md) §Layer 3 for full-CAPS honest today-state.*

---

## Market Opportunity

| Segment | Size |
|---------|------|
| **TAM**: US Healthcare EDI | $8.2B (8% CAGR) |
| **SAM**: Cloud-Native EDI | $2.1B (15% CAGR) |
| **SOM**: 3-Year Target | $50M |

**Regulatory Catalyst**: CMS-0057-F creates mandatory, urgent demand across 900+ Medicare, Medicaid, CHIP, and QHP payers.

---

## Traction

| Metric | Current | Year 1 Target |
|--------|---------|---------------|
| Platform Status | ✅ 100% CMS-Ready | Maintained |
| Test Suite | ~2,800 test methods across 44 test projects | Maintain coverage |
| Pilot Pipeline | Active conversations | 25 customers |
| GitHub Stars | Growing | 1,000 |

**Product Milestones Achieved**: Complete FHIR R4 transformation, ValueAdds277 enhanced claim status, production-grade HIPAA security, zero-code payer onboarding.

---

## Financial Snapshot

| Year | Customers | ARR | Gross Margin |
|------|-----------|-----|--------------|
| **Year 1** | 50 | $1.8M | 75% |
| **Year 2** | 150 | $6.0M | 78% |
| **Year 3** | 300 | $13.5M | 82% |

*All rows are forward-looking targets, not present-state. Pre-pilot status disclosed above.*

**Unit Economics** (modeled targets, pre-revenue - not yet realized with a production customer): LTV:CAC 25:1 | CAC Payback: 6 months | NRR: 115-125%

---

## Business Model

These are modeling segments, not customer-facing pricing tiers — Cloud Health Office's customer-facing pricing is PMPM, pilot-scoped, with founding-partner terms in each Platform Engagement layer (Layer 1 — Compliance Accelerator, Layer 2 — Progressive Modernization, Layer 3 — Full CAPS Platform). Annualized figures below are internal modeled projections used to produce ARR targets, not list prices.

| Modeled Segment | Annual | Target Profile |
|-----------------|--------|----------------|
| **Small** | $10,788 | Regional payers, evaluation |
| **Mid-Market** | $32,388 | Mid-market, production |
| **Large** | $86,388 | Large plans, unlimited scale |

**Revenue Streams**: 80% Subscription | 12% Professional Services | 5% Premium Support | 3% Partner Revenue Share

---

## Team

Experienced leadership in healthcare IT, Azure platform architecture, and SaaS sales. Advisory board includes former payer executives and regulatory compliance experts.

**Key Hires Planned**: Sr. AI/ML Engineer (Q1), Developer Advocate (Q1), Sr. Backend Engineer (Q2)

---

## The Ask

**$2M Seed Round**

| Use of Funds | Allocation |
|--------------|------------|
| Engineering | 50% |
| Sales & Marketing | 30% |
| Operations | 20% |

---

## Investment Highlights

- **Market Timing**: CMS deadline creates $8B+ market urgency
- **Product-Market Fit**: Only source-available, Azure-native solution
- **Capital Efficiency**: LTV:CAC > 25:1, profitability Year 3
- **Defensibility**: Source-available community, compliance expertise, Azure partnership
- **Exit Potential**: 8-12x ARR ($108M-$162M Year 3)

---

## Contact

**Aurelianware — Cloud Health Office**

| Channel | Contact |
|---------|---------|
| Website | cloudhealthoffice.com |
| Investors | investors@aurelianware.com |
| GitHub | github.com/aurelianware/cloudhealthoffice |

---

*Cloud Health Office — The inevitable evolution of healthcare EDI*

*Source-Available (BSL 1.1) | Azure-Native | CMS-0057-F Compliant | HIPAA-Ready*

---

**Confidential**: This document contains forward-looking statements. Financial projections are estimates based on current assumptions.
