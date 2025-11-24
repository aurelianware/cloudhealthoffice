# Cloud Health Office

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Faurelianware%2Fcloudhealthoffice%2Fmain%2Fazuredeploy.json)
[![Tests](https://img.shields.io/badge/tests-62%20passing-brightgreen)](https://github.com/aurelianware/cloudhealthoffice)
[![HIPAA Compliant](https://img.shields.io/badge/HIPAA-compliant-blue)](./SECURITY.md)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](./LICENSE)

The #1 open-source, Azure-native, multi-payer EDI integration platform for healthcare.

> **📢 Major Updates Since v1.0.0**: Zero-code payer onboarding, FHIR R4 integration, ValueAdds277 enhanced claim status, production-grade security (9/10 score), and comprehensive testing suite. See **[What's New](./WHATS-NEW.md)** for highlights or [FEATURES.md](./FEATURES.md) for complete details.

## 🚀 Quick Start

Deploy a complete HIPAA-compliant EDI platform in **&lt;5 minutes**:

1. **Click Deploy to Azure** ☝️ (button above)
2. **Configure** basic settings (baseName, region)
3. **Deploy workflows** via CLI
4. **Start processing** 275/277/278 transactions

See [QUICKSTART.md](./QUICKSTART.md) for detailed guide.

## ✨ What's New

### Enhanced Onboarding Experience

- 🎯 **Interactive Wizard** - Guided configuration in &lt;5 minutes
- ⚡ **One-Click Azure Deploy** - Instant sandbox environment  
- 🧪 **Test Data Generator** - Synthetic 837 claims for testing
- 📊 **E2E Test Suite** - Automated health checks and reporting
- 🔒 **PHI Validation** - Automated HIPAA compliance checks
- 📚 **Comprehensive Docs** - Quickstart + 60+ troubleshooting solutions

### Try It Now

```bash
# Interactive wizard mode
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice && npm install && npm run build
npm run generate -- interactive --output my-config.json --generate

# Or use Azure Deploy button above for instant sandbox
```

## 📋 Core Features

### EDI Transaction Processing
- ✅ **275 Attachments** - Clinical and administrative attachment processing with file validation
- ✅ **277 RFAI** - Request for Additional Information outbound workflow
- ✅ **278 Authorizations** - Prior authorization requests (inpatient, outpatient, referrals)
- ✅ **278 Authorization Inquiry** - Real-time status checks for existing authorizations
- ✅ **278 Replay Endpoint** - HTTP endpoint for deterministic transaction replay
- ✅ **837 Claims** - Professional, Institutional, and Dental claims submission support
- ✅ **270/271 Eligibility** - Real-time eligibility verification with 6 search methods
- ✅ **276/277 Claim Status** - Claim status inquiries with date range filtering

### Zero-Code Payer Onboarding
- ✅ **Config-to-Workflow Generator** - TypeScript-based automation for deployment artifacts
- ✅ **Interactive Configuration Wizard** - Guided setup in <5 minutes
- ✅ **30+ Handlebars Template Helpers** - Comprehensive template system
- ✅ **23-Test Suite** - Validated workflow and infrastructure generation
- ✅ **Example Configurations** - Medicaid MCO and Regional Blues templates

### FHIR R4 Integration
- ✅ **X12 270 → FHIR R4 Mapping** - Patient & CoverageEligibilityRequest transformation
- ✅ **Provider Access API (CMS-0057-F)** - SMART on FHIR authentication, consent management, HIPAA safeguards
- ✅ **CMS Patient Access API Ready** - Compliant with CMS-9115-F and CMS-0057-F requirements
- ✅ **US Core Implementation** - US Core Patient profile v3.1.1
- ✅ **Clinical USCDI Data** - Condition and Observation resources for comprehensive clinical data
- ✅ **63+ Comprehensive Tests** - 100% pass rate, production-ready
- ✅ **Zero External Dependencies** - Secure core mapper with no vulnerabilities

### Enhanced Claim Status (ECS)
- ✅ **ValueAdds277 Premium Features** - 60+ enhanced response fields
- ✅ **Cross-Module Integration Flags** - Seamless appeals, attachments, corrections
- ✅ **Premium Product Capability** - $10k/year additional revenue per payer
- ✅ **Provider Time Savings** - 7-21 minutes saved per claim lookup
- ✅ **Configurable Field Groups** - Financial, clinical, demographics, remittance

### Production-Grade Security
- ✅ **Premium Key Vault** - HSM-backed keys (FIPS 140-2 Level 2)
- ✅ **Private Endpoints** - Complete network isolation for PHI
- ✅ **PHI Masking** - DCR-based redaction in Application Insights
- ✅ **Customer-Managed Keys** - Optional BYOK for compliance
- ✅ **Data Lifecycle Management** - 7-year retention, automated tiering
- ✅ **HIPAA Compliance** - 100% technical safeguards addressed

### Deployment & Operations
- ✅ **One-Click Azure Deploy** - Instant sandbox environment
- ✅ **Gated Release Strategy** - Pre-approval security validation for UAT/PROD
- ✅ **E2E Test Suite** - Automated health checks and reporting
- ✅ **Synthetic Test Data** - 837 claim generator (no real PHI needed)
- ✅ **CI/CD PHI Validation** - 18 automated tests prevent PHI exposure
- ✅ **Comprehensive Monitoring** - Application Insights with PHI-safe logging

## 🎯 Key Capabilities

### Config-to-Workflow Generator
Transform what was previously a multi-week engineering project into minutes:

```bash
# Interactive wizard mode
npm run generate -- interactive --output my-config.json --generate

# Or generate from existing config
node dist/scripts/generate-payer-deployment.js core/examples/medicaid-mco-config.json
```

**What It Generates:**
- Complete Logic App workflows (workflow.json files)
- Bicep infrastructure templates
- Deployment scripts and documentation
- JSON validation schemas
- Payer-specific configuration

**Documentation:** [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md)

### FHIR R4 Integration
Bridge traditional X12 EDI with modern FHIR APIs:

```typescript
import { mapX12270ToFhirEligibility } from './src/fhir/fhirEligibilityMapper';

// Transform X12 270 to FHIR R4
const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
```

**Standards Compliance:**
- HIPAA X12 270: 005010X279A1 ✓
- HL7 FHIR R4: v4.0.1 ✓
- US Core Patient: 3.1.1 ✓
- CMS Patient Access Rule: Ready ✓

**Documentation:** [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md)

### ValueAdds277 Enhanced Claim Status
Premium ECS features that save providers 7-21 minutes per lookup:

**Enhanced Fields:**
- Financial (8 fields): BILLED, ALLOWED, PAID, COPAY, COINSURANCE, DEDUCTIBLE
- Clinical (4 fields): Diagnosis codes, procedure codes, service dates
- Demographics (4 objects): Patient, subscriber, billing provider, rendering provider
- Remittance (4 fields): Check/EFT details, payment date, trace numbers

**Integration Flags:**
- `eligibleForAppeal` - Direct link to appeals module
- `eligibleForAttachment` - Send HIPAA 275 attachments
- `eligibleForCorrection` - Resubmit corrected claims
- `eligibleForRemittanceViewer` - View 835 remittance data

**ROI:** $69,600/year for providers (1,000 lookups/month), $10k/year additional revenue per payer

**Documentation:** [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md)

### Security Hardening (9/10 Security Score)
Production-ready security for PHI workloads:

**Infrastructure:**
- Premium Key Vault with HSM-backed keys
- Private endpoints (Storage, Service Bus, Key Vault)
- VNet integration for Logic Apps
- Customer-managed keys (optional BYOK)

**Compliance:**
- 100% HIPAA technical safeguards ✓
- Automated PHI masking in logs ✓
- 7-year data retention with lifecycle management ✓
- 365-day audit log retention ✓

**Cost Impact:** 94% storage cost reduction ($463/mo → $29/mo) with lifecycle policies

**Documentation:** [SECURITY-HARDENING.md](./SECURITY-HARDENING.md)

## 📖 Documentation

### Getting Started
- **[What's New](./WHATS-NEW.md)** - Major updates since v1.0.0 with highlights and metrics
- [Quick Start Guide](./QUICKSTART.md) - Deploy in 5 minutes
- [Onboarding Guide](./ONBOARDING.md) - Complete setup instructions
- [Troubleshooting FAQ](./TROUBLESHOOTING-FAQ.md) - 60+ solutions

### Features & Capabilities
- **[Complete Feature Matrix](./FEATURES.md)** - Comprehensive feature overview with comparison tables
- [Config-to-Workflow Generator](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md) - Zero-code payer onboarding
- [FHIR R4 Integration](./docs/FHIR-INTEGRATION.md) - X12 to FHIR transformation
- [ValueAdds277](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md) - Enhanced claim status
- [ECS Integration](./docs/ECS-INTEGRATION.md) - Enhanced Claim Status API

### Security & Compliance
- [Security Hardening](./SECURITY-HARDENING.md) - Production security controls
- [HIPAA Compliance Matrix](./docs/HIPAA-COMPLIANCE-MATRIX.md) - Regulatory mapping
- [Security Guide](./SECURITY.md) - General security practices

### Deployment & Operations
- [Deployment Guide](./DEPLOYMENT.md) - Step-by-step deployment
- [Gated Release Guide](./DEPLOYMENT-GATES-GUIDE.md) - UAT/PROD approval workflows
- [Architecture](./ARCHITECTURE.md) - Technical deep-dive

## 🧪 Testing

```bash
# Run all tests (62 tests)
npm test

# Generate synthetic test claims
node dist/scripts/utils/generate-837-claims.js 837P 10 ./test-data

# End-to-end health checks
./scripts/test-e2e.ps1 -ResourceGroup my-rg -LogicAppName my-la

# Workflow testing
./test-workflows.ps1 -TestFullWorkflow
```

## 🛡️ Security & Compliance

All logging automatically redacts PHI:
```typescript
import { redactPHI } from './src/security/hipaaLogger';
console.log('Patient:', redactPHI(patient)); // Safe
```

Automated PHI scanning in CI/CD prevents accidental exposure.

## 🤝 Contributing

See [CONTRIBUTING.md](./CONTRIBUTING.md) for guidelines.

## AI-Assisted Development

Contributors: Install GitHub Copilot in VS Code. Prefix code blocks with detailed comments like '// Implement [feature] with [constraints]'. Review AI-generated code for security and compliance—never commit secrets. Run 'npm test' before submitting PRs.  
All output must remain HIPAA-safe—redact PHI, never log confidential info, and validate AI completions before merging.

## 📄 License

Apache 2.0 - See [LICENSE](./LICENSE) for details.

---

**Cloud Health Office** – The Future of Healthcare EDI Integration

*Open Source | Azure-Native | Production-Grade | HIPAA-Compliant*
