# Prior Authorization & Clinical Attachments Architecture

## Overview

Cloud Health Office supports the complete prior authorization workflow with clinical attachments, including the RFAI (Request for Additional Information) process.

## Architecture Diagram

```mermaid
graph TB
    subgraph "Provider Systems"
        Provider[Healthcare Provider]
    end

    subgraph "Cloud Health Office - Authorization & Attachments"
        AuthAPI[Authorization Service<br/>278 Prior Auth API]
        AttachAPI[Attachment Service<br/>275 Attachments API]
        AckService[Acknowledgment Service<br/>999/824 Generator]
        TPConfig[Trading Partner Config]
    end

    subgraph "Data Layer"
        CosmosAuth[(Cosmos DB<br/>Authorizations)]
        CosmosAttach[(Cosmos DB<br/>Attachments)]
        CosmosTP[(Cosmos DB<br/>Trading Partners)]
        BlobStorage[(Azure Blob Storage<br/>Clinical Documents)]
    end

    subgraph "Payer Systems"
        Payer[Health Plan / Payer]
    end

    %% Initial Authorization Request
    Provider -->|1. Submit 278<br/>Prior Auth Request| AuthAPI
    Provider -.->|1a. Optional: Unsolicited 275<br/>Clinical Attachments| AttachAPI
    
    AuthAPI -->|Store| CosmosAuth
    AttachAPI -->|Store Metadata| CosmosAttach
    AttachAPI -->|Store Files| BlobStorage
    AttachAPI -->|Link via AuthorizationId| CosmosAuth
    
    %% RFAI Workflow (Pended)
    AuthAPI -->|2. Status: Pended (A4)<br/>Set RFAIReference| CosmosAuth
    AuthAPI -->|3. Generate 277 RFAI<br/>Request Additional Info| Payer
    
    %% Provider Responds to RFAI
    Provider -->|4. Submit 275<br/>Solicited Attachment<br/>with RFAIReference| AttachAPI
    AttachAPI -->|5. Link to Authorization<br/>Update RFAIResponseDate| CosmosAuth
    
    %% Acknowledgments
    AttachAPI -->|Query Config| TPConfig
    TPConfig -->|Get Payer Preferences| CosmosTP
    AttachAPI -->|6a. Generate 999<br/>Syntax Acknowledgment| AckService
    AttachAPI -->|6b. Generate 824<br/>Business Acknowledgment| AckService
    AckService -->|7. Send Acknowledgment| Provider
    
    %% Final Approval
    AuthAPI -->|8. Update Status<br/>Approved/Denied| CosmosAuth
    AuthAPI -->|9. Send 278 Response| Payer
    Payer -->|10. Notify Provider| Provider

    style Provider fill:#e1f5ff
    style Payer fill:#fff4e1
    style AuthAPI fill:#d4edda
    style AttachAPI fill:#d4edda
    style AckService fill:#d4edda
    style CosmosAuth fill:#cce5ff
    style CosmosAttach fill:#cce5ff
    style CosmosTP fill:#cce5ff
    style BlobStorage fill:#cce5ff
```

## Workflow Details

### 1. Initial Authorization Request

**Transaction:** 278 Prior Authorization Request

**Flow:**
1. Provider submits 278 to Authorization Service
2. System stores in Cosmos DB `Authorizations` container
3. Provider MAY include unsolicited 275 attachments proactively
4. System returns initial status (Auto-Approved, Pended, or Denied)

**Unsolicited Attachments:**
- Submitted WITH the 278 request
- No `RFAIReference` (proactive documentation)
- Linked via `AuthorizationId`
- Helps avoid delays and RFAI

### 2. RFAI Process (If Pended)

**Transaction:** 277 Additional Information Request

**Flow:**
1. Authorization status set to **Pended (A4)**
2. System generates `RFAIReference` (tracking number)
3. System updates Authorization:
   - `RFAIIssued = true`
   - `RFAIIssuedDate = now`
   - `RFAIReference = TRN02 segment value`
4. 277 RFAI sent to provider
5. Provider has configured timeframe to respond (typically 14-30 days)

### 3. Solicited Attachment Response

**Transaction:** 275 Attachment (Solicited)

**Flow:**
1. Provider submits 275 with:
   - `AuthorizationId` (links to authorization)
   - `RFAIReference` (links to 277 RFAI)
   - Clinical document files (PDF, images, etc.)
2. System stores:
   - Metadata in Cosmos DB `Attachments` container
   - Files in Azure Blob Storage
3. System updates Authorization:
   - `RFAIResponseDate = now`
   - Status may change to "Under Review"
4. Attachment automatically linked to authorization

### 4. Acknowledgment Generation

**Transactions:** 999 Implementation Acknowledgment, 824 Application Advice

**999 (Syntax Validation):**
- Sent immediately upon receipt
- Validates EDI structure is correct
- Reports segment-level errors
- **When:** Always (technical acknowledgment)

**824 (Business Response):**
- Sent after business processing
- Detailed acceptance/rejection reasons
- Includes attachment ID, authorization linkage
- **When:** Based on trading partner configuration

**Trading Partner Configuration:**
- Payer-specific preferences stored in `TradingPartners` container
- Options: `999`, `824`, or `Both`
- Auto-send flag for automation
- Includes EDI interchange IDs (ISA06/ISA08, GS02/GS03)

### 5. Final Authorization Decision

**Transaction:** 278 Prior Authorization Response

**Flow:**
1. Payer reviews authorization + all attachments
2. System updates Authorization status:
   - **Approved** - Services authorized
   - **Denied** - Services not authorized
   - **Modified** - Partial approval
3. 278 response sent to provider
4. Workflow complete

## API Endpoints

### Authorization Service

```
POST   /api/Authorizations              # Submit 278 prior auth request
GET    /api/Authorizations/{id}         # Get authorization by ID
GET    /api/Authorizations/tenant/{id}  # Get all for tenant
PUT    /api/Authorizations/{id}         # Update authorization status
POST   /api/Authorizations/{id}/rfai    # Generate 277 RFAI (future)
GET    /health                           # Health check
```

### Attachment Service

```
POST   /api/Attachments                          # Submit 275 with file upload
GET    /api/Attachments/{id}                     # Get attachment by ID
GET    /api/Attachments/authorization/{authId}   # Get all for authorization
GET    /api/Attachments/claim/{claimId}          # Get all for claim
GET    /api/Attachments/appeal/{appealId}        # Get all for appeal
GET    /api/Attachments/rfai/{rfaiReference}     # Find by RFAI reference
GET    /api/Attachments/{id}/download            # Download file
POST   /api/Attachments/{id}/acknowledgment      # Generate 999/824
GET    /health                                    # Health check
```

## Data Models

### Authorization

```csharp
{
  "id": "auth-20260207-001",
  "tenantId": "blueshield-ca",
  "payerId": "BSCA123456789",
  "payerName": "Blue Shield of California",
  "subscriberId": "BSCA987654321",
  "patientFirstName": "John",
  "patientLastName": "Doe",
  "requestedService": "MRI - Brain",
  "procedureCode": "70551",
  "diagnosisCode": "G43.909",
  "requestDate": "2026-02-07T10:00:00Z",
  "status": "Pended",
  
  // RFAI Tracking
  "rfaiReference": "RFAI-2026-12345",
  "rfaiIssued": true,
  "rfaiIssuedDate": "2026-02-07T14:00:00Z",
  "rfaiResponseDate": "2026-02-08T09:30:00Z"
}
```

### Attachment

```csharp
{
  "id": "attach-20260207-001",
  "tenantId": "blueshield-ca",
  
  // Link to parent entity (mutually exclusive)
  "authorizationId": "auth-20260207-001",
  "claimId": null,
  "appealId": null,
  
  // Attachment type
  "rfaiReference": "RFAI-2026-12345",  // null for unsolicited
  "attachmentType": "Solicited",         // or "Unsolicited"
  
  // Document details
  "documentType": "Medical Records",
  "documentFormat": "PDF",
  "blobUrl": "https://chostorage97884.blob.core.windows.net/attachments/...",
  "fileSizeBytes": 2457600,
  "fileHash": "sha256-hash",
  
  // Acknowledgment
  "acknowledgmentType": "999",
  "acknowledgmentSent": true,
  "acknowledgmentSentDate": "2026-02-07T14:30:15Z",
  "generated999": "ISA*00*...",
  "generated824": null
}
```

### Trading Partner

```csharp
{
  "id": "tp-bsca",
  "tenantId": "blueshield-ca",
  "partnerId": "BSCA123456789",
  "partnerName": "Blue Shield of California",
  
  // Acknowledgment preferences
  "attachmentAckType": "Both",  // 999, 824, or Both
  "claimAckType": "999",
  "autoSendAcknowledgments": true,
  
  // EDI identifiers
  "interchangeSenderId": "CLOUDHEALTH",
  "interchangeReceiverId": "BSCA",
  "applicationSenderId": "CLOUDHEALTH",
  "applicationReceiverId": "BSCA"
}
```

## Integration Scenarios

### Scenario 1: Proactive Provider (No RFAI)

1. Provider submits 278 + unsolicited 275 attachments
2. Payer reviews immediately with all documentation
3. 999 acknowledgment sent to provider
4. Authorization approved without RFAI
5. 278 response sent

**Timeline:** 1-2 business days

### Scenario 2: RFAI Required

1. Provider submits 278 (no attachments)
2. Payer pends authorization (A4)
3. 277 RFAI generated with tracking reference
4. Provider submits solicited 275 with RFAI reference
5. 824 acknowledgment sent with linkage confirmation
6. Payer reviews with attachments
7. Authorization decision made
8. 278 response sent

**Timeline:** 7-14 business days

### Scenario 3: Multi-Document Authorization

1. Provider submits 278
2. Provider submits 3 unsolicited 275 attachments:
   - Lab results
   - Imaging reports
   - Clinical notes
3. Payer pends, issues RFAI for specialist consultation
4. Provider submits 1 solicited 275 (consultation notes)
5. All 4 attachments queryable via `GET /api/Attachments/authorization/{id}`
6. Payer reviews all documentation
7. Authorization approved

## Multi-Entity Support

Attachments support **three parent entity types**:

### Claims (837)
- Link via `claimId`
- Supporting documentation for claim adjudication
- Appeals documentation

### Authorizations (278)
- Link via `authorizationId`
- Clinical documentation for prior auth
- RFAI responses

### Appeals
- Link via `appealId`
- Reconsideration documentation
- Additional clinical evidence

**Validation:** Exactly ONE parent entity must be specified per attachment.

## Trading Partner Onboarding

To onboard a new payer:

1. Create Trading Partner record in Cosmos DB:
```json
{
  "tenantId": "your-tenant",
  "partnerId": "PAYER123",
  "partnerName": "Example Health Plan",
  "attachmentAckType": "Both",
  "autoSendAcknowledgments": true,
  "interchangeSenderId": "CLOUDHEALTH",
  "interchangeReceiverId": "PAYER123"
}
```

2. System will automatically:
   - Generate acknowledgments per preference
   - Use correct EDI identifiers
   - Apply payer-specific business rules

## Security & Compliance

- **HIPAA Compliant:** All PHI encrypted at rest and in transit
- **Azure Blob Storage:** Private containers, no public access
- **Cosmos DB:** TLS 1.2+, partition isolation by tenant
- **Access Control:** Kubernetes RBAC, service-to-service authentication
- **Audit Trail:** All transactions logged with timestamps

## Performance

- **Authorization Service:** 2 replicas, auto-scaling enabled
- **Attachment Service:** 2 replicas, auto-scaling enabled
- **Cosmos DB:** 400 RU/s per container (burst to 4000 RU/s)
- **Blob Storage:** Standard LRS, 99.9% availability
- **Typical Response Time:** <200ms for API calls, <2s for file uploads

## Monitoring

- **Health Checks:** `/health` endpoints on both services
- **Kubernetes:** Liveness and readiness probes
- **Logs:** Structured logging to Azure Monitor
- **Metrics:** Request rate, error rate, latency, storage usage

## Future Enhancements

- [ ] RFAI automation (auto-generate 277, auto-link 275)
- [ ] 276/277 claim status inquiry
- [ ] Real-time payer integration via FHIR/REST APIs
- [ ] ML-based attachment requirement prediction
- [ ] Batch processing for high-volume scenarios
- [ ] Advanced analytics dashboard
