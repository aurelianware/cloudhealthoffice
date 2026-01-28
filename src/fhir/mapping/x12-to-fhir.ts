/**
 * X12 to FHIR R4 Mapping Module
 * 
 * Consolidated mapping logic for converting HIPAA X12 EDI transactions to FHIR R4 resources.
 * Supports CMS-0057-F Patient Access API requirements.
 * 
 * Mappings:
 * - X12 837 (Professional/Institutional/Dental Claims) → FHIR Claim
 * - X12 835 (Remittance Advice) → FHIR ExplanationOfBenefit
 * - X12 278 (Prior Authorization) → FHIR ServiceRequest
 * 
 * References:
 * - HIPAA X12 005010X222, 005010X223, 005010X224 (837)
 * - HIPAA X12 005010X221 (835)
 * - HIPAA X12 005010X217 (278)
 * - HL7 FHIR R4.0.1
 * - US Core Implementation Guide v3.1.1+
 * - Da Vinci Payer Data Exchange (PDex) IG
 */

import { 
  Claim, 
  ExplanationOfBenefit,
  ServiceRequest,
  CodeableConcept
} from 'fhir/r4';
import { X12_837_Claim, X12_278_Request, X12_835_Remittance } from '../x12ClaimTypes';

// ============================================================================
// Helper Functions
// ============================================================================

/**
 * Format date from X12 CCYYMMDD or YYYY-MM-DD to FHIR date format
 */
function formatDate(date: string): string {
  if (!date) return '';
  
  // If already in YYYY-MM-DD format, return as-is
  if (date.match(/^\d{4}-\d{2}-\d{2}$/)) {
    return date;
  }
  
  // Convert CCYYMMDD to YYYY-MM-DD
  if (date.length === 8) {
    return `${date.substring(0, 4)}-${date.substring(4, 6)}-${date.substring(6, 8)}`;
  }
  
  return date;
}

/**
 * Map X12 claim type to FHIR CodeableConcept
 */
function mapClaimType(claimType: 'P' | 'I' | 'D'): CodeableConcept {
  const typeMap: Record<string, { code: string; display: string }> = {
    'P': { code: 'professional', display: 'Professional' },
    'I': { code: 'institutional', display: 'Institutional' },
    'D': { code: 'oral', display: 'Dental' }
  };
  
  const mapped = typeMap[claimType] || typeMap['P'];
  
  return {
    coding: [{
      system: 'http://terminology.hl7.org/CodeSystem/claim-type',
      code: mapped.code,
      display: mapped.display
    }]
  };
}

// ============================================================================
// X12 837 → FHIR Claim Mapping
// ============================================================================

/**
 * Maps X12 837 claim to FHIR R4 Claim resource
 * Supports Professional (837P), Institutional (837I), and Dental (837D) claims
 * 
 * @param input X12 837 claim data
 * @returns FHIR R4 Claim resource conforming to US Core
 */
export function mapX12837ToFhirClaim(input: X12_837_Claim): Claim {
  const claim: Claim = {
    resourceType: 'Claim',
    id: input.claimId,
    status: 'active',
    type: mapClaimType(input.claimType),
    use: 'claim',
    
    // Patient reference
    patient: {
      reference: `Patient/${input.patient.memberId}`,
      display: `${input.patient.firstName} ${input.patient.lastName}`
    },
    
    // Billable period
    billablePeriod: input.statementDates ? {
      start: formatDate(input.statementDates.fromDate),
      end: input.statementDates.toDate ? formatDate(input.statementDates.toDate) : undefined
    } : undefined,
    
    // Created timestamp
    created: new Date().toISOString(),
    
    // Provider (billing provider)
    provider: {
      reference: `Organization/${input.billingProvider.npi}`,
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.billingProvider.npi
      },
      display: input.billingProvider.organizationName
    },
    
    // Priority (standard unless urgent)
    priority: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/processpriority',
        code: 'normal',
        display: 'Normal'
      }]
    },
    
    // Insurance coverage
    insurance: [{
      sequence: 1,
      focal: true,
      coverage: {
        reference: `Coverage/${input.patient.memberId}`,
        display: input.payer.payerName
      }
    }],
    
    // Diagnosis codes
    diagnosis: input.diagnosisCodes.map((diag) => ({
      sequence: diag.sequence,
      diagnosisCodeableConcept: {
        coding: [{
          system: 'http://hl7.org/fhir/sid/icd-10',
          code: diag.code
        }]
      },
      type: diag.type ? [{
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/ex-diagnosistype',
          code: diag.type === 'principal' ? 'principal' : diag.type === 'admitting' ? 'admitting' : 'other'
        }]
      }] : undefined
    })),
    
    // Service line items
    item: input.serviceLines.map(line => ({
      sequence: line.lineNumber,
      
      // Service/procedure code
      productOrService: {
        coding: [{
          system: 'http://www.ama-assn.org/go/cpt',
          code: line.procedureCode
        }],
        text: line.procedureCode
      },
      
      // Service date
      servicedDate: formatDate(line.serviceDate),
      
      // Quantity
      quantity: {
        value: line.units
      },
      
      // Unit price and net amount
      unitPrice: {
        value: line.units > 0 ? line.chargeAmount / line.units : line.chargeAmount,
        currency: 'USD'
      },
      
      net: {
        value: line.chargeAmount,
        currency: 'USD'
      },
      
      // Diagnosis pointers
      diagnosisSequence: line.diagnosisPointers,
      
      // Modifiers
      modifier: line.procedureModifiers?.map(mod => ({
        coding: [{
          system: 'http://www.ama-assn.org/go/cpt',
          code: mod
        }]
      })),
      
      // Place of service
      locationCodeableConcept: line.placeOfServiceCode ? {
        coding: [{
          system: 'https://www.cms.gov/Medicare/Coding/place-of-service-codes',
          code: line.placeOfServiceCode
        }]
      } : undefined,
      
      // Rendering provider (if specified)
      provider: line.renderingProviderNpi ? [{
        reference: `Practitioner/${line.renderingProviderNpi}`,
        identifier: {
          system: 'http://hl7.org/fhir/sid/us-npi',
          value: line.renderingProviderNpi
        }
      }] : undefined
    })),
    
    // Total claim amount
    total: {
      value: input.totalChargeAmount,
      currency: 'USD'
    },
    
    // Reference identifiers
    identifier: [{
      system: 'urn:oid:2.16.840.1.113883.3.8901.1',
      value: input.claimId
    }]
  };
  
  // Add prior authorization if present
  if (input.referenceNumbers?.priorAuthorizationNumber) {
    claim.identifier?.push({
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          code: 'PRIOR_AUTH'
        }]
      },
      value: input.referenceNumbers.priorAuthorizationNumber
    });
  }
  
  // Add facility for institutional claims
  if (input.claimType === 'I' && input.billTypeCode) {
    claim.facility = {
      reference: `Location/${input.billingProvider.npi}`,
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.billingProvider.npi
      }
    };
  }
  
  return claim;
}

// ============================================================================
// X12 835 → FHIR ExplanationOfBenefit Mapping
// ============================================================================

/**
 * Maps X12 835 remittance to FHIR R4 ExplanationOfBenefit
 * Represents claim adjudication and payment details
 * 
 * @param input X12 835 remittance advice
 * @returns FHIR R4 ExplanationOfBenefit resource
 */
export function mapX12835ToFhirEob(input: X12_835_Remittance): ExplanationOfBenefit {
  // Process first claim in remittance (typically one claim per EOB)
  const claim = input.claims[0];
  
  if (!claim) {
    throw new Error('X12 835 remittance must contain at least one claim');
  }
  
  const eob: ExplanationOfBenefit = {
    resourceType: 'ExplanationOfBenefit',
    id: claim.claimId,
    
    // Status: active for processed claims
    status: 'active',
    
    // Type: professional (default - can be adjusted based on claim type)
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: 'professional'
      }]
    },
    
    // Use: claim
    use: 'claim',
    
    // Patient
    patient: {
      reference: `Patient/${claim.patient.memberId}`,
      display: `${claim.patient.firstName} ${claim.patient.lastName}`
    },
    
    // Billable period
    billablePeriod: {
      start: formatDate(claim.claimDates.statementFromDate),
      end: claim.claimDates.statementToDate ? formatDate(claim.claimDates.statementToDate) : undefined
    },
    
    // Created timestamp
    created: formatDate(claim.claimDates.processedDate || claim.claimDates.receivedDate || claim.claimDates.statementFromDate),
    
    // Insurer (payer)
    insurer: {
      reference: `Organization/${input.payer.payerId}`,
      display: input.payer.payerName
    },
    
    // Provider (payee)
    provider: {
      reference: `Organization/${input.payee.npi}`,
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.payee.npi
      },
      display: input.payee.organizationName
    },
    
    // Outcome: complete processing
    outcome: 'complete',
    
    // Insurance coverage
    insurance: [{
      focal: true,
      coverage: {
        reference: `Coverage/${claim.patient.memberId}`
      }
    }],
    
    // Service line items
    item: claim.serviceLines.map(line => ({
      sequence: line.lineNumber,
      
      // Procedure code
      productOrService: {
        coding: [{
          system: 'http://www.ama-assn.org/go/cpt',
          code: line.procedureCode
        }]
      },
      
      // Service date
      servicedDate: formatDate(line.serviceDate),
      
      // Quantity
      quantity: {
        value: line.units
      },
      
      // Adjudication (amounts and adjustments)
      adjudication: [
        // Submitted amount
        {
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'submitted'
            }]
          },
          amount: {
            value: line.amounts.billedAmount,
            currency: 'USD'
          }
        },
        // Eligible amount
        ...(line.amounts.allowedAmount ? [{
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'eligible'
            }]
          },
          amount: {
            value: line.amounts.allowedAmount,
            currency: 'USD'
          }
        }] : []),
        // Benefit amount (paid)
        {
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'benefit'
            }]
          },
          amount: {
            value: line.amounts.paidAmount,
            currency: 'USD'
          }
        },
        // Deductible
        ...(line.amounts.deductible ? [{
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'deductible'
            }]
          },
          amount: {
            value: line.amounts.deductible,
            currency: 'USD'
          }
        }] : []),
        // Coinsurance
        ...(line.amounts.coinsurance ? [{
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'coinsurance'
            }]
          },
          amount: {
            value: line.amounts.coinsurance,
            currency: 'USD'
          }
        }] : []),
        // Copay
        ...(line.amounts.copay ? [{
          category: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/adjudication',
              code: 'copay'
            }]
          },
          amount: {
            value: line.amounts.copay,
            currency: 'USD'
          }
        }] : [])
      ]
    })),
    
    // Claim-level totals
    total: [
      {
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication',
            code: 'submitted'
          }]
        },
        amount: {
          value: claim.claimAmounts.billedAmount,
          currency: 'USD'
        }
      },
      ...(claim.claimAmounts.allowedAmount ? [{
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication',
            code: 'eligible'
          }]
        },
        amount: {
          value: claim.claimAmounts.allowedAmount,
          currency: 'USD'
        }
      }] : []),
      {
        category: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication',
            code: 'benefit'
          }]
        },
        amount: {
          value: claim.claimAmounts.paidAmount,
          currency: 'USD'
        }
      }
    ],
    
    // Payment information
    payment: {
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/ex-paymenttype',
          code: input.payment.paymentMethodCode === 'ACH' ? 'complete' : 'partial'
        }]
      },
      amount: {
        value: claim.claimAmounts.paidAmount,
        currency: 'USD'
      },
      date: formatDate(input.payment.paymentDate),
      identifier: {
        system: 'urn:oid:2.16.840.1.113883.3.8901.2',
        value: input.payment.checkOrEftNumber || input.payment.traceNumber || ''
      }
    }
  };
  
  // Add reference identifiers
  eob.identifier = [{
    system: 'urn:oid:2.16.840.1.113883.3.8901.1',
    value: claim.claimId
  }];
  
  if (claim.referenceNumbers?.payerClaimControlNumber) {
    eob.identifier.push({
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          code: 'PAYER_CLAIM'
        }]
      },
      value: claim.referenceNumbers.payerClaimControlNumber
    });
  }
  
  return eob;
}

// ============================================================================
// X12 278 → FHIR ServiceRequest Mapping
// ============================================================================

/**
 * Maps X12 278 prior authorization request to FHIR R4 ServiceRequest
 * Aligns with Da Vinci Prior Authorization Support (PAS) Implementation Guide
 * 
 * @param input X12 278 authorization request
 * @returns FHIR R4 ServiceRequest for prior authorization
 */
export function mapX12278ToFhirPriorAuth(input: X12_278_Request): ServiceRequest {
  const serviceRequest: ServiceRequest = {
    resourceType: 'ServiceRequest',
    id: input.transactionId,
    
    // Status: draft for new requests, active for renewals
    status: input.certificationType === 'R' ? 'active' : 'draft',
    
    // Intent: order for authorization requests
    intent: 'order',
    
    // Priority: urgent vs routine
    priority: input.levelOfService === 'U' ? 'urgent' : 'routine',
    
    // Subject (patient)
    subject: {
      reference: `Patient/${input.patient.memberId}`,
      display: `${input.patient.firstName} ${input.patient.lastName}`
    },
    
    // Requester (ordering provider)
    requester: {
      reference: `Practitioner/${input.requestingProvider.npi}`,
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.requestingProvider.npi
      },
      display: input.requestingProvider.organizationName || 
               `${input.requestingProvider.firstName} ${input.requestingProvider.lastName}`
    },
    
    // Performer (servicing provider if different)
    performer: input.servicingProvider ? [{
      reference: `Practitioner/${input.servicingProvider.npi}`,
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.servicingProvider.npi
      }
    }] : undefined,
    
    // Service being authorized (first service from requested services)
    code: input.requestedServices.length > 0 && input.requestedServices[0].procedureCode ? {
      coding: [{
        system: 'http://www.ama-assn.org/go/cpt',
        code: input.requestedServices[0].procedureCode
      }]
    } : undefined,
    
    // Occurrence period (service date range)
    occurrencePeriod: input.requestedServices.length > 0 && input.requestedServices[0].serviceDateRange ? {
      start: formatDate(input.requestedServices[0].serviceDateRange.startDate),
      end: input.requestedServices[0].serviceDateRange.endDate 
        ? formatDate(input.requestedServices[0].serviceDateRange.endDate) 
        : undefined
    } : undefined,
    
    // Authorization timestamp
    authoredOn: new Date().toISOString(),
    
    // Reason codes (diagnosis)
    reasonCode: input.diagnosisCodes?.map(diag => ({
      coding: [{
        system: 'http://hl7.org/fhir/sid/icd-10',
        code: diag.code
      }]
    }))
  };
  
  // Add quantity if specified
  if (input.requestedServices.length > 0 && input.requestedServices[0].quantity) {
    serviceRequest.quantityQuantity = {
      value: input.requestedServices[0].quantity,
      unit: input.requestedServices[0].measurementUnit || 'unit'
    };
  }
  
  // Add identifiers
  serviceRequest.identifier = [{
    system: 'urn:oid:2.16.840.1.113883.3.8901.3',
    value: input.transactionId
  }];
  
  // Add prior auth number if renewal
  if (input.referenceNumbers?.priorAuthorizationNumber) {
    serviceRequest.identifier.push({
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          code: 'PRIOR_AUTH'
        }]
      },
      value: input.referenceNumbers.priorAuthorizationNumber
    });
  }
  
  return serviceRequest;
}

// ============================================================================
// Batch Processing Functions
// ============================================================================

/**
 * Process multiple X12 835 claims to multiple EOB resources
 */
export function mapX12835ClaimsToEobs(input: X12_835_Remittance): ExplanationOfBenefit[] {
  return input.claims.map(claim => {
    const singleClaimRemittance: X12_835_Remittance = {
      ...input,
      claims: [claim]
    };
    return mapX12835ToFhirEob(singleClaimRemittance);
  });
}
