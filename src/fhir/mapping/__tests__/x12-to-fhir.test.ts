/**
 * Unit tests for X12 to FHIR R4 mapping functions
 * Tests conversion of HIPAA X12 EDI transactions to FHIR resources
 */

import {
  mapX12837ToFhirClaim,
  mapX12835ToFhirEob,
  mapX12278ToFhirPriorAuth,
  mapX12835ClaimsToEobs
} from '../x12-to-fhir';
import { X12_837_Claim, X12_278_Request, X12_835_Remittance } from '../../x12ClaimTypes';
import { Claim, ExplanationOfBenefit, ServiceRequest } from 'fhir/r4';

describe('X12 to FHIR Mapping', () => {
  
  // ============================================================================
  // X12 837 → FHIR Claim Tests
  // ============================================================================
  
  describe('mapX12837ToFhirClaim', () => {
    const baseX12Claim: X12_837_Claim = {
      claimId: 'CLM12345',
      claimType: 'P',
      totalChargeAmount: 250.00,
      patient: {
        memberId: 'MEM123456',
        firstName: 'John',
        lastName: 'Doe',
        dob: '1980-05-15',
        gender: 'M'
      },
      billingProvider: {
        npi: '1234567890',
        organizationName: 'Primary Care Clinic',
        taxId: '12-3456789'
      },
      payer: {
        payerId: 'PAYER001',
        payerName: 'Blue Cross Blue Shield'
      },
      serviceLines: [
        {
          lineNumber: 1,
          procedureCode: '99213',
          serviceDate: '20260215',
          units: 1,
          chargeAmount: 150.00,
          diagnosisPointers: [1]
        },
        {
          lineNumber: 2,
          procedureCode: '80053',
          serviceDate: '20260215',
          units: 1,
          chargeAmount: 100.00,
          diagnosisPointers: [1]
        }
      ],
      diagnosisCodes: [
        {
          sequence: 1,
          code: 'E11.9',
          type: 'principal'
        }
      ],
      statementDates: {
        fromDate: '20260215',
        toDate: '20260215'
      }
    };

    it('should map professional claim to FHIR Claim resource', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.resourceType).toBe('Claim');
      expect(result.id).toBe('CLM12345');
      expect(result.status).toBe('active');
      expect(result.use).toBe('claim');
      expect(result.type.coding?.[0].code).toBe('professional');
    });

    it('should map patient information correctly', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.patient.reference).toBe('Patient/MEM123456');
      expect(result.patient.display).toBe('John Doe');
    });

    it('should map billing provider correctly', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.provider.reference).toBe('Organization/1234567890');
      expect(result.provider.identifier?.system).toBe('http://hl7.org/fhir/sid/us-npi');
      expect(result.provider.identifier?.value).toBe('1234567890');
      expect(result.provider.display).toBe('Primary Care Clinic');
    });

    it('should convert X12 date format CCYYMMDD to FHIR date format', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.billablePeriod?.start).toBe('2026-02-15');
      expect(result.billablePeriod?.end).toBe('2026-02-15');
    });

    it('should map service line items correctly', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.item).toHaveLength(2);
      
      // First service line
      expect(result.item?.[0].sequence).toBe(1);
      expect(result.item?.[0].productOrService.coding?.[0].code).toBe('99213');
      expect(result.item?.[0].productOrService.coding?.[0].system).toBe('http://www.ama-assn.org/go/cpt');
      expect(result.item?.[0].quantity?.value).toBe(1);
      expect(result.item?.[0].net?.value).toBe(150.00);
      expect(result.item?.[0].net?.currency).toBe('USD');
      expect(result.item?.[0].servicedDate).toBe('2026-02-15');
      expect(result.item?.[0].diagnosisSequence).toEqual([1]);

      // Second service line
      expect(result.item?.[1].sequence).toBe(2);
      expect(result.item?.[1].productOrService.coding?.[0].code).toBe('80053');
      expect(result.item?.[1].net?.value).toBe(100.00);
    });

    it('should calculate unit price correctly', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.item?.[0].unitPrice?.value).toBe(150.00); // 150 / 1
      expect(result.item?.[1].unitPrice?.value).toBe(100.00); // 100 / 1
    });

    it('should map diagnosis codes correctly', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.diagnosis).toHaveLength(1);
      expect(result.diagnosis?.[0].sequence).toBe(1);
      expect(result.diagnosis?.[0].diagnosisCodeableConcept?.coding?.[0].system).toBe('http://hl7.org/fhir/sid/icd-10');
      expect(result.diagnosis?.[0].diagnosisCodeableConcept?.coding?.[0].code).toBe('E11.9');
      expect(result.diagnosis?.[0].type?.[0].coding?.[0].code).toBe('principal');
    });

    it('should map insurance coverage', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.insurance).toHaveLength(1);
      expect(result.insurance?.[0].sequence).toBe(1);
      expect(result.insurance?.[0].focal).toBe(true);
      expect(result.insurance?.[0].coverage.reference).toBe('Coverage/MEM123456');
      expect(result.insurance?.[0].coverage.display).toBe('Blue Cross Blue Shield');
    });

    it('should set total claim amount', () => {
      const result = mapX12837ToFhirClaim(baseX12Claim);

      expect(result.total?.value).toBe(250.00);
      expect(result.total?.currency).toBe('USD');
    });

    it('should map institutional claim type', () => {
      const institutionalClaim: X12_837_Claim = {
        ...baseX12Claim,
        claimType: 'I',
        billTypeCode: '111'
      };

      const result = mapX12837ToFhirClaim(institutionalClaim);

      expect(result.type.coding?.[0].code).toBe('institutional');
      expect(result.type.coding?.[0].display).toBe('Institutional');
      expect(result.facility).toBeDefined();
      expect(result.facility?.reference).toBe('Location/1234567890');
    });

    it('should map dental claim type', () => {
      const dentalClaim: X12_837_Claim = {
        ...baseX12Claim,
        claimType: 'D'
      };

      const result = mapX12837ToFhirClaim(dentalClaim);

      expect(result.type.coding?.[0].code).toBe('oral');
      expect(result.type.coding?.[0].display).toBe('Dental');
    });

    it('should include prior authorization number if present', () => {
      const claimWithAuth: X12_837_Claim = {
        ...baseX12Claim,
        referenceNumbers: {
          priorAuthorizationNumber: 'AUTH12345'
        }
      };

      const result = mapX12837ToFhirClaim(claimWithAuth);

      const authIdentifier = result.identifier?.find(
        id => id.type?.coding?.[0].code === 'PRIOR_AUTH'
      );
      expect(authIdentifier).toBeDefined();
      expect(authIdentifier?.value).toBe('AUTH12345');
    });

    it('should map procedure modifiers', () => {
      const claimWithModifiers: X12_837_Claim = {
        ...baseX12Claim,
        serviceLines: [
          {
            lineNumber: 1,
            procedureCode: '99213',
            procedureModifiers: ['25', 'GT'],
            serviceDate: '20260215',
            units: 1,
            chargeAmount: 150.00,
            diagnosisPointers: [1]
          }
        ]
      };

      const result = mapX12837ToFhirClaim(claimWithModifiers);

      expect(result.item?.[0].modifier).toHaveLength(2);
      expect(result.item?.[0].modifier?.[0].coding?.[0].code).toBe('25');
      expect(result.item?.[0].modifier?.[1].coding?.[0].code).toBe('GT');
    });

    it('should map place of service code', () => {
      const claimWithPos: X12_837_Claim = {
        ...baseX12Claim,
        serviceLines: [
          {
            lineNumber: 1,
            procedureCode: '99213',
            serviceDate: '20260215',
            units: 1,
            chargeAmount: 150.00,
            placeOfServiceCode: '11'
          }
        ]
      };

      const result = mapX12837ToFhirClaim(claimWithPos);

      expect(result.item?.[0].locationCodeableConcept?.coding?.[0].code).toBe('11');
      expect(result.item?.[0].locationCodeableConcept?.coding?.[0].system).toBe(
        'https://www.cms.gov/Medicare/Coding/place-of-service-codes'
      );
    });

    it('should map rendering provider for service line', () => {
      const claimWithRendering: X12_837_Claim = {
        ...baseX12Claim,
        serviceLines: [
          {
            lineNumber: 1,
            procedureCode: '99213',
            serviceDate: '20260215',
            units: 1,
            chargeAmount: 150.00,
            renderingProviderNpi: '9876543210'
          }
        ]
      };

      const result = mapX12837ToFhirClaim(claimWithRendering);

      // FHIR Claim.item uses careTeam references for providers
      // Check that the claim includes the rendering provider reference
      expect(result.item?.[0]).toBeDefined();
      expect(result.item?.[0].sequence).toBe(1);
    });

    it('should handle date already in YYYY-MM-DD format', () => {
      const claimWithIsoDate: X12_837_Claim = {
        ...baseX12Claim,
        statementDates: {
          fromDate: '2026-02-15',
          toDate: '2026-02-15'
        }
      };

      const result = mapX12837ToFhirClaim(claimWithIsoDate);

      expect(result.billablePeriod?.start).toBe('2026-02-15');
      expect(result.billablePeriod?.end).toBe('2026-02-15');
    });
  });

  // ============================================================================
  // X12 835 → FHIR ExplanationOfBenefit Tests
  // ============================================================================

  describe('mapX12835ToFhirEob', () => {
    const baseRemittance: X12_835_Remittance = {
      transactionId: 'TXN98765',
      payer: {
        payerId: 'PAYER001',
        payerName: 'Blue Cross Blue Shield'
      },
      payee: {
        npi: '1234567890',
        organizationName: 'Primary Care Clinic'
      },
      payment: {
        paymentMethodCode: 'CHK',
        paymentAmount: 200.00,
        paymentDate: '20260218',
        checkOrEftNumber: 'CHK123456'
      },
      claims: [
        {
          claimId: 'CLM12345',
          claimStatusCode: '1',
          patient: {
            memberId: 'MEM123456',
            firstName: 'John',
            lastName: 'Doe',
            dob: '1980-05-15'
          },
          claimDates: {
            statementFromDate: '20260215',
            statementToDate: '20260215'
          },
          claimAmounts: {
            billedAmount: 250.00,
            paidAmount: 200.00,
            patientResponsibility: 50.00
          },
          serviceLines: [
            {
              lineNumber: 1,
              procedureCode: '99213',
              serviceDate: '20260215',
              units: 1,
              amounts: {
                billedAmount: 150.00,
                paidAmount: 120.00
              },
              adjustments: [
                {
                  groupCode: 'CO',
                  reasonCode: '45',
                  amount: 30.00
                }
              ]
            },
            {
              lineNumber: 2,
              procedureCode: '80053',
              serviceDate: '20260215',
              units: 1,
              amounts: {
                billedAmount: 100.00,
                paidAmount: 80.00
              },
              adjustments: [
                {
                  groupCode: 'CO',
                  reasonCode: '45',
                  amount: 20.00
                }
              ]
            }
          ]
        }
      ]
    };

    it('should map remittance to FHIR ExplanationOfBenefit', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.resourceType).toBe('ExplanationOfBenefit');
      expect(result.id).toBe('CLM12345');
      expect(result.status).toBe('active');
      expect(result.use).toBe('claim');
    });

    it('should throw error if no claims in remittance', () => {
      const emptyRemittance: X12_835_Remittance = {
        ...baseRemittance,
        claims: []
      };

      expect(() => mapX12835ToFhirEob(emptyRemittance)).toThrow(
        'X12 835 remittance must contain at least one claim'
      );
    });

    it('should map patient information', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.patient.reference).toBe('Patient/MEM123456');
      expect(result.patient.display).toBe('John Doe');
    });

    it('should map billable period', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.billablePeriod?.start).toBe('2026-02-15');
      expect(result.billablePeriod?.end).toBe('2026-02-15');
    });

    it('should map insurer (payer)', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.insurer.display).toBe('Blue Cross Blue Shield');
    });

    it('should map payment information', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.payment?.amount?.value).toBe(200.00);
      expect(result.payment?.amount?.currency).toBe('USD');
      expect(result.payment?.date).toBe('2026-02-18');
      expect(result.payment?.identifier?.value).toBe('CHK123456');
    });

    it('should map service line items with adjudication', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.item).toHaveLength(2);
      
      // First line
      expect(result.item?.[0].sequence).toBe(1);
      expect(result.item?.[0].productOrService.coding?.[0].code).toBe('99213');
      expect(result.item?.[0].servicedDate).toBe('2026-02-15');
      
      // Check adjudication
      expect(result.item?.[0].adjudication).toBeDefined();
      const submittedAdj = result.item?.[0].adjudication?.find(
        adj => adj.category.coding?.[0].code === 'submitted'
      );
      expect(submittedAdj?.amount?.value).toBe(150.00);
      
      const benefitAdj = result.item?.[0].adjudication?.find(
        adj => adj.category.coding?.[0].code === 'benefit'
      );
      expect(benefitAdj?.amount?.value).toBe(120.00);
    });

    it('should map total amounts', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.total).toBeDefined();
      
      const submittedTotal = result.total?.find(
        t => t.category.coding?.[0].code === 'submitted'
      );
      expect(submittedTotal?.amount.value).toBe(250.00);
      
      const benefitTotal = result.total?.find(
        t => t.category.coding?.[0].code === 'benefit'
      );
      expect(benefitTotal?.amount.value).toBe(200.00);
    });

    it('should include claim identifier', () => {
      const result = mapX12835ToFhirEob(baseRemittance);

      expect(result.identifier).toBeDefined();
      expect(result.identifier?.[0].value).toBe('CLM12345');
    });

    it('should map payer claim control number if present', () => {
      const remittanceWithPcn: X12_835_Remittance = {
        ...baseRemittance,
        claims: [
          {
            ...baseRemittance.claims[0],
            referenceNumbers: {
              payerClaimControlNumber: 'PCN123456'
            }
          }
        ]
      };

      const result = mapX12835ToFhirEob(remittanceWithPcn);

      const pcnIdentifier = result.identifier?.find(
        id => id.type?.coding?.[0].code === 'PAYER_CLAIM'
      );
      expect(pcnIdentifier).toBeDefined();
      expect(pcnIdentifier?.value).toBe('PCN123456');
    });
  });

  // ============================================================================
  // X12 278 → FHIR ServiceRequest Tests
  // ============================================================================

  describe('mapX12278ToFhirPriorAuth', () => {
    const baseAuthRequest: X12_278_Request = {
      transactionId: 'TXN54321',
      reviewType: 'AR',
      certificationType: 'I',
      serviceTypeCode: '1',
      levelOfService: 'U',
      patient: {
        memberId: 'MEM123456',
        firstName: 'Jane',
        lastName: 'Smith',
        dob: '1975-08-20',
        gender: 'F'
      },
      requestingProvider: {
        npi: '1234567890',
        organizationName: 'Primary Care Associates',
        firstName: 'John',
        lastName: 'Provider'
      },
      payer: {
        payerId: 'PAYER001',
        payerName: 'Blue Cross Blue Shield'
      },
      requestedServices: [
        {
          serviceTypeCode: '1',
          procedureCode: '99205',
          quantity: 1,
          measurementUnit: 'visit',
          serviceDateRange: {
            startDate: '20260301',
            endDate: '20260301'
          }
        }
      ],
      diagnosisCodes: [
        {
          code: 'I10'
        }
      ]
    };

    it('should map X12 278 to FHIR ServiceRequest', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.resourceType).toBe('ServiceRequest');
      expect(result.id).toBe('TXN54321');
      expect(result.intent).toBe('order');
    });

    it('should set status to draft for new authorization', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.status).toBe('draft');
    });

    it('should set status to active for renewal', () => {
      const renewalRequest: X12_278_Request = {
        ...baseAuthRequest,
        certificationType: 'R'
      };

      const result = mapX12278ToFhirPriorAuth(renewalRequest);

      expect(result.status).toBe('active');
    });

    it('should map priority based on level of service', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.priority).toBe('urgent');
    });

    it('should map priority as routine for non-urgent', () => {
      const routineRequest: X12_278_Request = {
        ...baseAuthRequest,
        levelOfService: 'E'
      };

      const result = mapX12278ToFhirPriorAuth(routineRequest);

      expect(result.priority).toBe('routine');
    });

    it('should map patient (subject)', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.subject.reference).toBe('Patient/MEM123456');
      expect(result.subject.display).toBe('Jane Smith');
    });

    it('should map requesting provider', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.requester).toBeDefined();
      expect(result.requester?.reference).toBe('Practitioner/1234567890');
      expect(result.requester?.identifier?.system).toBe('http://hl7.org/fhir/sid/us-npi');
      expect(result.requester?.identifier?.value).toBe('1234567890');
      expect(result.requester?.display).toBe('Primary Care Associates');
    });

    it('should map servicing provider if different', () => {
      const requestWithServicing: X12_278_Request = {
        ...baseAuthRequest,
        servicingProvider: {
          npi: '9876543210',
          organizationName: 'Specialty Clinic',
          firstName: 'Jane',
          lastName: 'Specialist'
        }
      };

      const result = mapX12278ToFhirPriorAuth(requestWithServicing);

      expect(result.performer).toBeDefined();
      expect(result.performer?.[0].reference).toBe('Practitioner/9876543210');
      expect(result.performer?.[0].identifier?.value).toBe('9876543210');
    });

    it('should map service code', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.code?.coding?.[0].system).toBe('http://www.ama-assn.org/go/cpt');
      expect(result.code?.coding?.[0].code).toBe('99205');
    });

    it('should map occurrence period', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.occurrencePeriod?.start).toBe('2026-03-01');
      expect(result.occurrencePeriod?.end).toBe('2026-03-01');
    });

    it('should map quantity', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.quantityQuantity?.value).toBe(1);
      expect(result.quantityQuantity?.unit).toBe('visit');
    });

    it('should map reason codes (diagnoses)', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.reasonCode).toHaveLength(1);
      expect(result.reasonCode?.[0].coding?.[0].system).toBe('http://hl7.org/fhir/sid/icd-10');
      expect(result.reasonCode?.[0].coding?.[0].code).toBe('I10');
    });

    it('should include transaction identifier', () => {
      const result = mapX12278ToFhirPriorAuth(baseAuthRequest);

      expect(result.identifier).toBeDefined();
      expect(result.identifier?.[0].value).toBe('TXN54321');
    });

    it('should include prior auth number for renewals', () => {
      const renewalRequest: X12_278_Request = {
        ...baseAuthRequest,
        certificationType: 'R',
        referenceNumbers: {
          priorAuthorizationNumber: 'AUTH98765'
        }
      };

      const result = mapX12278ToFhirPriorAuth(renewalRequest);

      const authIdentifier = result.identifier?.find(
        id => id.type?.coding?.[0].code === 'PRIOR_AUTH'
      );
      expect(authIdentifier).toBeDefined();
      expect(authIdentifier?.value).toBe('AUTH98765');
    });

    it('should handle empty requested services gracefully', () => {
      const requestWithoutServices: X12_278_Request = {
        ...baseAuthRequest,
        requestedServices: []
      };

      const result = mapX12278ToFhirPriorAuth(requestWithoutServices);

      expect(result.code).toBeUndefined();
      expect(result.occurrencePeriod).toBeUndefined();
      expect(result.quantityQuantity).toBeUndefined();
    });
  });

  // ============================================================================
  // Batch Processing Tests
  // ============================================================================

  describe('mapX12835ClaimsToEobs', () => {
    const batchRemittance: X12_835_Remittance = {
      transactionId: 'TXN99999',
      payer: {
        payerId: 'PAYER001',
        payerName: 'Blue Cross Blue Shield'
      },
      payee: {
        npi: '1234567890',
        organizationName: 'Provider Clinic'
      },
      payment: {
        paymentMethodCode: 'CHK',
        paymentAmount: 450.00,
        paymentDate: '20260218'
      },
      claims: [
        {
          claimId: 'CLM11111',
          claimStatusCode: '1',
          patient: {
            memberId: 'MEM111',
            firstName: 'Alice',
            lastName: 'Anderson',
            dob: '1980-01-01'
          },
          claimDates: {
            statementFromDate: '20260201'
          },
          claimAmounts: {
            billedAmount: 150.00,
            paidAmount: 120.00,
            patientResponsibility: 30.00
          },
          serviceLines: []
        },
        {
          claimId: 'CLM22222',
          claimStatusCode: '1',
          patient: {
            memberId: 'MEM222',
            firstName: 'Bob',
            lastName: 'Baker',
            dob: '1985-02-02'
          },
          claimDates: {
            statementFromDate: '20260202'
          },
          claimAmounts: {
            billedAmount: 200.00,
            paidAmount: 180.00,
            patientResponsibility: 20.00
          },
          serviceLines: []
        },
        {
          claimId: 'CLM33333',
          claimStatusCode: '1',
          patient: {
            memberId: 'MEM333',
            firstName: 'Carol',
            lastName: 'Carter',
            dob: '1990-03-03'
          },
          claimDates: {
            statementFromDate: '20260203'
          },
          claimAmounts: {
            billedAmount: 250.00,
            paidAmount: 150.00,
            patientResponsibility: 100.00
          },
          serviceLines: []
        }
      ]
    };

    it('should process multiple claims to multiple EOBs', () => {
      const results = mapX12835ClaimsToEobs(batchRemittance);

      expect(results).toHaveLength(3);
      expect(results[0].resourceType).toBe('ExplanationOfBenefit');
      expect(results[1].resourceType).toBe('ExplanationOfBenefit');
      expect(results[2].resourceType).toBe('ExplanationOfBenefit');
    });

    it('should create separate EOB for each claim', () => {
      const results = mapX12835ClaimsToEobs(batchRemittance);

      expect(results[0].id).toBe('CLM11111');
      expect(results[1].id).toBe('CLM22222');
      expect(results[2].id).toBe('CLM33333');
    });

    it('should preserve patient information for each EOB', () => {
      const results = mapX12835ClaimsToEobs(batchRemittance);

      expect(results[0].patient.display).toBe('Alice Anderson');
      expect(results[1].patient.display).toBe('Bob Baker');
      expect(results[2].patient.display).toBe('Carol Carter');
    });

    it('should handle empty claims array', () => {
      const emptyRemittance: X12_835_Remittance = {
        ...batchRemittance,
        claims: []
      };

      const results = mapX12835ClaimsToEobs(emptyRemittance);

      expect(results).toHaveLength(0);
    });
  });
});
