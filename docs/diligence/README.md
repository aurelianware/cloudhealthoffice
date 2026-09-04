# Cloud Health Office — Pilot Diligence Binder

**Audience:** payer CISO, privacy counsel, interoperability lead, procurement
**Status as of:** September 2026
**Offer this binder supports:** [CMS-0057-F Compliance Accelerator](../sales-materials/CMS-0057-F-ACCELERATOR-OFFER.md)

This packet is what we hand a buyer **before** PHI, before a production cluster, and before anyone says “compliant.” It is not a completed SOC 2 report, not a CMS certification, and not a signed BAA.

Cloud Health Office is pre-pilot. There is no production reference customer. Founding-partner terms exist because of that fact.

## Contents

| Document | What a reviewer uses it for |
| --- | --- |
| [SECURITY-ONE-PAGER.md](SECURITY-ONE-PAGER.md) | Architecture, tenant isolation, encryption, PHI logging, Key Vault, current gaps |
| [BAA-TEMPLATE.md](BAA-TEMPLATE.md) | Counsel-reviewable Business Associate Agreement template |
| [DATA-HANDLING.md](DATA-HANDLING.md) | Synthetic-only until BAA; then limited PHI; never surprise production loads |
| [ADAPTER-STATUS.md](ADAPTER-STATUS.md) | Demo vs Hybrid vs Live per FHIR resource, with the live endpoint |
| [INCIDENT-AND-SUPPORT.md](INCIDENT-AND-SUPPORT.md) | Contacts, severity, response targets for a founding-partner pilot |
| [SAMPLE-PILOT-CHECKLIST.md](SAMPLE-PILOT-CHECKLIST.md) | Worked example of the CMS-0057-F diligence checklist |
| [FOUNDING-PARTNER-TARGET-LIST.md](FOUNDING-PARTNER-TARGET-LIST.md) | 25 named **targets**, not a claimed pipeline |

## Already-canonical companions (do not fork)

| Document | Role |
| --- | --- |
| [POSITIONING.md](../POSITIONING.md) | What we claim, by product line and layer |
| [CMS-0057-F-READINESS-MATRIX.md](../compliance/CMS-0057-F-READINESS-MATRIX.md) | Requirement-by-requirement technical posture |
| [CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md](../compliance/CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md) | Blank checklist used on a live engagement |
| [CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md](../compliance/CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md) | Labeling rules |
| [LICENSE](../../LICENSE) / [COMMERCIAL-LICENSING.md](../../COMMERCIAL-LICENSING.md) | BSL 1.1; production needs a commercial license |

## How to use this in a sales cycle

1. Send the [one-page offer](../sales-materials/CMS-0057-F-ACCELERATOR-OFFER.md) and this README.
2. Run the [20-minute labeled demo](../sales-materials/demo-materials/cms-0057-f-accelerator-demo.md).
3. Walk CISO through the security one-pager and adapter-status table. Hit `GET /fhir/r4/adapter-status` live.
4. Counsel redlines the BAA. No PHI until it is signed.
5. Order form: founding-partner accelerator SKU, case-study clause, Layer 2 appeals as an optional amendment.

## Phrases we do not use in this binder

- “Certified CMS-0057-F compliant”
- “100% compliant out of the box”
- “Production-ready compliance”
- “Trusted by [named health plans]” (we have no production reference)
- “Live” for seeded, synthetic, or mock-backed resources
