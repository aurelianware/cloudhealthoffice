# ValueAdds277 Testing Guide

## Overview

This guide provides comprehensive testing procedures for validating the ValueAdds277 enhanced response fields in the ECS Summary Search workflow.

## Prerequisites

- Access to DEV/UAT environment
- Valid Azure AD authentication token
- Test payer configured with ValueAdds277 support
- Test claims in QNXT system covering all scenarios

## Test Scenarios

### 1. Paid Claim with Complete ValueAdds277 Data

**Objective:** Verify all ValueAdds277 fields populate correctly for a paid claim.

**Test Data:**
- Use example: `docs/examples/ecs-valueadds277-paid-claim.json`
- Claim Status: Paid
- Expected: All financial fields populated, eligibleForRemittanceViewer=true

**Request:**
```bash
curl -X POST \
  -H "Authorization: Bearer ${AZURE_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{
    "searchMethod": "ClaimHistory",
    "requestId": "TEST-PAID-001",
    "submitterId": "TEST-PROV",
    "claimHistorySearch": {
      "claimNumber": "CLM123456789"
    }
  }' \
  https://${LOGIC_APP_URL}/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke
```

**Validation Checklist:**
- [ ] Response status is 200 OK
- [ ] `status` field is "success"
- [ ] `claims` array has at least one claim
- [ ] `BILLED` field is populated (REQUIRED)
- [ ] `effectiveDate` field is populated (REQUIRED)
- [ ] All financial fields present: ALLOWED, INSURANCE_TOTAL_PAID, PATIENT_RESPONSIBILITY, COPAY, COINSURANCE, DEDUCTIBLE, DISCOUNT
- [ ] Clinical fields present: drgCode, facilityTypeCodeDescription, diagnosisCodes
- [ ] Demographic objects present: patient, subscriber, billingProvider, renderingProvider
- [ ] Remittance fields present: payeeName, checkAmount, checkCashedDate
- [ ] Service lines array populated with financial breakdowns
- [ ] `eligibleForAppeal` is false
- [ ] `eligibleForRemittanceViewer` is true
- [ ] `eligibleForAttachment` is false
- [ ] Response time < 3 seconds

### 2. Denied Claim with Appeal Eligibility

**Objective:** Verify appeal integration flags and metadata populate correctly.

**Test Data:**
- Use example: `docs/examples/ecs-valueadds277-denied-claim.json`
- Claim Status: Denied
- Expected: eligibleForAppeal=true, appeal metadata populated

**Request:**
```bash
curl -X POST \
  -H "Authorization: Bearer ${AZURE_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{
    "searchMethod": "Member",
    "requestId": "TEST-DENIED-001",
    "submitterId": "TEST-PROV",
    "memberSearch": {
      "memberId": "M789012",
      "serviceFromDate": "20231215",
      "serviceToDate": "20231215"
    }
  }' \
  https://${LOGIC_APP_URL}/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke
```

**Validation Checklist:**
- [ ] Response status is 200 OK
- [ ] Claim status is "Denied"
- [ ] `eligibleForAppeal` is **true**
- [ ] `appealMessage` is populated with timely filing deadline
- [ ] `appealType` is populated (e.g., "Reconsideration")
- [ ] `appealTimelyFilingDate` is populated (CCYYMMDD format)
- [ ] Timely filing date is in the future (not expired)
- [ ] `eligibleForCorrection` is true
- [ ] `eligibleForRemittanceViewer` is false
- [ ] Denial reason codes present in `code` or `reasonCodes`
- [ ] `comment` field explains denial reason
- [ ] Financial amounts show $0 paid
- [ ] Response validates against schema

### 3. Pending Claim with Attachment Eligibility

**Objective:** Verify attachment integration flag for pending claims.

**Test Data:**
- Use example: `docs/examples/ecs-valueadds277-pending-claim.json`
- Claim Status: Pending
- Expected: eligibleForAttachment=true

**Request:**
```bash
curl -X POST \
  -H "Authorization: Bearer ${AZURE_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{
    "searchMethod": "ServiceDate",
    "requestId": "TEST-PENDING-001",
    "submitterId": "TEST-PROV",
    "serviceDateSearch": {
      "serviceFromDate": "20240102",
      "serviceToDate": "20240105",
      "providerId": "5550100010",
      "providerIdQualifier": "NPI"
    }
  }' \
  https://${LOGIC_APP_URL}/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke
```

**Validation Checklist:**
- [ ] Response status is 200 OK
- [ ] Claim status is "Pending"
- [ ] `eligibleForAttachment` is **true**
- [ ] `eligibleForAppeal` is false
- [ ] `comment` field indicates additional documentation needed
- [ ] Financial amounts may be null (not yet adjudicated)
- [ ] Service lines present with procedure codes
- [ ] Response time < 3 seconds

### 4. Configuration Parameter Testing

**Objective:** Verify field groups can be disabled via parameters.

**Test 4a: Disable Financial Fields**

Modify workflow parameters:
```json
{
  "includeFinancialFields": false
}
```

**Expected:**
- [ ] `BILLED`, `ALLOWED`, etc. fields are null or absent
- [ ] Original `billedAmount`, `paidAmount` still present (backward compatibility)

**Test 4b: Disable Integration Flags**

Modify workflow parameters:
```json
{
  "includeIntegrationFlags": false
}
```

**Expected:**
- [ ] All `eligibleFor*` fields are null or absent
- [ ] Appeal metadata fields are null

**Test 4c: Disable Service Line Details**

Modify workflow parameters:
```json
{
  "includeServiceLineDetails": false
}
```

**Expected:**
- [ ] `serviceLines` array is null or absent

### 5. Integration Flag Logic Testing

**Test Integration Flag Calculation:**

| Claim Status | eligibleForAppeal | eligibleForAttachment | eligibleForCorrection | eligibleForRemittanceViewer |
|--------------|-------------------|----------------------|----------------------|----------------------------|
| Paid | false | false | false | **true** |
| Partially Paid | **true** | false | false | **true** |
| Denied | **true** | false | **true** | false |
| Pending | false | **true** | false | false |
| In Process | false | **true** | false | false |
| Suspended | false | **true** | false | false |

**Validation:**
Query claims with each status and verify flags match table above.

### 6. Backward Compatibility Testing

**Objective:** Verify existing ECS queries continue to work.

**Test Request (Standard ECS, no ValueAdds277):**
```bash
curl -X POST \
  -H "Authorization: Bearer ${AZURE_TOKEN}" \
  -H "Content-Type: application/json" \
  -d '{
    "searchMethod": "ServiceDate",
    "requestId": "BACKWARD-COMPAT-001",
    "submitterId": "LEGACY-PROV",
    "serviceDateSearch": {
      "serviceFromDate": "20240101",
      "serviceToDate": "20240131",
      "providerId": "1234567890"
    }
  }' \
  https://${LOGIC_APP_URL}/api/ecs_summary_search/triggers/HTTP_ECS_Summary_Search_Request/invoke
```

**Validation Checklist:**
- [ ] Response status is 200 OK
- [ ] All original ECS fields present: claimNumber, claimStatus, billedAmount, paidAmount, etc.
- [ ] ValueAdds277 fields may be present but don't break response
- [ ] Client ignoring new fields still gets valid data
- [ ] No breaking changes to request schema

### 7. Performance Testing

**Objective:** Verify ValueAdds277 doesn't degrade performance significantly.

**Test Setup:**
- Query 50 claims via Service Date search
- Measure response time and size

**Metrics:**
- [ ] Average response time: < 3 seconds
- [ ] 95th percentile: < 5 seconds
- [ ] Response size increase: 2-3x (expected)
- [ ] No timeout errors
- [ ] Memory usage acceptable

**Load Test:**
```bash
# Use Apache Bench or similar tool
ab -n 100 -c 10 -p request.json -T application/json \
  -H "Authorization: Bearer ${AZURE_TOKEN}" \
  https://${LOGIC_APP_URL}/api/ecs_summary_search/...
```

### 8. Error Handling Testing

**Test 8a: Invalid Configuration**
- Set `includeFinancialFields` to invalid value
- Expected: Use default (true) or return validation error

**Test 8b: Missing Required Field (BILLED)**
- QNXT response missing `billedAmount` or `BILLED`
- Expected: Fallback to other field or null, no workflow failure

**Test 8c: Invalid Appeal Date Calculation**
- Claim has no `finalizedDate` or `processedDate`
- Expected: Use current date + 180 days or set to null

### 9. Schema Validation

**Validate Response Against Schema:**

```bash
# Install ajv-cli if needed
npm install -g ajv-cli

# Validate response
ajv validate \
  -s schemas/ECS-SummarySearch-Response.json \
  -d test-response.json
```

**Expected:**
- [ ] All responses validate against schema
- [ ] Required fields present
- [ ] Data types correct
- [ ] Enums have valid values

### 10. Appeals Integration Testing

**Objective:** Verify Appeals module can consume ECS data.

**Test Flow:**
1. Query denied claim via ECS
2. Verify `eligibleForAppeal` is true
3. Extract `appealTimelyFilingDate`, `appealType`, `appealMessage`
4. Pass to Appeals module
5. Verify appeal can be initiated

**Integration Points:**
- [ ] Appeal metadata correctly formatted
- [ ] Timely filing date is valid date string (CCYYMMDD)
- [ ] Appeal type is recognized by Appeals module
- [ ] Claim number and metadata pass through correctly

## Test Data Requirements

### Paid Claim Test Data
- Claim with status: Paid
- Complete financial breakdown
- Check number and remittance details
- Multiple service lines
- Complete demographics

### Denied Claim Test Data
- Claim with status: Denied
- Denial reason codes
- No payment amounts
- Finalized within last 180 days (for timely filing)

### Pending Claim Test Data
- Claim with status: Pending or In Process
- No finalized amounts
- Request for additional documentation

## Automated Testing Recommendations

While this repository has no test framework, consider:

1. **API Testing Framework**
   - Postman collections with pre-defined test cases
   - Newman for CI/CD integration
   - Automated schema validation

2. **Integration Tests**
   - Mock QNXT backend for consistent test data
   - Test all search methods with ValueAdds277
   - Verify configuration parameters

3. **Contract Testing**
   - Pact or similar for consumer-driven contracts
   - Verify Appeals module can consume ECS responses
   - Validate Attachments module integration

4. **Performance Tests**
   - JMeter or Gatling for load testing
   - Monitor response times and throughput
   - Test with large result sets (100+ claims)

## Monitoring in Production

After deployment, monitor these metrics:

```kusto
// Application Insights Query - Response Times
requests
| where name contains "ECS_Summary_Search"
| summarize 
    avg(duration), 
    percentile(duration, 95), 
    percentile(duration, 99) 
  by bin(timestamp, 1h)
| render timechart

// Field Population Rate
traces
| where customDimensions.EventName == "ECSSearchSuccess"
| extend hasValueAdds277 = customDimensions.includeFinancialFields == "true"
| summarize count() by hasValueAdds277
| render piechart

// Integration Flag Usage
traces
| where message contains "eligibleFor"
| extend flagName = extract("(eligibleFor\\w+)", 1, message)
| summarize count() by flagName
| render barchart
```

## Troubleshooting Common Issues

### Issue 1: ValueAdds277 Fields Not Populating

**Symptoms:** Response has standard fields but ValueAdds277 fields are null

**Possible Causes:**
- Configuration parameters disabled
- QNXT backend not returning enhanced data
- Field mapping incorrect

**Resolution:**
1. Check workflow parameters: `includeFinancialFields`, etc.
2. Verify QNXT API returns expected fields
3. Review Application Insights for mapping errors

### Issue 2: Integration Flags Always False

**Symptoms:** All `eligibleFor*` flags are false regardless of claim status

**Possible Causes:**
- `includeIntegrationFlags` parameter is false
- Claim status mapping incorrect
- Logic expression error in workflow

**Resolution:**
1. Check `includeIntegrationFlags` parameter
2. Verify claim status values match expected enums
3. Review workflow logic for flag calculation

### Issue 3: Appeal Metadata Missing

**Symptoms:** `eligibleForAppeal` is true but `appealMessage` is null

**Possible Causes:**
- `includeAppealMetadata` configuration is false
- Date fields missing for timely filing calculation
- Expression error in workflow

**Resolution:**
1. Check appeal configuration
2. Verify `finalizedDate` or `processedDate` exists
3. Review Application Insights for expression errors

## References

- [ECS-INTEGRATION.md](./ECS-INTEGRATION.md) - Complete ValueAdds277 documentation
- [APPEALS-INTEGRATION.md](./APPEALS-INTEGRATION.md) - Appeals integration details
- Example responses: `docs/examples/ecs-valueadds277-*.json`
- Configuration schema: `config/schemas/availity-integration-config.schema.json`
- OpenAPI specification: `docs/api/ECS-OPENAPI.yaml`
