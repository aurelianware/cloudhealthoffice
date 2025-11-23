import { mapX12270ToFhirEligibility } from '../fhirEligibilityMapper';
import { X12_270 } from '../x12Types';

describe('mapX12270ToFhirEligibility', () => {
  it('maps X12 270 EDI to FHIR R4 objects', () => {
    const sampleInput: X12_270 = {
      inquiryId: 'INQ123456',
      member: {
        id: 'MEM1001',
        firstName: 'John',
        lastName: 'Doe',
        gender: 'male',
        dob: '1985-06-15',
      },
      insurerId: 'INSURERX',
    };

    const { patient, eligibility } = mapX12270ToFhirEligibility(sampleInput);
    expect(patient.resourceType).toBe('Patient');
    expect(patient.id).toBe('MEM1001');
    expect(eligibility.patient.reference).toBe('Patient/MEM1001');
    expect(eligibility.insurer.identifier.value).toBe('INSURERX');
  });
});