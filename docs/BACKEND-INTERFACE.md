# Claims Backend Interface

## Overview

This document defines the backend-agnostic `ClaimsBackend` interface for the Cloud Health Office system. The interface abstracts claims adjudication system-specific implementations to enable **configuration-driven multi-payer platformization** and support for any backend system (QNXT, FacetsRx, TriZetto, etc.).

## Purpose

The `ClaimsBackend` interface serves as a contract between the Logic Apps workflows and the underlying claims adjudication systems. By defining a consistent interface, we enable:

1. **Backend Agnosticism**: Logic Apps remain independent of specific backend implementations
2. **Zero-Code Onboarding**: Add new payers by configuring backend connection, no custom code required
3. **Extensibility**: New backend systems can be integrated by implementing the interface
4. **Testing**: Mock implementations can be used for testing without connecting to production systems
5. **Maintainability**: Changes to backend systems are isolated from workflow logic
6. **Multi-Tenant**: Single platform serves multiple payers with different backend systems

### Configuration-Driven Architecture

All backend implementations are configured through the **Unified Availity Integration Configuration Schema**. Each payer defines their backend connection parameters including:
- API endpoints and authentication
- Field mappings between standard interface and backend schema
- Retry policies and timeouts
- Custom business rules

This enables **zero-code payer onboarding** - adding a new payer requires only creating a configuration file and running the deployment generator.

## Interface Definition

### Core Methods

#### 1. Search Claims by Service Date

**Purpose**: Query claims based on service date range and provider identifier.

**Input Parameters**:
```typescript
interface ServiceDateSearchParams {
  serviceFromDate: string;      // CCYYMMDD format
  serviceToDate: string;         // CCYYMMDD format
  providerId: string;            // Provider NPI or Tax ID
  providerIdQualifier: string;   // 'NPI' | 'XX' | 'EI'
}
```

**Output**:
```typescript
interface ClaimsSearchResult {
  claims: Claim[];
  totalResults: number;
  searchTimestamp: string;
}
```

**Endpoint Pattern**: `POST /api/v1/claims/search/service-date`

**Expected Behavior**:
- Returns all claims for the specified provider within the date range
- Date range is inclusive (includes both start and end dates)
- Results should be sorted by service date (most recent first)
- Empty array returned if no claims found (not an error)

---

#### 2. Search Claims by Member

**Purpose**: Query claims for a specific member within a date range.

**Input Parameters**:
```typescript
interface MemberSearchParams {
  memberId: string;              // Member/Subscriber ID
  firstName?: string;            // Optional for verification
  lastName?: string;             // Optional for verification
  dateOfBirth?: string;          // Optional, CCYYMMDD format
  serviceFromDate: string;       // CCYYMMDD format
  serviceToDate: string;         // CCYYMMDD format
}
```

**Output**: `ClaimsSearchResult` (same as Service Date search)

**Endpoint Pattern**: `POST /api/v1/claims/search/member`

**Expected Behavior**:
- Returns all claims for the specified member within the date range
- If name/DOB provided, should validate before returning results
- Results sorted by service date (most recent first)
- Empty array if no claims found or member validation fails

---

#### 3. Search Claims by Check Number

**Purpose**: Query claims paid via a specific check or EFT.

**Input Parameters**:
```typescript
interface CheckNumberSearchParams {
  checkNumber: string;           // Check or EFT trace number
  payerId: string;               // Payer identifier
  checkDate?: string;            // Optional, CCYYMMDD format
}
```

**Output**: `ClaimsSearchResult`

**Endpoint Pattern**: `POST /api/v1/claims/search/check-number`

**Expected Behavior**:
- Returns all claims associated with the specified check/EFT
- Check date, if provided, should be used for validation
- Results grouped by check number
- Empty array if check number not found

---

#### 4. Search Claim History

**Purpose**: Query detailed history for a specific claim.

**Input Parameters**:
```typescript
interface ClaimHistorySearchParams {
  claimNumber: string;           // Payer claim control number
  patientAccountNumber?: string; // Optional provider reference
  memberId?: string;             // Optional for validation
}
```

**Output**: `ClaimHistoryResult`

**Endpoint Pattern**: `POST /api/v1/claims/search/claim-history`

**Expected Behavior**:
- Returns full history for the specified claim
- Includes all status changes, adjustments, and payments
- History sorted chronologically (oldest to newest)
- Error if claim not found

---

## Common Data Structures

### Claim Object

```typescript
interface Claim {
  claimNumber: string;              // Required: Payer claim control number
  patientAccountNumber?: string;    // Provider's patient account number
  claimStatus: ClaimStatus;         // Current status
  claimStatusCode: string;          // X12 status code (F0, F1, etc.)
  claimStatusCategory?: string;     // Status category
  
  // Date fields (CCYYMMDD format)
  serviceFromDate: string;
  serviceToDate: string;
  receivedDate?: string;
  processedDate?: string;
  paidDate?: string;
  
  // Financial amounts
  billedAmount: number;
  allowedAmount?: number;
  paidAmount?: number;
  patientResponsibility?: number;
  adjustmentAmount?: number;
  
  // Party identifiers
  payerId: string;
  payerName?: string;
  providerId: string;
  providerName?: string;
  memberId: string;
  memberName?: string;
  
  // Payment information
  checkNumber?: string;
  checkDate?: string;              // CCYYMMDD format
  
  // Adjustment codes
  remarkCodes?: string[];
  reasonCodes?: string[];
  
  // Additional details
  statusDetails?: string;
}
```

### Claim Status Enumeration

```typescript
enum ClaimStatus {
  Pending = "Pending",
  Paid = "Paid",
  Denied = "Denied",
  PartiallyPaid = "Partially Paid",
  InProcess = "In Process",
  Suspended = "Suspended",
  Forwarded = "Forwarded",
  Adjusted = "Adjusted"
}
```

### Claims Search Result

```typescript
interface ClaimsSearchResult {
  claims: Claim[];
  totalResults: number;
  searchTimestamp: string;        // ISO 8601 format
}
```

### Claim History Result

```typescript
interface ClaimHistoryResult {
  claim: Claim;
  history: HistoryEntry[];
  totalEvents: number;
}

interface HistoryEntry {
  eventDate: string;              // ISO 8601 format
  eventType: string;              // e.g., "Received", "Processed", "Paid"
  statusBefore?: string;
  statusAfter: string;
  amount?: number;
  notes?: string;
  userId?: string;
}
```

## Error Handling

### Standard Error Response

All backend implementations must return consistent error responses:

```typescript
interface ErrorResponse {
  errorCode: string;              // Machine-readable error code
  errorMessage: string;           // Human-readable message
  errorDetails?: string;          // Additional context
  timestamp: string;              // ISO 8601 format
}
```

### Common Error Codes

- `INVALID_REQUEST`: Request parameters are invalid or missing
- `MEMBER_NOT_FOUND`: Member ID does not exist in the system
- `CLAIM_NOT_FOUND`: Claim number does not exist
- `PROVIDER_NOT_FOUND`: Provider ID is not valid
- `DATE_RANGE_INVALID`: Date range is invalid (e.g., from > to)
- `AUTHENTICATION_FAILED`: API token is invalid or expired
- `AUTHORIZATION_FAILED`: User lacks permission for the operation
- `RATE_LIMIT_EXCEEDED`: Too many requests in time window
- `BACKEND_UNAVAILABLE`: Backend system is temporarily unavailable
- `INTERNAL_ERROR`: Unexpected internal error occurred

### HTTP Status Codes

- `200 OK`: Successful search (even if no results)
- `400 Bad Request`: Invalid request parameters
- `401 Unauthorized`: Authentication failure
- `403 Forbidden`: Authorization failure
- `404 Not Found`: Resource not found (claim, member, etc.)
- `429 Too Many Requests`: Rate limit exceeded
- `500 Internal Server Error`: Backend system error
- `503 Service Unavailable`: Backend system unavailable

## Implementation Requirements

### Required Features

All backend implementations MUST support:

1. **Authentication**: Bearer token authentication via Authorization header
2. **Retry Logic**: Graceful handling of transient failures
3. **Timeout**: Responses within 30 seconds (or appropriate timeout)
4. **Pagination**: Support for large result sets (future enhancement)
5. **Logging**: Audit logging of all search operations
6. **HIPAA Compliance**: Encryption in transit (TLS 1.2+) and at rest

### Optional Features

Backend implementations MAY support:

1. **Caching**: Response caching for frequently accessed claims
2. **Batch Operations**: Multiple searches in a single request
3. **Webhooks**: Notifications for claim status changes
4. **Advanced Filtering**: Additional search criteria
5. **Real-time Updates**: WebSocket or SSE for live status updates

## QNXT Backend Implementation

### Overview

The QNXT Claims API is the initial backend implementation for this system.

### API Base URL

- **DEV**: `https://qnxt-api-dev.example.com`
- **UAT**: `https://qnxt-api-uat.example.com`
- **PROD**: `https://qnxt-api.example.com`

### Authentication

QNXT uses Bearer token authentication:

```http
Authorization: Bearer <jwt-token>
```

Tokens are obtained via Azure AD OAuth 2.0 and expire after 1 hour.

### Endpoint Mappings

| Interface Method | QNXT Endpoint |
|-----------------|---------------|
| Service Date Search | `POST /api/v1/claims/search/service-date` |
| Member Search | `POST /api/v1/claims/search/member` |
| Check Number Search | `POST /api/v1/claims/search/check-number` |
| Claim History | `POST /api/v1/claims/search/claim-history` |

### QNXT-Specific Considerations

1. **Date Format**: QNXT expects `CCYYMMDD` format (e.g., `20240115`)
2. **Claim Status Mapping**: QNXT internal codes are mapped to standard status enum
3. **Pagination**: QNXT supports pagination via `page` and `pageSize` query parameters
4. **Rate Limits**: 100 requests per minute per API key
5. **Retry Policy**: 4 retries with 15-second intervals for transient failures

### QNXT Response Transformation

QNXT responses must be transformed to the standard `Claim` format:

```typescript
// QNXT Internal Format (example)
interface QnxtClaim {
  claimId: string;              // Maps to claimNumber
  clmStatus: string;            // Maps to claimStatus (via lookup)
  svcFromDt: string;            // Maps to serviceFromDate
  // ... other QNXT-specific fields
}

// Transformation logic in Logic App:
// - Map QNXT field names to standard interface
// - Convert date formats (QNXT uses YYYYMMDD)
// - Translate status codes to standard enum values
// - Calculate derived fields (e.g., adjustmentAmount)
```

## Future Backend Implementations

### Potential Backend Systems

The interface is designed to support additional backend systems:

1. **Epic Payer Platform**: Large health system claims
2. **Change Healthcare**: Clearinghouse claims management
3. **Optum**: UnitedHealth claims system
4. **Custom Backend**: Organization-specific systems

### Integration Approach

To add a new backend:

1. Implement all four search methods according to interface
2. Ensure error responses follow standard format
3. Map backend-specific data to standard `Claim` format
4. Create backend-specific configuration module
5. Update Logic Apps to route to appropriate backend
6. Document backend-specific considerations

### Configuration-Based Routing

Future enhancement: Route searches to different backends based on configuration:

```json
{
  "backendConfig": {
    "default": "qnxt",
    "backends": {
      "qnxt": {
        "baseUrl": "https://qnxt-api.example.com",
        "authType": "bearer",
        "timeout": 30
      },
      "epic": {
        "baseUrl": "https://epic-api.example.com",
        "authType": "oauth2",
        "timeout": 45
      }
    },
    "routing": {
      "rules": [
        {
          "condition": "payerId == 'UNITED'",
          "backend": "optum"
        }
      ]
    }
  }
}
```

## Testing and Validation

### Mock Backend Implementation

For testing, a mock backend should be created that:

1. Returns predictable test data
2. Simulates various error conditions
3. Validates request parameters
4. Supports all search methods
5. Does not require actual backend connectivity

### Integration Testing

Integration tests should verify:

1. All four search methods work correctly
2. Error handling behaves as expected
3. Retry logic functions properly
4. Response transformation is accurate
5. Performance meets requirements

### Contract Testing

Use contract testing to ensure:

1. Backend implementations adhere to interface
2. Request/response schemas are validated
3. Breaking changes are detected early
4. Version compatibility is maintained

## Best Practices

### For Backend Implementers

1. **Follow Interface Strictly**: Adhere to all interface requirements
2. **Document Deviations**: Clearly document any limitations or variations
3. **Version API**: Use semantic versioning for API changes
4. **Provide Swagger/OpenAPI**: Include machine-readable API specification
5. **Support Monitoring**: Expose health and metrics endpoints

### For Logic App Developers

1. **Use Interface Types**: Reference this document for data structures
2. **Handle All Errors**: Implement robust error handling for all methods
3. **Log Appropriately**: Log search operations for audit and troubleshooting
4. **Transform Data**: Ensure all responses match Availity ECS format
5. **Test Thoroughly**: Test with multiple backend implementations

## Versioning

- **Current Version**: 1.0.0
- **Last Updated**: 2024-01-15
- **Change History**:
  - 1.0.0 (2024-01-15): Initial interface definition

## References

- [ECS-INTEGRATION.md](./ECS-INTEGRATION.md) - Detailed ECS integration guide
- [ECS-OPENAPI.yaml](./api/ECS-OPENAPI.yaml) - OpenAPI specification for ECS endpoints
- [ARCHITECTURE.md](../ARCHITECTURE.md) - System architecture overview
- [QNXT API Documentation](https://qnxt-api-docs.example.com) - QNXT-specific documentation
