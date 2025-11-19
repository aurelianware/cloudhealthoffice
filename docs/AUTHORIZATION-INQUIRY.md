# Authorization Inquiry (X12 278 X215) - Query Existing Authorizations

## Table of Contents

1. [Overview](#overview)
2. [X215 vs X217: Understanding the Difference](#x215-vs-x217-understanding-the-difference)
3. [When to Use Authorization Inquiry](#when-to-use-authorization-inquiry)
4. [Query Patterns](#query-patterns)
5. [Request Field Requirements](#request-field-requirements)
6. [Response Structure and Status Codes](#response-structure-and-status-codes)
7. [Error Handling](#error-handling)
8. [Configuration Guide](#configuration-guide)
9. [Integration Examples](#integration-examples)
10. [Testing Scenarios](#testing-scenarios)
11. [Performance and Optimization](#performance-and-optimization)
12. [Security Considerations](#security-considerations)
13. [Troubleshooting](#troubleshooting)
14. [API Reference](#api-reference)
15. [Appendix](#appendix)

---

## Overview

The Authorization Inquiry workflow implements the X12 278 X215 Healthcare Services Review - Inquiry and Response transaction set. This transaction type is specifically designed for querying the status of previously submitted authorization requests, without submitting new authorization requests.

### Key Features

- **Lightweight Queries**: Minimal data requirements following Availity QRE best practices
- **Dual Query Methods**: Query by authorization number OR member demographics
- **Real-time Status**: Immediate authorization status from payer systems
- **Standard Timeout**: 30-second response time per Availity standards
- **Automatic Retry**: 3 retry attempts with 30-second intervals
- **Comprehensive Logging**: Application Insights integration for monitoring

### Use Cases

1. **Status Verification**: Check current status of pending authorizations
2. **Pre-claim Submission**: Verify authorization before submitting claims
3. **Member Service**: Respond to patient inquiries about authorization status
4. **Appeals Preparation**: Gather authorization details for appeals process
5. **Automated Workflows**: Integrate into downstream processing systems

### Supported Payers

The Authorization Inquiry workflow is compatible with any payer that supports X12 278 X215 transactions via Availity. Configuration is per-payer and includes:

- Endpoint URL (test/production)
- Authentication credentials
- X12 envelope parameters (ISA/GS identifiers)
- Field requirement overrides

---

## X215 vs X217: Understanding the Difference

### X12 278 X215 - Inquiry and Response

**Purpose**: Query existing authorizations  
**Transaction Type**: Inquiry only  
**Data Requirements**: Minimal (QRE best practice)  
**Response Time**: 30 seconds (synchronous)  
**UM01 Code**: I (Inquiry)

**Typical Use**:
- "What is the status of authorization AUTH12345?"
- "Does member MEM123456 have an active authorization?"
- "What services are authorized under this auth number?"

### X12 278 X217 - Request and Response

**Purpose**: Submit new authorization requests  
**Transaction Type**: Request for authorization  
**Data Requirements**: Comprehensive (clinical data, diagnosis, procedure details)  
**Response Time**: Varies (may be asynchronous/pended)  
**UM01 Code**: AR (Admission Review), HS (Health Services Review), SC (Specialty Care Review)

**Typical Use**:
- "Please authorize 30 days of skilled nursing care"
- "Request authorization for MRI scan"
- "Submit authorization for specialist referral"

### Decision Matrix

| Scenario | Use X215 (Inquiry) | Use X217 (Request) |
|----------|-------------------|-------------------|
| Check status of existing auth | ✅ | ❌ |
| Verify auth before claim | ✅ | ❌ |
| Submit new auth request | ❌ | ✅ |
| Modify existing auth | ❌ | ✅ |
| Query member's active auths | ✅ | ❌ |
| Appeal denied auth | ❌ | ✅ (new request) |

---

## When to Use Authorization Inquiry

### Appropriate Scenarios

#### 1. Pre-Claim Validation
Before submitting claims to the payer, verify that the authorization is:
- Still active (not expired)
- Approved (not pended or denied)
- Matches the service being claimed
- Has remaining quantity available

**Example Workflow**:
```
Claim Submission → Check Auth Status → Verify Active → Submit Claim
                                    → If Expired → Request New Auth
```

#### 2. Member Service Desk
When members call to inquire about authorization status:
- Query by member ID + DOB
- Provide real-time status
- Share approval dates and authorized quantities
- Explain next steps if pended/denied

#### 3. Automated Workflows
Integrate into authorization management systems:
- Nightly batch to check expiring authorizations
- Pre-appointment verification
- Automated status updates in EMR/EHR
- Integration with appeals workflows

#### 4. Appeals Process
Before filing an appeal:
- Confirm authorization was actually denied (not just pended)
- Retrieve denial reason codes
- Gather timeline information (submission date, decision date)
- Collect all relevant messages from payer

### Inappropriate Scenarios

❌ **Submitting New Authorization Requests**: Use X217 (Request) instead  
❌ **Modifying Existing Authorizations**: Use X217 with modification indicators  
❌ **Querying Eligibility**: Use X12 270/271 eligibility transactions  
❌ **Claims Status Inquiry**: Use X12 276/277 claim status transactions  

---

## Query Patterns

The Authorization Inquiry workflow supports two distinct query patterns, each optimized for different scenarios.

### Pattern 1: Query by Authorization Number

**Best for**: When you have the payer-assigned authorization number

**Required Fields**:
- `authorizationNumber` (REF*D9 segment in X12)
- `providerNpi` (requesting provider NPI)

**Optional Fields**:
- `serviceFromDate` / `serviceToDate` (to verify date range)
- `serviceType` (AR/HS/SC)

**Example JSON Request**:
```json
{
  "authorizationNumber": "AUTH12345",
  "providerNpi": "1234567890",
  "serviceType": "HS"
}
```

**X12 278 X215 Request Structure**:
```
ISA*00*...*ZZ*AVAILITY001*ZZ*PAYERID*...~
GS*HI*AVAILITY*PAYER*...~
ST*278*0001*005010X215~
BHT*0007*13*AUTH-INQ-001*20251119*1030~
HL*1**20*1~
NM1*PR*2*PAYER NAME*****PI*PAYERID~
HL*2*1*21*1~
NM1*1P*2*PROVIDER NAME*****XX*1234567890~
HL*3*2*22*1~
REF*D9*AUTH12345~               ← Authorization number
HL*4*3*23*0~
TRN*1*AUTH-INQ-001*9AVAILITY~
UM*HS*I~                        ← Service type + Inquiry
HCR*I1~                         ← Inquiry action code
SE*14*0001~
GE*1*1~
IEA*1*000000001~
```

**When to Use**:
- Authorization number is known (stored in your system)
- Fastest query method (precise lookup)
- Best for claim submission workflows
- Ideal for automated batch processes

---

### Pattern 2: Query by Member Demographics

**Best for**: When authorization number is unknown but member information is available

**Required Fields**:
- `memberId` (NM1*IL segment, MI qualifier)
- `patientDateOfBirth` (DMG*D8 segment)
- `providerNpi` (requesting provider NPI)

**Optional Fields**:
- `patientFirstName` / `patientLastName` (for improved matching)
- `serviceFromDate` / `serviceToDate` (to narrow results)
- `serviceType` (AR/HS/SC)

**Example JSON Request**:
```json
{
  "memberId": "MEM123456",
  "patientDateOfBirth": "1985-06-15",
  "providerNpi": "1234567890",
  "patientFirstName": "John",
  "patientLastName": "Smith",
  "serviceType": "HS"
}
```

**X12 278 X215 Request Structure**:
```
ISA*00*...*ZZ*AVAILITY001*ZZ*PAYERID*...~
GS*HI*AVAILITY*PAYER*...~
ST*278*0001*005010X215~
BHT*0007*13*MEMBER-INQ-001*20251119*1030~
HL*1**20*1~
NM1*PR*2*PAYER NAME*****PI*PAYERID~
HL*2*1*21*1~
NM1*1P*2*PROVIDER NAME*****XX*1234567890~
HL*3*2*22*1~
NM1*IL*1*SMITH*JOHN****MI*MEM123456~    ← Member demographics
DMG*D8*19850615~                         ← Date of birth
HL*4*3*23*0~
TRN*1*MEMBER-INQ-001*9AVAILITY~
UM*HS*I~
HCR*I1~
SE*13*0001~
GE*1*1~
IEA*1*000000001~
```

**When to Use**:
- Authorization number is not stored in your system
- Member service desk inquiries
- Manual queries by staff
- Initial authorization discovery

**Important Note**: Some payers may return multiple authorizations if member has multiple active auths. Your application should handle this by:
- Filtering by date range
- Filtering by service type
- Presenting all results to user
- Using most recent auth by effective date

---

## Request Field Requirements

### Required Fields (All Requests)

#### Provider NPI
- **Field**: `providerNpi`
- **X12 Location**: 2010B NM1*1P segment, NM108-109
- **Format**: 10-digit numeric string
- **Validation**: Must match valid NPI format (^\\d{10}$)
- **Example**: "1234567890"

**Why Required**: Identifies the provider requesting the authorization inquiry. Payer uses this to verify provider eligibility and authorization access.

---

### Conditional Required Fields

The request must include **either** an authorization number **or** member demographics (both member ID and date of birth).

#### Authorization Number (Option 1)
- **Field**: `authorizationNumber`
- **X12 Location**: 2000E REF*D9 segment
- **Format**: String, 1-50 characters
- **Example**: "AUTH12345", "2025-11-19-001"

**When Required**: If `memberId` and `patientDateOfBirth` are not provided

#### Member ID (Option 2A)
- **Field**: `memberId`
- **X12 Location**: 2010C NM1*IL segment, NM109 (MI qualifier)
- **Format**: String, 1-80 characters
- **Example**: "MEM123456", "PCHP789456"

**When Required**: If `authorizationNumber` is not provided (requires `patientDateOfBirth`)

#### Patient Date of Birth (Option 2B)
- **Field**: `patientDateOfBirth`
- **X12 Location**: 2010C/D DMG*D8 segment, DMG02
- **Format**: CCYY-MM-DD (JSON), CCYYMMDD (X12)
- **Example**: "1985-06-15" → X12: "19850615"

**When Required**: If `authorizationNumber` is not provided (requires `memberId`)

---

### Optional Fields

#### Service Type
- **Field**: `serviceType`
- **X12 Location**: 2000E UM01 (UM segment)
- **Format**: Enum [AR, HS, SC]
- **Default**: "HS" (Health Services Review)
- **Values**:
  - **AR**: Admission Review (Inpatient)
  - **HS**: Health Services Review (Outpatient)
  - **SC**: Specialty Care Review (Referrals)

**Usage**: Helps narrow results when querying by member demographics.

#### Service Date Range
- **Fields**: `serviceFromDate`, `serviceToDate`
- **X12 Location**: 2000E DTP*AAH segment (or DTP*472)
- **Format**: CCYY-MM-DD (JSON), CCYYMMDD (X12)
- **X12 Qualifier**: RD8 (Date Range)
- **Example**: "2025-12-01" to "2025-12-31" → X12: "20251201-20251231"

**Usage**: Filters authorizations by service date, useful for finding specific time periods.

#### Patient Name
- **Fields**: `patientFirstName`, `patientLastName`
- **X12 Location**: 2010C NM1*IL segment, NM103-NM104
- **Format**: String (first: 1-35 chars, last: 1-60 chars)
- **Example**: First: "John", Last: "Smith"

**Usage**: Improves matching accuracy when querying by member demographics.

---

### Field Validation Rules

The Logic App workflow validates requests before processing:

1. **Required Field Check**: `providerNpi` must be present
2. **Conditional Logic Check**: Must have either:
   - `authorizationNumber`, OR
   - Both `memberId` AND `patientDateOfBirth`
3. **Format Validation**:
   - NPI: Exactly 10 digits
   - Dates: CCYY-MM-DD format
   - Service Type: One of [AR, HS, SC]

**Invalid Request Example**:
```json
{
  "providerNpi": "1234567890",
  "memberId": "MEM123456"
  // Missing: patientDateOfBirth
}
```
**Error Response**:
```json
{
  "status": "error",
  "errors": [{
    "errorCode": "INVALID_SCHEMA",
    "errorMessage": "Request must include either authorizationNumber OR (memberId + patientDateOfBirth)"
  }]
}
```

---

## Response Structure and Status Codes

### Success Response Structure

```json
{
  "authorizationNumber": "AUTH12345",
  "status": "APPROVED",
  "effectiveDate": "2025-12-01",
  "expirationDate": "2025-12-31",
  "serviceType": "HS",
  "authorizedQuantity": 30,
  "quantityType": "DAYS",
  "decision": "Approved",
  "decisionReason": "Medical necessity verified",
  "messages": [
    "AUTH APPROVED FOR OUTPATIENT SERVICES",
    "Please submit claims with this authorization number"
  ],
  "patient": {
    "memberId": "MEM123456",
    "firstName": "John",
    "lastName": "Smith",
    "dateOfBirth": "1985-06-15"
  },
  "provider": {
    "npi": "1234567890",
    "name": "PROVIDER NAME"
  },
  "services": [
    {
      "procedureCode": "76700",
      "quantity": 30,
      "fromDate": "2025-12-01",
      "toDate": "2025-12-31"
    }
  ],
  "reviewOutcome": "A1",
  "requestId": "AUTH-INQ-001",
  "responseTimestamp": "2025-11-19T10:30:00Z"
}
```

### Authorization Status Codes

| Status | Description | Next Steps |
|--------|-------------|------------|
| **APPROVED** | Authorization approved and active | Proceed with service delivery, submit claims |
| **PENDED** | Under review, decision pending | Wait for payer decision, check again in 24-48 hours |
| **DENIED** | Authorization denied | Review denial reason, consider appeal, submit new request |
| **MODIFIED** | Approved with modifications | Review modifications (quantity, dates, services), adjust plan |

### HCR Review Decision Codes (X12 278)

The `reviewOutcome` field contains the HCR01 review decision code from X12 278:

| Code | Meaning | Status Mapping |
|------|---------|----------------|
| **A1** | Certified in total | APPROVED |
| **A2** | Certified in part | MODIFIED |
| **A3** | Denied | DENIED |
| **A4** | Pended | PENDED |
| **A6** | Modified | MODIFIED |
| **I1** | Inquiry | (Used in request only) |

### Quantity Type Codes

The `quantityType` field indicates the unit of measure:

| Code | Description | Example |
|------|-------------|---------|
| **DAYS** | Calendar days | 30 days of home health |
| **VISITS** | Number of visits | 12 physical therapy visits |
| **UNITS** | Service units | 20 units of DME |
| **PROCEDURES** | Number of procedures | 1 surgical procedure |
| **HOURS** | Hours of service | 40 hours of nursing care |

---

## Error Handling

### Error Response Structure

```json
{
  "status": "error",
  "timestamp": "2025-11-19T10:30:00Z",
  "providerNpi": "1234567890",
  "errors": [
    {
      "errorCode": "AUTH_NOT_FOUND",
      "errorMessage": "Authorization number not found in payer system",
      "errorDetails": "AUTH99999"
    }
  ]
}
```

### Common Error Codes

#### AUTH_NOT_FOUND
**Cause**: Authorization number does not exist in payer system  
**X12 HCR Code**: A3 (Denied)  
**Resolution**: 
- Verify authorization number is correct
- Check if authorization was submitted to different payer
- Confirm member eligibility at time of service

#### MEMBER_NOT_FOUND
**Cause**: Member ID not found or invalid  
**Resolution**:
- Verify member ID format and qualifier
- Check member eligibility
- Confirm DOB matches member record

#### INVALID_PROVIDER
**Cause**: Provider NPI not authorized for inquiries  
**Resolution**:
- Verify provider is in-network with payer
- Check provider enrollment status
- Confirm NPI is active and valid

#### TIMEOUT
**Cause**: Payer system did not respond within 30 seconds  
**Resolution**:
- Request is automatically retried (3 attempts)
- If all retries fail, try again later
- Check payer system status page

#### INVALID_SCHEMA
**Cause**: Request validation failed  
**Resolution**:
- Review required field validation
- Ensure conditional logic is met (auth number OR member ID + DOB)
- Check field formats (NPI, dates)

### Retry Logic

The workflow implements automatic retry for transient errors:

**Configuration**:
- **Max Retries**: 3
- **Retry Interval**: 30 seconds (PT30S)
- **Retry Policy**: Fixed interval

**Retriable Errors**:
- HTTP 500 (Internal Server Error)
- HTTP 502 (Bad Gateway)
- HTTP 503 (Service Unavailable)
- HTTP 504 (Gateway Timeout)
- Connection timeout

**Non-Retriable Errors**:
- HTTP 400 (Bad Request) - validation error
- HTTP 401 (Unauthorized) - authentication failure
- HTTP 404 (Not Found) - authorization not found
- HTTP 403 (Forbidden) - provider not authorized

---

## Configuration Guide

### Configuration Schema

Authorization Inquiry is configured via `core/schemas/availity-integration-config.schema.json`:

```json
{
  "authorizations": {
    "inquiry_x215": {
      "enabled": true,
      "tr3Version": "005010X215",
      "connectivity": {
        "testUrl": "https://payer-test.example.com/auth-inquiry",
        "prodUrl": "https://payer-prod.example.com/auth-inquiry",
        "testUserId": "",
        "prodUserId": "",
        "timeout": 30,
        "xmlWrapperRequired": false
      },
      "fieldRequirements": {
        "authNumberRequired": false,
        "memberIdRequired": false,
        "patientDobRequired": false,
        "serviceDatesRequired": false
      },
      "enveloping": {
        "isa06_senderId": "AVAILITY001",
        "isa08_receiverId": "PAYERID",
        "gs02_applicationSenderCode": "AVAILITY",
        "gs03_applicationReceiverCode": "PAYER"
      },
      "retry": {
        "enabled": true,
        "maxRetries": 3,
        "retryInterval": 30
      }
    }
  }
}
```

### Per-Payer Configuration

Each payer may have different requirements. Override settings as needed:

#### Example: Payer A (Standard Availity)
```json
{
  "payerId": "PAYERA",
  "connectivity": {
    "testUrl": "https://payera-test.availity.com/auth-inquiry",
    "prodUrl": "https://payera.availity.com/auth-inquiry",
    "timeout": 30
  },
  "enveloping": {
    "isa08_receiverId": "PAYERA",
    "gs03_applicationReceiverCode": "PAYERA"
  }
}
```

#### Example: Payer B (Requires XML Wrapper)
```json
{
  "payerId": "PAYERB",
  "connectivity": {
    "testUrl": "https://payerb-gateway.example.com/x12",
    "prodUrl": "https://payerb-prod.example.com/x12",
    "timeout": 45,
    "xmlWrapperRequired": true
  },
  "enveloping": {
    "isa08_receiverId": "PAYERB123",
    "gs03_applicationReceiverCode": "PAYERB"
  }
}
```

### Logic App Parameters

Configure in Logic App settings (Application Settings or Key Vault references):

| Parameter | Description | Example |
|-----------|-------------|---------|
| `payer_inquiry_endpoint` | Payer API endpoint URL | https://payer-api.example.com/auth-inquiry |
| `payer_api_token` | Bearer token for authentication | (SecureString from Key Vault) |
| `isa06_senderId` | ISA06 Sender ID | AVAILITY001 |
| `isa08_receiverId` | ISA08 Receiver ID (Payer ID) | PAYERID |
| `gs02_applicationSenderCode` | GS02 Sender Code | AVAILITY |
| `gs03_applicationReceiverCode` | GS03 Receiver Code | PAYER |

---

## Integration Examples

### Example 1: Provider Portal Integration

**Scenario**: Provider searches for patient's authorization before scheduling procedure

**Implementation**:

```javascript
// Frontend: React component
async function checkAuthorization(memberId, dob, providerNpi) {
  const response = await fetch('/api/auth-inquiry', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      memberId: memberId,
      patientDateOfBirth: dob,
      providerNpi: providerNpi,
      serviceType: 'HS'
    })
  });
  
  const result = await response.json();
  
  if (result.status === 'APPROVED') {
    alert(`Authorization ${result.authorizationNumber} is active until ${result.expirationDate}`);
  } else {
    alert(`Authorization status: ${result.status}. Reason: ${result.decisionReason}`);
  }
}
```

### Example 2: Pre-Claim Validation Workflow

**Scenario**: Automatically verify authorization before submitting claim

**Implementation**:

```python
# Python: Pre-claim validation script
def validate_claim_authorization(claim):
    auth_response = requests.post('https://logicapp.azurewebsites.net/api/auth-inquiry', 
        json={
            'authorizationNumber': claim['authNumber'],
            'providerNpi': claim['providerNpi']
        },
        headers={'Content-Type': 'application/json'}
    )
    
    auth_data = auth_response.json()
    
    if auth_data['status'] != 'APPROVED':
        return {
            'valid': False,
            'reason': f"Authorization not approved: {auth_data['status']}"
        }
    
    if auth_data['expirationDate'] < claim['serviceDate']:
        return {
            'valid': False,
            'reason': f"Authorization expired on {auth_data['expirationDate']}"
        }
    
    return {'valid': True, 'authNumber': auth_data['authorizationNumber']}
```

### Example 3: Nightly Batch - Expiring Authorizations

**Scenario**: Check all upcoming appointments for expiring authorizations

**Implementation**:

```powershell
# PowerShell: Nightly batch script
$appointments = Get-UpcomingAppointments -Days 14
$expiringAuths = @()

foreach ($appt in $appointments) {
    if ($appt.AuthNumber) {
        $response = Invoke-RestMethod `
            -Uri "https://logicapp.azurewebsites.net/api/auth-inquiry" `
            -Method Post `
            -ContentType "application/json" `
            -Body (@{
                authorizationNumber = $appt.AuthNumber
                providerNpi = $appt.ProviderNpi
            } | ConvertTo-Json)
        
        $daysUntilExpiry = ([DateTime]$response.expirationDate - [DateTime]::Now).Days
        
        if ($daysUntilExpiry -le 7) {
            $expiringAuths += @{
                Patient = $appt.PatientName
                AuthNumber = $appt.AuthNumber
                ExpiresIn = $daysUntilExpiry
                AppointmentDate = $appt.Date
            }
        }
    }
}

# Email report to authorization team
Send-ExpiringAuthsReport -Data $expiringAuths
```

---

## Testing Scenarios

### Test Case 1: Query by Authorization Number (Approved)

**Request**:
```json
POST /api/auth-inquiry
{
  "authorizationNumber": "AUTH12345",
  "providerNpi": "1234567890",
  "serviceType": "HS"
}
```

**Expected Response**:
```json
{
  "authorizationNumber": "AUTH12345",
  "status": "APPROVED",
  "effectiveDate": "2025-12-01",
  "expirationDate": "2025-12-31",
  "serviceType": "HS",
  "authorizedQuantity": 30,
  "quantityType": "DAYS"
}
```

**Validation**:
- Status code: 200
- Status: APPROVED
- Authorization number matches request
- Dates are valid and in future

---

### Test Case 2: Query by Member Demographics (Pended)

**Request**:
```json
POST /api/auth-inquiry
{
  "memberId": "MEM789012",
  "patientDateOfBirth": "1992-03-20",
  "providerNpi": "9876543210",
  "patientFirstName": "Jane",
  "patientLastName": "Doe"
}
```

**Expected Response**:
```json
{
  "authorizationNumber": "AUTH67890",
  "status": "PENDED",
  "decision": "Under review",
  "messages": [
    "ADDITIONAL INFORMATION REQUIRED",
    "Please submit clinical documentation within 5 business days"
  ]
}
```

**Validation**:
- Status code: 200
- Status: PENDED
- Messages array is populated
- Patient information is returned

---

### Test Case 3: Authorization Not Found

**Request**:
```json
POST /api/auth-inquiry
{
  "authorizationNumber": "INVALID999",
  "providerNpi": "1234567890"
}
```

**Expected Response**:
```json
{
  "status": "error",
  "errors": [{
    "errorCode": "AUTH_NOT_FOUND",
    "errorMessage": "Authorization number not found in payer system"
  }]
}
```

**Validation**:
- Status code: 500
- Error code: AUTH_NOT_FOUND
- Error message is descriptive

---

### Test Case 4: Invalid Schema (Missing Required Field)

**Request**:
```json
POST /api/auth-inquiry
{
  "memberId": "MEM123456"
  // Missing: patientDateOfBirth AND authorizationNumber
}
```

**Expected Response**:
```json
{
  "status": "error",
  "errors": [{
    "errorCode": "INVALID_SCHEMA",
    "errorMessage": "Request must include either authorizationNumber OR (memberId + patientDateOfBirth)"
  }]
}
```

**Validation**:
- Workflow terminates with Failed status
- Error code: INVALID_SCHEMA
- Conditional validation is enforced

---

### Test Case 5: Timeout and Retry

**Scenario**: Payer system slow to respond

**Request**:
```json
POST /api/auth-inquiry
{
  "authorizationNumber": "AUTH99999",
  "providerNpi": "1234567890"
}
```

**Expected Behavior**:
1. Initial request times out after 30 seconds
2. Retry #1 after 30 seconds
3. Retry #2 after 30 seconds
4. Retry #3 after 30 seconds
5. If all fail, return timeout error

**Expected Response** (after 3 failed retries):
```json
{
  "status": "error",
  "errors": [{
    "errorCode": "TIMEOUT",
    "errorMessage": "Payer system did not respond within timeout period (90 seconds total with retries)"
  }]
}
```

**Validation**:
- Application Insights shows 4 HTTP requests (initial + 3 retries)
- Each retry waited 30 seconds
- Total time ~120 seconds (3 x 30s timeout + 3 x 30s retry interval)

---

## Performance and Optimization

### Response Time SLA

**Target**: 95th percentile < 5 seconds  
**Maximum**: 30 seconds (with timeout)  
**Typical**: 2-4 seconds

**Factors Affecting Performance**:
- Payer system response time (largest factor)
- Network latency to payer endpoint
- X12 transformation overhead (minimal)
- Application Insights logging (negligible)

### Optimization Strategies

#### 1. Caching Authorization Status

For high-volume scenarios, consider caching recent inquiries:

**Cache Key**: `{authNumber}:{providerNpi}:{date}`  
**TTL**: 5-15 minutes (balance freshness vs. load)  
**Implementation**: Redis Cache or Logic App in-memory variable

**Example**:
```javascript
// Check cache before calling Logic App
const cacheKey = `auth:${authNumber}:${providerNpi}:${today}`;
let result = await redis.get(cacheKey);

if (!result) {
  result = await callAuthInquiryAPI(authNumber, providerNpi);
  await redis.setex(cacheKey, 300, JSON.stringify(result)); // 5 min TTL
}
```

#### 2. Batch Processing

For nightly batches, process authorizations in parallel:

**Recommended**: 10-20 concurrent requests  
**Rate Limiting**: Check payer API limits  
**Implementation**: PowerShell `-Parallel` or Python `asyncio`

**Example**:
```powershell
$appointments | ForEach-Object -Parallel {
    $result = Invoke-RestMethod -Uri $using:apiUrl -Method Post -Body (...)
    # Process result
} -ThrottleLimit 10
```

#### 3. Connection Pooling

Reuse HTTP connections to Logic App endpoint:

**HTTP/2**: Enabled by default in Azure  
**Keep-Alive**: Set `Connection: keep-alive` header  
**Connection Pool Size**: 100-200 connections

---

## Security Considerations

### Authentication

**Logic App Endpoint**: Protected by Azure AD Easy Auth  
**Payer Endpoint**: Bearer token authentication (stored in Key Vault)  
**Recommendation**: Use managed identity for Key Vault access

### PHI Protection

Authorization inquiries contain Protected Health Information (PHI):

**Data Elements**:
- Member ID
- Patient name
- Date of birth
- Provider NPI
- Authorization numbers

**HIPAA Compliance Requirements**:

1. **Encryption in Transit**: TLS 1.2+ for all API calls
2. **Encryption at Rest**: Data Lake storage encryption
3. **Access Logging**: Application Insights with PHI masking
4. **Minimum Necessary**: Only query data needed for purpose
5. **Audit Trail**: Log all authorization inquiries with timestamp and user

### PHI Masking in Application Insights

Configure Data Collection Rules to mask PHI:

```kusto
// Application Insights transformation
customEvents
| extend maskedMemberId = replace(@'\d{6,}', 'MASKED', tostring(customDimensions.memberId))
| extend maskedDob = replace(@'\d{4}-\d{2}-\d{2}', 'MASKED', tostring(customDimensions.dateOfBirth))
| project timestamp, name, maskedMemberId, maskedDob, operation_Id
```

---

## Troubleshooting

### Issue: "Authorization Not Found"

**Symptoms**: HCR code A3, message "Authorization not found"

**Troubleshooting Steps**:
1. Verify authorization number format and spelling
2. Check if authorization was submitted to different payer
3. Confirm member eligibility at time of service
4. Try querying by member demographics instead

**Common Causes**:
- Authorization number typo
- Authorization submitted to wrong payer
- Authorization expired and purged from system
- Member coverage terminated

---

### Issue: "Timeout" Errors

**Symptoms**: HTTP 504, timeout after 30 seconds

**Troubleshooting Steps**:
1. Check payer system status page
2. Verify network connectivity to payer endpoint
3. Review retry logic in Application Insights
4. Increase timeout if payer consistently slow (up to 60s max)

**Resolution**:
- Wait 5-10 minutes and retry
- Contact payer support if persistent
- Consider increasing timeout in configuration

---

### Issue: Invalid NPI

**Symptoms**: HTTP 400, "Invalid provider NPI"

**Troubleshooting Steps**:
1. Validate NPI format (exactly 10 digits)
2. Verify NPI is active in NPPES registry
3. Check provider enrollment with payer
4. Confirm provider is in-network

**Resolution**:
- Update NPI if incorrect
- Complete provider enrollment if needed
- Contact payer credentialing department

---

## API Reference

### HTTP POST /api/auth-inquiry

**Description**: Query authorization status by authorization number or member demographics

**Authentication**: Azure AD Easy Auth (Bearer token)

**Request Headers**:
```
Content-Type: application/json
Authorization: Bearer {token}
```

**Request Body Schema**: See [Request Field Requirements](#request-field-requirements)

**Response Codes**:
- **200 OK**: Successful inquiry
- **400 Bad Request**: Invalid request format
- **401 Unauthorized**: Missing or invalid authentication
- **500 Internal Server Error**: Payer system error or timeout

**Response Body Schema**: See [Response Structure and Status Codes](#response-structure-and-status-codes)

---

## Appendix

### X12 278 X215 Segment Reference

| Segment | Description | Required | Max Use |
|---------|-------------|----------|---------|
| ISA | Interchange Control Header | R | 1 |
| GS | Functional Group Header | R | 1 |
| ST | Transaction Set Header | R | 1 |
| BHT | Beginning of Hierarchical Transaction | R | 1 |
| HL | Hierarchical Level (20 - Payer) | R | 1 |
| NM1 | Payer Name | R | 1 |
| HL | Hierarchical Level (21 - Provider) | R | 1 |
| NM1 | Provider Name | R | 1 |
| HL | Hierarchical Level (22 - Subscriber) | R | 1 |
| NM1 | Subscriber Name | R | 1 |
| DMG | Subscriber Demographics | S | 1 |
| REF | Authorization Number Reference | S | 9 |
| HL | Hierarchical Level (23 - Utilization) | R | 1 |
| TRN | Trace | R | 2 |
| UM | Utilization Management | R | 1 |
| HCR | Health Care Services Review | R | 1 |
| DTP | Date/Time Period | S | 9 |
| SE | Transaction Set Trailer | R | 1 |
| GE | Functional Group Trailer | R | 1 |
| IEA | Interchange Control Trailer | R | 1 |

### Glossary

**AR**: Admission Review (Inpatient authorization)  
**Authorization Number**: Payer-assigned unique identifier for authorization  
**HS**: Health Services Review (Outpatient authorization)  
**NPI**: National Provider Identifier (10-digit provider ID)  
**QRE**: Quick Reference for Essentials (Availity implementation guide)  
**SC**: Specialty Care Review (Referral authorization)  
**TR3**: Technical Report Type 3 (X12 implementation guide)  
**X215**: X12 278 Healthcare Services Review - Inquiry and Response  
**X217**: X12 278 Healthcare Services Review - Request and Response  

### Related Documentation

- [Availity X12 278 X215 QRE](https://availity.com/documentation)
- [X12 278 TR3 005010X215](https://x12.org)
- [HIPAA Transaction Standards](https://www.cms.gov/regulations-and-guidance/administrative-simplification/hipaa-aca/hipaa-transaction-code-set-standards)
- [Authorization Request Workflow (X217)](./AUTHORIZATION-REQUEST.md) *(Future PR #55)*

---

**Document Version**: 1.0  
**Last Updated**: 2025-11-19  
**Maintained By**: HIPAA Attachments Integration Team  
**Questions?**: Contact integration-support@example.com
