> **Note:** This document references Azure Logic Apps, which were the original orchestration runtime. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details.

# Cloud Health Office v3.0.0 — The Open Frontier Release

**December 1, 2025**

The frontier is now open.  
Healthcare EDI runs anywhere.

This is the third major release of Cloud Health Office — the source-available, multi-cloud, HIPAA-engineered platform that breaks free from cloud vendor lock-in while maintaining enterprise-grade security and compliance.

---

## Release Highlights

### 🌐 Multi-Cloud Independence
- **Kubernetes-Native Deployment**: Run on AKS, EKS, GKE, or any Kubernetes cluster
- **Argo Workflows**: Cloud-agnostic workflow orchestration replacing Azure Logic Apps
- **Apache Kafka**: Open-source messaging replacing Azure Service Bus
- **HashiCorp Vault**: Alternative to Azure Key Vault for secrets management

### 🚀 Azure Marketplace Ready
- **Managed Application**: One-click deployment via Azure Marketplace
- **Meter-Based Billing**: Per-transaction pricing for 837, 278, 275, FHIR API calls
- **3-Tier Pricing**: Starter, Professional, Enterprise — [Contact sales](mailto:sales@cloudhealthoffice.com) for pricing

### 🏗️ Microservices Architecture
- **Eligibility Service**: Dual X12 270/271 + FHIR interface on Azure Container Apps
- **ClaimRiskScorer**: ML-powered fraud detection with PyTorch
- **Provider Directory API**: FHIR endpoints with NPPES NPI integration
- **Prior Auth API**: Da Vinci PAS CDex with 72-hour SLA tracking

### 💼 Commercial Launch Ready
- **Sales Materials**: Product overview, ROI calculator, pitch deck
- **Financial Model**: 3-year projections with unit economics
- **Pilot Program**: 60-day structured engagement framework
- **VC Fundraising**: Target list, due diligence prep, partner strategy

---

## What's Inside

### Multi-Cloud Platform (v3.0.0)
- Deploy to Azure, AWS, or GCP with unified Helm charts
- Argo Workflows for Kubernetes-native EDI processing
- Kafka for event-driven messaging across clouds
- HashiCorp Vault for cloud-agnostic secrets management
- Complete migration tooling from Azure Logic Apps

### Azure Marketplace Offer
- ARM template deploying complete Cloud Health Office stack
- SaaS fulfillment API integration ready
- Usage metering for per-transaction billing
- Legal documentation: Privacy, SLA, Support Terms
- Sentinel-branded marketplace assets

### Commercial Materials Suite
- Executive sales collateral and ROI calculators
- Case study template for pilot customers
- 15-slide investor/customer pitch deck content
- Email templates for outreach campaigns
- Landing page copy optimized for conversion

### VC Fundraising Package
- Healthcare and SaaS VC target research
- Due diligence preparation checklist
- 50+ strategic partner prospects
- Alternative funding options (SBIR, RBF, venture debt)
- PR and thought leadership strategy

### Microservices
- Eligibility service with Cosmos DB caching
- ClaimRiskScorer with custom ZZZ segment generation
- Provider Directory with NPPES real-time lookup
- Prior Auth API with automated SLA enforcement

---

## Key Metrics

| Metric | Value |
|--------|-------|
| Total Tests | 424 passing |
| Security Vulnerabilities | 0 |
| CMS-0057-F Ready | 100% |
| Vendor References Removed | 1,295 vendor-specific references across 185 files |
| New PRs Merged | 17 since v2.0.0 |
| Deployment Targets | Azure, AWS (EKS), GCP (GKE) |

---

## Migration from v2.0.0

### No Breaking Changes
v3.0.0 is backward compatible with v2.0.0 deployments. Existing Azure Logic Apps workflows continue to function.

### Optional Migrations
- **Kubernetes Migration**: Follow [ARGO-MIGRATION-GUIDE.md](./docs/ARGO-MIGRATION-GUIDE.md) for Argo Workflows
- **Vault Migration**: See [MULTI-CLOUD-DEPLOYMENT.md](./docs/MULTI-CLOUD-DEPLOYMENT.md) for HashiCorp Vault setup
- **Marketplace Publishing**: Review [marketplace/README.md](./marketplace/) for Partner Center submission

---

## Documentation

New documentation in v3.0.0:
- [MULTI-CLOUD-DEPLOYMENT.md](./docs/MULTI-CLOUD-DEPLOYMENT.md) - Complete multi-cloud guide
- [ARGO-MIGRATION-GUIDE.md](./docs/ARGO-MIGRATION-GUIDE.md) - Logic Apps to Argo migration
- [ARGO-OPERATIONS.md](./docs/ARGO-OPERATIONS.md) - Operational runbook
- [AZURE-MONITOR-DASHBOARDS.md](./docs/AZURE-MONITOR-DASHBOARDS.md) - CMS-0057-F compliance dashboard
- [WHITEPAPER-CMS-0057-F-COMPLIANCE.md](./docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.md) - Executive whitepaper
- [ROADMAP-2026.md](./ROADMAP-2026.md) - 2026 product roadmap

---

## PRs Merged (17 since v2.0.0)

| PR | Title | Category |
|----|-------|----------|
| [#116](https://github.com/aurelianware/cloudhealthoffice/pull/116) | Remove vendor-specific references | Platform |
| [#115](https://github.com/aurelianware/cloudhealthoffice/pull/115) | Multi-cloud deployment and HashiCorp Vault | Multi-Cloud |
| [#114](https://github.com/aurelianware/cloudhealthoffice/pull/114) | Container build workflow fix | CI/CD |
| [#113](https://github.com/aurelianware/cloudhealthoffice/pull/113) | Argo Workflows and Kafka migration | Multi-Cloud |
| [#112](https://github.com/aurelianware/cloudhealthoffice/pull/112) | VC fundraising strategy | Commercial |
| [#111](https://github.com/aurelianware/cloudhealthoffice/pull/111) | Commercial launch materials | Commercial |
| [#110](https://github.com/aurelianware/cloudhealthoffice/pull/110) | CMS-0057-F whitepaper enhancements | Documentation |
| [#109](https://github.com/aurelianware/cloudhealthoffice/pull/109) | CMS-0057-F compliance whitepaper | Documentation |
| [#108](https://github.com/aurelianware/cloudhealthoffice/pull/108) | 2026 product roadmap | Roadmap |
| [#107](https://github.com/aurelianware/cloudhealthoffice/pull/107) | Community governance | Governance |
| [#106](https://github.com/aurelianware/cloudhealthoffice/pull/106) | Blazor migration wizard | Tools |
| [#105](https://github.com/aurelianware/cloudhealthoffice/pull/105) | Azure Marketplace offer structure | Marketplace |
| [#104](https://github.com/aurelianware/cloudhealthoffice/pull/104) | ClaimRiskScorer Azure Function | Microservices |
| [#103](https://github.com/aurelianware/cloudhealthoffice/pull/103) | Eligibility service (X12 + FHIR) | Microservices |
| [#102](https://github.com/aurelianware/cloudhealthoffice/pull/102) | CMS-0057-F Compliance Dashboard | Compliance |
| [#101](https://github.com/aurelianware/cloudhealthoffice/pull/101) | patient_access_api workflow fix | Bug Fix |
| [#100](https://github.com/aurelianware/cloudhealthoffice/pull/100) | ProviderDirectoryApi and PriorAuthApi | Microservices |

For detailed PR descriptions, see the [Full Changelog](./CHANGELOG.md).

---

## Contributors

Special thanks to all contributors who made this release possible.

---

## The Open Frontier

The frontier is now open.  
Healthcare EDI runs anywhere—Azure, AWS, GCP, or your private cloud.

**Cloud independence is now a reality.**

BSL 1.1 licensed • Actively maintained by Aurelianware  
Star ★ the repo if you believe healthcare deserves multi-cloud freedom.

---

[Full Changelog](./CHANGELOG.md) • [GitHub Releases](https://github.com/aurelianware/cloudhealthoffice/releases)