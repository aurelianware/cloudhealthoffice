# Cloud Health Office - Azure Marketplace Offer

This directory contains all assets required for publishing Cloud Health Office on the Azure Marketplace.

## Directory Structure

```
marketplace/
├── managed-app/           # Azure Managed Application deployment
│   ├── mainTemplate.json  # ARM template for full stack deployment
│   ├── createUiDefinition.json  # Azure Portal deployment UI
│   └── viewDefinition.json      # Managed app dashboard definition
├── saas-offer/            # SaaS offer configuration
│   └── saas-offer-config.json   # Plans, meters, and pricing
├── legal/                 # Legal documents
│   ├── privacy-policy.md  # HIPAA-compliant privacy policy
│   ├── sla.md             # Service Level Agreement
│   └── support-terms.md   # Support terms and conditions
├── partnercenter/         # Microsoft Partner Center metadata
│   └── partnercenter-offer-metadata.json  # Complete offer listing
├── icons/                 # Marketplace icons (SVG format)
│   ├── logo-48x48.svg     # Small icon
│   ├── logo-90x90.svg     # Medium icon
│   ├── logo-216x216.svg   # Large icon
│   ├── logo-255x115.svg   # Wide icon
│   └── hero-815x290.svg   # Hero banner
└── README.md              # This file
```

## Offer Types

### 1. Managed Application

Deploys the complete Cloud Health Office stack to the customer's Azure subscription with managed access for updates and support.

**Key Files:**
- `mainTemplate.json`: ARM template deploying Logic Apps, Storage, Service Bus, Key Vault, etc.
- `createUiDefinition.json`: Multi-step wizard for deployment configuration
- `viewDefinition.json`: Custom dashboard for the managed application

**Deployment Tiers:**
- **Sandbox**: Development/testing ($150-300/month Azure costs)
- **Standard**: Production with geo-redundancy ($500-1000/month)
- **Enterprise**: High availability with zone redundancy ($1500-3000/month)

### 2. SaaS Offer

Fully managed SaaS deployment with meter-based billing for EDI transactions.

**Plans:**
| Plan | Base Price | 837 Claims Included | Target |
|------|-----------|-------------------|--------|
| Starter | [Contact sales](mailto:sales@cloudhealthoffice.com) | 1,000 | Small orgs, dev/test |
| Professional | [Contact sales](mailto:sales@cloudhealthoffice.com) | 10,000 | Mid-size orgs |
| Enterprise | [Contact sales](mailto:sales@cloudhealthoffice.com) | 50,000 | Large orgs |

**Meters:**
- `edi_837_transactions`: 837 Professional, Institutional, Dental claims
- `edi_278_transactions`: Prior authorization requests/responses
- `edi_275_transactions`: Clinical and administrative attachments
- `fhir_api_calls`: FHIR R4 API calls

## Legal Documents

### Privacy Policy
HIPAA-compliant privacy policy covering:
- PHI handling and Business Associate obligations
- Data retention and security measures
- Individual rights under HIPAA
- Third-party service providers

### Service Level Agreement (SLA)
- Uptime commitments by plan tier (99.5% - 99.95%)
- Service credits for downtime
- Performance targets
- Data durability and backup policies

### Support Terms
- Support channels and hours by plan
- Response time objectives
- Escalation procedures
- Customer responsibilities

## Icons

All icons follow the Cloud Health Office Sentinel branding guidelines:
- Absolute black backgrounds (#000000)
- Neon cyan (#00ffff) and green (#00ff88) accents
- Shield/monolith shape with all-seeing eye motif
- Circuit/holographic design elements

**Required Sizes for Azure Marketplace:**
- Small: 48x48 px
- Medium: 90x90 px
- Large: 216x216 px
- Wide: 255x115 px
- Hero: 815x290 px

## Publishing Checklist

### Before Submission
- [ ] Validate mainTemplate.json with ARM TTK
- [ ] Test createUiDefinition.json in sandbox
- [ ] Review all legal documents with legal counsel
- [ ] Verify pricing in Partner Center
- [ ] Convert SVG icons to PNG format
- [ ] Complete all Partner Center metadata fields

### Partner Center Configuration
1. Create new offer in Partner Center
2. Upload offer metadata from `partnercenter-offer-metadata.json`
3. Configure technical details for each plan
4. Upload icons and marketing assets
5. Set up pricing and marketplace distribution
6. Submit for certification

### Post-Submission
- [ ] Monitor certification status
- [ ] Address any certification feedback
- [ ] Test deployment in preview
- [ ] Publish to production

## Validation Commands

```bash
# Validate ARM template
az deployment group validate \
  --resource-group test-rg \
  --template-file marketplace/managed-app/mainTemplate.json \
  --parameters baseName=test organizationName="Test Org" adminEmail="test@test.com"

# Validate JSON files
jq . marketplace/managed-app/mainTemplate.json > /dev/null
jq . marketplace/managed-app/createUiDefinition.json > /dev/null
jq . marketplace/managed-app/viewDefinition.json > /dev/null
jq . marketplace/saas-offer/saas-offer-config.json > /dev/null
jq . marketplace/partnercenter/partnercenter-offer-metadata.json > /dev/null
```

## Converting SVG to PNG

Azure Marketplace requires PNG format for icons. Convert SVGs using:

```bash
# Using Inkscape (recommended)
inkscape --export-type=png --export-filename=logo-48x48.png logo-48x48.svg

# Using ImageMagick
convert -background none logo-48x48.svg logo-48x48.png
```

## References

- [Azure Marketplace Documentation](https://docs.microsoft.com/azure/marketplace/)
- [Managed Application Guide](https://docs.microsoft.com/azure/azure-resource-manager/managed-applications/)
- [Partner Center Documentation](https://docs.microsoft.com/partner-center/)
- [ARM Template Best Practices](https://docs.microsoft.com/azure/azure-resource-manager/templates/best-practices)

---

**Cloud Health Office** – Advancing Healthcare EDI Integration  
**Source-Available (BSL 1.1) | Azure-Native | HIPAA-Compliant**

© 2025 Cloud Health Office. All rights reserved.
