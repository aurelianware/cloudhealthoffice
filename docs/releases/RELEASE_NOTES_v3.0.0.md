> **Note:** This document references Azure Logic Apps, which were the original orchestration runtime. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details.

# CloudHealthOffice v3.0.0 Release Notes

**Release Date**: December 1, 2025  
**Codename**: The Open Frontier Release  
**Previous Version**: v2.8.3  
**Support End Date**: December 1, 2027 (Long-term support)

---

## 📢 Overview

CloudHealthOffice v3.0.0 marks a transformational release that delivers multi-cloud independence, enterprise-grade microservices architecture, and commercial launch readiness. This release breaks free from single-cloud vendor lock-in while maintaining the security and compliance standards healthcare organizations require.

---

## 🆕 What's New in v3.0.0

### Multi-Cloud & Cloud Independence

| Feature | Description |
|---------|-------------|
| **Kubernetes/Helm Deployment** | Deploy Cloud Health Office to AKS, EKS, GKE, or any Kubernetes cluster with unified Helm charts |
| **Argo Workflows Migration** | Cloud-native workflow orchestration replacing Azure Logic Apps for platform-agnostic EDI processing |
| **Apache Kafka Integration** | Cloud-agnostic messaging system replacing Azure Service Bus |
| **HashiCorp Vault Support** | Open-source secrets management as an alternative to Azure Key Vault |
| **Multi-Cloud Deployment Guide** | Comprehensive documentation for deploying across Azure, AWS, and GCP |

### Microservices Architecture

| Service | Capabilities |
|---------|-------------|
| **Eligibility Service** | Dual X12 270/271 and FHIR interface with Cosmos DB caching and Event Grid publishing |
| **ClaimRiskScorer Function** | ML-powered fraud/abuse scoring (0-100) using PyTorch with custom ZZZ segment generation |
| **Provider Directory API** | FHIR endpoints with real-time NPPES NPI integration |
| **Prior Auth API** | Da Vinci PAS CDex flow with automated 72-hour SLA tracking |

### Azure Marketplace Readiness

- **Managed Application Plan**: ARM template deploying complete Cloud Health Office stack
- **SaaS Plan with Meter-Based Billing**: Per-transaction pricing for 837, 278, 275, and FHIR API calls
- **3-Tier Pricing Structure**: Starter, Professional, Enterprise — [Contact sales](mailto:sales@cloudhealthoffice.com) for pricing
- **Legal Documentation**: Privacy policy, SLA (99.5%-99.95% uptime), and support terms

### Commercial Launch Materials

- **Sales Product Overview**: 2-page executive summary with competitive positioning
- **ROI Calculator**: TCO analysis and 5-year savings projections
- **Case Study Template**: Reusable template for pilot customer success stories
- **Financial Model**: 3-year projections with unit economics
- **Pitch Deck Content**: 15-slide framework for investor/customer presentations
- **Pilot Program**: 60-day structured pilot with success criteria
- **Sales Email Templates**: 5 targeted outreach templates
- **Marketing Landing Page Copy**: Conversion-optimized content

### VC Fundraising Strategy

- **VC Target List**: 12+ prioritized healthcare and SaaS VCs with investment thesis fit
- **Investor One-Pager**: Single-page investment summary
- **Due Diligence Checklist**: Legal, financial, technical, commercial preparation
- **Strategic Partner Targets**: 50+ partners including Microsoft, SIs, technology vendors
- **Alternative Funding Options**: SBIR grants, revenue-based financing, venture debt

### CMS-0057-F Compliance Dashboard

- **Azure Monitor Workbook**: Real-time compliance metrics visualization
- **Patient Access API Tracking**: Enablement percentage with daily trends
- **Prior Auth SLA Monitoring**: 72-hour urgent and 7-day standard response tracking
- **Error Rate Analysis**: Transaction-level error tracking

### Migration Wizard

- **Blazor Web App**: `/tools/migration-wizard` for legacy system migration
- **Claims Backend SOAP Integration**: Paginated export via Open Access APIs
- **Cosmos DB Export**: Batch upsert for Members, ProviderDirectory, BenefitPlans
- **Mapping Report Generator**: 95%+ auto-match with field-level validation
- **One-Click API Cutover**: Routing key flip via Azure API Management

### 2026 Product Roadmap

- **Quarterly Milestones**: Q1-Q4 2026 with CMS compliance timeline
- **Microservice Releases**: eligibility-service v2.0, prior-auth-service v2.0, claims-service v1.0, remittance-service v1.0
- **Community Targets**: 500→7,500 GitHub stars, 15→150 contributors
- **OKRs**: Measurable success criteria for compliance, adoption, community, and AI

### Community Governance

- **Enhanced CONTRIBUTING.md**: DCO and CLA instructions
- **CODE_OF_CONDUCT.md**: Contributor Covenant 2.1
- **GOVERNANCE.md**: Steering committee election process
- **Issue Templates**: Feature request and bug report YAML forms
- **PR Automation**: Auto-labeling and reviewer assignment workflows

---

## ⚠️ Breaking Changes from v2.x

### API Changes

| Change | Impact | Migration Path |
|--------|--------|----------------|
| **None** | v3.0.0 is fully backward compatible | Existing v2.x API integrations continue to function without modification |

### Configuration Changes

| Change | Impact | Migration Path |
|--------|--------|----------------|
| **Vault Configuration** | New `vault.type` configuration option | Add `vault.type: "azure"` to maintain existing behavior or `vault.type: "hashicorp"` for multi-cloud |
| **Workflow Engine Selection** | New `workflow.engine` option | Add `workflow.engine: "logicapps"` to maintain existing behavior or `workflow.engine: "argo"` for Kubernetes |

### Infrastructure Changes

| Change | Impact | Migration Path |
|--------|--------|----------------|
| **Optional Kubernetes Support** | Helm charts now available | Azure Logic Apps deployments continue to work unchanged |
| **Optional Kafka Integration** | Kafka messaging available | Azure Service Bus remains the default for Azure deployments |

---

## 📋 Upgrade Instructions

### Prerequisites

- CloudHealthOffice v2.5.0 or later
- Node.js 18.x or later
- Azure CLI 2.50+ (for Azure deployments)
- kubectl 1.28+ (for Kubernetes deployments)
- Helm 3.12+ (for Kubernetes deployments)

### Standard Upgrade (Azure Logic Apps)

```bash
# 1. Backup current configuration
az logicapp config backup -n <logicapp-name> -g <resource-group>

# 2. Update to v3.0.0
git fetch origin
git checkout v3.0.0

# 3. Rebuild and deploy
npm install
npm run build

# 4. Deploy updated workflows
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/main.bicep \
  --parameters baseName=<base-name>

# 5. Verify deployment
./scripts/test-e2e.ps1 -ResourceGroup <resource-group> -LogicAppName <logicapp-name>
```

### Kubernetes Migration (Optional)

```bash
# 1. Install using local Helm chart (from repository clone)
helm install cloudhealthoffice ./helm/cloudhealthoffice \
  --namespace cloudhealthoffice \
  --create-namespace \
  --set vault.enabled=true \
  --set vault.type=hashicorp

# 2. Apply Argo Workflows
kubectl apply -f argo-workflows/ -n cloudhealthoffice

# 3. Verify deployment
kubectl get pods -n cloudhealthoffice
kubectl get workflows -n cloudhealthoffice
```

### Post-Upgrade Verification

```bash
# Run comprehensive health checks
./scripts/test-e2e.ps1 -ResourceGroup <resource-group> -LogicAppName <logicapp-name>

# Verify FHIR endpoints
npm run test:fhir

# Check compliance dashboard
az monitor workbook show --name "CMS-0057-F Compliance" --resource-group <resource-group>
```

---

## 🐛 Known Issues and Limitations

### Known Issues

| Issue | Description | Workaround | Expected Fix |
|-------|-------------|------------|--------------|
| **Argo Workflow UI Timeout** | Dashboard may timeout with 1000+ workflow runs | Apply pagination or archive old runs | v3.0.1 |
| **Kafka Consumer Lag** | High lag during initial sync with large datasets | Increase consumer parallelism | Documented behavior |
| **Vault Token Renewal** | Manual token renewal required for long-lived pods | Enable auto-renewal via Vault Agent | v3.0.1 |

### Limitations

| Limitation | Description | Planned Resolution |
|------------|-------------|-------------------|
| **FHIR R5 Partial Support** | R5 support for core resources only; extensions in progress | Full R5 in v3.1.0 |
| **GCP Deployment** | GKE deployment tested; Anthos not yet supported | Anthos support in v3.2.0 |
| **Private Link for Kafka** | Azure Private Link for Kafka not yet available | Planned for v3.1.0 |

---

## 📅 Deprecation Notices

### Deprecated in v3.0.0

| Component | Deprecation Date | Removal Date | Migration Path |
|-----------|-----------------|--------------|----------------|
| **v2 REST API** | December 1, 2025 | December 1, 2026 | Migrate to v3 API endpoints |
| **Legacy Auth Module** | December 1, 2025 | June 1, 2026 | Migrate to OAuth 2.0 / OIDC |
| **Azure Logic Apps (standalone)** | Not deprecated | N/A | Kubernetes is optional; Logic Apps remains supported |

### Previously Deprecated (Removed in v3.0.0)

| Component | Removed Date | Alternative |
|-----------|-------------|-------------|
| **Vendor-specific References** | December 1, 2025 | Generic backend integration patterns |

---

## 📊 Metrics and Statistics

| Metric | Value |
|--------|-------|
| **Total Tests Passing** | 424 |
| **Security Vulnerabilities** | 0 |
| **CMS-0057-F Compliance** | 100% ready |
| **PRs Merged Since v2.0.0** | 17 |
| **Vendor References Removed** | 1,295 across 185 files |
| **Deployment Targets** | Azure, AWS (EKS), GCP (GKE) |

---

## 🔗 Related Documentation

- [v3.0.0 Features Overview](./v3.0.0-features-overview.md)
- [Multi-Cloud Deployment Guide](../MULTI-CLOUD-DEPLOYMENT.md)
- [Argo Migration Guide](../ARGO-MIGRATION-GUIDE.md)
- [Argo Operations Runbook](../ARGO-OPERATIONS.md)
- [CMS-0057-F Compliance Whitepaper](../WHITEPAPER-CMS-0057-F-COMPLIANCE.md)
- [2026 Product Roadmap](../../ROADMAP-2026.md)
- [Full Changelog](../../CHANGELOG.md)

---

## 🙏 Acknowledgments

Special thanks to all contributors who made this release possible. The v3.0.0 release represents a community effort to make enterprise healthcare EDI accessible across all cloud platforms.

---

## 📄 License

BSL 1.1 - See [LICENSE](../../LICENSE) for details.

---

**CloudHealthOffice** – The Open Frontier  
*Multi-Cloud | Open Source | Production-Grade | HIPAA-Compliant*
