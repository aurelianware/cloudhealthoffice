/**
 * Tests for CMS-0057-F FHIR Mappers
 */

import { 
  mapX12837ToFhirClaim, 
  mapX12278ToFhirServiceRequest, 
  mapX12835ToFhirExplanationOfBenefit 
} from '../fhir-mapper';
import { X12_837, X12_278, X12_835 } from '../x12Types';

describe('CMS-0057-F FHIR Mappers', () => {
  describe('mapX12837ToFhirClaim', () => {
    it('maps minimal X12 837 to FHIR R4 Claim', () => {
      const input: X12_837 = {
        claimId: 'CLM001',
        transactionDate: '20240115',
        billingProvider: {
          npi: '1234567890',
          name: 'Provider Clinic',
          taxId: '12-3456789'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dob: '19850615',
          gender: 'M'
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
              chargeAmount: 150.00,
              units: 1
            }
          ]
        }
      };
      
      const claim = mapX12837ToFhirClaim(input);
      
      // Verify resource structure
      expect(claim.resourceType).toBe('Claim');
      expect(claim.id).toBe('CLM001');
      expect(claim.status).toBe('active');
      expect(claim.type.coding![0].code).toBe('professional');
      expect(claim.use).toBe('claim');
      
      // Verify patient reference
      expect(claim.patient.reference).toBe('Patient/MEM001');
      expect(claim.patient.identifier!.value).toBe('MEM001');
      
      // Verify provider
      expect(claim.provider.identifier!.value).toBe('1234567890');
      expect(claim.provider.identifier!.system).toBe('http://hl7.org/fhir/sid/us-npi');
      
      // Verify insurer
      expect(claim.insurer?.identifier!.value).toBe('PAYER001');
      
      // Verify service lines
      expect(claim.item).toHaveLength(1);
      expect(claim.item![0].sequence).toBe(1);
      expect(claim.item![0].productOrService.coding![0].code).toBe('99213');
      expect(claim.item![0].net!.value).toBe(150.00);
      
      // Verify total
      expect(claim.total!.value).toBe(150.00);
      expect(claim.total!.currency).toBe('USD');
    });
    
    it('maps X12 837 with multiple service lines', () => {
      const input: X12_837 = {
        claimId: 'CLM002',
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
          totalChargeAmount: 450.00,
          serviceLines: [
            {
              lineNumber: 1,
              procedureCode: '99213',
              serviceDateFrom: '20240110',
              chargeAmount: 150.00,
              units: 1
            },
            {
              lineNumber: 2,
              procedureCode: '80053',
              serviceDateFrom: '20240110',
              chargeAmount: 300.00,
              units: 1
            }
          ]
        }
      };
      
      const claim = mapX12837ToFhirClaim(input);
      
      expect(claim.item).toHaveLength(2);
      expect(claim.item![0].productOrService.coding![0].code).toBe('99213');
      expect(claim.item![1].productOrService.coding![0].code).toBe('80053');
      expect(claim.total!.value).toBe(450.00);
    });
    
    it('maps X12 837 with diagnosis codes', () => {
      const input: X12_837 = {
        claimId: 'CLM003',
        billingProvider: {
          npi: '1234567890',
          name: 'Provider Clinic'
        },
        subscriber: {
          memberId: 'MEM003',
          firstName: 'Bob',
          lastName: 'Johnson',
          dob: '19750815'
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
              chargeAmount: 150.00,
              diagnosisPointers: [1, 2]
            }
          ]
        },
        diagnoses: [
          {
            sequence: 1,
            code: 'E11.9'
          },
          {
            sequence: 2,
            code: 'I10'
          }
        ]
      };
      
      const claim = mapX12837ToFhirClaim(input);
      
      expect(claim.diagnosis).toHaveLength(2);
      expect(claim.diagnosis![0].sequence).toBe(1);
      expect(claim.diagnosis![0].diagnosisCodeableConcept!.coding![0].code).toBe('E11.9');
      expect(claim.diagnosis![1].diagnosisCodeableConcept!.coding![0].code).toBe('I10');
      
      expect(claim.item![0].diagnosisSequence).toEqual([1, 2]);
    });
    
    it('maps US Core profile metadata', () => {
      const input: X12_837 = {
        claimId: 'CLM004',
        billingProvider: {
          npi: '1234567890',
          name: 'Provider Clinic'
        },
        subscriber: {
          memberId: 'MEM004',
          firstName: 'Alice',
          lastName: 'Brown',
          dob: '19880505'
        },
        payer: {
          payerId: 'PAYER001'
        },
        claim: {
          totalChargeAmount: 100.00,
          serviceLines: [
            {
              lineNumber: 1,
              procedureCode: '99211',
              serviceDateFrom: '20240110',
              chargeAmount: 100.00
            }
          ]
        }
      };
      
      const claim = mapX12837ToFhirClaim(input);
      
      expect(claim.meta?.profile).toBeDefined();
      expect(claim.meta!.profile!.some(p => p.includes('us-core'))).toBe(true);
      expect(claim.meta!.profile!.some(p => p.includes('davinci-pdex'))).toBe(true);
    });
  });
  
  describe('mapX12278ToFhirServiceRequest', () => {
    it('maps minimal X12 278 to FHIR R4 ServiceRequest', () => {
      const input: X12_278 = {
        authorizationId: 'AUTH001',
        transactionDate: '20240115-1430',
        requester: {
          npi: '1234567890',
          name: 'Dr. Smith'
        },
        subscriber: {
          memberId: 'MEM001',
          firstName: 'John',
          lastName: 'Doe',
          dob: '19850615',
          gender: 'M'
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
      
      // Verify resource structure
      expect(serviceRequest.resourceType).toBe('ServiceRequest');
      expect(serviceRequest.id).toBe('AUTH001');
      expect(serviceRequest.status).toBe('active');
      expect(serviceRequest.intent).toBe('order');
      
      // Verify category
      expect(serviceRequest.category).toBeDefined();
      expect(serviceRequest.category![0].coding![0].code).toBe('auth');
      
      // Verify subject
      expect(serviceRequest.subject.reference).toBe('Patient/MEM001');
      
      // Verify requester
      expect(serviceRequest.requester!.identifier!.value).toBe('1234567890');
      expect(serviceRequest.requester!.identifier!.system).toBe('http://hl7.org/fhir/sid/us-npi');
      
      // Verify code
      expect(serviceRequest.code?.coding![0].code).toBe('27447');
      
      // Verify timing
      expect(serviceRequest.occurrencePeriod!.start).toBe('2024-01-20');
      
      // Verify insurance
      expect(serviceRequest.insurance).toBeDefined();
      expect(serviceRequest.insurance![0].identifier!.value).toBe('PAYER001');
    });
    
    it('maps X12 278 with date range', () => {
      const input: X12_278 = {
        authorizationId: 'AUTH002',
        requester: {
          npi: '1234567890',
          organizationName: 'Hospital System'
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
        serviceRequest: {
          serviceTypeCode: 'HS',
          certificationType: 'HS',
          serviceDateFrom: '20240201',
          serviceDateTo: '20240203',
          procedureCode: '99233',
          units: 3
        }
      };
      
      const serviceRequest = mapX12278ToFhirServiceRequest(input);
      
      expect(serviceRequest.occurrencePeriod!.start).toBe('2024-02-01');
      expect(serviceRequest.occurrencePeriod!.end).toBe('2024-02-03');
      expect(serviceRequest.quantityQuantity!.value).toBe(3);
    });
    
    it('maps X12 278 with diagnosis code', () => {
      const input: X12_278 = {
        authorizationId: 'AUTH003',
        requester: {
          npi: '1234567890'
        },
        subscriber: {
          memberId: 'MEM003',
          firstName: 'Bob',
          lastName: 'Johnson',
          dob: '19750815'
        },
        payer: {
          payerId: 'PAYER001'
        },
        serviceRequest: {
          serviceTypeCode: 'HS',
          certificationType: 'AR',
          serviceDateFrom: '20240120',
          procedureCode: '27447',
          diagnosisCode: 'M17.11'
        }
      };
      
      const serviceRequest = mapX12278ToFhirServiceRequest(input);
      
      expect(serviceRequest.reasonCode).toBeDefined();
      expect(serviceRequest.reasonCode![0].coding![0].code).toBe('M17.11');
      expect(serviceRequest.reasonCode![0].coding![0].system).toBe('http://hl7.org/fhir/sid/icd-10');
    });
    
    it('maps Da Vinci PAS profile metadata', () => {
      const input: X12_278 = {
        authorizationId: 'AUTH004',
        requester: {
          npi: '1234567890'
        },
        subscriber: {
          memberId: 'MEM004',
          firstName: 'Alice',
          lastName: 'Brown',
          dob: '19880505'
        },
        payer: {
          payerId: 'PAYER001'
        },
        serviceRequest: {
          serviceTypeCode: 'HS',
          certificationType: 'AR',
          serviceDateFrom: '20240120'
        }
      };
      
      const serviceRequest = mapX12278ToFhirServiceRequest(input);
      
      expect(serviceRequest.meta?.profile).toBeDefined();
      expect(serviceRequest.meta!.profile!.some(p => p.includes('davinci-pas'))).toBe(true);
      expect(serviceRequest.meta!.profile!.some(p => p.includes('davinci-crd'))).toBe(true);
    });
  });
  
  describe('mapX12835ToFhirExplanationOfBenefit', () => {
    it('maps minimal X12 835 to FHIR R4 ExplanationOfBenefit', () => {
      const input: X12_835 = {
        remittanceId: 'REM001',
        transactionDate: '20240115',
        payer: {
          payerId: 'PAYER001',
          name: 'Test Health Plan'
        },
        payee: {
          npi: '1234567890',
          name: 'Provider Clinic',
          taxId: '12-3456789'
        },
        claims: [
          {
            claimId: 'CLM001',
            patient: {
              memberId: 'MEM001',
              firstName: 'John',
              lastName: 'Doe'
            },
            claimStatusCode: '1',
            totalChargeAmount: 150.00,
            totalPaidAmount: 120.00,
            serviceDateFrom: '20240110',
            serviceLines: [
              {
                lineNumber: 1,
                procedureCode: '99213',
                serviceDate: '20240110',
                chargeAmount: 150.00,
                paidAmount: 120.00,
                allowedAmount: 130.00
              }
            ]
          }
        ]
      };
      
      const eobs = mapX12835ToFhirExplanationOfBenefit(input);
      
      expect(eobs).toHaveLength(1);
      
      const eob = eobs[0];
      
      // Verify resource structure
      expect(eob.resourceType).toBe('ExplanationOfBenefit');
      expect(eob.id).toBe('REM001-CLM001');
      expect(eob.status).toBe('active');
      expect(eob.type.coding![0].code).toBe('professional');
      expect(eob.use).toBe('claim');
      expect(eob.outcome).toBe('complete');
      
      // Verify patient
      expect(eob.patient.reference).toBe('Patient/MEM001');
      
      // Verify insurer
      expect(eob.insurer.identifier!.value).toBe('PAYER001');
      
      // Verify provider
      expect(eob.provider.identifier!.value).toBe('1234567890');
      
      // Verify service lines
      expect(eob.item).toHaveLength(1);
      expect(eob.item![0].sequence).toBe(1);
      expect(eob.item![0].productOrService.coding![0].code).toBe('99213');
      
      // Verify adjudication
      expect(eob.item![0].adjudication).toBeDefined();
      const submittedAdj = eob.item![0].adjudication!.find(a => 
        a.category.coding![0].code === 'submitted'
      );
      expect(submittedAdj?.amount?.value).toBe(150.00);
      
      const benefitAdj = eob.item![0].adjudication!.find(a => 
        a.category.coding![0].code === 'benefit'
      );
      expect(benefitAdj?.amount?.value).toBe(120.00);
      
      const eligibleAdj = eob.item![0].adjudication!.find(a => 
        a.category.coding![0].code === 'eligible'
      );
      expect(eligibleAdj?.amount?.value).toBe(130.00);
      
      // Verify totals
      expect(eob.total).toHaveLength(2);
      const totalSubmitted = eob.total!.find(t => 
        t.category.coding![0].code === 'submitted'
      );
      expect(totalSubmitted?.amount.value).toBe(150.00);
      
      const totalBenefit = eob.total!.find(t => 
        t.category.coding![0].code === 'benefit'
      );
      expect(totalBenefit?.amount.value).toBe(120.00);
      
      // Verify payment
      expect(eob.payment?.amount?.value).toBe(120.00);
      expect(eob.payment?.date).toBe('2024-01-15');
    });
    
    it('maps X12 835 with multiple claims', () => {
      const input: X12_835 = {
        remittanceId: 'REM002',
        transactionDate: '20240115',
        payer: {
          payerId: 'PAYER001',
          name: 'Test Health Plan'
        },
        payee: {
          npi: '1234567890',
          name: 'Provider Clinic'
        },
        claims: [
          {
            claimId: 'CLM001',
            patient: {
              memberId: 'MEM001',
              firstName: 'John',
              lastName: 'Doe'
            },
            claimStatusCode: '1',
            totalChargeAmount: 150.00,
            totalPaidAmount: 120.00,
            serviceDateFrom: '20240110',
            serviceLines: [
              {
                lineNumber: 1,
                procedureCode: '99213',
                serviceDate: '20240110',
                chargeAmount: 150.00,
                paidAmount: 120.00
              }
            ]
          },
          {
            claimId: 'CLM002',
            patient: {
              memberId: 'MEM002',
              firstName: 'Jane',
              lastName: 'Smith'
            },
            claimStatusCode: '1',
            totalChargeAmount: 200.00,
            totalPaidAmount: 180.00,
            serviceDateFrom: '20240111',
            serviceLines: [
              {
                lineNumber: 1,
                procedureCode: '99214',
                serviceDate: '20240111',
                chargeAmount: 200.00,
                paidAmount: 180.00
              }
            ]
          }
        ]
      };
      
      const eobs = mapX12835ToFhirExplanationOfBenefit(input);
      
      expect(eobs).toHaveLength(2);
      expect(eobs[0].claim!.identifier!.value).toBe('CLM001');
      expect(eobs[1].claim!.identifier!.value).toBe('CLM002');
    });
    
    it('maps X12 835 with adjustments', () => {
      const input: X12_835 = {
        remittanceId: 'REM003',
        transactionDate: '20240115',
        payer: {
          payerId: 'PAYER001',
          name: 'Test Health Plan'
        },
        payee: {
          npi: '1234567890',
          name: 'Provider Clinic'
        },
        claims: [
          {
            claimId: 'CLM003',
            patient: {
              memberId: 'MEM003',
              firstName: 'Bob',
              lastName: 'Johnson'
            },
            claimStatusCode: '1',
            totalChargeAmount: 200.00,
            totalPaidAmount: 150.00,
            serviceDateFrom: '20240110',
            serviceLines: [
              {
                lineNumber: 1,
                procedureCode: '99214',
                serviceDate: '20240110',
                chargeAmount: 200.00,
                paidAmount: 150.00,
                allowedAmount: 175.00,
                adjustments: [
                  {
                    groupCode: 'CO',
                    reasonCode: '45',
                    amount: 25.00
                  },
                  {
                    groupCode: 'PR',
                    reasonCode: '1',
                    amount: 25.00
                  }
                ]
              }
            ]
          }
        ]
      };
      
      const eobs = mapX12835ToFhirExplanationOfBenefit(input);
      const eob = eobs[0];
      
      // Check adjustments are included in adjudication
      const contractualAdj = eob.item![0].adjudication!.find(a => 
        a.category.coding![0].code === 'contractual'
      );
      expect(contractualAdj).toBeDefined();
      expect(contractualAdj?.amount?.value).toBe(25.00);
      
      const copayAdj = eob.item![0].adjudication!.find(a => 
        a.category.coding![0].code === 'copay'
      );
      expect(copayAdj).toBeDefined();
      expect(copayAdj?.amount?.value).toBe(25.00);
    });
    
    it('maps US Core EOB profile metadata', () => {
      const input: X12_835 = {
        remittanceId: 'REM004',
        transactionDate: '20240115',
        payer: {
          payerId: 'PAYER001',
          name: 'Test Health Plan'
        },
        payee: {
          npi: '1234567890',
          name: 'Provider Clinic'
        },
        claims: [
          {
            claimId: 'CLM004',
            patient: {
              memberId: 'MEM004',
              firstName: 'Alice',
              lastName: 'Brown'
            },
            claimStatusCode: '1',
            totalChargeAmount: 100.00,
            totalPaidAmount: 90.00,
            serviceDateFrom: '20240110',
            serviceLines: [
              {
                lineNumber: 1,
                procedureCode: '99211',
                serviceDate: '20240110',
                chargeAmount: 100.00,
                paidAmount: 90.00
              }
            ]
          }
        ]
      };
      
      const eobs = mapX12835ToFhirExplanationOfBenefit(input);
      const eob = eobs[0];
      
      expect(eob.meta?.profile).toBeDefined();
      expect(eob.meta!.profile!.some(p => p.includes('us-core'))).toBe(true);
      expect(eob.meta!.profile!.some(p => p.includes('davinci-pdex'))).toBe(true);
    });
  });
});
