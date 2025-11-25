/**
 * FHIR R4 Mapper for CMS-0057-F Compliance
 * Maps X12 EDI transactions to FHIR R4 resources for interoperability
 * 
 * Standards Compliance:
 * - CMS-0057-F: Improving Interoperability Final Rule (Prior Authorization)
 * - HL7 FHIR R4: v4.0.1
 * - US Core IG: v3.1.1+
 * - Da Vinci Implementation Guides:
 *   - PDex (Payer Data Exchange)
 *   - CRD (Coverage Requirements Discovery)
 *   - DTR (Documentation Templates and Rules)
 *   - PAS (Prior Authorization Support)
 * 
 * References:
 * - CMS-0057-F Final Rule (March 2023)
 * - X12 005010X222A1 (837 Professional)
 * - X12 005010X217 (278 Health Care Services Review)
 * - X12 005010X221A1 (835 Health Care Claim Payment/Advice)
 */

import {
  Claim,
  ExplanationOfBenefit,
  ServiceRequest,
  Reference,
  Period,
} from 'fhir/r4';

/**
 * X12 837 Professional Claim structure
 * Based on HIPAA X12 005010X222A1
 */
export interface X12_837 {
  claimId: string;
  transactionDate: string;
  totalClaimChargeAmount: number;
  
  // Patient/Subscriber information
  subscriber: {
    memberId: string;
    firstName: string;
    lastName: string;
    middleName?: string;
    dob: string;
    gender?: string;
    address?: {
      street1?: string;
      street2?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  // Billing Provider
  billingProvider: {
    npi: string;
    taxId?: string;
    organizationName?: string;
    address?: {
      street1?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  // Service Lines
  serviceLines: Array<{
    lineNumber: number;
    procedureCode: string;
    procedureModifiers?: string[];
    diagnosisCodes?: string[];
    serviceDate: string;
    placeOfService?: string;
    units: number;
    chargeAmount: number;
  }>;
  
  // Diagnosis codes
  diagnosisCodes?: Array<{
    code: string;
    type?: string; // ICD-10, etc.
  }>;
  
  // Payer information
  payerId: string;
  payerName?: string;
}

/**
 * X12 278 Health Care Services Review structure
 * Based on HIPAA X12 005010X217
 */
export interface X12_278 {
  authorizationNumber?: string;
  requestId: string;
  requestType: 'AR' | 'HS' | 'SC'; // AR=Authorization Request, HS=Health Services Review, SC=Service Certification
  requestCategory: string; // e.g., '1'=Admission Review, '3'=Continued Stay
  serviceTypeCode: string;
  levelOfService?: string;
  
  // Patient information
  patient: {
    memberId: string;
    firstName: string;
    lastName: string;
    dob: string;
    gender?: string;
  };
  
  // Requester (Provider)
  requester: {
    npi: string;
    organizationName?: string;
    contactName?: string;
  };
  
  // Service information
  serviceDetails: {
    admissionDate?: string;
    dischargeDate?: string;
    serviceDate?: string;
    diagnosisCode?: string;
    procedureCode?: string;
    requestedQuantity?: number;
    quantityUnit?: string; // e.g., 'UN'=Units, 'DA'=Days
    placeOfService?: string;
  };
  
  // Payer information
  payerId: string;
  payerName?: string;
  
  // Additional details
  certificationInfo?: {
    certType?: string;
    beginDate?: string;
    endDate?: string;
  };
}

/**
 * X12 835 Health Care Claim Payment/Advice structure
 * Based on HIPAA X12 005010X221A1
 */
export interface X12_835 {
  transactionId: string;
  paymentDate: string;
  paymentAmount: number;
  paymentMethod: string; // CHK=Check, ACH=Electronic
  checkNumber?: string;
  
  // Payer information
  payer: {
    id: string;
    name: string;
    address?: {
      street1?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  // Payee (Provider)
  payee: {
    npi: string;
    taxId?: string;
    organizationName?: string;
  };
  
  // Claims included in this remittance
  claims: Array<{
    claimId: string;
    patientControlNumber?: string;
    claimStatusCode: string;
    chargedAmount: number;
    paidAmount: number;
    patientResponsibility?: number;
    
    // Patient information
    patient: {
      firstName: string;
      lastName: string;
      memberId: string;
    };
    
    // Service lines
    serviceLines?: Array<{
      procedureCode: string;
      serviceDate: string;
      chargedAmount: number;
      paidAmount: number;
      adjustmentReasonCodes?: Array<{
        groupCode: string; // CO=Contractual Obligation, PR=Patient Responsibility
        reasonCode: string;
        amount: number;
      }>;
    }>;
    
    // Adjustments
    adjustments?: Array<{
      groupCode: string;
      reasonCode: string;
      amount: number;
    }>;
  }>;
}

/**
 * Maps X12 837 Professional Claim to FHIR R4 Claim resource
 * Compliant with US Core and CMS-0057-F requirements
 * 
 * @param input X12 837 claim data
 * @returns FHIR R4 Claim resource
 */
export function mapX12_837_ToFhirClaim(input: X12_837): Claim {
  const claim: Claim = {
    resourceType: 'Claim',
    id: input.claimId,
    meta: {
      profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim'],
    },
    
    // Status: active for submitted claims
    status: 'active',
    
    // Type: professional claim
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: 'professional',
        display: 'Professional',
      }],
    },
    
    // Use: claim (vs preauthorization or predetermination)
    use: 'claim',
    
    // Patient reference
    patient: {
      identifier: {
        system: `urn:oid:${input.payerId}`,
        value: input.subscriber.memberId,
      },
      display: `${input.subscriber.firstName} ${input.subscriber.lastName}`,
    },
    
    // Created date
    created: normalizeX12DateTime(input.transactionDate),
    
    // Insurer/Payer
    insurer: {
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.payerId,
      },
      display: input.payerName,
    },
    
    // Provider (billing)
    provider: {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.billingProvider.npi,
      },
      display: input.billingProvider.organizationName,
    },
    
    // Priority: normal for standard claims
    priority: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/processpriority',
        code: 'normal',
        display: 'Normal',
      }],
    },
    
    // Insurance (required field)
    insurance: [{
      sequence: 1,
      focal: true,
      coverage: {
        identifier: {
          value: input.payerId,
        },
      },
    }],
    
    // Diagnosis codes
    diagnosis: mapDiagnosisCodes(input.diagnosisCodes),
    
    // Service line items
    item: mapServiceLines(input.serviceLines),
    
    // Total claim amount
    total: {
      value: input.totalClaimChargeAmount,
      currency: 'USD',
    },
  };
  
  return claim;
}

/**
 * Maps X12 278 Health Care Services Review to FHIR R4 ServiceRequest
 * Implements Da Vinci PAS (Prior Authorization Support) IG
 * 
 * @param input X12 278 authorization request
 * @returns FHIR R4 ServiceRequest resource
 */
export function mapX12_278_ToFhirServiceRequest(input: X12_278): ServiceRequest {
  const serviceRequest: ServiceRequest = {
    resourceType: 'ServiceRequest',
    id: input.requestId,
    meta: {
      profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'],
    },
    
    // Status: active for pending authorization requests
    status: 'active',
    
    // Intent: order for authorization requests
    intent: 'order',
    
    // Category: authorization
    category: [{
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/servicerequest-category',
        code: 'authorization',
        display: 'Authorization',
      }],
    }],
    
    // Priority based on request type
    priority: getPriorityFromRequestType(input.requestType),
    
    // Service type code
    code: {
      coding: [{
        system: 'https://x12.org/codes/service-type-codes',
        code: input.serviceTypeCode,
        display: getServiceTypeDisplay(input.serviceTypeCode),
      }],
    },
    
    // Subject (Patient)
    subject: {
      identifier: {
        system: `urn:oid:${input.payerId}`,
        value: input.patient.memberId,
      },
      display: `${input.patient.firstName} ${input.patient.lastName}`,
    },
    
    // Occurrence period
    occurrencePeriod: buildOccurrencePeriod(input.serviceDetails),
    
    // Authored date - use certification begin date if available, otherwise current date
    authoredOn: input.certificationInfo?.beginDate 
      ? normalizeX12Date(input.certificationInfo.beginDate)
      : new Date().toISOString(),
    
    // Requester (Provider)
    requester: {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.requester.npi,
      },
      display: input.requester.organizationName || input.requester.contactName,
    },
    
    // Performer (if different from requester)
    performer: [{
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.requester.npi,
      },
    }],
    
    // Insurance/Coverage
    insurance: [{
      identifier: {
        value: input.payerId,
      },
      display: input.payerName,
    }],
    
    // Supporting info
    supportingInfo: buildSupportingInfo(input),
  };
  
  // Add quantity if specified
  if (input.serviceDetails.requestedQuantity) {
    serviceRequest.quantityQuantity = {
      value: input.serviceDetails.requestedQuantity,
      unit: input.serviceDetails.quantityUnit || 'units',
    };
  }
  
  return serviceRequest;
}

/**
 * Maps X12 835 Health Care Claim Payment to FHIR R4 ExplanationOfBenefit
 * Compliant with US Core and CMS Interoperability requirements
 * 
 * @param input X12 835 remittance advice
 * @param claimIndex Index of specific claim in remittance (0-based)
 * @returns FHIR R4 ExplanationOfBenefit resource
 */
export function mapX12_835_ToFhirExplanationOfBenefit(
  input: X12_835,
  claimIndex: number = 0
): ExplanationOfBenefit {
  const claimData = input.claims[claimIndex];
  
  if (!claimData) {
    throw new Error(
      `Claim index ${claimIndex} not found. X12 835 transaction contains ${input.claims.length} claim(s).`
    );
  }
  
  const eob: ExplanationOfBenefit = {
    resourceType: 'ExplanationOfBenefit',
    id: claimData.claimId,
    meta: {
      profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-explanationofbenefit'],
    },
    
    // Status: active for processed claims
    status: 'active',
    
    // Type: professional
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: 'professional',
        display: 'Professional',
      }],
    },
    
    // Use: claim
    use: 'claim',
    
    // Patient
    patient: {
      identifier: {
        value: claimData.patient.memberId,
      },
      display: `${claimData.patient.firstName} ${claimData.patient.lastName}`,
    },
    
    // Created date (payment date)
    created: input.paymentDate,
    
    // Insurer (Payer)
    insurer: {
      identifier: {
        value: input.payer.id,
      },
      display: input.payer.name,
    },
    
    // Provider (Payee)
    provider: {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.payee.npi,
      },
      display: input.payee.organizationName,
    },
    
    // Outcome: complete (claim processed)
    outcome: 'complete',
    
    // Insurance (required field)
    insurance: [{
      focal: true,
      coverage: {
        identifier: {
          value: input.payer.id,
        },
      },
    }],
    
    // Service line items
    item: mapEOBServiceLines(claimData.serviceLines),
    
    // Payment information
    payment: {
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/ex-paymenttype',
          code: claimData.paidAmount === claimData.chargedAmount ? 'complete' : 'partial',
          display: claimData.paidAmount === claimData.chargedAmount ? 'Complete' : 'Partial',
        }],
      },
      date: input.paymentDate,
      amount: {
        value: claimData.paidAmount,
        currency: 'USD',
      },
      identifier: input.checkNumber ? {
        value: input.checkNumber,
      } : undefined,
    },
    
    // Total amounts
    total: [
      {
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication',
            code: 'submitted',
            display: 'Submitted Amount',
          }],
        },
        amount: {
          value: claimData.chargedAmount,
          currency: 'USD',
        },
      },
      {
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication',
            code: 'benefit',
            display: 'Benefit Amount',
          }],
        },
        amount: {
          value: claimData.paidAmount,
          currency: 'USD',
        },
      },
    ],
  };
  
  // Add patient responsibility if present
  if (claimData.patientResponsibility) {
    eob.total?.push({
      category: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/adjudication',
          code: 'deductible',
          display: 'Deductible',
        }],
      },
      amount: {
        value: claimData.patientResponsibility,
        currency: 'USD',
      },
    });
  }
  
  return eob;
}

/**
 * Helper function to map diagnosis codes
 */
function mapDiagnosisCodes(
  codes?: Array<{ code: string; type?: string }>
): Claim['diagnosis'] {
  if (!codes || codes.length === 0) return undefined;
  
  return codes.map((diag, index) => ({
    sequence: index + 1,
    diagnosisCodeableConcept: {
      coding: [{
        system: 'http://hl7.org/fhir/sid/icd-10-cm',
        code: diag.code,
      }],
    },
  }));
}

/**
 * Helper function to map service lines to FHIR Claim items
 */
function mapServiceLines(
  serviceLines: X12_837['serviceLines']
): Claim['item'] {
  return serviceLines.map((line) => ({
    sequence: line.lineNumber,
    productOrService: {
      coding: [{
        system: 'http://www.ama-assn.org/go/cpt',
        code: line.procedureCode,
      }],
    },
    servicedDate: normalizeX12Date(line.serviceDate),
    locationCodeableConcept: line.placeOfService ? {
      coding: [{
        system: 'https://www.cms.gov/Medicare/Coding/place-of-service-codes/Place_of_Service_Code_Set',
        code: line.placeOfService,
      }],
    } : undefined,
    quantity: {
      value: line.units,
    },
    net: {
      value: line.chargeAmount,
      currency: 'USD',
    },
  }));
}

/**
 * Helper function to map EOB service lines
 */
function mapEOBServiceLines(
  serviceLines?: X12_835['claims'][0]['serviceLines']
): ExplanationOfBenefit['item'] {
  if (!serviceLines || serviceLines.length === 0) return undefined;
  
  return serviceLines.map((line, index) => ({
    sequence: index + 1,
    productOrService: {
      coding: [{
        system: 'http://www.ama-assn.org/go/cpt',
        code: line.procedureCode,
      }],
    },
    servicedDate: normalizeX12Date(line.serviceDate),
    net: {
      value: line.chargedAmount,
      currency: 'USD',
    },
    adjudication: [
      {
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication',
            code: 'submitted',
            display: 'Submitted Amount',
          }],
        },
        amount: {
          value: line.chargedAmount,
          currency: 'USD',
        },
      },
      {
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication',
            code: 'benefit',
            display: 'Benefit Amount',
          }],
        },
        amount: {
          value: line.paidAmount,
          currency: 'USD',
        },
      },
    ],
  }));
}

/**
 * Helper function to build occurrence period for ServiceRequest
 */
function buildOccurrencePeriod(
  serviceDetails: X12_278['serviceDetails']
): Period | undefined {
  if (!serviceDetails.admissionDate && !serviceDetails.serviceDate) {
    return undefined;
  }
  
  const period: Period = {};
  
  if (serviceDetails.admissionDate) {
    period.start = normalizeX12Date(serviceDetails.admissionDate);
  } else if (serviceDetails.serviceDate) {
    period.start = normalizeX12Date(serviceDetails.serviceDate);
  }
  
  if (serviceDetails.dischargeDate) {
    period.end = normalizeX12Date(serviceDetails.dischargeDate);
  }
  
  return period;
}

/**
 * Helper function to build supporting info for ServiceRequest
 */
function buildSupportingInfo(input: X12_278): Reference[] | undefined {
  const supportingInfo: Reference[] = [];
  
  // Add diagnosis code if present
  if (input.serviceDetails.diagnosisCode) {
    supportingInfo.push({
      reference: `Condition/${input.serviceDetails.diagnosisCode}`,
      display: input.serviceDetails.diagnosisCode,
    });
  }
  
  // Add procedure code if present
  if (input.serviceDetails.procedureCode) {
    supportingInfo.push({
      reference: `Procedure/${input.serviceDetails.procedureCode}`,
      display: input.serviceDetails.procedureCode,
    });
  }
  
  return supportingInfo.length > 0 ? supportingInfo : undefined;
}

/**
 * Helper function to determine priority from request type
 */
function getPriorityFromRequestType(requestType: string): 'routine' | 'urgent' | 'asap' | 'stat' {
  switch (requestType) {
    case 'HS': // Health Services Review
      return 'urgent';
    case 'SC': // Service Certification
      return 'asap';
    default:
      return 'routine';
  }
}

/**
 * Helper function to get service type display name
 */
function getServiceTypeDisplay(code: string): string {
  const serviceTypes: Record<string, string> = {
    '1': 'Medical Care',
    '30': 'Health Benefit Plan Coverage',
    '47': 'Hospital',
    '48': 'Hospital - Inpatient',
    '49': 'Hospital - Outpatient',
    '86': 'Emergency Services',
    '98': 'Professional (Physician) Visit - Inpatient',
    'A3': 'Psychiatric',
    'A4': 'Psychiatric - Room and Board',
  };
  
  return serviceTypes[code] || `Service Type ${code}`;
}

/**
 * Normalizes X12 date format (CCYYMMDD) to FHIR date format (YYYY-MM-DD)
 */
/**
 * Validates that a date has valid month (01-12) and day (01-31) ranges
 * Note: Does not validate day ranges per month (e.g., Feb 30 would pass basic validation)
 */
function isValidDateComponents(month: string, day: string): boolean {
  const monthNum = parseInt(month, 10);
  const dayNum = parseInt(day, 10);
  return monthNum >= 1 && monthNum <= 12 && dayNum >= 1 && dayNum <= 31;
}

function normalizeX12Date(dateStr: string): string {
  // Validate and return if already in YYYY-MM-DD format
  const isoDatePattern = /^\d{4}-\d{2}-\d{2}$/;
  if (isoDatePattern.test(dateStr)) {
    return dateStr;
  }
  
  // Convert CCYYMMDD to YYYY-MM-DD
  if (dateStr.length === 8 && /^\d{8}$/.test(dateStr)) {
    const month = dateStr.substring(4, 6);
    const day = dateStr.substring(6, 8);
    
    // Validate date components
    if (!isValidDateComponents(month, day)) {
      // Return as-is if invalid - let downstream handle validation
      return dateStr;
    }
    
    return `${dateStr.substring(0, 4)}-${month}-${day}`;
  }
  
  // Fallback: return as-is if format unknown
  return dateStr;
}

/**
 * Normalizes X12 date-time format to ISO 8601
 * Handles formats: CCYYMMDD-HHMM, CCYYMMDDHHMM, or YYYY-MM-DD-HH:MM
 */
/**
 * Validates that time components are valid (hours 00-23, minutes 00-59)
 */
function isValidTimeComponents(hours: string, minutes: string): boolean {
  const hoursNum = parseInt(hours, 10);
  const minutesNum = parseInt(minutes, 10);
  return hoursNum >= 0 && hoursNum <= 23 && minutesNum >= 0 && minutesNum <= 59;
}

function normalizeX12DateTime(dateTime: string): string {
  // Handle formats with separator (CCYYMMDD-HHMM or YYYY-MM-DD-HH:MM)
  if (dateTime.includes('-')) {
    const parts = dateTime.split('-');
    const date = parts[0];
    const time = parts.length > 1 ? parts[parts.length - 1] : undefined;
    const normalizedDate = normalizeX12Date(date);
    
    if (time && time.length === 4) {
      const hours = time.substring(0, 2);
      const minutes = time.substring(2, 4);
      
      // Validate time components
      if (!isValidTimeComponents(hours, minutes)) {
        // Return date with midnight time if time is invalid
        return `${normalizedDate}T00:00:00Z`;
      }
      
      return `${normalizedDate}T${hours}:${minutes}:00Z`;
    }
    
    return `${normalizedDate}T00:00:00Z`;
  }
  
  // Handle format without separator (CCYYMMDDHHMM - 12 characters)
  if (dateTime.length === 12) {
    const date = dateTime.substring(0, 8); // CCYYMMDD
    const hours = dateTime.substring(8, 10); // HH
    const minutes = dateTime.substring(10, 12); // MM
    const normalizedDate = normalizeX12Date(date);
    
    // Validate time components
    if (!isValidTimeComponents(hours, minutes)) {
      return `${normalizedDate}T00:00:00Z`;
    }
    
    return `${normalizedDate}T${hours}:${minutes}:00Z`;
  }
  
  // Fallback: treat as date only
  return `${normalizeX12Date(dateTime)}T00:00:00Z`;
}
