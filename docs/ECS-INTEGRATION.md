# ECS (Enhanced Claim Status) Integration Guide

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Search Methods](#search-methods)
- [Request Examples](#request-examples)
- [Response Examples](#response-examples)
- [Error Handling](#error-handling)
- [API Mappings](#api-mappings)
- [Security](#security)
- [Testing](#testing)
- [Troubleshooting](#troubleshooting)

## Overview

The Enhanced Claim Status (ECS) integration enables providers to query claim status information from the QNXT claims adjudication system. The integration implements four search methods to support various claim lookup scenarios.

### Features

- **Four Search Methods**: Service Date, Member, Check Number, and Claim History
- **QNXT Backend Integration**: Direct integration with QNXT Claims API
- **Request Validation**: Schema-based validation of incoming requests
- **Response Transformation**: Converts QNXT responses to Availity ECS format
- **Retry Logic**: Automatic retries for transient failures (4 retries @ 15s intervals)
- **Error Handling**: Comprehensive error handling with detailed error messages
- **Audit Logging**: All searches logged to Application Insights

### Workflow Location

The ECS Summary Search workflow is located at:
```
logicapps/workflows/ecs_summary_search/workflow.json
```

### Endpoint

**HTTP POST**: `https://{logic-app-name}.azurewebsites.net/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke`

## Architecture

### High-Level Flow

```
┌─────────────┐      ┌──────────────────┐      ┌──────────────┐      ┌─────────────────┐
│   Provider  │ ───> │  ECS Logic App   │ ───> │  QNXT API    │ ───> │  Claims Database│
│   System    │ <─── │  (Workflow)      │ <─── │              │ <─── │                 │
└─────────────┘      └──────────────────┘      └──────────────┘      └─────────────────┘
                              │
                              ├──> Application Insights (Logging)
                              └──> Service Bus (Future: async processing)
```

### Components

1. **HTTP Trigger**: Receives ECS search requests
2. **Request Validation**: Validates incoming request schema
3. **Search Router**: Routes to appropriate search method
4. **QNXT Client**: Makes API calls to QNXT backend with retry logic
5. **Response Transformer**: Converts QNXT format to Availity ECS format
6. **Error Handler**: Catches and formats errors
7. **Logger**: Records all operations to Application Insights

## Search Methods

### 1. Service Date Search

**Use Case**: Find claims for a specific provider within a date range.

**Required Parameters**:
- `serviceFromDate`: Start of service date range (CCYYMMDD)
- `serviceToDate`: End of service date range (CCYYMMDD)
- `providerId`: Provider NPI or Tax ID
- `providerIdQualifier`: Type of provider ID ('NPI', 'XX', 'EI')

**Example Scenario**: 
> "Find all claims for Dr. Smith (NPI: 1234567890) with service dates between January 1, 2024 and January 31, 2024"

---

### 2. Member Search

**Use Case**: Find claims for a specific member/patient within a date range.

**Required Parameters**:
- `memberId`: Member/Subscriber ID
- `serviceFromDate`: Start of service date range (CCYYMMDD)
- `serviceToDate`: End of service date range (CCYYMMDD)

**Optional Parameters**:
- `firstName`: Member first name (for verification)
- `lastName`: Member last name (for verification)
- `dateOfBirth`: Member date of birth (CCYYMMDD)

**Example Scenario**:
> "Find all claims for member John Doe (ID: M123456) from January 2024 to March 2024"

---

### 3. Check Number Search

**Use Case**: Find claims paid via a specific check or EFT.

**Required Parameters**:
- `checkNumber`: Check or EFT trace number
- `payerId`: Payer identifier

**Optional Parameters**:
- `checkDate`: Check issue date (CCYYMMDD)

**Example Scenario**:
> "Find all claims paid on check #CHK789456 from UnitedHealthcare"

---

### 4. Claim History Search

**Use Case**: Retrieve detailed history for a specific claim.

**Required Parameters**:
- `claimNumber`: Payer claim control number

**Optional Parameters**:
- `patientAccountNumber`: Provider's patient account number
- `memberId`: Member ID (for validation)

**Example Scenario**:
> "Retrieve full history for claim CLM987654321 including all status changes and adjustments"

## Request Examples

### Service Date Search Request

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

### Member Search Request

```json
{
  "searchMethod": "Member",
  "requestId": "REQ-2024-002",
  "submitterId": "PROV-12345",
  "memberSearch": {
    "memberId": "M123456",
    "firstName": "John",
    "lastName": "Doe",
    "dateOfBirth": "19850315",
    "serviceFromDate": "20240101",
    "serviceToDate": "20240331"
  }
}
```

### Check Number Search Request

```json
{
  "searchMethod": "CheckNumber",
  "requestId": "REQ-2024-003",
  "submitterId": "PROV-12345",
  "checkNumberSearch": {
    "checkNumber": "CHK789456",
    "payerId": "UNITED",
    "checkDate": "20240215"
  }
}
```

### Claim History Search Request

```json
{
  "searchMethod": "ClaimHistory",
  "requestId": "REQ-2024-004",
  "submitterId": "PROV-12345",
  "claimHistorySearch": {
    "claimNumber": "CLM987654321",
    "patientAccountNumber": "PAT-9876",
    "memberId": "M123456"
  }
}
```

## Response Examples

### Successful Response (Multiple Claims)

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
      "patientAccountNumber": "PAT-1001",
      "claimStatus": "Paid",
      "claimStatusCode": "F2",
      "claimStatusCategory": "Finalized",
      "serviceFromDate": "20240110",
      "serviceToDate": "20240110",
      "receivedDate": "20240112",
      "processedDate": "20240113",
      "paidDate": "20240115",
      "billedAmount": 250.00,
      "allowedAmount": 200.00,
      "paidAmount": 160.00,
      "patientResponsibility": 40.00,
      "adjustmentAmount": -50.00,
      "payerId": "PCHP",
      "payerName": "Parkland Community Health Plan",
      "providerId": "1234567890",
      "providerName": "Dr. John Smith",
      "memberId": "M123456",
      "memberName": "Jane Doe",
      "checkNumber": "CHK789456",
      "checkDate": "20240115",
      "remarkCodes": ["M80"],
      "reasonCodes": ["CO-45"],
      "statusDetails": "Claim processed and paid"
    },
    {
      "claimNumber": "CLM123456790",
      "patientAccountNumber": "PAT-1002",
      "claimStatus": "Pending",
      "claimStatusCode": "F0",
      "claimStatusCategory": "Adjudication",
      "serviceFromDate": "20240125",
      "serviceToDate": "20240125",
      "receivedDate": "20240126",
      "billedAmount": 150.00,
      "payerId": "PCHP",
      "payerName": "Parkland Community Health Plan",
      "providerId": "1234567890",
      "providerName": "Dr. John Smith",
      "memberId": "M789012",
      "memberName": "John Smith",
      "statusDetails": "Claim in adjudication process"
    }
  ]
}
```

### Successful Response (No Results)

```json
{
  "requestId": "REQ-2024-005",
  "status": "success",
  "timestamp": "2024-01-15T11:00:00.000Z",
  "searchMethod": "ServiceDate",
  "totalResults": 0,
  "claims": []
}
```

### Error Response

```json
{
  "requestId": "REQ-2024-006",
  "status": "error",
  "timestamp": "2024-01-15T11:15:30.456Z",
  "searchMethod": "Member",
  "totalResults": 0,
  "claims": [],
  "errors": [
    {
      "errorCode": "MEMBER_NOT_FOUND",
      "errorMessage": "The specified member ID was not found in the system",
      "errorDetails": "Member ID M999999 does not exist in the claims database"
    }
  ]
}
```

## Error Handling

### Error Scenarios

The ECS workflow handles the following error scenarios:

1. **Invalid Request Schema**
   - **Cause**: Missing required fields or invalid data types
   - **Response**: 400 Bad Request with validation errors
   - **Example**: Missing `searchMethod` field

2. **Invalid Search Method**
   - **Cause**: Unknown search method specified
   - **Response**: 400 Bad Request with error details
   - **Example**: `searchMethod: "InvalidMethod"`

3. **QNXT API Unavailable**
   - **Cause**: QNXT backend system is down or unreachable
   - **Response**: 500 Internal Server Error
   - **Retry**: Automatic retry (4 attempts @ 15s intervals)

4. **QNXT Authentication Failure**
   - **Cause**: Invalid or expired API token
   - **Response**: 401 Unauthorized
   - **Action**: Token refresh required

5. **Rate Limit Exceeded**
   - **Cause**: Too many requests to QNXT API
   - **Response**: 429 Too Many Requests
   - **Action**: Implement exponential backoff

6. **Timeout**
   - **Cause**: QNXT API response took too long
   - **Response**: 504 Gateway Timeout
   - **Action**: Check QNXT system performance

### Error Response Format

All errors follow this standard format:

```json
{
  "requestId": "string",
  "status": "error",
  "timestamp": "ISO-8601 datetime",
  "searchMethod": "string",
  "totalResults": 0,
  "claims": [],
  "errors": [
    {
      "errorCode": "ERROR_CODE",
      "errorMessage": "Human-readable message",
      "errorDetails": "Additional context"
    }
  ]
}
```

### Common Error Codes

| Error Code | Description | HTTP Status |
|------------|-------------|-------------|
| `INVALID_REQUEST` | Request validation failed | 400 |
| `INVALID_SEARCH_METHOD` | Unknown search method | 400 |
| `MEMBER_NOT_FOUND` | Member ID not found | 404 |
| `CLAIM_NOT_FOUND` | Claim number not found | 404 |
| `PROVIDER_NOT_FOUND` | Provider ID not found | 404 |
| `DATE_RANGE_INVALID` | Invalid date range | 400 |
| `AUTHENTICATION_FAILED` | API token invalid | 401 |
| `RATE_LIMIT_EXCEEDED` | Too many requests | 429 |
| `QNXT_UNAVAILABLE` | Backend unavailable | 503 |
| `INTERNAL_ERROR` | Unexpected error | 500 |
| `TIMEOUT` | Request timeout | 504 |

## API Mappings

### QNXT to Availity ECS Mapping

#### Claim Status Mapping

| QNXT Status Code | QNXT Status | ECS Status | ECS Status Code |
|------------------|-------------|------------|-----------------|
| 01 | Received | Pending | F0 |
| 02 | In Process | In Process | F1 |
| 03 | Finalized - Paid | Paid | F2 |
| 04 | Finalized - Denied | Denied | F3 |
| 05 | Partially Paid | Partially Paid | F2 |
| 06 | Suspended | Suspended | F1 |
| 07 | Forwarded | Forwarded | F1 |
| 08 | Adjusted | Adjusted | F2 |

#### Date Format Mapping

| QNXT Format | ECS Format | Example |
|-------------|------------|---------|
| YYYY-MM-DD | CCYYMMDD | 2024-01-15 → 20240115 |
| ISO 8601 | CCYYMMDD | 2024-01-15T10:30:00Z → 20240115 |

#### Amount Mapping

| QNXT Field | ECS Field | Notes |
|------------|-----------|-------|
| `billed_amount` | `billedAmount` | Convert to decimal |
| `allowed_amount` | `allowedAmount` | Convert to decimal |
| `paid_amount` | `paidAmount` | Convert to decimal |
| `patient_liability` | `patientResponsibility` | Convert to decimal |
| `adjustment_total` | `adjustmentAmount` | Calculate from adjustments |

#### Field Mapping

| QNXT Field | ECS Field | Transformation |
|------------|-----------|----------------|
| `claim_id` | `claimNumber` | Direct mapping |
| `patient_acct_num` | `patientAccountNumber` | Direct mapping |
| `claim_status_code` | `claimStatusCode` | Via status mapping table |
| `service_from_dt` | `serviceFromDate` | Date format conversion |
| `service_to_dt` | `serviceToDate` | Date format conversion |
| `received_dt` | `receivedDate` | Date format conversion |
| `processed_dt` | `processedDate` | Date format conversion |
| `paid_dt` | `paidDate` | Date format conversion |
| `payer_id` | `payerId` | Direct mapping |
| `payer_name` | `payerName` | Direct mapping |
| `provider_npi` | `providerId` | Direct mapping |
| `provider_name` | `providerName` | Direct mapping |
| `member_id` | `memberId` | Direct mapping |
| `member_name` | `memberName` | Format: "LastName, FirstName" |
| `check_number` | `checkNumber` | Direct mapping |
| `check_dt` | `checkDate` | Date format conversion |
| `remark_codes` | `remarkCodes` | Array conversion |
| `reason_codes` | `reasonCodes` | Array conversion |
| `status_description` | `statusDetails` | Direct mapping |

## Security

### Authentication

The ECS workflow uses Bearer token authentication:

```http
POST /api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke
Authorization: Bearer YOUR_JWT_TOKEN_HERE
Content-Type: application/json
```

**Note**: Replace `YOUR_JWT_TOKEN_HERE` with your actual JWT token obtained from Azure AD OAuth 2.0.

### QNXT API Authentication

QNXT API calls use a separate Bearer token:

```http
Authorization: Bearer YOUR_QNXT_TOKEN_HERE
```

**Note**: The QNXT token is stored securely in Azure Key Vault and referenced via Logic App parameters.

**Token Management**:
- Tokens are stored as secure parameters in Logic App configuration
- Tokens expire after 1 hour
- Automatic token refresh is implemented
- Failed authentication triggers alert

### HIPAA Compliance

1. **Encryption in Transit**: All API calls use TLS 1.2 or higher
2. **Encryption at Rest**: Response data encrypted in storage
3. **Audit Logging**: All searches logged to Application Insights
4. **Access Control**: Role-based access to Logic App endpoints
5. **PHI Protection**: No PHI in application logs

### Security Best Practices

- Use Azure Key Vault for API tokens
- Implement IP whitelisting for Logic App
- Enable diagnostic logging
- Monitor for unusual access patterns
- Regularly rotate API credentials

## Testing

### Unit Tests

Test individual components:

1. **Request Schema Validation**
   ```bash
   # Validate request against schema
   ajv validate -s schemas/ECS-SummarySearch-Request.json -d test-request.json
   ```

2. **Response Schema Validation**
   ```bash
   # Validate response against schema
   ajv validate -s schemas/ECS-SummarySearch-Response.json -d test-response.json
   ```

### Integration Tests

Test end-to-end workflow:

1. **Service Date Search Test**
   ```bash
   # Replace YOUR_JWT_TOKEN with your actual Azure AD JWT token
   curl -X POST \
     -H "Authorization: Bearer YOUR_JWT_TOKEN" \
     -H "Content-Type: application/json" \
     -d @test-service-date-request.json \
     https://{logic-app}.azurewebsites.net/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke
   ```

2. **Member Search Test**
3. **Check Number Search Test**
4. **Claim History Search Test**

### Test Data

Sample test data is available in:
- `test-data/ecs-service-date-request.json`
- `test-data/ecs-member-request.json`
- `test-data/ecs-check-number-request.json`
- `test-data/ecs-claim-history-request.json`

### Mock QNXT Backend

For testing without QNXT connectivity:

1. Deploy mock QNXT API (Azure Function or API Management)
2. Configure Logic App to use mock URL
3. Mock returns predictable test data
4. Supports all search methods and error scenarios

## Troubleshooting

### Common Issues

#### 1. Request Validation Failures

**Symptom**: 400 Bad Request responses

**Causes**:
- Missing required fields
- Invalid date formats (must be CCYYMMDD)
- Invalid search method value

**Solution**:
- Validate request against schema before sending
- Use JSON schema validator in client code
- Check date format (CCYYMMDD, not YYYY-MM-DD)

#### 2. QNXT Connection Timeouts

**Symptom**: 504 Gateway Timeout

**Causes**:
- QNXT API is slow to respond
- Network connectivity issues
- Large result sets

**Solution**:
- Check QNXT system status
- Narrow date ranges to reduce result size
- Contact QNXT support if persistent

#### 3. Authentication Failures

**Symptom**: 401 Unauthorized from QNXT

**Causes**:
- Expired API token
- Invalid API token
- Token configuration issue

**Solution**:
- Verify token in Logic App parameters
- Check token expiration
- Refresh token if expired
- Verify token has correct permissions

#### 4. No Results Returned

**Symptom**: 200 OK but empty claims array

**Causes**:
- No claims match search criteria
- Date range is incorrect
- Provider ID or Member ID is wrong

**Solution**:
- Verify search criteria are correct
- Broaden date range
- Confirm IDs exist in QNXT
- Check for typos in identifiers

### Debugging

#### Application Insights Queries

**Search Request Logs**:
```kusto
traces
| where customDimensions.EventName == "ECSSearchRequestReceived"
| project timestamp, requestId = customDimensions.requestId, 
          searchMethod = customDimensions.searchMethod
| order by timestamp desc
```

**Search Success Logs**:
```kusto
traces
| where customDimensions.EventName == "ECSSearchSuccess"
| project timestamp, requestId = customDimensions.requestId,
          totalResults = customDimensions.totalResults
| order by timestamp desc
```

**Search Error Logs**:
```kusto
traces
| where customDimensions.EventName == "ECSSearchError"
| project timestamp, requestId = customDimensions.requestId,
          errorDetails = customDimensions.errorDetails
| order by timestamp desc
```

**QNXT API Performance**:
```kusto
dependencies
| where name contains "QNXT"
| summarize avg(duration), percentile(duration, 95) by bin(timestamp, 1h)
| order by timestamp desc
```

#### Enable Debug Logging

In Logic App configuration, set:
```json
{
  "logLevel": "Debug",
  "includeRequestBody": true,
  "includeResponseBody": true
}
```

**⚠️ Warning**: Debug logging may include PHI. Use only in non-production environments.

### Performance Tuning

**Optimize Search Performance**:
1. Use narrow date ranges (≤90 days recommended)
2. Specify optional parameters for faster filtering
3. Implement client-side caching for repeated searches
4. Consider pagination for large result sets (future)

**Monitor Performance Metrics**:
- Average response time by search method
- 95th percentile response times
- QNXT API call duration
- Retry frequency
- Error rates by type

## References

- [BACKEND-INTERFACE.md](./BACKEND-INTERFACE.md) - Backend interface specification
- [ECS-OPENAPI.yaml](./api/ECS-OPENAPI.yaml) - OpenAPI specification
- [ARCHITECTURE.md](../ARCHITECTURE.md) - System architecture
- [SECURITY.md](../SECURITY.md) - Security best practices
- [Availity ECS Documentation](https://www.availity.com/ecs) - ECS standard reference
- [X12 277 Implementation Guide](https://x12.org/products/277) - Health Care Claim Status Response
