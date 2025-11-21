# Cloud Health Office - Platform Architecture

This document provides a comprehensive overview of the Cloud Health Office multi-tenant SaaS platform architecture, component interactions, and data flows supporting unlimited health plans.

## Table of Contents
- [Overview](#overview)
- [Platform Architecture](#platform-architecture)
- [High-Level Architecture](#high-level-architecture)
- [Component Details](#component-details)
- [Data Flows](#data-flows)
- [Integration Points](#integration-points)
- [Design Decisions](#design-decisions)
- [HIPAA Compliance](#hipaa-compliance)
- [Scalability and Performance](#scalability-and-performance)

## Overview

Cloud Health Office is a **cloud-native multi-tenant SaaS platform** built on Azure Logic Apps that processes healthcare EDI transactions for unlimited health plans. The platform supports claims, eligibility, attachments, authorizations, appeals, and claim status through configuration-driven deployment.

### Key Objectives
- **Multi-Tenant SaaS**: Single codebase serves unlimited payers with per-tenant isolation
- **Zero-Code Onboarding**: Add new payers through configuration (<1 hour to production)
- **Backend Agnostic**: Works with any claims system (QNXT, FacetsRx, TriZetto, Epic, Cerner, custom)
- **Enterprise Security**: HIPAA-compliant with Key Vault, private endpoints, PHI masking, automated rotation
- **Reliability**: Comprehensive error handling, retry logic, dead-letter queues, and health monitoring
- **Horizontal Scalability**: Auto-scales to handle any transaction volume across all tenants
- **Complete Auditability**: Full transaction tracking, compliance logging, and per-payer reporting
- **Standards-Based**: X12 EDI integration with Availity and other clearinghouses
- **SaaS-Ready**: Marketplace-ready with billing integration and customer portal (roadmap)

### Platform Context

**Platform Model:**
- **Multi-Tenant**: Single Logic Apps instance serves multiple payers with config-based isolation
- **Configuration-Driven**: All payer-specific logic defined in configuration files
- **Automated Deployment**: Config-to-Workflow Generator creates complete deployments
- **Self-Service**: Interactive onboarding wizard for guided configuration

**Trading Partners (Generic):**
- **Availity** (ID: 030240928) - EDI clearinghouse sending attachment requests
- **Health Plans** (ID: {config.payerId}) - Individual payer claims processing systems

**Supported Use Cases:**
1. Inbound attachment requests from providers via Availity (275)
2. Outbound status responses to Availity (277)
3. Health care services review processing (278)
4. Deterministic transaction replay for debugging
5. Appeals integration with attachment workflows

## Platform Architecture

### Configuration-Driven Design

The platform uses a **unified configuration schema** to support multiple payers without custom code:

```
┌─────────────────────────────────────────────────────────────┐
│           Unified Configuration Schema                       │
│  (Payer-specific settings, backend mappings, business rules) │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────┐
│           Config-to-Workflow Generator                       │
│  (Generates Logic App workflows, Bicep infrastructure)       │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────┐
│           Payer-Specific Deployment Package                  │
│  (Workflows, infrastructure, API connections, docs)          │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────┐
│           Azure Logic Apps Standard                          │
│  (Multi-tenant execution with config-based routing)          │
└─────────────────────────────────────────────────────────────┘
```

### Key Platform Components

1. **Unified Configuration Schema** (`docs/UNIFIED-CONFIG-SCHEMA.md`)
   - Payer organization information
   - Module enablement (Appeals, ECS, Attachments, etc.)
   - Backend system connections and field mappings
   - X12 EDI settings per payer
   - Business rules and validation logic

2. **Config-to-Workflow Generator** (`docs/CONFIG-TO-WORKFLOW-GENERATOR.md`)
   - Reads payer configuration files
   - Generates Logic App workflow.json files
   - Creates Bicep infrastructure templates
   - Produces deployment scripts and documentation

3. **Onboarding Wizard** (`scripts/cli/payer-onboarding-wizard.js`)
   - Interactive CLI for guided configuration
   - 10-step process from organization info to deployment
   - Real-time validation and schema compliance

4. **Multi-Tenant Runtime**
   - Single Logic Apps instance
   - Config-based tenant isolation
   - Payer-specific parameters injected at runtime
   - Shared infrastructure, isolated data and configuration

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    HIPAA Attachments System                      │
│                     (Azure West US / East US)                    │
└─────────────────────────────────────────────────────────────────┘

┌──────────────┐         ┌──────────────────────────────────┐
│   Availity   │ SFTP    │     Logic Apps Standard          │
│   (275/278)  │────────▶│  ┌────────────────────────────┐  │
│              │         │  │  ingest275 Workflow        │  │
└──────────────┘         │  │  (SFTP Trigger)            │  │
                         │  └────────────┬───────────────┘  │
┌──────────────┐         │               │                  │
│    QNXT      │◀────────│  ┌────────────▼───────────────┐  │
│     API      │  HTTP   │  │  Decode X12 275            │  │
│              │         │  │  Extract Metadata          │  │
└──────────────┘         │  │  Archive to Data Lake      │  │
                         │  └────────────┬───────────────┘  │
┌──────────────┐         │               │                  │
│ Data Lake    │◀────────│  ┌────────────▼───────────────┐  │
│  Storage     │  Blob   │  │  Publish to Service Bus    │  │
│  Gen2        │         │  │  (attachments-in topic)    │  │
│              │         │  └────────────────────────────┘  │
└──────────────┘         │                                  │
                         │  ┌────────────────────────────┐  │
┌──────────────┐         │  │  rfai277 Workflow          │  │
│   Availity   │◀────────│  │  (Service Bus Trigger)     │  │
│   (277)      │  SFTP   │  │  ▲                         │  │
│              │         │  └──┼─────────────────────────┘  │
└──────────────┘         │     │                            │
                         │  ┌──┴─────────────────────────┐  │
┌──────────────┐         │  │  Service Bus Namespace     │  │
│ Service Bus  │         │  │  • attachments-in          │  │
│  Topics      │         │  │  • rfai-requests           │  │
│              │         │  │  • edi-278                 │  │
└──────────────┘         │  └────────────────────────────┘  │
                         │                                  │
┌──────────────┐         │  ┌────────────────────────────┐  │
│ Integration  │         │  │  ingest278 Workflow        │  │
│  Account     │◀───────▶│  │  (SFTP Trigger)            │  │
│ (X12 EDI)    │         │  └────────────┬───────────────┘  │
└──────────────┘         │               │                  │
                         │  ┌────────────▼───────────────┐  │
┌──────────────┐         │  │  replay278 Workflow        │  │
│   HTTP       │────────▶│  │  (HTTP Trigger)            │  │
│  Client      │  POST   │  │  Queue to edi-278 topic    │  │
└──────────────┘         │  └────────────────────────────┘  │
                         │                                  │
┌──────────────┐         │  ┌────────────────────────────┐  │
│ Application  │         │  │  Monitoring & Logging      │  │
│  Insights    │◀────────│  │  • Workflow runs           │  │
│              │         │  │  • Performance metrics     │  │
└──────────────┘         │  │  • Error tracking          │  │
                         │  └────────────────────────────┘  │
                         └──────────────────────────────────┘
```

## Component Details

### Logic App Standard (WS1 SKU)

**Resource Name:** `{baseName}-la` (e.g., `hipaa-attachments-la`)

**Purpose:** Hosts and executes the stateful workflows that orchestrate the attachment processing pipeline.

**Key Features:**
- **Stateful Workflows**: All workflows maintain state for reliable processing
- **Managed Identity**: System-assigned identity for secure Azure resource access
- **Built-in Connectors**: SFTP, Blob Storage, Service Bus, Integration Account
- **Retry Policies**: Configurable retry logic for transient failures
- **Application Insights Integration**: Automatic telemetry and logging

**Configuration:**
- SKU: WS1 (Logic Apps Standard - Workflow Standard 1)
- Runtime: ~4.0
- Platform: Windows
- Always On: Enabled
- HTTPS Only: Enabled

### Workflows

#### 1. ingest275 - Attachment Ingestion (275)

**Trigger:** SFTP file polling  
**Purpose:** Process inbound attachment requests from Availity  
**Flow:**

```
SFTP New File Event
  │
  ├─▶ Get File Content (SFTP)
  │
  ├─▶ Store Raw File in Data Lake
  │   Path: hipaa-attachments/raw/275/{yyyy}/{MM}/{dd}/
  │
  ├─▶ Decode X12 275 Message
  │   (Integration Account)
  │
  ├─▶ Extract Metadata
  │   • Claim Number
  │   • Member ID
  │   • Provider NPI
  │   • Attachment Reference
  │
  ├─▶ Call QNXT API
  │   POST /api/claims/attachments/link
  │   Retry: 4 attempts, 15s interval
  │
  ├─▶ Publish to Service Bus
  │   Topic: attachments-in
  │   Message: {claimId, memberId, blobPath, status}
  │
  └─▶ Delete File from SFTP
      (After successful processing)
```

**Key Actions:**
- `SFTP_New_or_Updated_File` - Trigger on new .edi files
- `Get_file_content` - Read file content
- `Store_Raw_in_Blob` - Archive to Data Lake
- `Decode_X12_275` - X12 decoding via Integration Account
- `Extract_Metadata` - Parse decoded JSON
- `Call_QNXT_Claim_Linkage_API` - Link attachment to claim
- `Publish_to_Service_Bus` - Queue for downstream processing

**Parameters:**
```json
{
  "sftp_inbound_folder": "/inbound/attachments",
  "blob_raw_folder": "hipaa-attachments/raw/275",
  "sb_topic": "attachments-in",
  "qnxt_base_url": "https://qnxt-api.example.com",
  "x12_messagetype_275": "X12_005010X210_275"
}
```

#### 2. rfai277 - RFAI Response (277)

**Trigger:** Service Bus topic subscription (rfai-requests)  
**Purpose:** Generate and send attachment status responses to Availity  
**Flow:**

```
Service Bus Message Received
  (Topic: rfai-requests)
  │
  ├─▶ Parse RFAI Request
  │   Extract: claimId, status, attachmentRef
  │
  ├─▶ Encode X12 277 Message
  │   (Integration Account)
  │   Sender: Health Plan ({config.payerId})
  │   Receiver: Availity (030240928)
  │
  ├─▶ Send to Availity via SFTP
  │   Path: /outbound/277/
  │
  ├─▶ Archive Sent Message
  │   Path: hipaa-attachments/sent/277/{yyyy}/{MM}/{dd}/
  │
  └─▶ Update QNXT Status
      POST /api/claims/attachments/status
```

**Key Actions:**
- Service Bus trigger on `rfai-requests` topic
- `Encode_X12_277` - X12 encoding via Integration Account
- `Send_to_SFTP` - Upload to Availity
- `Archive_Sent` - Store in Data Lake
- `Update_QNXT` - Confirm delivery

**Parameters:**
```json
{
  "sb_topic_rfai": "rfai-requests",
  "sftp_outbound_folder": "/outbound/277",
  "blob_sent_folder": "hipaa-attachments/sent/277",
  "x12_messagetype_277": "X12_005010X212_277"
}
```

#### 3. ingest278 - Health Care Services Review (278)

**Trigger:** SFTP file polling  
**Purpose:** Process health care services review information  
**Flow:**

```
SFTP New File Event
  │
  ├─▶ Get File Content (SFTP)
  │
  ├─▶ Store Raw File in Data Lake
  │   Path: hipaa-attachments/raw/278/{yyyy}/{MM}/{dd}/
  │
  ├─▶ Decode X12 278 Message
  │   (Integration Account)
  │
  ├─▶ Extract Review Information
  │   • Service Request
  │   • Authorization Details
  │   • Provider Information
  │
  ├─▶ Publish to Service Bus
  │   Topic: edi-278
  │   Message: {blobUrl, reviewType, status}
  │
  └─▶ Delete File from SFTP
      (After successful processing)
```

**Parameters:**
```json
{
  "blob_raw_folder_278": "hipaa-attachments/raw/278",
  "sb_topic_edi278": "edi-278",
  "x12_messagetype_278": "X12_005010X217_278"
}
```

#### 4. replay278 - Deterministic Replay Endpoint

**Trigger:** HTTP POST request  
**Purpose:** Enable deterministic replay of 278 transactions for debugging  
**Flow:**

```
HTTP POST /api/replay278/triggers/HTTP_Replay_278_Request/invoke
  │
  ├─▶ Validate Input
  │   • blobUrl is not empty
  │   • blobUrl contains "hipaa-attachments"
  │
  ├─▶ Queue Message to Service Bus
  │   Topic: edi-278
  │   Message: {blobUrl, fileName, timestamp}
  │
  └─▶ Return Response
      Success: 200 {status, message, data}
      Error: 400 {status, error, data}
```

**Request Example:**
```json
POST /api/replay278/triggers/HTTP_Replay_278_Request/invoke
Content-Type: application/json

{
  "blobUrl": "hipaa-attachments/raw/278/2024/01/15/review-request.edi",
  "fileName": "custom-replay-name"
}
```

**Response Example:**
```json
{
  "status": "success",
  "message": "278 replay message queued successfully",
  "data": {
    "blobUrl": "hipaa-attachments/raw/278/2024/01/15/review-request.edi",
    "fileName": "custom-replay-name",
    "queueTimestamp": "2024-01-15T10:30:00Z",
    "topicName": "edi-278"
  }
}
```

### Azure Data Lake Storage Gen2

**Resource Name:** `{baseName}storage` (with hierarchical namespace)

**Purpose:** Secure, scalable storage for all EDI files with HIPAA-compliant encryption

**Container Structure:**
```
hipaa-attachments/
├── raw/                      # Original inbound files
│   ├── 275/
│   │   └── {yyyy}/          # Year partition
│   │       └── {MM}/        # Month partition
│   │           └── {dd}/    # Day partition
│   │               └── file_{timestamp}.edi
│   └── 278/
│       └── {yyyy}/
│           └── {MM}/
│               └── {dd}/
│                   └── file_{timestamp}.edi
├── sent/                     # Outbound transmitted files
│   └── 277/
│       └── {yyyy}/
│           └── {MM}/
│               └── {dd}/
│                   └── file_{timestamp}.edi
└── processed/               # Post-processing artifacts
    └── {type}/
        └── {yyyy}/{MM}/{dd}/
```

**Key Features:**
- **Hierarchical Namespace**: Enables efficient directory operations
- **Encryption at Rest**: Azure Storage Service Encryption (SSE) with Microsoft-managed keys
- **Access Control**: Managed Identity + RBAC (Storage Blob Data Contributor)
- **Soft Delete**: 7-day retention for accidental deletion recovery
- **Versioning**: Enabled for data recovery
- **Immutability**: Consider for compliance requirements

**Date Partitioning Benefits:**
- Efficient querying by date range
- Simplified data lifecycle management
- Optimized storage costs (archive older partitions)
- Better performance for time-based queries

### Service Bus Namespace

**Resource Name:** `{baseName}-svc`  
**SKU:** Standard (supports topics and subscriptions)

**Topics:**

#### 1. attachments-in
**Purpose:** Queue successfully processed 275 attachment events  
**Subscribers:** Downstream processing systems, analytics pipelines  
**Message Schema:**
```json
{
  "claimNumber": "CLM20240115001",
  "memberId": "123456789",
  "providerNpi": "1234567890",
  "attachmentReference": "ATT001",
  "blobPath": "hipaa-attachments/raw/275/2024/01/15/file.edi",
  "status": "processed",
  "timestamp": "2024-01-15T10:30:00Z"
}
```

#### 2. rfai-requests
**Purpose:** Queue RFAI (Request for Additional Information) requests for 277 generation  
**Subscribers:** rfai277 workflow  
**Message Schema:**
```json
{
  "claimNumber": "CLM20240115001",
  "rfaiReasonCode": "A7",
  "rfaiReference": "RFAI20240115001",
  "requestedDocuments": ["Medical Records", "Lab Results"],
  "dueDate": "2024-01-22",
  "timestamp": "2024-01-15T11:00:00Z"
}
```

#### 3. edi-278
**Purpose:** Queue 278 health care services review transactions for processing  
**Subscribers:** Processing systems, analytics, replay handlers  
**Message Schema:**
```json
{
  "blobUrl": "hipaa-attachments/raw/278/2024/01/15/review.edi",
  "fileName": "review-20240115.edi",
  "reviewType": "authorization",
  "status": "queued",
  "timestamp": "2024-01-15T12:00:00Z"
}
```

**Topic Configuration:**
- **Max Message Size:** 256 KB
- **TTL:** 14 days (default)
- **Dead-letter Queue:** Enabled
- **Duplicate Detection:** 10-minute window
- **Sessions:** Disabled (not required)

### Integration Account

**Resource Name:** `{baseName}-ia`  
**SKU:** Standard

**Purpose:** X12 EDI encoding/decoding and trading partner management

**Components:**

#### Trading Partners
| Partner | ID | Qualifier | Role |
|---------|-----|-----------|------|
| Availity | 030240928 | ZZ | Sender (275/278), Receiver (277) |
| Health Plan-QNXT | {config.payerId} | ZZ | Receiver (275/278), Sender (277) |

#### X12 Schemas
| Transaction | Version | Schema Name | Purpose |
|-------------|---------|-------------|---------|
| 275 | 005010X210 | X12_005010X210_275 | Additional Information to Support Claim |
| 277 | 005010X212 | X12_005010X212_277 | Healthcare Information Status Notification |
| 278 | 005010X217 | X12_005010X217_278 | Health Care Services Review Information |

#### X12 Agreements
1. **Availity-to-Health Plan-275-Receive**
   - Direction: Receive (Inbound)
   - Host: Health Plan-QNXT
   - Guest: Availity
   - Transaction: 275

2. **Health Plan-to-Availity-277-Send**
   - Direction: Send (Outbound)
   - Host: Health Plan-QNXT
   - Guest: Availity
   - Transaction: 277

3. **Health Plan-278-Processing**
   - Direction: Receive (Internal)
   - Host: Health Plan-QNXT
   - Guest: Health Plan-QNXT
   - Transaction: 278

### Application Insights

**Resource Name:** `{baseName}-ai`

**Purpose:** Telemetry, monitoring, and diagnostics for all workflows

**Tracked Metrics:**
- Workflow execution duration
- Success/failure rates
- API call latencies (QNXT)
- Service Bus message processing
- SFTP connection reliability
- X12 decode/encode operations
- Exception tracking and stack traces

**Custom Events:**
```
- workflow_started
- workflow_completed
- x12_decoded
- x12_encoded
- qnxt_api_called
- service_bus_published
- blob_stored
- sftp_file_deleted
```

**Queries for Common Scenarios:**
```kusto
// Failed workflow runs
traces
| where severityLevel >= 3
| where message contains "workflow"
| project timestamp, message, severityLevel

// QNXT API performance
dependencies
| where name contains "QNXT"
| summarize avg(duration), percentile(duration, 95) by bin(timestamp, 1h)

// X12 decode failures
traces
| where message contains "Decode_X12"
| where severityLevel >= 3
```

## Data Flows

### 275 Attachment Ingestion Flow

**Overview:** Process inbound attachment requests from Availity and link to claims in QNXT

```
┌─────────────┐
│  Availity   │
│    SFTP     │
└──────┬──────┘
       │ 1. New 275 file arrives
       ▼
┌─────────────────────────┐
│  ingest275 Workflow     │
│  (SFTP Trigger)         │
└──────┬──────────────────┘
       │ 2. Read file content
       ▼
┌─────────────────────────┐
│  Data Lake Storage      │
│  Archive raw file       │
│  Path: raw/275/YYYY/MM/DD
└──────┬──────────────────┘
       │ 3. File stored
       ▼
┌─────────────────────────┐
│  Integration Account    │
│  Decode X12 275         │
│  Agreement: Availity→Health Plan
└──────┬──────────────────┘
       │ 4. Decoded JSON
       ▼
┌─────────────────────────┐
│  Extract Metadata       │
│  • Claim Number         │
│  • Member ID            │
│  • Provider NPI         │
│  • Attachment Reference │
└──────┬──────────────────┘
       │ 5. Metadata extracted
       ▼
┌─────────────────────────┐
│  QNXT API               │
│  Link attachment to claim│
│  Retry: 4x, 15s interval│
└──────┬──────────────────┘
       │ 6. Linkage confirmed
       ▼
┌─────────────────────────┐
│  Service Bus            │
│  Topic: attachments-in  │
│  Message: claim details │
└──────┬──────────────────┘
       │ 7. Event published
       ▼
┌─────────────────────────┐
│  Delete SFTP File       │
│  (After success)        │
└─────────────────────────┘
```

**Duration:** ~5-15 seconds per file  
**Error Handling:** Retry logic for QNXT API (4 attempts, 15s intervals)  
**Dead Letter:** Failed messages sent to Service Bus DLQ

### 277 RFAI Response Flow

**Overview:** Generate and send status responses back to Availity

```
┌─────────────────────────┐
│  QNXT or Internal       │
│  System                 │
└──────┬──────────────────┘
       │ 1. RFAI request created
       ▼
┌─────────────────────────┐
│  Service Bus            │
│  Topic: rfai-requests   │
└──────┬──────────────────┘
       │ 2. Message queued
       ▼
┌─────────────────────────┐
│  rfai277 Workflow       │
│  (Service Bus Trigger)  │
└──────┬──────────────────┘
       │ 3. Parse RFAI request
       ▼
┌─────────────────────────┐
│  Integration Account    │
│  Encode X12 277         │
│  Agreement: Health Plan→Availity│
└──────┬──────────────────┘
       │ 4. X12 277 created
       ▼
┌─────────────────────────┐
│  Availity SFTP          │
│  Send 277 file          │
│  Path: /outbound/277/   │
└──────┬──────────────────┘
       │ 5. File transmitted
       ▼
┌─────────────────────────┐
│  Data Lake Storage      │
│  Archive sent file      │
│  Path: sent/277/YYYY/MM/DD
└──────┬──────────────────┘
       │ 6. Archive complete
       ▼
┌─────────────────────────┐
│  QNXT API               │
│  Update status          │
│  Status: transmitted    │
└─────────────────────────┘
```

**Duration:** ~3-8 seconds per message  
**Frequency:** Event-driven based on RFAI requests  
**Monitoring:** Track delivery confirmations in Application Insights

### 278 Processing and Replay Flow

**Overview:** Process health care services review information with replay capability

```
┌─────────────────────────┐         ┌─────────────────────────┐
│  SFTP Inbound           │         │  HTTP Client            │
│  (278 files)            │         │  (Replay Request)       │
└──────┬──────────────────┘         └──────┬──────────────────┘
       │                                    │
       │ 1. New 278 file                    │ 1. POST with blobUrl
       ▼                                    ▼
┌─────────────────────────┐         ┌─────────────────────────┐
│  ingest278 Workflow     │         │  replay278 Workflow     │
│  (SFTP Trigger)         │         │  (HTTP Trigger)         │
└──────┬──────────────────┘         └──────┬──────────────────┘
       │                                    │
       │ 2. Store raw file                  │ 2. Validate blobUrl
       ▼                                    │
┌─────────────────────────┐                │
│  Data Lake Storage      │                │
│  Path: raw/278/YYYY/MM/DD◀───────────────┘
└──────┬──────────────────┘         
       │ 3. Decode X12 278
       ▼
┌─────────────────────────┐
│  Integration Account    │
│  X12_005010X217_278     │
└──────┬──────────────────┘
       │ 4. Extract review data
       ▼
┌─────────────────────────┐
│  Service Bus            │
│  Topic: edi-278         │◀─── 3. Queue replay message
└──────┬──────────────────┘
       │ 5. Message published
       ▼
┌─────────────────────────┐
│  Downstream Processing  │
│  • Authorization system │
│  • Analytics pipeline   │
│  • Audit logging        │
└─────────────────────────┘
```

**Replay Benefits:**
- Deterministic reprocessing for debugging
- No need to re-upload files via SFTP
- Controlled replay with specific blobUrl
- Maintains audit trail

## Integration Points

### 1. Availity SFTP Integration

**Protocol:** SSH File Transfer Protocol (SFTP)  
**Authentication:** SSH Key-based  
**Connection:** API Connection (`sftp-ssh`)

**Inbound Paths:**
- `/inbound/attachments/` - 275 attachment requests
- `/inbound/278/` - Health care services review

**Outbound Paths:**
- `/outbound/277/` - Status responses

**File Naming Convention:**
- Inbound: `{source}_{date}_{sequence}.edi`
- Outbound: `{destination}_{date}_{sequence}.edi`

**Polling Interval:** 
- Production: 5 minutes
- UAT: 15 minutes
- DEV: 30 minutes

**Error Handling:**
- Connection failures: Exponential backoff retry
- File read errors: Move to error folder
- Authentication failures: Alert operations team

### 2. QNXT API Integration

**Base URL:** Environment-specific (DEV/UAT/PROD)  
**Authentication:** Bearer token (OAuth 2.0)  
**Content-Type:** application/json

**Endpoints:**

#### Link Attachment to Claim
```
POST /api/claims/attachments/link
Content-Type: application/json
Authorization: Bearer {token}

Request:
{
  "claimNumber": "CLM20240115001",
  "memberId": "123456789",
  "providerNpi": "1234567890",
  "attachmentReference": "ATT001",
  "documentType": "Medical Records",
  "receivedDate": "2024-01-15T10:30:00Z",
  "blobPath": "hipaa-attachments/raw/275/2024/01/15/file.edi"
}

Response:
{
  "success": true,
  "linkageId": "LINK20240115001",
  "claimStatus": "pending_review",
  "message": "Attachment linked successfully"
}
```

**Retry Policy:**
- Attempts: 4
- Interval: 15 seconds
- Exponential: true
- Max Interval: 60 seconds

**Timeout:** 30 seconds per request

**Error Codes:**
- 400: Invalid request data
- 401: Authentication failure
- 404: Claim not found
- 409: Attachment already linked
- 500: Internal server error (retry)
- 503: Service unavailable (retry)

### 3. X12 EDI Processing

**Provider:** Azure Integration Account  
**Standard:** ANSI ASC X12

**Supported Transactions:**

| Code | Name | Version | Direction |
|------|------|---------|-----------|
| 275 | Additional Information | 005010X210 | Inbound |
| 277 | Status Notification | 005010X212 | Outbound |
| 278 | Services Review | 005010X217 | Inbound |

**ISA/GS Identifiers:**
```
ISA Header:
- ISA06: Sender ID (Availity: 030240928, Health Plan: {config.payerId})
- ISA08: Receiver ID
- ISA11: Usage Indicator (T=Test, P=Production)

GS Header:
- GS02: Application Sender
- GS03: Application Receiver
- GS08: Version (e.g., 005010X210)
```

**Validation:**
- Schema compliance (XSD validation)
- Trading partner agreement match
- Segment order and cardinality
- Code set validation
- Date/time format validation

**Error Handling:**
- Schema validation failures logged
- TA1 technical acknowledgments generated
- 997 functional acknowledgments sent
- Failed messages moved to DLQ

## Design Decisions

### Why Logic Apps Standard?

**Decision:** Use Logic Apps Standard (vs Consumption)

**Rationale:**
1. **Stateful Workflows**: Required for reliable multi-step processing
2. **VNET Integration**: Future requirement for private endpoints
3. **Cost Predictability**: Fixed SKU cost vs per-execution
4. **Performance**: Better throughput for high-volume scenarios
5. **Local Development**: VS Code integration for testing

**Trade-offs:**
- Higher base cost ($200-300/month)
- More complex deployment
- Additional management overhead

### Why Data Lake Gen2?

**Decision:** Use Azure Data Lake Storage Gen2 with hierarchical namespace

**Rationale:**
1. **HIPAA Compliance**: Built-in encryption and access controls
2. **Scalability**: Petabyte-scale storage capacity
3. **Performance**: Optimized for analytics workloads
4. **Date Partitioning**: Efficient time-based queries
5. **Lifecycle Management**: Automated archival to cool/archive tiers

**Alternative Considered:** Blob Storage (flat namespace)  
**Why Rejected:** Less efficient for directory operations, no hierarchical ACLs

### Why Service Bus?

**Decision:** Use Azure Service Bus (vs Event Grid, Storage Queues)

**Rationale:**
1. **Guaranteed Delivery**: At-least-once delivery semantics
2. **Dead-letter Queue**: Built-in error handling
3. **Message Sessions**: Future support for ordered processing
4. **Duplicate Detection**: Prevents processing same message twice
5. **AMQP Protocol**: Standard messaging protocol

**Alternative Considered:** Event Grid  
**Why Rejected:** Less suitable for guaranteed message processing

### Why Separate Workflows?

**Decision:** Separate workflows for 275, 277, 278, and replay

**Rationale:**
1. **Single Responsibility**: Each workflow has one clear purpose
2. **Independent Scaling**: Scale workflows based on load
3. **Easier Debugging**: Isolate issues to specific workflow
4. **Deployment Flexibility**: Deploy/update workflows independently
5. **Monitoring**: Clear metrics per transaction type

**Alternative Considered:** Single monolithic workflow  
**Why Rejected:** Complex, harder to maintain and troubleshoot

### Why Retry Policies?

**Decision:** Implement retry logic for external API calls

**Rationale:**
1. **Transient Failures**: Network issues, timeouts
2. **Service Availability**: QNXT API may be temporarily unavailable
3. **Success Rate**: Significantly improves overall success rate
4. **Exponential Backoff**: Reduces load on struggling services

**Configuration:**
- QNXT API: 4 retries, 15s interval, exponential backoff
- SFTP: 3 retries, 30s interval
- Service Bus: Built-in retry (10 attempts)

## HIPAA Compliance

### Data Encryption

**At Rest:**
- Data Lake Storage: Azure SSE with Microsoft-managed keys
- Service Bus: Encrypted using Microsoft-managed keys
- Application Insights: Encrypted logs

**In Transit:**
- SFTP: SSH encryption (minimum TLS 1.2)
- HTTPS: All API calls use TLS 1.2+
- Service Bus: AMQP over TLS

### Access Control

**Managed Identity:**
- Logic App uses system-assigned managed identity
- No connection strings or keys stored in code
- RBAC role assignments:
  - Storage Blob Data Contributor
  - Azure Service Bus Data Sender
  - Integration Account Contributor

**Network Security:**
- HTTPS only for all endpoints
- Consider VNET integration for production
- Private endpoints for storage and Service Bus
- NSG rules to restrict access

### Audit Logging

**Application Insights:**
- All workflow runs logged
- API call tracking with request/response
- Exception tracking with stack traces
- Custom events for key milestones

**Azure Activity Log:**
- Resource changes tracked
- Access attempts logged
- Role assignments audited

**Retention:**
- Application Insights: 90 days
- Activity Log: 90 days (default)
- Storage audit logs: 365 days

### Data Classification

**PHI Elements Tracked:**
- Member/Patient ID
- Claim numbers
- Provider NPI
- Medical attachment references

**Handling Requirements:**
- Never log PHI in plain text
- Mask/redact in Application Insights
- Secure transmission only
- Access logged and monitored

### Business Associate Agreement (BAA)

**Required With:**
- Availity (trading partner)
- Any downstream systems receiving PHI
- Azure (Microsoft BAA in place)

## Scalability and Performance

### Current Capacity

**Logic Apps:**
- Concurrent runs: 25 (WS1 SKU)
- Typical duration: 5-15 seconds per 275
- Throughput: ~100-150 messages/minute

**Service Bus:**
- Max message size: 256 KB
- Queue depth: 80 GB (Standard tier)
- Throughput: 2,000 messages/second

**Data Lake:**
- Storage: Unlimited
- Throughput: 60 Gbps per storage account
- Operations: 20,000 requests/second

### Scaling Strategies

**Vertical Scaling:**
- Upgrade Logic App SKU (WS1 → WS2 → WS3)
- Increase Service Bus tier (Standard → Premium)

**Horizontal Scaling:**
- Multiple Logic App instances (scale-out)
- Service Bus partitioning for topics
- Geo-redundant storage accounts

**Performance Optimization:**
- Batch processing for high volumes
- Parallel workflow execution
- Connection pooling for APIs
- Caching for frequently accessed data

### Monitoring and Alerts

**Key Metrics:**
- Workflow run success rate (target: >99%)
- Average execution duration (target: <15s)
- QNXT API latency (target: <2s)
- Service Bus queue depth (alert if >1000)
- Storage IOPS utilization

**Alerts:**
- Workflow failure rate >1%
- QNXT API timeout rate >5%
- Service Bus dead-letter count >10
- Storage throttling detected

---

For deployment procedures, see [DEPLOYMENT.md](DEPLOYMENT.md)  
For troubleshooting, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md)  
For security guidelines, see [SECURITY.md](SECURITY.md)
