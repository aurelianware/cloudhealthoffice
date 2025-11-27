# FHIR R4 Eligibility Mapper

**Production-ready FHIR R4 integration for Cloud Health Office**

This module provides comprehensive mapping between X12 270 EDI eligibility transactions and FHIR R4 resources, supporting CMS Patient Access API mandates.

## Quick Start

```typescript
import { mapX12270ToFhirEligibility } from './fhirEligibilityMapper';
import { X12_270 } from './x12Types';

// Your X12 270 data
const x12Data: X12_270 = {
  inquiryId: 'INQ001',
  informationSource: { id: '030240928' },
  subscriber: {
    memberId: 'MEM123',
    firstName: 'John',
    lastName: 'Doe',
    dob: '1985-06-15',
    gender: 'M'
  },
  insurerId: 'PLAN01'
};

// Transform to FHIR R4
const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);

console.log('Patient:', patient);
console.log('Eligibility Request:', eligibility);
```

## Files

### Core Implementation

- **`x12Types.ts`**: TypeScript interfaces for X12 270 structure
- **`fhirEligibilityMapper.ts`**: Main mapping logic (X12 → FHIR)
- **`provider-access-api.ts`**: Provider Access API (CMS-0057-F) with SMART on FHIR authentication
- **`examples.ts`**: Runnable usage examples (9 scenarios)
- **`provider-access-examples.ts`**: Provider Access API examples (10 scenarios)

### Testing

- **`__tests__/fhirEligibilityMapper.test.ts`**: Comprehensive test suite (19 tests)
- **`__tests__/provider-access-api.test.ts`**: Provider Access API tests (44 tests)

## Features

### Eligibility Mapping
✅ HIPAA X12 005010X279A1 compliant  
✅ HL7 FHIR R4 v4.0.1 compliant  
✅ US Core Patient profile support  
✅ CMS Patient Access API ready  
✅ 100+ service type codes mapped  
✅ Comprehensive test coverage  
✅ Production-ready error handling

### Provider Access API (CMS-0057-F)
✅ SMART on FHIR authentication (OpenID Connect/OAuth2)  
✅ Patient consent management  
✅ Search & read for 6 FHIR resource types (Patient, Claim, Encounter, EOB, Condition, Observation)  
✅ Backend data mapping (QNXT → FHIR)  
✅ HIPAA safeguards (AES-256-GCM encryption, audit logging, PHI redaction)  
✅ US Core v3.1.1 compliance  
✅ Da Vinci PDex alignment  
✅ 44 comprehensive unit tests  

## Running Examples

```bash
# Build the project
npm run build

# Run eligibility mapping examples
npm run examples:fhir

# Run Provider Access API examples
npm run examples:provider-access

# Or run directly:
node dist/src/fhir/examples.js
node dist/src/fhir/provider-access-examples.js
```

## Running Tests

```bash
# Run all FHIR tests (63 tests total: 19 eligibility + 44 provider access)
npm run test:fhir

# Or with Jest directly:
npm test -- --testPathPattern=fhir

# Run with coverage
npm test -- --testPathPattern=fhir --coverage

# Watch mode
npm test -- --testPathPattern=fhir --watch
```

## Mapping Overview

### X12 270 → FHIR R4 Patient

| X12 Field | FHIR Field | Type |
|-----------|------------|------|
| `subscriber.memberId` | `id` | string |
| `subscriber.firstName` | `name[0].given[0]` | string |
| `subscriber.lastName` | `name[0].family` | string |
| `subscriber.dob` | `birthDate` | date |
| `subscriber.gender` | `gender` | code |
| `subscriber.address` | `address[0]` | Address |
| `subscriber.phone` | `telecom[0]` | ContactPoint |

### X12 270 → FHIR R4 CoverageEligibilityRequest

| X12 Field | FHIR Field | Type |
|-----------|------------|------|
| `inquiryId` | `id` | string |
| `subscriber.memberId` | `patient.reference` | Reference |
| `insurerId` | `insurer.identifier` | Identifier |
| `serviceTypeCodes` | `item[].category` | CodeableConcept |
| `informationReceiver.npi` | `provider.identifier` | Identifier |

## Common Use Cases

### 1. Basic Eligibility Check

```typescript
const result = mapX12270ToFhirEligibility({
  inquiryId: 'INQ001',
  informationSource: { id: '030240928' },
  subscriber: {
    memberId: 'MEM123',
    firstName: 'John',
    lastName: 'Doe',
    dob: '1985-06-15'
  },
  insurerId: 'PLAN01'
});
```

### 2. Dependent Eligibility

```typescript
const result = mapX12270ToFhirEligibility({
  inquiryId: 'INQ002',
  informationSource: { id: '030240928' },
  subscriber: {
    memberId: 'FAM123', // Family plan
    firstName: 'Parent',
    lastName: 'Name',
    dob: '1980-01-01'
  },
  dependent: {
    relationshipCode: '19', // Child
    firstName: 'Child',
    lastName: 'Name',
    dob: '2010-05-15',
    gender: 'F'
  },
  insurerId: 'PLAN01'
});
```

### 3. With Full Demographics

```typescript
const result = mapX12270ToFhirEligibility({
  inquiryId: 'INQ003',
  informationSource: { id: '030240928', name: 'Payer Name' },
  informationReceiver: {
    npi: '1234567890',
    organizationName: 'Provider Clinic'
  },
  subscriber: {
    memberId: 'MEM123',
    firstName: 'John',
    lastName: 'Doe',
    middleName: 'Robert',
    dob: '1985-06-15',
    gender: 'M',
    address: {
      street1: '123 Main St',
      city: 'Austin',
      state: 'TX',
      zip: '78701'
    },
    phone: '512-555-1234',
    email: 'john.doe@example.com'
  },
  insurerId: 'PLAN01',
  serviceTypeCodes: ['30', '48', '98']
});
```

## Integration with native fetch

```typescript
import { mapX12270ToFhirEligibility } from './fhirEligibilityMapper';

const fhirBaseUrl = 'https://your-fhir-server.com/fhir';
const accessToken = 'your-oauth-token';

// Transform X12 to FHIR
const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);

// Helper for authenticated requests
async function postResource(resourceType: string, body: unknown) {
  const response = await fetch(`${fhirBaseUrl}/${resourceType}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/fhir+json',
      Authorization: `Bearer ${accessToken}`
    },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    const errorBody = await response.text();
    throw new Error(`FHIR POST failed (${response.status}): ${errorBody}`);
  }

  return response.json();
}

// Store on FHIR server
await postResource('Patient', patient);
await postResource('CoverageEligibilityRequest', eligibility);
```

## Azure Logic Apps Integration

```typescript
// In Azure Function or Logic App custom action
export async function processEligibilityInquiry(x12Data: X12_270) {
  // Map to FHIR
  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
  
  // Store in Cosmos DB with FHIR API
  await cosmosClient.database('fhir').container('Patient')
    .items.create(patient);
  
  await cosmosClient.database('fhir').container('CoverageEligibilityRequest')
    .items.create(eligibility);
  
  return {
    patientId: patient.id,
    eligibilityId: eligibility.id
  };
}
```

## Gender Code Mapping

| X12 Code | FHIR Code | Description |
|----------|-----------|-------------|
| M | male | Male |
| F | female | Female |
| U | unknown | Unknown |
| (missing) | unknown | Not specified |

## Date Format Conversion

```typescript
// X12 Format (CCYYMMDD)
"19850615"  →  "1985-06-15"  // FHIR format

// X12 DateTime (CCYYMMDD-HHMM)
"20240115-1430"  →  "2024-01-15T14:30:00Z"  // ISO 8601
```

## Service Type Codes

The mapper includes 100+ X12 service type codes. Common examples:

- `1`: Medical Care
- `30`: Health Benefit Plan Coverage (default)
- `33`: Chiropractic
- `35`: Dental Care
- `48`: Hospital - Inpatient
- `49`: Hospital - Outpatient
- `86`: Emergency Services
- `88`: Pharmacy
- `98`: Professional (Physician) Visit - Office

*See `fhirEligibilityMapper.ts` for complete list*

## HIPAA Compliance

⚠️ **Important Security Notes**:

1. **SSN Handling**: SSN is included in identifiers but should:
   - Never be logged in Application Insights
   - Not be returned in API responses (unless required)
   - Be encrypted at rest
   - Be used only for patient matching

2. **PHI Protection**: All FHIR resources contain PHI and must be:
   - Encrypted in transit (TLS 1.2+)
   - Encrypted at rest (Azure Storage encryption)
   - Access controlled (RBAC, managed identity)
   - Audit logged (Application Insights)

3. **Minimum Necessary**: Only include fields required for the use case

## CMS Patient Access API

This implementation supports CMS-9115-F requirements:

✅ FHIR R4 based API  
✅ Patient resource with demographics  
✅ Coverage eligibility information  
✅ US Core Patient profile compliance  
✅ Standardized identifiers (NPI, Member ID)  
✅ Proper terminology bindings (v2-0203, processpriority)  

## Error Handling

```typescript
try {
  const { patient, eligibility } = mapX12270ToFhirEligibility(x12Data);
  // Process resources
} catch (error) {
  console.error('Mapping error:', error);
  // Handle error appropriately
}
```

The mapper handles:
- Missing optional fields (gracefully omitted)
- Invalid date formats (preserved as-is with warning)
- Empty arrays (default values provided)
- Missing gender (defaults to 'unknown')

## Documentation

- **Full Integration Guide**: [docs/FHIR-INTEGRATION.md](../../docs/FHIR-INTEGRATION.md)
- **Usage Examples**: Run `node dist/src/fhir/examples.js`
- **Test Suite**: 19 comprehensive tests in `__tests__/`

## Contributing

When adding features:

1. Update type definitions in `x12Types.ts`
2. Enhance mapper logic in `fhirEligibilityMapper.ts`
3. Add tests in `__tests__/fhirEligibilityMapper.test.ts`
4. Run `npm test -- --testPathPattern=fhir` to verify
5. Update documentation

## Support

**Issues**: [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)  
**License**: Apache 2.0  
**Version**: 1.0.0

---

**Built with ❤️ by the Cloud Health Office team**  
*Democratizing healthcare EDI integration*
