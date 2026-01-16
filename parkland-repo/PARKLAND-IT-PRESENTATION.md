# Parkland IT Presentation - PCHP Integration Platform

**For**: Parkland Hospital System IT Leadership  
**Subject**: PCHP Integration Platform Deployment Proposal  
**Date**: January 2026  
**Prepared by**: PCHP IT Team

---

## Executive Summary

We propose deploying a **private integration platform** for Parkland Community Health Plan (PCHP) that will:

1. Enable PCHP members to access healthcare records via mobile app
2. Provide infrastructure for gradual operations migration from Cognizant BPass
3. Save approximately **$359,000 over 3 years** (55% cost reduction)
4. Leverage existing Parkland Hospital System infrastructure (ExpressRoute, VPN)

## Quick Facts

| Metric | Value |
|--------|-------|
| **Organization** | Parkland Community Health Plan (PCHP) |
| **Parent Company** | Parkland Hospital System |
| **Initial Investment** | ~$25,000 (one-time setup) |
| **Monthly Cost** | $1,725 (dev) to $3,200 (prod) |
| **Annual Cost** | ~$88,000 (all environments) |
| **3-Year TCO** | ~$289,000 |
| **Deployment Time** | 2-3 weeks (Phase 1) |
| **ROI** | 55% savings vs. current Cognizant BPass |

## Architecture Overview

```
Parkland Hospital System (Parent Company)
    ├── ExpressRoute Hub ──────────> On-Premises Network
    ├── VPN Gateway ───────────────> Cognizant QNXT
    └── Azure Subscription
            │
            └── PCHP Integration Platform (Spoke)
                    ├── Azure Kubernetes Service (AKS)
                    │   ├── Member Interoperability API
                    │   ├── File Ingestion Service
                    │   └── FHIR Gateway
                    │
                    └── Azure Services
                        ├── Key Vault (Premium HSM)
                        ├── Storage (Data Lake Gen2)
                        ├── Event Hub (Kafka)
                        └── Monitoring (App Insights)
```

## Phased Approach

### Phase 1: Member Interoperability API (Q1 2026) - **INITIAL**
- **Timeline**: 2-3 weeks
- **Cost**: $1,725/month (dev environment)
- **Capabilities**:
  - PCHP member registration via Okta
  - Mobile app for healthcare records access
  - FHIR R4 API for records download
  - Support for 10,000+ PCHP members

### Phase 2: File Ingestion (Q2 2026)
- **Timeline**: 4-6 weeks
- **Cost**: Add UAT environment (+$2,400/month)
- **Capabilities**:
  - SFTP ingestion from Cognizant BPass
  - Automated EDI file processing
  - Archive to Azure Storage

### Phase 3-5: Full Operations (2026-2027)
- Progressive migration from Cognizant BPass
- Claims processing, prior authorization, full EDI suite
- Complete operations control

## Cost Breakdown

### Monthly Azure Costs (PCHP-Dedicated Resources)

| Service | Dev | UAT | Prod |
|---------|-----|-----|------|
| **AKS Cluster** | $609 | $1,096 | $1,826 |
| **Storage** | $46 | $72 | $120 |
| **Key Vault** | $40 | $65 | $125 |
| **Event Hub** | $24 | $47 | $60 |
| **Monitoring** | $179 | $276 | $538 |
| **Networking** | $39 | $63 | $83 |
| **Data Transfer** | $17 | $30 | $65 |
| **Backup & DR** | $12 | $25 | $62 |
| **Contingency (10%)** | $155 | $217 | $290 |
| **Total** | **$1,725** | **$2,400** | **$3,200** |

### Shared Infrastructure (No Incremental Cost)

Parkland Hospital System already provides:
- ExpressRoute Circuit (~$1,000/month value)
- VPN Gateway to Cognizant (~$500/month value)
- Hub VNet and routing (~$300/month value)
- Corporate security services (~$700/month value)

**Total Shared Value**: ~$2,500/month (no additional charge to PCHP)

### 3-Year Total Cost of Ownership

| Solution | Setup | Year 1 | Year 2 | Year 3 | **3-Year Total** |
|----------|-------|--------|--------|--------|------------------|
| **PCHP Platform** | $25k | $88k | $75k | $65k | **$289k** |
| **Cognizant BPass** | $0 | $216k | $216k | $216k | **$648k** |
| **Savings** | | | | | **$359k (55%)** |

## Deployment Options

### Option 1: One-Click Deploy (Recommended)

1. Click "Deploy to Azure" button in repository
2. Fill in deployment parameters
3. Wait 30-45 minutes for infrastructure
4. Deploy applications via kubectl
5. Configure Okta for PCHP members
6. Go live with member API

**Estimated Time**: 2-3 weeks from approval

### Option 2: Custom Deployment

1. Review and customize Bicep templates
2. Deploy infrastructure via Azure CLI
3. Configure network integration
4. Deploy Kubernetes workloads
5. Test and validate

**Estimated Time**: 4-6 weeks

## Network Integration

### Leveraging Existing Infrastructure

The PCHP platform connects as a **spoke** to Parkland Hospital System's existing network:

- **ExpressRoute**: Use existing circuit, no new provisioning
- **VPN to Cognizant**: Leverage existing VPN, add routes for PCHP
- **Hub VNet**: Peer PCHP spoke to existing hub
- **DNS**: Use existing DNS infrastructure

### New PCHP-Specific Components

- Spoke VNet (10.200.0.0/16) for PCHP workloads
- Private endpoints for PCHP Azure services
- Network security groups for PCHP resources

### Security & Compliance

- ✅ All traffic via ExpressRoute (no public internet)
- ✅ Private endpoints for all Azure PaaS services
- ✅ HIPAA-compliant encryption (at rest and in transit)
- ✅ Premium Key Vault with HSM-backed keys
- ✅ 7-year audit log retention
- ✅ Automated security patching

## Repository Access

### For Parkland IT Team

The complete deployment package is available in a separate repository:

**Repository**: `parkland-pchp/integration-platform` (private)

**Contents**:
- ✅ Complete infrastructure templates (Bicep/ARM)
- ✅ "Deploy to Azure" button for one-click deployment
- ✅ Detailed cost estimates and breakdowns
- ✅ Architecture diagrams
- ✅ Step-by-step deployment guide
- ✅ Network integration instructions
- ✅ QNXT connectivity guide
- ✅ Okta configuration steps
- ✅ Operations migration roadmap

### How to Access

```bash
# Clone repository
git clone https://github.com/parkland-pchp/integration-platform.git

# View cost estimates
open docs/COST-ESTIMATE.md

# View architecture
open docs/architecture-diagram.svg

# View deployment guide
open docs/DEPLOYMENT-GUIDE.md
```

## Key Benefits

### For PCHP Members
- 📱 Access healthcare records via mobile app
- 🔐 Secure authentication via Okta
- 📄 Download records in multiple formats (PDF, CCD, FHIR)
- ⚡ Fast, responsive API (<500ms)

### For PCHP Operations
- 💰 55% cost savings over 3 years
- 🎯 Full control over integration operations
- 📈 Scalable infrastructure (auto-scaling)
- 🔧 Flexibility to add new capabilities
- 🚀 Faster time-to-market for new features

### For Parkland Hospital System
- 🏢 Leverage existing infrastructure investments
- 🔒 Maintain security and compliance standards
- 🌐 Consistent network architecture across subsidiaries
- 📊 Centralized monitoring and management

## Risks & Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Cost overruns | Medium | Low | Detailed cost tracking, budget alerts at 80% |
| Deployment delays | Medium | Medium | Phased approach, clear milestones |
| QNXT connectivity issues | High | Low | Test VPN connectivity before deployment |
| Okta integration problems | Medium | Low | Proof-of-concept in dev environment first |
| Member adoption | Medium | Medium | User testing, training, support resources |

## Success Metrics

### Phase 1 (3 months)
- [ ] 100% uptime for member API
- [ ] 10,000+ PCHP members registered
- [ ] <500ms API response time (P95)
- [ ] Zero security incidents
- [ ] 90%+ member satisfaction

### Phase 2-5 (12+ months)
- [ ] 50% of EDI processing migrated from BPass
- [ ] $100k+ annual savings realized
- [ ] <0.1% error rate on file processing
- [ ] 99.9% SLA achievement

## Next Steps

### Immediate (Week 1-2)
1. ✅ **Review repository** - Parkland IT team reviews deployment package
2. ✅ **Validate costs** - Finance team approves budget
3. ✅ **Network planning** - Coordinate with Parkland network team on VNet peering
4. ✅ **Security review** - Parkland security team validates architecture

### Short-term (Week 3-4)
5. 🔲 **Deploy dev environment** - Initial deployment for testing
6. 🔲 **Configure Okta** - Set up authentication for PCHP members
7. 🔲 **Test QNXT connectivity** - Validate VPN access to Cognizant
8. 🔲 **Deploy member API** - Initial service deployment

### Medium-term (Month 2-3)
9. 🔲 **User acceptance testing** - PCHP staff test member portal
10. 🔲 **Deploy UAT environment** - Full testing environment
11. 🔲 **Member pilot** - 100 members test mobile app
12. 🔲 **Production deployment** - Go-live with Phase 1

## Approval & Sign-Off

| Stakeholder | Role | Status |
|-------------|------|--------|
| PCHP CIO | Executive Sponsor | ⏳ Pending |
| PCHP CFO | Budget Approval | ⏳ Pending |
| Parkland Hospital System CTO | Infrastructure Approval | ⏳ Pending |
| Parkland Network Team | Network Integration | ⏳ Pending |
| Parkland Security Team | Security Review | ⏳ Pending |

## Questions & Support

**PCHP IT Team**:
- Email: itsupport@pchp.com
- Phone: +1-214-590-8000

**Repository Issues**:
- https://github.com/parkland-pchp/integration-platform/issues

**Deployment Support**:
- Schedule meeting with PCHP IT team

---

## Appendix: Repository Quick Access

### Key Documents

| Document | Location | Description |
|----------|----------|-------------|
| **Deployment Guide** | `docs/DEPLOYMENT-GUIDE.md` | Step-by-step deployment |
| **Cost Estimate** | `docs/COST-ESTIMATE.md` | Detailed cost breakdown |
| **Architecture** | `docs/architecture-diagram.svg` | Visual architecture |
| **Network Guide** | `docs/NETWORK-INTEGRATION.md` | ExpressRoute & VPN setup |
| **Okta Config** | `docs/OKTA-CONFIGURATION.md` | Authentication setup |
| **QNXT Integration** | `docs/QNXT-INTEGRATION.md` | Backend connectivity |

### Deploy Button

Click to deploy infrastructure to Azure:

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fparkland-pchp%2Fintegration-platform%2Fmain%2Fazuredeploy.json)

---

**Document Version**: 1.0  
**Classification**: Internal - Parkland Hospital System  
**Last Updated**: January 2026
