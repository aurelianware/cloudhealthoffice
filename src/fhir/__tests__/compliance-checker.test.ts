import { 
  validateCMS0057FCompliance,
  validateBatchCompliance,
  generateComplianceReport,
  ComplianceResult
} from '../compliance-checker';
import { ServiceRequest, ExplanationOfBenefit, Claim, Patient } from 'fhir/r4';

describe('CMS-0057-F Compliance Checker', () => {
  
  describe('validateCMS0057FCompliance - ServiceRequest', () => {
    it('validates compliant ServiceRequest for prior authorization', () => {
      const serviceRequest: ServiceRequest = {
        resourceType: 'ServiceRequest',
        id: 'auth-001',
        status: 'draft',
        intent: 'order',
        priority: 'urgent',
        subject: {
          reference: 'Patient/12345',
          display: 'John Doe'
        },
        authoredOn: new Date().toISOString(),
        requester: {
          reference: 'Practitioner/98765',
          identifier: {
            system: 'http://hl7.org/fhir/sid/us-npi',
            value: '1234567890'
          }
        },
        insurance: [{
          reference: 'Coverage/12345'
        }],
        code: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: '99213'
          }]
        },
        reasonCode: [{
          coding: [{
            system: 'http://hl7.org/fhir/sid/icd-10',
            code: 'E11.9'
          }]
        }],
        occurrencePeriod: {
          start: '2024-03-01',
          end: '2024-03-01'
        }
      };

      const result = validateCMS0057FCompliance(serviceRequest);

      expect(result.compliant).toBe(true);
      expect(result.issues.filter(i => i.severity === 'error')).toHaveLength(0);
      expect(result.summary.resourceType).toBe('ServiceRequest');
      expect(result.summary.daVinciProfiles).toContain('PAS ServiceRequest');
      expect(result.summary.uscdiDataClasses.length).toBeGreaterThan(0);
    });

    it('flags missing required elements in ServiceRequest', () => {
      const serviceRequest: ServiceRequest = {
        resourceType: 'ServiceRequest',
        id: 'auth-002',
        status: 'draft',
        intent: 'order',
        subject: {
          reference: 'Patient/12345'
        }
        // Missing requester, insurance, code
      };

      const result = validateCMS0057FCompliance(serviceRequest);

      expect(result.compliant).toBe(false);
      expect(result.issues.filter(i => i.severity === 'error').length).toBeGreaterThan(0);
      
      const errorCodes = result.issues.map(i => i.code);
      expect(errorCodes).toContain('MISSING_REQUESTER');
      expect(errorCodes).toContain('MISSING_SERVICE_CODE');
    });

    it('checks prior authorization timeline compliance', () => {
      const recentRequest: ServiceRequest = {
        resourceType: 'ServiceRequest',
        id: 'auth-003',
        status: 'draft',
        intent: 'order',
        priority: 'urgent',
        subject: { reference: 'Patient/12345' },
        requester: { reference: 'Practitioner/98765' },
        authoredOn: new Date(Date.now() - 1000 * 60 * 60 * 24).toISOString(), // 24 hours ago
        insurance: [{ reference: 'Coverage/12345' }],
        code: { coding: [{ code: '99213' }] }
      };

      const result = validateCMS0057FCompliance(recentRequest);

      expect(result.summary.timelineCompliance.applicable).toBe(true);
      expect(result.summary.timelineCompliance.compliant).toBe(true);
      expect(result.summary.timelineCompliance.deadline).toBe('72 hours');
    });
  });

  describe('validateCMS0057FCompliance - ExplanationOfBenefit', () => {
    it('validates compliant ExplanationOfBenefit', () => {
      const eob: ExplanationOfBenefit = {
        resourceType: 'ExplanationOfBenefit',
        id: 'eob-001',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'claim',
        patient: {
          reference: 'Patient/54321'
        },
        created: '2024-02-15',
        insurer: {
          reference: 'Organization/payer-001'
        },
        provider: {
          reference: 'Organization/provider-001'
        },
        outcome: 'complete',
        insurance: [{
          focal: true,
          coverage: { reference: 'Coverage/54321' }
        }],
        item: [{
          sequence: 1,
          productOrService: {
            coding: [{
              system: 'http://www.ama-assn.org/go/cpt',
              code: '99213'
            }]
          },
          adjudication: [{
            category: {
              coding: [{
                system: 'http://terminology.hl7.org/CodeSystem/adjudication',
                code: 'submitted'
              }]
            },
            amount: { value: 150, currency: 'USD' }
          }]
        }],
        total: [{
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'submitted'
            }]
          },
          amount: { value: 150, currency: 'USD' }
        }],
        payment: {
          amount: { value: 135, currency: 'USD' },
          date: '2024-02-15'
        }
      };

      const result = validateCMS0057FCompliance(eob);

      expect(result.compliant).toBe(true);
      expect(result.summary.resourceType).toBe('ExplanationOfBenefit');
      expect(result.summary.uscdiDataClasses).toContain('Patient Demographics');
      expect(result.summary.uscdiDataClasses).toContain('Financial');
    });

    it('flags missing items in ExplanationOfBenefit', () => {
      const eob: ExplanationOfBenefit = {
        resourceType: 'ExplanationOfBenefit',
        id: 'eob-002',
        status: 'active',
        type: {
          coding: [{ code: 'professional' }]
        },
        use: 'claim',
        patient: { reference: 'Patient/54321' },
        created: '2024-02-15',
        insurer: { reference: 'Organization/payer-001' },
        provider: { reference: 'Organization/provider-001' },
        outcome: 'complete',
        insurance: [{
          focal: true,
          coverage: { reference: 'Coverage/54321' }
        }]
        // Missing items
      };

      const result = validateCMS0057FCompliance(eob);

      expect(result.compliant).toBe(false);
      const errorCodes = result.issues.filter(i => i.severity === 'error').map(i => i.code);
      expect(errorCodes).toContain('MISSING_ITEMS');
    });
  });

  describe('validateCMS0057FCompliance - Claim', () => {
    it('validates compliant Claim resource', () => {
      const claim: Claim = {
        resourceType: 'Claim',
        id: 'claim-001',
        status: 'active',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/claim-type',
            code: 'professional'
          }]
        },
        use: 'claim',
        patient: {
          reference: 'Patient/67890'
        },
        created: '2024-01-15',
        provider: {
          reference: 'Organization/provider-001'
        },
        priority: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/processpriority',
            code: 'normal'
          }]
        },
        insurance: [{
          sequence: 1,
          focal: true,
          coverage: { reference: 'Coverage/67890' }
        }],
        item: [{
          sequence: 1,
          productOrService: {
            coding: [{ code: '99213' }]
          },
          quantity: { value: 1 },
          net: { value: 150, currency: 'USD' }
        }],
        diagnosis: [{
          sequence: 1,
          diagnosisCodeableConcept: {
            coding: [{
              system: 'http://hl7.org/fhir/sid/icd-10',
              code: 'Z00.00'
            }]
          }
        }]
      };

      const result = validateCMS0057FCompliance(claim);

      expect(result.compliant).toBe(true);
      expect(result.summary.uscdiDataClasses).toContain('Patient Demographics');
      expect(result.summary.uscdiDataClasses).toContain('Procedures');
      expect(result.summary.uscdiDataClasses).toContain('Problems');
    });

    it('validates Claim with missing diagnosis', () => {
      const claim: Claim = {
        resourceType: 'Claim',
        id: 'claim-002',
        status: 'active',
        type: { coding: [{ code: 'professional' }] },
        use: 'claim',
        patient: { reference: 'Patient/67890' },
        created: '2024-01-15',
        provider: { reference: 'Organization/provider-001' },
        priority: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/processpriority',
            code: 'normal'
          }]
        },
        insurance: [{
          sequence: 1,
          focal: true,
          coverage: { reference: 'Coverage/67890' }
        }],
        item: [{
          sequence: 1,
          productOrService: { coding: [{ code: '99213' }] },
          quantity: { value: 1 }
        }]
        // Missing diagnosis
      };

      const result = validateCMS0057FCompliance(claim);

      expect(result.compliant).toBe(true); // Diagnosis is warning, not error
      expect(result.warnings.some(w => w.code === 'MISSING_DIAGNOSIS')).toBe(true);
    });
  });

  describe('validateCMS0057FCompliance - Patient', () => {
    it('validates US Core compliant Patient', () => {
      const patient: Patient = {
        resourceType: 'Patient',
        id: 'pat-001',
        meta: {
          profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
        },
        identifier: [{
          system: 'http://hospital.example.org',
          value: 'MRN12345'
        }],
        name: [{
          family: 'Doe',
          given: ['John']
        }],
        gender: 'male',
        birthDate: '1980-01-15',
        address: [{
          line: ['123 Main St'],
          city: 'Anytown',
          state: 'CA',
          postalCode: '90210'
        }],
        telecom: [{
          system: 'phone',
          value: '555-1234'
        }]
      };

      const result = validateCMS0057FCompliance(patient);

      expect(result.compliant).toBe(true);
      expect(result.summary.resourceType).toBe('Patient');
      expect(result.summary.usCoreSections).toContain('US Core Patient');
      expect(result.summary.uscdiDataClasses).toContain('Patient Demographics');
    });

    it('flags missing US Core required elements', () => {
      const patient: Patient = {
        resourceType: 'Patient',
        id: 'pat-002'
        // Missing identifier, name, gender (required by US Core)
      };

      const result = validateCMS0057FCompliance(patient);

      expect(result.compliant).toBe(false);
      
      const errorCodes = result.issues.filter(i => i.severity === 'error').map(i => i.code);
      expect(errorCodes).toContain('MISSING_IDENTIFIER');
      expect(errorCodes).toContain('MISSING_NAME');
      expect(errorCodes).toContain('MISSING_GENDER');
    });
  });

  describe('validateBatchCompliance', () => {
    it('validates multiple resources at once', () => {
      const resources = [
        {
          resourceType: 'Patient',
          id: 'pat-001',
          identifier: [{ value: 'MRN001' }],
          name: [{ family: 'Smith', given: ['Jane'] }],
          gender: 'female'
        } as Patient,
        {
          resourceType: 'ServiceRequest',
          id: 'auth-001',
          status: 'draft',
          intent: 'order',
          subject: { reference: 'Patient/pat-001' },
          requester: { reference: 'Practitioner/123' },
          code: { coding: [{ code: '99213' }] },
          insurance: [{ reference: 'Coverage/pat-001' }]
        } as ServiceRequest
      ];

      const results = validateBatchCompliance(resources);

      expect(results).toHaveLength(2);
      expect(results[0].summary.resourceType).toBe('Patient');
      expect(results[1].summary.resourceType).toBe('ServiceRequest');
    });
  });

  describe('generateComplianceReport', () => {
    it('generates comprehensive compliance report', () => {
      const results: ComplianceResult[] = [
        {
          compliant: true,
          issues: [],
          warnings: [],
          summary: {
            resourceType: 'Patient',
            requiredElementsPresent: 7,
            totalRequiredElements: 7,
            usCoreSections: ['US Core Patient'],
            daVinciProfiles: ['PDex Patient'],
            uscdiDataClasses: ['Patient Demographics'],
            timelineCompliance: { applicable: false }
          }
        },
        {
          compliant: false,
          issues: [
            { severity: 'error' as const, code: 'MISSING_STATUS', message: 'Status required' }
          ],
          warnings: [
            { code: 'MISSING_REASON', message: 'Reason recommended' }
          ],
          summary: {
            resourceType: 'ServiceRequest',
            requiredElementsPresent: 8,
            totalRequiredElements: 10,
            usCoreSections: ['ServiceRequest'],
            daVinciProfiles: ['PAS ServiceRequest'],
            uscdiDataClasses: ['Patient Demographics', 'Procedures'],
            timelineCompliance: { applicable: true, compliant: true, deadline: '72 hours' }
          }
        }
      ];

      const report = generateComplianceReport(results);

      expect(report).toContain('CMS-0057-F Compliance Report');
      expect(report).toContain('Total Resources Validated: 2');
      expect(report).toContain('Compliant Resources: 1');
      expect(report).toContain('Patient');
      expect(report).toContain('ServiceRequest');
      expect(report).toContain('USCDI Data Classes');
      expect(report).toContain('Da Vinci Profiles');
    });

    it('generates report for all compliant resources', () => {
      const results: ComplianceResult[] = [
        {
          compliant: true,
          issues: [],
          warnings: [],
          summary: {
            resourceType: 'Patient',
            requiredElementsPresent: 7,
            totalRequiredElements: 7,
            usCoreSections: [],
            daVinciProfiles: [],
            uscdiDataClasses: [],
            timelineCompliance: { applicable: false }
          }
        }
      ];

      const report = generateComplianceReport(results);

      expect(report).toContain('✓ No critical issues found');
      expect(report).toContain('✓ No warnings');
    });
  });
});
