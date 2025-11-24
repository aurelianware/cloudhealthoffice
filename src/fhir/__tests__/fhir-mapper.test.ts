/**
 * Tests for FHIR R4 Mapper - CMS-0057-F Compliance
 * Validates X12 to FHIR mappings for 837, 278, and 835 transactions
 */

import {
  mapX12_837_ToFhirClaim,
  mapX12_278_ToFhirServiceRequest,
  mapX12_835_ToFhirExplanationOfBenefit,
  X12_837,
  X12_278,
  X12_835,
} from '../fhir-mapper';

describe('FHIR Mapper - X12 837 to FHIR Claim', () => {
  const sampleX12_837: X12_837 = {
    claimId: 'CLM123456',
    transactionDate: '20240115-1430',
    totalClaimChargeAmount: 250.00,
    subscriber: {
      memberId: 'MEM001',
      firstName: 'John',
      lastName: 'Doe',
      middleName: 'Q',
      dob: '19850615',
      gender: 'M',
      address: {
        street1: '123 Main St',
        city: 'Boston',
        state: 'MA',
        zip: '02101',
      },
    },
    billingProvider: {
      npi: '1234567890',
      taxId: '12-3456789',
      organizationName: 'Boston Medical Group',
      address: {
        street1: '456 Medical Plaza',
        city: 'Boston',
        state: 'MA',
        zip: '02102',
      },
    },
    serviceLines: [
      {
        lineNumber: 1,
        procedureCode: '99213',
        procedureModifiers: ['25'],
        diagnosisCodes: ['E11.9', 'I10'],
        serviceDate: '20240115',
        placeOfService: '11',
        units: 1,
        chargeAmount: 150.00,
      },
      {
        lineNumber: 2,
        procedureCode: '80053',
        serviceDate: '20240115',
        placeOfService: '11',
        units: 1,
        chargeAmount: 100.00,
      },
    ],
    diagnosisCodes: [
      { code: 'E11.9', type: 'ICD-10' },
      { code: 'I10', type: 'ICD-10' },
    ],
    payerId: 'PAYER123',
    payerName: 'Sample Health Plan',
  };

  it('maps X12 837 to FHIR Claim with all required fields', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.resourceType).toBe('Claim');
    expect(claim.id).toBe('CLM123456');
    expect(claim.status).toBe('active');
    expect(claim.type.coding![0].code).toBe('professional');
    expect(claim.use).toBe('claim');
  });

  it('maps patient reference correctly', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.patient.identifier!.value).toBe('MEM001');
    expect(claim.patient.display).toContain('John');
    expect(claim.patient.display).toContain('Doe');
  });

  it('maps billing provider with NPI', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.provider.identifier!.system).toBe('http://hl7.org/fhir/sid/us-npi');
    expect(claim.provider.identifier!.value).toBe('1234567890');
    expect(claim.provider.display).toBe('Boston Medical Group');
  });

  it('maps insurer/payer information', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.insurer).toBeDefined();
    expect(claim.insurer!.identifier!.value).toBe('PAYER123');
    expect(claim.insurer!.display).toBe('Sample Health Plan');
  });

  it('maps diagnosis codes to FHIR format', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.diagnosis).toBeDefined();
    expect(claim.diagnosis!.length).toBe(2);
    expect(claim.diagnosis![0].sequence).toBe(1);
    expect(claim.diagnosis![0].diagnosisCodeableConcept!.coding![0].code).toBe('E11.9');
    expect(claim.diagnosis![0].diagnosisCodeableConcept!.coding![0].system).toBe('http://hl7.org/fhir/sid/icd-10-cm');
  });

  it('maps service line items correctly', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.item).toBeDefined();
    expect(claim.item!.length).toBe(2);
    
    const firstLine = claim.item![0];
    expect(firstLine.sequence).toBe(1);
    expect(firstLine.productOrService.coding![0].code).toBe('99213');
    expect(firstLine.productOrService.coding![0].system).toBe('http://www.ama-assn.org/go/cpt');
    expect(firstLine.servicedDate).toBe('2024-01-15');
    expect(firstLine.quantity!.value).toBe(1);
    expect(firstLine.net!.value).toBe(150.00);
  });

  it('maps total claim charge amount', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.total).toBeDefined();
    expect(claim.total!.value).toBe(250.00);
    expect(claim.total!.currency).toBe('USD');
  });

  it('includes US Core profile reference', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.meta?.profile).toBeDefined();
    expect(claim.meta?.profile![0]).toContain('us-core');
  });

  it('normalizes X12 dates to FHIR format', () => {
    const claim = mapX12_837_ToFhirClaim(sampleX12_837);

    expect(claim.created).toBe('2024-01-15T14:30:00Z');
    expect(claim.item![0].servicedDate).toBe('2024-01-15');
  });
});

describe('FHIR Mapper - X12 278 to FHIR ServiceRequest', () => {
  const sampleX12_278: X12_278 = {
    authorizationNumber: 'AUTH123456',
    requestId: 'REQ789012',
    requestType: 'AR',
    requestCategory: '1',
    serviceTypeCode: '48',
    levelOfService: 'U',
    patient: {
      memberId: 'MEM002',
      firstName: 'Jane',
      lastName: 'Smith',
      dob: '19900321',
      gender: 'F',
    },
    requester: {
      npi: '9876543210',
      organizationName: 'City Hospital',
      contactName: 'Dr. Johnson',
    },
    serviceDetails: {
      admissionDate: '20240120',
      dischargeDate: '20240125',
      diagnosisCode: 'S72.001A',
      procedureCode: '27236',
      requestedQuantity: 5,
      quantityUnit: 'DA',
      placeOfService: '21',
    },
    payerId: 'PAYER456',
    payerName: 'Regional Health Plan',
    certificationInfo: {
      certType: 'I',
      beginDate: '20240120',
      endDate: '20240125',
    },
  };

  it('maps X12 278 to FHIR ServiceRequest with required fields', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.resourceType).toBe('ServiceRequest');
    expect(serviceRequest.id).toBe('REQ789012');
    expect(serviceRequest.status).toBe('active');
    expect(serviceRequest.intent).toBe('order');
  });

  it('includes Da Vinci PAS profile reference', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.meta?.profile).toBeDefined();
    expect(serviceRequest.meta?.profile![0]).toContain('davinci-pas');
  });

  it('maps patient reference correctly', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.subject.identifier!.value).toBe('MEM002');
    expect(serviceRequest.subject.display).toContain('Jane');
    expect(serviceRequest.subject.display).toContain('Smith');
  });

  it('maps requester (provider) with NPI', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.requester!.identifier!.system).toBe('http://hl7.org/fhir/sid/us-npi');
    expect(serviceRequest.requester!.identifier!.value).toBe('9876543210');
    expect(serviceRequest.requester!.display).toBe('City Hospital');
  });

  it('maps service type code', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.code).toBeDefined();
    expect(serviceRequest.code!.coding![0].code).toBe('48');
    expect(serviceRequest.code!.coding![0].system).toBe('https://x12.org/codes/service-type-codes');
    expect(serviceRequest.code!.coding![0].display).toContain('Hospital');
  });

  it('maps occurrence period from service dates', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.occurrencePeriod).toBeDefined();
    expect(serviceRequest.occurrencePeriod!.start).toBe('2024-01-20');
    expect(serviceRequest.occurrencePeriod!.end).toBe('2024-01-25');
  });

  it('maps requested quantity', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.quantityQuantity).toBeDefined();
    expect(serviceRequest.quantityQuantity!.value).toBe(5);
    expect(serviceRequest.quantityQuantity!.unit).toBe('DA');
  });

  it('includes insurance reference', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.insurance).toBeDefined();
    expect(serviceRequest.insurance!.length).toBeGreaterThan(0);
    expect(serviceRequest.insurance![0].identifier!.value).toBe('PAYER456');
    expect(serviceRequest.insurance![0].display).toBe('Regional Health Plan');
  });

  it('sets appropriate priority based on request type', () => {
    const urgentRequest = { ...sampleX12_278, requestType: 'HS' as const };
    const serviceRequest = mapX12_278_ToFhirServiceRequest(urgentRequest);

    expect(serviceRequest.priority).toBe('urgent');
  });

  it('includes supporting info for diagnosis and procedure', () => {
    const serviceRequest = mapX12_278_ToFhirServiceRequest(sampleX12_278);

    expect(serviceRequest.supportingInfo).toBeDefined();
    expect(serviceRequest.supportingInfo!.length).toBeGreaterThan(0);
    
    const diagnosisInfo = serviceRequest.supportingInfo!.find(
      si => si.reference?.includes('S72.001A')
    );
    expect(diagnosisInfo).toBeDefined();
  });
});

describe('FHIR Mapper - X12 835 to FHIR ExplanationOfBenefit', () => {
  const sampleX12_835: X12_835 = {
    transactionId: 'TXN123456',
    paymentDate: '2024-01-20',
    paymentAmount: 180.00,
    paymentMethod: 'ACH',
    payer: {
      id: 'PAYER789',
      name: 'National Health Plan',
      address: {
        street1: '100 Insurance Blvd',
        city: 'Hartford',
        state: 'CT',
        zip: '06103',
      },
    },
    payee: {
      npi: '1234567890',
      taxId: '12-3456789',
      organizationName: 'Boston Medical Group',
    },
    claims: [
      {
        claimId: 'CLM123456',
        patientControlNumber: 'PCN001',
        claimStatusCode: '1',
        chargedAmount: 250.00,
        paidAmount: 180.00,
        patientResponsibility: 50.00,
        patient: {
          firstName: 'John',
          lastName: 'Doe',
          memberId: 'MEM001',
        },
        serviceLines: [
          {
            procedureCode: '99213',
            serviceDate: '20240115',
            chargedAmount: 150.00,
            paidAmount: 120.00,
            adjustmentReasonCodes: [
              {
                groupCode: 'CO',
                reasonCode: '45',
                amount: 30.00,
              },
            ],
          },
          {
            procedureCode: '80053',
            serviceDate: '20240115',
            chargedAmount: 100.00,
            paidAmount: 60.00,
            adjustmentReasonCodes: [
              {
                groupCode: 'PR',
                reasonCode: '1',
                amount: 40.00,
              },
            ],
          },
        ],
        adjustments: [
          {
            groupCode: 'CO',
            reasonCode: '45',
            amount: 30.00,
          },
          {
            groupCode: 'PR',
            reasonCode: '1',
            amount: 40.00,
          },
        ],
      },
    ],
  };

  it('maps X12 835 to FHIR ExplanationOfBenefit', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.resourceType).toBe('ExplanationOfBenefit');
    expect(eob.id).toBe('CLM123456');
    expect(eob.status).toBe('active');
    expect(eob.type.coding![0].code).toBe('professional');
    expect(eob.use).toBe('claim');
    expect(eob.outcome).toBe('complete');
  });

  it('includes US Core profile reference', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.meta?.profile).toBeDefined();
    expect(eob.meta?.profile![0]).toContain('us-core');
  });

  it('maps patient information', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.patient.identifier!.value).toBe('MEM001');
    expect(eob.patient.display).toContain('John');
    expect(eob.patient.display).toContain('Doe');
  });

  it('maps insurer (payer) information', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.insurer.identifier!.value).toBe('PAYER789');
    expect(eob.insurer.display).toBe('National Health Plan');
  });

  it('maps provider (payee) with NPI', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.provider.identifier!.system).toBe('http://hl7.org/fhir/sid/us-npi');
    expect(eob.provider.identifier!.value).toBe('1234567890');
    expect(eob.provider.display).toBe('Boston Medical Group');
  });

  it('maps payment information', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.payment).toBeDefined();
    expect(eob.payment!.date).toBe('2024-01-20');
    expect(eob.payment!.amount!.value).toBe(180.00);
    expect(eob.payment!.amount!.currency).toBe('USD');
  });

  it('maps service line items with adjudication', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.item).toBeDefined();
    expect(eob.item!.length).toBe(2);
    
    const firstLine = eob.item![0];
    expect(firstLine.sequence).toBe(1);
    expect(firstLine.productOrService.coding![0].code).toBe('99213');
    expect(firstLine.servicedDate).toBe('2024-01-15');
    expect(firstLine.net!.value).toBe(150.00);
    
    expect(firstLine.adjudication).toBeDefined();
    expect(firstLine.adjudication!.length).toBeGreaterThan(0);
  });

  it('maps total amounts including submitted and benefit', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    expect(eob.total).toBeDefined();
    expect(eob.total!.length).toBeGreaterThan(0);
    
    const submittedTotal = eob.total!.find(
      t => t.category.coding![0].code === 'submitted'
    );
    expect(submittedTotal!.amount.value).toBe(250.00);
    
    const benefitTotal = eob.total!.find(
      t => t.category.coding![0].code === 'benefit'
    );
    expect(benefitTotal!.amount.value).toBe(180.00);
  });

  it('includes patient responsibility in totals', () => {
    const eob = mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 0);

    const deductibleTotal = eob.total!.find(
      t => t.category.coding![0].code === 'deductible'
    );
    expect(deductibleTotal).toBeDefined();
    expect(deductibleTotal!.amount.value).toBe(50.00);
  });

  it('throws error for invalid claim index', () => {
    expect(() => {
      mapX12_835_ToFhirExplanationOfBenefit(sampleX12_835, 5);
    }).toThrow('Claim index 5 not found');
  });

  it('handles multiple claims in remittance', () => {
    const multiClaimRemittance = {
      ...sampleX12_835,
      claims: [
        ...sampleX12_835.claims,
        {
          ...sampleX12_835.claims[0],
          claimId: 'CLM789012',
          patient: {
            firstName: 'Jane',
            lastName: 'Smith',
            memberId: 'MEM002',
          },
        },
      ],
    };

    const eob1 = mapX12_835_ToFhirExplanationOfBenefit(multiClaimRemittance, 0);
    const eob2 = mapX12_835_ToFhirExplanationOfBenefit(multiClaimRemittance, 1);

    expect(eob1.id).toBe('CLM123456');
    expect(eob2.id).toBe('CLM789012');
    expect(eob2.patient.display).toContain('Jane Smith');
  });
});

describe('Date Normalization', () => {
  it('converts X12 date format CCYYMMDD to YYYY-MM-DD', () => {
    const x12_837: X12_837 = {
      claimId: 'TEST',
      transactionDate: '20240115-0000',
      totalClaimChargeAmount: 100,
      subscriber: {
        memberId: 'MEM001',
        firstName: 'Test',
        lastName: 'User',
        dob: '19850615',
      },
      billingProvider: {
        npi: '1234567890',
      },
      serviceLines: [{
        lineNumber: 1,
        procedureCode: '99213',
        serviceDate: '20240115',
        units: 1,
        chargeAmount: 100,
      }],
      diagnosisCodes: [],
      payerId: 'PAYER',
    };

    const claim = mapX12_837_ToFhirClaim(x12_837);
    expect(claim.item![0].servicedDate).toBe('2024-01-15');
  });

  it('handles dates already in YYYY-MM-DD format', () => {
    const x12_837: X12_837 = {
      claimId: 'TEST',
      transactionDate: '2024-01-15-00:00',
      totalClaimChargeAmount: 100,
      subscriber: {
        memberId: 'MEM001',
        firstName: 'Test',
        lastName: 'User',
        dob: '1985-06-15',
      },
      billingProvider: {
        npi: '1234567890',
      },
      serviceLines: [{
        lineNumber: 1,
        procedureCode: '99213',
        serviceDate: '2024-01-15',
        units: 1,
        chargeAmount: 100,
      }],
      diagnosisCodes: [],
      payerId: 'PAYER',
    };

    const claim = mapX12_837_ToFhirClaim(x12_837);
    expect(claim.item![0].servicedDate).toBe('2024-01-15');
  });

  it('handles X12 datetime without separator (CCYYMMDDHHMM)', () => {
    const x12_837 = {
      claimId: 'TEST',
      transactionDate: '202401151430', // 12 characters, no separator
      totalClaimChargeAmount: 100,
      subscriber: {
        memberId: 'MEM001',
        firstName: 'Test',
        lastName: 'User',
        dob: '19850615',
      },
      billingProvider: {
        npi: '1234567890',
      },
      serviceLines: [{
        lineNumber: 1,
        procedureCode: '99213',
        serviceDate: '20240115',
        units: 1,
        chargeAmount: 100,
      }],
      diagnosisCodes: [],
      payerId: 'PAYER',
    };

    const claim = mapX12_837_ToFhirClaim(x12_837);
    expect(claim.created).toBe('2024-01-15T14:30:00Z');
  });
});
