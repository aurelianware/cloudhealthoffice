/**
 * Comprehensive FHIR R4 Mappers for CMS-0057-F Compliance
 * 
 * This module provides mappings between X12 EDI transactions and FHIR R4 resources
 * to support CMS Interoperability and Prior Authorization Final Rule (CMS-0057-F).
 * 
 * Mappings included:
 * - X12 837 Professional Claim → FHIR R4 Claim
 * - X12 278 Prior Authorization → FHIR R4 ServiceRequest
 * - X12 835 Remittance Advice → FHIR R4 ExplanationOfBenefit
 * 
 * References:
 * - CMS-0057-F: Advancing Interoperability and Improving Prior Authorization Processes
 * - HL7 FHIR R4 Specification (v4.0.1)
 * - US Core Implementation Guide v3.1.1+
 * - Da Vinci PDex (Payer Data Exchange) IG
 * - Da Vinci PAS (Prior Authorization Support) IG
 * - Da Vinci CRD (Coverage Requirements Discovery) IG
 * - Da Vinci DTR (Documentation Templates and Rules) IG
 * 
 * @module fhir-mapper
 */

import { 
  Claim, 
  ServiceRequest, 
  ExplanationOfBenefit,
  Patient,
  Identifier,
  CodeableConcept,
  Reference,
  Money
} from 'fhir/r4';
import { X12_837, X12_278, X12_835 } from './x12Types';

/**
 * Maps X12 837 Professional Claim to FHIR R4 Claim resource
 * 
 * Supports CMS-0057-F requirements for claims data exchange via FHIR APIs.
 * Conforms to US Core Claim profile and Da Vinci PDex IG.
 * 
 * @param input X12 837 claim data
 * @returns FHIR R4 Claim resource
 */
export function mapX12837ToFhirClaim(input: X12_837): Claim {
  const patient = input.patient || input.subscriber;
  
  const claim: Claim = {
    resourceType: 'Claim',
    id: input.claimId,
    meta: {
      profile: [
        'http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim',
        'http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-claim'
      ]
    },
    
    // Status: active for submitted claims
    status: 'active',
    
    // Type: professional claim
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: 'professional',
        display: 'Professional'
      }]
    },
    
    // Use: claim submission
    use: 'claim',
    
    // Patient reference
    patient: {
      reference: `Patient/${input.subscriber.memberId}`,
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.subscriber.memberId,
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            code: 'MB',
            display: 'Member Number'
          }]
        }
      }
    },
    
    // Billable period (from service lines)
    billablePeriod: {
      start: normalizeDateFormat(input.claim.serviceLines[0]?.serviceDateFrom),
      end: normalizeDateFormat(
        input.claim.serviceLines[input.claim.serviceLines.length - 1]?.serviceDateTo || 
        input.claim.serviceLines[input.claim.serviceLines.length - 1]?.serviceDateFrom
      )
    },
    
    // Created timestamp
    created: input.transactionDate 
      ? normalizeDateFormat(input.transactionDate)
      : new Date().toISOString(),
    
    // Insurer/Payer
    insurer: {
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.payer.payerId,
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            code: 'NIIP',
            display: 'National Insurance Payor Identifier (Payor)'
          }]
        }
      },
      display: input.payer.name
    },
    
    // Insurance (required by FHIR R4)
    insurance: [{
      sequence: 1,
      focal: true,
      coverage: {
        identifier: {
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          value: input.payer.payerId
        }
      }
    }],
    
    // Provider (billing provider)
    provider: {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.billingProvider.npi
      },
      display: input.billingProvider.name
    },
    
    // Priority: normal for standard claims
    priority: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/processpriority',
        code: 'normal',
        display: 'Normal'
      }]
    },
    
    // Diagnosis codes
    diagnosis: input.diagnoses?.map((diag) => ({
      sequence: diag.sequence,
      diagnosisCodeableConcept: {
        coding: [{
          system: 'http://hl7.org/fhir/sid/icd-10',
          code: diag.code
        }]
      }
    })),
    
    // Service line items
    item: input.claim.serviceLines.map((line) => ({
      sequence: line.lineNumber,
      
      // Procedure code (CPT/HCPCS)
      productOrService: {
        coding: [{
          system: 'http://www.ama-assn.org/go/cpt',
          code: line.procedureCode
        }]
      },
      
      // Service date
      servicedDate: normalizeDateFormat(line.serviceDateFrom),
      
      // Quantity
      quantity: line.units ? {
        value: line.units
      } : undefined,
      
      // Unit price and net amount
      unitPrice: {
        value: line.chargeAmount / (line.units || 1),
        currency: 'USD'
      },
      
      net: {
        value: line.chargeAmount,
        currency: 'USD'
      },
      
      // Modifiers
      modifier: line.modifiers?.map((mod) => ({
        coding: [{
          system: 'http://www.ama-assn.org/go/cpt',
          code: mod
        }]
      })),
      
      // Diagnosis linkage
      diagnosisSequence: line.diagnosisPointers
    })),
    
    // Total claim amount
    total: {
      value: input.claim.totalChargeAmount,
      currency: 'USD'
    }
  };
  
  return claim;
}

/**
 * Maps X12 278 Prior Authorization Request to FHIR R4 ServiceRequest resource
 * 
 * Supports CMS-0057-F requirements for prior authorization via FHIR APIs.
 * Conforms to Da Vinci PAS (Prior Authorization Support) IG and CRD/DTR requirements.
 * 
 * @param input X12 278 authorization request data
 * @returns FHIR R4 ServiceRequest resource
 */
export function mapX12278ToFhirServiceRequest(input: X12_278): ServiceRequest {
  const patient = input.patient || input.subscriber;
  
  const serviceRequest: ServiceRequest = {
    resourceType: 'ServiceRequest',
    id: input.authorizationId,
    meta: {
      profile: [
        'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest',
        'http://hl7.org/fhir/us/davinci-crd/StructureDefinition/profile-servicerequest'
      ]
    },
    
    // Status: active for pending authorization requests
    status: 'active',
    
    // Intent: order (requesting authorization for a service)
    intent: 'order',
    
    // Category: prior authorization
    category: [{
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/service-category',
        code: 'auth',
        display: 'Prior Authorization'
      }]
    }],
    
    // Priority: routine for standard requests
    priority: 'routine',
    
    // Code: service being requested
    code: input.serviceRequest.procedureCode ? {
      coding: [{
        system: 'http://www.ama-assn.org/go/cpt',
        code: input.serviceRequest.procedureCode
      }]
    } : {
      text: input.serviceRequest.serviceTypeCode
    },
    
    // Subject (patient)
    subject: {
      reference: `Patient/${input.subscriber.memberId}`,
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.subscriber.memberId,
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            code: 'MB',
            display: 'Member Number'
          }]
        }
      }
    },
    
    // Service timing
    occurrencePeriod: {
      start: normalizeDateFormat(input.serviceRequest.serviceDateFrom),
      end: input.serviceRequest.serviceDateTo 
        ? normalizeDateFormat(input.serviceRequest.serviceDateTo)
        : undefined
    },
    
    // Authored date
    authoredOn: input.transactionDate 
      ? parseX12DateTime(input.transactionDate)
      : new Date().toISOString(),
    
    // Requester (provider)
    requester: {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.requester.npi
      },
      display: input.requester.name || input.requester.organizationName
    },
    
    // Insurance
    insurance: [{
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.payer.payerId,
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            code: 'NIIP',
            display: 'National Insurance Payor Identifier (Payor)'
          }]
        }
      },
      display: input.payer.name
    }],
    
    // Quantity (units)
    quantityQuantity: input.serviceRequest.units ? {
      value: input.serviceRequest.units
    } : undefined,
    
    // Reason code (diagnosis)
    reasonCode: input.serviceRequest.diagnosisCode ? [{
      coding: [{
        system: 'http://hl7.org/fhir/sid/icd-10',
        code: input.serviceRequest.diagnosisCode
      }]
    }] : undefined
  };
  
  return serviceRequest;
}

/**
 * Maps X12 835 Electronic Remittance Advice to FHIR R4 ExplanationOfBenefit resource
 * 
 * Supports CMS-0057-F requirements for claims adjudication data via FHIR APIs.
 * Conforms to US Core EOB profile and Da Vinci PDex IG.
 * 
 * @param input X12 835 remittance advice data
 * @returns Array of FHIR R4 ExplanationOfBenefit resources (one per claim)
 */
export function mapX12835ToFhirExplanationOfBenefit(input: X12_835): ExplanationOfBenefit[] {
  return input.claims.map((claimPayment) => {
    const eob: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      id: `${input.remittanceId}-${claimPayment.claimId}`,
      meta: {
        profile: [
          'http://hl7.org/fhir/us/core/StructureDefinition/us-core-explanationofbenefit',
          'http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-eob'
        ]
      },
      
      // Status: active for processed claims
      status: 'active',
      
      // Type: professional claim
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/claim-type',
          code: 'professional',
          display: 'Professional'
        }]
      },
      
      // Use: claim
      use: 'claim',
      
      // Patient
      patient: {
        reference: `Patient/${claimPayment.patient.memberId}`,
        identifier: {
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          value: claimPayment.patient.memberId,
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
              code: 'MB',
              display: 'Member Number'
            }]
          }
        }
      },
      
      // Billable period
      billablePeriod: {
        start: normalizeDateFormat(claimPayment.serviceDateFrom),
        end: normalizeDateFormat(claimPayment.serviceDateTo || claimPayment.serviceDateFrom)
      },
      
      // Created timestamp
      created: normalizeDateFormat(input.transactionDate),
      
      // Insurer (payer)
      insurer: {
        identifier: {
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          value: input.payer.payerId
        },
        display: input.payer.name
      },
      
      // Provider (payee)
      provider: {
        identifier: {
          system: 'http://hl7.org/fhir/sid/us-npi',
          value: input.payee.npi
        },
        display: input.payee.name
      },
      
      // Insurance (required by FHIR R4)
      insurance: [{
        focal: true,
        coverage: {
          identifier: {
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            value: input.payer.payerId
          }
        }
      }],
      
      // Outcome: complete (claim processed)
      outcome: 'complete',
      
      // Claim reference
      claim: {
        identifier: {
          value: claimPayment.claimId
        }
      },
      
      // Service line items
      item: claimPayment.serviceLines.map((line) => ({
        sequence: line.lineNumber,
        
        // Procedure code
        productOrService: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: line.procedureCode
          }]
        },
        
        // Service date
        servicedDate: normalizeDateFormat(line.serviceDate),
        
        // Modifiers
        modifier: line.modifiers?.map((mod) => ({
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: mod
          }]
        })),
        
        // Adjudication details
        adjudication: buildAdjudication(line)
      })),
      
      // Total amounts
      total: [
        {
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'submitted',
              display: 'Submitted Amount'
            }]
          },
          amount: {
            value: claimPayment.totalChargeAmount,
            currency: 'USD'
          }
        },
        {
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'benefit',
              display: 'Benefit Amount'
            }]
          },
          amount: {
            value: claimPayment.totalPaidAmount,
            currency: 'USD'
          }
        }
      ],
      
      // Payment details
      payment: {
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/ex-paymenttype',
            code: 'complete',
            display: 'Complete'
          }]
        },
        amount: {
          value: claimPayment.totalPaidAmount,
          currency: 'USD'
        },
        date: normalizeDateFormat(input.transactionDate)
      }
    };
    
    return eob;
  });
}

/**
 * Builds adjudication array for service line
 */
function buildAdjudication(line: X12_835['claims'][0]['serviceLines'][0]) {
  const adjudication = [
    {
      category: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/adjudication',
          code: 'submitted',
          display: 'Submitted Amount'
        }]
      },
      amount: {
        value: line.chargeAmount,
        currency: 'USD'
      }
    },
    {
      category: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/adjudication',
          code: 'benefit',
          display: 'Benefit Amount'
        }]
      },
      amount: {
        value: line.paidAmount,
        currency: 'USD'
      }
    }
  ];
  
  // Add allowed amount if present
  if (line.allowedAmount !== undefined) {
    adjudication.push({
      category: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/adjudication',
          code: 'eligible',
          display: 'Eligible Amount'
        }]
      },
      amount: {
        value: line.allowedAmount,
        currency: 'USD'
      }
    });
  }
  
  // Add adjustments
  // Note: FHIR ExplanationOfBenefit.item.adjudication doesn't include a 'reason' field
  // in the base spec, but adjustment reason codes are important for claims processing.
  // We include them as extensions in production implementations.
  line.adjustments?.forEach((adj) => {
    adjudication.push({
      category: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/adjudication',
          code: getAdjustmentCategoryCode(adj.groupCode),
          display: getAdjustmentCategoryDisplay(adj.groupCode)
        }]
      },
      amount: {
        value: adj.amount,
        currency: 'USD'
      }
    });
  });
  
  return adjudication;
}

/**
 * Maps X12 adjustment group codes to FHIR adjudication categories
 */
function getAdjustmentCategoryCode(groupCode: string): string {
  switch (groupCode) {
    case 'CO': return 'contractual'; // Contractual Obligation
    case 'PR': return 'copay'; // Patient Responsibility
    case 'OA': return 'adjustment'; // Other Adjustments
    case 'PI': return 'adjustment'; // Payer Initiated
    default: return 'adjustment';
  }
}

/**
 * Maps X12 adjustment group codes to display text
 */
function getAdjustmentCategoryDisplay(groupCode: string): string {
  switch (groupCode) {
    case 'CO': return 'Contractual Obligation';
    case 'PR': return 'Patient Responsibility';
    case 'OA': return 'Other Adjustments';
    case 'PI': return 'Payer Initiated';
    default: return 'Adjustment';
  }
}

/**
 * Normalizes date format from X12 (CCYYMMDD) to FHIR (YYYY-MM-DD)
 */
function normalizeDateFormat(date?: string): string {
  if (!date) return new Date().toISOString().split('T')[0];
  
  // Already in YYYY-MM-DD format
  if (date.includes('-')) return date;
  
  // Convert CCYYMMDD to YYYY-MM-DD
  if (date.length === 8) {
    return `${date.substring(0, 4)}-${date.substring(4, 6)}-${date.substring(6, 8)}`;
  }
  
  return date;
}

/**
 * Parses X12 datetime format (CCYYMMDD-HHMM) to ISO 8601
 */
function parseX12DateTime(datetime: string): string {
  if (datetime.includes('T')) return datetime; // Already ISO format
  
  const parts = datetime.split('-');
  if (parts.length === 2) {
    const date = normalizeDateFormat(parts[0]);
    const time = `${parts[1].substring(0, 2)}:${parts[1].substring(2, 4)}:00`;
    return `${date}T${time}Z`;
  }
  
  return normalizeDateFormat(datetime);
}
