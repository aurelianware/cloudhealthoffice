/**
 * Cloud Health Office - Eligibility Service Tests
 */

import { X12EligibilityMapper } from '../src/x12-mapper';
import { FHIREligibilityMapper } from '../src/fhir-mapper';
import { 
  X12_270_Request, 
  X12_271_Response,
  QNXTEligibilityRule 
} from '../src/types';
import { loadQNXTRulesFromCSV, validateRules, generateSampleCSV } from '../src/migration';
import * as fs from 'fs';
import * as path from 'path';

describe('X12EligibilityMapper', () => {
  const mapper = new X12EligibilityMapper();

  describe('getServiceTypeDescription', () => {
    it('returns correct description for known service type codes', () => {
      expect(mapper.getServiceTypeDescription('30')).toBe('Health Benefit Plan Coverage');
      expect(mapper.getServiceTypeDescription('48')).toBe('Hospital Inpatient');
      expect(mapper.getServiceTypeDescription('85')).toBe('Emergency Services');
      expect(mapper.getServiceTypeDescription('MH')).toBe('Mental Health');
    });

    it('returns generic description for unknown codes', () => {
      expect(mapper.getServiceTypeDescription('ZZ')).toBe('Service Type ZZ');
    });
  });

  describe('getEligibilityInfoDescription', () => {
    it('returns correct description for known eligibility codes', () => {
      expect(mapper.getEligibilityInfoDescription('1')).toBe('Active Coverage');
      expect(mapper.getEligibilityInfoDescription('6')).toBe('Inactive');
      expect(mapper.getEligibilityInfoDescription('B')).toBe('Co-Payment');
    });

    it('returns generic description for unknown codes', () => {
      expect(mapper.getEligibilityInfoDescription('ZZ')).toBe('Code ZZ');
    });
  });

  describe('generateX12271', () => {
    it('generates valid X12 271 EDI string', () => {
      const response: X12_271_Response = {
        transactionControlNumber: '123456789',
        responseControlNumber: '987654321',
        transactionDate: '20240115',
        transactionTime: '1200',
        informationSource: {
          entityIdentifier: 'PR',
          name: 'Test Health Plan',
          identificationCode: 'TESTPLAN'
        },
        informationReceiver: {
          entityIdentifier: '1P',
          npi: '1234567890'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dateOfBirth: '19850615',
          gender: 'M',
          planName: 'Gold PPO'
        },
        eligibilityStatus: 'active',
        benefits: [
          {
            serviceTypeCode: '30',
            eligibilityInfoCode: '1',
            coverageLevelCode: 'IND',
            inNetwork: true
          }
        ]
      };

      const edi = mapper.generateX12271(response);
      
      expect(edi).toContain('ISA*');
      expect(edi).toContain('ST*271');
      expect(edi).toContain('BHT*');
      expect(edi).toContain('NM1*PR*2*Test Health Plan');
      expect(edi).toContain('NM1*IL*1*Doe*John');
      expect(edi).toContain('EB*1*IND*30');
      expect(edi).toContain('SE*');
      expect(edi).toContain('IEA*');
    });
  });
});

describe('FHIREligibilityMapper', () => {
  const mapper = new FHIREligibilityMapper();

  describe('createCoverageEligibilityRequest', () => {
    it('creates valid FHIR CoverageEligibilityRequest', () => {
      const request = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM001',
        insurerId: 'HEALTHPLAN',
        providerId: '1234567890',
        servicedDate: '2024-01-15',
        serviceTypeCodes: ['30', '48']
      });

      expect(request.resourceType).toBe('CoverageEligibilityRequest');
      expect(request.status).toBe('active');
      expect(request.purpose).toContain('validation');
      expect(request.purpose).toContain('benefits');
      expect(request.patient?.identifier?.value).toBe('MEM001');
      expect(request.insurer?.identifier?.value).toBe('HEALTHPLAN');
      expect(request.provider?.identifier?.value).toBe('1234567890');
      expect(request.item).toHaveLength(2);
    });

    it('handles optional parameters correctly', () => {
      const request = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM002',
        insurerId: 'HEALTHPLAN'
      });

      expect(request.resourceType).toBe('CoverageEligibilityRequest');
      expect(request.provider).toBeUndefined();
      expect(request.servicedDate).toBeUndefined();
      expect(request.item).toBeUndefined();
    });
  });

  describe('fhirToX12', () => {
    it('converts FHIR CoverageEligibilityRequest to X12 270', () => {
      const fhirRequest = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM003',
        insurerId: 'TESTPAYER',
        providerId: '9876543210',
        serviceTypeCodes: ['30']
      });

      const x12Request = mapper.fhirToX12(fhirRequest);

      expect(x12Request.subscriber.memberId).toBe('MEM003');
      expect(x12Request.informationSource.identificationCode).toBe('TESTPAYER');
      expect(x12Request.informationReceiver?.npi).toBe('9876543210');
      expect(x12Request.serviceTypeCodes).toContain('30');
    });
  });

  describe('x12ToFhir', () => {
    it('converts X12 271 Response to FHIR CoverageEligibilityResponse', () => {
      const x12Response: X12_271_Response = {
        transactionControlNumber: '123',
        responseControlNumber: '456',
        transactionDate: '20240115',
        informationSource: {
          entityIdentifier: 'PR',
          name: 'Test Plan',
          identificationCode: 'TESTPLAN'
        },
        informationReceiver: {
          entityIdentifier: '1P'
        },
        subscriber: {
          memberId: 'MEM004',
          firstName: 'Jane',
          lastName: 'Smith',
          dateOfBirth: '19900301'
        },
        eligibilityStatus: 'active',
        benefits: [
          {
            serviceTypeCode: '30',
            eligibilityInfoCode: '1',
            inNetwork: true,
            additionalInfo: {
              copay: 25
            }
          }
        ]
      };

      const fhirRequest = mapper.createCoverageEligibilityRequest({
        patientId: 'MEM004',
        insurerId: 'TESTPLAN'
      });

      const fhirResponse = mapper.x12ToFhir(x12Response, fhirRequest);

      expect(fhirResponse.resourceType).toBe('CoverageEligibilityResponse');
      expect(fhirResponse.outcome).toBe('complete');
      expect(fhirResponse.insurance).toHaveLength(1);
      expect(fhirResponse.insurance![0].inforce).toBe(true);
    });
  });

  describe('createPatientFromX12', () => {
    it('creates valid FHIR Patient from X12 subscriber data', () => {
      const subscriberData: X12_270_Request['subscriber'] = {
        memberId: 'MEM005',
        firstName: 'Robert',
        lastName: 'Johnson',
        middleName: 'Michael',
        dateOfBirth: '19750810',
        gender: 'M',
        ssn: '123-45-6789',
        address: {
          line1: '123 Main St',
          city: 'Austin',
          state: 'TX',
          postalCode: '78701'
        }
      };

      const patient = mapper.createPatientFromX12(subscriberData);

      expect(patient.resourceType).toBe('Patient');
      expect(patient.id).toBe('MEM005');
      expect(patient.name![0].family).toBe('Johnson');
      expect(patient.name![0].given).toContain('Robert');
      expect(patient.name![0].given).toContain('Michael');
      expect(patient.gender).toBe('male');
      expect(patient.birthDate).toBe('1975-08-10');
      expect(patient.identifier).toHaveLength(2);
      expect(patient.address![0].city).toBe('Austin');
    });
  });
});

describe('QNXT Migration', () => {
  const tempDir = '/tmp/eligibility-test';
  const sampleCsvPath = path.join(tempDir, 'test-rules.csv');

  beforeAll(() => {
    if (!fs.existsSync(tempDir)) {
      fs.mkdirSync(tempDir, { recursive: true });
    }
  });

  afterAll(() => {
    if (fs.existsSync(sampleCsvPath)) {
      fs.unlinkSync(sampleCsvPath);
    }
  });

  describe('generateSampleCSV', () => {
    it('generates a valid sample CSV file', () => {
      generateSampleCSV(sampleCsvPath);
      
      expect(fs.existsSync(sampleCsvPath)).toBe(true);
      
      const content = fs.readFileSync(sampleCsvPath, 'utf-8');
      const lines = content.split('\n');
      
      expect(lines.length).toBeGreaterThan(1);
      expect(lines[0]).toContain('rule_id');
      expect(lines[0]).toContain('plan_code');
      expect(lines[0]).toContain('service_type_code');
    });
  });

  describe('loadQNXTRulesFromCSV', () => {
    it('loads rules from CSV file', async () => {
      generateSampleCSV(sampleCsvPath);
      
      const rules = await loadQNXTRulesFromCSV(sampleCsvPath);
      
      expect(rules.length).toBeGreaterThan(0);
      expect(rules[0].ruleId).toBeDefined();
      expect(rules[0].planCode).toBeDefined();
      expect(rules[0].serviceTypeCode).toBeDefined();
    });

    it('throws error for non-existent file', async () => {
      await expect(loadQNXTRulesFromCSV('/nonexistent/file.csv'))
        .rejects.toThrow('CSV file not found');
    });
  });

  describe('validateRules', () => {
    it('validates valid rules successfully', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: 'RULE001',
          ruleName: 'Test Rule',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: {
            startDate: '20240101'
          },
          priority: 10,
          isActive: true
        }
      ];

      const result = validateRules(rules);
      
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('reports errors for invalid rules', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: '',
          ruleName: 'Invalid Rule',
          planCode: '',
          serviceTypeCode: '',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          effectiveDateRange: {
            startDate: ''
          },
          priority: -1,
          isActive: true
        }
      ];

      const result = validateRules(rules);
      
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
    });

    it('validates coinsurance percentages', () => {
      const rules: QNXTEligibilityRule[] = [
        {
          ruleId: 'RULE002',
          ruleName: 'Invalid Coinsurance',
          planCode: 'PPO_GOLD',
          serviceTypeCode: '30',
          benefitCategory: 'General',
          coverageIndicator: 'covered',
          priorAuthRequired: false,
          referralRequired: false,
          inNetworkRequirements: {
            coinsurance: 150 // Invalid - should be 0-100
          },
          effectiveDateRange: {
            startDate: '20240101'
          },
          priority: 10,
          isActive: true
        }
      ];

      const result = validateRules(rules);
      
      expect(result.valid).toBe(false);
      expect(result.errors.some(e => e.includes('coinsurance'))).toBe(true);
    });
  });
});

describe('Types', () => {
  it('X12_270_Request has correct structure', () => {
    const request: X12_270_Request = {
      transactionControlNumber: '123',
      interchangeControlNumber: '456',
      transactionDate: '20240115',
      informationSource: {
        entityIdentifier: 'PR',
        entityType: '2',
        name: 'Test Plan',
        identificationCode: 'TEST',
        identificationCodeQualifier: 'PI'
      },
      informationReceiver: {
        entityIdentifier: '1P',
        entityType: '1'
      },
      subscriber: {
        memberId: 'MEM001',
        firstName: 'John',
        lastName: 'Doe',
        dateOfBirth: '19850615'
      }
    };

    expect(request.transactionControlNumber).toBe('123');
    expect(request.subscriber.memberId).toBe('MEM001');
  });

  it('QNXTEligibilityRule has correct structure', () => {
    const rule: QNXTEligibilityRule = {
      ruleId: 'RULE001',
      ruleName: 'Test Rule',
      planCode: 'PPO_GOLD',
      serviceTypeCode: '30',
      benefitCategory: 'Preventive Care',
      coverageIndicator: 'covered',
      priorAuthRequired: false,
      referralRequired: false,
      inNetworkRequirements: {
        copay: 0,
        coinsurance: 0,
        deductibleApplies: false
      },
      outOfNetworkRequirements: {
        copay: 50,
        coinsurance: 40,
        deductibleApplies: true,
        coveragePercent: 60
      },
      quantityLimits: {
        maxQuantity: 1,
        quantityPeriod: 'year'
      },
      effectiveDateRange: {
        startDate: '20240101'
      },
      priority: 10,
      isActive: true
    };

    expect(rule.ruleId).toBe('RULE001');
    expect(rule.inNetworkRequirements?.copay).toBe(0);
    expect(rule.quantityLimits?.quantityPeriod).toBe('year');
  });
});
