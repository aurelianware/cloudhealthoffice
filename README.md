# HIPAA Attachments Processing System 🏥

A complete Azure Logic Apps solution for processing HIPAA-compliant medical attachments with secure file handling, message queuing, and monitoring capabilities.

## 🏗️ Architecture Overview

This solution implements the following process:
1. **SFTP Polling**: Monitors Availity SFTP for new 275 attachment files (ingest275 workflow)
2. **Data Lake Archive**: Stores raw files in Azure Data Lake Storage Gen2 with date-based partitioning
3. **X12 Decoding**: Processes 275/278 messages via Integration Account
4. **Metadata Extraction**: Extracts claim, member, and provider information
5. **QNXT Integration**: Links attachments to claims via QNXT API
6. **Service Bus**: Publishes events for downstream processing
7. **RFAI Processing**: Handles Request for Additional Information via rfai277 workflow
8. **278 Transaction Processing**: Processes Health Care Services Review Information via ingest278 workflow
9. **Deterministic Replay**: HTTP endpoint for replaying 278 transactions via replay278 workflow

## 📦 Current Production Deployment

### ✅ Successfully Deployed Infrastructure
- **Resource Group**: `rg-hipaa-logic-apps` (West US)
- **Logic App Standard**: `hipaa-logic-la` ✅ RUNNING
- **Storage Account**: `hipaa7v2rrsoo6tac2` (Data Lake Gen2 enabled) ✅
- **Service Bus Namespace**: `hipaa-logic-svc` (Standard tier) ✅
  - Topics: `attachments-in`, `rfai-requests`, `edi-278` ✅
- **Application Insights**: `hipaa-logic-ai` ✅

### 🌐 Resource URLs
- **Logic App Portal**: https://hipaa-logic-la.azurewebsites.net
- **Azure Management**: [Logic App Designer](https://portal.azure.com/#@/resource/subscriptions/caf68aff-3bee-40e3-bf26-c4166efa952b/resourceGroups/rg-hipaa-logic-apps/providers/Microsoft.Web/sites/hipaa-logic-la/logicApp)

## 🔧 Components
- **Application Insights**: Telemetry and monitoring

### Workflows
- `logicapps/workflows/ingest275/workflow.json`: Main 275 ingestion workflow
- `logicapps/workflows/rfai277/workflow.json`: Outbound 277 RFAI workflow
- `logicapps/workflows/ingest278/workflow.json`: X12 278 transaction processing workflow
- `logicapps/workflows/replay278/workflow.json`: HTTP endpoint for deterministic 278 replay

### Key Features
- **Data Lake Storage**: Files stored with `hipaa-attachments/raw/{275|278}/yyyy/MM/dd/` partitioning
- **Retry Logic**: 4 retries with 15-second intervals for QNXT API calls
- **Error Handling**: Service Bus dead-letter support
- **Monitoring**: Application Insights integration
- **278 Replay Endpoint**: HTTP trigger for deterministic transaction replay

## Build & Deployment

### Prerequisites

Before deploying, ensure you have:
- **Azure CLI** 2.77.0+ installed locally
- **Bicep CLI** 0.37.4+ installed
- **jq** 1.7+ for JSON validation
- **PowerShell** 7.4+ for scripts
- **GitHub repository secrets** configured for target environments

### Required GitHub Secrets

For each environment (DEV/UAT/PROD), configure these secrets:
- `AZURE_CLIENT_ID_{ENV}`: OIDC application client ID
- `AZURE_TENANT_ID_{ENV}`: Azure AD tenant ID
- `AZURE_SUBSCRIPTION_ID_{ENV}`: Target Azure subscription ID

### Deployment Options

#### 1. Automated UAT Deployment (Recommended)
**Trigger**: Push to `release/*` branches  
**Workflow**: `.github/workflows/deploy-uat.yml`

The UAT deployment runs automatically when you push to any `release/*` branch:
```bash
git checkout -b release/v1.0.0
git push origin release/v1.0.0
```

**UAT Environment Details:**
- Resource Group: `pchp-attachments-uat-rg`
- Base Name: `hipaa-attachments-uat`
- Location: `eastus`
- Logic App: `hipaa-attachments-uat-la`

#### 2. Manual Multi-Environment Deployment
**Workflow**: `.github/workflows/deploy_logicapps_workflows_matrix_with_lint.yml`

Run from GitHub Actions tab with parameters:
- Azure Subscription ID
- Resource Group name
- Azure region
- Base name for resources

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
     "x12_receiver_id": "PCHP"
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