import {
  X12_278,
  PriorAuthorizationRequest,
  PriorAuthorizationResponse,
  mapX12278ToFhirPriorAuth,
  mapFhirPriorAuthToX12278,
  createDecisionTimeline,
  checkSlaCompliance,
  createAttachmentBinary,
  createAttachmentDocumentReference,
  validateCRDHookRequest,
  packageForClearinghouse,
  processFromClearinghouse,
  ClearinghouseConfig
} from '../prior-auth-api';

describe('Prior Authorization API - CMS-0057-F Implementation', () => {
  
  describe('X12 278 to FHIR Mapping', () => {
    
    it('maps X12 278 request (type 11) to FHIR PriorAuthorizationRequest', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-001',
        certificationType: '1',
        serviceTypeCode: '48',
        levelOfService: 'U',
        requester: {
          npi: '1234567890',
          name: 'Dr. Jane Smith',
          organizationName: 'Smith Medical Group'
        },
        patient: {
          memberId: 'MEM123456',
          firstName: 'John',
          lastName: 'Doe',
          dob: '1980-05-15',
          gender: 'M'
        },
        payer: {
          id: 'PAYER001',
          name: 'Health Plan Inc'
        },
        serviceRequest: {
          procedureCodes: [{
            code: '99213',
            codeType: 'CPT',
            description: 'Office visit',
            quantity: 1
          }],
          diagnosisCodes: [{
            code: 'I10',
            codeType: 'ICD10',
            description: 'Essential hypertension'
          }],
          serviceStartDate: '2024-12-01',
          serviceEndDate: '2024-12-01'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);

      expect(fhirRequest.resourceType).toBe('Claim');
      expect(fhirRequest.id).toBe('PA-2024-001');
      expect(fhirRequest.use).toBe('preauthorization');
      expect(fhirRequest.status).toBe('active');
      expect(fhirRequest.meta.profile).toContain('http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim');
      expect(fhirRequest.patient.reference).toBe('Patient/MEM123456');
      expect(fhirRequest.provider.identifier?.value).toBe('1234567890');
      expect(fhirRequest.priority?.coding?.[0]?.code).toBe('urgent');
      expect(fhirRequest.item).toHaveLength(1);
      expect(fhirRequest.item?.[0].productOrService.coding?.[0].code).toBe('99213');
      expect(fhirRequest.diagnosis).toHaveLength(1);
      expect(fhirRequest.diagnosis?.[0].diagnosisCodeableConcept?.coding?.[0].code).toBe('I10');
    });

    it('includes Da Vinci PAS extensions for certification type', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-002',
        certificationType: '2',
        serviceTypeCode: '49',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM123', firstName: 'Jane', lastName: 'Doe', dob: '1990-01-01' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '27447', codeType: 'CPT' }],
          serviceStartDate: '2025-01-15'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);

      const certTypeExt = fhirRequest.extension?.find(
        ext => ext.url === 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-certificationType'
      );
      expect(certTypeExt).toBeDefined();
      expect(certTypeExt?.valueCoding?.code).toBe('2');
      expect(certTypeExt?.valueCoding?.display).toBe('Renewal');
    });

    it('handles inpatient admission details', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-003',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM123', firstName: 'John', lastName: 'Smith', dob: '1975-03-20' },
        payer: { id: 'PAYER001' },
        admission: {
          admissionDate: '2024-12-10',
          dischargeDate: '2024-12-15',
          admissionType: 'emergency'
        },
        serviceRequest: {
          procedureCodes: [{ code: '470', codeType: 'DRG' }],
          serviceStartDate: '2024-12-10',
          serviceEndDate: '2024-12-15'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);

      const admissionExt = fhirRequest.extension?.find(
        ext => ext.url === 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-admissionDates'
      );
      expect(admissionExt).toBeDefined();
      expect(admissionExt?.valuePeriod?.start).toBe('2024-12-10');
      expect(admissionExt?.valuePeriod?.end).toBe('2024-12-15');
    });

    it('throws error for non-request transaction types', () => {
      const x12Response: X12_278 = {
        transactionType: '13', // Response type
        transactionId: 'PA-2024-999',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM123', firstName: 'Test', lastName: 'User', dob: '1985-01-01' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2024-12-01'
        }
      };

      expect(() => mapX12278ToFhirPriorAuth(x12Response)).toThrow(
        "Invalid transaction type for request: 13. Expected '11' (request)."
      );
    });

    it('handles multiple procedure codes', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-004',
        certificationType: '1',
        serviceTypeCode: '98',
        requester: { npi: '9876543210' },
        patient: { memberId: 'MEM999', firstName: 'Alice', lastName: 'Johnson', dob: '1992-07-30' },
        payer: { id: 'PAYER002' },
        serviceRequest: {
          procedureCodes: [
            { code: '99213', codeType: 'CPT', description: 'Office visit', quantity: 1 },
            { code: '93000', codeType: 'CPT', description: 'ECG', quantity: 1 },
            { code: '80053', codeType: 'CPT', description: 'Comprehensive metabolic panel', quantity: 1 }
          ],
          diagnosisCodes: [
            { code: 'I10', codeType: 'ICD10', description: 'Hypertension' },
            { code: 'E11.9', codeType: 'ICD10', description: 'Type 2 diabetes' }
          ],
          serviceStartDate: '2025-02-01'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);

      expect(fhirRequest.item).toHaveLength(3);
      expect(fhirRequest.item?.[0].productOrService.coding?.[0].code).toBe('99213');
      expect(fhirRequest.item?.[1].productOrService.coding?.[0].code).toBe('93000');
      expect(fhirRequest.item?.[2].productOrService.coding?.[0].code).toBe('80053');
      expect(fhirRequest.diagnosis).toHaveLength(2);
    });
  });

  describe('FHIR to X12 278 Mapping', () => {
    
    it('maps FHIR ClaimResponse to X12 278 response (type 13)', () => {
      const originalRequest: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-005',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM123', firstName: 'John', lastName: 'Doe', dob: '1980-01-01' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2024-12-01'
        }
      };

      const fhirResponse: PriorAuthorizationResponse = {
        resourceType: 'ClaimResponse',
        id: 'RESP-PA-2024-005',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse']
        },
        status: 'active',
        type: {
          coding: [{ system: 'http://terminology.hl7.org/CodeSystem/claim-type', code: 'professional' }]
        },
        use: 'preauthorization',
        patient: { reference: 'Patient/MEM123' },
        created: '2024-11-24T10:00:00Z',
        insurer: { reference: 'Organization/PAYER001' },
        outcome: 'complete',
        disposition: 'Prior authorization approved',
        preAuthRef: 'AUTH123456'
      };

      const x12Response = mapFhirPriorAuthToX12278(fhirResponse, originalRequest);

      expect(x12Response.transactionType).toBe('13');
      expect(x12Response.reviewResponse?.authorizationNumber).toBe('AUTH123456');
      expect(x12Response.reviewResponse?.responseCode).toBe('01'); // Approved
      expect(x12Response.reviewResponse?.responseDescription).toBe('Prior authorization approved');
    });

    it('handles partial approval responses', () => {
      const originalRequest: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-006',
        certificationType: '1',
        serviceTypeCode: '49',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM456', firstName: 'Jane', lastName: 'Smith', dob: '1985-05-15' },
        payer: { id: 'PAYER002' },
        serviceRequest: {
          procedureCodes: [{ code: '27447', codeType: 'CPT', quantity: 2 }],
          serviceStartDate: '2025-01-10'
        }
      };

      const fhirResponse: PriorAuthorizationResponse = {
        resourceType: 'ClaimResponse',
        id: 'RESP-PA-2024-006',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse']
        },
        status: 'active',
        type: { coding: [{ code: 'professional' }] },
        use: 'preauthorization',
        patient: { reference: 'Patient/MEM456' },
        created: '2024-11-24T11:00:00Z',
        insurer: { reference: 'Organization/PAYER002' },
        outcome: 'partial',
        disposition: 'Approved for 1 procedure only',
        preAuthRef: 'AUTH789012'
      };

      const x12Response = mapFhirPriorAuthToX12278(fhirResponse, originalRequest);

      expect(x12Response.reviewResponse?.responseCode).toBe('04'); // Partial
      expect(x12Response.reviewResponse?.reviewOutcome).toBe('A2'); // Modified
    });

    it('handles pended/additional info required responses', () => {
      const originalRequest: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-007',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM789', firstName: 'Bob', lastName: 'Wilson', dob: '1970-12-01' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '27130', codeType: 'CPT' }],
          serviceStartDate: '2025-03-01'
        }
      };

      const fhirResponse: PriorAuthorizationResponse = {
        resourceType: 'ClaimResponse',
        id: 'RESP-PA-2024-007',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse']
        },
        status: 'active',
        type: { coding: [{ code: 'professional' }] },
        use: 'preauthorization',
        patient: { reference: 'Patient/MEM789' },
        created: '2024-11-24T12:00:00Z',
        insurer: { reference: 'Organization/PAYER001' },
        outcome: 'queued',
        disposition: 'Additional clinical documentation required'
      };

      const x12Response = mapFhirPriorAuthToX12278(fhirResponse, originalRequest);

      expect(x12Response.reviewResponse?.responseCode).toBe('03'); // Pended
      expect(x12Response.reviewResponse?.additionalInfoRequired).toBe(true);
      expect(x12Response.reviewResponse?.additionalInfoDeadline).toBeDefined();
    });
  });

  describe('Decision Timeline and SLA Compliance', () => {
    
    it('creates standard decision timeline with 72-hour SLA', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-008',
        certificationType: '1',
        serviceTypeCode: '49',
        levelOfService: 'E',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM111', firstName: 'Test', lastName: 'Patient', dob: '1980-01-01' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2025-01-15'
        }
      };

      const timeline = createDecisionTimeline(x12Request, 'standard');

      expect(timeline.requestType).toBe('standard');
      expect(timeline.slaHours).toBe(72);
      expect(timeline.slaStatus).toBe('pending');
      expect(timeline.dueAt.getTime()).toBeGreaterThan(timeline.receivedAt.getTime());
    });

    it('creates urgent decision timeline with 24-hour SLA', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-009',
        certificationType: '1',
        serviceTypeCode: '48',
        levelOfService: 'U',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM222', firstName: 'Urgent', lastName: 'Case', dob: '1975-06-15' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '27447', codeType: 'CPT' }],
          serviceStartDate: '2024-12-25'
        }
      };

      const timeline = createDecisionTimeline(x12Request, 'urgent');

      expect(timeline.requestType).toBe('urgent');
      expect(timeline.slaHours).toBe(24);
      expect(timeline.dueAt.getTime() - timeline.receivedAt.getTime()).toBe(24 * 60 * 60 * 1000);
    });

    it('checks SLA compliance for timely decision', () => {
      const receivedAt = new Date('2024-11-20T10:00:00Z');
      const dueAt = new Date('2024-11-23T10:00:00Z'); // 72 hours later
      const decidedAt = new Date('2024-11-22T14:00:00Z'); // Before deadline

      const timeline = {
        receivedAt,
        requestType: 'standard' as const,
        slaHours: 72,
        dueAt,
        slaStatus: 'pending' as const
      };

      const result = checkSlaCompliance(timeline, decidedAt);

      expect(result.slaStatus).toBe('compliant');
      expect(result.decidedAt).toEqual(decidedAt);
    });

    it('checks SLA compliance for breached decision', () => {
      const receivedAt = new Date('2024-11-20T10:00:00Z');
      const dueAt = new Date('2024-11-23T10:00:00Z'); // 72 hours later
      const decidedAt = new Date('2024-11-24T12:00:00Z'); // After deadline

      const timeline = {
        receivedAt,
        requestType: 'standard' as const,
        slaHours: 72,
        dueAt,
        slaStatus: 'pending' as const
      };

      const result = checkSlaCompliance(timeline, decidedAt);

      expect(result.slaStatus).toBe('breached');
      expect(result.decidedAt).toEqual(decidedAt);
    });

    it('creates expedited decision timeline with 48-hour SLA', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-010',
        certificationType: '1',
        serviceTypeCode: '49',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM333', firstName: 'Expedited', lastName: 'Request', dob: '1988-03-10' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2025-01-05'
        }
      };

      const timeline = createDecisionTimeline(x12Request, 'expedited');

      expect(timeline.requestType).toBe('expedited');
      expect(timeline.slaHours).toBe(48);
    });
  });

  describe('Attachment Handling (Binary Resource)', () => {
    
    it('creates Binary resource for PDF attachment', () => {
      const pdfData = 'JVBERi0xLjQKJeLjz9MKMSAwIG9iag=='; // Base64 PDF sample
      const binary = createAttachmentBinary(pdfData, 'application/pdf', 'Clinical Notes');

      expect(binary.resourceType).toBe('Binary');
      expect(binary.contentType).toBe('application/pdf');
      expect(binary.data).toBe(pdfData);
      expect(binary.securityContext?.display).toBe('Clinical Notes');
      expect(binary.meta).toBeDefined();
      expect(binary.meta?.profile).toContain('http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-binary');
    });

    it('creates Binary resource for image attachment', () => {
      const imageData = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
      const binary = createAttachmentBinary(imageData, 'image/png', 'X-Ray Image');

      expect(binary.resourceType).toBe('Binary');
      expect(binary.contentType).toBe('image/png');
      expect(binary.securityContext?.display).toBe('X-Ray Image');
    });

    it('creates DocumentReference linking Binary to Claim', () => {
      const binary = createAttachmentBinary('base64data', 'application/pdf', 'Lab Results');
      const claimRef = { reference: 'Claim/PA-2024-011' };
      
      const docRef = createAttachmentDocumentReference(binary, claimRef, 'clinical-note');

      expect(docRef.resourceType).toBe('DocumentReference');
      expect(docRef.status).toBe('current');
      expect(docRef.subject).toEqual(claimRef);
      expect(docRef.content).toHaveLength(1);
      expect(docRef.content[0].attachment.url).toBe(`Binary/${binary.id}`);
      expect(docRef.content[0].attachment.contentType).toBe('application/pdf');
    });

    it('creates Binary without description', () => {
      const binary = createAttachmentBinary('data', 'text/plain');

      expect(binary.securityContext).toBeUndefined();
    });
  });

  describe('Da Vinci CRD Hook Validation', () => {
    
    it('validates valid order-sign hook request', () => {
      const hookRequest = {
        hook: 'order-sign',
        hookInstance: 'hook-instance-123',
        context: {
          userId: 'Practitioner/123',
          patientId: 'Patient/456',
          draftOrders: {
            resourceType: 'Bundle',
            entry: []
          }
        }
      };

      const result = validateCRDHookRequest(hookRequest);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('validates valid appointment-book hook request', () => {
      const hookRequest = {
        hook: 'appointment-book',
        hookInstance: 'hook-instance-456',
        context: {
          userId: 'Practitioner/789',
          patientId: 'Patient/012',
          appointments: {
            resourceType: 'Bundle',
            entry: []
          }
        }
      };

      const result = validateCRDHookRequest(hookRequest);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('fails validation for missing hook', () => {
      const hookRequest = {
        hookInstance: 'hook-instance-789',
        context: {}
      };

      const result = validateCRDHookRequest(hookRequest);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Missing required field: hook');
    });

    it('fails validation for missing context', () => {
      const hookRequest = {
        hook: 'order-sign',
        hookInstance: 'hook-instance-999'
      };

      const result = validateCRDHookRequest(hookRequest);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Missing required field: context');
    });

    it('fails validation for order-sign without draftOrders', () => {
      const hookRequest = {
        hook: 'order-sign',
        hookInstance: 'hook-instance-111',
        context: {
          userId: 'Practitioner/123'
        }
      };

      const result = validateCRDHookRequest(hookRequest);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('order-sign hook requires context.draftOrders');
    });

    it('fails validation for appointment-book without appointments', () => {
      const hookRequest = {
        hook: 'appointment-book',
        hookInstance: 'hook-instance-222',
        context: {
          userId: 'Practitioner/456'
        }
      };

      const result = validateCRDHookRequest(hookRequest);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('appointment-book hook requires context.appointments');
    });
  });

  describe('Clearinghouse Integration', () => {
    
    const availityConfig: ClearinghouseConfig = {
      name: 'Availity',
      tradingPartnerId: '030240928',
      sftp: {
        host: 'sftp.availity.com',
        port: 22,
        username: 'healthplan001',
        inboundPath: '/inbound/278',
        outboundPath: '/outbound/278'
      }
    };

    it('packages prior auth request for clearinghouse submission', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-2024-012',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM444', firstName: 'Clearinghouse', lastName: 'Test', dob: '1982-09-20' },
        payer: { id: 'PAYER001', tradingPartnerId: '030240928' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2025-01-20'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);
      const package_ = packageForClearinghouse(fhirRequest, x12Request, availityConfig);

      expect(package_.metadata.transactionId).toBe('PA-2024-012');
      expect(package_.metadata.tradingPartnerId).toBe('030240928');
      expect(package_.metadata.clearinghouse).toBe('Availity');
      expect(package_.metadata.direction).toBe('outbound');
      expect(package_.destination).toBe('/outbound/278');
      expect(package_.x12Payload).toContain('PA-2024-012');
    });

    it('processes incoming response from clearinghouse', () => {
      const x12ResponsePayload = JSON.stringify({
        transactionType: '13',
        transactionId: 'RESP-PA-2024-012',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM444', firstName: 'Clearinghouse', lastName: 'Test', dob: '1982-09-20' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2025-01-20'
        },
        reviewResponse: {
          authorizationNumber: 'AUTH999888',
          responseCode: '01',
          responseDescription: 'Approved',
          reviewOutcome: 'A1',
          certifiedQuantity: 1
        }
      });

      const result = processFromClearinghouse(x12ResponsePayload, availityConfig);

      expect(result.x12Response.transactionType).toBe('13');
      expect(result.x12Response.reviewResponse?.authorizationNumber).toBe('AUTH999888');
      expect(result.fhirResponse.resourceType).toBe('ClaimResponse');
      expect(result.fhirResponse.outcome).toBe('complete');
      expect(result.fhirResponse.preAuthRef).toBe('AUTH999888');
    });

    it('throws error for invalid response transaction type', () => {
      const invalidPayload = JSON.stringify({
        transactionType: '11', // Request type, not response
        transactionId: 'PA-2024-999',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM999', firstName: 'Invalid', lastName: 'Response', dob: '1980-01-01' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2025-01-20'
        }
      });

      expect(() => processFromClearinghouse(invalidPayload, availityConfig)).toThrow(
        'Invalid response transaction type. Expected type 13 (response).'
      );
    });
  });

  describe('Error Handling and Edge Cases', () => {
    
    it('handles missing optional fields gracefully', () => {
      const minimalRequest: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-MIN-001',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1234567890' },
        patient: { memberId: 'MEM001', firstName: 'Min', lastName: 'Test', dob: '1990-01-01' },
        payer: { id: 'PAYER001' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2025-01-01'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(minimalRequest);

      expect(fhirRequest.resourceType).toBe('Claim');
      expect(fhirRequest.status).toBe('active');
      expect(fhirRequest.use).toBe('preauthorization');
    });

    it('handles empty diagnosis codes array', () => {
      const noDiagnosisRequest: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-NODIAG-001',
        certificationType: '1',
        serviceTypeCode: '49',
        requester: { npi: '9876543210' },
        patient: { memberId: 'MEM002', firstName: 'No', lastName: 'Diagnosis', dob: '1985-05-05' },
        payer: { id: 'PAYER002' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          diagnosisCodes: [],
          serviceStartDate: '2025-02-01'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(noDiagnosisRequest);

      expect(fhirRequest.diagnosis).toEqual([]);
    });

    it('handles missing diagnosis codes', () => {
      const noDiagnosisRequest: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-NODIAG-002',
        certificationType: '1',
        serviceTypeCode: '98',
        requester: { npi: '5555555555' },
        patient: { memberId: 'MEM003', firstName: 'Also', lastName: 'NoDiag', dob: '1978-12-25' },
        payer: { id: 'PAYER003' },
        serviceRequest: {
          procedureCodes: [{ code: '80053', codeType: 'CPT' }],
          serviceStartDate: '2025-03-15'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(noDiagnosisRequest);

      expect(fhirRequest.diagnosis).toBeUndefined();
    });
  });

  describe('Da Vinci IG Mapping Accuracy', () => {
    
    it('includes correct Da Vinci PAS profile URLs', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-DAVINCI-001',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '1111111111' },
        patient: { memberId: 'MEM-DAVINCI', firstName: 'Da', lastName: 'Vinci', dob: '1990-01-01' },
        payer: { id: 'PAYER-DAVINCI' },
        serviceRequest: {
          procedureCodes: [{ code: '99213', codeType: 'CPT' }],
          serviceStartDate: '2025-01-01'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);

      expect(fhirRequest.meta.profile).toContain(
        'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim'
      );
    });

    it('uses correct terminology systems for coding', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-TERM-001',
        certificationType: '1',
        serviceTypeCode: '48',
        requester: { npi: '2222222222' },
        patient: { memberId: 'MEM-TERM', firstName: 'Terminology', lastName: 'Test', dob: '1985-06-15' },
        payer: { id: 'PAYER-TERM' },
        serviceRequest: {
          procedureCodes: [
            { code: '99213', codeType: 'CPT', description: 'CPT Procedure' },
            { code: 'J1234', codeType: 'HCPCS', description: 'HCPCS Code' }
          ],
          diagnosisCodes: [
            { code: 'I10', codeType: 'ICD10', description: 'ICD-10 Diagnosis' }
          ],
          serviceStartDate: '2025-01-01'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);

      expect(fhirRequest.item?.[0].productOrService.coding?.[0].system).toBe('http://www.ama-assn.org/go/cpt');
      expect(fhirRequest.item?.[1].productOrService.coding?.[0].system).toBe('https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets');
      expect(fhirRequest.diagnosis?.[0].diagnosisCodeableConcept?.coding?.[0].system).toBe('http://hl7.org/fhir/sid/icd-10');
    });

    it('validates service type extension URLs', () => {
      const x12Request: X12_278 = {
        transactionType: '11',
        transactionId: 'PA-EXT-001',
        certificationType: '2',
        serviceTypeCode: '49',
        requester: { npi: '3333333333' },
        patient: { memberId: 'MEM-EXT', firstName: 'Extension', lastName: 'Check', dob: '1992-03-10' },
        payer: { id: 'PAYER-EXT' },
        serviceRequest: {
          procedureCodes: [{ code: '27447', codeType: 'CPT' }],
          serviceStartDate: '2025-02-15'
        }
      };

      const fhirRequest = mapX12278ToFhirPriorAuth(x12Request);

      const serviceTypeExt = fhirRequest.extension?.find(
        ext => ext.url === 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-serviceType'
      );
      
      expect(serviceTypeExt).toBeDefined();
      expect(serviceTypeExt?.valueCoding?.system).toBe('https://codesystem.x12.org/005010/1365');
      expect(serviceTypeExt?.valueCoding?.code).toBe('49');
    });
  });
});
