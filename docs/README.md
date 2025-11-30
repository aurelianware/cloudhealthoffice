# CloudHealthOffice Documentation

This directory contains comprehensive documentation for the CloudHealthOffice platform.

## Key Documents

### Compliance & Regulatory

| Document | Description |
|----------|-------------|
| [WHITEPAPER-CMS-0057-F-COMPLIANCE.md](WHITEPAPER-CMS-0057-F-COMPLIANCE.md) | Executive whitepaper for CMS-0057-F compliance strategy |
| [CMS-0057-F-COMPLIANCE.md](CMS-0057-F-COMPLIANCE.md) | Technical compliance guide with API specifications |
| [HIPAA-COMPLIANCE-MATRIX.md](HIPAA-COMPLIANCE-MATRIX.md) | HIPAA security control mapping |
| [HIPAA-AUDIT-REPORT.md](HIPAA-AUDIT-REPORT.md) | Audit report template |

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
# Using npm script
npm run generate-pdf

# Or directly with ts-node
npx ts-node scripts/generate-whitepaper-pdf.ts

# View help
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
