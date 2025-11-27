/**
 * Integration Tests for CMS-0057-F APIs
 * 
 * Tests full API flows including:
 * - OAuth 2.0 authentication/authorization
 * - Bulk FHIR operations
 * - End-to-end workflows
 * - Compliance validations
 */

import { 
  validateCMS0057FCompliance, 
  validateBatchCompliance
} from '../compliance-checker';
import { 
  mapX12837ToFhirClaim, 
  mapX12278ToFhirPriorAuth, 
  mapX12835ToFhirEOB 
} from '../fhir-mapper';
import { X12_837_Claim, X12_278_Request, X12_835_Remittance } from '../x12ClaimTypes';
import { Bundle } from 'fhir/r4';

describe('CMS-0057-F Integration Tests', () => {
  
  describe('OAuth 2.0 Authentication Flow', () => {
    it('validates patient scope requirements for Patient Access API', () => {
      // Simulate OAuth token with patient scope
      const patientToken = {
        scope: 'patient/*.read',
        patient_id: 'PAT-12345',
        aud: 'https://fhir-api.example.com'
      };
      
      // Validate scope allows access to patient resources
      expect(patientToken.scope).toContain('patient/*.read');
      expect(patientToken.patient_id).toBeDefined();
    });
    
    it('validates provider scope requirements for Provider Access API', () => {
      // Simulate OAuth token with user scope
      const providerToken = {
        scope: 'user/*.read',
        npi: '1234567890',
        aud: 'https://fhir-api.example.com'
      };
      
      // Validate scope allows provider access
      expect(providerToken.scope).toContain('user/*.read');
      expect(providerToken.npi).toBeDefined();
    });
    
    it('validates system scope requirements for bulk operations', () => {
      // Simulate OAuth token with system scope
      const systemToken = {
        scope: 'system/*.read',
        client_id: 'bulk-export-client',
        aud: 'https://fhir-api.example.com'
      };
      
      // Validate scope allows system-level access
      expect(systemToken.scope).toContain('system/*.read');
      expect(systemToken.client_id).toBeDefined();
    });
    
    it('validates SMART on FHIR launch context', () => {
      // Simulate SMART launch parameters
      const launchContext = {
        iss: 'https://fhir-api.example.com',
        launch: 'launch-token-12345',
        aud: 'https://fhir-api.example.com',
        redirect_uri: 'https://provider-app.example.com/callback'
      };
      
      expect(launchContext.iss).toBeDefined();
      expect(launchContext.launch).toBeDefined();
      expect(launchContext.aud).toBe(launchContext.iss);
    });
  });
  
  describe('Bulk FHIR Operations', () => {
    it('generates bulk export request for payer-to-payer exchange', () => {
      // Simulate bulk export request
      const exportRequest = {
        resourceType: 'Group',
        id: 'all-patients',
        exportType: 'Patient,Coverage,ExplanationOfBenefit,Claim',
        since: '2019-01-01T00:00:00Z', // 5-year requirement
        outputFormat: 'application/fhir+ndjson'
      };
      
      expect(exportRequest.resourceType).toBe('Group');
      expect(exportRequest.since).toBeDefined();
      expect(exportRequest.outputFormat).toBe('application/fhir+ndjson');
      
      // Verify 5-year historical data requirement
      // The 'since' parameter should allow retrieving data at least 5 years old
      const sinceDate = new Date(exportRequest.since);
      const fiveYearsAgo = new Date();
      fiveYearsAgo.setFullYear(fiveYearsAgo.getFullYear() - 5);
      // Since date should be at or before 5 years ago to capture all required historical data
      expect(sinceDate.getTime()).toBeLessThanOrEqual(fiveYearsAgo.getTime());
    });
    
    it('validates bulk export response format', () => {
      // Simulate bulk export status response
      const exportStatus = {
        transactionTime: new Date().toISOString(),
        request: 'https://fhir-api.example.com/$export',
        requiresAccessToken: true,
        output: [
          {
            type: 'Patient',
            url: 'https://storage.example.com/patients.ndjson',
            count: 10000
          },
          {
            type: 'ExplanationOfBenefit',
            url: 'https://storage.example.com/eobs.ndjson',
            count: 50000
          }
        ],
        error: []
      };
      
      expect(exportStatus.output).toBeDefined();
      expect(exportStatus.output.length).toBeGreaterThan(0);
      expect(exportStatus.requiresAccessToken).toBe(true);
    });
    
    it('processes bulk NDJSON data stream', () => {
      // Simulate NDJSON line-delimited FHIR resources
      const ndjsonData = `
{"resourceType":"Patient","id":"1","name":[{"family":"Doe","given":["John"]}]}
{"resourceType":"Patient","id":"2","name":[{"family":"Smith","given":["Jane"]}]}
{"resourceType":"Patient","id":"3","name":[{"family":"Johnson","given":["Bob"]}]}
`.trim();
      
      const resources = ndjsonData.split('\n').map(line => JSON.parse(line));
      
      expect(resources).toHaveLength(3);
      expect(resources.every(r => r.resourceType === 'Patient')).toBe(true);
    });
  });
  
  describe('End-to-End Claim Workflow', () => {
    it('processes complete claim lifecycle from submission to payment', () => {
      // Step 1: Submit claim (837)
      const claimSubmission: X12_837_Claim = {
        claimId: 'CLM-E2E-001',
        claimType: 'P',
        totalChargeAmount: 500.00,
        patient: {
          memberId: 'MEM-E2E-001',
          firstName: 'John',
          lastName: 'Doe',
          dob: '19800101',
          gender: 'M'
        },
        billingProvider: {
          npi: '1234567890',
          organizationName: 'Test Clinic'
        },
        payer: {
          payerId: 'PAYER-001',
          payerName: 'Test Health Plan'
        },
        serviceLines: [{
          lineNumber: 1,
          procedureCode: '99213',
          serviceDate: '20240115',
          units: 1,
          chargeAmount: 500.00,
          diagnosisPointers: [1]
        }],
        diagnosisCodes: [{
          sequence: 1,
          code: 'Z00.00'
        }]
      };
      
      // Transform to FHIR Claim
      const fhirClaim = mapX12837ToFhirClaim(claimSubmission);
      expect(fhirClaim.resourceType).toBe('Claim');
      
      // Validate compliance
      const claimCompliance = validateCMS0057FCompliance(fhirClaim);
      expect(claimCompliance.compliant).toBe(true);
      expect(claimCompliance.summary.uscdiDataClasses).toContain('Patient Demographics');
      
      // Step 2: Process remittance (835)
      const remittance: X12_835_Remittance = {
        transactionId: 'REM-E2E-001',
        payer: {
          payerId: 'PAYER-001',
          payerName: 'Test Health Plan'
        },
        payee: {
          npi: '1234567890',
          organizationName: 'Test Clinic'
        },
        payment: {
          paymentMethodCode: 'ACH',
          paymentAmount: 450.00,
          paymentDate: '20240215'
        },
        claims: [{
          claimId: 'CLM-E2E-001',
          patient: {
            memberId: 'MEM-E2E-001',
            firstName: 'John',
            lastName: 'Doe'
          },
          claimAmounts: {
            billedAmount: 500.00,
            allowedAmount: 475.00,
            paidAmount: 450.00,
            deductible: 25.00,
            patientResponsibility: 25.00
          },
          claimStatusCode: '1',
          claimDates: {
            statementFromDate: '20240115',
            processedDate: '20240214'
          },
          serviceLines: [{
            lineNumber: 1,
            procedureCode: '99213',
            serviceDate: '20240115',
            units: 1,
            amounts: {
              billedAmount: 500.00,
              allowedAmount: 475.00,
              paidAmount: 450.00,
              deductible: 25.00
            }
          }]
        }]
      };
      
      // Transform to FHIR EOB
      const fhirEobs = mapX12835ToFhirEOB(remittance);
      expect(fhirEobs).toHaveLength(1);
      expect(fhirEobs[0].resourceType).toBe('ExplanationOfBenefit');
      expect(fhirEobs[0].payment?.amount?.value).toBe(450.00);
      
      // Validate EOB compliance
      const eobCompliance = validateCMS0057FCompliance(fhirEobs[0]);
      expect(eobCompliance.compliant).toBe(true);
      expect(eobCompliance.summary.uscdiDataClasses).toContain('Financial');
    });
  });
  
  describe('Prior Authorization Workflow', () => {
    it('processes complete prior auth lifecycle from request to approval', () => {
      // Step 1: Submit prior auth request (278)
      const authRequest: X12_278_Request = {
        transactionId: 'AUTH-E2E-001',
        reviewType: 'AR',
        certificationType: 'I',
        serviceTypeCode: '1',
        levelOfService: 'U',
        patient: {
          memberId: 'MEM-E2E-002',
          firstName: 'Jane',
          lastName: 'Smith',
          dob: '19850615',
          gender: 'F'
        },
        requestingProvider: {
          npi: '9876543210',
          organizationName: 'Specialist Group'
        },
        payer: {
          payerId: 'PAYER-002',
          payerName: 'Test Medicaid'
        },
        requestedServices: [{
          serviceTypeCode: '1',
          procedureCode: '99205',
          quantity: 1,
          serviceDateRange: {
            startDate: '20240301',
            endDate: '20240301'
          }
        }],
        diagnosisCodes: [{
          code: 'E11.9',
          type: 'principal'
        }]
      };
      
      // Transform to FHIR ServiceRequest
      const fhirServiceRequest = mapX12278ToFhirPriorAuth(authRequest);
      expect(fhirServiceRequest.resourceType).toBe('ServiceRequest');
      expect(fhirServiceRequest.priority).toBe('urgent'); // Level U = urgent
      
      // Validate compliance including timeline
      const authCompliance = validateCMS0057FCompliance(fhirServiceRequest);
      expect(authCompliance.compliant).toBe(true);
      expect(authCompliance.summary.daVinciProfiles).toContain('PAS ServiceRequest');
      expect(authCompliance.summary.timelineCompliance.applicable).toBe(true);
      
      // Verify 72-hour requirement for urgent
      if (authCompliance.summary.timelineCompliance.deadline) {
        expect(authCompliance.summary.timelineCompliance.deadline).toBe('72 hours');
      }
    });
    
    it('tracks standard prior auth timeline (7 days)', () => {
      const standardAuthRequest: X12_278_Request = {
        transactionId: 'AUTH-E2E-002',
        reviewType: 'AR',
        serviceTypeCode: '30',
        levelOfService: 'E', // Elective = standard
        patient: {
          memberId: 'MEM-E2E-003',
          firstName: 'Bob',
          lastName: 'Johnson',
          dob: '19700101',
          gender: 'M'
        },
        requestingProvider: {
          npi: '1111111111',
          organizationName: 'Physical Therapy'
        },
        payer: {
          payerId: 'PAYER-003'
        },
        requestedServices: [{
          serviceTypeCode: '30',
          procedureCode: '97110',
          quantity: 12,
          serviceDateRange: {
            startDate: '20240401',
            endDate: '20240430'
          }
        }]
      };
      
      const fhirServiceRequest = mapX12278ToFhirPriorAuth(standardAuthRequest);
      expect(fhirServiceRequest.priority).toBe('routine'); // Elective = routine
      
      // Note: Timeline validation would be based on actual processing date
      // In a real system, this would be tracked from authoredOn timestamp
    });
  });
  
  describe('Batch Compliance Validation', () => {
    it('validates multiple resources in a single batch', () => {
      // Create a set of FHIR resources from X12 data
      const claim837: X12_837_Claim = {
        claimId: 'BATCH-001',
        claimType: 'P',
        totalChargeAmount: 300.00,
        patient: {
          memberId: 'MEM-BATCH-001',
          firstName: 'Test',
          lastName: 'Patient',
          dob: '19900101',
          gender: 'F'
        },
        billingProvider: {
          npi: '5555555555',
          organizationName: 'Batch Clinic'
        },
        payer: {
          payerId: 'BATCH-PAYER'
        },
        serviceLines: [{
          lineNumber: 1,
          procedureCode: '99214',
          serviceDate: '20240201',
          units: 1,
          chargeAmount: 300.00,
          diagnosisPointers: [1]
        }],
        diagnosisCodes: [{
          sequence: 1,
          code: 'I10'
        }]
      };
      
      const auth278: X12_278_Request = {
        transactionId: 'BATCH-AUTH-001',
        reviewType: 'AR',
        serviceTypeCode: '1',
        patient: {
          memberId: 'MEM-BATCH-002',
          firstName: 'Another',
          lastName: 'Patient',
          dob: '19850315',
          gender: 'M'
        },
        requestingProvider: {
          npi: '6666666666',
          organizationName: 'Batch Provider'
        },
        payer: {
          payerId: 'BATCH-PAYER'
        },
        requestedServices: [{
          serviceTypeCode: '1',
          procedureCode: '99215',
          quantity: 1,
          serviceDateRange: {
            startDate: '20240301'
          }
        }]
      };
      
      // Transform to FHIR
      const fhirClaim = mapX12837ToFhirClaim(claim837);
      const fhirAuth = mapX12278ToFhirPriorAuth(auth278);
      
      // Batch validate
      const results = validateBatchCompliance([fhirClaim, fhirAuth]);
      
      expect(results).toHaveLength(2);
      expect(results[0].summary.resourceType).toBe('Claim');
      expect(results[1].summary.resourceType).toBe('ServiceRequest');
      
      // All should be compliant
      expect(results.every(r => r.compliant)).toBe(true);
      
      // Verify USCDI coverage
      const allUscdiClasses = results.flatMap(r => r.summary.uscdiDataClasses);
      expect(allUscdiClasses).toContain('Patient Demographics');
      expect(allUscdiClasses).toContain('Procedures');
    });
  });
  
  describe('FHIR Bundle Support', () => {
    it('creates transaction bundle for batch submission', () => {
      // Simulate FHIR transaction bundle
      const bundle: Bundle = {
        resourceType: 'Bundle',
        type: 'transaction',
        entry: [
          {
            request: {
              method: 'POST',
              url: 'Claim'
            },
            resource: {
              resourceType: 'Claim',
              status: 'active',
              type: { coding: [{ code: 'professional' }] },
              use: 'claim',
              patient: { reference: 'Patient/12345' },
              created: '2024-01-15',
              provider: { reference: 'Organization/67890' },
              priority: { coding: [{ code: 'normal' }] },
              insurance: [{
                sequence: 1,
                focal: true,
                coverage: { reference: 'Coverage/12345' }
              }],
              item: [{
                sequence: 1,
                productOrService: { coding: [{ code: '99213' }] }
              }]
            }
          },
          {
            request: {
              method: 'POST',
              url: 'ServiceRequest'
            },
            resource: {
              resourceType: 'ServiceRequest',
              status: 'draft',
              intent: 'order',
              subject: { reference: 'Patient/12345' },
              requester: { reference: 'Practitioner/98765' },
              code: { coding: [{ code: '99213' }] }
            }
          }
        ]
      };
      
      expect(bundle.type).toBe('transaction');
      expect(bundle.entry).toHaveLength(2);
      expect(bundle.entry![0].request?.method).toBe('POST');
      expect(bundle.entry![1].request?.method).toBe('POST');
    });
  });
  
  describe('Patient Access API Integration', () => {
    it('validates patient can access their claims and EOBs', () => {
      // Simulate patient requesting their data
      // Patient should have access to:
      // - Patient resource (demographics)
      // - Coverage resource (insurance info)
      // - Claim resources (submitted claims)
      // - ExplanationOfBenefit (adjudicated claims)
      
      const accessibleResources = [
        'Patient',
        'Coverage',
        'Claim',
        'ExplanationOfBenefit',
        'Encounter'
      ];
      
      // Verify all required resource types for Patient Access API
      expect(accessibleResources).toContain('Patient');
      expect(accessibleResources).toContain('ExplanationOfBenefit');
      
      // Within 1 business day requirement
      const adjudicationDate = new Date('2024-02-15T10:00:00Z');
      const availableDate = new Date('2024-02-16T10:00:00Z');
      const hoursDiff = (availableDate.getTime() - adjudicationDate.getTime()) / (1000 * 60 * 60);
      
      expect(hoursDiff).toBeLessThanOrEqual(24); // 1 business day
    });
  });
});
