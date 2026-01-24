# Parkland Community Health Plan - Integration Platform

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fparkland-pchp%2Fintegration-platform%2Fmain%2Fazuredeploy.json)

> **Note:** The "Deploy to Azure" button will work after you create the `parkland-pchp/integration-platform` repository and publish the `azuredeploy.json` file. Until then, use the manual deployment instructions below.

**Private Integration Environment for Parkland Community Health Plan**

*A subsidiary of Parkland Hospital System*

## Overview

This repository contains the infrastructure-as-code, deployment templates, and documentation for **Parkland Community Health Plan's** private healthcare integration platform. PCHP is a subsidiary of **Parkland Hospital System** operating in the Dallas-Fort Worth metroplex.

This is a **private deployment** connected to Parkland Hospital System's network infrastructure via ExpressRoute, with connectivity to Cognizant QNXT systems via existing VPN.

### Key Features

- ✅ **Member Interoperability API** - Okta-authenticated FHIR R4 API for PCHP member healthcare records access
- ✅ **File Ingestion Service** - Automated EDI file processing from Cognizant BPass
- ✅ **Kubernetes Architecture** - AKS-based for flexibility and control
- ✅ **Network Integration** - Spoke VNet connected to Parkland Hospital System ExpressRoute hub
- ✅ **HIPAA Compliant** - Enterprise-grade security and encryption
- ✅ **Scalable** - Support for gradual operations migration from Cognizant

### Organizational Context

```
Parkland Hospital System (Parent Company)
    └── Parkland Community Health Plan (PCHP) - Subsidiary
        └── Integration Platform (This Repository)
```

**Parkland Hospital System** provides the core network infrastructure (ExpressRoute, VPN to Cognizant) and Azure subscription. **PCHP** operates the integration platform as a spoke deployment within this infrastructure.

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│        Parkland Hospital System Network (Parent)                 │
│              (ExpressRoute Hub + VPN Gateway)                    │
└────────────────────────┬────────────────────────────────────────┘
                         │ ExpressRoute
                         │
┌────────────────────────▼────────────────────────────────────────┐
│         PCHP Integration Platform (Subsidiary)                   │
│              (Azure Spoke VNet - PCHP Operations)                │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │    Azure Kubernetes Service (3-6 nodes)                  │  │
│  │  • Member Interoperability API (PCHP members)            │  │
│  │  • File Ingestion Service (PCHP claims/EDI)              │  │
│  │  • FHIR Gateway (PCHP records)                           │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │    Azure Services (PCHP-dedicated resources)             │  │
│  │  • Key Vault (Premium HSM) - PCHP secrets                │  │
│  │  • Storage Account (Data Lake Gen2, GRS) - PCHP data     │  │
│  │  • Event Hub (Kafka protocol) - PCHP events              │  │
│  │  • Application Insights + Log Analytics - PCHP logs      │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────┬────────────────────────────────────────┘
                         │ Site-to-Site VPN (Parkland Hospital System)
                         │
┌────────────────────────▼────────────────────────────────────────┐
│     Cognizant Network (QNXT - PCHP Claims System)               │
│  • PCHP Claims API   • PCHP Member API   • BPass SFTP           │
└─────────────────────────────────────────────────────────────────┘
                         │
                         │ Internet (HTTPS)
                         │
┌────────────────────────▼────────────────────────────────────────┐
│              PCHP Member Apps (Mobile/Web)                       │
│         (Okta Authentication for PCHP members)                   │
└─────────────────────────────────────────────────────────────────┘
```

[**View Detailed Architecture Diagram**](./docs/architecture-diagram.svg)

## Cost Estimate

### Monthly Azure Spend (USD)

**PCHP-Dedicated Resources:**

| Service | Configuration | Monthly Cost |
|---------|--------------|--------------|
| **Azure Kubernetes Service** | 6 nodes (3 system + 3 worker), Standard_D4s_v3 | $730 |
| **Storage Account** | 1TB Data Lake Gen2, GRS, with lifecycle | $120 |
| **Key Vault** | Premium SKU with HSM | $175 |
| **Event Hub** | Standard tier, 2 throughput units | $155 |
| **Application Insights** | 50GB/month ingestion | $115 |
| **Log Analytics** | 100GB/month, 2-year retention | $230 |
| **VNet & Private Endpoints** | 10 private endpoints | $75 |
| **Data Transfer** | 500GB outbound | $45 |
| **Azure Monitor** | Alerts and dashboards | $30 |
| **Backup & DR** | Geo-redundant storage | $50 |
| **Subtotal** | **PCHP Development Environment** | **~$1,725/month** |
| **Subtotal** | **PCHP Production Environment** | **~$3,200/month** |

**Shared Parkland Hospital System Infrastructure (No Additional Cost to PCHP):**
- ExpressRoute Circuit (already provisioned by parent)
- VPN Gateway to Cognizant (existing connection)
- Hub VNet and routing infrastructure
- Corporate network security services

**Total PCHP Cost:**
- **Development**: ~$1,725/month
- **Production**: ~$3,200/month

**Notes:**
- Costs are for PCHP-dedicated resources only
- Network connectivity leverages existing Parkland Hospital System infrastructure
- Production includes additional nodes for high availability
- Costs may vary based on actual PCHP member usage patterns

[**View Detailed Cost Breakdown**](./docs/COST-ESTIMATE.md)

## Quick Start

### Prerequisites

- Azure subscription (managed by Parkland Hospital System)
- ExpressRoute hub VNet resource ID (from Parkland Hospital System network team)
- Azure CLI installed
- kubectl installed
- Contributor access to PCHP resource group

### Option 1: One-Click Deploy (Recommended)

Click the "Deploy to Azure" button above and fill in:

1. **Subscription**: Parkland Hospital System Azure subscription
2. **Resource Group**: `pchp-integration-rg` (or custom name)
3. **Region**: `centralus` (or preferred region)
4. **Hub VNet ID**: Resource ID of Parkland Hospital System ExpressRoute hub VNet
5. **Environment**: `dev`, `uat`, or `prod`

### Option 2: Deploy via Azure CLI

```bash
# 1. Clone repository
git clone https://github.com/parkland-pchp/integration-platform.git
cd integration-platform

# 2. Login to Azure
az login

# 3. Set subscription (Parkland Hospital System subscription)
az account set --subscription "Parkland Hospital System"

# 4. Create resource group for PCHP
az group create \
  --name pchp-integration-rg \
  --location centralus \
  --tags Organization="PCHP" ParentCompany="Parkland Hospital System"

# 5. Deploy infrastructure
az deployment group create \
  --resource-group pchp-integration-rg \
  --template-file azuredeploy.json \
  --parameters @azuredeploy.parameters.json

# 6. Get AKS credentials
az aks get-credentials \
  --resource-group pchp-integration-rg \
  --name pchp-integration-aks-dev

# 7. Deploy applications
kubectl apply -f k8s/
```

### Option 3: Deploy via Terraform (Alternative)

```bash
cd terraform
terraform init
terraform plan -var-file=pchp.tfvars
terraform apply -var-file=pchp.tfvars
```

## Repository Structure

```
pchp-integration-platform/
├── README.md                          # This file
├── azuredeploy.json                   # ARM template for Azure deployment
├── azuredeploy.parameters.json        # Parameters for deployment
├── LICENSE                            # Apache 2.0 License
├── docs/
│   ├── DEPLOYMENT-GUIDE.md           # Detailed deployment instructions
│   ├── COST-ESTIMATE.md              # Detailed cost breakdown
│   ├── ARCHITECTURE.md               # Architecture documentation
│   ├── NETWORK-INTEGRATION.md        # ExpressRoute & VPN setup
│   ├── OKTA-CONFIGURATION.md         # Okta setup guide for PCHP
│   ├── QNXT-INTEGRATION.md           # QNXT connectivity guide
│   ├── architecture-diagram.svg      # Architecture diagram
│   └── cost-breakdown.xlsx           # Cost calculator
├── infra/
│   ├── main.bicep                    # Main Bicep template
│   ├── modules/                      # Bicep modules
│   └── terraform/                    # Terraform alternative
├── k8s/
│   ├── namespaces/                   # Kubernetes namespaces
│   ├── member-api/                   # Member API deployment
│   ├── file-ingestion/               # File ingestion service
│   ├── fhir-gateway/                 # FHIR gateway
│   └── monitoring/                   # Prometheus/Grafana
├── helm/
│   └── pchp-services/                # Helm chart for all services
├── config/
│   ├── dev.json                      # Development config
│   ├── uat.json                      # UAT config
│   └── prod.json                     # Production config
└── scripts/
    ├── deploy.sh                     # Deployment script
    ├── setup-okta.sh                 # Okta configuration
    └── test-connectivity.sh          # Network connectivity tests
```

## Deployment Phases

### Phase 1: Member Interoperability API (Q1 2026) - CURRENT

**Services:**
- PCHP member registration and authentication via Okta
- FHIR R4 API for PCHP member healthcare records access
- Mobile app backend for PCHP members
- Record download in multiple formats (FHIR, PDF, CCD)

**Status:** Ready for deployment

**Deployment Steps:**
1. Deploy infrastructure (click button above)
2. Configure Okta application for PCHP
3. Deploy member API service
4. Test with sample PCHP member

**Estimated Timeline:** 2-3 weeks

### Phase 2: File Ingestion (Q2 2026) - PLANNED

**Services:**
- SFTP ingestion from Cognizant BPass (PCHP claims)
- X12 file validation and parsing
- Automated archival of PCHP EDI files
- Error handling and retry

**Estimated Timeline:** 4-6 weeks

### Phase 3: Claims Processing (Q3 2026) - PLANNED

**Services:**
- 837 PCHP claims submission
- 835 remittance processing
- Claim status inquiry for PCHP claims
- QNXT integration for PCHP

**Estimated Timeline:** 8-12 weeks

### Phase 4: Prior Authorization (Q4 2026) - PLANNED

**Services:**
- 278 prior auth requests for PCHP
- Da Vinci PAS API
- CRD integration
- SLA tracking for PCHP authorizations

**Estimated Timeline:** 8-12 weeks

### Phase 5: Full Operations (2027+) - FUTURE

**Scope:** Complete Cognizant BPass replacement for PCHP operations

## Security & Compliance

### HIPAA Compliance

- ✅ **Encryption at Rest**: All PCHP PHI encrypted with Azure SSE
- ✅ **Encryption in Transit**: TLS 1.2+ for all communications
- ✅ **Network Isolation**: Private endpoints, no public internet access
- ✅ **Access Control**: RBAC with managed identities
- ✅ **Audit Logging**: 7-year retention (2,555 days) for PCHP data
- ✅ **Key Management**: Premium Key Vault with HSM

### Network Security

- ✅ **ExpressRoute Only**: All traffic via Parkland Hospital System ExpressRoute
- ✅ **VPN to Cognizant**: Secure connectivity to QNXT (via Parkland Hospital System VPN)
- ✅ **Private Endpoints**: Storage, Key Vault, Event Hub (PCHP resources)
- ✅ **NSG Rules**: Restrictive network security groups
- ✅ **Network Policies**: Calico for pod-level security

### Authentication & Authorization

- ✅ **Okta Integration**: Enterprise SSO for PCHP members
- ✅ **Managed Identity**: Azure AD for service authentication
- ✅ **API Keys**: Stored in Key Vault, auto-rotated
- ✅ **OAuth 2.0**: Industry-standard authorization

## Support & Contacts

### PCHP IT Support

- **Email**: itsupport@pchp.com
- **Phone**: +1-214-590-8000 (PCHP extension)
- **On-call**: PCHP Integration team rotation

### Parkland Hospital System (Parent Company)

- **Network Team**: For ExpressRoute and VPN issues
- **Azure Admin**: For subscription and IAM questions
- **Security Team**: For compliance and audit support

### External Partners

- **Cognizant QNXT Support**: Per existing PCHP support contract
- **Okta Support**: PCHP enterprise support portal
- **Microsoft Azure Support**: Via Parkland Hospital System enterprise agreement

## Documentation

- [Deployment Guide](./docs/DEPLOYMENT-GUIDE.md) - Step-by-step deployment
- [Architecture Documentation](./docs/ARCHITECTURE.md) - Technical details
- [Cost Estimate](./docs/COST-ESTIMATE.md) - Detailed cost breakdown for PCHP
- [Network Integration](./docs/NETWORK-INTEGRATION.md) - ExpressRoute & VPN via Parkland Hospital System
- [Okta Configuration](./docs/OKTA-CONFIGURATION.md) - Authentication setup for PCHP
- [QNXT Integration](./docs/QNXT-INTEGRATION.md) - Backend connectivity for PCHP
- [Operations Migration](./docs/OPERATIONS-MIGRATION.md) - BPass replacement plan
- [Monitoring Guide](./docs/MONITORING.md) - Observability and alerts
- [Troubleshooting](./docs/TROUBLESHOOTING.md) - Common issues

## About PCHP

**Parkland Community Health Plan (PCHP)** is a Medicaid managed care organization and subsidiary of Parkland Hospital System, serving members in the Dallas-Fort Worth metroplex. This integration platform enables PCHP to:

- Provide members with digital access to their healthcare records
- Process EDI transactions (claims, eligibility, prior authorizations)
- Integrate with existing Cognizant QNXT claims system
- Gradually take on operations currently managed by Cognizant BPass
- Maintain HIPAA compliance and enterprise security standards

## License

Apache 2.0 - See [LICENSE](./LICENSE) for details.

---

**Repository**: Parkland Community Health Plan Integration Platform  
**Organization**: Parkland Community Health Plan (PCHP)  
**Parent Company**: Parkland Hospital System  
**Classification**: Internal Use Only  
**Last Updated**: January 2026
