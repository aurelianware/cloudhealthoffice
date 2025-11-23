import { mapX12270ToFhirEligibility } from '../fhirEligibilityMapper';
import { X12_270 } from '../x12Types';

describe('mapX12270ToFhirEligibility', () => {
  describe('Basic Mapping', () => {
    it('maps minimal X12 270 EDI to FHIR R4 Patient and CoverageEligibilityRequest', () => {
      const sampleInput: X12_270 = {
        inquiryId: 'INQ123456',
        informationSource: {
          id: '030240928',
          name: 'Availity Health Plan'
        },
        subscriber: {
          memberId: 'MEM1001',
          firstName: 'John',
          lastName: 'Doe',
          gender: 'M',
          dob: '19850615',
        },
        insurerId: 'INSURERX',
      };

      const { patient, eligibility } = mapX12270ToFhirEligibility(sampleInput);
      
      // Verify Patient resource structure
      expect(patient.resourceType).toBe('Patient');
      expect(patient.id).toBe('MEM1001');
      expect(patient.active).toBe(true);
      expect(patient.gender).toBe('male');
      expect(patient.birthDate).toBe('1985-06-15');
      
      // Verify name mapping
      expect(patient.name).toHaveLength(1);
      expect(patient.name![0].family).toBe('Doe');
      expect(patient.name![0].given).toContain('John');
      
      // Verify identifier
      expect(patient.identifier).toBeDefined();
      expect(patient.identifier!.length).toBeGreaterThan(0);
      expect(patient.identifier![0].value).toBe('MEM1001');
      
      // Verify CoverageEligibilityRequest structure
      expect(eligibility.resourceType).toBe('CoverageEligibilityRequest');
      expect(eligibility.id).toBe('INQ123456');
      expect(eligibility.status).toBe('active');
      expect(eligibility.patient.reference).toBe('Patient/MEM1001');
      expect(eligibility.insurer.identifier!.value).toBe('INSURERX');
      expect(eligibility.purpose).toContain('validation');
      expect(eligibility.purpose).toContain('benefits');
    });
    
    it('handles dates already in YYYY-MM-DD format', () => {
      const sampleInput: X12_270 = {
        inquiryId: 'INQ123457',
        informationSource: {
          id: '030240928'
        },
        subscriber: {
          memberId: 'MEM1002',
          firstName: 'Jane',
          lastName: 'Smith',
          dob: '1990-03-21', // Already formatted
        },
        insurerId: 'INSURERX',
      };

      const { patient } = mapX12270ToFhirEligibility(sampleInput);
      expect(patient.birthDate).toBe('1990-03-21');
    });
  });

  describe('Gender Mapping', () => {
    it('maps X12 gender code M to FHIR male', () => {
      const input: X12_270 = {
        inquiryId: 'INQ001',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          gender: 'M',
          dob: '1985-01-01',
        },
        insurerId: 'TEST',
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      expect(patient.gender).toBe('male');
    });

    it('maps X12 gender code F to FHIR female', () => {
      const input: X12_270 = {
        inquiryId: 'INQ002',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM002',
          firstName: 'Jane',
          lastName: 'Doe',
          gender: 'F',
          dob: '1985-01-01',
        },
        insurerId: 'TEST',
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      expect(patient.gender).toBe('female');
    });

    it('maps X12 gender code U to FHIR unknown', () => {
      const input: X12_270 = {
        inquiryId: 'INQ003',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM003',
          firstName: 'Alex',
          lastName: 'Doe',
          gender: 'U',
          dob: '1985-01-01',
        },
        insurerId: 'TEST',
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      expect(patient.gender).toBe('unknown');
    });
    
    it('handles missing gender as unknown', () => {
      const input: X12_270 = {
        inquiryId: 'INQ004',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM004',
          firstName: 'Jordan',
          lastName: 'Doe',
          dob: '1985-01-01',
        },
        insurerId: 'TEST',
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      expect(patient.gender).toBe('unknown');
    });
  });

  describe('Comprehensive Demographic Mapping', () => {
    it('maps complete subscriber information including address and contact', () => {
      const input: X12_270 = {
        inquiryId: 'INQ200',
        transactionDate: '20240115-1430',
        informationSource: {
          id: '030240928',
          name: 'Availity Health Network',
          taxId: '12-3456789'
        },
        informationReceiver: {
          npi: '1234567890',
          organizationName: 'City Medical Center',
          taxId: '98-7654321'
        },
        subscriber: {
          memberId: 'MEM2001',
          firstName: 'Robert',
          lastName: 'Johnson',
          middleName: 'Michael',
          dob: '19750810',
          gender: 'M',
          ssn: '123-45-6789',
          address: {
            street1: '123 Main Street',
            street2: 'Apt 4B',
            city: 'Austin',
            state: 'TX',
            zip: '78701',
            country: 'US'
          },
          phone: '512-555-1234',
          email: 'robert.johnson@example.com'
        },
        insurerId: 'TXHEALTH01',
        tradingPartnerId: 'AVLTX001',
        serviceTypeCodes: ['30', '48', '98'],
        serviceDateRange: {
          startDate: '20240101',
          endDate: '20241231'
        }
      };

      const { patient, eligibility } = mapX12270ToFhirEligibility(input);
      
      // Verify comprehensive patient demographics
      expect(patient.name![0].given).toEqual(['Robert', 'Michael']);
      expect(patient.name![0].family).toBe('Johnson');
      
      // Verify identifiers including SSN
      expect(patient.identifier!.length).toBe(2);
      const memberIdIdent = patient.identifier!.find(i => i.type?.coding?.[0].code === 'MB');
      expect(memberIdIdent?.value).toBe('MEM2001');
      const ssnIdent = patient.identifier!.find(i => i.type?.coding?.[0].code === 'SS');
      expect(ssnIdent?.value).toBe('123-45-6789');
      
      // Verify address
      expect(patient.address).toHaveLength(1);
      expect(patient.address![0].line).toEqual(['123 Main Street', 'Apt 4B']);
      expect(patient.address![0].city).toBe('Austin');
      expect(patient.address![0].state).toBe('TX');
      expect(patient.address![0].postalCode).toBe('78701');
      expect(patient.address![0].country).toBe('US');
      
      // Verify telecom
      expect(patient.telecom).toHaveLength(2);
      const phone = patient.telecom!.find(t => t.system === 'phone');
      expect(phone?.value).toBe('512-555-1234');
      const email = patient.telecom!.find(t => t.system === 'email');
      expect(email?.value).toBe('robert.johnson@example.com');
      
      // Verify eligibility request details
      expect(eligibility.created).toBe('2024-01-15T14:30:00Z');
      expect(eligibility.provider?.identifier?.value).toBe('1234567890');
      expect(eligibility.servicedPeriod?.start).toBe('2024-01-01');
      expect(eligibility.servicedPeriod?.end).toBe('2024-12-31');
      
      // Verify service items
      expect(eligibility.item).toHaveLength(3);
      expect(eligibility.item![0].category?.coding?.[0].code).toBe('30');
    });
  });

  describe('Dependent Mapping', () => {
    it('maps dependent information when different from subscriber', () => {
      const input: X12_270 = {
        inquiryId: 'INQ300',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM3001',
          firstName: 'John',
          lastName: 'Parent',
          dob: '1980-01-01',
          gender: 'M'
        },
        dependent: {
          relationshipCode: '19', // Child
          firstName: 'Emily',
          lastName: 'Parent',
          dob: '2010-05-15',
          gender: 'F'
        },
        insurerId: 'FAMILYPLAN01'
      };

      const { patient, eligibility } = mapX12270ToFhirEligibility(input);
      
      // Should map dependent, not subscriber
      expect(patient.name![0].given).toContain('Emily');
      expect(patient.name![0].family).toBe('Parent');
      expect(patient.birthDate).toBe('2010-05-15');
      expect(patient.gender).toBe('female');
      
      // Member ID should still be subscriber's
      expect(patient.id).toBe('MEM3001');
    });
  });

  describe('Service Type Mapping', () => {
    it('maps multiple X12 service type codes to FHIR benefit categories', () => {
      const input: X12_270 = {
        inquiryId: 'INQ400',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM4001',
          firstName: 'Test',
          lastName: 'User',
          dob: '1980-01-01'
        },
        insurerId: 'TEST',
        serviceTypeCodes: ['1', '30', '48', '98']
      };

      const { eligibility } = mapX12270ToFhirEligibility(input);
      
      expect(eligibility.item).toHaveLength(4);
      expect(eligibility.item![0].category?.coding?.[0].code).toBe('1');
      expect(eligibility.item![0].category?.coding?.[0].display).toBe('Medical Care');
      expect(eligibility.item![1].category?.coding?.[0].code).toBe('30');
      expect(eligibility.item![1].category?.coding?.[0].display).toBe('Health Benefit Plan Coverage');
    });
    
    it('uses default service category when no service types provided', () => {
      const input: X12_270 = {
        inquiryId: 'INQ401',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM4002',
          firstName: 'Test',
          lastName: 'User',
          dob: '1980-01-01'
        },
        insurerId: 'TEST'
      };

      const { eligibility } = mapX12270ToFhirEligibility(input);
      
      expect(eligibility.item).toHaveLength(1);
      expect(eligibility.item![0].category?.coding?.[0].code).toBe('30');
      expect(eligibility.item![0].category?.coding?.[0].display).toBe('Health Benefit Plan Coverage');
    });
  });

  describe('Date Format Handling', () => {
    it('converts CCYYMMDD format to YYYY-MM-DD', () => {
      const input: X12_270 = {
        inquiryId: 'INQ500',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM5001',
          firstName: 'Test',
          lastName: 'User',
          dob: '19850615' // X12 format
        },
        insurerId: 'TEST'
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      expect(patient.birthDate).toBe('1985-06-15');
    });
    
    it('preserves existing YYYY-MM-DD format', () => {
      const input: X12_270 = {
        inquiryId: 'INQ501',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM5002',
          firstName: 'Test',
          lastName: 'User',
          dob: '1985-06-15' // Already formatted
        },
        insurerId: 'TEST'
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      expect(patient.birthDate).toBe('1985-06-15');
    });
    
    it('parses X12 date-time format correctly', () => {
      const input: X12_270 = {
        inquiryId: 'INQ502',
        transactionDate: '20240115-1430',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM5003',
          firstName: 'Test',
          lastName: 'User',
          dob: '1985-01-01'
        },
        insurerId: 'TEST'
      };

      const { eligibility } = mapX12270ToFhirEligibility(input);
      expect(eligibility.created).toBe('2024-01-15T14:30:00Z');
    });
  });

  describe('FHIR Profile Compliance', () => {
    it('includes US Core Patient profile reference', () => {
      const input: X12_270 = {
        inquiryId: 'INQ600',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM6001',
          firstName: 'Test',
          lastName: 'User',
          dob: '1980-01-01'
        },
        insurerId: 'TEST'
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      expect(patient.meta?.profile).toContain('http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient');
    });
    
    it('includes proper identifier type codings per v2-0203', () => {
      const input: X12_270 = {
        inquiryId: 'INQ601',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM6002',
          firstName: 'Test',
          lastName: 'User',
          dob: '1980-01-01'
        },
        insurerId: 'TEST'
      };

      const { patient } = mapX12270ToFhirEligibility(input);
      const memberIdent = patient.identifier![0];
      expect(memberIdent.type?.coding?.[0].system).toBe('http://terminology.hl7.org/CodeSystem/v2-0203');
      expect(memberIdent.type?.coding?.[0].code).toBe('MB');
      expect(memberIdent.type?.coding?.[0].display).toBe('Member Number');
    });
    
    it('uses proper NPI identifier system', () => {
      const input: X12_270 = {
        inquiryId: 'INQ602',
        informationSource: { id: '030240928' },
        informationReceiver: {
          npi: '1234567890'
        },
        subscriber: {
          memberId: 'MEM6003',
          firstName: 'Test',
          lastName: 'User',
          dob: '1980-01-01'
        },
        insurerId: 'TEST'
      };

      const { eligibility } = mapX12270ToFhirEligibility(input);
      expect(eligibility.provider?.identifier?.system).toBe('http://hl7.org/fhir/sid/us-npi');
    });
  });

  describe('CMS Interoperability Compliance', () => {
    it('supports Patient Access API requirements', () => {
      // This test demonstrates compliance with CMS-9115-F Patient Access final rule
      const input: X12_270 = {
        inquiryId: 'INQ700',
        informationSource: { id: '030240928', name: 'Example Health Plan' },
        informationReceiver: { npi: '1234567890' },
        subscriber: {
          memberId: 'MEM7001',
          firstName: 'Patient',
          lastName: 'Example',
          dob: '1980-01-01',
          gender: 'F'
        },
        insurerId: 'CMSCOMPLIANT',
        serviceTypeCodes: ['30']
      };

      const { patient, eligibility } = mapX12270ToFhirEligibility(input);
      
      // Patient resource must be valid
      expect(patient.resourceType).toBe('Patient');
      expect(patient.id).toBeDefined();
      expect(patient.identifier).toBeDefined();
      expect(patient.name).toBeDefined();
      expect(patient.gender).toBeDefined();
      expect(patient.birthDate).toBeDefined();
      
      // CoverageEligibilityRequest must be valid
      expect(eligibility.resourceType).toBe('CoverageEligibilityRequest');
      expect(eligibility.status).toBeDefined();
      expect(eligibility.purpose).toBeDefined();
      expect(eligibility.patient.reference).toBeDefined();
      expect(eligibility.insurer.identifier).toBeDefined();
    });
  });

  describe('Edge Cases and Error Handling', () => {
    it('handles minimal required fields only', () => {
      const minimalInput: X12_270 = {
        inquiryId: 'INQ800',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM8001',
          firstName: 'Min',
          lastName: 'Fields',
          dob: '1980-01-01'
        },
        insurerId: 'MINIMAL'
      };

      const { patient, eligibility } = mapX12270ToFhirEligibility(minimalInput);
      
      expect(patient.resourceType).toBe('Patient');
      expect(patient.id).toBe('MEM8001');
      expect(eligibility.resourceType).toBe('CoverageEligibilityRequest');
      expect(eligibility.id).toBe('INQ800');
    });
    
    it('handles empty optional arrays gracefully', () => {
      const input: X12_270 = {
        inquiryId: 'INQ801',
        informationSource: { id: '030240928' },
        subscriber: {
          memberId: 'MEM8002',
          firstName: 'No',
          lastName: 'Services',
          dob: '1980-01-01'
        },
        insurerId: 'TEST',
        serviceTypeCodes: [] // Empty array
      };

      const { eligibility } = mapX12270ToFhirEligibility(input);
      
      // Should provide default service category
      expect(eligibility.item).toHaveLength(1);
      expect(eligibility.item![0].category?.coding?.[0].code).toBe('30');
    });
  });
});