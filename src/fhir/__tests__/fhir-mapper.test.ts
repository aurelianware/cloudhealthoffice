import { 
  mapX12837ToFhirClaim, 
  mapX12278ToFhirPriorAuth, 
  mapX12835ToFhirEOB 
} from '../fhir-mapper';
import { X12_837_Claim, X12_278_Request, X12_835_Remittance } from '../x12ClaimTypes';

describe('FHIR Mapper - CMS-0057-F Compliance', () => {
  
  describe('mapX12837ToFhirClaim', () => {
    it('maps X12 837 Professional claim to FHIR R4 Claim', () => {
      const input: X12_837_Claim = {
        claimId: 'CLM001',
        claimType: 'P',
        totalChargeAmount: 500.00,
        placeOfServiceCode: '11',
        patient: {
          memberId: 'MEM12345',
          firstName: 'John',
          lastName: 'Doe',
          dob: '19800101',
          gender: 'M'
        },
        billingProvider: {
          npi: '1234567890',
          organizationName: 'Main Street Clinic',
          taxId: '12-3456789'
        },
        payer: {
          payerId: 'PAYER001',
          payerName: 'Acme Health Plan'
        },
        serviceLines: [{
          lineNumber: 1,
          procedureCode: '99213',
          serviceDate: '20240115',
          units: 1,
          chargeAmount: 150.00,
          diagnosisPointers: [1]
        }, {
          lineNumber: 2,
          procedureCode: '36415',
          serviceDate: '20240115',
          units: 1,
          chargeAmount: 35.00,
          diagnosisPointers: [1]
        }],
        diagnosisCodes: [{
          sequence: 1,
          code: 'Z00.00',
          type: 'principal'
        }],
        statementDates: {
          fromDate: '20240115',
          toDate: '20240115'
        }
      };

      const claim = mapX12837ToFhirClaim(input);

      expect(claim.resourceType).toBe('Claim');
      expect(claim.id).toBe('CLM001');
      expect(claim.status).toBe('active');
      expect(claim.use).toBe('claim');
      
      // Patient reference
      expect(claim.patient.reference).toBe('Patient/MEM12345');
      expect(claim.patient.display).toBe('John Doe');
      
      // Provider
      expect(claim.provider.identifier?.system).toBe('http://hl7.org/fhir/sid/us-npi');
      expect(claim.provider.identifier?.value).toBe('1234567890');
      
      // Total
      expect(claim.total?.value).toBe(500.00);
      expect(claim.total?.currency).toBe('USD');
      
      // Diagnosis
      expect(claim.diagnosis).toHaveLength(1);
      expect(claim.diagnosis![0].diagnosisCodeableConcept?.coding![0].code).toBe('Z00.00');
      expect(claim.diagnosis![0].diagnosisCodeableConcept?.coding![0].system).toBe('http://hl7.org/fhir/sid/icd-10');
      
      // Service lines
      expect(claim.item).toHaveLength(2);
      expect(claim.item![0].productOrService.coding![0].code).toBe('99213');
      expect(claim.item![0].net?.value).toBe(150.00);
      expect(claim.item![1].productOrService.coding![0].code).toBe('36415');
    });

    it('handles institutional claims with facility information', () => {
      const input: X12_837_Claim = {
        claimId: 'CLM002',
        claimType: 'I',
        totalChargeAmount: 15000.00,
        billTypeCode: '111',
        patient: {
          memberId: 'MEM67890',
          firstName: 'Jane',
          lastName: 'Smith',
          dob: '19750315',
          gender: 'F'
        },
        billingProvider: {
          npi: '9876543210',
          organizationName: 'City Hospital',
          taxId: '98-7654321'
        },
        payer: {
          payerId: 'PAYER002',
          payerName: 'State Medicaid'
        },
        serviceLines: [{
          lineNumber: 1,
          procedureCode: '99285',
          serviceDate: '20240120',
          units: 1,
          chargeAmount: 15000.00,
          diagnosisPointers: [1]
        }],
        diagnosisCodes: [{
          sequence: 1,
          code: 'S06.0X0A',
          type: 'principal'
        }],
        statementDates: {
          fromDate: '20240120',
          toDate: '20240122'
        },
        admissionInfo: {
          admissionDate: '20240120',
          admissionTypeCode: '1',
          patientStatusCode: '01'
        }
      };

      const claim = mapX12837ToFhirClaim(input);

      expect(claim.resourceType).toBe('Claim');
      expect(claim.type.coding![0].code).toBe('institutional');
      expect(claim.facility).toBeDefined();
      expect(claim.facility?.identifier?.value).toBe('9876543210');
    });

    it('includes prior authorization numbers when present', () => {
      const input: X12_837_Claim = {
        claimId: 'CLM003',
        claimType: 'P',
        totalChargeAmount: 250.00,
        patient: {
          memberId: 'MEM11111',
          firstName: 'Bob',
          lastName: 'Johnson',
          dob: '19900505',
          gender: 'M'
        },
        billingProvider: {
          npi: '1111111111',
          organizationName: 'Specialist Clinic'
        },
        payer: {
          payerId: 'PAYER003'
        },
        serviceLines: [{
          lineNumber: 1,
          procedureCode: '93000',
          serviceDate: '20240201',
          units: 1,
          chargeAmount: 250.00,
          diagnosisPointers: [1]
        }],
        diagnosisCodes: [{
          sequence: 1,
          code: 'I10'
        }],
        referenceNumbers: {
          priorAuthorizationNumber: 'AUTH12345'
        }
      };

      const claim = mapX12837ToFhirClaim(input);

      const priorAuthIdentifier = claim.identifier?.find(
        id => id.type?.coding?.[0]?.code === 'PRIOR_AUTH'
      );
      expect(priorAuthIdentifier).toBeDefined();
      expect(priorAuthIdentifier?.value).toBe('AUTH12345');
    });
  });

  describe('mapX12278ToFhirPriorAuth', () => {
    it('maps X12 278 authorization request to FHIR ServiceRequest', () => {
      const input: X12_278_Request = {
        transactionId: 'AUTH001',
        reviewType: 'AR',
        certificationType: 'I',
        serviceTypeCode: '1',
        levelOfService: 'U',
        patient: {
          memberId: 'MEM54321',
          firstName: 'Alice',
          lastName: 'Williams',
          dob: '19851210',
          gender: 'F'
        },
        requestingProvider: {
          npi: '5555555555',
          firstName: 'Dr. Sarah',
          lastName: 'Johnson',
          organizationName: 'Family Practice'
        },
        payer: {
          payerId: 'PAYER004',
          payerName: 'Blue Cross'
        },
        requestedServices: [{
          serviceTypeCode: '1',
          procedureCode: '99204',
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

      const serviceRequest = mapX12278ToFhirPriorAuth(input);

      expect(serviceRequest.resourceType).toBe('ServiceRequest');
      expect(serviceRequest.id).toBe('AUTH001');
      expect(serviceRequest.status).toBe('draft');
      expect(serviceRequest.intent).toBe('order');
      expect(serviceRequest.priority).toBe('urgent');
      
      // Patient
      expect(serviceRequest.subject.reference).toBe('Patient/MEM54321');
      
      // Requester
      expect(serviceRequest.requester).toBeDefined();
      expect(serviceRequest.requester!.identifier?.value).toBe('5555555555');
      
      // Timing
      expect(serviceRequest.occurrencePeriod?.start).toBe('2024-03-01');
      
      // Reason (diagnosis)
      expect(serviceRequest.reasonCode).toBeDefined();
      expect(serviceRequest.reasonCode![0].coding![0].code).toBe('E11.9');
      
      // Insurance
      expect(serviceRequest.insurance).toBeDefined();
      expect(serviceRequest.insurance![0].reference).toBe('Coverage/MEM54321');
    });

    it('handles renewal authorization requests', () => {
      const input: X12_278_Request = {
        transactionId: 'AUTH002',
        reviewType: 'AR',
        certificationType: 'R',
        serviceTypeCode: '30',
        patient: {
          memberId: 'MEM98765',
          firstName: 'Charlie',
          lastName: 'Brown',
          dob: '19700101',
          gender: 'M'
        },
        requestingProvider: {
          npi: '7777777777',
          organizationName: 'Therapy Center'
        },
        payer: {
          payerId: 'PAYER005'
        },
        requestedServices: [{
          serviceTypeCode: '30',
          procedureCode: '97110',
          quantity: 12,
          measurementUnit: 'sessions',
          serviceDateRange: {
            startDate: '20240401',
            endDate: '20240430'
          }
        }],
        referenceNumbers: {
          priorAuthorizationNumber: 'OLDAUTH999'
        }
      };

      const serviceRequest = mapX12278ToFhirPriorAuth(input);

      expect(serviceRequest.status).toBe('active'); // Renewal = active
      expect(serviceRequest.quantityQuantity?.value).toBe(12);
      expect(serviceRequest.quantityQuantity?.unit).toBe('sessions');
      
      const priorAuthId = serviceRequest.identifier?.find(
        id => id.type?.coding?.[0]?.code === 'PRIOR_AUTH'
      );
      expect(priorAuthId?.value).toBe('OLDAUTH999');
    });

    it('handles standard priority requests', () => {
      const input: X12_278_Request = {
        transactionId: 'AUTH003',
        reviewType: 'AR',
        serviceTypeCode: '47',
        levelOfService: 'E',
        patient: {
          memberId: 'MEM33333',
          firstName: 'David',
          lastName: 'Lee',
          dob: '19820320',
          gender: 'M'
        },
        requestingProvider: {
          npi: '8888888888',
          organizationName: 'Surgical Associates'
        },
        payer: {
          payerId: 'PAYER006'
        },
        requestedServices: [{
          serviceTypeCode: '47',
          procedureCode: '43239',
          quantity: 1,
          serviceDateRange: {
            startDate: '20240515'
          }
        }]
      };

      const serviceRequest = mapX12278ToFhirPriorAuth(input);

      expect(serviceRequest.priority).toBe('routine'); // Elective = routine
    });
  });

  describe('mapX12835ToFhirEOB', () => {
    it('maps X12 835 remittance to FHIR ExplanationOfBenefit', () => {
      const input: X12_835_Remittance = {
        transactionId: 'REM001',
        payer: {
          payerId: 'PAYER007',
          payerName: 'United Health'
        },
        payee: {
          npi: '2222222222',
          organizationName: 'Main Street Medical'
        },
        payment: {
          paymentMethodCode: 'ACH',
          paymentAmount: 400.00,
          paymentDate: '20240215',
          checkOrEftNumber: 'EFT123456'
        },
        claims: [{
          claimId: 'CLM100',
          patient: {
            memberId: 'MEM77777',
            firstName: 'Emily',
            lastName: 'Davis',
            dob: '19950808'
          },
          claimAmounts: {
            billedAmount: 500.00,
            allowedAmount: 450.00,
            paidAmount: 400.00,
            deductible: 50.00,
            copay: 0,
            coinsurance: 0,
            patientResponsibility: 50.00
          },
          claimStatusCode: '1',
          claimDates: {
            statementFromDate: '20240110',
            statementToDate: '20240110',
            receivedDate: '20240112',
            processedDate: '20240214'
          },
          serviceLines: [{
            lineNumber: 1,
            procedureCode: '99213',
            serviceDate: '20240110',
            units: 1,
            amounts: {
              billedAmount: 150.00,
              allowedAmount: 135.00,
              paidAmount: 120.00,
              deductible: 15.00,
              copay: 0,
              coinsurance: 0
            },
            adjustments: [{
              groupCode: 'CO',
              reasonCode: '45',
              amount: 15.00
            }],
            remarkCodes: ['N123']
          }, {
            lineNumber: 2,
            procedureCode: '36415',
            serviceDate: '20240110',
            units: 1,
            amounts: {
              billedAmount: 35.00,
              allowedAmount: 30.00,
              paidAmount: 30.00,
              deductible: 0,
              copay: 0,
              coinsurance: 0
            }
          }]
        }]
      };

      const eobs = mapX12835ToFhirEOB(input);

      expect(eobs).toHaveLength(1);
      
      const eob = eobs[0];
      expect(eob.resourceType).toBe('ExplanationOfBenefit');
      expect(eob.id).toBe('CLM100');
      expect(eob.status).toBe('active');
      expect(eob.outcome).toBe('complete');
      
      // Patient
      expect(eob.patient.reference).toBe('Patient/MEM77777');
      
      // Provider
      expect(eob.provider.identifier?.value).toBe('2222222222');
      
      // Items
      expect(eob.item).toHaveLength(2);
      expect(eob.item![0].productOrService.coding![0].code).toBe('99213');
      
      // Adjudication on first item
      const firstItemAdj = eob.item![0].adjudication!;
      expect(firstItemAdj.some(a => a.category.coding![0].code === 'submitted')).toBe(true);
      expect(firstItemAdj.some(a => a.category.coding![0].code === 'eligible')).toBe(true);
      expect(firstItemAdj.some(a => a.category.coding![0].code === 'benefit')).toBe(true);
      expect(firstItemAdj.some(a => a.category.coding![0].code === 'deductible')).toBe(true);
      
      // Payment
      expect(eob.payment?.amount?.value).toBe(400.00);
      expect(eob.payment?.date).toBe('2024-02-15');
      expect(eob.payment?.identifier?.value).toBe('EFT123456');
      
      // Totals
      expect(eob.total).toBeDefined();
      expect(eob.total!.some(t => t.category.coding![0].code === 'submitted')).toBe(true);
      expect(eob.total!.some(t => t.category.coding![0].code === 'benefit')).toBe(true);
    });

    it('handles multiple claims in single remittance', () => {
      const input: X12_835_Remittance = {
        transactionId: 'REM002',
        payer: {
          payerId: 'PAYER008',
          payerName: 'Aetna'
        },
        payee: {
          npi: '3333333333',
          organizationName: 'Multi Specialty Group'
        },
        payment: {
          paymentMethodCode: 'CHK',
          paymentAmount: 750.00,
          paymentDate: '20240301',
          checkOrEftNumber: 'CHK789012'
        },
        claims: [
          {
            claimId: 'CLM200',
            patient: {
              memberId: 'MEM88888',
              firstName: 'Frank',
              lastName: 'Wilson'
            },
            claimAmounts: {
              billedAmount: 400.00,
              paidAmount: 350.00
            },
            claimStatusCode: '1',
            claimDates: {
              statementFromDate: '20240201',
              processedDate: '20240228'
            },
            serviceLines: [{
              lineNumber: 1,
              procedureCode: '99214',
              serviceDate: '20240201',
              units: 1,
              amounts: {
                billedAmount: 400.00,
                paidAmount: 350.00
              }
            }]
          },
          {
            claimId: 'CLM201',
            patient: {
              memberId: 'MEM99999',
              firstName: 'Grace',
              lastName: 'Taylor'
            },
            claimAmounts: {
              billedAmount: 450.00,
              paidAmount: 400.00
            },
            claimStatusCode: '1',
            claimDates: {
              statementFromDate: '20240202',
              processedDate: '20240228'
            },
            serviceLines: [{
              lineNumber: 1,
              procedureCode: '99215',
              serviceDate: '20240202',
              units: 1,
              amounts: {
                billedAmount: 450.00,
                paidAmount: 400.00
              }
            }]
          }
        ]
      };

      const eobs = mapX12835ToFhirEOB(input);

      expect(eobs).toHaveLength(2);
      expect(eobs[0].id).toBe('CLM200');
      expect(eobs[1].id).toBe('CLM201');
    });
  });
});
