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
# Appeals Module Integration with Enhanced Claim Status

## Overview

The Appeals module (PR #49) integrates seamlessly with Enhanced Claim Status (ECS) to enable one-click appeal workflows directly from claim status queries. This integration leverages the ValueAdds277 `eligibleForAppeal` flag and appeal metadata to provide providers with a streamlined dispute process.

## Architecture

```
┌──────────────┐      ┌──────────────┐      ┌──────────────┐
│   Provider   │ ───> │ ECS Query    │ ───> │  QNXT API    │
│   Portal     │ <─── │  (Logic App) │ <─── │              │
└──────────────┘      └──────────────┘      └──────────────┘
       │                      │
       │                      │ ValueAdds277 Response
       │                      │ - eligibleForAppeal: true
       │                      │ - appealMessage
       │                      │ - appealTimelyFilingDate
       │                      v
       │              ┌──────────────┐
       └───────────>  │   Appeals    │
          Initiate    │   Module     │
          Appeal      │  (Logic App) │
                      └──────────────┘
                              │
                              v
                      ┌──────────────┐
                      │ Service Bus  │
                      │ (appeals-in) │
                      └──────────────┘
```

## ECS Integration Points

### 1. eligibleForAppeal Flag

The `eligibleForAppeal` flag is calculated by the ECS workflow based on claim status:

```javascript
eligibleForAppeal = (claimStatus === 'Denied' || claimStatus === 'Partially Paid')
```

**When true:**
- Provider UI displays "Dispute Claim" button
- Appeal metadata is populated
- Provider can initiate appeal workflow with one click

**When false:**
- "Dispute Claim" button is disabled/hidden
- Appeal is not available for this claim status

### 2. Appeal Metadata Fields

Three metadata fields support the appeal workflow:

| Field | Type | Description | Example |
|-------|------|-------------|---------|
| `appealMessage` | string | Provider-facing message with filing deadline | "This claim may be eligible for appeal. Timely filing deadline: 2024-07-15" |
| `appealType` | string | Type of appeal allowed | "Reconsideration" |
| `appealTimelyFilingDate` | date | Last date to file appeal (CCYYMMDD) | "20240715" |

**Appeal Type Values:**
- `Reconsideration`: Standard first-level appeal
- `Redetermination`: Medicare/Medicaid specific
- `Administrative Review`: Higher-level review

**Timely Filing Calculation:**
```javascript
appealTimelyFilingDate = finalizedDate + timelyFilingDays (default: 180 days)
```

### 3. Claim Status Eligibility Matrix

| Claim Status | eligibleForAppeal | Reasoning |
|--------------|-------------------|-----------|
| Paid | false | Claim is finalized and paid in full |
| Partially Paid | **true** | Provider may dispute underpaid amount |
| Denied | **true** | Provider may appeal denial decision |
| Pending | false | Claim is still being processed |
| In Process | false | Claim is actively being reviewed |
| Suspended | false | Claim processing is temporarily paused |
| Forwarded | false | Claim forwarded to another payer |
| Adjusted | false | Claim already adjusted, use corrections |

## Complete Provider Workflow

### Scenario: Denied Claim Appeal

**Step 1: Provider Queries Claim**

Request:
```json
{
  "searchMethod": "ClaimHistory",
  "requestId": "REQ-2024-APPEAL-001",
  "submitterId": "PROV-12345",
  "claimHistorySearch": {
    "claimNumber": "CLM987654321"
  }
}
```

**Step 2: ECS Returns ValueAdds277 Response**

Response (excerpt):
```json
{
  "claims": [
    {
      "claimNumber": "CLM987654321",
      "claimStatus": "Denied",
      "statusCode": "P4",
      "statusCodeDescription": "The procedure code is inconsistent with the modifier used",
      
      "BILLED": 450.00,
      "ALLOWED": 0.00,
      "INSURANCE_TOTAL_PAID": 0.00,
      
      "reasonCodes": ["CO-96", "CO-197"],
      "comment": "Procedure code 29881 with modifier 59 is not appropriate.",
      
      "eligibleForAppeal": true,
      "appealMessage": "This claim may be eligible for appeal. Timely filing deadline: 2024-07-06",
      "appealType": "Reconsideration",
      "appealTimelyFilingDate": "20240706"
    }
  ]
}
```

**Step 3: UI Displays "Dispute Claim" Button**

UI Logic:
```javascript
function shouldShowDisputeButton(claim) {
  return claim.eligibleForAppeal === true;
}

function getAppealDeadline(claim) {
  return claim.appealTimelyFilingDate; // "20240706"
}

function getAppealWarning(claim) {
  const deadline = parseDate(claim.appealTimelyFilingDate);
  const daysRemaining = Math.floor((deadline - new Date()) / (1000 * 60 * 60 * 24));
  
  if (daysRemaining < 30) {
    return `⚠️ Appeal deadline in ${daysRemaining} days!`;
  }
  return claim.appealMessage;
}
```

**Step 4: Provider Clicks "Dispute Claim"**

Appeals module receives pre-populated data:
```json
{
  "appealType": "Reconsideration",
  "claimNumber": "CLM987654321",
  "patientAccountNumber": "PAT-2024-002",
  "memberId": "M789012",
  "providerNpi": "5550100001",
  "claimStatus": "Denied",
  "denialReasons": ["CO-96", "CO-197"],
  "denialComment": "Procedure code 29881 with modifier 59 is not appropriate.",
  "billedAmount": 450.00,
  "timelyFilingDeadline": "20240706",
  "serviceFromDate": "20231215",
  "serviceToDate": "20231215",
  "diagnosisCodes": ["M25.551", "S83.511A"],
  "procedureCode": "29881",
  "modifiers": ["59"]
}
```

**Step 5: Provider Submits Appeal**

Appeal submission includes:
- Dispute reason selected by provider
- Supporting narrative
- Optional attachment references
- Provider contact information

**Step 6: Appeals Workflow Processes**

The Appeals Logic App:
1. Validates appeal data
2. Checks timely filing deadline
3. Generates X12 277 appeal transaction
4. Publishes to Service Bus `appeals-in` topic
5. Updates claim tracking system
6. Sends confirmation to provider

## UI Implementation Examples

### React Component Example

```jsx
import React from 'react';

function ClaimDetailCard({ claim }) {
  const canAppeal = claim.eligibleForAppeal === true;
  const appealDeadline = new Date(
    claim.appealTimelyFilingDate.replace(
      /(\d{4})(\d{2})(\d{2})/,
      '$1-$2-$3'
    )
  );
  const daysUntilDeadline = Math.floor(
    (appealDeadline - new Date()) / (1000 * 60 * 60 * 24)
  );

  const handleDisputeClaim = () => {
    // Navigate to appeals flow with pre-populated data
    window.location.href = `/appeals/new?claimNumber=${claim.claimNumber}`;
  };

  return (
    <div className="claim-card">
      <h3>Claim {claim.claimNumber}</h3>
      <div className="claim-status denied">
        <span className="status-badge">Denied</span>
        <p>{claim.statusCodeDescription}</p>
      </div>

      <div className="financial-summary">
        <div>Billed: ${claim.BILLED.toFixed(2)}</div>
        <div>Allowed: ${claim.ALLOWED.toFixed(2)}</div>
        <div>Paid: ${claim.INSURANCE_TOTAL_PAID.toFixed(2)}</div>
      </div>

      <div className="denial-details">
        <h4>Denial Reason</h4>
        <p>{claim.comment}</p>
        <div className="denial-codes">
          {claim.reasonCodes.map(code => (
            <span key={code} className="code-badge">{code}</span>
          ))}
        </div>
      </div>

      {canAppeal && (
        <div className="appeal-section">
          <div className="appeal-message">
            {daysUntilDeadline < 30 ? (
              <div className="warning">
                ⚠️ Appeal deadline in {daysUntilDeadline} days!
              </div>
            ) : (
              <div className="info">{claim.appealMessage}</div>
            )}
          </div>
          <button 
            className="btn-primary dispute-btn"
            onClick={handleDisputeClaim}
          >
            Dispute Claim
          </button>
        </div>
      )}

      {!canAppeal && (
        <div className="appeal-unavailable">
          <p>This claim is not eligible for appeal at this time.</p>
        </div>
      )}
    </div>
  );
}
```

### Angular Component Example

```typescript
import { Component, Input } from '@angular/core';
import { Router } from '@angular/router';

interface Claim {
  claimNumber: string;
  claimStatus: string;
  eligibleForAppeal: boolean;
  appealMessage: string;
  appealTimelyFilingDate: string;
  BILLED: number;
  ALLOWED: number;
  INSURANCE_TOTAL_PAID: number;
  comment: string;
  reasonCodes: string[];
}

@Component({
  selector: 'app-claim-detail',
  template: `
    <div class="claim-card">
      <h3>Claim {{ claim.claimNumber }}</h3>
      
      <div *ngIf="canAppeal" class="appeal-section">
        <div [ngClass]="appealUrgency">
          {{ appealWarningMessage }}
        </div>
        <button 
          (click)="initiateAppeal()" 
          class="btn-dispute"
        >
          Dispute Claim
        </button>
      </div>
      
      <div *ngIf="!canAppeal" class="appeal-unavailable">
        Appeals are not available for this claim status.
      </div>
    </div>
  `
})
export class ClaimDetailComponent {
  @Input() claim!: Claim;

  get canAppeal(): boolean {
    return this.claim.eligibleForAppeal === true;
  }

  get daysUntilDeadline(): number {
    const deadline = new Date(
      this.claim.appealTimelyFilingDate.replace(
        /(\d{4})(\d{2})(\d{2})/,
        '$1-$2-$3'
      )
    );
    return Math.floor(
      (deadline.getTime() - new Date().getTime()) / (1000 * 60 * 60 * 24)
    );
  }

  get appealUrgency(): string {
    return this.daysUntilDeadline < 30 ? 'warning' : 'info';
  }

  get appealWarningMessage(): string {
    if (this.daysUntilDeadline < 30) {
      return `⚠️ Appeal deadline in ${this.daysUntilDeadline} days!`;
    }
    return this.claim.appealMessage;
  }

  constructor(private router: Router) {}

  initiateAppeal(): void {
    this.router.navigate(['/appeals/new'], {
      queryParams: { claimNumber: this.claim.claimNumber }
    });
  }
}
```

## Configuration

Appeals integration is configured via the Availity integration configuration:

```json
{
  "ecs": {
    "enabled": true,
    "valueAdds277": {
      "enabled": true,
      "integrationFlags": {
        "appeals": true
      },
      "appealConfiguration": {
        "timelyFilingDays": 180,
        "includeAppealMetadata": true,
        "defaultAppealType": "Reconsideration"
      }
    }
  },
  "appeals": {
    "enabled": true,
    "consumesEcsData": true
  }
}
```

**Configuration Location:** `config/schemas/availity-integration-config.schema.json`

### Payer-Specific Overrides

Different payers may have different timely filing requirements:

```json
{
  "payerSpecificOverrides": {
    "MEDICARE": {
      "payerId": "MEDICARE",
      "payerName": "Medicare",
      "ecsValueAdds277Override": {
        "integrationFlags": {
          "appeals": true
        },
        "appealConfiguration": {
          "timelyFilingDays": 120,
          "defaultAppealType": "Redetermination"
        }
      }
    },
    "UNITED": {
      "payerId": "UNITED",
      "payerName": "UnitedHealthcare",
      "ecsValueAdds277Override": {
        "integrationFlags": {
          "appeals": true
        },
        "appealConfiguration": {
          "timelyFilingDays": 365,
          "defaultAppealType": "Reconsideration"
        }
      }
    }
  }
}
```

## Business Logic Rules

### Timely Filing Validation

Appeals module validates timely filing before accepting appeal:

```javascript
function isTimelyFiling(claim, today = new Date()) {
  const deadline = parseDate(claim.appealTimelyFilingDate); // CCYYMMDD -> Date
  return today <= deadline;
}

function getAppealStatus(claim) {
  if (!claim.eligibleForAppeal) {
    return { canAppeal: false, reason: 'Claim status does not allow appeals' };
  }
  
  if (!isTimelyFiling(claim)) {
    return { canAppeal: false, reason: 'Timely filing deadline has passed' };
  }
  
  return { canAppeal: true, reason: null };
}
```

### Appeal Eligibility Checks

Before initiating appeal, perform these checks:

1. ✅ `eligibleForAppeal === true`
2. ✅ Current date ≤ `appealTimelyFilingDate`
3. ✅ Provider is authorized for this claim
4. ✅ Claim has not already been appealed (check tracking system)
5. ✅ Required appeal documentation is available

## Testing

### Test Scenario 1: Denied Claim Appeal Flow

**Given:**
- Claim status is "Denied"
- Timely filing deadline is 60 days away

**When:**
- Provider queries claim via ECS
- ECS returns `eligibleForAppeal: true`

**Then:**
- UI displays "Dispute Claim" button
- Clicking button navigates to appeals flow
- Appeal form pre-populated with claim data

### Test Scenario 2: Partially Paid Claim Appeal

**Given:**
- Claim status is "Partially Paid"
- Provider disputes underpaid amount

**When:**
- Provider queries claim via ECS
- ECS returns `eligibleForAppeal: true`

**Then:**
- UI shows appeal option for underpayment
- Appeal type is "Reconsideration"
- Provider can submit dispute with explanation

### Test Scenario 3: Paid Claim (No Appeal)

**Given:**
- Claim status is "Paid"
- Claim paid in full

**When:**
- Provider queries claim via ECS
- ECS returns `eligibleForAppeal: false`

**Then:**
- UI does NOT show "Dispute Claim" button
- Appeal section indicates appeal not available
- Provider sees complete payment details

### Test Scenario 4: Expired Timely Filing

**Given:**
- Claim status is "Denied"
- Timely filing deadline is in the past

**When:**
- Provider attempts to initiate appeal

**Then:**
- Appeals module rejects appeal
- Error message: "Timely filing deadline has passed"
- UI suggests contacting payer for late appeal consideration

## Error Handling

### Common Error Scenarios

1. **Missing appealTimelyFilingDate**
   - Fallback: Calculate deadline using default (180 days)
   - Log warning in Application Insights

2. **Invalid appealType**
   - Fallback: Use "Reconsideration"
   - Log warning

3. **ECS eligibleForAppeal but Appeals module disabled**
   - Show user-friendly message
   - Suggest contacting support

4. **Network error during appeal submission**
   - Queue appeal for retry
   - Show "Appeal Pending" status to provider

## Monitoring & Metrics

Track these metrics in Application Insights:

- **Appeal Initiation Rate**: % of denied claims that result in appeals
- **Appeals from ECS**: Count of appeals initiated via ECS integration
- **Timely Filing Compliance**: % of appeals filed before deadline
- **Appeal Success Rate**: % of appeals that result in payment
- **Average Time to Appeal**: Time from denial to appeal initiation

**Sample Kusto Query:**
```kusto
customEvents
| where name == "AppealInitiated"
| where customDimensions.source == "ECS"
| summarize count() by bin(timestamp, 1d)
| render timechart
```

## References

- [ECS-INTEGRATION.md](./ECS-INTEGRATION.md) - ECS ValueAdds277 documentation
- [ARCHITECTURE.md](../ARCHITECTURE.md) - System architecture
- Appeals Logic App workflow: `logicapps/workflows/process_appeals/workflow.json`
- ECS Logic App workflow: `logicapps/workflows/ecs_summary_search/workflow.json`
- Configuration schema: `config/schemas/availity-integration-config.schema.json`
