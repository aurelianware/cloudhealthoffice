# Cloud Health Office - Onboarding Guide

## Welcome to Cloud Health Office

Welcome to **Cloud Health Office** – the premier open-source, Azure-native, multi-payer EDI integration platform. This guide will help you get started with the platform, understand key concepts, and ensure HIPAA-compliant operations.

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Quick Start](#quick-start)
4. [Architecture Overview](#architecture-overview)
5. [Configuration](#configuration)
6. [Development Workflow](#development-workflow)
7. [Testing](#testing)
8. [Deployment](#deployment)
9. [HIPAA Logging & Compliance](#hipaa-logging--compliance)
10. [Support & Resources](#support--resources)

---

## Overview

Cloud Health Office provides a complete EDI integration solution for healthcare payers, supporting:

- **HIPAA X12 Transactions**: 275 (Attachments), 277 (RFAI), 278 (Authorization)
- **Azure-Native Architecture**: Logic Apps, Service Bus, Data Lake Storage
- **Multi-Tenant SaaS**: Configuration-driven deployment for multiple payers
- **Production-Grade Security**: HIPAA-compliant logging, encryption, and audit trails

### Key Features

- ✅ **&lt;1 Hour Onboarding**: Rapid deployment with configuration templates
- ✅ **Zero Custom Code**: Template-driven workflow generation
- ✅ **Production HIPAA Controls**: Built-in compliance and security
- ✅ **Automated Testing**: Comprehensive test framework included
- ✅ **Scalable Architecture**: Azure-native services for enterprise scale

---

## Prerequisites

Before getting started, ensure you have:

### Required Tools

- **Azure Subscription**: Active Azure subscription with appropriate permissions
- **Azure CLI**: Version 2.50.0 or later ([Install](https://docs.microsoft.com/cli/azure/install-azure-cli))
- **Node.js**: Version 20.x or later ([Install](https://nodejs.org/))
- **PowerShell**: Version 7.x or later ([Install](https://docs.microsoft.com/powershell/scripting/install/installing-powershell))
- **Git**: For version control and repository management

### Azure Resources

- Resource Group for deployment
- Azure Storage Account (Data Lake Gen2)
- Azure Service Bus Namespace
- Azure Logic Apps Standard instance
- Azure Integration Account (for X12 processing)

### Access & Permissions

- Contributor role on Azure subscription or resource group
- Ability to create service principals (for CI/CD)
- Access to trading partner SFTP credentials (e.g., Availity)

---

## Quick Start

Get up and running in minutes with these steps:

### 1. Clone the Repository

```bash
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice
```

### 2. Install Dependencies

```bash
npm install
```

### 3. Build the Project

```bash
npm run build
```

### 4. Configure Your Payer

Create a configuration file in `config/payers/your-payer.json`:

```json
{
  "payerId": "YOUR_PAYER_ID",
  "payerName": "Your Health Plan",
  "azureRegion": "eastus",
  "enabledModules": ["275-attachments", "277-rfai", "278-authorization"]
}
```

### 5. Run Tests

```bash
npm test
```

### 6. Deploy to Azure

```bash
# Authenticate with Azure
az login

# Deploy infrastructure and workflows
./scripts/deploy-all.sh -g your-resource-group -p your-payer
```

---

## Architecture Overview

Cloud Health Office uses a modern, event-driven architecture:

### Core Components

1. **Logic Apps Workflows**: Stateful workflows for EDI processing
   - `ingest275`: Process 275 attachment submissions
   - `ingest278`: Process 278 authorization requests
   - `rfai277`: Generate and send 277 RFAI responses
   - `replay278`: Deterministic replay endpoint for 278 transactions

2. **Azure Service Bus**: Pub/sub messaging for decoupled processing
   - `attachments-in`: Published after 275 ingestion
   - `edi-278`: Published after 278 ingestion
   - `rfai-requests`: Triggers 277 RFAI generation

3. **Data Lake Storage**: Hierarchical storage for EDI files
   - Raw files: `hipaa-attachments/raw/{type}/{yyyy}/{MM}/{dd}/`
   - Archive: Date-partitioned for compliance and auditing

4. **Integration Account**: X12 encoding/decoding with trading partner agreements

### Data Flow

```
SFTP → Logic App → Integration Account (X12 Decode) → Service Bus → QNXT API
                                                              ↓
                                                        Data Lake Archive
```

---

## Configuration

### Payer Configuration Schema

Each payer requires a configuration file with the following structure:

```typescript
{
  payerId: string;           // Unique identifier (e.g., "BCBS-CA")
  payerName: string;         // Display name
  azureRegion: string;       // Azure region (e.g., "eastus")
  resourceGroupName: string; // Azure resource group
  
  tradingPartners: {
    clearinghouse: {
      name: string;          // e.g., "Availity"
      senderId: string;      // X12 sender ID
      receiverId: string;    // X12 receiver ID
      sftpHost: string;      // SFTP endpoint
      sftpUsername: string;  // SFTP credentials (use Key Vault)
    }
  };
  
  enabledModules: string[];  // Feature flags
  customConfig?: object;     // Extension point
}
```

### Environment Variables

Set these environment variables for local development:

```bash
AZURE_SUBSCRIPTION_ID=your-subscription-id
AZURE_RESOURCE_GROUP=your-resource-group
PAYER_CONFIG_PATH=./config/payers/your-payer.json
```

---

## Development Workflow

### Setting Up Development Environment

1. **Fork and Clone**: Fork the repository and clone locally
2. **Create Feature Branch**: `git checkout -b feature/your-feature`
3. **Make Changes**: Follow coding standards and conventions
4. **Run Tests**: `npm test` to ensure no regressions
5. **Build**: `npm run build` to verify TypeScript compilation
6. **Lint**: `npm run lint` to check code quality
7. **Commit**: Use conventional commit messages
8. **Push and PR**: Create pull request for review

### Branching Strategy

- `main`: Production-ready code
- `release/*`: Release candidates
- `feature/*`: New features
- `hotfix/*`: Critical production fixes

---

## Testing

### Running Tests

```bash
# Run all tests
npm test

# Run with coverage
npm test -- --coverage

# Run specific test file
npm test -- validator.test.ts

# Watch mode for development
npm test -- --watch
```

### Testing Workflows

Use the provided PowerShell test script to validate workflows:

```powershell
# Test inbound 275 processing
./test-workflows.ps1 -TestInbound275 -ResourceGroup "dev-rg" -LogicAppName "dev-la"

# Test outbound 277 RFAI
./test-workflows.ps1 -TestOutbound277

# Full end-to-end workflow
./test-workflows.ps1 -TestFullWorkflow
```

### Test Data

Sample test files are included:
- `test-x12-275-availity-to-healthplan.edi`: Sample 275 transaction
- `test-qnxt-response-payload.json`: Mock QNXT API response
- `test-x12-278-review-request.edi`: Sample 278 authorization request

---

## Deployment

### Manual Deployment

1. **Prepare Azure Environment**:
   ```bash
   az group create -n payer-attachments-rg -l eastus
   ```

2. **Deploy Infrastructure**:
   ```bash
   az deployment group create \
     -g payer-attachments-rg \
     -f infra/main.bicep \
     -p baseName=payer-attachments
   ```

3. **Deploy Workflows**:
   ```bash
   ./deploy-workflows.ps1 -ResourceGroup payer-attachments-rg -LogicAppName payer-attachments-la
   ```

### CI/CD Deployment

GitHub Actions workflows handle automated deployment:

- **DEV**: Auto-deploys on merge to `main`
- **UAT**: Auto-deploys on `release/*` branches
- **Production**: Manual approval required

See `.github/workflows/deploy.yml` for details.

### Health Checks

After deployment, verify:

```bash
# Check Logic App status
az webapp show -g your-rg -n your-logic-app --query state

# List workflows
az logicapp list-workflows -g your-rg --name your-logic-app

# Check Service Bus topics
az servicebus topic list --namespace-name your-sb-ns -g your-rg
```

---

## HIPAA Logging & Compliance

Cloud Health Office implements comprehensive HIPAA compliance measures, including audit logging and PHI redaction.

### Overview

HIPAA (Health Insurance Portability and Accountability Act) requires:
- **Audit Trails**: Log all access to Protected Health Information (PHI)
- **Data Redaction**: Never log raw PHI in production systems
- **Access Controls**: Track who accessed what data and when
- **Immutable Logs**: Maintain tamper-proof audit logs

### HIPAA Logger Module

The platform includes a purpose-built HIPAA logging module at `src/security/hipaaLogger.ts` that provides:

#### Core Functions

**1. PHI Detection and Redaction**

```typescript
import { isPHI, redactPHI, redactValue } from '../src/security/hipaaLogger';

// Check if a value contains PHI
const containsPHI = isPHI('123-45-6789'); // true (SSN pattern)

// Redact a single value
const redacted = redactValue('john.doe@example.com'); // "j*********m"

// Recursively redact PHI from an object
const patient = {
  firstName: 'John',
  lastName: 'Doe',
  ssn: '123-45-6789',
  mrn: 'MRN123456',
  claimNumber: 'CLM-2024-001' // Non-PHI, preserved
};

const safePatient = redactPHI(patient);
// Result: { firstName: 'J**n', lastName: 'D*e', ssn: '1********9', mrn: 'M********6', claimNumber: 'CLM-2024-001' }
```

**2. Audit Logging**

```typescript
import { logPHIAccess, createHIPAALogger } from '../src/security/hipaaLogger';

// Create a logger instance for a user
const logger = createHIPAALogger('user@healthplan.com', '192.168.1.1');

// Log data access
logger.logDataAccess('Patient', 'PAT-12345', 'VIEW');

// Log access denial
logger.logAccessDenied('Claim', 'CLM-67890', 'Insufficient permissions');

// Log data export
logger.logDataExport('Patient', 150, 'Analytics System');
```

**3. Validation**

```typescript
import { validateRedaction } from '../src/security/hipaaLogger';

const dataToLog = {
  claimId: 'CLM-2024-001',
  patientSSN: '123-45-6789', // Unredacted PHI!
  status: 'approved'
};

const validation = validateRedaction(dataToLog);
if (!validation.isValid) {
  console.error('PHI detected in log data:', validation.violations);
  // Output: ["root.patientSSN: Unredacted SSN detected"]
}
```

### Best Practices

#### DO:
- ✅ **Always redact before logging**: Use `redactPHI()` on any object that might contain PHI
- ✅ **Log all PHI access**: Use `logPHIAccess()` for audit trails
- ✅ **Validate redaction**: Use `validateRedaction()` before logging in production
- ✅ **Use managed identities**: Avoid storing credentials for PHI systems
- ✅ **Enable Application Insights**: Configure secure telemetry for production
- ✅ **Archive logs immutably**: Store audit logs in Azure Data Explorer or similar

#### DON'T:
- ❌ **Never log raw PHI**: Always redact SSN, MRN, DOB, names, addresses, etc.
- ❌ **Don't use console.log in production**: Use structured logging services
- ❌ **Don't hardcode PHI**: Even in tests, use realistic but fake data
- ❌ **Don't disable audit logging**: Required for HIPAA compliance
- ❌ **Don't store logs in unauthenticated locations**: Use secure, access-controlled storage

### Integration with Azure Services

**Application Insights Integration**

```typescript
import { ApplicationInsights } from '@azure/monitor-opentelemetry';

// Configure Application Insights with PHI redaction
const logger = createHIPAALogger(userId, ipAddress);

// Log events with redacted data
appInsights.trackEvent({
  name: 'ClaimProcessed',
  properties: redactPHI({
    claimId: claim.id,
    patientName: claim.patientName, // Will be redacted
    amount: claim.amount
  })
});
```

**Azure Monitor Logs**

All audit logs should be sent to Azure Monitor with:
- Role-Based Access Control (RBAC)
- Immutable storage policy
- Encryption at rest and in transit
- Retention policies per HIPAA requirements (minimum 6 years)

### Compliance Checklist

Before deploying to production, verify:

- [ ] All PHI fields are identified and redacted in logging code
- [ ] Audit logging is enabled for all PHI access points
- [ ] Application Insights is configured with proper redaction
- [ ] Azure Monitor workspace has RBAC configured
- [ ] Log retention policies meet HIPAA requirements (6+ years)
- [ ] Access to logs is restricted to authorized personnel
- [ ] Encryption is enabled for all log storage
- [ ] Incident response procedures are documented
- [ ] Regular security audits are scheduled

### Sample Implementation

Here's a complete example of HIPAA-compliant logging in a Logic App workflow:

```typescript
import { redactPHI, createHIPAALogger } from '../src/security/hipaaLogger';

// Initialize logger for the current user/system
const hipaaLogger = createHIPAALogger('system@healthplan.com', 'logic-app-ip');

// Process a 275 attachment
export async function process275Attachment(payload: any) {
  try {
    // Log access attempt (before processing)
    hipaaLogger.logDataAccess('Attachment', payload.attachmentId, 'PROCESS_275');

    // Redact PHI from any logging output
    const safePayload = redactPHI(payload);
    console.log('Processing attachment:', safePayload);

    // Process the attachment...
    const result = await processAttachment(payload);

    // Log successful completion
    hipaaLogger.logDataAccess('Attachment', payload.attachmentId, 'COMPLETED_275');

    return result;
  } catch (error) {
    // Log failure (with redacted error details)
    const safeError = redactPHI({ error: error.message, attachmentId: payload.attachmentId });
    console.error('Processing failed:', safeError);
    throw error;
  }
}
```

### Further Reading

- [HIPAA Security Rule](https://www.hhs.gov/hipaa/for-professionals/security/index.html)
- [Azure HIPAA Compliance](https://docs.microsoft.com/azure/compliance/offerings/offering-hipaa-us)
- [HIPAA Compliance Matrix](./docs/HIPAA-COMPLIANCE-MATRIX.md)
- [Security Implementation Guide](./SECURITY.md)

---

## Support & Resources

### Documentation

- **[README.md](./README.md)**: Project overview and quick reference
- **[ARCHITECTURE.md](./ARCHITECTURE.md)**: Detailed architecture documentation
- **[DEPLOYMENT.md](./DEPLOYMENT.md)**: Deployment guides and troubleshooting
- **[SECURITY.md](./SECURITY.md)**: Security best practices and guidelines
- **[CONTRIBUTING.md](./CONTRIBUTING.md)**: How to contribute to the project

### Sample Configurations

- **[Onboarding Worksheet](./docs/ONBOARDING-CONFIGURATION-WORKSHEET.md)**: Complete configuration checklist
- **[HIPAA Compliance Matrix](./docs/HIPAA-COMPLIANCE-MATRIX.md)**: HIPAA requirement mapping
- **[Trading Partners Guide](./HIPAA-X12-Agreements-Guide.md)**: X12 configuration reference

### Getting Help

- **Issues**: Report bugs or request features via [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
- **Discussions**: Community support via [GitHub Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions)
- **Documentation**: Browse the `/docs` directory for detailed guides

### Stay Updated

- **Roadmap**: See [ROADMAP.md](./ROADMAP.md) for planned features
- **Changelog**: Review [CHANGELOG.md](./CHANGELOG.md) for release notes
- **Releases**: Watch the repository for new releases

---

## Next Steps

After completing onboarding:

1. **Customize Configuration**: Tailor the payer configuration to your needs
2. **Set Up CI/CD**: Configure GitHub Actions for automated deployments
3. **Test Thoroughly**: Run comprehensive tests before production deployment
4. **Enable Monitoring**: Set up Application Insights and alerts
5. **Review Security**: Complete the security hardening checklist
6. **Train Team**: Ensure team members understand HIPAA compliance requirements
7. **Go Live**: Deploy to production with confidence

---

**Welcome to the future of healthcare EDI integration. Let's build something amazing together.**

For questions or support, reach out via GitHub Issues or Discussions.

---

*Cloud Health Office* – Powered by Azure, Built for Healthcare, Open for Everyone.
