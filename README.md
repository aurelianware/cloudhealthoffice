<div align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-cloudhealthoffice-sentinel-primary.svg">
    <source media="(prefers-color-scheme: light)" srcset="docs/images/logo-cloudhealthoffice-sentinel-primary.svg">
    <img alt="Cloud Health Office – Just emerged from the void" src="docs/images/logo-cloudhealthoffice-sentinel-primary.svg" width="100%">
  </picture>

  <p><em>Just emerged from the void</em></p>
</div>

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

### Marketing Site with Auto-Custom Domain

The repository includes a static marketing site (`/site` folder) that automatically deploys to Azure Static Web Apps with custom domain configuration.

**Automated Features:**
- Deploys to Azure Static Web Apps on push to `main` branch
- Automatically configures `cloudhealthoffice.com` as custom domain
- Generates DNS configuration instructions as workflow artifact
- Idempotent domain assignment (safe to re-run)
- HTTPS/SSL certificate provisioned automatically by Azure

**DNS Configuration:**

After deployment, download the `dns-configuration-instructions` artifact from the GitHub Actions workflow run. Add one of these DNS records:

- **For www subdomain (recommended):** CNAME record pointing `www` to the Static Web App hostname
- **For root domain (@):** ALIAS or ANAME record (if supported by DNS provider)

DNS verification typically completes in 5-10 minutes. Azure automatically provisions SSL/TLS certificates once DNS is verified.

**Manual Verification:**

```bash
# Check custom domain status
az staticwebapp hostname list \
  --name <base-name>-swa \
  --resource-group <resource-group-name>

# Test DNS propagation
dig cloudhealthoffice.com
```

For troubleshooting or manual deployment, see `.github/workflows/deploy-static-site.yml`.
