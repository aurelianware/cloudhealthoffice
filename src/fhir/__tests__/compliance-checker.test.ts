/**
 * Tests for CMS-0057-F Compliance Checker
 */

import {
  CMS0057FComplianceChecker,
  createComplianceChecker,
  validateWithAzureFHIR,
} from '../compliance-checker';
import { ServiceRequest, Claim, ExplanationOfBenefit, Patient } from 'fhir/r4';

describe('CMS0057FComplianceChecker - ServiceRequest Validation', () => {
  let checker: CMS0057FComplianceChecker;

  beforeEach(() => {
    checker = createComplianceChecker();
  });

  it('validates compliant ServiceRequest', () => {
    const validServiceRequest: ServiceRequest = {
      resourceType: 'ServiceRequest',
      id: 'SR001',
      meta: {
        profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
      },
      status: 'active',
      intent: 'order',
      code: {
        coding: [{
          system: 'https://x12.org/codes/service-type-codes',
          code: '48',
          display: 'Hospital - Inpatient',
        }],
      },
      subject: {
        reference: 'Patient/PAT001',
      },
      requester: {
        identifier: {
          system: 'http://hl7.org/fhir/sid/us-npi',
          value: '1234567890',
        },
      },
      insurance: [{
        identifier: {
          value: 'PAYER001',
        },
      }],
      authoredOn: '2024-01-15T10:00:00Z',
    };

    const result = checker.validateServiceRequest(validServiceRequest);

    expect(result.compliant).toBe(true);
    expect(result.issues.filter(i => i.severity === 'error').length).toBe(0);
    expect(result.score).toBeGreaterThan(90);
  });

  it('detects missing required fields in ServiceRequest', () => {
    const invalidServiceRequest: ServiceRequest = {
      resourceType: 'ServiceRequest',
      id: 'SR002',
      status: 'active',
      intent: 'order',
      // Missing required fields: code, subject, requester
    } as any;

    const result = checker.validateServiceRequest(invalidServiceRequest);

    expect(result.compliant).toBe(false);
    expect(result.issues.length).toBeGreaterThan(0);
    
    const errors = result.issues.filter(i => i.severity === 'error');
    expect(errors.length).toBeGreaterThan(0);
  });

  it('validates Da Vinci PAS profile requirement', () => {
    const serviceRequestWithoutProfile: ServiceRequest = {
      resourceType: 'ServiceRequest',
      id: 'SR003',
      status: 'active',
      intent: 'order',
      code: {
        coding: [{ code: '48' }],
      },
      subject: {
        reference: 'Patient/PAT001',
      },
      requester: {
        identifier: { value: '1234567890' },
      },
      // Missing meta.profile
    };

    const result = checker.validateServiceRequest(serviceRequestWithoutProfile);

    const profileIssue = result.issues.find(i => i.rule === 'SR-002');
    expect(profileIssue).toBeDefined();
    expect(profileIssue!.severity).toBe('error');
    expect(profileIssue!.suggestion).toContain('davinci-pas');
  });

  it('checks for insurance reference', () => {
    const serviceRequestWithoutInsurance: ServiceRequest = {
      resourceType: 'ServiceRequest',
      id: 'SR004',
      meta: {
        profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
      },
      status: 'active',
      intent: 'order',
      code: { coding: [{ code: '48' }] },
      subject: { reference: 'Patient/PAT001' },
      requester: { identifier: { value: '1234567890' } },
      // Missing insurance
    };

    const result = checker.validateServiceRequest(serviceRequestWithoutInsurance);

    const insuranceIssue = result.issues.find(i => i.rule === 'SR-008');
    expect(insuranceIssue).toBeDefined();
    expect(insuranceIssue!.severity).toBe('warning');
  });
});

describe('CMS0057FComplianceChecker - Claim Validation', () => {
  let checker: CMS0057FComplianceChecker;

  beforeEach(() => {
    checker = createComplianceChecker();
  });

  it('validates compliant Claim', () => {
    const validClaim: Claim = {
      resourceType: 'Claim',
      id: 'CLM001',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim'],
      },
      status: 'active',
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/claim-type',
          code: 'professional',
        }],
      },
      use: 'claim',
      patient: {
        reference: 'Patient/PAT001',
      },
      created: '2024-01-15T10:00:00Z',
      provider: {
        identifier: {
          system: 'http://hl7.org/fhir/sid/us-npi',
          value: '1234567890',
        },
      },
      insurer: {
        identifier: {
          value: 'PAYER001',
        },
      },
      priority: {
        coding: [{ code: 'normal' }],
      },
      insurance: [{
        sequence: 1,
        focal: true,
        coverage: {
          reference: 'Coverage/COV001',
        },
      }],
      diagnosis: [{
        sequence: 1,
        diagnosisCodeableConcept: {
          coding: [{
            system: 'http://hl7.org/fhir/sid/icd-10-cm',
            code: 'E11.9',
          }],
        },
      }],
      item: [{
        sequence: 1,
        productOrService: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213',
          }],
        },
        net: {
          value: 150,
          currency: 'USD',
        },
      }],
      total: {
        value: 150,
        currency: 'USD',
      },
    };

    const result = checker.validateClaim(validClaim);

    expect(result.compliant).toBe(true);
    expect(result.issues.filter(i => i.severity === 'error').length).toBe(0);
    expect(result.score).toBeGreaterThan(90);
  });

  it('detects missing US Core profile', () => {
    const claimWithoutProfile: Claim = {
      resourceType: 'Claim',
      id: 'CLM002',
      status: 'active',
      type: { coding: [{ code: 'professional' }] },
      use: 'claim',
      patient: { reference: 'Patient/PAT001' },
      created: '2024-01-15T10:00:00Z',
      provider: { identifier: { value: '1234567890' } },
      insurer: { identifier: { value: 'PAYER001' } },
      priority: { coding: [{ code: 'normal' }] },
      insurance: [{ sequence: 1, focal: true, coverage: { reference: 'Coverage/COV001' } }],
      item: [{ sequence: 1, productOrService: { coding: [{ code: '99213' }] } }],
    };

    const result = checker.validateClaim(claimWithoutProfile);

    const profileIssue = result.issues.find(i => i.rule === 'CL-002');
    expect(profileIssue).toBeDefined();
    expect(profileIssue!.severity).toBe('error');
  });

  it('requires at least one service line item', () => {
    const claimWithoutItems: Claim = {
      resourceType: 'Claim',
      id: 'CLM003',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim'],
      },
      status: 'active',
      type: { coding: [{ code: 'professional' }] },
      use: 'claim',
      patient: { reference: 'Patient/PAT001' },
      created: '2024-01-15T10:00:00Z',
      provider: { identifier: { value: '1234567890' } },
      insurer: { identifier: { value: 'PAYER001' } },
      priority: { coding: [{ code: 'normal' }] },
      insurance: [{ sequence: 1, focal: true, coverage: { reference: 'Coverage/COV001' } }],
      item: [], // Empty items array
    };

    const result = checker.validateClaim(claimWithoutItems);

    const itemsIssue = result.issues.find(i => i.rule === 'CL-011');
    expect(itemsIssue).toBeDefined();
    expect(itemsIssue!.severity).toBe('error');
  });
});

describe('CMS0057FComplianceChecker - ExplanationOfBenefit Validation', () => {
  let checker: CMS0057FComplianceChecker;

  beforeEach(() => {
    checker = createComplianceChecker();
  });

  it('validates compliant ExplanationOfBenefit', () => {
    const validEOB: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      id: 'EOB001',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-explanationofbenefit'],
      },
      status: 'active',
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/claim-type',
          code: 'professional',
        }],
      },
      use: 'claim',
      patient: {
        reference: 'Patient/PAT001',
      },
      created: '2024-01-20T10:00:00Z',
      insurer: {
        identifier: { value: 'PAYER001' },
      },
      provider: {
        identifier: { value: '1234567890' },
      },
      outcome: 'complete',
      insurance: [{
        focal: true,
        coverage: {
          reference: 'Coverage/COV001',
        },
      }],
      payment: {
        date: '2024-01-20',
        amount: {
          value: 120,
          currency: 'USD',
        },
      },
      total: [{
        category: {
          coding: [{
            code: 'submitted',
          }],
        },
        amount: {
          value: 150,
          currency: 'USD',
        },
      }],
    };

    const result = checker.validateExplanationOfBenefit(validEOB);

    expect(result.compliant).toBe(true);
    expect(result.issues.filter(i => i.severity === 'error').length).toBe(0);
  });

  it('requires outcome field', () => {
    const eobWithoutOutcome: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      id: 'EOB002',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-explanationofbenefit'],
      },
      status: 'active',
      type: { coding: [{ code: 'professional' }] },
      use: 'claim',
      patient: { reference: 'Patient/PAT001' },
      created: '2024-01-20T10:00:00Z',
      insurer: { identifier: { value: 'PAYER001' } },
      provider: { identifier: { value: '1234567890' } },
      // Missing outcome
    } as any;

    const result = checker.validateExplanationOfBenefit(eobWithoutOutcome);

    const outcomeIssue = result.issues.find(i => i.rule === 'EOB-010');
    expect(outcomeIssue).toBeDefined();
    expect(outcomeIssue!.severity).toBe('error');
  });
});

describe('CMS0057FComplianceChecker - Patient Validation', () => {
  let checker: CMS0057FComplianceChecker;

  beforeEach(() => {
    checker = createComplianceChecker();
  });

  it('validates compliant Patient', () => {
    const validPatient: Patient = {
      resourceType: 'Patient',
      id: 'PAT001',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'],
      },
      identifier: [{
        system: 'http://example.org/member-id',
        value: 'MEM001',
      }],
      name: [{
        family: 'Doe',
        given: ['John'],
      }],
      gender: 'male',
    };

    const result = checker.validatePatient(validPatient);

    expect(result.compliant).toBe(true);
    expect(result.issues.filter(i => i.severity === 'error').length).toBe(0);
  });

  it('requires identifier', () => {
    const patientWithoutIdentifier: Patient = {
      resourceType: 'Patient',
      id: 'PAT002',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'],
      },
      name: [{ family: 'Doe', given: ['Jane'] }],
      gender: 'female',
      identifier: [], // Empty array
    };

    const result = checker.validatePatient(patientWithoutIdentifier);

    const identifierIssue = result.issues.find(i => i.rule === 'PAT-003');
    expect(identifierIssue).toBeDefined();
    expect(identifierIssue!.severity).toBe('error');
  });

  it('requires name', () => {
    const patientWithoutName: Patient = {
      resourceType: 'Patient',
      id: 'PAT003',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'],
      },
      identifier: [{ value: 'MEM003' }],
      gender: 'male',
      name: [], // Empty array
    };

    const result = checker.validatePatient(patientWithoutName);

    const nameIssue = result.issues.find(i => i.rule === 'PAT-004');
    expect(nameIssue).toBeDefined();
    expect(nameIssue!.severity).toBe('error');
  });

  it('requires gender', () => {
    const patientWithoutGender: Patient = {
      resourceType: 'Patient',
      id: 'PAT004',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'],
      },
      identifier: [{ value: 'MEM004' }],
      name: [{ family: 'Smith', given: ['Jane'] }],
      // Missing gender
    };

    const result = checker.validatePatient(patientWithoutGender);

    const genderIssue = result.issues.find(i => i.rule === 'PAT-005');
    expect(genderIssue).toBeDefined();
    expect(genderIssue!.severity).toBe('error');
  });
});

describe('CMS0057FComplianceChecker - Timeline Validation', () => {
  let checker: CMS0057FComplianceChecker;

  beforeEach(() => {
    checker = createComplianceChecker();
  });

  it('validates compliant life-threatening timeline (within 24 hours)', () => {
    const requestDate = new Date('2024-01-15T10:00:00Z');
    const responseDate = new Date('2024-01-16T08:00:00Z'); // 22 hours later

    const result = checker.validateTimeline(requestDate, responseDate, false, true);

    expect(result.compliant).toBe(true);
    expect(result.issues.length).toBe(0);
  });

  it('detects non-compliant life-threatening timeline (over 24 hours)', () => {
    const requestDate = new Date('2024-01-15T10:00:00Z');
    const responseDate = new Date('2024-01-16T12:00:00Z'); // 26 hours later

    const result = checker.validateTimeline(requestDate, responseDate, false, true);

    expect(result.compliant).toBe(false);
    const timelineIssue = result.issues.find(i => i.rule === 'TIME-001');
    expect(timelineIssue).toBeDefined();
    expect(timelineIssue!.message).toContain('24 hours');
    expect(timelineIssue!.message).toContain('26');
  });

  it('validates compliant expedited timeline (within 72 hours)', () => {
    const requestDate = new Date('2024-01-15T10:00:00Z');
    const responseDate = new Date('2024-01-18T09:00:00Z'); // 71 hours later

    const result = checker.validateTimeline(requestDate, responseDate, true, false);

    expect(result.compliant).toBe(true);
    expect(result.issues.length).toBe(0);
  });

  it('detects non-compliant expedited timeline (over 72 hours)', () => {
    const requestDate = new Date('2024-01-15T10:00:00Z');
    const responseDate = new Date('2024-01-19T10:00:00Z'); // 96 hours later

    const result = checker.validateTimeline(requestDate, responseDate, true, false);

    expect(result.compliant).toBe(false);
    const timelineIssue = result.issues.find(i => i.rule === 'TIME-002');
    expect(timelineIssue).toBeDefined();
    expect(timelineIssue!.message).toContain('72 hours');
  });

  it('validates compliant standard timeline (within 7 days)', () => {
    const requestDate = new Date('2024-01-15T10:00:00Z');
    const responseDate = new Date('2024-01-21T10:00:00Z'); // 6 days later

    const result = checker.validateTimeline(requestDate, responseDate, false, false);

    expect(result.compliant).toBe(true);
    expect(result.issues.length).toBe(0);
  });

  it('detects non-compliant standard timeline (over 7 days)', () => {
    const requestDate = new Date('2024-01-15T10:00:00Z');
    const responseDate = new Date('2024-01-23T10:00:00Z'); // 8 days later

    const result = checker.validateTimeline(requestDate, responseDate, false, false);

    expect(result.compliant).toBe(false);
    const timelineIssue = result.issues.find(i => i.rule === 'TIME-003');
    expect(timelineIssue).toBeDefined();
    expect(timelineIssue!.message).toContain('7 calendar days');
  });
});

describe('CMS0057FComplianceChecker - Comprehensive Validation', () => {
  let checker: CMS0057FComplianceChecker;

  beforeEach(() => {
    checker = createComplianceChecker();
  });

  it('validates multiple resources together', () => {
    const serviceRequest: ServiceRequest = {
      resourceType: 'ServiceRequest',
      meta: {
        profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
      },
      status: 'active',
      intent: 'order',
      code: { coding: [{ code: '48' }] },
      subject: { reference: 'Patient/PAT001' },
      requester: { identifier: { value: '1234567890' } },
    };

    const patient: Patient = {
      resourceType: 'Patient',
      id: 'PAT001',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'],
      },
      identifier: [{ value: 'MEM001' }],
      name: [{ family: 'Doe', given: ['John'] }],
      gender: 'male',
    };

    const result = checker.validateCMSCompliance({
      serviceRequest,
      patient,
    });

    expect(result.compliant).toBe(true);
    expect(result.checkedRules.length).toBeGreaterThan(0);
  });

  it('aggregates issues from multiple resources', () => {
    const invalidServiceRequest: ServiceRequest = {
      resourceType: 'ServiceRequest',
      status: 'active',
      intent: 'order',
      // Missing required fields
    } as any;

    const invalidPatient: Patient = {
      resourceType: 'Patient',
      // Missing required fields
    } as any;

    const result = checker.validateCMSCompliance({
      serviceRequest: invalidServiceRequest,
      patient: invalidPatient,
    });

    expect(result.compliant).toBe(false);
    expect(result.issues.length).toBeGreaterThan(0);
  });

  it('calculates overall compliance score', () => {
    const validServiceRequest: ServiceRequest = {
      resourceType: 'ServiceRequest',
      meta: {
        profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
      },
      status: 'active',
      intent: 'order',
      code: { coding: [{ code: '48' }] },
      subject: { reference: 'Patient/PAT001' },
      requester: { identifier: { value: '1234567890' } },
      insurance: [{ identifier: { value: 'PAYER001' } }],
      authoredOn: '2024-01-15T10:00:00Z',
    };

    const result = checker.validateCMSCompliance({
      serviceRequest: validServiceRequest,
    });

    expect(result.score).toBeGreaterThanOrEqual(0);
    expect(result.score).toBeLessThanOrEqual(100);
  });
});

describe('validateWithAzureFHIR', () => {
  it('validates ServiceRequest without Azure endpoint', async () => {
    const serviceRequest: ServiceRequest = {
      resourceType: 'ServiceRequest',
      meta: {
        profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
      },
      status: 'active',
      intent: 'order',
      code: { coding: [{ code: '48' }] },
      subject: { reference: 'Patient/PAT001' },
      requester: { identifier: { value: '1234567890' } },
    };

    const result = await validateWithAzureFHIR(serviceRequest);

    expect(result).toBeDefined();
    expect(result.compliant).toBeDefined();
  });

  it('validates ServiceRequest with Azure endpoint (falls back to local)', async () => {
    const serviceRequest: ServiceRequest = {
      resourceType: 'ServiceRequest',
      meta: {
        profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
      },
      status: 'active',
      intent: 'order',
      code: { coding: [{ code: '48' }] },
      subject: { reference: 'Patient/PAT001' },
      requester: { identifier: { value: '1234567890' } },
    };

    // Should log warning about not implemented but still return result
    const result = await validateWithAzureFHIR(
      serviceRequest,
      'https://my-fhir.azurehealthcareapis.com'
    );

    expect(result).toBeDefined();
    expect(result.compliant).toBeDefined();
  });

  it('validates Claim with Azure endpoint', async () => {
    const claim: Claim = {
      resourceType: 'Claim',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim'],
      },
      status: 'active',
      type: { coding: [{ code: 'professional' }] },
      use: 'claim',
      patient: { reference: 'Patient/PAT001' },
      created: '2024-01-15T10:00:00Z',
      provider: { identifier: { value: '1234567890' } },
      insurer: { identifier: { value: 'PAYER001' } },
      priority: { coding: [{ code: 'normal' }] },
      insurance: [{ sequence: 1, focal: true, coverage: { reference: 'Coverage/COV001' } }],
      item: [{ sequence: 1, productOrService: { coding: [{ code: '99213' }] } }],
    };

    const result = await validateWithAzureFHIR(
      claim,
      'https://my-fhir.azurehealthcareapis.com'
    );

    expect(result).toBeDefined();
  });

  it('validates ExplanationOfBenefit with Azure endpoint', async () => {
    const eob: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-explanationofbenefit'],
      },
      status: 'active',
      type: { coding: [{ code: 'professional' }] },
      use: 'claim',
      patient: { reference: 'Patient/PAT001' },
      created: '2024-01-20T10:00:00Z',
      insurer: { identifier: { value: 'PAYER001' } },
      provider: { identifier: { value: '1234567890' } },
      outcome: 'complete',
      insurance: [{ focal: true, coverage: { reference: 'Coverage/COV001' } }],
    };

    const result = await validateWithAzureFHIR(
      eob,
      'https://my-fhir.azurehealthcareapis.com'
    );

    expect(result).toBeDefined();
  });

  it('validates Patient with Azure endpoint', async () => {
    const patient: Patient = {
      resourceType: 'Patient',
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'],
      },
      identifier: [{ value: 'MEM001' }],
      name: [{ family: 'Doe', given: ['John'] }],
      gender: 'male',
    };

    const result = await validateWithAzureFHIR(
      patient,
      'https://my-fhir.azurehealthcareapis.com'
    );

    expect(result).toBeDefined();
  });

  it('handles unsupported resource types', async () => {
    const unsupportedResource = {
      resourceType: 'Observation',
    } as any;

    const result = await validateWithAzureFHIR(unsupportedResource);

    expect(result.compliant).toBe(false);
    expect(result.issues[0].message).toContain('Unsupported resource type');
  });

  it('handles unsupported resource types with Azure endpoint', async () => {
    const unsupportedResource = {
      resourceType: 'Observation',
    } as any;

    const result = await validateWithAzureFHIR(
      unsupportedResource,
      'https://my-fhir.azurehealthcareapis.com'
    );

    expect(result.compliant).toBe(false);
    expect(result.issues[0].message).toContain('Unsupported resource type');
  });
});
