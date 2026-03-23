# Cloud Health Office - Quick Start Guide

Get started with the #1 open-source Azure-native multi-payer EDI platform in **5 minutes**.

## 🌐 Self-Service Signup (Fastest - 5 minutes)

The easiest way to get started is through our production portal:

### Step 1: Visit the Portal

Navigate to **[portal.cloudhealthoffice.com](https://portal.cloudhealthoffice.com)**

### Step 2: Sign In with Azure AD

Click **Sign In** and authenticate with any Microsoft account:
- Personal Microsoft account (Outlook.com, Hotmail.com)
- Work or school account (Office 365, Azure AD)
- GitHub account linked to Microsoft

**What happens**: Azure AD multi-tenant authentication validates your identity and extracts tenant information from your account claims.

### Step 3: Select Your Tier

Choose the plan that fits your needs:

| Tier | Price | Claims/Month | Modules | Support | Trial |
|------|-------|--------------|---------|---------|-------|
| **Starter** | $499/mo | 10,000 | EDI + Claims Adjudication | Community + Email | 14 days |
| **Professional** | $1,499/mo | 50,000 | All + FHIR + Analytics | Priority + Slack | 14 days |
| **Enterprise** | Custom | Unlimited | White-label + SLA | 24/7 + Dedicated CSM | Custom |

**Note**: All tiers include a **14-day free trial**. Cancel anytime before Feb 23, 2026 (trial end).

### Step 4: Enter Payment Details

Provide credit card information via **Stripe** (PCI DSS compliant):
- Credit card (Visa, Mastercard, Amex, Discover)
- ACH Direct Debit (US customers)
- SEPA Direct Debit (EU customers)

**Security**: Stripe handles all payment processing. Cloud Health Office never stores your payment information.

### Step 5: Choose Modules

Select the EDI modules you need:
- ✅ **EDI Transactions** - 270/271 (Eligibility), 275 (Attachments), 276/277 (Claim Status), 278 (Authorization), 837 (Claims)
- ✅ **Claims Adjudication** - <500ms workflow processing
- ✅ **Provider Network** - 13 specialties, contracted rates
- ✅ **FHIR Integration** - X12 → FHIR R4 mapping (Professional+)

### Step 6: Start Free Trial

Click **Start Free Trial** to complete signup.

**What gets auto-provisioned:**
- ✅ Cosmos DB tenant partition (`tenantId`)
- ✅ SFTP credentials for clearinghouse integration
- ✅ Azure AD application for API access
- ✅ Stripe subscription with 14-day trial
- ✅ Welcome email with credentials and access URLs

### Step 7: Access Your Tenant

After signup, you can immediately access:

- **Portal**: [https://portal.cloudhealthoffice.com](https://portal.cloudhealthoffice.com) - Manage subscription, view claims, configure settings
- **API**: [https://api.cloudhealthoffice.com](https://api.cloudhealthoffice.com) - RESTful endpoints with OpenAPI docs
- **Docs**: [https://docs.cloudhealthoffice.com](https://docs.cloudhealthoffice.com) - Integration guides and API reference

**Trial Details:**
- Starts: Today
- Ends: Feb 23, 2026 (14 days)
- No credit card charge until trial ends
- Cancel anytime via Portal → Subscription → Cancel

### Enterprise Customers

Need custom pricing, SLAs, or white-labeling?

👉 **[Contact Sales](https://portal.cloudhealthoffice.com/contact-sales)** for:
- Unlimited claims processing
- Dedicated customer success manager
- Custom SLA (99.95%+ uptime)
- White-label portal and branding
- On-premise or hybrid deployment
- BAA and compliance support

---

## 🚀 Self-Hosted Deployment (Advanced)

For customers requiring on-premise or Azure-hosted deployment:

### One-Click Azure Deployment

Deploy a complete sandbox environment with a single click:

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Faurelianware%2Fcloudhealthoffice%2Fmain%2Fazuredeploy.json)

#### What Gets Deployed

**Note**: For most users, we recommend the **[SaaS platform at portal.cloudhealthoffice.com](#self-service-signup-fastest---5-minutes)** instead of self-hosted deployment.

The Azure Deploy button creates a self-hosted environment:

- ✅ **Kubernetes Cluster (AKS)** - Container orchestration
- ✅ **Argo Workflows** - Cloud-native workflow engine
- ✅ **Azure Storage Gen2** - HIPAA-compliant data lake
- ✅ **Service Bus Namespace** - Event-driven messaging
- ✅ **C# X12 Services** - X12 EDI parsing and encoding (runs on AKS)
- ✅ **Cosmos DB** - Multi-tenant data isolation
- ✅ **Application Insights** - Monitoring and telemetry

**Estimated cost**: ~$300-500/month for production environment (compare to SaaS: $499/mo Starter, $1,499/mo Professional)

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
| **sftpHost** | Clearinghouse SFTP host | `sftp.clearinghouse.example.com` |
| **sftpUsername** | SFTP username | `demo` |
| **deploymentEnvironment** | Environment type | `sandbox` |

Click **Review + Create** → **Create** (deployment takes ~3-4 minutes)

### Step 2: Clone and Deploy to Kubernetes (3 minutes)

```bash
# Clone repository
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice

# Connect to AKS cluster
az aks get-credentials --resource-group your-rg --name your-aks-cluster

# Deploy Argo Workflows
kubectl apply -f argo-workflows/

# Deploy microservices
kubectl apply -f k8s/
kubectl apply -f services/*/k8s/

# Verify deployment
kubectl get workflows -n cho-workflows
kubectl get pods -n cloudhealthoffice
```

### Step 3: Configure X12 Integration (Optional)

X12 EDI parsing is handled by C# microservices running on AKS (no Azure Integration Account needed). Configuration is via Kubernetes ConfigMaps:

```bash
# Apply X12 configuration
kubectl apply -f infrastructure/argo-workflows/config/x12-settings.yaml -n cloudhealthoffice
```

**Note**: The SaaS platform includes pre-configured X12 processing.

### Step 4: Test Your Deployment (&lt;1 minute)

```bash
# Run end-to-end tests with Argo Workflows
kubectl create -f tests/e2e-workflows/test-claim-workflow.yaml -n cho-workflows

# Monitor execution
kubectl get workflows -n cho-workflows -w

# View results
cat tests/E2E-TEST-RESULTS.md
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
- Complete Argo workflow YAML generation
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
  -AksCluster "your-aks" `
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
# Recent Argo workflow logs
kubectl logs -n cho-workflows -l workflows.argoproj.io/workflow --tail=100

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

**Issue**: Argo workflows not visible after deployment

**Solution**: Wait 1-2 minutes for workflow pods to initialize, then check with `kubectl get workflows -n cho-workflows`

---

**Issue**: SFTP connection test fails

**Solution**: 
1. Verify SFTP credentials in Key Vault
2. Check network connectivity (may need VNet integration)
3. Validate SFTP host allows Azure IP ranges

---

**Issue**: X12 decode fails

**Solution**:
1. Verify the C# X12 service pod is running (`kubectl get pods -n cloudhealthoffice -l app=x12-parser`)
2. Check X12 configuration in ConfigMap
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
2. **Enable Production Features**: Scale AKS node pools and configure production Argo workflow parameters
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
