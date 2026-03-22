# Archived: Azure Marketplace Offer (Logic App Era)

> **Status:** Archived — no longer the active go-to-market path.

## What This Was

This directory contains the pre-migration Azure Marketplace offer for Cloud Health Office.
The offer was built around **Azure Logic App Standard** as the primary compute resource,
with an ARM-based Managed Application deployment model published through Microsoft Partner Center.

### Key Components

| Directory | Contents |
|-----------|----------|
| `managed-app/` | ARM template (`mainTemplate.json`), portal UI definition (`createUiDefinition.json`), and dashboard view (`viewDefinition.json`) for the Azure Managed Application |
| `partnercenter/` | Partner Center offer metadata (listing, plans, pricing, certifications) |
| `saas-offer/` | SaaS offer configuration for tiered subscription plans |
| `legal/` | Terms of Service, SLA, Support Terms, and Privacy Policy |
| `icons/` | Marketplace branding assets (SVG logos and hero images) |
| `original-README.md` | The original publishing guide and validation instructions |
| `PRICING.md` | Original pricing documentation |

### Architecture at Time of Archive

- **Compute:** Logic App Standard (Workflow Service Plan — WS1/WS2/WS3 by tier)
- **Data:** Azure Data Lake Gen2, Cosmos DB, Service Bus
- **Integration:** Logic App Integration Account for X12 B2B/EDI
- **Monitoring:** Application Insights + Log Analytics
- **Security:** Azure Key Vault with RBAC
- **Deployment tiers:** Sandbox, Standard, Enterprise

## Why It Was Archived

Cloud Health Office transitioned from an Azure Marketplace Managed Application to a
**SaaS model** delivered via [cloudhealthoffice.com](https://cloudhealthoffice.com),
running on **Azure Kubernetes Service (AKS)**. The Marketplace offer is no longer
maintained or published.

## Should I Use These Files?

No. These files are preserved for historical reference only. The current infrastructure
lives in the active deployment directories. If you need to understand the original
Marketplace offer design or reference the legal templates, this archive is the place to look.
