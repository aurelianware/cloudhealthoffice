/**
 * FHIR R4 Mapper for CMS-0057-F Compliance
 * 
 * Comprehensive X12 EDI to FHIR R4 mappings supporting:
 * - CMS-0057-F Prior Authorization Rule
 * - HL7 Da Vinci Implementation Guides (PDex, CRD, DTR, PAS)
 * - US Core Implementation Guide v3.1.1+
 * 
 * References:
 * - CMS-0057-F: Advancing Interoperability and Improving Prior Authorization Processes
 * - HL7 FHIR R4: v4.0.1
 * - Da Vinci PDex: Payer Data Exchange
 * - Da Vinci PAS: Prior Authorization Support
 * - US Core IG: v3.1.1+
 */

import { 
  Claim, 
  ExplanationOfBenefit, 
  ServiceRequest,
  CodeableConcept
} from 'fhir/r4';
import { X12_837_Claim, X12_278_Request, X12_835_Remittance } from './x12ClaimTypes';
import { redactPHI } from '../security/hipaaLogger';

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
    diagnosis: input.diagnosisCodes.map((diag, index) => ({
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
      system: 'urn:oid:2.16.840.1.113883.3.8901.1', // TODO: Configure with payer-specific OID namespace
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

/**
 * Maps X12 278 prior authorization request to FHIR R4 ServiceRequest
 * Aligns with Da Vinci PAS Implementation Guide
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
    
    // Authorization code/identifier
    identifier: [{
      system: 'urn:oid:2.16.840.1.113883.3.8901.2', // TODO: Configure with payer-specific OID namespace
      value: input.transactionId
    }],
    
    // Service category
    category: [{
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/service-category',
        code: mapServiceTypeToCategory(input.serviceTypeCode)
      }]
    }],
    
    // Authorization timing
    occurrencePeriod: input.requestedServices[0]?.serviceDateRange ? {
      start: formatDate(input.requestedServices[0].serviceDateRange.startDate),
      end: input.requestedServices[0].serviceDateRange.endDate ? 
           formatDate(input.requestedServices[0].serviceDateRange.endDate) : undefined
    } : undefined,
    
    // Authored on
    authoredOn: new Date().toISOString(),
    
    // Reason codes (diagnoses)
    reasonCode: input.diagnosisCodes?.map(diag => ({
      coding: [{
        system: 'http://hl7.org/fhir/sid/icd-10',
        code: diag.code
      }]
    })),
    
    // Insurance
    insurance: [{
      reference: `Coverage/${input.patient.memberId}`,
      display: input.payer.payerName
    }]
  };
  
  // Add requested services as orderDetail
  if (input.requestedServices.length > 0) {
    serviceRequest.orderDetail = input.requestedServices.map(service => ({
      coding: service.procedureCode ? [{
        system: 'http://www.ama-assn.org/go/cpt',
        code: service.procedureCode
      }] : []
    }));
    
    // Add quantity for first service
    if (input.requestedServices[0].quantity) {
      serviceRequest.quantityQuantity = {
        value: input.requestedServices[0].quantity,
        unit: input.requestedServices[0].measurementUnit || 'unit'
      };
    }
  }
  
  // Add prior auth number if renewal
  if (input.referenceNumbers?.priorAuthorizationNumber) {
    serviceRequest.identifier?.push({
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

/**
 * Maps X12 835 remittance to FHIR R4 ExplanationOfBenefit
 * Represents claim adjudication and payment details
 * 
 * @param input X12 835 remittance advice
 * @returns Array of FHIR R4 ExplanationOfBenefit resources (one per claim)
 */
export function mapX12835ToFhirEOB(input: X12_835_Remittance): ExplanationOfBenefit[] {
  return input.claims.map(claim => {
    const eob: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      id: claim.claimId,
      
      // Status: active for processed claims
      status: 'active',
      
      // Type: claim
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
          }] : [])
        ]
      })),
      
      // Total amounts
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
            code: input.payment.paymentMethodCode === 'ACH' ? 'electronic' : 'check'
          }]
        },
        amount: {
          value: claim.claimAmounts.paidAmount,
          currency: 'USD'
        },
        date: formatDate(input.payment.paymentDate),
        identifier: input.payment.checkOrEftNumber ? {
          value: input.payment.checkOrEftNumber
        } : undefined
      },
      
      // Identifiers
      identifier: [
        {
          system: 'urn:oid:2.16.840.1.113883.3.8901.3', // TODO: Configure with payer-specific OID namespace
          value: claim.claimId
        },
        ...(claim.referenceNumbers?.payerClaimControlNumber ? [{
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
              code: 'PAYER_CLAIM'
            }]
          },
          value: claim.referenceNumbers.payerClaimControlNumber
        }] : [])
      ]
    };
    
    return eob;
  });
}

/**
 * Helper: Map claim type to FHIR CodeableConcept
 */
function mapClaimType(claimType: 'P' | 'I' | 'D'): CodeableConcept {
  const typeMap: Record<string, { code: string; display: string }> = {
    'P': { code: 'professional', display: 'Professional' },
    'I': { code: 'institutional', display: 'Institutional' },
    'D': { code: 'oral', display: 'Dental' }
  };
  
  const type = typeMap[claimType] || typeMap['P'];
  
  return {
    coding: [{
      system: 'http://terminology.hl7.org/CodeSystem/claim-type',
      code: type.code,
      display: type.display
    }]
  };
}

/**
 * Helper: Map service type code to FHIR category
 */
function mapServiceTypeToCategory(serviceTypeCode: string): string {
  // Map common X12 service type codes to FHIR categories
  const categoryMap: Record<string, string> = {
    '1': 'medical-care',
    '2': 'surgical',
    '3': 'consultation',
    '4': 'diagnostic-xray',
    '5': 'diagnostic-lab',
    '6': 'radiation-therapy',
    '30': 'health-benefit-plan-coverage',
    '33': 'chiropractic',
    '35': 'dental-care',
    '47': 'hospital-inpatient',
    '48': 'hospital-outpatient',
    '50': 'hospital-emergency',
    '86': 'emergency-services',
    '88': 'pharmacy'
  };
  
  return categoryMap[serviceTypeCode] || 'other';
}

/**
 * Helper: Format date from X12 format (CCYYMMDD or YYYY-MM-DD) to FHIR format (YYYY-MM-DD)
 */
function formatDate(date: string): string {
  if (!date) return '';
  
  // Already in YYYY-MM-DD format
  if (date.includes('-')) {
    return date;
  }
  
  // Convert CCYYMMDD to YYYY-MM-DD
  if (date.length === 8) {
    return `${date.substring(0, 4)}-${date.substring(4, 6)}-${date.substring(6, 8)}`;
  }
  
  // Invalid format - log warning and return empty string
  console.warn(`[Cloud Health Office] Invalid date format received: "${redactPHI(date)}". Expected CCYYMMDD or YYYY-MM-DD.`);
  return '';
}
