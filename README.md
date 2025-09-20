# HIPAA Attachments - Logic Apps & Infrastructure

This repository contains the implementation for processing HIPAA 275 Attachments from Availity, archiving them in Azure Data Lake Storage Gen2, and linking them to claims in QNXT.

## Architecture Overview

The solution implements the following process:
1. **SFTP Polling**: Monitors Availity SFTP for new 275 attachment files
2. **Data Lake Archive**: Stores raw files in Azure Data Lake Storage Gen2 with date-based partitioning
3. **X12 Decoding**: Processes 275 messages via Integration Account
4. **Metadata Extraction**: Extracts claim, member, and provider information
5. **QNXT Integration**: Links attachments to claims via QNXT API
6. **Service Bus**: Publishes events for downstream processing

## Components

### Infrastructure (`infra/main.bicep`)
- **Azure Data Lake Storage Gen2**: Storage account with hierarchical namespace enabled
- **Service Bus**: Topics for attachment processing and RFAI requests
- **Logic App Standard**: Serverless workflow execution
- **Application Insights**: Telemetry and monitoring

### Workflows
- `logicapps/workflows/ingest275/workflow.json`: Main 275 ingestion workflow
- `logicapps/workflows/rfai277/workflow.json`: Outbound 277 RFAI workflow

### Key Features
- **Data Lake Storage**: Files stored with `hipaa-attachments/raw/275/yyyy/MM/dd/` partitioning
- **Retry Logic**: 4 retries with 15-second intervals for QNXT API calls
- **Error Handling**: Service Bus dead-letter support
- **Monitoring**: Application Insights integration

## Deployment

Run the GitHub Actions workflow **Deploy HIPAA Attachments (Bicep + Logic Apps)** from the Actions tab.

Required parameters:
- Azure Subscription ID
- Resource Group name
- Azure region
- Base name for resources

## Configuration

After deployment, configure:
1. **API Connections**: SFTP, Blob Storage, Service Bus, Integration Account
2. **Integration Account**: X12 schemas and agreements for 275/277
3. **Logic App Parameters**: SFTP folders, QNXT endpoints, X12 identifiers
4. **Role Assignments**: Logic App managed identity access to Storage & Service Bus

## Data Lake Structure

Files are organized in Azure Data Lake Storage Gen2 as:
```
hipaa-attachments/
├── raw/
│   └── 275/
│       └── yyyy/
│           └── MM/
│               └── dd/
│                   └── filename_timestamp.edi
```

This partitioning structure supports efficient querying and data management in the data lake.