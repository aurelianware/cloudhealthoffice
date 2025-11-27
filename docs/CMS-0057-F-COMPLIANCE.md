# CMS-0057-F Compliance Guide

**Cloud Health Office** - Complete Prior Authorization Rule Implementation

This document provides comprehensive guidance on CMS-0057-F (Improving Interoperability Final Rule) compliance in Cloud Health Office, including FHIR API implementation, timeline requirements, and validation procedures.

---

## Table of Contents

1. [Overview](#overview)
2. [CMS-0057-F Requirements](#cms-0057-f-requirements)
3. [FHIR Mappers](#fhir-mappers)
4. [Compliance Checker](#compliance-checker)
5. [Timeline Requirements](#timeline-requirements)
6. [Implementation Guide](#implementation-guide)
7. [Testing and Validation](#testing-and-validation)
8. [Azure API for FHIR Integration](#azure-api-for-fhir-integration)

---

## Overview

The CMS-0057-F Final Rule (Improving Interoperability, March 2023) requires payers to implement:

1. **Prior Authorization APIs** using FHIR R4
2. **Patient Access APIs** for claims and coverage data
3. **Provider Access APIs** for payer-to-payer data exchange
4. **Specific response timelines** for authorization requests

Cloud Health Office provides complete implementation of these requirements through:
- Comprehensive X12 to FHIR R4 mappers
- Automated compliance validation
- Timeline checking and enforcement
- US Core IG and Da Vinci IG conformance

---

## CMS-0057-F Requirements

### Core APIs Required

#### 1. Prior Authorization API (Da Vinci PAS)
- **Standard**: Da Vinci Prior Authorization Support (PAS) IG
- **Resources**: ServiceRequest, Claim, ClaimResponse
- **Timeline**: 
  - Expedited: 72 hours
  - Standard: 7 calendar days
  - Life-threatening: 24 hours

#### 2. Patient Access API
- **Standard**: US Core IG v3.1.1+
- **Resources**: Patient, Claim, ExplanationOfBenefit, Coverage
- **Requirement**: 5 years of historical data

#### 3. Provider Access API
- **Standard**: US Core IG + Da Vinci PDex
- **Resources**: Patient, Coverage, Claim, ExplanationOfBenefit
- **Requirement**: Payer-to-payer data exchange

### Compliance Timeline

| Request Type | Response Deadline | CMS Rule |
|--------------|------------------|----------|
| Life-Threatening | 24 hours | CMS-0057-F §438.210(d) |
| Expedited | 72 hours | CMS-0057-F §438.210(c) |
| Standard | 7 calendar days | CMS-0057-F §438.210(b) |

---

## FHIR Mappers

Cloud Health Office provides four comprehensive FHIR mappers in `src/fhir/fhir-mapper.ts`:

### 1. X12 837 → FHIR Claim

Maps professional claims to FHIR R4 Claim resources.

```typescript
import { mapX12_837_ToFhirClaim, X12_837 } from './src/fhir/fhir-mapper';

const x12Claim: X12_837 = {
  claimId: 'CLM123456',
  transactionDate: '20240115-1430',
  totalClaimChargeAmount: 250.00,
  subscriber: {
    memberId: 'MEM001',
    firstName: 'John',
    lastName: 'Doe',
    dob: '19850615',
    gender: 'M',
  },
  billingProvider: {
    npi: '1234567890',
    organizationName: 'Boston Medical Group',
  },
  serviceLines: [
    {
      lineNumber: 1,
      procedureCode: '99213',
      serviceDate: '20240115',
      units: 1,
      chargeAmount: 150.00,
    },
  ],
  diagnosisCodes: [
    { code: 'E11.9', type: 'ICD-10' },
  ],
  payerId: 'PAYER123',
};

const fhirClaim = mapX12_837_ToFhirClaim(x12Claim);
```

**Mapped Fields**:
- Patient demographics and identifiers
- Billing provider (NPI, Tax ID, organization)
- Service line items with CPT codes
- Diagnosis codes (ICD-10)
- Total charges and currency
- US Core Claim profile compliance

### 2. X12 278 → FHIR ServiceRequest

Maps prior authorization requests to FHIR R4 ServiceRequest resources (Da Vinci PAS).

```typescript
import { mapX12_278_ToFhirServiceRequest, X12_278 } from './src/fhir/fhir-mapper';

const x12Auth: X12_278 = {
  requestId: 'REQ789012',
  requestType: 'AR', // Authorization Request
  serviceTypeCode: '48', // Hospital - Inpatient
  patient: {
    memberId: 'MEM002',
    firstName: 'Jane',
    lastName: 'Smith',
    dob: '19900321',
  },
  requester: {
    npi: '9876543210',
    organizationName: 'City Hospital',
  },
  serviceDetails: {
    admissionDate: '20240120',
    dischargeDate: '20240125',
    diagnosisCode: 'S72.001A',
    requestedQuantity: 5,
    quantityUnit: 'DA', // Days
  },
  payerId: 'PAYER456',
};

const fhirServiceRequest = mapX12_278_ToFhirServiceRequest(x12Auth);
```

**Mapped Fields**:
- Patient reference and demographics
- Requester (provider) with NPI
- Service type codes (X12 to FHIR)
- Occurrence period (admission/discharge dates)
- Requested quantity and units
- Supporting information (diagnosis, procedure)
- Da Vinci PAS profile compliance

### 3. X12 835 → FHIR ExplanationOfBenefit

Maps remittance advice to FHIR R4 ExplanationOfBenefit resources.

```typescript
import { mapX12_835_ToFhirExplanationOfBenefit, X12_835 } from './src/fhir/fhir-mapper';

const x12Remit: X12_835 = {
  transactionId: 'TXN123456',
  paymentDate: '2024-01-20',
  paymentAmount: 180.00,
  paymentMethod: 'ACH',
  payer: {
    id: 'PAYER789',
    name: 'National Health Plan',
  },
  payee: {
    npi: '1234567890',
    organizationName: 'Boston Medical Group',
  },
  claims: [
    {
      claimId: 'CLM123456',
      claimStatusCode: '1',
      chargedAmount: 250.00,
      paidAmount: 180.00,
      patientResponsibility: 50.00,
      patient: {
        firstName: 'John',
        lastName: 'Doe',
        memberId: 'MEM001',
      },
      serviceLines: [
        {
          procedureCode: '99213',
          serviceDate: '20240115',
          chargedAmount: 150.00,
          paidAmount: 120.00,
          adjustmentReasonCodes: [
            { groupCode: 'CO', reasonCode: '45', amount: 30.00 },
          ],
        },
      ],
    },
  ],
};

const fhirEOB = mapX12_835_ToFhirExplanationOfBenefit(x12Remit, 0);
```

**Mapped Fields**:
- Patient information
- Payer and provider identifiers
- Payment information (date, amount, method)
- Service line items with adjudication
- Total amounts (submitted, benefit, patient responsibility)
- Adjustment reason codes
- US Core ExplanationOfBenefit profile compliance

### 4. X12 270 → FHIR Patient + CoverageEligibilityRequest

Already implemented in `src/fhir/fhirEligibilityMapper.ts` (see [FHIR-INTEGRATION.md](./FHIR-INTEGRATION.md)).

---

## Compliance Checker

The `CMS0057FComplianceChecker` class provides automated validation of FHIR resources against CMS-0057-F requirements.

### Basic Usage

```typescript
import { 
  CMS0057FComplianceChecker, 
  createComplianceChecker 
} from './src/fhir/compliance-checker';

const checker = createComplianceChecker();

// Validate a ServiceRequest
const result = checker.validateServiceRequest(serviceRequest);

console.log(`Compliant: ${result.compliant}`);
console.log(`Score: ${result.score}/100`);
console.log(`Issues: ${result.issues.length}`);

result.issues.forEach(issue => {
  console.log(`[${issue.severity}] ${issue.rule}: ${issue.message}`);
  if (issue.suggestion) {
    console.log(`  Suggestion: ${issue.suggestion}`);
  }
});
```

### Validation Methods

#### validateServiceRequest()
Validates FHIR ServiceRequest against Da Vinci PAS requirements.

**Checks**:
- ✓ Resource type is ServiceRequest
- ✓ Da Vinci PAS profile in meta.profile
- ✓ Required fields: status, intent, code, subject, requester
- ✓ Insurance/coverage reference (warning)
- ✓ AuthoredOn timestamp (warning)

#### validateClaim()
Validates FHIR Claim against US Core requirements.

**Checks**:
- ✓ Resource type is Claim
- ✓ US Core Claim profile in meta.profile
- ✓ Required fields: status, type, use, patient, created, provider, insurer
- ✓ At least one service line item
- ✓ Diagnosis codes (warning)
- ✓ Total amount (warning)

#### validateExplanationOfBenefit()
Validates FHIR ExplanationOfBenefit against US Core requirements.

**Checks**:
- ✓ Resource type is ExplanationOfBenefit
- ✓ US Core EOB profile in meta.profile
- ✓ Required fields: status, type, use, patient, created, insurer, provider, outcome
- ✓ Payment information (warning)
- ✓ Total amounts (warning)

#### validatePatient()
Validates FHIR Patient against US Core requirements.

**Checks**:
- ✓ Resource type is Patient
- ✓ US Core Patient profile in meta.profile
- ✓ At least one identifier
- ✓ At least one name
- ✓ Gender specified

#### validateTimeline()
Validates response timeline compliance with CMS-0057-F requirements.

**Parameters**:
- `requestDate`: Authorization request timestamp
- `responseDate`: Authorization response timestamp
- `isExpedited`: Expedited request flag
- `isLifeThreatening`: Life-threatening situation flag

**Timeline Rules**:
- Life-threatening: ≤ 24 hours
- Expedited: ≤ 72 hours
- Standard: ≤ 7 calendar days

```typescript
const requestDate = new Date('2024-01-15T10:00:00Z');
const responseDate = new Date('2024-01-18T09:00:00Z');

const result = checker.validateTimeline(
  requestDate, 
  responseDate, 
  true, // isExpedited
  false // isLifeThreatening
);

// Result: Compliant (71 hours < 72 hours)
```

### Comprehensive Validation

Validate multiple resources together:

```typescript
const result = checker.validateCMSCompliance({
  serviceRequest: myServiceRequest,
  claim: myClaim,
  eob: myEOB,
  patient: myPatient,
  timeline: {
    requestDate: new Date('2024-01-15T10:00:00Z'),
    responseDate: new Date('2024-01-17T14:30:00Z'),
    isExpedited: true,
  },
});

console.log(`Overall Compliance: ${result.compliant}`);
console.log(`Overall Score: ${result.score}/100`);
console.log(`Rules Checked: ${result.checkedRules.length}`);
```

### Compliance Result Structure

```typescript
interface ComplianceResult {
  compliant: boolean;           // Overall compliance status
  issues: ComplianceIssue[];    // List of validation issues
  summary: string;              // Human-readable summary
  checkedRules: string[];       // Rules that were checked
  score: number;                // Compliance score (0-100)
}

interface ComplianceIssue {
  severity: 'error' | 'warning' | 'information';
  rule: string;                 // Rule identifier (e.g., 'SR-001')
  message: string;              // Issue description
  location?: string;            // FHIR path to issue
  suggestion?: string;          // How to fix the issue
}
```

---

## Timeline Requirements

### Implementation

Cloud Health Office automatically tracks authorization timelines:

1. **Request Submission**: Timestamp recorded when X12 278 received
2. **Processing**: System processes and validates request
3. **Response Generation**: FHIR ServiceRequest created
4. **Timeline Validation**: Automatic check against CMS-0057-F deadlines
5. **Alerts**: Notification if approaching deadline

### Configuration

Configure timeline thresholds in your deployment:

```json
{
  "complianceSettings": {
    "timelines": {
      "lifeThreatening": {
        "hours": 24,
        "alertThreshold": 20
      },
      "expedited": {
        "hours": 72,
        "alertThreshold": 60
      },
      "standard": {
        "days": 7,
        "alertThreshold": 6
      }
    }
  }
}
```

### Monitoring

Use Application Insights queries to monitor timeline compliance:

```kusto
customEvents
| where name == "PriorAuthTimeline"
| extend requestType = tostring(customDimensions.requestType)
| extend hoursElapsed = todouble(customDimensions.hoursElapsed)
| extend compliant = tobool(customDimensions.compliant)
| summarize 
    TotalRequests = count(),
    CompliantRequests = countif(compliant == true),
    AvgHours = avg(hoursElapsed)
  by requestType
```

---

## Implementation Guide

### Step 1: Install Dependencies

```bash
npm install @types/fhir fhir.js
```

### Step 2: Import Mappers

```typescript
import {
  mapX12_837_ToFhirClaim,
  mapX12_278_ToFhirServiceRequest,
  mapX12_835_ToFhirExplanationOfBenefit,
} from './src/fhir/fhir-mapper';

import { createComplianceChecker } from './src/fhir/compliance-checker';
```

### Step 3: Transform X12 to FHIR

```typescript
// Receive X12 278 prior authorization request
const x12Auth: X12_278 = parseX12Message(incomingEDI);

// Transform to FHIR ServiceRequest
const serviceRequest = mapX12_278_ToFhirServiceRequest(x12Auth);

// Validate compliance
const checker = createComplianceChecker();
const result = checker.validateServiceRequest(serviceRequest);

if (!result.compliant) {
  console.error('Compliance issues:', result.issues);
  // Handle non-compliant resource
}
```

### Step 4: Store in Azure API for FHIR

```typescript
import { FhirClient } from 'fhir.js';

const fhirClient = new FhirClient({
  baseUrl: process.env.AZURE_FHIR_ENDPOINT,
  credentials: managedIdentityToken,
});

// Create ServiceRequest in FHIR server
const response = await fhirClient.create({
  resourceType: 'ServiceRequest',
  body: serviceRequest,
});

console.log('Created:', response.id);
```

### Step 5: Monitor Timeline Compliance

```typescript
const requestDate = new Date(serviceRequest.authoredOn!);
const responseDate = new Date();

const timelineResult = checker.validateTimeline(
  requestDate,
  responseDate,
  isExpedited(serviceRequest),
  isLifeThreatening(serviceRequest)
);

if (!timelineResult.compliant) {
  // Alert: Response deadline exceeded
  await sendComplianceAlert(timelineResult);
}
```

---

## Testing and Validation

### Unit Tests

Cloud Health Office includes 57+ comprehensive unit tests for CMS-0057-F features (FHIR mapping and compliance checking), plus additional integration and legacy module tests for full platform coverage:

```bash
# Run all FHIR tests
npm run test:fhir

# Run specific test suites
npx jest src/fhir/__tests__/fhir-mapper.test.ts
npx jest src/fhir/__tests__/compliance-checker.test.ts
```

### Integration Testing

Test complete workflows:

```typescript
// Test X12 → FHIR → Validation → Azure FHIR
describe('Prior Authorization Workflow', () => {
  it('processes X12 278 to FHIR and validates', async () => {
    // 1. Parse X12
    const x12 = parseX12_278(sampleEDI);
    
    // 2. Transform to FHIR
    const serviceRequest = mapX12_278_ToFhirServiceRequest(x12);
    
    // 3. Validate compliance
    const checker = createComplianceChecker();
    const result = checker.validateServiceRequest(serviceRequest);
    expect(result.compliant).toBe(true);
    
    // 4. Post to Azure FHIR
    const response = await fhirClient.create({
      resourceType: 'ServiceRequest',
      body: serviceRequest,
    });
    expect(response.id).toBeDefined();
    
    // 5. Verify timeline
    const timelineResult = checker.validateTimeline(
      new Date(x12.transactionDate),
      new Date(),
      false,
      false
    );
    expect(timelineResult.compliant).toBe(true);
  });
});
```

### Compliance Validation

Use the compliance checker in CI/CD:

```bash
# Validate all generated FHIR resources
node dist/scripts/validate-fhir-compliance.js
```

---

## Azure API for FHIR Integration

### Setup

1. **Deploy Azure API for FHIR**
   ```bash
   az healthcareapis service create \
     --resource-group my-rg \
     --resource-name my-fhir-service \
     --kind fhir-R4 \
     --location eastus
   ```

2. **Configure Managed Identity**
   ```bash
   az webapp identity assign \
     --resource-group my-rg \
     --name my-logic-app
   
   az role assignment create \
     --assignee <logic-app-identity> \
     --role "FHIR Data Contributor" \
     --scope <fhir-service-id>
   ```

3. **Enable FHIR Validation**
   
   Azure API for FHIR provides $validate operation:
   
   ```typescript
   const validateResult = await fhirClient.operation({
     resourceType: 'ServiceRequest',
     name: 'validate',
     method: 'POST',
     body: serviceRequest,
   });
   ```

### Production Deployment

```typescript
import { validateWithAzureFHIR } from './src/fhir/compliance-checker';

// Validate against Azure API for FHIR
const result = await validateWithAzureFHIR(
  serviceRequest,
  process.env.AZURE_FHIR_ENDPOINT
);

if (!result.compliant) {
  console.error('FHIR validation failed:', result.issues);
}
```

---

## Support and Resources

### Documentation
- [FHIR Integration Guide](./FHIR-INTEGRATION.md)
- [Architecture Overview](../ARCHITECTURE.md)
- [Security Hardening](../SECURITY-HARDENING.md)

### Standards References
- [CMS-0057-F Final Rule](https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-and-prior-authorization-final-rule-cms-0057-f)
- [Da Vinci PAS IG](http://hl7.org/fhir/us/davinci-pas/)
- [US Core IG v3.1.1](http://hl7.org/fhir/us/core/STU3.1.1/)
- [FHIR R4 Specification](https://hl7.org/fhir/R4/)

### Support
- **Issues**: [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
- **Discussions**: [GitHub Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions)

---

**Last Updated**: November 2024  
**Version**: 1.0.0  
**CMS-0057-F Status**: Fully Compliant
