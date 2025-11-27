/**
 * Tests for CMS-0057-F Compliance Checker
 */

import { checkCMSCompliance, checkBatchCompliance } from '../compliance-checker';
import { mapX12837ToFhirClaim, mapX12278ToFhirServiceRequest } from '../fhir-mapper';
import { X12_837, X12_278 } from '../x12Types';

describe('CMS-0057-F Compliance Checker', () => {
  describe('checkCMSCompliance', () => {
    it('validates compliant ServiceRequest (Prior Auth)', () => {
      const input: X12_278 = {
        authorizationId: 'AUTH001',
        transactionDate: new Date().toISOString(),
        requester: {
          npi: '1234567890',
          name: 'Dr. Smith'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dob: '19850615'
        },
        payer: {
          payerId: 'PAYER001',
          name: 'Test Health Plan'
        },
        serviceRequest: {
          serviceTypeCode: 'HS',
          certificationType: 'AR',
          serviceDateFrom: '20240120',
          procedureCode: '27447'
        }
      };
      
      const serviceRequest = mapX12278ToFhirServiceRequest(input);
      const result = checkCMSCompliance(serviceRequest);
      
      expect(result.compliant).toBe(true);
      expect(result.score).toBeGreaterThan(80);
      expect(result.breakdown.dataClasses.compliant).toBe(true);
      expect(result.breakdown.apiRequirements.compliant).toBe(true);
    });
    
    it('detects missing required fields in ServiceRequest', () => {
      const serviceRequest: any = {
        resourceType: 'ServiceRequest',
        id: 'AUTH001',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest']
        }
        // Missing: status, intent, code, subject
      };
      
      const result = checkCMSCompliance(serviceRequest);
      
      expect(result.compliant).toBe(false);
      expect(result.issues.length).toBeGreaterThan(0);
      
      const statusError = result.issues.find(i => 
        i.path === 'status' && i.severity === 'error'
      );
      expect(statusError).toBeDefined();
      
      const intentError = result.issues.find(i => 
        i.path === 'intent' && i.severity === 'error'
      );
      expect(intentError).toBeDefined();
      
      const codeError = result.issues.find(i => 
        i.path === 'code' && i.severity === 'error'
      );
      expect(codeError).toBeDefined();
      
      const subjectError = result.issues.find(i => 
        i.path === 'subject' && i.severity === 'error'
      );
      expect(subjectError).toBeDefined();
    });
    
    it('validates compliant Claim', () => {
      const input: X12_837 = {
        claimId: 'CLM001',
        transactionDate: '20240115',
        billingProvider: {
          npi: '1234567890',
          name: 'Provider Clinic'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dob: '19850615'
        },
        payer: {
          payerId: 'PAYER001',
          name: 'Test Health Plan'
        },
        claim: {
          totalChargeAmount: 150.00,
          serviceLines: [
            {
              lineNumber: 1,
              procedureCode: '99213',
              serviceDateFrom: '20240110',
              chargeAmount: 150.00
            }
          ]
        }
      };
      
      const claim = mapX12837ToFhirClaim(input);
      const result = checkCMSCompliance(claim);
      
      expect(result.compliant).toBe(true);
      expect(result.breakdown.dataClasses.compliant).toBe(true);
    });
    
    it('detects missing required fields in Claim', () => {
      const claim: any = {
        resourceType: 'Claim',
        id: 'CLM001',
        meta: {
          profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim']
        }
        // Missing: status, type, use, patient, provider, item
      };
      
      const result = checkCMSCompliance(claim);
      
      expect(result.compliant).toBe(false);
      
      const statusError = result.issues.find(i => 
        i.path === 'status' && i.severity === 'error'
      );
      expect(statusError).toBeDefined();
      
      const itemError = result.issues.find(i => 
        i.path === 'item' && i.severity === 'error'
      );
      expect(itemError).toBeDefined();
    });
    
    it('warns about missing profiles', () => {
      const resource: any = {
        resourceType: 'ServiceRequest',
        id: 'SR001',
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        }
        // Missing: meta.profile
      };
      
      const result = checkCMSCompliance(resource);
      
      const profileWarning = result.issues.find(i => 
        i.category === 'profile' && i.severity === 'warning'
      );
      expect(profileWarning).toBeDefined();
      expect(profileWarning?.message).toContain('does not declare conformance');
    });
    
    it('warns about non-US Core profiles', () => {
      const resource: any = {
        resourceType: 'ServiceRequest',
        id: 'SR001',
        meta: {
          profile: ['http://hl7.org/fhir/StructureDefinition/ServiceRequest']
        },
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        }
      };
      
      const result = checkCMSCompliance(resource);
      
      const profileWarning = result.issues.find(i => 
        i.category === 'profile' && i.message.includes('US Core or Da Vinci')
      );
      expect(profileWarning).toBeDefined();
    });
    
    it('validates timeline compliance for recent ServiceRequest', () => {
      const resource: any = {
        resourceType: 'ServiceRequest',
        id: 'SR001',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest']
        },
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        },
        authoredOn: new Date().toISOString() // Recent date
      };
      
      const result = checkCMSCompliance(resource);
      
      expect(result.breakdown.timelines.compliant).toBe(true);
    });
    
    it('warns about old ServiceRequest', () => {
      const oldDate = new Date();
      oldDate.setDate(oldDate.getDate() - 45); // 45 days ago
      
      const resource: any = {
        resourceType: 'ServiceRequest',
        id: 'SR001',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest']
        },
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        },
        authoredOn: oldDate.toISOString()
      };
      
      const result = checkCMSCompliance(resource);
      
      const timelineWarning = result.issues.find(i => 
        i.category === 'timeline' && i.severity === 'warning'
      );
      expect(timelineWarning).toBeDefined();
      expect(timelineWarning?.message).toContain('days old');
    });
    
    it('validates API requirements', () => {
      const resource: any = {
        resourceType: 'ServiceRequest',
        id: 'SR001',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
          versionId: '1'
        },
        identifier: [{
          system: 'http://example.org',
          value: 'AUTH001'
        }],
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        }
      };
      
      const result = checkCMSCompliance(resource);
      
      expect(result.breakdown.apiRequirements.compliant).toBe(true);
      expect(result.breakdown.apiRequirements.passed).toBe(3);
    });
    
    it('detects missing resource ID', () => {
      const resource: any = {
        resourceType: 'ServiceRequest',
        // Missing: id
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest']
        },
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        }
      };
      
      const result = checkCMSCompliance(resource);
      
      const idError = result.issues.find(i => 
        i.path === 'id' && i.severity === 'error'
      );
      expect(idError).toBeDefined();
      expect(result.breakdown.apiRequirements.compliant).toBe(false);
    });
    
    it('calculates compliance score correctly', () => {
      // Fully compliant resource
      const compliantResource: any = {
        resourceType: 'ServiceRequest',
        id: 'SR001',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest']
        },
        identifier: [{
          value: 'AUTH001'
        }],
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        },
        authoredOn: new Date().toISOString()
      };
      
      const compliantResult = checkCMSCompliance(compliantResource);
      expect(compliantResult.score).toBeGreaterThanOrEqual(90);
      
      // Resource with warnings
      const warningResource: any = {
        resourceType: 'ServiceRequest',
        id: 'SR002',
        status: 'active',
        intent: 'order',
        code: {
          coding: [{
            code: '99213'
          }]
        },
        subject: {
          reference: 'Patient/123'
        }
        // Missing: meta.profile (warning)
      };
      
      const warningResult = checkCMSCompliance(warningResource);
      expect(warningResult.score).toBeLessThan(100);
      expect(warningResult.score).toBeGreaterThan(80);
      
      // Resource with errors
      const errorResource: any = {
        resourceType: 'ServiceRequest'
        // Missing many required fields (errors)
      };
      
      const errorResult = checkCMSCompliance(errorResource);
      expect(errorResult.score).toBeLessThan(70);
    });
  });
  
  describe('checkBatchCompliance', () => {
    it('validates multiple resources and aggregates results', () => {
      const input1: X12_278 = {
        authorizationId: 'AUTH001',
        requester: {
          npi: '1234567890'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dob: '19850615'
        },
        payer: {
          payerId: 'PAYER001'
        },
        serviceRequest: {
          serviceTypeCode: 'HS',
          certificationType: 'AR',
          serviceDateFrom: '20240120',
          procedureCode: '27447'
        }
      };
      
      const input2: X12_837 = {
        claimId: 'CLM001',
        billingProvider: {
          npi: '1234567890',
          name: 'Provider Clinic'
        },
        subscriber: {
          memberId: 'MEM002',
          firstName: 'Jane',
          lastName: 'Smith',
          dob: '19900321'
        },
        payer: {
          payerId: 'PAYER001'
        },
        claim: {
          totalChargeAmount: 150.00,
          serviceLines: [
            {
              lineNumber: 1,
              procedureCode: '99213',
              serviceDateFrom: '20240110',
              chargeAmount: 150.00
            }
          ]
        }
      };
      
      const serviceRequest = mapX12278ToFhirServiceRequest(input1);
      const claim = mapX12837ToFhirClaim(input2);
      
      const result = checkBatchCompliance([serviceRequest, claim]);
      
      expect(result.score).toBeGreaterThan(0);
      expect(result.breakdown.dataClasses.total).toBeGreaterThan(0);
      expect(result.breakdown.apiRequirements.total).toBeGreaterThan(0);
      expect(result.issues.length).toBeGreaterThanOrEqual(0);
    });
    
    it('handles empty batch', () => {
      const result = checkBatchCompliance([]);
      
      expect(result.score).toBe(0);
      expect(result.compliant).toBe(true);
      expect(result.issues).toHaveLength(0);
    });
    
    it('aggregates issues from multiple resources', () => {
      const incompleteResource1: any = {
        resourceType: 'ServiceRequest',
        id: 'SR001',
        meta: {
          profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest']
        }
        // Missing required fields
      };
      
      const incompleteResource2: any = {
        resourceType: 'Claim',
        id: 'CLM001',
        meta: {
          profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim']
        }
        // Missing required fields
      };
      
      const result = checkBatchCompliance([incompleteResource1, incompleteResource2]);
      
      expect(result.compliant).toBe(false);
      expect(result.issues.length).toBeGreaterThan(0);
      
      // Should have errors from both resources
      const sr001Errors = result.issues.filter(i => 
        i.severity === 'error' && i.message.includes('ServiceRequest')
      );
      expect(sr001Errors.length).toBeGreaterThan(0);
      
      const clm001Errors = result.issues.filter(i => 
        i.severity === 'error' && i.message.includes('Claim')
      );
      expect(clm001Errors.length).toBeGreaterThan(0);
    });
  });
});
