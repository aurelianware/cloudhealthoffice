# Claims Appeals Integration

## Overview

This document describes the integration architecture for processing claim appeals between providers and health plans via the Availity platform. The integration is fully compliant with Availity QRE (Qualified RESTful Endpoint) requirements and supports multi-payer platformization.

## Architecture

### High-Level Flow

```
Provider → Availity Platform → Health Plan Appeals System → Health Plan Backend
                                        ↓
                                Decision Processing
                                        ↓
Health Plan → Availity Bedlam API → Availity Platform → Provider
```

### Key Components

1. **Appeal Submission Endpoint** (`/appeal/submit`)
   - Receives appeal requests from providers via Availity
   - Validates request data and attachments
   - Stores appeals in health plan system
   - Returns acknowledgment with appeal ID and case number

2. **Appeal Status Endpoint** (`/appeal/status`)
   - Provides current status of submitted appeals
   - Returns detailed information including sub-status, decision, and document links
   - Supports queries by appealId or claimNumber

3. **Document Download Endpoint** (`/appeal/document/download`)
   - Enables providers to download appeal-related documents
   - Supports two document types:
     - `PROVIDER_UPLOAD`: Original documents submitted with appeal
     - `DECISION_LETTER`: Health plan decision letters and correspondence
   - Implements authorization checks and access logging

4. **Status Update Push Workflow** (`appeal_update_from_payer_to_availity`)
   - **Direction**: Health Plan → Availity Bedlam API
   - **Trigger**: Service Bus topic `payer-appeal-status-updates`
   - **Purpose**: Push appeal status updates back to Availity platform
   - **Implementation**: Azure Logic App with Service Bus trigger
   - **Retry Logic**: 3 retries with 30-second intervals
   - **Validation**: Requires `decision` and `decisionReason` for FINALIZED status

## Data Flow

### Inbound: Provider Appeals Submission

```
1. Provider submits appeal via Availity portal
2. Availity forwards appeal to health plan /appeal/submit endpoint
3. Health plan validates and stores appeal
4. Health plan assigns appealId and caseNumber
5. Health plan returns AppealToPayerResponse with status RECEIVED
6. Appeal enters processing workflow (sub-status: REQUEST_RECEIVED)
```

### Outbound: Status Updates to Availity

```
1. Health plan adjudicates appeal (status changes)
2. Health plan publishes status update to Service Bus topic payer-appeal-status-updates
3. Logic App workflow appeal_update_from_payer_to_availity triggers
4. Workflow validates status transition (currently SUBMITTED→FINALIZED only)
5. Workflow POSTs update to Availity Bedlam API endpoint
6. Availity propagates update to provider portal
7. Workflow logs success/failure to Application Insights
```

### Document Download Flow

```
1. Provider requests document via /appeal/document/download endpoint
2. API validates query parameters (documentId, appealId, documentType)
3. API calls authorization service to verify provider access
4. API retrieves document from Azure Blob Storage
   Path: hipaa-attachments/appeals/{appealId}/{documentType}/{documentId}
5. API returns document with appropriate Content-Type and Content-Disposition headers
6. API logs access event to Application Insights
```

## Appeal Status Lifecycle

### Status Values

- **RECEIVED**: Appeal received by health plan, awaiting classification
- **SUBMITTED**: Appeal has been classified and entered into review queue
- **IN_REVIEW**: Appeal is actively under review
- **FINALIZED**: Review complete, decision rendered
- **REJECTED**: Appeal rejected (e.g., untimely filing, incomplete submission)

### Sub-Status Values (8 States per QRE Requirements)

1. **REQUEST_RECEIVED**: Initial state when appeal first received
2. **AWAITING_CLASSIFICATION**: Awaiting initial classification by health plan
3. **PENDING_ASSIGNMENT**: In queue awaiting assignment to reviewer
4. **CASE_ASSIGNED**: Assigned to reviewer or case manager
5. **IN_PROGRESS**: Actively being processed (general processing state)
6. **IN_CLINICAL_REVIEW**: Undergoing clinical review by medical personnel
7. **NEED_ADDITIONAL_INFO**: Paused pending additional information from provider
8. **PENDING_PAYMENT**: Approved, payment processing pending

### Valid Status Transitions

Current implementation supports:
- **SUBMITTED → FINALIZED** (with required decision and decisionReason)

Future implementations may support:
- RECEIVED → SUBMITTED
- SUBMITTED → IN_REVIEW
- IN_REVIEW → FINALIZED
- Any status → REJECTED

## Webhook Direction (Important)

**Critical**: The webhook direction is **Health Plan → Availity**, not Availity → Health Plan.

- **Inbound**: Providers submit appeals via Availity to health plan REST endpoints (pull model)
- **Outbound**: Health plan pushes status updates to Availity Bedlam API (push model)

This bidirectional pattern ensures:
1. Providers have real-time submission feedback
2. Health plans control the update cadence
3. Availity platform stays synchronized with health plan system of record

## Authentication

All API endpoints require **Bearer token authentication** via OAuth 2.0.

### Token Acquisition

```bash
curl -X POST https://auth.healthplan.com/oauth/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id={client_id}" \
  -d "client_secret={client_secret}" \
  -d "scope=appeals.read appeals.write"
```

### Using the Token

```bash
curl -X GET https://api.healthplan.com/v1/appeal/status?appealId=APP-2024-001234 \
  -H "Authorization: Bearer {access_token}"
```

## Request/Response Examples

### Submit Appeal

**Request**:
```json
POST /appeal/submit
Content-Type: application/json
Authorization: Bearer {token}

{
  "claimNumber": "CLM-2024-001234",
  "providerNpi": "1234567890",
  "submittingProviderType": "RENDERING",
  "providerAddress": {
    "addressLine1": "123 Medical Plaza Dr",
    "city": "Dallas",
    "state": "TX",
    "zipCode": "75201"
  },
  "contactInfo": {
    "phone": "2145551234",
    "email": "appeals@providerclinic.com"
  },
  "memberId": "MBR-987654321",
  "patientFirstName": "John",
  "patientLastName": "Smith",
  "patientDateOfBirth": "1985-03-15",
  "requestReason": "MEDICAL_NECESSITY",
  "supportingRationale": "Medical records demonstrate that the service was medically necessary per clinical guidelines.",
  "attachments": [
    {
      "fileName": "medical_records.pdf",
      "fileType": ".pdf",
      "fileSizeBytes": 2097152,
      "documentType": "MEDICAL_RECORDS"
    }
  ],
  "ecsAdditionalProperties": {
    "appealType": "FIRST_LEVEL",
    "providerParticipationStatus": "IN_NETWORK",
    "programId": "{config.payerId}",
    "dataSource": "AVAILITY"
  }
}
```

**Response**:
```json
{
  "appealId": "APP-2024-001234",
  "caseNumber": "CASE-2024-5678",
  "status": "RECEIVED",
  "subStatus": "REQUEST_RECEIVED",
  "receivedDate": "2024-01-15T14:35:00Z",
  "isEligibleForAttachmentsDate": "2024-02-15T23:59:59Z",
  "estimatedCompletionDate": "2024-03-15",
  "attachmentStatus": "COMPLETE",
  "contactInfo": {
    "phone": "8005551234",
    "email": "appeals@healthplan.com"
  },
  "availityTraceId": "AVL-2024-abc123",
  "payerTraceId": "PAY-2024-001234",
  "claimNumber": "CLM-2024-001234",
  "lastUpdatedDate": "2024-01-15T14:35:00Z"
}
```

### Get Appeal Status

**Request**:
```bash
GET /appeal/status?appealId=APP-2024-001234
Authorization: Bearer {token}
```

**Response**:
```json
{
  "appealId": "APP-2024-001234",
  "caseNumber": "CASE-2024-5678",
  "status": "FINALIZED",
  "subStatus": "PENDING_PAYMENT",
  "receivedDate": "2024-01-15T14:35:00Z",
  "decision": "APPROVED",
  "decisionReason": "Appeal approved. Medical necessity established per clinical review.",
  "decisionDate": "2024-03-10T16:45:00Z",
  "approvedAmount": 1500.00,
  "deniedAmount": 0,
  "documentLinks": [
    {
      "documentId": "DOC-2024-5678",
      "documentType": "DECISION_LETTER",
      "fileName": "decision_letter.pdf",
      "availableUntil": "2024-06-10T23:59:59Z",
      "downloadUrl": "/api/appeal/document/download?documentId=DOC-2024-5678&appealId=APP-2024-001234"
    }
  ]
}
```

### Download Document

**Request**:
```bash
GET /appeal/document/download?documentId=DOC-2024-5678&appealId=APP-2024-001234&documentType=DECISION_LETTER
Authorization: Bearer {token}
```

**Response**:
```
HTTP/1.1 200 OK
Content-Type: application/pdf
Content-Disposition: attachment; filename="decision_letter.pdf"
Content-Length: 245760

[Binary PDF content]
```

### Push Status Update to Availity

**Service Bus Message** (published to `payer-appeal-status-updates` topic):
```json
{
  "availityTraceId": "AVL-2024-abc123",
  "payerTraceId": "PAY-2024-001234",
  "appealId": "APP-2024-001234",
  "caseNumber": "CASE-2024-5678",
  "status": "FINALIZED",
  "subStatus": "PENDING_PAYMENT",
  "decision": "APPROVED",
  "decisionReason": "Appeal approved. Medical necessity established per clinical review.",
  "decisionDate": "2024-03-10T16:45:00Z",
  "approvedAmount": 1500.00,
  "deniedAmount": 0,
  "claimNumber": "CLM-2024-001234",
  "memberId": "MBR-987654321",
  "providerNpi": "1234567890"
}
```

**Logic App POSTs to Availity Bedlam API**:
```json
POST https://api.availity.com/bedlam/v1/appeals/status
Authorization: Bearer {availity_api_key}
Content-Type: application/json
X-Payer-Id: {config.payerId}

{
  "traceId": "AVL-2024-abc123",
  "payerReferenceId": "PAY-2024-001234",
  "appealId": "APP-2024-001234",
  "status": "FINALIZED",
  "decision": "APPROVED",
  "decisionReason": "Appeal approved. Medical necessity established per clinical review.",
  "updateTimestamp": "2024-03-10T16:50:00Z"
}
```

## Attachment Requirements

### File Constraints

- **Maximum attachments per appeal**: 10
- **Maximum file size**: 64MB (67,108,864 bytes) per file
- **Supported file types**: `.pdf`, `.jpg`, `.jpeg`, `.gif`, `.tif`, `.tiff`

### Storage Path Pattern

```
hipaa-attachments/appeals/{appealId}/{documentType}/{documentId}
```

Examples:
- `hipaa-attachments/appeals/APP-2024-001234/PROVIDER_UPLOAD/medical_records.pdf`
- `hipaa-attachments/appeals/APP-2024-001234/DECISION_LETTER/decision_2024-03-10.pdf`

### Document Types

- **PROVIDER_UPLOAD**: Documents submitted by provider with appeal
- **DECISION_LETTER**: Health plan decision letters and correspondence

## Configuration

### Required Parameters

All workflows and APIs require the following configuration parameters:

```json
{
  "payerId": "{config.payerId}",
  "availityBedlamEndpoint": "https://api.availity.com/bedlam/v1/appeals/status",
  "availityBedlamApiKey": "{secure-from-keyvault}",
  "serviceBusTopic": "payer-appeal-status-updates",
  "serviceBusSubscription": "availity-push",
  "blobStorageAccount": "hipaa-attachments-storage",
  "authorizationApiEndpoint": "https://api.healthplan.local/authorization"
}
```

### Security Notes

- **Never hardcode API keys or secrets** - Use Azure Key Vault references
- **Use Managed Identity** for Azure resource access when possible
- **Rotate API keys** according to security policy (recommended: 90 days)
- **Log all access** to PHI-containing documents

## Monitoring and Logging

### Application Insights Events

All workflows log the following custom events:

**Appeal Processing**:
- `AppealReceived`: New appeal submission
- `AppealValidationError`: Invalid appeal data
- `AppealStatusChanged`: Status transition

**Status Updates**:
- `AppealUpdateValidationSuccess`: Update validation passed
- `AppealUpdateValidationError`: Update validation failed
- `AppealUpdateSentToAvality`: Update successfully pushed to Availity
- `AppealUpdateError`: Update push failed

**Document Access**:
- `AppealDocumentAccessed`: Successful document download
- `AppealDocumentUnauthorizedAccess`: Unauthorized access attempt
- `AppealDocumentNotFound`: Document not found

### Key Metrics to Monitor

- Appeal submission rate (per hour)
- Appeal processing time (received → finalized)
- Document download success rate
- Availity update push success rate
- Authorization check latency
- Dead letter queue depth

## Error Handling

### Dead Letter Queue

Failed messages are sent to dead letter queue with the following reasons:

- `ValidationError`: Message failed validation
- `AppealProcessingError`: Error during appeal processing
- `AppealUpdateError`: Error pushing update to Availity
- `AuthorizationError`: Authorization check failed

### Retry Strategy

- **Appeal submission**: No automatic retry (client should retry)
- **Status update push**: 3 retries with 30-second intervals
- **Authorization checks**: 2 retries with 10-second intervals
- **Document retrieval**: No retry (immediate success/failure)

## Compliance and Security

### HIPAA Requirements

All PHI (Protected Health Information) is:
- Encrypted at rest in Azure Blob Storage
- Encrypted in transit via TLS 1.2+
- Access-logged to Application Insights (with PHI masking)
- Subject to 7-year retention requirements

### Authorization Model

Document downloads require:
1. Valid OAuth 2.0 Bearer token
2. Authorization check confirming user has access to appeal
3. Document exists in expected storage location

### Audit Trail

All operations are logged with:
- Timestamp
- User/system identifier
- Operation type
- Appeal ID / Document ID
- Success/failure status
- IP address (for API calls)

## Testing

### Test Scenarios

1. **Submit valid appeal** - Should return appealId and status RECEIVED
2. **Submit invalid appeal** - Should return 400 with validation errors
3. **Get appeal status** - Should return current status and sub-status
4. **Download document (authorized)** - Should return document binary
5. **Download document (unauthorized)** - Should return 403 Forbidden
6. **Push status update** - Should successfully POST to Availity Bedlam API
7. **Push update with invalid status** - Should send to dead letter queue

### Example cURL Commands

```bash
# Submit appeal
curl -X POST https://api.healthplan.com/v1/appeal/submit \
  -H "Authorization: Bearer {token}" \
  -H "Content-Type: application/json" \
  -d @appeal-request.json

# Get status
curl -X GET "https://api.healthplan.com/v1/appeal/status?appealId=APP-2024-001234" \
  -H "Authorization: Bearer {token}"

# Download document
curl -X GET "https://api.healthplan.com/v1/appeal/document/download?documentId=DOC-2024-5678&appealId=APP-2024-001234&documentType=DECISION_LETTER" \
  -H "Authorization: Bearer {token}" \
  -o decision_letter.pdf
```

## Troubleshooting

### Common Issues

1. **401 Unauthorized**: Check Bearer token validity and expiration
2. **403 Forbidden**: Verify user has access to requested appeal/document
3. **404 Not Found**: Confirm appealId/documentId exists in system
4. **400 Bad Request**: Review request body for missing/invalid fields
5. **Dead letter queue growth**: Check Availity Bedlam API connectivity

### Debugging Steps

1. Check Application Insights for error events
2. Review Logic App run history
3. Inspect Service Bus dead letter queue
4. Verify Azure Key Vault secret access
5. Confirm blob storage permissions

## Future Enhancements

Planned features:
- Support for additional status transitions (not just SUBMITTED→FINALIZED)
- Bulk appeal submission API
- Appeal search and filtering capabilities
- Webhook subscriptions for real-time status updates
- Support for expedited appeals with SLA tracking
- Multi-language support for decision letters

## References

- [Availity QRE Documentation](https://www.availity.com)
- [Appeal-ToPayer-Request Schema](../schemas/Appeal-ToPayer-Request.json)
- [Appeal-ToPayer-Response Schema](../schemas/Appeal-ToPayer-Response.json)
- [Appeal-SubStatus Schema](../schemas/Appeal-SubStatus.json)
- [OpenAPI Specification](./api/APPEALS-OPENAPI.yaml)
