# Cloud Health Office

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Faurelianware%2Fcloudhealthoffice%2Fmain%2Fazuredeploy.json)
[![Tests](https://img.shields.io/badge/tests-62%20passing-brightgreen)](https://github.com/aurelianware/cloudhealthoffice)
[![HIPAA Compliant](https://img.shields.io/badge/HIPAA-compliant-blue)](./SECURITY.md)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](./LICENSE)

The #1 open-source, Azure-native, multi-payer EDI integration platform for healthcare.

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

## 📋 Features

- ✅ **HIPAA X12 Transactions**: 275 (Attachments), 277 (RFAI), 278 (Authorization)
- ✅ **Azure-Native Architecture**: Logic Apps, Service Bus, Data Lake Storage
- ✅ **Multi-Tenant SaaS**: Configuration-driven deployment
- ✅ **Production-Grade Security**: Built-in HIPAA compliance
- ✅ **&lt;1 Hour Onboarding**: Rapid deployment with templates
- ✅ **Automated Testing**: Comprehensive test framework
- ✅ **PHI Redaction**: Automatic PHI protection in logs

## 📖 Documentation

- [Quick Start Guide](./QUICKSTART.md) - Get started in 5 minutes
- [Onboarding Guide](./ONBOARDING.md) - Complete setup instructions
- [Troubleshooting FAQ](./TROUBLESHOOTING-FAQ.md) - 60+ solutions
- [Architecture](./ARCHITECTURE.md) - Technical deep-dive
- [Security](./SECURITY.md) - HIPAA compliance details

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
