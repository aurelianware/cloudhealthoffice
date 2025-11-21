<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-cloudhealthoffice-sentinel-primary.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/images/logo-cloudhealthoffice-sentinel-primary.svg">
    <img alt="Cloud Health Office – Just emerged from the void" src="docs/images/logo-cloudhealthoffice-sentinel-primary.svg" width="100%">
  </picture>

  <p><em>Just emerged from the void</em></p>
</div>

<br/>

# Cloud Health Office v1.0.0 — The Sentinel

**November 21, 2025**

**The monolith has landed.**  
Legacy EDI integration is now optional.

Cloud Health Office is the first open-source, Azure-native, HIPAA-engineered, multi-payer EDI platform that deploys a complete production tenant in **under one hour** — with zero custom code.

[![License](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Azure Logic Apps](https://img.shields.io/badge/Azure-Logic_Apps-0078D4?logo=microsoftazure)](https://azure.microsoft.com/services/logic-apps/)
[![HIPAA Compliant](https://img.shields.io/badge/HIPAA-Compliant-brightgreen)](docs/HIPAA-COMPLIANCE-MATRIX.md)

### What Just Changed Forever
| Traditional EDI Integration          | Cloud Health Office                     |
|--------------------------------------|-----------------------------------------|
| 6–18 months deployment              | < 1 hour                                |
| $500k–$2M professional services     | $0 (bring-your-own-subscription)        |
| Public endpoints & shared secrets    | Private endpoints only, HSM-backed keys |
| Custom code per payer                | Zero-code onboarding wizard             |
| Single-tenant silos                  | Unlimited payers, full isolation        |

### Core Capabilities (All Production-Ready Today)
- Availity, Change Healthcare, Optum 360, Inovalon, direct payer connectivity
- Real-time 278 prior-authorization with QNXT/Facets/HealthEdge correlation
- 837 claims (P/I/D), 270/271 eligibility, 276/277 status, appeals, attachments
- Full X12 Integration Account + deterministic replay endpoint
- ECS Enhanced Claim Status + Authorization Inquiry (X215) & Request (X217)

### Security That Actually Passes Audits
- Private Endpoints everywhere (no public IPs)
- DCR-based PHI redaction in Application Insights
- Premium Key Vault with HSM + soft-delete + purge protection
- Immutability policies + 7-year retention
- Managed identities only — no secrets in code

Full technical safeguards matrix → [HIPAA-COMPLIANCE-MATRIX.md](docs/HIPAA-COMPLIANCE-MATRIX.md)

### One-Click to Production (< 45 minutes)

```bash
# 1. Run the onboarding wizard
node dist/scripts/cli/payer-onboarding-wizard.js

# 2. Generate everything
node dist/scripts/cli/payer-generator-cli.js generate -c payer-config.json

# 3. Deploy
cd generated/your-payer/infrastructure && ./deploy.sh
```

## One-Click Deployment Package

Ready to deploy in under 1 hour? Download the complete production package:

[![Download Latest Release](https://img.shields.io/github/v/release/aurelianware/cloudhealthoffice?label=Download%20v1.0.0&style=for-the-badge&color=000000&logo=github)](https://github.com/aurelianware/cloudhealthoffice/releases/download/v1.0.0/cloudhealthoffice-v1.0.0.zip)

**Contains everything:**
- Full Bicep infrastructure templates
- All Logic App Standard workflows
- CLI onboarding wizard + config generator
- Security modules (Key Vault, Private Endpoints, PHI masking)
- Complete documentation  set
- Sample payer configurations

## Quick Start (45 minutes to production)

```bash
# 1. Download & unzip
curl -L -o cloudhealthoffice-v1.0.0.zip \
  https://github.com/aurelianware/cloudhealthoffice/releases/download/v1.0.0/cloudhealthoffice-v1.0.0.zip
unzip cloudhealthoffice-v1.0.0.zip -d cloudhealthoffice

# 2. Run the onboarding wizard
cd cloudhealthoffice
node dist/scripts/cli/payer-onboarding-wizard.js

# 3. Deploy
cd generated/your-payer-id/infrastructure
./deploy.sh
```