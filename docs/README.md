# CloudHealthOffice Documentation

This directory contains comprehensive documentation for the CloudHealthOffice platform.

## 🚀 v3.0.0 Documentation

CloudHealthOffice v3.0.0 — The Open Frontier Release delivers multi-cloud independence and commercial launch readiness.

| Document | Description |
|----------|-------------|
| [v3.0.0 Features Overview](./releases/v3.0.0-features-overview.md) | Comprehensive features matrix and capability overview |
| [v3.0.0 Release Notes](./releases/RELEASE_NOTES_v3.0.0.md) | Detailed release notes with upgrade instructions |
| [v3.0.0 Announcement](./announcements/v3.0.0-announcement.md) | Executive summary and stakeholder benefits |
| [Release Documentation Index](./releases/README.md) | All release documentation and migration guides |

## Key Documents

### Multi-Cloud & Deployment

| Document | Description |
|----------|-------------|
| [MULTI-CLOUD-DEPLOYMENT.md](MULTI-CLOUD-DEPLOYMENT.md) | Deploy on Azure, AWS (EKS), GCP (GKE), or any Kubernetes cluster |
| [ARGO-MIGRATION-GUIDE.md](ARGO-MIGRATION-GUIDE.md) | Migrate from Azure Logic Apps to Argo Workflows |
| [ARGO-OPERATIONS.md](ARGO-OPERATIONS.md) | Argo Workflows operational runbook |

### Compliance & Regulatory

| Document | Description |
|----------|-------------|
| [WHITEPAPER-CMS-0057-F-COMPLIANCE.md](security/WHITEPAPER-CMS-0057-F-COMPLIANCE.md) | Executive whitepaper for CMS-0057-F compliance strategy |
| [CMS-0057-F-COMPLIANCE.md](features/CMS-0057-F-COMPLIANCE.md) | Technical compliance guide with API specifications |
| [Compliance README](compliance/README.md) | Compliance document index and CMS-0057-F pilot package workflow |
| [CMS-0057-F-READINESS-MATRIX.md](compliance/CMS-0057-F-READINESS-MATRIX.md) | Canonical cross-service CMS-0057-F readiness matrix and gap record |
| [CMS-0057-F-COMPLIANCE-ACCELERATOR-BRIEF.md](compliance/CMS-0057-F-COMPLIANCE-ACCELERATOR-BRIEF.md) | Buyer-facing CMS-0057-F accelerator brief |
| [CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md](compliance/CMS-0057-F-PILOT-DILIGENCE-CHECKLIST.md) | Pilot diligence checklist for CMS-0057-F implementation planning |
| [HIPAA-COMPLIANCE-MATRIX.md](features/HIPAA-COMPLIANCE-MATRIX.md) | HIPAA security control mapping |
| [HIPAA-AUDIT-REPORT.md](features/HIPAA-AUDIT-REPORT.md) | Audit report template |
| [FL-AHCA-COMPLIANCE.md](compliance/FL-AHCA-COMPLIANCE.md) | Florida AHCA / SMMC 3.0 compliance guide — FMMIS, MPIP, encounter submission |

### Adjudication Engines

| Document | Description |
|----------|-------------|
| [ACCUMULATOR-ENGINE.md](engines/ACCUMULATOR-ENGINE.md) | Redis-backed deductible/OOP/visit accumulator engine — design, key layout, cache miss/rebuild, and DI wiring |
| [FEE-SCHEDULE-ENGINE.md](engines/FEE-SCHEDULE-ENGINE.md) | Rate resolution engine — MPFS RVU calc, modifier rules, provider contracts, and persistence |

### Technical Guides

| Document | Description |
|----------|-------------|
| [FHIR-INTEGRATION.md](FHIR-INTEGRATION.md) | FHIR R4 API integration guide |
| [PRIOR-AUTHORIZATION-API.md](PRIOR-AUTHORIZATION-API.md) | Prior authorization workflow APIs |
| [PATIENT-ACCESS-API.md](PATIENT-ACCESS-API.md) | Patient Access API documentation |
| [BACKEND-INTERFACE.md](BACKEND-INTERFACE.md) | Backend integration specifications |

### Implementation

| Document | Description |
|----------|-------------|
| [CONFIG-TO-WORKFLOW-GENERATOR.md](CONFIG-TO-WORKFLOW-GENERATOR.md) | Configuration generator guide |
| [ONBOARDING-CONFIGURATION-WORKSHEET.md](ONBOARDING-CONFIGURATION-WORKSHEET.md) | Payer onboarding worksheet |
| [AZURE-MONITOR-DASHBOARDS.md](AZURE-MONITOR-DASHBOARDS.md) | Monitoring setup guide |

---

## Generating PDF from Whitepaper

The CMS-0057-F compliance whitepaper can be converted to a professional PDF document for offline distribution and executive presentations.

### Prerequisites

1. **pandoc** - Universal document converter
   ```bash
   # macOS
   brew install pandoc
   
   # Ubuntu/Debian
   sudo apt-get install pandoc
   
   # Windows
   choco install pandoc
   ```

2. **weasyprint** - PDF rendering engine
   ```bash
   pip install weasyprint
   ```

3. **mermaid-filter** (optional) - Render Mermaid diagrams
   ```bash
   npm install -g mermaid-filter
   ```

### Generate PDF

From the repository root:

```bash
# Using npm script (recommended)
npm run generate-pdf

# Or directly with bash script
./scripts/generate-whitepaper-pdf.sh

# Or with TypeScript (alternative)
npx ts-node scripts/generate-whitepaper-pdf.ts

# View help (TypeScript version)
npx ts-node scripts/generate-whitepaper-pdf.ts --help
```

### Output

The generated PDF will be saved to:
```
docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.pdf
```

### Customizing PDF Style

The PDF styling is controlled by `docs/whitepaper-style.css`. Key customization options:

- **Page size**: Default is US Letter (8.5" × 11")
- **Margins**: 1 inch on all sides
- **Typography**: Segoe UI font family
- **Colors**: Blue (#3498db) accent color
- **Dark mode**: Supported for screen viewing

### Note on Mermaid Diagrams

If `mermaid-filter` is not installed, Mermaid code blocks will appear as syntax-highlighted code in the PDF. For fully rendered diagrams:

1. Install mermaid-filter: `npm install -g mermaid-filter`
2. Ensure Chrome/Chromium is available for headless rendering
3. Re-run the PDF generation script

Alternatively, pre-render Mermaid diagrams to PNG and reference them as images.

---

## Contributing to Documentation

When adding or updating documentation:

1. Use Markdown format (`.md` files)
2. Follow the existing document structure
3. Add entries to this README for new documents
4. Test rendering with the site build: `npm run build:site`
5. For whitepapers, test PDF generation: `npm run generate-pdf`

### Style Guidelines

- Use sentence case for headings
- Include table of contents for documents > 500 lines
- Add alt text for all images
- Use Mermaid for diagrams (with HTML comment alt text)
- Include footnotes for citations

---

## Support

- **Issues**: [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
- **Email**: support@aurelianware.com
