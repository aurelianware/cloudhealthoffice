# PCHP Repository Creation Guide

## Overview

This document explains how to create a separate repository for **Parkland Community Health Plan (PCHP)** that can be shared with Parkland IT team while keeping the Cloud Health Office SaaS commercial offering separate.

## Organizational Structure

```
Parkland Hospital System (Parent Company)
    └── Parkland Community Health Plan (PCHP) - Subsidiary
        └── Integration Platform (Separate Repository)
```

## Repository Separation Strategy

| Repository | Purpose | Audience | Content |
|------------|---------|----------|---------|
| **cloudhealthoffice** | Multi-tenant SaaS platform | Commercial customers, public | SaaS features, marketing, marketplace |
| **parkland-pchp-integration** | Private PCHP deployment | Parkland IT team only | PCHP-specific config, deployment templates, cost estimates |

## What Gets Separated

### Included in PCHP Repository

✅ Infrastructure templates (Bicep/ARM) for PCHP deployment  
✅ PCHP-specific configuration files  
✅ Deployment documentation for PCHP  
✅ Cost estimates for PCHP Azure resources  
✅ Architecture diagrams showing PCHP integration  
✅ Network integration guides (ExpressRoute, VPN)  
✅ QNXT integration documentation  
✅ Okta configuration for PCHP members  
✅ "Deploy to Azure" button  
✅ Kubernetes manifests for PCHP services  

### Excluded from PCHP Repository

❌ Cloud Health Office SaaS marketing materials  
❌ Multi-tenant platform features  
❌ Azure Marketplace listings  
❌ Commercial pricing models  
❌ Sales collateral and pitch decks  
❌ Other customer configurations  
❌ SaaS-specific automation  

## Quick Start

### Option 1: Automated Script

```bash
# Run the creation script
./scripts/create-parkland-repo.sh

# Output will be in ../parkland-pchp-integration/
```

### Option 2: Manual Creation

```bash
# 1. Create new directory
mkdir ../parkland-pchp-integration
cd ../parkland-pchp-integration

# 2. Initialize git
git init

# 3. Copy PCHP-specific files from cloudhealthoffice repo
cp -r ../cloudhealthoffice/parkland-repo/* .
cp ../cloudhealthoffice/config/parkland-pchp-config.json config/
cp ../cloudhealthoffice/infra/parkland-infrastructure.bicep infra/main.bicep

# 4. Compile Bicep to ARM for "Deploy to Azure" button
az bicep build --file infra/main.bicep --outfile azuredeploy.json

# 5. Create initial commit
git add .
git commit -m "Initial commit: PCHP Integration Platform"

# 6. Create GitHub repository (private)
gh repo create parkland-pchp/integration-platform --private

# 7. Push to GitHub
git remote add origin https://github.com/parkland-pchp/integration-platform.git
git branch -M main
git push -u origin main
```

## Repository Structure

The PCHP repository will have the following structure:

```
parkland-pchp-integration/
├── README.md                          # PCHP-specific README with Deploy button
├── LICENSE                            # Apache 2.0
├── azuredeploy.json                   # ARM template (compiled from Bicep)
├── azuredeploy.parameters.json        # Deployment parameters
├── .gitignore                         # Git ignore patterns
├── docs/
│   ├── DEPLOYMENT-GUIDE.md           # Step-by-step deployment
│   ├── COST-ESTIMATE.md              # Detailed monthly costs (~$1,725 dev, ~$3,200 prod)
│   ├── ARCHITECTURE.md               # Technical architecture
│   ├── NETWORK-INTEGRATION.md        # ExpressRoute & VPN setup
│   ├── OKTA-CONFIGURATION.md         # Okta setup for PCHP members
│   ├── QNXT-INTEGRATION.md           # Cognizant QNXT connectivity
│   ├── OPERATIONS-MIGRATION.md       # BPass replacement phases
│   ├── TROUBLESHOOTING.md            # Common issues
│   ├── architecture-diagram.svg      # Visual architecture
│   └── cost-breakdown.xlsx           # Cost calculator
├── infra/
│   ├── main.bicep                    # Main infrastructure template
│   ├── modules/                      # Bicep modules
│   │   ├── networking.bicep
│   │   ├── aks.bicep
│   │   ├── storage.bicep
│   │   ├── keyvault.bicep
│   │   └── monitoring.bicep
│   └── terraform/                    # Terraform alternative (optional)
├── k8s/
│   ├── namespaces/
│   │   ├── pchp-system.yaml
│   │   ├── pchp-services.yaml
│   │   └── pchp-workflows.yaml
│   ├── member-api/
│   │   ├── deployment.yaml
│   │   ├── service.yaml
│   │   ├── ingress.yaml
│   │   └── configmap.yaml
│   ├── file-ingestion/
│   │   ├── deployment.yaml
│   │   ├── cronjob.yaml
│   │   └── pvc.yaml
│   ├── fhir-gateway/
│   │   ├── deployment.yaml
│   │   └── service.yaml
│   └── monitoring/
│       ├── prometheus.yaml
│       └── grafana.yaml
├── helm/
│   └── pchp-services/
│       ├── Chart.yaml
│       ├── values.yaml
│       ├── values-dev.yaml
│       ├── values-uat.yaml
│       ├── values-prod.yaml
│       └── templates/
├── config/
│   ├── parkland-pchp-config.json     # PCHP configuration
│   ├── dev.json                      # Development overrides
│   ├── uat.json                      # UAT overrides
│   └── prod.json                     # Production overrides
└── scripts/
    ├── deploy.sh                     # Deployment automation
    ├── setup-okta.sh                 # Okta configuration
    ├── test-connectivity.sh          # Network tests
    └── validate-deployment.sh        # Post-deployment validation
```

## Sharing with Parkland IT

Once the repository is created and pushed to GitHub, share it with Parkland IT team:

1. **Grant Access**:
   ```bash
   # Add Parkland IT team members
   gh repo add-collaborator parkland-pchp/integration-platform parkland-it-user
   ```

2. **Share Repository URL**:
   ```
   https://github.com/parkland-pchp/integration-platform
   ```

3. **Provide Quick Start**:
   - README.md has "Deploy to Azure" button
   - Cost estimates in docs/COST-ESTIMATE.md
   - Architecture diagram at docs/architecture-diagram.svg
   - Full deployment guide at docs/DEPLOYMENT-GUIDE.md

## Maintaining Separation

### Future Updates

When updating Cloud Health Office SaaS features:
- ✅ Keep SaaS features in cloudhealthoffice repo
- ✅ Only copy PCHP-relevant updates to parkland-pchp-integration
- ✅ Don't mix commercial/marketing content

When updating PCHP deployment:
- ✅ Make changes directly in parkland-pchp-integration repo
- ✅ Don't copy PCHP-specific changes back to cloudhealthoffice
- ✅ Keep PCHP deployment private

### Version Control

- **cloudhealthoffice**: Main branch for SaaS releases, tags like v3.0.0, v3.1.0
- **parkland-pchp-integration**: Independent versioning, tags like pchp-v1.0.0, pchp-v1.1.0

## Cost Summary for IT Team

| Environment | Monthly Cost | Annual Cost |
|-------------|--------------|-------------|
| Development | $1,725 | $20,700 |
| UAT | $2,400 | $28,800 |
| Production | $3,200 | $38,400 |
| **Total** | **$7,325** | **$87,900** |

**Note**: Shared Parkland Hospital System infrastructure (ExpressRoute, VPN) has no incremental cost.

**3-Year TCO**: ~$289k (vs. $648k for Cognizant BPass - **55% savings**)

## Support

- **PCHP IT Support**: itsupport@pchp.com
- **Parkland Hospital System Network Team**: For ExpressRoute/VPN
- **Repository Issues**: https://github.com/parkland-pchp/integration-platform/issues

---

**Classification**: Internal Documentation  
**Last Updated**: January 2026  
**Owner**: PCHP IT Department
