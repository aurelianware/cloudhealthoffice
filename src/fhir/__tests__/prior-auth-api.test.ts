/**
 * Prior Authorization API Test Suite
 * 
 * Comprehensive test coverage for CMS-0057-F compliance
 * - X12 278 to FHIR mapping
 * - Da Vinci CRD/DTR/PAS integration
 * - SLA timeline calculations
 * - Attachment workflows
 * - Provider hooks
 * - Error handling
 * - Compliance validation
 * 
 * Target Coverage: 20+ test cases
 */

import { describe, it, expect } from '@jest/globals';
import {
  X12_278_Request,
  PriorAuthorizationRequest,
  PriorAuthorizationResponse,
  mapX12278ToFHIRPriorAuth,
  mapFHIRToX12278Response,
  calculatePriorAuthSLA,
  updateSLAWithDecision,
  createCRDCard,
  createAttachmentBinary,
  createAttachmentDocumentReference,
  createPriorAuthConsent,
  createCDSHooksRequest,
  validatePriorAuthRequest,
  createOrchestrationConfig
} from '../prior-auth-api';

describe('Prior Authorization API - X12 278 to FHIR Mapping', () => {
  
  describe('mapX12278ToFHIRPriorAuth', () => {
    
    it('should map complete X12 278 request to FHIR Claim resource', () => {
      const x12Request: X12_278_Request = {
        transactionSetControlNumber: 'TSC001',
        requestCategory: 'HS',
        certificationType: 'I',
        patient: {
          memberId: 'MEM123456',
          firstName: 'John',
          lastName: 'Doe',
          dateOfBirth: '1985-06-15',
          gender: 'M'
        },
        requestingProvider: {
          npi: '1234567890',
          firstName: 'Jane',
          lastName: 'Smith',
          organizationName: 'City Medical Center'
        },
        services: [{
          serviceTypeCode: '48',
          procedureCode: '99213',
          procedureCodeType: 'CPT',
          quantity: 1,
          quantityType: 'UN',
          fromDate: '2024-12-15',
          toDate: '2024-12-15'
        }],
        diagnoses: [{
          code: 'I50.9',
          codeType: 'ABK'
        }],
        payerId: 'PAYER001'
      };
      
      const fhirClaim = mapX12278ToFHIRPriorAuth(x12Request);
      
      expect(fhirClaim.resourceType).toBe('Claim');
      expect(fhirClaim.status).toBe('active');
      expect(fhirClaim.use).toBe('preauthorization');
      expect(fhirClaim.patient.reference).toBe('Patient/MEM123456');
      expect(fhirClaim.provider.identifier?.value).toBe('1234567890');
      expect(fhirClaim.diagnosis).toHaveLength(1);
      expect(fhirClaim.diagnosis[0].diagnosisCodeableConcept.coding?.[0].code).toBe('I50.9');
      expect(fhirClaim.item).toHaveLength(1);
      expect(fhirClaim.item[0].productOrService.coding?.[0].code).toBe('99213');
    });
    
    it('should map inpatient request (AR) as institutional claim', () => {
      const x12Request: X12_278_Request = {
        transactionSetControlNumber: 'TSC002',
        requestCategory: 'AR',
        certificationType: 'I',
        patient: {
          memberId: 'MEM789012',
          firstName: 'Jane',
          lastName: 'Smith',
          dateOfBirth: '1990-03-21',
          gender: 'F'
        },
        requestingProvider: {
          npi: '9876543210',
          organizationName: 'General Hospital'
        },
        services: [{
          serviceTypeCode: '48',
          placeOfService: '21',
          fromDate: '2024-12-01',
          toDate: '2024-12-05',
          quantity: 5,
          quantityType: 'DY'
        }],
        diagnoses: [{
          code: 'J18.9',
          codeType: 'ABK'
        }],
        payerId: 'PAYER002',
        additionalInfo: {
          admissionDate: '2024-12-01',
          admissionType: 'Elective'
        }
      };
      
      const fhirClaim = mapX12278ToFHIRPriorAuth(x12Request);
      
      expect(fhirClaim.type.coding?.[0].code).toBe('institutional');
      expect(fhirClaim.priority.coding?.[0].code).toBe('stat');
      expect(fhirClaim.item[0].locationCodeableConcept?.coding?.[0].code).toBe('21');
      expect(fhirClaim.item[0].quantity?.value).toBe(5);
      expect(fhirClaim.item[0].quantity?.unit).toBe('days');
    });
    
    it('should handle cancellation request (certificationType=3)', () => {
      const x12Request: X12_278_Request = {
        transactionSetControlNumber: 'TSC003',
        requestCategory: 'HS',
        certificationType: '3',
        patient: {
          memberId: 'MEM456789',
          firstName: 'Bob',
          lastName: 'Johnson',
          dateOfBirth: '1975-11-30',
          gender: 'M'
        },
        requestingProvider: {
          npi: '1112223334'
        },
        services: [{
          serviceTypeCode: '98',
          fromDate: '2024-12-20'
        }],
        diagnoses: [{
          code: 'M54.5',
          codeType: 'ABK'
        }],
        payerId: 'PAYER003',
        authorizationNumber: 'AUTH20241119001'
      };
      
      const fhirClaim = mapX12278ToFHIRPriorAuth(x12Request);
      
      expect(fhirClaim.status).toBe('cancelled');
    });
    
    it('should map multiple services and diagnoses', () => {
      const x12Request: X12_278_Request = {
        transactionSetControlNumber: 'TSC004',
        requestCategory: 'SC',
        certificationType: 'I',
        patient: {
          memberId: 'MEM111222',
          firstName: 'Alice',
          lastName: 'Williams',
          dateOfBirth: '1988-07-10',
          gender: 'F'
        },
        requestingProvider: {
          npi: '5556667778'
        },
        services: [
          {
            serviceTypeCode: '33',
            procedureCode: '97110',
            procedureCodeType: 'CPT',
            fromDate: '2024-12-10',
            quantity: 12,
            quantityType: 'VS'
          },
          {
            serviceTypeCode: '33',
            procedureCode: '97112',
            procedureCodeType: 'CPT',
            fromDate: '2024-12-10',
            quantity: 8,
            quantityType: 'VS'
          }
        ],
        diagnoses: [
          { code: 'M25.561', codeType: 'ABK' },
          { code: 'M79.3', codeType: 'ABK' }
        ],
        payerId: 'PAYER004'
      };
      
      const fhirClaim = mapX12278ToFHIRPriorAuth(x12Request);
      
      expect(fhirClaim.item).toHaveLength(2);
      expect(fhirClaim.diagnosis).toHaveLength(2);
      expect(fhirClaim.item[0].quantity?.value).toBe(12);
      expect(fhirClaim.item[0].quantity?.unit).toBe('visits');
      expect(fhirClaim.item[1].quantity?.value).toBe(8);
    });
    
    it('should include Da Vinci PAS profile in meta', () => {
      const x12Request: X12_278_Request = {
        transactionSetControlNumber: 'TSC005',
        requestCategory: 'HS',
        certificationType: 'I',
        patient: {
          memberId: 'MEM999888',
          firstName: 'Test',
          lastName: 'User',
          dateOfBirth: '1980-01-01',
          gender: 'U'
        },
        requestingProvider: {
          npi: '9999999999'
        },
        services: [{ serviceTypeCode: '30', fromDate: '2024-12-01' }],
        diagnoses: [{ code: 'Z00.00', codeType: 'ABK' }],
        payerId: 'TEST'
      };
      
      const fhirClaim = mapX12278ToFHIRPriorAuth(x12Request);
      
      expect(fhirClaim.meta?.profile).toContain('http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim');
    });
  });
  
  describe('mapFHIRToX12278Response', () => {
    
    it('should map approved FHIR ClaimResponse to X12 278 with A1 status', () => {
      const claimResponse: PriorAuthorizationResponse = {
        resourceType: 'ClaimResponse',
        id: 'CR001',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'preauthorization',
        patient: { reference: 'Patient/MEM123' },
        created: '2024-11-24T10:00:00Z',
        insurer: { identifier: { value: 'PAYER001' } },
        outcome: 'complete',
        preAuthRef: 'AUTH20241124001',
        preAuthPeriod: {
          start: '2024-12-01',
          end: '2024-12-31'
        }
      };
      
      const x12Response = mapFHIRToX12278Response(claimResponse);
      
      expect(x12Response.statusCode).toBe('A1');
      expect(x12Response.authorizationNumber).toBe('AUTH20241124001');
      expect(x12Response.effectiveDate).toBe('2024-12-01');
      expect(x12Response.expirationDate).toBe('2024-12-31');
    });
    
    it('should map partial approval to X12 278 with A2 status', () => {
      const claimResponse: PriorAuthorizationResponse = {
        resourceType: 'ClaimResponse',
        id: 'CR002',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'institutional'
          }]
        },
        use: 'preauthorization',
        patient: { reference: 'Patient/MEM456' },
        created: '2024-11-24T10:00:00Z',
        insurer: { identifier: { value: 'PAYER002' } },
        outcome: 'partial',
        preAuthRef: 'AUTH20241124002'
      };
      
      const x12Response = mapFHIRToX12278Response(claimResponse);
      
      expect(x12Response.statusCode).toBe('A2');
      expect(x12Response.authorizationNumber).toBe('AUTH20241124002');
    });
    
    it('should map denied response with error codes', () => {
      const claimResponse: PriorAuthorizationResponse = {
        resourceType: 'ClaimResponse',
        id: 'CR003',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'preauthorization',
        patient: { reference: 'Patient/MEM789' },
        created: '2024-11-24T10:00:00Z',
        insurer: { identifier: { value: 'PAYER003' } },
        outcome: 'error',
        error: [{
          code: {
            coding: [{
              code: '41',
              display: 'Authorization Information Missing'
            }]
          }
        }]
      };
      
      const x12Response = mapFHIRToX12278Response(claimResponse);
      
      expect(x12Response.statusCode).toBe('A3');
      expect(x12Response.reasons).toHaveLength(1);
      expect(x12Response.reasons?.[0].code).toBe('41');
      expect(x12Response.reasons?.[0].description).toBe('Authorization Information Missing');
    });
  });
});

describe('Prior Authorization API - SLA Management', () => {
  
  describe('calculatePriorAuthSLA', () => {
    
    it('should calculate 72-hour SLA for urgent requests', () => {
      const submittedAt = '2024-11-24T10:00:00Z';
      const sla = calculatePriorAuthSLA('urgent', submittedAt);
      
      expect(sla.requestType).toBe('urgent');
      expect(sla.standardTimelineHours).toBe(72);
      expect(sla.status).toBe('pending');
      
      const expectedDueDate = new Date('2024-11-24T10:00:00Z');
      expectedDueDate.setHours(expectedDueDate.getHours() + 72);
      expect(sla.decisionDueBy).toBe(expectedDueDate.toISOString());
    });
    
    it('should calculate 7-day (168-hour) SLA for standard requests', () => {
      const submittedAt = '2024-11-24T10:00:00Z';
      const sla = calculatePriorAuthSLA('standard', submittedAt);
      
      expect(sla.requestType).toBe('standard');
      expect(sla.standardTimelineHours).toBe(168);
      expect(sla.status).toBe('pending');
      
      const expectedDueDate = new Date('2024-11-24T10:00:00Z');
      expectedDueDate.setHours(expectedDueDate.getHours() + 168);
      expect(sla.decisionDueBy).toBe(expectedDueDate.toISOString());
    });
    
    it('should calculate 72-hour SLA for expedited requests', () => {
      const submittedAt = '2024-11-20T14:30:00Z';
      const sla = calculatePriorAuthSLA('expedited', submittedAt);
      
      expect(sla.requestType).toBe('expedited');
      expect(sla.standardTimelineHours).toBe(72);
    });
  });
  
  describe('updateSLAWithDecision', () => {
    
    it('should mark SLA as compliant when decided within timeline', () => {
      const submittedAt = '2024-11-24T10:00:00Z';
      const sla = calculatePriorAuthSLA('urgent', submittedAt);
      
      const decidedAt = '2024-11-26T08:00:00Z'; // 46 hours later
      const updatedSLA = updateSLAWithDecision(sla, decidedAt);
      
      expect(updatedSLA.decidedAt).toBe(decidedAt);
      expect(updatedSLA.status).toBe('decided');
      expect(updatedSLA.slaCompliant).toBe(true);
      expect(updatedSLA.actualTimelineHours).toBeCloseTo(46, 0);
    });
    
    it('should mark SLA as non-compliant when decided after deadline', () => {
      const submittedAt = '2024-11-24T10:00:00Z';
      const sla = calculatePriorAuthSLA('urgent', submittedAt);
      
      const decidedAt = '2024-11-28T12:00:00Z'; // 98 hours later (exceeds 72)
      const updatedSLA = updateSLAWithDecision(sla, decidedAt);
      
      expect(updatedSLA.status).toBe('overdue');
      expect(updatedSLA.slaCompliant).toBe(false);
      expect(updatedSLA.actualTimelineHours).toBeGreaterThan(72);
    });
    
    it('should calculate exact timeline for standard requests', () => {
      const submittedAt = '2024-11-20T09:00:00Z';
      const sla = calculatePriorAuthSLA('standard', submittedAt);
      
      const decidedAt = '2024-11-25T09:00:00Z'; // Exactly 120 hours later
      const updatedSLA = updateSLAWithDecision(sla, decidedAt);
      
      expect(updatedSLA.actualTimelineHours).toBe(120);
      expect(updatedSLA.slaCompliant).toBe(true);
    });
  });
});

describe('Prior Authorization API - Da Vinci CRD Integration', () => {
  
  describe('createCRDCard', () => {
    
    it('should create CRD card for prior auth requirement', () => {
      const card = createCRDCard(
        'prior-auth-required',
        'Prior authorization required',
        'This service requires prior authorization from the payer before proceeding.'
      );
      
      expect(card.summary).toBe('Prior authorization required');
      expect(card.detail).toBe('This service requires prior authorization from the payer before proceeding.');
      expect(card.indicator).toBe('warning');
      expect(card.source.label).toBe('Cloud Health Office - Coverage Requirements');
      expect(card.uuid).toBeDefined();
    });
    
    it('should create info card for documentation needed', () => {
      const card = createCRDCard(
        'documentation-needed',
        'Additional documentation required',
        'Please provide clinical notes and lab results.'
      );
      
      expect(card.indicator).toBe('info');
      expect(card.summary).toBe('Additional documentation required');
    });
    
    it('should create info card for alternative available', () => {
      const card = createCRDCard(
        'alternative-available',
        'Preferred alternative available',
        'Consider using generic medication X instead.'
      );
      
      expect(card.indicator).toBe('info');
    });
  });
  
  describe('createCDSHooksRequest', () => {
    
    it('should create order-select hook request', () => {
      const request = createCDSHooksRequest('order-select', {
        userId: 'Practitioner/123',
        patientId: 'Patient/456',
        encounterId: 'Encounter/789',
        selections: ['MedicationRequest/001']
      });
      
      expect(request.hook).toBe('order-select');
      expect(request.context.userId).toBe('Practitioner/123');
      expect(request.context.patientId).toBe('Patient/456');
      expect(request.prefetch.patient).toBe('Patient/456');
      // UUID v4 format validation
      expect(request.hookInstance).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
    });
    
    it('should create order-sign hook request', () => {
      const request = createCDSHooksRequest('order-sign', {
        userId: 'Practitioner/999',
        patientId: 'Patient/888'
      });
      
      expect(request.hook).toBe('order-sign');
      expect(request.fhirServer).toBe('https://fhir.cloudhealthoffice.com');
    });
    
    it('should create appointment-book hook request', () => {
      const request = createCDSHooksRequest('appointment-book', {
        userId: 'Practitioner/111',
        patientId: 'Patient/222'
      });
      
      expect(request.hook).toBe('appointment-book');
    });
  });
});

describe('Prior Authorization API - Attachment Support', () => {
  
  describe('createAttachmentBinary', () => {
    
    it('should create FHIR Binary resource for PDF attachment', () => {
      const base64Data = 'JVBERi0xLjQKJeLjz9MK...'; // Truncated for brevity
      const binary = createAttachmentBinary('application/pdf', base64Data);
      
      expect(binary.resourceType).toBe('Binary');
      expect(binary.contentType).toBe('application/pdf');
      expect(binary.data).toBe(base64Data);
    });
    
    it('should create Binary resource for image attachment', () => {
      const base64Image = 'iVBORw0KGgoAAAANSUhEUgA...';
      const binary = createAttachmentBinary('image/jpeg', base64Image);
      
      expect(binary.resourceType).toBe('Binary');
      expect(binary.contentType).toBe('image/jpeg');
    });
  });
  
  describe('createAttachmentDocumentReference', () => {
    
    it('should create DocumentReference for clinical notes', () => {
      const binaryRef = { reference: 'Binary/12345' };
      const patientRef = { reference: 'Patient/67890' };
      
      const docRef = createAttachmentDocumentReference(binaryRef, patientRef, 'clinical-notes');
      
      expect(docRef.resourceType).toBe('DocumentReference');
      expect(docRef.status).toBe('current');
      expect(docRef.subject).toBe(patientRef);
      expect(docRef.type?.coding?.[0].system).toBe('http://loinc.org');
      expect(docRef.type?.coding?.[0].code).toBe('11506-3'); // Progress note
      expect(docRef.content[0].attachment.url).toContain('Binary/12345');
    });
    
    it('should create DocumentReference for lab results', () => {
      const binaryRef = { reference: 'Binary/lab001' };
      const patientRef = { reference: 'Patient/pat001' };
      
      const docRef = createAttachmentDocumentReference(binaryRef, patientRef, 'lab-results');
      
      expect(docRef.type?.coding?.[0].code).toBe('11502-2'); // Laboratory report
    });
    
    it('should create DocumentReference for imaging', () => {
      const binaryRef = { reference: 'Binary/img001' };
      const patientRef = { reference: 'Patient/pat002' };
      
      const docRef = createAttachmentDocumentReference(binaryRef, patientRef, 'imaging');
      
      expect(docRef.type?.coding?.[0].code).toBe('18748-4'); // Diagnostic imaging study
    });
  });
});

describe('Prior Authorization API - Consent Management', () => {
  
  describe('createPriorAuthConsent', () => {
    
    it('should create active consent for prior authorization', () => {
      const patientRef = { reference: 'Patient/12345' };
      const performerRef = { reference: 'Patient/12345' }; // Patient gives own consent
      
      const consent = createPriorAuthConsent(patientRef, performerRef);
      
      expect(consent.resourceType).toBe('Consent');
      expect(consent.status).toBe('active');
      expect(consent.patient).toBe(patientRef);
      expect(consent.performer).toContain(performerRef);
      expect(consent.scope.coding?.[0].code).toBe('patient-privacy');
      expect(consent.category[0].coding?.[0].code).toBe('HIPAA-Auth');
      expect(consent.provision?.type).toBe('permit');
      expect(consent.provision?.purpose?.[0].code).toBe('TREAT');
    });
    
    it('should create consent without performer if not provided', () => {
      const patientRef = { reference: 'Patient/99999' };
      
      const consent = createPriorAuthConsent(patientRef);
      
      expect(consent.patient).toBe(patientRef);
      expect(consent.performer).toBeUndefined();
    });
    
    it('should include datetime when consent was given', () => {
      const patientRef = { reference: 'Patient/11111' };
      const consent = createPriorAuthConsent(patientRef);
      
      expect(consent.dateTime).toBeDefined();
      const consentDate = new Date(consent.dateTime!);
      expect(consentDate.getTime()).toBeLessThanOrEqual(Date.now());
    });
  });
});

describe('Prior Authorization API - Validation', () => {
  
  describe('validatePriorAuthRequest - X12 278', () => {
    
    it('should validate complete X12 278 request', () => {
      const request: X12_278_Request = {
        transactionSetControlNumber: 'TSC999',
        requestCategory: 'HS',
        certificationType: 'I',
        patient: {
          memberId: 'MEM999',
          firstName: 'Test',
          lastName: 'Patient',
          dateOfBirth: '1990-01-01',
          gender: 'F'
        },
        requestingProvider: {
          npi: '1234567890'
        },
        services: [{
          serviceTypeCode: '30',
          fromDate: '2024-12-01'
        }],
        diagnoses: [{
          code: 'Z00.00',
          codeType: 'ABK'
        }],
        payerId: 'PAYER999'
      };
      
      const result = validatePriorAuthRequest(request);
      
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });
    
    it('should fail validation when patient member ID is missing', () => {
      const request: X12_278_Request = {
        transactionSetControlNumber: 'TSC888',
        requestCategory: 'HS',
        certificationType: 'I',
        patient: {
          memberId: '',
          firstName: 'Test',
          lastName: 'Patient',
          dateOfBirth: '1990-01-01',
          gender: 'M'
        },
        requestingProvider: {
          npi: '1234567890'
        },
        services: [{
          serviceTypeCode: '30',
          fromDate: '2024-12-01'
        }],
        diagnoses: [{
          code: 'Z00.00',
          codeType: 'ABK'
        }],
        payerId: 'PAYER888'
      };
      
      const result = validatePriorAuthRequest(request);
      
      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Patient member ID is required');
    });
    
    it('should fail validation when provider NPI is missing', () => {
      const request: X12_278_Request = {
        transactionSetControlNumber: 'TSC777',
        requestCategory: 'HS',
        certificationType: 'I',
        patient: {
          memberId: 'MEM777',
          firstName: 'Test',
          lastName: 'Patient',
          dateOfBirth: '1990-01-01',
          gender: 'U'
        },
        requestingProvider: {
          npi: ''
        },
        services: [{
          serviceTypeCode: '30',
          fromDate: '2024-12-01'
        }],
        diagnoses: [{
          code: 'Z00.00',
          codeType: 'ABK'
        }],
        payerId: 'PAYER777'
      };
      
      const result = validatePriorAuthRequest(request);
      
      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Requesting provider NPI is required');
    });
    
    it('should fail validation when services are missing', () => {
      const request: X12_278_Request = {
        transactionSetControlNumber: 'TSC666',
        requestCategory: 'HS',
        certificationType: 'I',
        patient: {
          memberId: 'MEM666',
          firstName: 'Test',
          lastName: 'Patient',
          dateOfBirth: '1990-01-01',
          gender: 'F'
        },
        requestingProvider: {
          npi: '1234567890'
        },
        services: [],
        diagnoses: [{
          code: 'Z00.00',
          codeType: 'ABK'
        }],
        payerId: 'PAYER666'
      };
      
      const result = validatePriorAuthRequest(request);
      
      expect(result.valid).toBe(false);
      expect(result.errors).toContain('At least one service is required');
    });
    
    it('should fail validation when diagnoses are missing', () => {
      const request: X12_278_Request = {
        transactionSetControlNumber: 'TSC555',
        requestCategory: 'HS',
        certificationType: 'I',
        patient: {
          memberId: 'MEM555',
          firstName: 'Test',
          lastName: 'Patient',
          dateOfBirth: '1990-01-01',
          gender: 'M'
        },
        requestingProvider: {
          npi: '1234567890'
        },
        services: [{
          serviceTypeCode: '30',
          fromDate: '2024-12-01'
        }],
        diagnoses: [],
        payerId: 'PAYER555'
      };
      
      const result = validatePriorAuthRequest(request);
      
      expect(result.valid).toBe(false);
      expect(result.errors).toContain('At least one diagnosis is required');
    });
  });
  
  describe('validatePriorAuthRequest - FHIR Claim', () => {
    
    it('should validate complete FHIR Claim', () => {
      const claim: PriorAuthorizationRequest = {
        resourceType: 'Claim',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'preauthorization',
        patient: {
          reference: 'Patient/123'
        },
        created: '2024-11-24T10:00:00Z',
        insurer: {
          identifier: { value: 'PAYER001' }
        },
        provider: {
          reference: 'Practitioner/456'
        },
        priority: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/processpriority',
            code: 'normal'
          }]
        },
        diagnosis: [{
          sequence: 1,
          diagnosisCodeableConcept: {
            coding: [{
              system: 'http://hl7.org/fhir/sid/icd-10-cm',
              code: 'Z00.00'
            }]
          }
        }],
        item: [{
          sequence: 1,
          productOrService: {
            coding: [{
              system: 'http://www.ama-assn.org/go/cpt',
              code: '99213'
            }]
          }
        }]
      };
      
      const result = validatePriorAuthRequest(claim);
      
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });
    
    it('should fail validation when patient reference is missing', () => {
      const claim: PriorAuthorizationRequest = {
        resourceType: 'Claim',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'preauthorization',
        patient: {
          reference: ''
        },
        created: '2024-11-24T10:00:00Z',
        insurer: {
          identifier: { value: 'PAYER001' }
        },
        provider: {
          reference: 'Practitioner/456'
        },
        priority: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/processpriority',
            code: 'normal'
          }]
        },
        diagnosis: [{
          sequence: 1,
          diagnosisCodeableConcept: {
            coding: [{
              system: 'http://hl7.org/fhir/sid/icd-10-cm',
              code: 'Z00.00'
            }]
          }
        }],
        item: [{
          sequence: 1,
          productOrService: {
            coding: [{
              system: 'http://www.ama-assn.org/go/cpt',
              code: '99213'
            }]
          }
        }]
      };
      
      const result = validatePriorAuthRequest(claim);
      
      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Patient reference is required');
    });
  });
});

describe('Prior Authorization API - Orchestration Configuration', () => {
  
  describe('createOrchestrationConfig', () => {
    
    it('should create orchestration config for dev environment', () => {
      const config = createOrchestrationConfig('https://dev.cloudhealthoffice.com', 'dev');
      
      expect(config.endpoints.submitRequest).toBe('https://dev.cloudhealthoffice.com/api/prior-auth/submit');
      expect(config.endpoints.checkStatus).toBe('https://dev.cloudhealthoffice.com/api/prior-auth/status');
      expect(config.serviceBus.requestTopic).toBe('prior-auth-requests-dev');
      expect(config.serviceBus.responseTopic).toBe('prior-auth-responses-dev');
      expect(config.availity.tradingPartnerId).toBe('AVAILITY');
      expect(config.slaMonitoring.enabled).toBe(true);
      expect(config.slaMonitoring.alertThresholdHours).toBe(24);
    });
    
    it('should create orchestration config for prod environment', () => {
      const config = createOrchestrationConfig('https://cloudhealthoffice.com', 'prod');
      
      expect(config.serviceBus.requestTopic).toBe('prior-auth-requests-prod');
      expect(config.serviceBus.responseTopic).toBe('prior-auth-responses-prod');
      expect(config.serviceBus.attachmentTopic).toBe('prior-auth-attachments-prod');
      expect(config.availity.sftpEndpoint).toBe('sftp.availity.com');
      expect(config.availity.outboundFolder).toBe('/outbound/278');
      expect(config.availity.inboundFolder).toBe('/inbound/278');
    });
    
    it('should create orchestration config for uat environment', () => {
      const config = createOrchestrationConfig('https://uat.cloudhealthoffice.com', 'uat');
      
      expect(config.serviceBus.requestTopic).toBe('prior-auth-requests-uat');
      expect(config.endpoints.submitDocumentation).toBe('https://uat.cloudhealthoffice.com/api/prior-auth/documentation');
      expect(config.endpoints.cancelAuthorization).toBe('https://uat.cloudhealthoffice.com/api/prior-auth/cancel');
    });
  });
});

describe('Prior Authorization API - CMS-0057-F Compliance', () => {
  
  it('should support 72-hour decision timeline for urgent requests', () => {
    const submittedAt = new Date().toISOString();
    const sla = calculatePriorAuthSLA('urgent', submittedAt);
    
    expect(sla.standardTimelineHours).toBe(72);
  });
  
  it('should support 7-day decision timeline for standard requests', () => {
    const submittedAt = new Date().toISOString();
    const sla = calculatePriorAuthSLA('standard', submittedAt);
    
    expect(sla.standardTimelineHours).toBe(168); // 7 days = 168 hours
  });
  
  it('should track SLA compliance', () => {
    const submittedAt = '2024-11-20T10:00:00Z';
    const sla = calculatePriorAuthSLA('urgent', submittedAt);
    const decidedAt = '2024-11-22T09:00:00Z';
    
    const updatedSLA = updateSLAWithDecision(sla, decidedAt);
    
    expect(updatedSLA.slaCompliant).toBeDefined();
    expect(typeof updatedSLA.slaCompliant).toBe('boolean');
  });
  
  it('should support Da Vinci PAS profile in FHIR mapping', () => {
    const x12Request: X12_278_Request = {
      transactionSetControlNumber: 'CMS001',
      requestCategory: 'HS',
      certificationType: 'I',
      patient: {
        memberId: 'CMS123',
        firstName: 'Test',
        lastName: 'Patient',
        dateOfBirth: '1980-01-01',
        gender: 'M'
      },
      requestingProvider: {
        npi: '1234567890'
      },
      services: [{ serviceTypeCode: '30', fromDate: '2024-12-01' }],
      diagnoses: [{ code: 'Z00.00', codeType: 'ABK' }],
      payerId: 'CMS'
    };
    
    const fhirClaim = mapX12278ToFHIRPriorAuth(x12Request);
    
    expect(fhirClaim.meta?.profile).toContain('http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim');
  });
});

// Export for test runner
export {};
