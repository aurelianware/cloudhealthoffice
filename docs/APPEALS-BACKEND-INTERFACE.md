# Appeals Backend Interface Specification

## Overview

This document defines the interface contract between the Appeals Integration Layer and the Health Plan Backend System (e.g., claims processing system). This specification is **payer-agnostic and designed for multi-payer platformization**, enabling rapid onboarding of new health plans through configuration rather than custom development.

### Platform Design Principles

1. **Backend Agnostic**: Interface works with any claims processing system (QNXT, FacetsRx, TriZetto, etc.)
2. **Configuration-Driven**: Backend endpoints, authentication, and field mappings defined in payer configuration
3. **Standardized Contract**: Consistent API interface across all payers
4. **Extensible**: Support for payer-specific custom fields via configuration
5. **Testable**: Mock implementations available for testing without backend connectivity

## Purpose

The Appeals Backend Interface enables:
1. **Appeal Registration**: Register new appeals in the backend system
2. **Status Updates**: Synchronize appeal status between frontend and backend
3. **Authorization**: Validate user access to appeals and documents
4. **Notification**: Trigger backend workflows when appeal events occur

## Interface Architecture

```
┌─────────────────────────────────────────────┐
│   Appeals Integration Layer                 │
│   (Logic Apps + REST APIs)                  │
└─────────────────┬───────────────────────────┘
                  │
                  │ Backend Interface Contract
                  │
┌─────────────────▼───────────────────────────┐
│   Health Plan Backend System                │
│   (Claims Processing / Core System)         │
└─────────────────────────────────────────────┘
```

## Interface Endpoints

The Health Plan Backend System must implement the following endpoints:

### 1. Register Appeal

**Endpoint**: `POST /api/backend/appeals/register`

**Purpose**: Register a new appeal in the backend claims processing system.

**Request Body**:
```json
{
  "appealId": "{appeal.appealId}",
  "caseNumber": "{appeal.caseNumber}",
  "claimNumber": "{claim.claimNumber}",
  "additionalClaims": ["{claim.additionalClaimNumbers}"],
  "providerNpi": "{provider.npi}",
  "memberId": "{member.memberId}",
  "patientFirstName": "{member.firstName}",
  "patientLastName": "{member.lastName}",
  "patientDateOfBirth": "{member.dateOfBirth}",
  "requestReason": "{appeal.requestReason}",
  "supportingRationale": "{appeal.supportingRationale}",
  "submissionDate": "{appeal.submissionDate}",
  "attachments": [
    {
      "fileName": "{document.fileName}",
      "documentType": "{document.documentType}",
      "blobPath": "cloud-health-office/appeals/{appeal.appealId}/PROVIDER_UPLOAD/{document.fileName}",
      "fileSizeBytes": "{document.fileSizeBytes}"
    }
  ],
  "availityTraceId": "{availity.traceId}",
  "ecsAdditionalProperties": {
    "appealType": "{config.defaultAppealType}",
    "providerParticipationStatus": "{provider.participationStatus}",
    "programId": "{config.payerId}"
  }
}
```

**Response**:
```json
{
  "success": true,
  "appealId": "{appeal.appealId}",
  "backendAppealId": "{backend.appealId}",
  "status": "RECEIVED",
  "subStatus": "REQUEST_RECEIVED",
  "assignedReviewer": null,
  "estimatedCompletionDate": "{timestamp.plus60Days}",
  "registrationTimestamp": "{timestamp}"
}
```

**Error Response** (400):
```json
{
  "success": false,
  "error": "ValidationError",
  "message": "Claim number does not exist in system",
  "claimNumber": "{claim.claimNumber}"
}
```

---

### 2. Update Appeal Status

**Endpoint**: `POST /api/backend/appeals/update-status`

**Purpose**: Update the status of an existing appeal in the backend system.

**Request Body**:
```json
{
  "appealId": "{appeal.appealId}",
  "backendAppealId": "{backend.appealId}",
  "status": "IN_REVIEW",
  "subStatus": "IN_CLINICAL_REVIEW",
  "assignedReviewer": "{reviewer.id}",
  "reviewerNotes": "Clinical documentation review in progress",
  "updatedBy": "SYSTEM",
  "updateTimestamp": "{timestamp}"
}
```

**Response**:
```json
{
  "success": true,
  "appealId": "{appeal.appealId}",
  "backendAppealId": "{backend.appealId}",
  "previousStatus": "SUBMITTED",
  "newStatus": "IN_REVIEW",
  "updateTimestamp": "{timestamp}"
}
```

---

### 3. Finalize Appeal Decision

**Endpoint**: `POST /api/backend/appeals/finalize`

**Purpose**: Record final decision on an appeal and trigger payment/notification workflows.

**Request Body**:
```json
{
  "appealId": "{appeal.appealId}",
  "backendAppealId": "{backend.appealId}",
  "decision": "APPROVED",
  "decisionReason": "{appeal.decisionReason}",
  "decisionDate": "{appeal.decisionDate}",
  "approvedAmount": "{appeal.approvedAmount}",
  "deniedAmount": "{appeal.deniedAmount}",
  "reviewerNotes": "{reviewer.notes}",
  "decisionMadeBy": "{reviewer.id}",
  "decisionLetterPath": "cloud-health-office/appeals/{appeal.appealId}/DECISION_LETTER/{document.fileName}"
}
```

**Response**:
```json
{
  "success": true,
  "appealId": "{appeal.appealId}",
  "backendAppealId": "{backend.appealId}",
  "decision": "APPROVED",
  "paymentReferenceId": "{payment.referenceId}",
  "providerNotificationSent": true,
  "memberNotificationSent": true,
  "finalizedTimestamp": "{timestamp}"
}
```

---

### 4. Validate Appeal Access (Authorization)

**Endpoint**: `POST /api/backend/authorization/validate-appeal-access`

**Purpose**: Validate whether a user has permission to access an appeal or document.

**Request Body**:
```json
{
  "appealId": "{appeal.appealId}",
  "documentId": "{document.documentId}",
  "requestedBy": "provider",
  "userId": "{user.id}",
  "providerNpi": "{provider.npi}",
  "operation": "READ_DOCUMENT"
}
```

**Response** (200 - Authorized):
```json
{
  "authorized": true,
  "appealId": "{appeal.appealId}",
  "userId": "{user.id}",
  "accessLevel": "FULL",
  "validUntil": "{timestamp.plus90Days}",
  "validationTimestamp": "{timestamp}"
}
```

**Response** (403 - Not Authorized):
```json
{
  "authorized": false,
  "appealId": "{appeal.appealId}",
  "userId": "{user.id}",
  "reason": "User does not have access to this appeal",
  "validationTimestamp": "{timestamp}"
}
```

---

### 5. Get Appeal Details

**Endpoint**: `GET /api/backend/appeals/{appealId}`

**Purpose**: Retrieve full appeal details from backend system.

**Query Parameters**:
- `appealId` (required): Health plan appeal identifier

**Response**:
```json
{
  "success": true,
  "appealId": "{appeal.appealId}",
  "backendAppealId": "{backend.appealId}",
  "caseNumber": "{appeal.caseNumber}",
  "status": "FINALIZED",
  "subStatus": "PENDING_PAYMENT",
  "claimNumber": "{claim.claimNumber}",
  "providerNpi": "{provider.npi}",
  "memberId": "{member.memberId}",
  "decision": "APPROVED",
  "decisionReason": "{appeal.decisionReason}",
  "decisionDate": "{appeal.decisionDate}",
  "approvedAmount": "{appeal.approvedAmount}",
  "assignedReviewer": "{reviewer.id}",
  "receivedDate": "{appeal.receivedDate}",
  "lastUpdatedDate": "{appeal.lastUpdatedDate}",
  "attachments": [
    {
      "documentId": "{document.documentId}",
      "fileName": "{document.fileName}",
      "documentType": "MEDICAL_RECORDS",
      "uploadedDate": "{document.uploadedDate}"
    }
  ]
}
```

---

### 6. Correlate Appeal with Claim

**Endpoint**: `POST /api/backend/claims/correlate-appeal`

**Purpose**: Link an appeal to the original claim in the claims processing system.

**Request Body**:
```json
{
  "claimNumber": "{claim.claimNumber}",
  "memberId": "{member.memberId}",
  "providerNpi": "{provider.npi}",
  "appealId": "{appeal.appealId}",
  "serviceFromDate": "{claim.serviceFromDate}",
  "serviceToDate": "{claim.serviceToDate}"
}
```

**Response**:
```json
{
  "success": true,
  "claimNumber": "{claim.claimNumber}",
  "claimId": "{backend.claimId}",
  "appealId": "{appeal.appealId}",
  "originalClaimAmount": "{claim.billedAmount}",
  "paidAmount": "{claim.paidAmount}",
  "deniedAmount": "{claim.deniedAmount}",
  "denialReasonCode": "{claim.denialReasonCode}",
  "denialReasonDescription": "{claim.denialReasonDescription}",
  "correlationTimestamp": "{timestamp}"
}
```

---

### 7. Request Additional Information (RFAI)

**Endpoint**: `POST /api/backend/appeals/request-additional-info`

**Purpose**: Request additional information from provider for appeal processing.

**Request Body**:
```json
{
  "appealId": "{appeal.appealId}",
  "backendAppealId": "{backend.appealId}",
  "requiredDocuments": [
    "{rfai.requiredDocuments}"
  ],
  "rfaiReasonCode": "INCOMPLETE_DOCUMENTATION",
  "rfaiMessage": "{rfai.message}",
  "dueDate": "{rfai.dueDate}",
  "requestedBy": "{reviewer.id}"
}
```

**Response**:
```json
{
  "success": true,
  "appealId": "{appeal.appealId}",
  "rfaiId": "{rfai.id}",
  "status": "NEED_ADDITIONAL_INFO",
  "dueDate": "{rfai.dueDate}",
  "providerNotificationSent": true,
  "rfaiTimestamp": "{timestamp}"
}
```

---

## Event Notifications

The backend system should publish events to Service Bus topics to enable real-time synchronization:

### Topic: `payer-appeal-status-updates`

Published when appeal status changes in backend system. Consumed by `appeal_update_from_payer_to_availity` workflow.

**Message Format**:
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
  "providerNpi": "1234567890",
  "eventTimestamp": "2024-03-10T16:45:30Z"
}
```

**User Properties**:
```json
{
  "eventType": "appeal-status-update",
  "appealId": "APP-2024-001234",
  "status": "FINALIZED",
  "priority": "normal"
}
```

---

## Data Model

### Appeal Entity

Backend system should store the following core appeal attributes:

```
appealId (PK)           - Health plan appeal identifier
backendAppealId         - Backend system internal ID
caseNumber              - External tracking/case number
claimNumber             - Original claim number
additionalClaims[]      - Additional claims (for consolidated appeals)
status                  - Current status (RECEIVED, SUBMITTED, IN_REVIEW, FINALIZED, REJECTED)
subStatus               - Detailed sub-status (8 values per QRE)
decision                - Final decision (APPROVED, PARTIALLY_APPROVED, DENIED, WITHDRAWN)
decisionReason          - Explanation for decision
decisionDate            - Date decision was made
approvedAmount          - Approved amount in dollars
deniedAmount            - Denied amount in dollars
providerNpi             - Provider National Provider Identifier
memberId                - Member identifier
patientFirstName        - Patient first name
patientLastName         - Patient last name
patientDateOfBirth      - Patient date of birth
requestReason           - Reason for appeal
supportingRationale     - Detailed rationale
assignedReviewer        - Reviewer/case manager assigned
reviewerNotes           - Internal reviewer notes
receivedDate            - Date appeal was received
submissionDate          - Date submitted to backend
estimatedCompletionDate - Estimated completion date
actualCompletionDate    - Actual completion date
availityTraceId         - Availity trace identifier
payerTraceId            - Backend trace identifier
lastUpdatedDate         - Last update timestamp
createdBy               - User who created record
updatedBy               - User who last updated record
```

### Document Reference Entity

```
documentId (PK)         - Unique document identifier
appealId (FK)           - Associated appeal
documentType            - Type (PROVIDER_UPLOAD, DECISION_LETTER)
fileName                - Original file name
fileType                - File extension (.pdf, .jpg, etc.)
fileSizeBytes           - File size in bytes
blobPath                - Azure Blob Storage path
uploadedDate            - Date uploaded
uploadedBy              - User who uploaded
description             - Document description
contentType             - MIME type
availableUntil          - Date document is available until
```

---

## Integration Patterns

### Pattern 1: Synchronous Appeal Registration

```
1. Provider submits appeal via Availity
2. Appeals API receives request
3. Appeals API calls /api/backend/appeals/register (synchronous)
4. Backend validates and registers appeal
5. Backend returns success with appealId
6. Appeals API returns response to Availity/provider
```

**Pros**: Immediate feedback, simple error handling
**Cons**: Timeout risk for long-running operations

---

### Pattern 2: Asynchronous Appeal Processing

```
1. Provider submits appeal via Availity
2. Appeals API receives request
3. Appeals API publishes message to Service Bus topic
4. Appeals API returns acknowledgment immediately
5. Backend processor consumes message asynchronously
6. Backend registers appeal and publishes status update
7. Status update triggers notification to Availity
```

**Pros**: Better resilience, no timeout risk
**Cons**: Eventual consistency, more complex error handling

---

## Security Requirements

### Authentication

All backend endpoints must:
- Require authentication via OAuth 2.0 Bearer tokens OR Azure Managed Identity
- Validate token audience and scopes
- Return 401 for missing/invalid tokens

### Authorization

All backend endpoints must:
- Implement role-based access control (RBAC)
- Verify user has permission for requested operation
- Log all access attempts (success and failure)
- Return 403 for unauthorized access

### Data Protection

All backend APIs must:
- Use TLS 1.2+ for transport encryption
- Mask PHI in logs and error messages
- Implement rate limiting to prevent abuse
- Validate all input data to prevent injection attacks

---

## Error Handling

### Standard Error Response Format

```json
{
  "success": false,
  "error": "ErrorCode",
  "message": "Human-readable error message",
  "details": {
    "field": "value",
    "additionalInfo": "..."
  },
  "timestamp": "2024-01-15T14:35:00Z",
  "requestId": "REQ-123-456"
}
```

### Common Error Codes

- `ValidationError`: Invalid request data
- `NotFoundError`: Appeal/claim not found
- `AuthorizationError`: User not authorized
- `DuplicateError`: Appeal already exists
- `StateTransitionError`: Invalid status transition
- `TimeoutError`: Operation timed out
- `SystemError`: Internal system error

---

## Performance Requirements

### Response Time SLAs

- Register Appeal: < 2 seconds (p95)
- Update Status: < 1 second (p95)
- Validate Access: < 500ms (p95)
- Get Appeal Details: < 1 second (p95)

### Throughput Requirements

- Support 100 concurrent appeal registrations
- Support 1,000 status updates per minute
- Support 500 authorization checks per second

### Availability

- Target: 99.9% uptime
- Planned maintenance windows: Communicate 48 hours in advance

---

## Testing

### Integration Test Scenarios

1. **Register valid appeal** → Should return success with appealId
2. **Register appeal with non-existent claim** → Should return 400 ValidationError
3. **Update appeal status** → Should return success with new status
4. **Finalize appeal with decision** → Should trigger payment and notifications
5. **Validate access (authorized user)** → Should return authorized=true
6. **Validate access (unauthorized user)** → Should return authorized=false
7. **Get appeal details** → Should return complete appeal information
8. **Request additional information** → Should update status and notify provider

### Mock Backend Implementation

For testing, a mock backend service should implement all endpoints with:
- Configurable response delays
- Configurable error injection
- In-memory data store
- Validation of request schemas

---

## Monitoring and Observability

### Required Metrics

Backend system should expose:
- Appeal registration success rate
- Average appeal processing time (received → finalized)
- Status update latency
- Authorization check latency
- API error rate (by endpoint and error type)

### Required Logs

Backend system should log:
- All API requests (method, endpoint, status code, duration)
- All authorization checks (user, appealId, authorized, reason)
- All status transitions (appealId, from status, to status, timestamp)
- All errors (with stack traces for 500 errors)

### Health Check Endpoint

Backend must implement: `GET /api/backend/health`

Response:
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "database": "connected",
  "servicebus": "connected",
  "timestamp": "2024-01-15T14:35:00Z"
}
```

---

## Configuration

### Platform Configuration

All backend interface implementations are configured through the **Unified Availity Integration Configuration Schema**. Each payer defines their backend connection parameters in their configuration file:

```json
{
  "payerId": "{config.payerId}",
  "appeals": {
    "enabled": true,
    "backend": {
      "apiBaseUrl": "{config.backendApiBaseUrl}",
      "apiTimeout": "30s",
      "authType": "ManagedIdentity",
      "clientIdSecretName": "appeals-backend-client-id",
      "clientSecretSecretName": "appeals-backend-client-secret"
    },
    "serviceBus": {
      "topic": "payer-appeal-status-updates",
      "subscription": "backend-updates"
    },
    "retryPolicy": {
      "maxRetries": 3,
      "retryInterval": "10s",
      "retryOn": ["500", "502", "503", "504"]
    },
    "fieldMappings": {
      "appealId": "backend.appeal_id",
      "caseNumber": "backend.case_num",
      "memberId": "backend.subscriber_id"
    }
  }
}
```

### Configuration Parameters

| Parameter | Type | Description | Required |
|-----------|------|-------------|----------|
| `payerId` | string | Unique payer identifier | Yes |
| `backend.apiBaseUrl` | string | Base URL for backend API | Yes |
| `backend.authType` | string | Authentication method (ManagedIdentity, OAuth2, ApiKey) | Yes |
| `backend.clientIdSecretName` | string | Azure Key Vault secret name for client ID | Conditional |
| `backend.clientSecretSecretName` | string | Azure Key Vault secret name for client secret | Conditional |
| `serviceBus.topic` | string | Service Bus topic for status updates | Yes |
| `fieldMappings` | object | Backend field mapping dictionary | No (uses defaults) |
| `retryPolicy` | object | Retry configuration for backend calls | No (uses defaults) |

---

## Versioning

Backend APIs should implement versioning via:
- URL path versioning: `/api/v1/backend/appeals/...`
- Support for multiple versions simultaneously
- Deprecation notices 6 months before version removal

---

## Migration Guide

For health plans implementing this interface:

1. **Phase 1**: Implement core endpoints (register, update status, get details)
2. **Phase 2**: Implement authorization endpoint
3. **Phase 3**: Implement event publishing to Service Bus
4. **Phase 4**: Implement RFAI and finalization endpoints

Each phase should be tested in DEV → UAT → PROD environments.

---

## Developer Guide: Implementing Backend Interface

### For Backend System Developers

If you're implementing this interface for a new claims processing system:

#### Step 1: Review Interface Contract

Read through all 7 endpoint specifications above to understand the complete API contract.

#### Step 2: Implement Core Endpoints

Start with the 3 core endpoints:
1. Register Appeal (`POST /api/backend/appeals/register`)
2. Update Appeal Status (`POST /api/backend/appeals/update-status`)
3. Validate Appeal Access (`POST /api/backend/authorization/validate-appeal-access`)

#### Step 3: Implement Service Bus Events

Set up event publishing to Service Bus topic `payer-appeal-status-updates` for status changes.

#### Step 4: Test with Mock Integration Layer

Use the provided mock integration layer to test your backend implementation:
```bash
npm run test:backend-interface -- --backend-url http://localhost:8080
```

#### Step 5: Configure Field Mappings

Define field mappings in your payer configuration to map standard interface fields to your backend schema:
```json
{
  "appeals": {
    "backend": {
      "fieldMappings": {
        "appealId": "your_backend.appeal_id",
        "memberId": "your_backend.subscriber_num",
        "claimNumber": "your_backend.claim_ref"
      }
    }
  }
}
```

### For Platform Integrators

If you're adding a new payer to the platform:

#### Step 1: Obtain Backend API Documentation

Request API documentation and credentials from the payer's IT team.

#### Step 2: Create Payer Configuration

Define backend connection in the payer's configuration file:
```bash
node dist/scripts/cli/payer-generator-cli.js template -t custom -o new-payer.json
```

#### Step 3: Configure Field Mappings

Map backend-specific field names to the standard interface using `fieldMappings` configuration.

#### Step 4: Generate and Deploy

Generate deployment package and deploy to Azure:
```bash
node dist/scripts/cli/payer-generator-cli.js generate -c new-payer.json
cd generated/NEW-PAYER-ID/infrastructure
./deploy.sh
```

## Support

For questions about this interface specification:
- Technical Documentation: See this file and related schemas
- Implementation Examples: See test-workflows.ps1
- API Specifications: See docs/api/APPEALS-OPENAPI.yaml
- Configuration Schema: See [UNIFIED-CONFIG-SCHEMA.md](./UNIFIED-CONFIG-SCHEMA.md)
- Developer Tools: See [CONFIG-TO-WORKFLOW-GENERATOR.md](./CONFIG-TO-WORKFLOW-GENERATOR.md)
- Issue Tracking: GitHub Issues

---

## Changelog

### Version 2.0.0 (2025-01-15)
- Updated to support configuration-driven platform architecture
- Added platform configuration section
- Added developer guide for backend and platform integrators
- Replaced all hardcoded examples with variable placeholders
- Added field mapping configuration support

### Version 1.0.0 (2024-01-15)
- Initial specification
- All 7 core endpoints defined
- Event notification pattern specified
- Security requirements documented
- Payer-agnostic design
