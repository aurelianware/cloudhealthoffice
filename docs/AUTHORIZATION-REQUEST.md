# Authorization Request (X12 278 X217) - Submit New Authorizations

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Transaction Types](#transaction-types)
4. [Workflows](#workflows)
5. [Request Schemas](#request-schemas)
6. [Response Handling](#response-handling)
7. [Revision & Cancellation](#revision--cancellation)
8. [Integration Points](#integration-points)
9. [Configuration](#configuration)
10. [Testing](#testing)
11. [Troubleshooting](#troubleshooting)

---

## Overview

The Authorization Request module implements X12 278 Healthcare Services Review - Request for Review and Response (Implementation Guide version 005010X217). This enables providers to submit new authorization requests and receive real-time approval/denial/pended responses from payers.

### Key Features

- **Three Transaction Types**: Inpatient (UM01=AR), Outpatient (UM01=HS), Referral (UM01=SC)
- **Revision & Cancellation**: Modify or cancel existing authorizations (UM02=S, UM02=3)
- **Eligibility Integration**: Required pre-check via 270/271 transaction
- **Attachment Support**: Trigger 275 attachment workflow during or after submission
- **Real-time Responses**: Parse X12 278 responses for all status codes
- **Comprehensive Validation**: JSON schema validation for all request types

### Workflow Triggers

All authorization request workflows use HTTP POST triggers, making them accessible via REST API endpoints. This design allows:
- Integration with external systems (EHRs, practice management systems)
- Direct API calls from web/mobile applications
- Queue-based processing via Service Bus (if needed)

---

## Architecture

### High-Level Flow

```
┌─────────────┐
│   Client    │ (EHR, Portal, Mobile App)
│  HTTP POST  │
└──────┬──────┘
       │
       ▼
┌─────────────────────────────────────────────────────────┐
│           Authorization Request Workflow                │
│                                                          │
│  1. Validate Request Schema                             │
│  2. Check Eligibility (270/271) ◄───────────────┐       │
│  3. Transform JSON → X12 278 X217                │       │
│  4. Encode via Integration Account               │       │
│  5. Archive to Data Lake                         │       │
│  6. POST to Payer Endpoint ──────────────────────┼────┐  │
│  7. Parse X12 278 Response ◄─────────────────────┘    │  │
│  8. Map Status (A1, A4, A6, A2)                       │  │
│  9. Archive Response                                   │  │
│  10. Trigger Attachments (if required)                │  │
│  11. Log to Application Insights                      │  │
│  12. Return JSON Response                             │  │
└───────────────────────────────────────────────────────┘  │
                                                            │
                                 ┌──────────────────────────┘
                                 ▼
                          ┌─────────────┐
                          │    Payer    │
                          │  Endpoint   │
                          └─────────────┘
```

### Data Flow

1. **Request Validation**: JSON schema validation against transaction type
2. **Eligibility Check**: Call 270/271 API to verify member is active
3. **X12 Transformation**: Build X12 278 transaction with all required segments
4. **Encoding**: Use Integration Account to encode to X12 EDI format
5. **Archival**: Store request in Data Lake with date partitioning
6. **Payer Submission**: POST X12 content to payer endpoint
7. **Response Processing**: Decode X12 278 response and extract status
8. **Status Mapping**: Convert X12 STC codes to human-readable statuses
9. **Response Archival**: Store response in Data Lake
10. **Attachment Triggering**: Publish to Service Bus if attachments needed
11. **Telemetry**: Log all events to Application Insights
12. **Return**: Send JSON response to caller

---

## Transaction Types

### 1. Inpatient Authorization (UM01=AR)

**Use Case**: Authorization for hospital admission and inpatient services

**Workflow**: `logicapps/workflows/auth_request_inpatient/workflow.json`

**Key Characteristics**:
- **UM Code**: AR (Admission Review)
- **Required Fields**: Admission date, facility, diagnosis codes (at least 1)
- **Date Format**: Single admission date (DTP*AAH*D8), optional discharge date
- **Quantity Type**: Days (DY)
- **Admission Details**: Admission type (CL101), admission source (CL102)
- **Procedures**: Situational (not always required)

**Example Request**:
```json
{
  "patient": {
    "memberId": "ABC123456789",
    "firstName": "John",
    "lastName": "Doe",
    "dateOfBirth": "1985-06-15",
    "gender": "M",
    "relationship": "18"
  },
  "requestingProvider": {
    "npi": "1234567890",
    "firstName": "Jane",
    "lastName": "Smith",
    "organizationName": "City Hospital"
  },
  "facility": {
    "npi": "9876543210",
    "name": "City Hospital",
    "address": {
      "street1": "123 Main St",
      "city": "Springfield",
      "state": "IL",
      "zipCode": "62701"
    }
  },
  "admission": {
    "admissionDate": "2024-12-01",
    "dischargeDate": "2024-12-05",
    "admissionType": "3",
    "admissionSource": "1"
  },
  "diagnosis": [
    {
      "code": "I50.9",
      "codeType": "ABK"
    }
  ],
  "service": {
    "serviceType": "48",
    "placeOfService": "21",
    "quantity": 5,
    "quantityType": "DY"
  }
}
```

### 2. Outpatient Authorization (UM01=HS)

**Use Case**: Authorization for outpatient procedures, surgeries, imaging, therapy

**Workflow**: `logicapps/workflows/auth_request_outpatient/workflow.json`

**Key Characteristics**:
- **UM Code**: HS (Healthcare Services Review)
- **Required Fields**: Service date range (from/to), procedures often required
- **Date Format**: Date range (DTP*AAH*RD8)
- **Quantity Types**: Days, Hours, Months, Units, Visits
- **Procedures**: Often required (CPT, HCPCS, revenue codes)

**Example Request**:
```json
{
  "patient": {
    "memberId": "XYZ987654321",
    "firstName": "Mary",
    "lastName": "Johnson",
    "dateOfBirth": "1990-03-22",
    "gender": "F"
  },
  "requestingProvider": {
    "npi": "1122334455",
    "lastName": "Wilson",
    "firstName": "Robert"
  },
  "serviceDateRange": {
    "fromDate": "2024-12-15",
    "toDate": "2024-12-15"
  },
  "diagnosis": [
    {
      "code": "M17.11",
      "codeType": "ABK"
    }
  ],
  "service": {
    "serviceType": "2",
    "placeOfService": "24",
    "quantity": 1,
    "quantityType": "UN"
  },
  "procedures": [
    {
      "code": "27447",
      "codeType": "HC"
    }
  ]
}
```

### 3. Referral Request (UM01=SC)

**Use Case**: Referral to specialist or facility for specialty care

**Workflow**: `logicapps/workflows/auth_request_referral/workflow.json`

**Key Characteristics**:
- **UM Code**: SC (Specialty Care Review)
- **Required Fields**: Referred-to provider with NPI, specialty/taxonomy
- **Provider Loop**: 2000EA with NM101=SJ for referred-to provider
- **Service Dates**: Optional (date range situational)
- **Procedures**: Typically not required
- **Service Type**: Situational (UM03 not always required)

**Example Request**:
```json
{
  "patient": {
    "memberId": "DEF555666777",
    "firstName": "James",
    "lastName": "Brown",
    "dateOfBirth": "1975-11-30",
    "gender": "M"
  },
  "requestingProvider": {
    "npi": "2233445566",
    "lastName": "Davis",
    "firstName": "Sarah"
  },
  "referredToProvider": {
    "npi": "9988776655",
    "firstName": "Michael",
    "lastName": "Chen",
    "specialty": "Cardiology",
    "taxonomy": "207RC0000X"
  },
  "diagnosis": [
    {
      "code": "I25.10",
      "codeType": "ABK"
    }
  ],
  "service": {
    "serviceType": "3"
  }
}
```

---

## Workflows

### Common Workflow Structure

All authorization request workflows follow a consistent pattern:

1. **Try Scope**: Main processing logic
   - Schema validation
   - Eligibility check
   - X12 transformation and encoding
   - Payer submission
   - Response parsing
   - Attachment triggering
   - Telemetry logging

2. **Catch Scope**: Error handling
   - Log errors to Application Insights
   - Return error response to caller

### HTTP Trigger Configuration

**Trigger Type**: Request (HTTP)  
**Method**: POST  
**Authentication**: Configure post-deployment (Azure AD, API Key, or Anonymous)

**Response Codes**:
- `200 OK`: Authorization processed successfully
- `400 Bad Request`: Invalid request or member not eligible
- `500 Internal Server Error`: Processing error

### Parameters

All workflows share common parameters:

```json
{
  "blob_storage_account": "default",
  "blob_auth_requests_folder": "hipaa-attachments/raw/auth-requests",
  "sb_namespace": "hipaa-logic-svc",
  "sb_topic_attachments": "attachments-in",
  "eligibility_api_url": "https://eligibility.api.local",
  "payer_auth_endpoint_url": "https://payer-prod.example.com/auth",
  "API_TOKEN": "<secure-string>",
  "x12_sender_id": "66917",
  "x12_receiver_id": "030240928",
  "x12_278_x217_messagetype": "X12_005010X217_278"
}
```

### Data Lake Archival

Authorization requests and responses are archived with date-based partitioning:

```
hipaa-attachments/raw/auth-requests/
├── inpatient/
│   └── 2024/
│       └── 11/
│           └── 19/
│               ├── 278-inpatient-request-{guid}.edi
│               └── 278-inpatient-response-{authNum}.edi
├── outpatient/
│   └── 2024/11/19/
├── referral/
│   └── 2024/11/19/
├── revisions/
│   └── 2024/11/19/
└── cancellations/
    └── 2024/11/19/
```

---

## Request Schemas

Comprehensive JSON schemas validate all authorization request types:

### Schema Files

- **`schemas/Auth-Request-Inpatient.json`**: Inpatient authorization validation
- **`schemas/Auth-Request-Outpatient.json`**: Outpatient authorization validation
- **`schemas/Auth-Request-Referral.json`**: Referral authorization validation
- **`schemas/Auth-Request-Revision.json`**: Revision request validation
- **`schemas/Auth-Request-Cancellation.json`**: Cancellation request validation

### Common Validation Rules

**Member ID**: 1-80 alphanumeric characters  
**NPI**: Exactly 10 digits  
**Date Format**: YYYY-MM-DD (converted to YYYYMMDD in X12)  
**Gender**: M, F, or U  
**Diagnosis Codes**: ICD-10-CM format (3-7 characters)  
**Procedure Codes**: CPT, HCPCS, ICD-10-PCS, or revenue codes

### Field Requirements by Transaction Type

| Field | Inpatient | Outpatient | Referral |
|-------|-----------|------------|----------|
| Member ID | Required | Required | Required |
| DOB | Required | Required | Required |
| Gender | Required | Required | Required |
| Requesting Provider NPI | Required | Required | Required |
| Referred-To Provider | Optional | Optional | **Required** |
| Admission Date | **Required** | N/A | N/A |
| Service Date Range | Optional | **Required** | Optional |
| Diagnosis (min 1) | **Required** | **Required** | **Required** |
| Service Type | Required | Required | Optional |
| Place of Service | Required | Required | Optional |
| Procedures | Situational | Often Required | Rarely Required |

---

## Response Handling

### X12 278 Response Structure

Authorization responses use the X12 278 transaction with these key segments:

- **ST**: Transaction Set Header (ST01=278)
- **BHT**: Beginning of Hierarchical Transaction
- **HL**: Hierarchical Levels (Information Source, Information Receiver, Service Provider)
- **STC**: Status Information (certification type code)
- **REF**: Reference Information (authorization number)
- **DTP**: Date/Time Period (effective/expiration dates)
- **HSD**: Health Care Services Delivery (authorized quantity)

### Certification Type Codes (STC Segment)

| Code | Description | Mapped Status |
|------|-------------|---------------|
| A1 | Certified in Total | **APPROVED** |
| A2 | Certified Partial | **MODIFIED** |
| A3 | Denial | **DENIED** |
| A4 | Pended | **PENDED** |
| A6 | Modified | **DENIED** (partial approval treated as denial in some configs) |
| A7 | Cancelled | **CANCELLED** |
| CT | Conditional | **PENDED** |
| NA | Not Applicable | **ERROR** |

### Response JSON Schema

**Schema**: `schemas/Auth-Response.json`

**Example Response**:
```json
{
  "authorizationNumber": "AUTH20241119001",
  "status": "APPROVED",
  "certificationTypeCode": "A1",
  "effectiveDate": "2024-12-01",
  "expirationDate": "2024-12-05",
  "serviceType": "48",
  "authorizedQuantity": 5,
  "quantityType": "DY",
  "patient": {
    "memberId": "ABC123456789",
    "firstName": "John",
    "lastName": "Doe"
  },
  "provider": {
    "npi": "1234567890",
    "lastName": "Smith",
    "firstName": "Jane"
  },
  "attachmentRequired": false
}
```

### Error Responses

**AAA Segment Errors**: X12 transactions may include AAA (Request Validation) segments for errors:

```json
{
  "status": "ERROR",
  "errors": [
    {
      "code": "41",
      "description": "Authorization Information Missing",
      "followUpAction": "Resubmit with Required Information"
    }
  ]
}
```

---

## Revision & Cancellation

### Authorization Revision (UM02=S)

**Workflow**: `logicapps/workflows/auth_revision/workflow.json`

**Purpose**: Modify existing authorization (dates, quantities, diagnoses, procedures)

**Requirements**:
- Payer must support revisions: `revisionsSupported=true`
- Authorization must be in allowed status (default: PENDED only)
- Original authorization number required

**Example Request**:
```json
{
  "authorizationNumber": "AUTH20241119001",
  "patient": {
    "memberId": "ABC123456789"
  },
  "revisionType": "date_change",
  "modifiedFields": {
    "serviceDateRange": {
      "fromDate": "2024-12-02",
      "toDate": "2024-12-06"
    }
  },
  "revisionReason": "Patient requested later admission date"
}
```

### Authorization Cancellation (UM02=3)

**Workflow**: `logicapps/workflows/auth_cancellation/workflow.json`

**Purpose**: Cancel existing authorization

**Requirements**:
- Payer must support cancellations: `cancellationsSupported=true`
- Authorization must be in allowed status (default: PENDED or APPROVED)
- Cancellation reason required

**Example Request**:
```json
{
  "authorizationNumber": "AUTH20241119001",
  "patient": {
    "memberId": "ABC123456789"
  },
  "cancellationReason": "PROCEDURE_NOT_PERFORMED"
}
```

**Cancellation Reasons**:
- `PATIENT_REQUEST`: Patient declined or cancelled
- `PROCEDURE_NOT_PERFORMED`: Service not rendered
- `DUPLICATE_REQUEST`: Duplicate authorization exists
- `INCORRECT_INFORMATION`: Request contained errors
- `COVERAGE_TERMINATED`: Member coverage ended
- `PROVIDER_ERROR`: Provider initiated cancellation
- `OTHER`: Other reason

---

## Integration Points

### 1. Eligibility Check (270/271)

**Required**: Yes (per X12 278 X217 QRE guidelines)

**Purpose**: Verify member is active and eligible before submitting authorization

**API Endpoint**: Configured via `eligibility_api_url` parameter

**Request**:
```json
{
  "memberId": "ABC123456789",
  "dateOfBirth": "1985-06-15",
  "serviceDate": "2024-12-01",
  "requestedAt": "2024-11-19T10:30:00Z"
}
```

**Response**:
```json
{
  "eligible": true,
  "coverageLevel": "Active Coverage",
  "effectiveDate": "2024-01-01",
  "terminationDate": null
}
```

**Error Handling**: If member is not eligible, workflow returns 400 Bad Request with eligibility details

### 2. Attachment Workflow (275)

**Trigger**: Service Bus topic `attachments-in`

**Condition**: `attachmentRequired: true` in request OR payer response indicates attachments needed

**Message Format**:
```json
{
  "authorizationNumber": "AUTH20241119001",
  "memberId": "ABC123456789",
  "requestType": "inpatient",
  "attachmentTrigger": "duringSubmission"
}
```

**Timing Options**:
- **During Submission**: Attachments sent with authorization request
- **Post Submission**: Attachments sent after authorization approved/pended

### 3. Authorization Inquiry (X215)

**Purpose**: Check for duplicate authorizations before submission

**Recommended**: Call inquiry endpoint before creating new authorization request

**Integration**: Via separate workflow (not part of this module)

### 4. Application Insights Telemetry

**Events Logged**:
- `Inpatient_Authorization_Request_Processed`
- `Outpatient_Authorization_Request_Processed`
- `Referral_Authorization_Request_Processed`
- `Authorization_Revision_Processed`
- `Authorization_Cancellation_Processed`
- `Authorization_Request_Error`

**Custom Properties**:
- Authorization number
- Member ID
- Request type
- Status
- Certification type code

---

## Configuration

### Configuration Schema

**File**: `core/schemas/availity-integration-config.schema.json`

**Complete Example**:
```json
{
  "payer": {
    "id": "030240928",
    "name": "Availity",
    "tradingPartnerId": "AVLTY"
  },
  "authorizations": {
    "transactionTypes": {
      "inpatientAuthorization": {
        "enabled": true,
        "umCode": "AR"
      },
      "outpatientAuthorization": {
        "enabled": true,
        "umCode": "HS"
      },
      "referrals": {
        "enabled": true,
        "umCode": "SC"
      }
    },
    "request_x217": {
      "enabled": true,
      "tr3Version": "005010X217",
      "connectivity": {
        "testUrl": "https://payer-test.example.com/auth",
        "prodUrl": "https://payer-prod.example.com/auth",
        "timeout": 30,
        "xmlWrapperRequired": false
      },
      "fieldRequirements": {
        "procedureCodesRequired": {
          "inpatient": "situational",
          "outpatient": "always",
          "referral": "never"
        },
        "serviceDateRangeRestrictions": {
          "outpatient": {
            "earliest": "0",
            "latest": "+365"
          },
          "inpatient": {
            "admissionEarliest": "0",
            "admissionLatest": "+90"
          }
        },
        "diagnosisCodeRequirements": {
          "minimumRequired": 1,
          "maximumAllowed": 12
        }
      }
    },
    "features": {
      "revisionsSupported": true,
      "cancellationsSupported": true,
      "revisionCancellationAllowedStatuses": ["PENDED"],
      "attachments": {
        "duringSubmission": true,
        "postSubmission": false
      },
      "realTimeResponse": true,
      "batchSubmission": false
    },
    "statusMapping": {
      "A1": "APPROVED",
      "A2": "MODIFIED",
      "A3": "DENIED",
      "A4": "PENDED",
      "A6": "DENIED",
      "A7": "CANCELLED"
    }
  },
  "eligibility": {
    "enabled": true,
    "tr3Version": "005010X279",
    "requirementForAuth": "required"
  },
  "x12": {
    "senderId": "66917",
    "receiverId": "030240928",
    "interchangeUsageIndicator": "P"
  }
}
```

### Environment-Specific Configuration

**DEV Environment**:
- Use test endpoints
- Set `interchangeUsageIndicator: "T"`
- Lower timeout values for faster feedback

**PROD Environment**:
- Use production endpoints
- Set `interchangeUsageIndicator: "P"`
- Standard timeout values (30 seconds)

---

## Testing

### Unit Testing

**Test File**: `scripts/tests/auth-request.test.ts` (to be created)

**Test Coverage**:
- Request schema validation for all transaction types
- Eligibility integration (mock API)
- X12 transformation correctness
- Response parsing for all certification codes
- Revision/cancellation logic
- Payer configuration variations
- Error handling

### Integration Testing

**Test Scenarios**:

1. **Inpatient Authorization - Approved**
   - Submit valid inpatient request
   - Verify eligibility check called
   - Verify X12 278 generated correctly
   - Verify response parsed as APPROVED
   - Verify authorization number returned

2. **Outpatient Authorization - Pended**
   - Submit outpatient request with attachment flag
   - Verify pended status
   - Verify attachment workflow triggered

3. **Referral Authorization - Denied**
   - Submit referral with invalid diagnosis
   - Verify denial status
   - Verify denial reason extracted

4. **Revision - Status Check**
   - Submit revision for APPROVED authorization
   - Verify rejection (revisions only allowed for PENDED)

5. **Cancellation - Success**
   - Submit cancellation for PENDED authorization
   - Verify cancellation confirmed

### Manual Testing

**Postman Collection**: Create collection with sample requests for each workflow

**Example Inpatient Request**:
```bash
POST https://hipaa-logic-la.azurewebsites.net/api/auth_request_inpatient/triggers/HTTP_Inpatient_Authorization_Request/invoke
Content-Type: application/json

{
  "patient": {
    "memberId": "TEST123456",
    "firstName": "Test",
    "lastName": "Patient",
    "dateOfBirth": "1980-01-01",
    "gender": "M"
  },
  "requestingProvider": {
    "npi": "1234567890"
  },
  "facility": {
    "npi": "9876543210",
    "name": "Test Hospital"
  },
  "admission": {
    "admissionDate": "2024-12-01"
  },
  "diagnosis": [
    {"code": "I50.9"}
  ],
  "service": {
    "serviceType": "48",
    "quantity": 3,
    "quantityType": "DY"
  }
}
```

---

## Troubleshooting

### Common Issues

#### 1. Eligibility Check Fails

**Symptom**: 400 error with message "Member not eligible"

**Causes**:
- Member ID incorrect
- Date of birth mismatch
- Coverage terminated
- Service date outside coverage period

**Resolution**:
- Verify member ID format
- Check coverage dates
- Ensure service date is within active coverage

#### 2. X12 Encoding Fails

**Symptom**: Error in "Encode_X12_278_Request" action

**Causes**:
- Integration Account not configured
- Missing X12 schema (005010X217_278)
- Invalid trading partner agreement
- Sender/receiver ID mismatch

**Resolution**:
- Upload X12 schema to Integration Account
- Configure trading partner agreement
- Verify ISA/GS sender/receiver IDs

#### 3. Payer Endpoint Timeout

**Symptom**: Request times out after 30 seconds

**Causes**:
- Payer endpoint slow or unavailable
- Network connectivity issues
- Timeout set too low

**Resolution**:
- Increase timeout in workflow parameters
- Check payer endpoint status
- Verify network/firewall rules

#### 4. Response Parsing Error

**Symptom**: Error in "Parse_X12_278_Response" action

**Causes**:
- Response not valid X12 format
- Unexpected response structure
- Missing required segments

**Resolution**:
- Check response content in archived file
- Verify payer returns 278 response (not 999/997)
- Review payer documentation for response format

#### 5. Revision/Cancellation Not Allowed

**Symptom**: 400 error "Revisions not allowed for authorization in current status"

**Causes**:
- Authorization in non-allowed status
- Payer configuration incorrect
- Authorization already cancelled

**Resolution**:
- Check authorization current status
- Verify `revisionCancellationAllowedStatuses` configuration
- Confirm payer supports revisions/cancellations

### Diagnostic Queries

**Application Insights - Failed Requests**:
```kusto
traces
| where timestamp > ago(24h)
| where customDimensions.eventName contains "Authorization"
| where severityLevel >= 3
| project timestamp, message, customDimensions
| order by timestamp desc
```

**Data Lake - Find Archived Requests**:
```bash
# List inpatient requests from today
az storage blob list \
  --account-name hipaa7v2rrsoo6tac2 \
  --container-name hipaa-attachments \
  --prefix "raw/auth-requests/inpatient/2024/11/19/" \
  --output table
```

**Service Bus - Check Attachment Messages**:
```bash
# Peek messages on attachments topic
az servicebus topic show \
  --namespace-name hipaa-logic-svc \
  --name attachments-in \
  --query messageCount
```

---

## Best Practices

### 1. Request Validation

- Always validate requests against JSON schemas before submission
- Use schema validation libraries in client applications
- Return clear validation errors to end users

### 2. Eligibility Checks

- Cache eligibility responses (1-hour TTL recommended)
- Implement retry logic for eligibility API failures
- Don't block authorization submission on eligibility check failure in emergencies

### 3. Error Handling

- Log all errors to Application Insights with correlation IDs
- Implement exponential backoff for payer endpoint retries
- Provide actionable error messages to end users

### 4. Performance

- Use asynchronous patterns for long-running authorization reviews
- Implement webhooks or polling for pended authorizations
- Archive requests/responses asynchronously

### 5. Security

- Never log PHI (member ID, DOB) in plain text
- Use managed identity for Azure resource access
- Rotate API tokens regularly
- Use HTTPS for all payer communications

### 6. Monitoring

- Set up alerts for authorization failures >5%
- Monitor payer endpoint response times
- Track authorization approval rates by payer
- Monitor Data Lake storage growth

---

## References

### X12 Standards

- **X12 278 Healthcare Services Review (005010X217)**: Authorization Request/Response TR3
- **X12 270/271 Eligibility (005010X279)**: Eligibility Check TR3
- **X12 275 Attachment (005010X215)**: Additional Information to Support a Health Care Services Review TR3

### Availity Documentation

- **Availity X12 278 X217 QRE (Quality Review and Evaluation)**: Payer-specific requirements for authorization submissions
- **Availity Developer Portal**: API documentation and testing tools

### Azure Resources

- **Logic Apps Standard**: Workflow runtime documentation
- **Integration Account**: X12 EDI processing
- **Data Lake Storage Gen2**: HIPAA-compliant file storage
- **Service Bus**: Message queuing for asynchronous workflows

---

**Document Version**: 1.0  
**Last Updated**: 2024-11-19  
**Author**: HIPAA Attachments Team
