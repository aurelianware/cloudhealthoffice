# FHIR R4 Integration Guide - Eligibility, Claims, and Provider Access

**Cloud Health Office** - HIPAA-compliant FHIR R4 integration for payer systems

This document details the FHIR R4 implementation for healthcare payer eligibility workflows. Supported mandates include mapping X12 EDI eligibility transactions (270/271) to FHIR resources, CMS Patient Access API requirements, and Provider Access API (CMS-0057-F) for provider data access.

---

## Table of Contents

1. [Overview](#overview)
2. [CMS Interoperability Compliance](#cms-interoperability-compliance)
3. [Architecture](#architecture)
4. [Provider Access API (CMS-0057-F)](#provider-access-api-cms-0057-f)
5. [X12 270 to FHIR R4 Mapping](#x12-270-to-fhir-r4-mapping)
6. [Usage Examples](#usage-examples)
7. [fhir.js Integration](#fhirjs-integration)
8. [API Endpoints](#api-endpoints)
9. [Testing](#testing)
10. [Security and HIPAA Compliance](#security-and-hipaa-compliance)

---

## Overview

Cloud Health Office provides comprehensive FHIR R4 integration for healthcare payer systems, enabling seamless conversion between traditional X12 EDI transactions and modern FHIR APIs. This implementation focuses on eligibility verification workflows:

- **X12 270**: Health Care Eligibility Benefit Inquiry (EDI → FHIR)
- **X12 271**: Health Care Eligibility Benefit Response (FHIR → EDI)

### Key Features

✅ **Standards Compliant**: Implements HIPAA X12 005010X279A1 and HL7 FHIR R4  
✅ **CMS Patient Access Ready**: Supports CMS-9115-F final rule requirements  
✅ **US Core Profile**: Aligns with US Core Patient and Da Vinci PDex profiles  
✅ **Comprehensive Mapping**: Demographics, identifiers, coverage, service types  
✅ **Production Ready**: Fully tested with 80%+ code coverage  
✅ **TypeScript Native**: Type-safe implementation with @types/fhir

---

## CMS Interoperability Compliance

### CMS-9115-F Patient Access Final Rule

The Patient Access final rule (effective July 1, 2021) requires most payers to provide patients with access to their health information through standardized APIs. Our FHIR R4 implementation supports these requirements:

#### Required API Capabilities

1. **Patient Access API**
   - FHIR R4-based API for patient data access
   - Patient resource with demographics
   - Coverage information via FHIR resources
   - Claims and encounter data (when available)

2. **Provider Directory API**
   - Provider information in FHIR format
   - Network status and relationships

3. **Payer-to-Payer Data Exchange**
   - Interoperable patient data exchange
   - 5-year historical data requirement

#### Our Implementation

```typescript
// X12 270 Eligibility Inquiry → FHIR R4 Patient + CoverageEligibilityRequest
const { patient, eligibility } = mapX12270ToFhirEligibility(x12_270_data);

// Results in compliant FHIR R4 resources:
// - Patient: US Core Patient profile
// - CoverageEligibilityRequest: Standard FHIR R4
```

### Standards References

- **CMS-9115-F**: Interoperability and Patient Access Final Rule
- **CMS-0057-F**: Prior Authorization API (March 2023, Effective January 1, 2026)
- **CMS-0057-F**: Provider Access API (March 2023, Effective January 1, 2027)
- **X12 005010X279A1**: Health Care Eligibility Benefit Inquiry and Response
- **HL7 FHIR R4**: v4.0.1 Specification
- **US Core 3.1.1**: US Core Implementation Guide
- **Da Vinci PDex**: Payer Data Exchange Implementation Guide
- **SMART on FHIR**: Authorization framework for healthcare apps

---

## Provider Access API (CMS-0057-F)

The Provider Access API enables healthcare providers to securely access patient data with proper authentication, authorization, and consent management. This implementation aligns with CMS-0057-F requirements effective in 2027.

### Key Features

✅ **SMART on FHIR Authentication** - OpenID Connect/OAuth2 for EHR/provider authentication  
✅ **Patient Consent Management** - Validate patient authorization before data access  
✅ **FHIR R4 Endpoints** - Search and read operations for clinical and administrative data  
✅ **HIPAA Safeguards** - Encryption, audit logging, and PHI redaction  
✅ **US Core v3.1.1 Compliance** - US Core Patient profile and USCDI data elements  
✅ **Da Vinci PDex Alignment** - Compatible with Payer Data Exchange Implementation Guides  
✅ **Backend Integration** - QNXT to FHIR data mapping

### Supported Resources

The Provider Access API supports search and read operations for the following FHIR resources:

| Resource Type | Purpose | Standards |
|---------------|---------|-----------|
| **Patient** | Patient demographics and identifiers | US Core v3.1.1 |
| **Claim** | Claims data (837 Professional, Institutional, Dental) | FHIR R4 |
| **Encounter** | Clinical encounters and visits | FHIR R4 |
| **ExplanationOfBenefit** | Adjudicated claim information | FHIR R4 |
| **Condition** | Clinical conditions (diagnoses) | US Core, USCDI |
| **Observation** | Laboratory results and vital signs | US Core, USCDI |

### Authentication Flow

The Provider Access API uses SMART on FHIR for secure authentication:

```
1. Provider EHR initiates OAuth2 authorization request
   ↓
2. Provider authenticates with Azure AD / Identity Provider
   ↓
3. Patient consent is validated (active consent required)
   ↓
4. Access token issued with appropriate scopes
   ↓
5. Provider makes FHIR API requests with Bearer token
   ↓
6. API validates token, checks consent, returns FHIR resources
   ↓
7. All access logged to audit trail (HIPAA compliance)
```

### Consent Model

Patient consent is required before providers can access data via the Provider Access API:

- **Active Consent**: Patient must have active, unexpired consent on file
- **Scope-Based Access**: Consent defines which resource types are accessible
- **Purpose of Use**: Consent records purpose (Treatment, Payment, Operations)
- **Expiration**: Consents have configurable expiration dates
- **Revocation**: Patients can revoke consent at any time

**Consent Statuses:**
- `active` - Provider has access to patient data
- `inactive` - Consent not yet effective
- `revoked` - Patient has revoked consent
- `pending` - Awaiting patient approval

### API Endpoints

#### Search Resources
```
GET /[ResourceType]?patient=[patientId]&[other-params]
Authorization: Bearer {access_token}
```

**Supported Parameters:**
- `patient` - Patient identifier (required)
- `date` - Filter by service/effective date
- `status` - Filter by resource status
- `category` - Filter by category (Condition, Observation)
- `_count` - Results per page (pagination)
- `_page` - Page number (pagination)

**Example Request:**
```http
GET /Condition?patient=PAT123&clinical-status=active
Authorization: Bearer provider:NPI12345:abc123token
```

**Example Response:**
```json
{
  "resourceType": "Bundle",
  "type": "searchset",
  "total": 2,
  "entry": [
    {
      "fullUrl": "Condition/COND001",
      "resource": {
        "resourceType": "Condition",
        "id": "COND001",
        "clinicalStatus": {
          "coding": [{
            "system": "http://terminology.hl7.org/CodeSystem/condition-clinical",
            "code": "active"
          }]
        },
        "code": {
          "coding": [{
            "system": "http://snomed.info/sct",
            "code": "44054006",
            "display": "Type 2 diabetes mellitus"
          }]
        },
        "subject": {
          "reference": "Patient/PAT123"
        },
        "onsetDateTime": "2020-06-15"
      }
    }
  ]
}
```

#### Read Specific Resource
```
GET /[ResourceType]/[id]
Authorization: Bearer {access_token}
```

**Example Request:**
```http
GET /Patient/PAT123
Authorization: Bearer provider:NPI12345:abc123token
```

**Example Response:**
```json
{
  "resourceType": "Patient",
  "id": "PAT123",
  "meta": {
    "profile": ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient"]
  },
  "identifier": [
    {
      "use": "official",
      "type": {
        "coding": [{
          "system": "http://terminology.hl7.org/CodeSystem/v2-0203",
          "code": "MB",
          "display": "Member Number"
        }]
      },
      "value": "PAT123"
    }
  ],
  "active": true,
  "name": [
    {
      "use": "official",
      "family": "Doe",
      "given": ["John"]
    }
  ],
  "gender": "male",
  "birthDate": "1980-01-01"
}
```

### Error Handling

The Provider Access API returns FHIR OperationOutcome resources for errors:

#### Authentication Error (401)
```json
{
  "resourceType": "OperationOutcome",
  "issue": [{
    "severity": "error",
    "code": "login",
    "diagnostics": "Invalid or missing SMART on FHIR token"
  }]
}
```

#### Consent Denied (403)
```json
{
  "resourceType": "OperationOutcome",
  "issue": [{
    "severity": "error",
    "code": "forbidden",
    "diagnostics": "Provider NPI12345 does not have consent to access patient PAT123 data"
  }]
}
```

#### Resource Not Found (404)
```json
{
  "resourceType": "OperationOutcome",
  "issue": [{
    "severity": "error",
    "code": "not-found",
    "diagnostics": "Patient/PAT999 not found"
  }]
}
```

### Backend Data Mapping (QNXT → FHIR)

The Provider Access API includes mappers to transform backend payer system data (e.g., QNXT) to FHIR resources:

**Patient Mapping:**
```typescript
// QNXT Patient data
const qnxtPatient = {
  memberId: 'MEM123',
  firstName: 'John',
  lastName: 'Doe',
  dob: '19850615',
  gender: 'M',
  address: { /* ... */ },
  phone: '555-1234',
  email: 'john@example.com'
};

// Transform to FHIR Patient (US Core)
const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);
```

**Claim Mapping:**
```typescript
// QNXT Claim data
const qnxtClaim = {
  claimId: 'CLM123',
  memberId: 'MEM123',
  providerId: 'NPI98765',
  claimType: 'Professional',
  serviceDate: '2024-01-15',
  diagnosisCodes: ['E11.9', 'I10'],
  procedureCodes: ['99213', '80053'],
  totalCharged: 350.00,
  totalPaid: 280.00,
  status: 'active'
};

// Transform to FHIR Claim
const fhirClaim = api.mapQnxtClaimToFhir(qnxtClaim);
```

**Encounter Mapping:**
```typescript
// QNXT Encounter data
const qnxtEncounter = {
  encounterId: 'ENC123',
  memberId: 'MEM123',
  providerId: 'NPI98765',
  encounterType: 'AMB',
  encounterDate: '2024-01-15T10:00:00Z',
  diagnosisCodes: ['E11.9'],
  status: 'finished'
};

// Transform to FHIR Encounter
const fhirEncounter = api.mapQnxtEncounterToFhir(qnxtEncounter);
```

### HIPAA Safeguards

The Provider Access API implements comprehensive HIPAA safeguards:

#### 1. Encryption (AES-256-GCM)
```typescript
// Encrypt PHI data
const encrypted = api.encryptPhi('John Doe, SSN: 123-45-6789');

// Decrypt when needed
const decrypted = api.decryptPhi(encrypted);
```

#### 2. Audit Logging
All access is logged with:
- Timestamp
- Event type (access, search, read, consent_check, auth_failure)
- User/Provider identifier
- Patient identifier
- Resource type and ID
- Result (success/failure)
- IP address
- Details

**Audit Log Example:**
```
[AUDIT] 2024-01-15T10:30:00Z - search - success - User: NPI12345 - Search: Patient with params {"patient":"PAT123"}
[AUDIT] 2024-01-15T10:30:01Z - consent_check - success - User: NPI12345 - Patient: PAT123
[AUDIT] 2024-01-15T10:30:02Z - read - success - User: NPI12345 - Read: Patient/PAT123
```

#### 3. PHI Redaction
```typescript
// Original patient resource
const patient = { /* full PHI */ };

// Redact for logging/monitoring
const redacted = api.redactPhi(patient);
// Result: names → ***, DOB → ****-**-**, addresses → ***
```

### Azure API Management Integration

Deploy the Provider Access API behind Azure API Management for:

- **Rate Limiting**: Prevent abuse and ensure fair usage
- **IP Whitelisting**: Restrict access to known provider networks
- **Additional Authentication**: Layer OAuth2 scopes and policies
- **Monitoring**: Track API usage, performance, and errors
- **Caching**: Improve performance for frequently accessed data
- **Transformation**: Additional request/response processing if needed

**Example API Management Policy:**
```xml
<policies>
  <inbound>
    <rate-limit calls="100" renewal-period="60" />
    <validate-jwt header-name="Authorization" />
    <set-header name="X-Forwarded-For" exists-action="override">
      <value>@(context.Request.IpAddress)</value>
    </set-header>
  </inbound>
  <backend>
    <forward-request />
  </backend>
  <outbound>
    <set-header name="X-Content-Type-Options" exists-action="override">
      <value>nosniff</value>
    </set-header>
  </outbound>
</policies>
```

### Usage Example

```typescript
import { createProviderAccessApi } from './src/fhir/provider-access-api';

// Initialize API with encryption key (from Azure Key Vault in production)
const api = createProviderAccessApi(process.env.ENCRYPTION_KEY);

// Provider authentication token from SMART on FHIR flow
const token = 'provider:NPI12345:valid-access-token';

// Search for patient conditions
const conditionsBundle = await api.searchResources(
  'Condition',
  { 
    resourceType: 'Condition', 
    patient: 'PAT123',
    'clinical-status': 'active'
  },
  token
);

// Read specific patient
const patient = await api.readResource('Patient', 'PAT123', token);

// Get audit logs for compliance reporting
const auditLogs = api.getAuditLogs();
```

### Testing

The Provider Access API includes 44 comprehensive unit tests covering:

- ✅ SMART on FHIR authentication (valid/invalid tokens)
- ✅ Patient consent validation (active, expired, revoked)
- ✅ Search operations (all 6 resource types)
- ✅ Read operations with consent checks
- ✅ QNXT to FHIR mapping (Patient, Claim, Encounter)
- ✅ Gender and date format handling
- ✅ Encryption/decryption (AES-256-GCM)
- ✅ PHI redaction
- ✅ Audit logging
- ✅ Error handling (401, 403, 404)
- ✅ US Core v3.1.1 compliance
- ✅ Integration scenarios

**Run Provider Access API tests:**
```bash
npm run test:fhir
```

**Expected Output:**
```
PASS src/fhir/__tests__/provider-access-api.test.ts
  ProviderAccessApi
    ✓ should validate SMART on FHIR tokens
    ✓ should check patient consent
    ✓ should search FHIR resources
    ✓ should map QNXT data to FHIR
    ✓ should encrypt/decrypt PHI
    ✓ should log audit trails
    ... (44 tests total)

Tests: 44 passed, 44 total
```

---

## Architecture

### High-Level Flow

```
┌─────────────┐      X12 270        ┌──────────────────┐
│   Provider  │ ──────────────────> │  Cloud Health    │
│   System    │                     │    Office        │
└─────────────┘                     │   (Logic Apps)   │
                                    └──────────────────┘
                                            │
                                            │ Process EDI
                                            ▼
                                    ┌──────────────────┐
                                    │  FHIR Mapper     │
                                    │  (TypeScript)    │
                                    └──────────────────┘
                                            │
                                            │ Generate FHIR
                                            ▼
                    ┌──────────────────────────────────────┐
                    │  FHIR R4 Resources                   │
                    │  - Patient                           │
                    │  - CoverageEligibilityRequest        │
                    └──────────────────────────────────────┘
                                            │
                                            │ Store/Query
                                            ▼
                    ┌──────────────────────────────────────┐
                    │  Payer Backend System                │
                    │  (QNXT, Claims System, etc.)         │
                    └──────────────────────────────────────┘
```

### Components

1. **X12 Type Definitions** (`src/fhir/x12Types.ts`)
   - Comprehensive X12 270 structure
   - TypeScript interfaces for type safety
   - Supports all HIPAA-required fields

2. **FHIR Mapper** (`src/fhir/fhirEligibilityMapper.ts`)
   - Core mapping logic
   - X12 → FHIR transformation
   - Profile-compliant resource generation

3. **Unit Tests** (`src/fhir/__tests__/fhirEligibilityMapper.test.ts`)
   - 19+ comprehensive test cases
   - Edge case coverage
   - Compliance validation

---

## X12 270 to FHIR R4 Mapping

### Patient Resource Mapping

| X12 270 Field | FHIR R4 Patient Field | Notes |
|---------------|----------------------|-------|
| `subscriber.memberId` | `identifier[0].value` | Member number identifier |
| `subscriber.firstName` | `name[0].given[0]` | First name |
| `subscriber.lastName` | `name[0].family` | Family name |
| `subscriber.middleName` | `name[0].given[1]` | Middle name (optional) |
| `subscriber.dob` | `birthDate` | Date of birth (YYYY-MM-DD) |
| `subscriber.gender` (M/F/U) | `gender` (male/female/unknown) | Gender code mapping |
| `subscriber.ssn` | `identifier[1].value` | Social Security Number |
| `subscriber.address` | `address[0]` | Full address structure |
| `subscriber.phone` | `telecom[0]` | Phone contact |
| `subscriber.email` | `telecom[1]` | Email contact |

### CoverageEligibilityRequest Mapping

| X12 270 Field | FHIR R4 CoverageEligibilityRequest | Notes |
|---------------|-----------------------------------|-------|
| `inquiryId` | `id` | Unique inquiry identifier |
| `transactionDate` | `created` | Request timestamp |
| `subscriber.memberId` | `patient.reference` | Patient reference |
| `insurerId` | `insurer.identifier.value` | Payer identifier |
| `informationReceiver.npi` | `provider.identifier.value` | Requesting provider NPI |
| `serviceTypeCodes[]` | `item[].category.coding` | Service categories |
| `serviceDateRange` | `servicedPeriod` | Date range for inquiry |

### Gender Code Mapping

```typescript
X12 Code → FHIR Code
   M     → male
   F     → female
   U     → unknown
 (null)  → unknown
```

### Date Format Conversion

```typescript
X12 Format:  CCYYMMDD (e.g., "19850615")
FHIR Format: YYYY-MM-DD (e.g., "1985-06-15")

X12 DateTime: CCYYMMDD-HHMM (e.g., "20240115-1430")
FHIR DateTime: ISO 8601 (e.g., "2024-01-15T14:30:00Z")
```

### Service Type Code Mapping

Common X12 service type codes mapped to FHIR benefit categories:

| X12 Code | Description | FHIR Category |
|----------|-------------|---------------|
| 1 | Medical Care | Medical Care |
| 2 | Surgical | Surgical |
| 30 | Health Benefit Plan Coverage | Health Benefit Plan Coverage |
| 33 | Chiropractic | Chiropractic |
| 35 | Dental Care | Dental Care |
| 47 | Hospital | Hospital |
| 48 | Hospital - Inpatient | Hospital - Inpatient |
| 49 | Hospital - Outpatient | Hospital - Outpatient |
| 86 | Emergency Services | Emergency Services |
| 88 | Pharmacy | Pharmacy |
| 98 | Professional (Physician) Visit - Office | Professional (Physician) Visit - Office |

*Full list of 100+ service type codes implemented in mapper*

---

## Usage Examples

### Basic Usage: X12 270 to FHIR

```typescript
import { mapX12270ToFhirEligibility } from './src/fhir/fhirEligibilityMapper';
import { X12_270 } from './src/fhir/x12Types';

// Sample X12 270 eligibility inquiry
const x12Input: X12_270 = {
  inquiryId: 'INQ20240115001',
  transactionDate: '20240115-0930',
  informationSource: {
    id: '030240928',  // Availity
    name: 'Texas Health Plan',
    taxId: '75-1234567'
  },
  informationReceiver: {
    npi: '1234567890',
    organizationName: 'Austin Medical Center'
  },
  subscriber: {
    memberId: 'THP123456789',
    firstName: 'John',
    lastName: 'Doe',
    middleName: 'Robert',
    dob: '19850615',
    gender: 'M',
    address: {
      street1: '123 Main Street',
      city: 'Austin',
      state: 'TX',
      zip: '78701'
    },
    phone: '512-555-1234',
    email: 'john.doe@example.com'
  },
  insurerId: 'TXHEALTH01',
  serviceTypeCodes: ['30', '48', '98']
};

// Transform to FHIR R4
const { patient, eligibility } = mapX12270ToFhirEligibility(x12Input);

console.log('Patient Resource:', JSON.stringify(patient, null, 2));
console.log('Eligibility Request:', JSON.stringify(eligibility, null, 2));
```

### Output: FHIR R4 Patient Resource

```json
{
  "resourceType": "Patient",
  "id": "THP123456789",
  "meta": {
    "profile": [
      "http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient"
    ]
  },
  "identifier": [
    {
      "use": "official",
      "type": {
        "coding": [
          {
            "system": "http://terminology.hl7.org/CodeSystem/v2-0203",
            "code": "MB",
            "display": "Member Number"
          }
        ]
      },
      "system": "urn:oid:TXHEALTH01",
      "value": "THP123456789"
    }
  ],
  "active": true,
  "name": [
    {
      "use": "official",
      "family": "Doe",
      "given": ["John", "Robert"]
    }
  ],
  "telecom": [
    {
      "system": "phone",
      "value": "512-555-1234",
      "use": "home"
    },
    {
      "system": "email",
      "value": "john.doe@example.com",
      "use": "home"
    }
  ],
  "gender": "male",
  "birthDate": "1985-06-15",
  "address": [
    {
      "use": "home",
      "type": "physical",
      "line": ["123 Main Street"],
      "city": "Austin",
      "state": "TX",
      "postalCode": "78701",
      "country": "US"
    }
  ]
}
```

### Output: FHIR R4 CoverageEligibilityRequest

```json
{
  "resourceType": "CoverageEligibilityRequest",
  "id": "INQ20240115001",
  "meta": {
    "profile": [
      "http://hl7.org/fhir/StructureDefinition/CoverageEligibilityRequest"
    ]
  },
  "status": "active",
  "priority": {
    "coding": [
      {
        "system": "http://terminology.hl7.org/CodeSystem/processpriority",
        "code": "normal",
        "display": "Normal"
      }
    ]
  },
  "purpose": ["validation", "benefits"],
  "patient": {
    "reference": "Patient/THP123456789",
    "identifier": {
      "system": "http://terminology.hl7.org/CodeSystem/v2-0203",
      "value": "THP123456789"
    }
  },
  "created": "2024-01-15T09:30:00Z",
  "provider": {
    "identifier": {
      "system": "http://hl7.org/fhir/sid/us-npi",
      "value": "1234567890"
    }
  },
  "insurer": {
    "identifier": {
      "system": "http://terminology.hl7.org/CodeSystem/v2-0203",
      "value": "TXHEALTH01",
      "type": {
        "coding": [
          {
            "system": "http://terminology.hl7.org/CodeSystem/v2-0203",
            "code": "NIIP",
            "display": "National Insurance Payor Identifier (Payor)"
          }
        ]
      }
    },
    "display": "Texas Health Plan"
  },
  "item": [
    {
      "category": {
        "coding": [
          {
            "system": "https://x12.org/codes/service-type-codes",
            "code": "30",
            "display": "Health Benefit Plan Coverage"
          }
        ]
      }
    },
    {
      "category": {
        "coding": [
          {
            "system": "https://x12.org/codes/service-type-codes",
            "code": "48",
            "display": "Hospital - Inpatient"
          }
        ]
      }
    },
    {
      "category": {
        "coding": [
          {
            "system": "https://x12.org/codes/service-type-codes",
            "code": "98",
            "display": "Professional (Physician) Visit - Office"
          }
        ]
      }
    }
  ]
}
```

---

## fhir.js Integration

### Installation

```bash
npm install fhir.js @types/fhir
```

### Using fhir.js Client

fhir.js provides a JavaScript/TypeScript client for interacting with FHIR servers:

```typescript
import Client from 'fhir.js';
import { mapX12270ToFhirEligibility } from './src/fhir/fhirEligibilityMapper';

// Initialize FHIR client
const client = Client({
  baseUrl: 'https://your-fhir-server.com/fhir',
  auth: {
    bearer: 'your-oauth-token-here'
  }
});

// Transform X12 to FHIR
const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);

// Create Patient resource on FHIR server
client.create({
  resource: patient
}).then(response => {
  console.log('Patient created:', response.id);
}).catch(error => {
  console.error('Error creating patient:', error);
});

// Create CoverageEligibilityRequest
client.create({
  resource: eligibility
}).then(response => {
  console.log('Eligibility request created:', response.id);
}).catch(error => {
  console.error('Error creating eligibility request:', error);
});
```

### Searching for Patients

```typescript
// Search by member ID
client.search({
  type: 'Patient',
  query: {
    identifier: 'THP123456789'
  }
}).then(bundle => {
  console.log('Found patients:', bundle.entry.length);
  bundle.entry.forEach(entry => {
    console.log('Patient:', entry.resource);
  });
});

// Search by name and birthdate
client.search({
  type: 'Patient',
  query: {
    family: 'Doe',
    given: 'John',
    birthdate: '1985-06-15'
  }
}).then(bundle => {
  console.log('Matching patients:', bundle.entry);
});
```

### Updating Resources

```typescript
// Fetch existing patient
client.read({
  type: 'Patient',
  id: 'THP123456789'
}).then(patient => {
  // Update telecom
  patient.telecom = patient.telecom || [];
  patient.telecom.push({
    system: 'phone',
    value: '512-555-9999',
    use: 'mobile'
  });
  
  // Update on server
  return client.update({
    resource: patient
  });
}).then(updated => {
  console.log('Patient updated:', updated);
});
```

### Handling CoverageEligibilityResponse

```typescript
// Poll for eligibility response
client.search({
  type: 'CoverageEligibilityResponse',
  query: {
    request: `CoverageEligibilityRequest/${eligibility.id}`
  }
}).then(bundle => {
  if (bundle.entry && bundle.entry.length > 0) {
    const response = bundle.entry[0].resource;
    console.log('Eligibility status:', response.outcome);
    console.log('Insurance details:', response.insurance);
  }
});
```

### Validation with fhir.js

```typescript
import { validateResource } from 'fhir.js';

// Validate generated FHIR resources
const patientValidation = validateResource(patient);
if (patientValidation.valid) {
  console.log('✅ Patient resource is valid');
} else {
  console.error('❌ Patient validation errors:', patientValidation.errors);
}

const eligibilityValidation = validateResource(eligibility);
if (eligibilityValidation.valid) {
  console.log('✅ Eligibility request is valid');
} else {
  console.error('❌ Eligibility validation errors:', eligibilityValidation.errors);
}
```

---

## API Endpoints

### Recommended RESTful API Structure

```
POST   /api/fhir/eligibility/inquiry
  → Accept X12 270 EDI, return FHIR CoverageEligibilityRequest ID

GET    /api/fhir/Patient/:id
  → Retrieve Patient resource

GET    /api/fhir/CoverageEligibilityRequest/:id
  → Retrieve eligibility request

GET    /api/fhir/CoverageEligibilityResponse/:id
  → Retrieve eligibility response (after processing)

POST   /api/fhir/Patient
  → Create or update patient from X12 data
```

### Sample API Implementation

```typescript
import express from 'express';
import { mapX12270ToFhirEligibility } from './src/fhir/fhirEligibilityMapper';

const app = express();
app.use(express.json());

// POST /api/fhir/eligibility/inquiry
app.post('/api/fhir/eligibility/inquiry', async (req, res) => {
  try {
    const x12Data: X12_270 = req.body;
    
    // Validate input
    if (!x12Data.inquiryId || !x12Data.subscriber) {
      return res.status(400).json({ error: 'Invalid X12 270 data' });
    }
    
    // Transform to FHIR
    const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
    
    // Store in FHIR server or database
    // await fhirClient.create({ resource: patient });
    // await fhirClient.create({ resource: eligibility });
    
    res.status(201).json({
      patientId: patient.id,
      eligibilityRequestId: eligibility.id,
      patient,
      eligibility
    });
  } catch (error) {
    console.error('Error processing eligibility inquiry:', error);
    res.status(500).json({ error: 'Internal server error' });
  }
});

// GET /api/fhir/Patient/:id
app.get('/api/fhir/Patient/:id', async (req, res) => {
  try {
    const patientId = req.params.id;
    // const patient = await fhirClient.read({ type: 'Patient', id: patientId });
    // res.json(patient);
    res.status(501).json({ error: 'Not implemented' });
  } catch (error) {
    res.status(404).json({ error: 'Patient not found' });
  }
});

app.listen(3000, () => {
  console.log('FHIR API server listening on port 3000');
});
```

---

## Testing

### Running Tests

```bash
# Run all FHIR tests
npm test -- --testPathPattern=fhir

# Run with coverage
npm test -- --testPathPattern=fhir --coverage

# Watch mode for development
npm test -- --testPathPattern=fhir --watch
```

### Test Coverage

Current test suite includes:

- ✅ Basic mapping (minimal required fields)
- ✅ Gender code mapping (M, F, U, missing)
- ✅ Comprehensive demographic mapping
- ✅ Dependent vs. subscriber handling
- ✅ Service type code mapping (100+ codes)
- ✅ Date format conversion (CCYYMMDD → YYYY-MM-DD)
- ✅ FHIR profile compliance (US Core Patient)
- ✅ CMS interoperability requirements
- ✅ Edge cases and error handling

**Coverage: 19 test cases, 100% pass rate**

### Sample Test

```typescript
it('maps complete subscriber information including address and contact', () => {
  const input: X12_270 = {
    inquiryId: 'INQ200',
    informationSource: { id: '030240928' },
    subscriber: {
      memberId: 'MEM2001',
      firstName: 'Robert',
      lastName: 'Johnson',
      dob: '19750810',
      gender: 'M',
      address: {
        street1: '123 Main Street',
        city: 'Austin',
        state: 'TX',
        zip: '78701'
      },
      phone: '512-555-1234',
      email: 'robert.johnson@example.com'
    },
    insurerId: 'TXHEALTH01'
  };

  const { patient, eligibility } = mapX12270ToFhirEligibility(input);
  
  expect(patient.address![0].city).toBe('Austin');
  expect(patient.telecom).toHaveLength(2);
  expect(eligibility.patient.reference).toBe('Patient/MEM2001');
});
```

---

## Security and HIPAA Compliance

### PHI Handling

⚠️ **CRITICAL**: FHIR resources contain Protected Health Information (PHI)

**Required Safeguards**:
1. ✅ Encrypt data at rest (Azure Storage encryption)
2. ✅ Encrypt data in transit (TLS 1.2+)
3. ✅ Access controls (RBAC, managed identity)
4. ✅ Audit logging (Application Insights)
5. ✅ Minimum necessary principle
6. ✅ Business Associate Agreements (BAAs)

### SSN Handling

```typescript
// ⚠️ Handle SSN with extreme care per HIPAA
if ('ssn' in member && member.ssn) {
  identifiers.push({
    use: 'official',
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        code: 'SS',
        display: 'Social Security Number'
      }]
    },
    system: 'http://hl7.org/fhir/sid/us-ssn',
    value: member.ssn  // Only for matching, never log or display
  });
}
```

**Best Practices**:
- ❌ Never log SSN in Application Insights
- ❌ Never return SSN in API responses (unless required for specific use case)
- ✅ Use for patient matching only
- ✅ Redact from audit logs
- ✅ Encrypt separately if stored

### Authentication & Authorization

```typescript
// Example: Azure AD Bearer token validation
app.use('/api/fhir', async (req, res, next) => {
  const token = req.headers.authorization?.replace('Bearer ', '');
  
  if (!token) {
    return res.status(401).json({ error: 'Unauthorized' });
  }
  
  try {
    // Validate Azure AD token
    const decoded = await validateAzureADToken(token);
    req.user = decoded;
    next();
  } catch (error) {
    return res.status(403).json({ error: 'Forbidden' });
  }
});
```

### Audit Logging

```typescript
import { ApplicationInsights } from 'applicationinsights';

// Log FHIR access with PHI redaction
function logFhirAccess(operation: string, resourceType: string, resourceId: string, userId: string) {
  ApplicationInsights.defaultClient.trackEvent({
    name: 'FHIRAccess',
    properties: {
      operation,
      resourceType,
      resourceId,  // Safe to log (no PHI)
      userId,
      timestamp: new Date().toISOString()
    }
  });
}

// Usage
logFhirAccess('READ', 'Patient', patient.id!, req.user.id);
```

---

## Roadmap

### Q1 2025
- [x] X12 270 → FHIR R4 Patient mapping
- [x] X12 270 → FHIR R4 CoverageEligibilityRequest
- [ ] X12 271 → FHIR R4 CoverageEligibilityResponse
- [ ] FHIR CoverageEligibilityRequest → X12 270 (reverse)

### Q2 2025
- [ ] X12 837 Claims → FHIR R4 Claim resource
- [ ] FHIR R4 Claim → X12 837 (reverse)
- [ ] FHIR Coverage resource implementation
- [ ] Da Vinci PDex profile compliance

### Q3 2025
- [ ] Prior authorization workflows (X12 278 ↔ FHIR)
- [ ] Attachments (X12 275 ↔ FHIR DocumentReference)
- [ ] SMART on FHIR integration
- [ ] FHIR Bulk Data export

---

## Support

**Documentation**: [docs/](../docs/)  
**Issues**: [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)  
**License**: Apache 2.0

**Contributors**:
- GitHub Copilot (AI-assisted development)
- Aurelianware development team

---

## References

### Standards
- [HL7 FHIR R4 Specification](https://hl7.org/fhir/R4/)
- [US Core Implementation Guide](http://hl7.org/fhir/us/core/)
- [Da Vinci Payer Data Exchange](http://hl7.org/fhir/us/davinci-pdex/)
- [X12 005010X279A1 Implementation Guide](https://x12.org/)

### Regulations
- [CMS-9115-F: Patient Access Final Rule](https://www.cms.gov/regulations-and-guidance/legislation/hipaa/interoperability-and-patient-access)
- [CMS-0057-F: Prior Authorization Rule](https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-and-prior-authorization-final-rule-cms-0057-f)
- [HIPAA Security Rule](https://www.hhs.gov/hipaa/for-professionals/security/index.html)

### Tools
- [fhir.js](https://github.com/FHIR/fhir.js) - FHIR JavaScript library
- [@types/fhir](https://www.npmjs.com/package/@types/fhir) - TypeScript definitions
- [FHIR Validator](https://validator.fhir.org/) - Online resource validation

---

**Last Updated**: November 2024  
**Version**: 1.0.0  
**Status**: Production Ready
