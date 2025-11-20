# HIPAA Attachments Processing System 🏥

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)

A complete Azure Logic Apps solution for processing HIPAA-compliant medical attachments with secure file handling, message queuing, and monitoring capabilities.

## 🔒 Security Architecture

This system implements **comprehensive HIPAA-compliant security controls** for Protected Health Information (PHI) workloads:

### Security Score
- **Current**: 9/10 (Production-ready for PHI)
- **HIPAA Compliance**: 100% (All technical safeguards addressed)

### Core Security Features

#### 1. Azure Key Vault Integration
- **Premium SKU** with HSM-backed keys (FIPS 140-2 Level 2)
- **Centralized secret management** for SFTP, QNXT API, connection strings
- **Soft delete** (90-day retention) + **purge protection**
- **RBAC authorization** with managed identity access
- **Network isolation** via private endpoints
- **Audit logging** (365-day retention in Log Analytics)

#### 2. Private Endpoints & Network Isolation
- **VNet integration** for Logic App Standard (10.0.0.0/16)
- **Private endpoints** for Storage (blob), Service Bus (namespace), Key Vault (vault)
- **Private DNS zones** for name resolution
- **Public access disabled** on all PHI resources
- **Zero public internet exposure** for PHI data paths

#### 3. PHI Masking in Application Insights
- **Data Collection Rules** with transformation queries
- **Automated masking** of member IDs, SSN, claim numbers, provider NPIs
- **Regex-based pattern detection** and redaction
- **Real-time monitoring** for unmasked PHI
- **Compliance alerts** for policy violations

#### 4. HTTP Endpoint Authentication
- **Azure AD Easy Auth** for replay278 endpoint
- **JWT token validation** with audience claim verification
- **OAuth 2.0** client credentials flow
- **401 Unauthorized** responses for missing/invalid tokens
- **Token expiration** (1-hour lifetime)

#### 5. Data Lifecycle Management
- **Automated tier transitions**: Hot → Cool (30 days) → Archive (90 days)
- **Retention policies**: 7-year HIPAA compliance
- **Secure deletion** after retention period
- **Cost optimization**: 89% storage cost savings
- **Blob versioning** and soft delete enabled

#### 6. Customer-Managed Keys (Optional)
- **BYOK encryption** support for organizations requiring key control
- **RSA-HSM keys** (4096-bit) in Premium Key Vault
- **Automatic key rotation** every 90 days
- **Full encryption key lifecycle** management

### Security Documentation

For comprehensive security guidance, see:
- **[SECURITY-HARDENING.md](SECURITY-HARDENING.md)** - 400+ line complete security implementation guide
- **[docs/HIPAA-COMPLIANCE-MATRIX.md](docs/HIPAA-COMPLIANCE-MATRIX.md)** - Regulatory mapping to HIPAA technical safeguards
- **[docs/PHI-SCANNER-GUIDE.md](docs/PHI-SCANNER-GUIDE.md)** - PHI/PII scanner usage and troubleshooting guide
- **[SECURITY.md](SECURITY.md)** - Security practices, encryption requirements, and incident response
- **[DEPLOYMENT.md](DEPLOYMENT.md)** - Includes Key Vault deployment and configuration steps

### Security Modules

The infrastructure includes modular security components:
- `infra/modules/keyvault.bicep` - Premium Key Vault with full HIPAA compliance
- `infra/modules/networking.bicep` - VNet, subnets, private DNS zones
- `infra/modules/private-endpoints.bicep` - Private endpoint configurations
- `infra/modules/cmk.bicep` - Optional customer-managed keys

## 🏗️ Architecture Overview

This solution implements the following process:
1. **SFTP Polling**: Monitors Availity SFTP for new 275 attachment files (ingest275 workflow)
2. **Data Lake Archive**: Stores raw files in Azure Data Lake Storage Gen2 with date-based partitioning
3. **X12 Decoding**: Processes 275/278 messages via Integration Account
4. **Metadata Extraction**: Extracts claim, member, and provider information
5. **QNXT Integration**: Links attachments to claims via QNXT API
6. **Service Bus**: Publishes events for downstream processing
7. **Appeals Processing**: Consumes attachment events, detects appeals, registers with QNXT (process_appeals workflow)
8. **RFAI Processing**: Handles Request for Additional Information via rfai277 workflow
9. **278 Transaction Processing**: Processes Health Care Services Review Information via ingest278 workflow
10. **Authorization Processing**: Processes 278 authorization requests/responses, generates 277 responses (process_authorizations workflow)
11. **Deterministic Replay**: HTTP endpoint for replaying 278 transactions via replay278 workflow

## ⚙️ Configuration-Driven Multi-Payer Platform

**Platform Architecture**: This system is designed as a **payer-agnostic platform** that supports multiple health plans through unified configuration, enabling **zero-code payer onboarding** and rapid deployment.

### Platform Capabilities

The platform supports **configuration-driven deployment** for all Availity transaction types:

- **837 Claims** - Professional, Institutional, and Dental claims submission
- **270/271 Eligibility** - Real-time eligibility verification and benefit inquiries  
- **276/277 Claim Status** - Claim status inquiries
- **Appeals** - Appeals submission and tracking
- **275 Attachments** - Clinical attachments
- **ECS (Enhanced Claim Status)** - Advanced claim status with extended data

### Key Benefits

✅ **Zero-Code Onboarding** - Add new payers by creating a configuration file (no custom development)  
✅ **Backend Agnostic** - Works with any claims processing system (QNXT, FacetsRx, TriZetto, etc.)  
✅ **Rapid Deployment** - Deploy new payer in days instead of months  
✅ **Multi-Transaction Support** - Enable any combination of modules  
✅ **Self-Service Configuration** - Interactive onboarding wizard with guided setup  
✅ **Multi-Tenant Architecture** - Single codebase serves multiple payers with isolated configuration  
✅ **Automated Validation** - Schema-based validation ensures configuration correctness

### How Platform Onboarding Works

#### Step 1: Create Configuration (15-30 minutes)

Use the interactive onboarding wizard:
```bash
node dist/scripts/cli/payer-onboarding-wizard.js
```

Or create configuration file manually following the Unified Schema.

#### Step 2: Validate Configuration (&lt;1 minute)

```bash
node dist/scripts/cli/payer-generator-cli.js validate payer-config.json
```

Schema validation ensures all requirements met.

#### Step 3: Generate Deployment Package (&lt;1 minute)

```bash
node dist/scripts/cli/payer-generator-cli.js generate -c payer-config.json
```

Generator creates complete deployment with:
- Logic App workflows
- Bicep infrastructure templates
- API connection configurations
- Custom documentation

#### Step 4: Deploy to Azure (15-30 minutes)

```bash
cd generated/{config.payerId}/infrastructure
./deploy.sh
```

Automated deployment provisions all Azure resources.

**Total Time: Production-ready in under 1 hour**

### Quick Start Example

**1. Create a payer configuration:**

```json
{
  "$schema": "core/schemas/availity-integration-config.schema.json",
  "organizationName": "Your Health Plan",
  "payerId": "YOUR_PAYER_ID",
  "payerName": "Display Name",
  "contacts": {
    "technical": {
      "name": "John Smith",
      "email": "john.smith@example.com",
      "phone": "5551234567"
    },
    "accountManager": { ... },
    "escalation": { ... }
  },
  "geography": {
    "nationwide": true
  },
  "modules": {
    "appeals": {
      "enabled": true,
      "transactionModes": {
        "realtime_web": {
          "enabled": true,
          "testUrl": "https://test-api.example.com/appeals",
          "prodUrl": "https://api.example.com/appeals"
        }
      },
      "requestReasons": ["Medical Necessity", "Timely Filing"],
      "maxFilesPerAppeal": 10
    }
  }
}
```

**2. Validate the configuration:**

```typescript
import { validateConfig } from './core/validation/config-validator';
import schema from './core/schemas/availity-integration-config.schema.json';

const result = validateConfig(config, schema);
if (result.valid) {
  console.log('✓ Configuration is valid');
}
```

**3. Load and use the configuration:**

```typescript
import { AvailityIntegrationConfig } from './core/interfaces/availity-integration-config.interface';

const config: AvailityIntegrationConfig = loadConfig('payer-config.json');
const appealsUrl = config.modules.appeals.transactionModes.realtime_web.prodUrl;
```

### Configuration Resources

📄 **[Complete Documentation](docs/UNIFIED-CONFIG-SCHEMA.md)** - Full schema reference and guide  
🔧 **[JSON Schema](core/schemas/availity-integration-config.schema.json)** - JSON Schema Draft-07  
📝 **[TypeScript Interfaces](core/interfaces/availity-integration-config.interface.ts)** - Type definitions  
✅ **[Validator](core/validation/config-validator.ts)** - Configuration validation module  
📋 **[Example: Medicaid MCO](core/examples/medicaid-mco-config.json)** - Nationwide MCO with Appeals + ECS  
📋 **[Example: Regional BCBS](core/examples/regional-blues-config.json)** - Regional plan with all modules

### Supported Modules

Each module can be independently enabled with its own configuration:

| Module | Transaction Types | Key Features |
|--------|------------------|--------------|
| **Claims 837** | 837P, 837I, 837D | Professional, Institutional, Dental claims |
| **Eligibility 270/271** | 270/271 | Real-time eligibility with 6 search patterns |
| **Claim Status 276/277** | 276/277 | Claim status inquiries with date ranges |
| **Appeals** | Appeals API | 8 sub-statuses, attachment support, multi-claim |
| **Attachments 275** | 275 | Clinical/admin attachments with file validation |
| **ECS** | ECS API | 4 query methods with extended claim data |

### Transaction Modes

Each module supports multiple transaction modes:

- **Real-time Web** - HTTP/HTTPS API endpoints
- **Real-time B2B** - B2B API integrations  
- **EDI Batch** - SFTP batch file processing

### Migration from Hardcoded Configs

See **[Migration Guide](docs/UNIFIED-CONFIG-SCHEMA.md#migration-guide)** for step-by-step instructions on converting from hardcoded payer configurations to the unified schema.

### Extending the Schema

The schema is designed for extensibility. See **[How to Extend](docs/UNIFIED-CONFIG-SCHEMA.md#how-to-extend-the-schema)** for guidance on:
- Adding new modules
- Adding new fields to existing modules
- Versioning the schema
- Maintaining backward compatibility

## 📦 Current Production Deployment

### ✅ Successfully Deployed Infrastructure
- **Resource Group**: `rg-hipaa-logic-apps` (West US)
- **Logic App Standard**: `hipaa-logic-la` ✅ RUNNING
- **Storage Account**: `hipaa7v2rrsoo6tac2` (Data Lake Gen2 enabled) ✅
- **Service Bus Namespace**: `hipaa-logic-svc` (Standard tier) ✅
  - Topics: `attachments-in`, `rfai-requests`, `edi-278`, `appeals-auth`, `auth-statuses`, `dead-letter` ✅
- **Application Insights**: `hipaa-logic-ai` ✅

### 🌐 Resource URLs
- **Logic App Portal**: <https://hipaa-logic-la.azurewebsites.net>
- **Azure Management**: [Logic App Designer](https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/rg-hipaa-logic-apps/providers/Microsoft.Web/sites/hipaa-logic-la/logicApp)

## 🔧 Components
- **Application Insights**: Telemetry and monitoring

### Workflows
- `logicapps/workflows/ingest275/workflow.json`: Main 275 ingestion workflow
- `logicapps/workflows/process_appeals/workflow.json`: Appeals processing workflow (consumes from attachments-in topic)
- `logicapps/workflows/rfai277/workflow.json`: Outbound 277 RFAI workflow
- `logicapps/workflows/ingest278/workflow.json`: X12 278 transaction processing workflow
- `logicapps/workflows/process_authorizations/workflow.json`: Authorization request/response processing workflow
- `logicapps/workflows/replay278/workflow.json`: HTTP endpoint for deterministic 278 replay
- `logicapps/workflows/ecs_summary_search/workflow.json`: Enhanced Claim Status (ECS) summary search workflow

### Key Features
- **Data Lake Storage**: Files stored with `hipaa-attachments/raw/{275|278|authorizations}/yyyy/MM/dd/` partitioning
- **Retry Logic**: 4 retries with 15-second intervals for QNXT API calls
- **Error Handling**: Service Bus dead-letter support
- **Monitoring**: Application Insights integration
- **278 Replay Endpoint**: HTTP trigger for deterministic transaction replay
- **Authorization Processing**: Complete authorization lifecycle from request through 277 response generation
- **ECS Summary Search**: Enhanced Claim Status queries via HTTP endpoint with four search methods (Service Date, Member, Check Number, Claim History)

## 🤖 Automated Payer Onboarding

### Config-to-Workflow Generator

**Zero-code payer onboarding** - Generate complete deployments from configuration files!

The Config-to-Workflow Generator automatically creates all deployment artifacts (workflows, infrastructure, documentation) from a single JSON configuration file, eliminating manual coding for new payer integrations.

#### Quick Start

```bash
# Install dependencies
npm install

# Build the generator
npm run build

# Generate deployment from example config
node dist/scripts/generate-payer-deployment.js core/examples/medicaid-mco-config.json

# Or use the CLI
node dist/scripts/cli/payer-generator-cli.js generate -c my-payer-config.json
```

#### What Gets Generated

From a single configuration file, you get:
- ✅ Complete Logic App workflows (process_appeals, ecs_summary_search, ingest275, etc.)
- ✅ Bicep infrastructure templates with deployment scripts
- ✅ Comprehensive documentation (DEPLOYMENT.md, CONFIGURATION.md, TESTING.md)
- ✅ JSON schemas for validation
- ✅ Ready-to-deploy package

#### Documentation

- **[docs/CONFIG-TO-WORKFLOW-GENERATOR.md](docs/CONFIG-TO-WORKFLOW-GENERATOR.md)** - Complete generator documentation
- **Examples**: 
  - `core/examples/medicaid-mco-config.json` - Medicaid MCO with all modules
  - `core/examples/regional-blues-config.json` - Regional Blues with EDI batch

## Build & Deployment

### 📚 Complete Deployment Documentation

**New to deployment? Start here:**

1. **[GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md)** - Complete guide to GitHub Actions configuration
   - Azure OIDC authentication setup
   - GitHub Secrets and Variables configuration
   - Environment setup and workflow permissions
   - Troubleshooting and testing procedures

2. **[DEPLOYMENT.md](DEPLOYMENT.md)** - Comprehensive deployment guide
   - Prerequisites and tool installation
   - Bicep compilation and ARM deployment
   - Environment protection rules and approval gates
   - Logic App workflow deployment
   - Post-deployment configuration
   - Verification and testing
   - Rollback procedures (6 detailed scenarios)
   - Audit logging and compliance reporting
   - Communication and notification strategy

3. **[DEPLOYMENT-GATES-GUIDE.md](DEPLOYMENT-GATES-GUIDE.md)** - ✨ Gated release strategy guide
   - Quick start for approvers and developers
   - Approval gate architecture
   - Pre-approval security validation
   - Team roles and responsibilities
   - Communication guidelines
   - Metrics and reporting procedures
   - Troubleshooting and configuration

4. **[GATED-RELEASE-IMPLEMENTATION-SUMMARY.md](GATED-RELEASE-IMPLEMENTATION-SUMMARY.md)** - ✨ Implementation status
   - Executive summary of gated release strategy
   - What's implemented and enhanced
   - Configuration checklist
   - Testing procedures
   - Success criteria and next steps

5. **[DEPLOYMENT-WORKFLOW-REFERENCE.md](DEPLOYMENT-WORKFLOW-REFERENCE.md)** - Quick reference
   - Deployment flow diagram
   - Documentation map
   - Required secrets and variables
   - Quick start commands
   - Common issues and solutions
   - Rollback quick reference

### Prerequisites

Before deploying, ensure you have:
- **Azure CLI** 2.77.0+ installed locally
- **Bicep CLI** 0.37.4+ installed
- **jq** 1.7+ for JSON validation
- **PowerShell** 7.4+ for scripts
- **GitHub repository secrets** configured for target environments

**See [DEPLOYMENT.md § Prerequisites](DEPLOYMENT.md#prerequisites) for detailed setup instructions.**

### Required GitHub Secrets

For each environment (DEV/UAT/PROD), configure these secrets:
- `AZURE_CLIENT_ID_{ENV}`: OIDC application client ID
- `AZURE_TENANT_ID_{ENV}`: Azure AD tenant ID
- `AZURE_SUBSCRIPTION_ID_{ENV}`: Target Azure subscription ID

**📚 Setup Guides:**
- [GITHUB-ACTIONS-SETUP.md](GITHUB-ACTIONS-SETUP.md) - Complete GitHub Actions and OIDC setup
- [DEPLOYMENT-SECRETS-SETUP.md](DEPLOYMENT-SECRETS-SETUP.md) - Detailed secrets configuration and validation

### 🔒 Gated Release Strategy

**All UAT and PROD deployments require manual approval** to ensure safety and compliance.

**What happens during deployment:**
1. 🔍 **Pre-approval security checks** (automatic)
   - TruffleHog secret detection
   - PII/PHI scanning
   - Artifact validation
2. ⏸️ **Approval gate** (manual review required)
   - Configured reviewers notified
   - Security scan results visible
   - Approval checklist provided
3. ✅ **Deployment proceeds** (after approval)
   - Infrastructure deployment
   - Logic App workflow deployment
   - Health checks and validation

**📚 Learn more:** See [DEPLOYMENT-GATES-GUIDE.md](DEPLOYMENT-GATES-GUIDE.md) for complete approval workflow details.

### Deployment Options

#### 1. Automated UAT Deployment (Recommended)
**Trigger**: Push to `release/*` branches  
**Workflow**: `.github/workflows/deploy-uat.yml`  
**Requires**: UAT approver review

The UAT deployment runs automatically when you push to any `release/*` branch:
```bash
git checkout -b release/v1.0.0
git push origin release/v1.0.0
# → Triggers pre-approval checks → Waits for UAT approval → Deploys
```

**UAT Environment Details:**
- Resource Group: `payer-attachments-uat-rg`
- Base Name: `hipaa-attachments-uat`
- Location: `eastus`
- Logic App: `hipaa-attachments-uat-la`

**See [DEPLOYMENT.md § UAT Environment](DEPLOYMENT.md#uat-environment) for detailed procedures.**

#### 2. Manual Production Deployment
**Trigger**: Manual workflow dispatch
**Workflow**: `.github/workflows/deploy.yml`

Production deployment requires approval:
1. Go to **Actions** → **Deploy (PROD)**
2. Click **Run workflow**
3. Wait for reviewer approval
4. Monitor deployment progress

**See [DEPLOYMENT.md § PROD Environment](DEPLOYMENT.md#prod-environment) for production checklist.**

#### 3. DEV Environment Deployment
**Trigger**: Manual or push to `main/*` branches
**Workflow**: `.github/workflows/deploy-dev.yml`

**See [DEPLOYMENT.md § DEV Environment](DEPLOYMENT.md#dev-environment) for DEV deployment procedures.**

### Deployment Timeline

| Environment | Validation | Infrastructure | Workflows | Total |
|-------------|-----------|----------------|-----------|-------|
| **DEV** | 30 sec | 5-8 min | 2 min | **7-10 min** |
| **UAT** | 30 sec | 8-12 min | 2 min | **10-14 min** |
| **PROD** | 30 sec | 10-15 min | 3 min | **13-18 min** |

**See [DEPLOYMENT-WORKFLOW-REFERENCE.md § Deployment Timeline](DEPLOYMENT-WORKFLOW-REFERENCE.md#deployment-timeline) for detailed breakdown.**

### Validation Checklist

Before deploying, validate your changes locally:

```bash
# 1. Validate JSON workflow syntax
find logicapps/workflows -name "workflow.json" -print0 | \
while IFS= read -r -d '' f; do
  echo "Checking $f"
  if ! jq . "$f" >/dev/null 2>&1; then
    echo "::error::Invalid JSON in $f"
    exit 1
  fi
  if ! jq -e 'has("definition") and has("kind") and has("parameters")' "$f" >/dev/null; then
    echo "::error::Missing required keys in $f (definition/kind/parameters)"
    exit 1
  fi
  echo "✓ Valid workflow JSON: $f"
done

# 2. Validate Bicep template compilation
az bicep build --file "infra/main.bicep" --outfile /tmp/arm.json
test -s /tmp/arm.json && echo "✓ Bicep compilation successful"

# 3. Create workflow package (for testing)
cd logicapps && zip -r ../workflows.zip workflows/
echo "✓ Workflow package created"
```

### Deployment Process

Each deployment follows these stages:

1. **Validation**
   - JSON workflow syntax and structure validation
   - Bicep template compilation check
   - Required parameters validation

2. **Infrastructure Deployment**
   - Resource group creation/update
   - ARM What-If analysis (preview changes)
   - Bicep template deployment via ARM
   - Resource verification

3. **Logic App Deployment**
   - Workflow packaging (ZIP creation)
   - ZIP deployment to Logic App Standard
   - Logic App restart

4. **Health Check**
   - Logic App status verification
   - Infrastructure resource status check
   - Deployment summary

### Troubleshooting

**Common Issues:**
- **JSON Validation Failures**: Use `jq . file.json` to identify syntax errors
- **Bicep Warnings**: "use-parent-property" warnings are cosmetic and safe to ignore
- **ZIP Package Issues**: Ensure ZIP contains `workflows/` as top-level directory
- **OIDC Authentication**: Verify GitHub environment secrets are correctly configured

## Configuration

After deployment, configure:
1. **API Connections**: SFTP, Blob Storage, Service Bus, Integration Account
2. **Integration Account**: X12 schemas and agreements for 275/277
3. **Logic App Parameters**: SFTP folders, QNXT endpoints, X12 identifiers
4. **Role Assignments**: Logic App managed identity access to Storage & Service Bus

### Post-Deployment Configuration Steps

1. **Configure API Connections**
   ```bash
   # Navigate to Logic App in Azure Portal
   # Go to Development Tools > API connections
   # Configure: sftp-ssh, azureblob, servicebus, integrationaccount
   ```

2. **Set up Integration Account**
   - Import X12 schemas for 275, 277, and 278 message types
   - Configure trading partner agreements
   - Update workflow parameters: `x12_275_messagetype`, `x12_277_messagetype`, `x12_278_messagetype`

3. **Configure Logic App Parameters**
   ```json
   {
     "sftp_inbound_folder": "/inbound/attachments",
     "blob_raw_folder": "hipaa-attachments/raw/275",
     "blob_raw_folder_278": "hipaa-attachments/raw/278",
     "sb_namespace": "hipaa-attachments-uat-sb",
     "sb_topic": "attachments-in",
     "sb_topic_edi278": "edi-278",
     "qnxt_base_url": "https://qnxt-api-uat.example.com",
     "x12_sender_id": "AVAILITY",
     "x12_receiver_id": "Health Plan"
   }
   ```

4. **Assign Managed Identity Permissions**
   ```bash
   # Storage Blob Data Contributor
   az role assignment create --assignee <logic-app-principal-id> \
     --role "Storage Blob Data Contributor" \
     --scope "/subscriptions/<sub-id>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<storage-name>"

   # Service Bus Data Sender
   az role assignment create --assignee <logic-app-principal-id> \
     --role "Azure Service Bus Data Sender" \
     --scope "/subscriptions/<sub-id>/resourceGroups/<rg>/providers/Microsoft.ServiceBus/namespaces/<sb-name>"
   ```

### Environment-Specific Configuration

**UAT Environment:**
- SFTP Endpoint: Configure for UAT Availity environment
- QNXT API: Point to UAT QNXT instance
- Service Bus Topics: `attachments-in`, `rfai-requests`, `edi-278`
- Storage Container: `hipaa-attachments`

**Production Considerations:**
- Enable diagnostic logging and alerts
- Configure backup and disaster recovery
- Set up monitoring dashboards in Application Insights
- Review and apply security best practices

## Data Lake Structure

Files are organized in Azure Data Lake Storage Gen2 as:
```
hipaa-attachments/
├── raw/
│   ├── 275/
│   │   └── yyyy/
│   │       └── MM/
│   │           └── dd/
│   │               └── filename_timestamp.edi
│   └── 278/
│       └── yyyy/
│           └── MM/
│               └── dd/
│                   └── filename_278_timestamp.edi
```

This partitioning structure supports efficient querying and data management in the data lake.

## Authorization Processing Workflow

The `process_authorizations` workflow processes X12 278 Health Care Services Review transactions (authorization requests and responses) and generates X12 277 authorization response messages.

### Key Features

- **Service Bus Trigger**: Consumes 278 authorization messages from `edi-278` topic via `auth-processor` subscription
- **X12 Processing**: Decodes 278 messages and encodes 277 responses via Integration Account
- **QNXT Integration**: 
  - Validates member eligibility
  - Verifies claim and provider information
  - Processes authorization requests with retry logic (4 retries @ 15s intervals)
- **Authorization Decision Tracking**:
  - Determines approval/denial status
  - Tracks authorization numbers and effective dates
  - Manages authorization limits and conditions
  - Links authorizations to appeals (if applicable)
- **277 Response Generation**: Creates properly formatted X12 277 authorization response messages
- **Service Bus Publishing**:
  - `auth-statuses` topic: Authorization status events for downstream tracking
  - `edi-278` topic: 277 responses for outbound processing via rfai277 workflow
- **Data Lake Archiving**: Archives both 278 requests and 277 responses to `hipaa-attachments/raw/authorizations/yyyy/MM/dd/`
- **Error Handling**: Comprehensive try-catch with dead-letter queue routing
- **Application Insights**: Logs all key events (request received, eligibility check, decision, 277 generation, completion, errors)

### Workflow Flow

1. Consume 278 authorization message from Service Bus (`edi-278` topic, `auth-processor` subscription)
2. Parse message and retrieve blob content from Data Lake
3. Archive 278 message to authorizations folder
4. Decode X12 278 message via Integration Account
5. Extract authorization metadata (claim, member, provider, authorization reference, service dates, service type, appeal reference)
6. Call QNXT APIs:
   - Check member eligibility
   - Verify claim and provider information
   - Process authorization request
7. Determine authorization decision (approval/denial, authorization number, effective dates, limits, conditions)
8. Generate and encode X12 277 authorization response
9. Archive 277 response to Data Lake
10. Publish authorization status to `auth-statuses` topic
11. Publish 277 response to `edi-278` topic for outbound processing
12. Log completion metrics to Application Insights

### Parameters

All configuration is externalized for DEV/UAT/PROD environments:
- `sb_topic_edi278`: Source topic for 278 messages (default: `edi-278`)
- `sb_subscription_auth`: Subscription name (default: `auth-processor`)
- `sb_topic_auth_statuses`: Target topic for authorization status events (default: `auth-statuses`)
- `sb_topic_dead_letter`: Dead-letter queue topic (default: `dead-letter`)
- `blob_authorizations_folder`: Data Lake folder path (default: `hipaa-attachments/raw/authorizations`)
- `qnxt_base_url`: QNXT API base URL
- `QNXT_API_TOKEN`: **[SECURE]** QNXT API authentication token (SecureString) - Configure via Azure Key Vault reference or Managed Identity
- `x12_278_messagetype`: X12 278 message type (default: `X12_005010X217_278`)
- `x12_277_messagetype`: X12 277 message type (default: `X12_005010X212_277`)

**Security Note**: The `QNXT_API_TOKEN` parameter must be configured using an Azure Key Vault reference (e.g., `@Microsoft.KeyVault(SecretUri=https://your-keyvault.vault.azure.net/secrets/qnxt-api-token)`) or obtained via Managed Identity. Never commit tokens or secrets to source code.

## 278 Replay Endpoint

The `replay278` workflow provides a deterministic HTTP endpoint for replaying X12 278 transactions. This allows for debugging and reprocessing of specific messages.

### Endpoint Usage

**POST** `/api/replay278/triggers/HTTP_Replay_278_Request/invoke`

**Request Body:**
```json
{
  "blobUrl": "hipaa-attachments/raw/278/2024/01/15/review-request-20240115.edi",
  "fileName": "custom-replay-name" // Optional
}
```

**Success Response (200):**
```json
{
  "status": "success",
  "message": "278 replay message queued successfully",
  "data": {
    "blobUrl": "hipaa-attachments/raw/278/2024/01/15/review-request-20240115.edi",
    "fileName": "custom-replay-name",
    "queueTimestamp": "2024-01-15T10:30:00Z",
    "topicName": "edi-278"
  }
}
```

**Error Response (400):**
```json
{
  "status": "error",
  "message": "Invalid blobUrl provided. Must be non-empty and contain 'hipaa-attachments' path.",
  "data": {
    "providedBlobUrl": "invalid-path",
    "timestamp": "2024-01-15T10:30:00Z"
  }
}
```

### Features
- **Validation**: Ensures blobUrl contains 'hipaa-attachments' path
- **Queueing**: Enqueues message to `edi-278` Service Bus topic for processing
- **Logging**: Records replay events in Application Insights
- **Deterministic**: Consistent replay behavior for troubleshooting

## Appeals Processing Workflow

The `process_appeals` workflow provides comprehensive appeals handling based on attachment events from the 275 ingestion workflow.

### Workflow Overview

**Trigger**: Service Bus topic `attachments-in` via `appeals-processor` subscription

**Process Flow**:
1. Consume attachment event from Service Bus
2. Parse attachment metadata (claim number, member ID, provider NPI, service dates)
3. Extract appeal indicators from message properties
4. Identify appeals via indicator flag, reason code, or attachment type
5. Correlate appeal with existing claim via QNXT API
6. Register appeal with QNXT Appeals API
7. Conditionally publish RFAI event to `rfai-requests` topic if required
8. Log all events to Application Insights

### Appeal Detection

Appeals are detected when any of the following conditions are met:
- **Appeal Indicator**: UserProperty `appealIndicator` is present and non-empty
- **Reason Code**: UserProperty `appealReasonCode` is present and non-empty
- **Attachment Type**: Content data `attachmentType` contains "appeal"

### QNXT Integration

**Two API Calls with Retry Logic**:
1. **Correlate Appeal** (`/claims/correlate-appeal`): Links appeal to existing claim
   - 4 retries @ 15-second intervals
   - Returns correlation data for appeal registration

2. **Register Appeal** (`/appeals/register`): Registers appeal in QNXT system
   - 4 retries @ 15-second intervals
   - Returns appeal ID, tracking number, status, and RFAI requirements

### RFAI Publishing

If QNXT response indicates `requiresRFAI: true`, the workflow:
- Composes RFAI event with appeal metadata
- Publishes to `rfai-requests` Service Bus topic
- Includes appeal tracking number as RFAI reference
- Logs RFAI publication to Application Insights

### Error Handling

**Try-Catch Scope Pattern**:
- Main processing in `Try_Process_Appeal` scope
- Error handling in `Catch_Appeal_Processing_Error` scope
- Failed messages abandoned to dead-letter queue
- Error details logged to Application Insights
- Dead-letter reason: "AppealProcessingError"

### Application Insights Events

**Logged Events**:
- `AppealDetected`: When appeal indicator found
- `AppealRegistered`: Upon successful QNXT registration
- `AppealRFAIPublished`: When RFAI event published
- `AppealNoRFAI`: When appeal doesn't require RFAI
- `NoAppealDetected`: When no appeal indicators present
- `AppealProcessingError`: On any processing failure

### Configuration Parameters

```json
{
  "sb_namespace": "hipaa-logic-svc",
  "sb_topic_attachments": "attachments-in",
  "sb_topic_rfai": "rfai-requests",
  "sb_subscription_appeals": "appeals-processor",
  "qnxt_base_url": "https://qnxt-api.example.com",
  "QNXT_API_TOKEN": "@Microsoft.KeyVault(SecretUri=https://your-keyvault.vault.azure.net/secrets/qnxt-api-token)",
  "appinsights_endpoint": "https://dc.services.visualstudio.com",
  "appinsights_instrumentation_key": "<instrumentation-key>"
}
```

**Security Note**: Secrets like `QNXT_API_TOKEN` should be configured as Azure Key Vault references, not hardcoded values. See [SECURITY-HARDENING.md](SECURITY-HARDENING.md) for complete security configuration guidance.

### Integration Points

**Upstream**:
- `ingest275` workflow publishes attachment events to `attachments-in` topic
- Service Bus subscription `appeals-processor` filters for appeal-related messages

**Downstream**:
- `rfai277` workflow consumes from `rfai-requests` topic for RFAI processing
- QNXT Appeals system maintains appeal lifecycle and status

### Deployment Considerations

**Service Bus Configuration**:
- Ensure subscription `appeals-processor` exists on `attachments-in` topic
- Configure subscription filters if needed to target appeal-specific messages
- Set appropriate message lock duration and max delivery count

**QNXT API Configuration**:
- Verify appeal endpoints are available: `/claims/correlate-appeal`, `/appeals/register`
- Ensure API token has appropriate permissions
- Test retry behavior with non-production endpoints

**Monitoring**:
- Set up Application Insights alerts for `AppealProcessingError` events
- Monitor dead-letter queue for failed appeal processing
- Track appeal registration success rate
- Monitor QNXT API latency and retry rates

## Enhanced Claim Status (ECS) Integration

The ECS Summary Search workflow enables real-time claim status queries through a RESTful HTTP endpoint. This feature provides integration with the QNXT claims adjudication system for enhanced claim visibility.

### ECS Features

- **Four Search Methods**:
  - **Service Date**: Query claims by provider and service date range
  - **Member**: Query claims by member/subscriber ID and date range
  - **Check Number**: Query claims paid via specific check or EFT
  - **Claim History**: Retrieve detailed history for a specific claim

- **QNXT Integration**: Direct API integration with retry logic (4 retries @ 15s intervals)
- **Request Validation**: Schema-based validation using JSON Schema
- **Response Transformation**: Converts QNXT responses to Availity ECS format
- **Comprehensive Error Handling**: Detailed error messages with troubleshooting guidance
- **Audit Logging**: All searches logged to Application Insights

### ECS Endpoint

**HTTP POST**: `https://{logic-app-name}.azurewebsites.net/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke`

**Authentication**: Bearer token (JWT) via Authorization header

### Example Request

```json
{
  "searchMethod": "ServiceDate",
  "requestId": "REQ-2024-001",
  "submitterId": "PROV-12345",
  "serviceDateSearch": {
    "serviceFromDate": "20240101",
    "serviceToDate": "20240131",
    "providerId": "1234567890",
    "providerIdQualifier": "NPI"
  }
}
```

### Example Response

```json
{
  "requestId": "REQ-2024-001",
  "status": "success",
  "timestamp": "2024-01-15T10:30:45.123Z",
  "searchMethod": "ServiceDate",
  "totalResults": 2,
  "claims": [
    {
      "claimNumber": "CLM123456789",
      "claimStatus": "Paid",
      "claimStatusCode": "F2",
      "billedAmount": 250.00,
      "paidAmount": 200.00,
      "providerId": "1234567890",
      "memberId": "M123456"
    }
  ]
}
```

### ECS Documentation

Complete documentation for the ECS integration:

- **[docs/ECS-INTEGRATION.md](docs/ECS-INTEGRATION.md)** - Comprehensive integration guide with request/response examples, error handling, and API mappings
- **[docs/BACKEND-INTERFACE.md](docs/BACKEND-INTERFACE.md)** - Backend-agnostic interface definition for extensibility
- **[docs/api/ECS-OPENAPI.yaml](docs/api/ECS-OPENAPI.yaml)** - OpenAPI 3.0 specification for API documentation

### ECS Schemas

Request and response validation schemas:

- **[schemas/ECS-SummarySearch-Request.json](schemas/ECS-SummarySearch-Request.json)** - JSON Schema for request validation
- **[schemas/ECS-SummarySearch-Response.json](schemas/ECS-SummarySearch-Response.json)** - JSON Schema for response validation

### Configuration

ECS-specific configuration in Logic App parameters:

**⚠️ SECURITY NOTE**: Never hardcode tokens in configuration files. Always use Azure Key Vault.

```bash
# Store QNXT API token in Key Vault
az keyvault secret set \
  --vault-name "${KV_NAME}" \
  --name "qnxt-api-token" \
  --value "${QNXT_TOKEN}"

# Configure Logic App app settings
az webapp config appsettings set \
  --resource-group "${RG_NAME}" \
  --name "${LOGIC_APP_NAME}" \
  --settings \
    "ECS_QNXT_BASE_URL=https://qnxt-api.example.com" \
    "ECS_QNXT_API_TOKEN=@Microsoft.KeyVault(SecretUri=https://${KV_NAME}.vault.azure.net/secrets/qnxt-api-token/)" \
    "ECS_WORKFLOW_ENABLED=true"
```

### Testing ECS

Test the ECS endpoint using curl:

```bash
# Replace {token} with your actual JWT token from Azure AD
curl -X POST \
  -H "Authorization: Bearer YOUR_JWT_TOKEN_HERE" \
  -H "Content-Type: application/json" \
  -d '{
    "searchMethod": "ServiceDate",
    "requestId": "TEST-001",
    "serviceDateSearch": {
      "serviceFromDate": "20240101",
      "serviceToDate": "20240131",
      "providerId": "1234567890",
      "providerIdQualifier": "NPI"
    }
  }' \
  https://{logic-app}.azurewebsites.net/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke
```

## 📚 Documentation

Comprehensive documentation is available to guide development, deployment, and maintenance:

### Core Documentation
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Development workflow, prerequisites, and setup guide
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - System architecture, components, and data flows
- **[DEPLOYMENT.md](DEPLOYMENT.md)** - Step-by-step deployment procedures for all environments
- **[BRANCHING-STRATEGY.md](BRANCHING-STRATEGY.md)** - Branch conventions, merge requirements, and workflows
- **[SECURITY.md](SECURITY.md)** - HIPAA compliance, security practices, and encryption
- **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** - Common issues, solutions, and debugging guides

### ECS Documentation
- **[docs/ECS-INTEGRATION.md](docs/ECS-INTEGRATION.md)** - Enhanced Claim Status integration guide
- **[docs/BACKEND-INTERFACE.md](docs/BACKEND-INTERFACE.md)** - Backend-agnostic interface specification
- **[docs/api/ECS-OPENAPI.yaml](docs/api/ECS-OPENAPI.yaml)** - OpenAPI 3.0 API specification

### Additional Resources
- **[HIPAA-X12-Agreements-Guide.md](HIPAA-X12-Agreements-Guide.md)** - X12 EDI agreement configuration
- **[test-plan-trading-partners.md](test-plan-trading-partners.md)** - Testing procedures and trading partner details

## 🤖 GitHub Copilot Instructions

This repository includes comprehensive instructions for GitHub Copilot to assist with development:

### Repository-Wide Instructions
- **[.github/copilot-instructions.md](.github/copilot-instructions.md)**: Complete guidance for working with this repository including build commands, validation steps, deployment processes, and common scenarios.

### Path-Specific Instructions
The repository includes context-aware instructions that automatically activate when working on specific parts of the codebase:

- **[GitHub Actions Workflows](.github/instructions/workflows.instructions.md)**: Guidance for `.github/workflows/*.yml` files
- **[Infrastructure (Bicep)](.github/instructions/infrastructure.instructions.md)**: Guidance for `infra/*.bicep` templates
- **[Logic Apps Workflows](.github/instructions/logicapps.instructions.md)**: Guidance for `logicapps/workflows/*/workflow.json` files
- **[PowerShell Scripts](.github/instructions/scripts.instructions.md)**: Guidance for `*.ps1` scripts

These instructions help Copilot understand the project structure, conventions, and best practices, providing more accurate and context-aware assistance throughout development.

## 📄 License

This project is licensed under the Apache License 2.0 - see the [LICENSE](LICENSE) file for full details.

### Why Apache 2.0?

Apache 2.0 was selected for this healthcare-focused project because it provides:

- **Healthcare Compatibility**: Widely accepted in healthcare and enterprise environments
- **Patent Protection**: Express patent grant protects both contributors and users from patent litigation
- **Commercial-Friendly**: Permissive terms suitable for HIPAA-regulated commercial use
- **Clear Contribution Guidelines**: Well-defined terms for contributions and derivative works
- **Enterprise Adoption**: Trusted by major healthcare technology organizations

### HIPAA Compliance Notice

This software includes features for handling Protected Health Information (PHI) in HIPAA-regulated environments. **Important considerations:**

- **Compliance Responsibility**: Ultimate responsibility for HIPAA compliance rests with the implementing organization
- **Security Requirements**: Organizations must implement appropriate administrative, physical, and technical safeguards
- **No Warranty**: Software is provided "AS IS" without warranties regarding HIPAA compliance
- **Security Guidelines**: Refer to [SECURITY.md](SECURITY.md) for detailed compliance requirements

Users must perform their own compliance assessments and implement necessary safeguards according to their specific requirements and risk analysis.

### Contributing

By contributing to this project, you agree that your contributions will be licensed under the Apache License 2.0. See [CONTRIBUTING.md](CONTRIBUTING.md) for details on our development process and how to submit contributions.