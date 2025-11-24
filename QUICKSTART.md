# Cloud Health Office - Quick Start Guide

Deploy the #1 open-source Azure-native multi-payer EDI platform in **&lt;5 minutes**.

## 🚀 One-Click Azure Deployment

Deploy a complete sandbox environment with a single click:

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Faurelianware%2Fcloudhealthoffice%2Fmain%2Fazuredeploy.json)

### What Gets Deployed

The Azure Deploy button creates:

- ✅ **Logic App Standard** (WS1) - Workflow runtime
- ✅ **Azure Storage Gen2** - HIPAA-compliant data lake
- ✅ **Service Bus Namespace** - Event-driven messaging
- ✅ **Integration Account** (Free tier) - X12 EDI processing
- ✅ **Application Insights** - Monitoring and telemetry
- ✅ **Log Analytics Workspace** - Centralized logging

**Estimated cost**: ~$50-100/month for sandbox environment (scales with usage)

## 📋 Prerequisites

- Azure subscription with Contributor access
- Azure CLI installed ([Download](https://docs.microsoft.com/cli/azure/install-azure-cli))
- PowerShell 7+ or Bash
- Git
- Node.js 20+ (for workflow deployment)

## 🎯 Deployment Steps

### Step 1: Deploy Infrastructure (2 minutes)

Click the **Deploy to Azure** button above and configure:

| Parameter | Description | Example |
|-----------|-------------|---------|
| **baseName** | Unique name for resources | `myhealthplan` |
| **location** | Azure region | `eastus` |
| **sftpHost** | Clearinghouse SFTP host | `sftp.availity.com` |
| **sftpUsername** | SFTP username | `demo` |
| **deploymentEnvironment** | Environment type | `sandbox` |

Click **Review + Create** → **Create** (deployment takes ~3-4 minutes)

### Step 2: Clone and Build Workflows (1 minute)

```bash
# Clone repository
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice

# Install dependencies and build
npm install
npm run build
```

### Step 3: Deploy Workflows (2 minutes)

```powershell
# PowerShell - Deploy Logic App workflows
./deploy-workflows.ps1 `
  -ResourceGroup "your-resource-group" `
  -LogicAppName "your-logic-app-name"
```

```bash
# Bash alternative
az webapp deploy \
  --resource-group your-resource-group \
  --name your-logic-app-name \
  --src-path workflows.zip \
  --type zip
```

### Step 4: Configure X12 Integration (Optional)

If using X12 EDI processing:

```powershell
# Upload X12 schemas to Integration Account
./configure-x12-agreements.ps1 `
  -ResourceGroup "your-resource-group" `
  -IntegrationAccountName "your-ia-name"
```

### Step 5: Test Your Deployment (&lt;1 minute)

```powershell
# Run end-to-end tests
./scripts/test-e2e.ps1 `
  -ResourceGroup "your-resource-group" `
  -LogicAppName "your-logic-app-name" `
  -ServiceBusNamespace "your-sb-namespace"
```

## ✨ New Capabilities (Post v1.0.0)

### Interactive Onboarding Wizard

For a guided configuration experience with zero-code payer onboarding:

```bash
# Start interactive wizard
npm run generate -- interactive --output my-config.json --generate
```

The wizard will guide you through:
1. Basic payer information
2. Trading partner configuration
3. Module selection (Attachments, Authorizations, Appeals, ECS)
4. Infrastructure settings
5. Monitoring preferences

Then automatically generate your deployment package!

**Features:**
- 30+ Handlebars template helpers for customization
- Complete workflow.json generation for Logic Apps
- Bicep infrastructure templates with parameters
- Payer-specific documentation auto-generated
- Validated with 23-test comprehensive suite

**Documentation:** [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md)

### FHIR R4 Integration

Transform X12 EDI data to modern FHIR resources:

```typescript
import { mapX12270ToFhirEligibility } from './src/fhir/fhirEligibilityMapper';

// X12 270 eligibility inquiry
const x12Data = {
  inquiryId: 'INQ001',
  subscriber: { memberId: 'MEM123', firstName: 'John', lastName: 'Doe' }
};

// Convert to FHIR R4
const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
```

**Compliance:**
- ✅ CMS Patient Access API (CMS-9115-F)
- ✅ US Core Patient profile v3.1.1
- ✅ HIPAA X12 270: 005010X279A1
- ✅ HL7 FHIR R4: v4.0.1

**Documentation:** [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md)

### ValueAdds277 Enhanced Claim Status

Premium ECS features for comprehensive claim intelligence:

```bash
# Enable in configuration
"ecsModule": {
  "enabled": true,
  "valueAdds277": {
    "enabled": true,
    "claimFields": {
      "financial": true,    // 8 fields: BILLED, ALLOWED, PAID, etc.
      "clinical": true,     // 4 fields: diagnosis, procedures, dates
      "demographics": true, // Patient, subscriber, providers
      "remittance": true    // Check/EFT details
    },
    "integrationFlags": {
      "eligibleForAppeal": true,        // Link to appeals module
      "eligibleForAttachment": true,    // Send 275 attachments
      "eligibleForCorrection": true,    // Resubmit claims
      "eligibleForRemittanceViewer": true  // View 835 data
    }
  }
}
```

**Benefits:**
- Provider time savings: 7-21 minutes per claim lookup
- 60+ enhanced response fields
- Cross-module integration workflows
- Additional $10k/year revenue per payer

**Documentation:** [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md)

### Security Hardening (9/10 Score)

Production-ready security controls for PHI workloads:

```bash
# Deploy security modules
az deployment group create \
  --resource-group "payer-attachments-prod-rg" \
  --template-file infra/modules/keyvault.bicep
  
az deployment group create \
  --resource-group "payer-attachments-prod-rg" \
  --template-file infra/modules/networking.bicep
  
az deployment group create \
  --resource-group "payer-attachments-prod-rg" \
  --template-file infra/modules/private-endpoints.bicep
```

**Implemented Controls:**
- Premium Key Vault with HSM-backed keys (FIPS 140-2 Level 2)
- Private endpoints for Storage, Service Bus, Key Vault
- PHI masking in Application Insights with DCR transformation
- Customer-managed keys (optional BYOK)
- 7-year data retention with automated lifecycle management
- 365-day audit log retention

**Cost Optimization:** 94% storage savings ($463/mo → $29/mo) with lifecycle policies

**Documentation:** [SECURITY-HARDENING.md](./SECURITY-HARDENING.md)

### Gated Release Strategy

Secure deployment approvals for UAT and PROD:

**Features:**
- Pre-approval security validation (TruffleHog, PII/PHI scanning)
- Automated audit logging and compliance reporting
- Communication/notification strategy for stakeholders
- Emergency hotfix procedures
- Rollback automation

**Approval Requirements:**
- **UAT**: 1-2 approvers, triggers on `release/*` branches
- **PROD**: 2-3 approvers, manual workflow dispatch only

**Documentation:** [DEPLOYMENT-GATES-GUIDE.md](./DEPLOYMENT-GATES-GUIDE.md)

## 🧪 Testing

### Generate Test Data

```bash
# Generate synthetic 837 claims (PHI-safe test data)
node dist/scripts/utils/generate-837-claims.js 837P 10 ./test-data

# Generate 837I institutional claims
node dist/scripts/utils/generate-837-claims.js 837I 5 ./test-data
```

### Run Workflow Tests

```powershell
# Test 275 attachment ingestion
./test-workflows.ps1 -TestInbound275

# Test 277 RFAI outbound
./test-workflows.ps1 -TestOutbound277

# Full end-to-end workflow
./test-workflows.ps1 -TestFullWorkflow
```

### Health Check

```powershell
# Comprehensive health check with report
./scripts/test-e2e.ps1 `
  -ResourceGroup "your-rg" `
  -LogicAppName "your-la" `
  -ServiceBusNamespace "your-sb" `
  -ReportPath "./health-report.json"
```

## 📊 Monitoring

### Application Insights

Access telemetry at:
```
https://portal.azure.com/#@yourtenant/resource/subscriptions/{sub-id}/resourceGroups/{rg}/providers/microsoft.insights/components/{app-insights-name}
```

Key metrics to monitor:
- **Workflow Run Success Rate**: Target &gt;99%
- **Processing Latency**: Target &lt;5 seconds (275 ingestion)
- **Error Rate**: Target &lt;1%

### View Logs

```bash
# Recent Logic App logs
az webapp log tail --resource-group your-rg --name your-logic-app

# Service Bus metrics
az monitor metrics list \
  --resource /subscriptions/{sub-id}/resourceGroups/{rg}/providers/Microsoft.ServiceBus/namespaces/{sb-name} \
  --metric-names "IncomingMessages,OutgoingMessages"
```

## 🔒 HIPAA Compliance

Cloud Health Office implements comprehensive HIPAA controls:

### PHI Redaction

All logging automatically redacts Protected Health Information:

```typescript
import { redactPHI, createHIPAALogger } from './src/security/hipaaLogger';

// Redact PHI before logging
const safeData = redactPHI({ patientName: 'John Doe', ssn: '123-45-6789' });
console.log(safeData); // { patientName: 'J**n D*e', ssn: '1********9' }
```

### Audit Logging

```typescript
import { createHIPAALogger } from './src/security/hipaaLogger';

const logger = createHIPAALogger('user@healthplan.com', '192.168.1.1');
logger.logDataAccess('Patient', 'PAT-12345', 'VIEW');
```

### Compliance Checklist

Before production deployment:

- [ ] Enable Application Insights with PHI redaction
- [ ] Configure Azure Monitor for audit logs
- [ ] Set log retention to 6+ years (HIPAA requirement)
- [ ] Enable encryption at rest (Azure Storage)
- [ ] Enable encryption in transit (TLS 1.2+)
- [ ] Configure RBAC for data access
- [ ] Set up BAA (Business Associate Agreement) with Microsoft
- [ ] Enable Azure Security Center recommendations

## 🛠️ Troubleshooting

### Common Issues

**Issue**: Logic App workflows not visible after deployment

**Solution**: Wait 1-2 minutes for workflows to initialize, then refresh portal

---

**Issue**: SFTP connection test fails

**Solution**: 
1. Verify SFTP credentials in Key Vault
2. Check network connectivity (may need VNet integration)
3. Validate SFTP host allows Azure IP ranges

---

**Issue**: X12 decode fails

**Solution**:
1. Verify X12 schemas uploaded to Integration Account
2. Check trading partner agreements configured correctly
3. Validate EDI file format (ISA/GS segments)

---

**Issue**: Service Bus messages not processing

**Solution**:
1. Check workflow is enabled and running
2. Verify managed identity has Service Bus Data Receiver role
3. Check dead-letter queue for failed messages

### Get Help

- 📖 [Full Documentation](./ONBOARDING.md)
- 🐛 [Report Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
- 💬 [Community Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions)
- 📧 Email: support@aurelianware.com

## 🎓 Next Steps

After successful deployment:

1. **Configure Trading Partners**: Set up clearinghouse credentials and X12 agreements
2. **Enable Production Features**: Upgrade to Standard Integration Account for production workloads
3. **Set Up CI/CD**: Configure GitHub Actions for automated deployments
4. **Customize Workflows**: Extend templates for payer-specific requirements
5. **Train Your Team**: Review HIPAA compliance and operational procedures

## 📚 Additional Resources

- [Architecture Documentation](./ARCHITECTURE.md)
- [Deployment Guide](./DEPLOYMENT.md)
- [Security Best Practices](./SECURITY.md)
- [HIPAA Compliance Matrix](./docs/HIPAA-COMPLIANCE-MATRIX.md)
- [API Reference](./docs/API-REFERENCE.md)

---

**Cloud Health Office** – The Future of Healthcare EDI Integration

*Open Source | Azure-Native | Production-Grade | HIPAA-Compliant*
