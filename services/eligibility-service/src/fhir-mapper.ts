/**
 * Cloud Health Office - FHIR Eligibility Mapper
 * 
 * Handles conversion between FHIR R4 CoverageEligibilityRequest/Response
 * and X12 270/271 formats for interoperability.
 * 
 * Supports:
 * - CMS Interoperability and Patient Access final rule (CMS-9115-F)
 * - Da Vinci Payer Data Exchange (PDex) Implementation Guide
 * - HL7 FHIR R4 Specification (v4.0.1)
 */

import { 
  CoverageEligibilityRequest, 
  CoverageEligibilityResponse,
  Patient,
  Coverage,
  Organization,
  Identifier,
  CodeableConcept,
  Reference,
  Period
} from 'fhir/r4';
import { v4 as uuidv4 } from 'uuid';
import { 
  X12_270_Request, 
  X12_271_Response,
  EligibilityBenefit 
} from './types';

/**
 * FHIR benefit category to X12 service type code mapping
 */
const BENEFIT_CATEGORY_TO_X12: Record<string, string> = {
  '1': '1',     // Medical Care
  '2': '2',     // Surgical
  '3': '3',     // Consultation
  '4': '4',     // Diagnostic X-Ray
  '5': '5',     // Diagnostic Lab
  '30': '30',   // Health Benefit Plan Coverage
  '33': '33',   // Chiropractic
  '35': '35',   // Dental Care
  '47': '47',   // Hospital
  '48': '48',   // Hospital Inpatient
  '49': '49',   // Hospital Outpatient
  '85': '85',   // Emergency Services
  '87': '87',   // Pharmacy
  'UC': 'UC',   // Urgent Care
  'MH': 'MH',   // Mental Health
  'PT': 'PT',   // Physical Therapy
  'vision': 'AK',
  'dental': '35',
  'pharmacy': '87',
  'mental-health': 'MH',
  'medical': '30',
  'emergency': '85'
};

/**
 * X12 service type code to FHIR benefit category mapping
 */
const X12_TO_BENEFIT_CATEGORY: Record<string, { code: string; display: string; system: string }> = {
  '1': { code: '1', display: 'Medical Care', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '2': { code: '2', display: 'Surgical', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '30': { code: '30', display: 'Health Benefit Plan Coverage', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '33': { code: '33', display: 'Chiropractic', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '35': { code: 'dental', display: 'Dental', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '47': { code: '47', display: 'Hospital', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '48': { code: '48', display: 'Hospital - Inpatient', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '49': { code: '49', display: 'Hospital - Outpatient', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '85': { code: 'emergency', display: 'Emergency Services', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  '87': { code: 'pharmacy', display: 'Pharmacy', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  'AK': { code: 'vision', display: 'Vision', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  'MH': { code: 'mental-health', display: 'Mental Health', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  'PT': { code: 'PT', display: 'Physical Therapy', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' },
  'UC': { code: 'UC', display: 'Urgent Care', system: 'http://terminology.hl7.org/CodeSystem/ex-benefitcategory' }
};

/**
 * FHIR Eligibility Mapper class
 */
export class FHIREligibilityMapper {
  
  /**
   * Convert FHIR CoverageEligibilityRequest to X12 270 Request
   */
  fhirToX12(fhirRequest: CoverageEligibilityRequest): X12_270_Request {
    const transactionControlNumber = uuidv4().substring(0, 9);
    const transactionDate = this.formatDateForX12(fhirRequest.created || new Date().toISOString());
    
    // Extract patient/subscriber info
    const patientId = this.extractReference(fhirRequest.patient);
    const insurerId = this.extractReference(fhirRequest.insurer);
    const providerId = fhirRequest.provider ? this.extractReference(fhirRequest.provider) : undefined;
    
    // Extract member identifier
    const memberIdentifier = fhirRequest.patient?.identifier;
    const memberId = memberIdentifier?.value || patientId;
    
    // Extract service types from items
    const serviceTypeCodes = this.extractServiceTypeCodes(fhirRequest);
    
    // Extract date range
    let eligibilityDateRange;
    if (fhirRequest.servicedPeriod) {
      eligibilityDateRange = {
        startDate: this.formatDateForX12(fhirRequest.servicedPeriod.start || ''),
        endDate: fhirRequest.servicedPeriod.end ? this.formatDateForX12(fhirRequest.servicedPeriod.end) : undefined
      };
    } else if (fhirRequest.servicedDate) {
      eligibilityDateRange = {
        startDate: this.formatDateForX12(fhirRequest.servicedDate)
      };
    }
    
    return {
      transactionControlNumber,
      interchangeControlNumber: transactionControlNumber,
      transactionDate,
      informationSource: {
        entityIdentifier: 'PR',
        entityType: '2',
        name: this.extractDisplayName(fhirRequest.insurer) || 'Health Plan',
        identificationCode: insurerId,
        identificationCodeQualifier: 'PI'
      },
      informationReceiver: providerId ? {
        entityIdentifier: '1P',
        entityType: '1',
        npi: providerId,
        identificationCode: providerId,
        identificationCodeQualifier: 'XX'
      } : {
        entityIdentifier: '1P',
        entityType: '1'
      },
      subscriber: {
        memberId,
        firstName: '',  // Would be populated from Patient resource
        lastName: '',
        dateOfBirth: ''
      },
      eligibilityDateRange,
      serviceTypeCodes: serviceTypeCodes.length > 0 ? serviceTypeCodes : ['30']
    };
  }
  
  /**
   * Convert X12 271 Response to FHIR CoverageEligibilityResponse
   */
  x12ToFhir(x12Response: X12_271_Response, originalRequest: CoverageEligibilityRequest): CoverageEligibilityResponse {
    const responseId = uuidv4();
    
    // Determine disposition based on eligibility status
    let disposition = 'Complete';
    let outcome: 'complete' | 'error' | 'partial' | 'queued' = 'complete';
    
    if (x12Response.errors && x12Response.errors.length > 0) {
      outcome = 'error';
      disposition = x12Response.errors.map(e => e.errorDescription).join('; ');
    }
    
    // Build insurance array with benefit details
    const insurance: CoverageEligibilityResponse['insurance'] = [{
      coverage: {
        reference: 'Coverage/' + x12Response.subscriber.memberId
      },
      inforce: x12Response.eligibilityStatus === 'active',
      item: this.convertBenefitsToItems(x12Response.benefits)
    }];
    
    // Build the response
    const response: CoverageEligibilityResponse = {
      resourceType: 'CoverageEligibilityResponse',
      id: responseId,
      meta: {
        profile: ['http://hl7.org/fhir/StructureDefinition/CoverageEligibilityResponse']
      },
      status: 'active',
      purpose: originalRequest.purpose,
      patient: originalRequest.patient,
      created: new Date().toISOString(),
      request: {
        reference: `CoverageEligibilityRequest/${originalRequest.id}`
      },
      outcome,
      disposition,
      insurer: originalRequest.insurer,
      insurance
    };
    
    // Add serviced period if present in original request
    if (originalRequest.servicedPeriod) {
      response.servicedPeriod = originalRequest.servicedPeriod;
    } else if (originalRequest.servicedDate) {
      response.servicedDate = originalRequest.servicedDate;
    }
    
    // Add errors if any
    if (x12Response.errors && x12Response.errors.length > 0) {
      response.error = x12Response.errors.map(err => ({
        code: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/adjudication-error',
            code: err.errorCode,
            display: err.errorDescription
          }]
        }
      }));
    }
    
    return response;
  }
  
  /**
   * Create a FHIR CoverageEligibilityRequest from component parts
   */
  createCoverageEligibilityRequest(params: {
    patientId: string;
    insurerId: string;
    providerId?: string;
    servicedDate?: string;
    servicedPeriod?: { start: string; end?: string };
    serviceTypeCodes?: string[];
    purpose?: ('validation' | 'benefits' | 'discovery' | 'auth-requirements')[];
  }): CoverageEligibilityRequest {
    const request: CoverageEligibilityRequest = {
      resourceType: 'CoverageEligibilityRequest',
      id: uuidv4(),
      meta: {
        profile: ['http://hl7.org/fhir/StructureDefinition/CoverageEligibilityRequest']
      },
      status: 'active',
      purpose: params.purpose || ['validation', 'benefits'],
      patient: {
        reference: `Patient/${params.patientId}`,
        identifier: {
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          value: params.patientId
        }
      },
      created: new Date().toISOString(),
      insurer: {
        reference: `Organization/${params.insurerId}`,
        identifier: {
          system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
          value: params.insurerId
        }
      }
    };
    
    // Add provider if specified
    if (params.providerId) {
      request.provider = {
        reference: `Practitioner/${params.providerId}`,
        identifier: {
          system: 'http://hl7.org/fhir/sid/us-npi',
          value: params.providerId
        }
      };
    }
    
    // Add serviced date/period
    if (params.servicedPeriod) {
      request.servicedPeriod = {
        start: params.servicedPeriod.start,
        end: params.servicedPeriod.end
      };
    } else if (params.servicedDate) {
      request.servicedDate = params.servicedDate;
    }
    
    // Add service type items
    if (params.serviceTypeCodes && params.serviceTypeCodes.length > 0) {
      request.item = params.serviceTypeCodes.map(code => ({
        category: {
          coding: [{
            system: 'https://x12.org/codes/service-type-codes',
            code,
            display: this.getServiceTypeDisplay(code)
          }]
        }
      }));
    }
    
    return request;
  }
  
  /**
   * Create a Patient resource from X12 subscriber data
   */
  createPatientFromX12(subscriberData: X12_270_Request['subscriber']): Patient {
    const patient: Patient = {
      resourceType: 'Patient',
      id: subscriberData.memberId,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
      },
      identifier: [
        {
          use: 'official',
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
              code: 'MB',
              display: 'Member Number'
            }]
          },
          value: subscriberData.memberId
        }
      ],
      active: true,
      name: [{
        use: 'official',
        family: subscriberData.lastName,
        given: subscriberData.middleName 
          ? [subscriberData.firstName, subscriberData.middleName]
          : [subscriberData.firstName]
      }],
      gender: this.mapGender(subscriberData.gender),
      birthDate: this.formatDateForFHIR(subscriberData.dateOfBirth)
    };
    
    // Add SSN if available
    if (subscriberData.ssn) {
      patient.identifier!.push({
        use: 'official',
        type: {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
            code: 'SS',
            display: 'Social Security Number'
          }]
        },
        system: 'http://hl7.org/fhir/sid/us-ssn',
        value: subscriberData.ssn
      });
    }
    
    // Add address if available
    if (subscriberData.address) {
      patient.address = [{
        use: 'home',
        type: 'physical',
        line: [subscriberData.address.line1, subscriberData.address.line2].filter(Boolean) as string[],
        city: subscriberData.address.city,
        state: subscriberData.address.state,
        postalCode: subscriberData.address.postalCode,
        country: subscriberData.address.countryCode || 'US'
      }];
    }
    
    return patient;
  }
  
  // ============================================================================
  // Private Helper Methods
  // ============================================================================
  
  private extractReference(ref?: Reference): string {
    if (!ref) return '';
    if (ref.identifier?.value) {
      return ref.identifier.value;
    }
    if (ref.reference) {
      const parts = ref.reference.split('/');
      return parts[parts.length - 1];
    }
    return '';
  }
  
  private extractDisplayName(ref?: Reference): string | undefined {
    return ref?.display;
  }
  
  private extractServiceTypeCodes(request: CoverageEligibilityRequest): string[] {
    const codes: string[] = [];
    
    if (request.item) {
      for (const item of request.item) {
        if (item.category?.coding) {
          for (const coding of item.category.coding) {
            if (coding.code) {
              // Convert FHIR benefit category to X12 service type if needed
              const x12Code = BENEFIT_CATEGORY_TO_X12[coding.code] || coding.code;
              codes.push(x12Code);
            }
          }
        }
      }
    }
    
    return codes;
  }
  
  private convertBenefitsToItems(benefits: EligibilityBenefit[]): NonNullable<NonNullable<CoverageEligibilityResponse['insurance']>[0]['item']> {
    return benefits.map(benefit => {
      const categoryMapping = X12_TO_BENEFIT_CATEGORY[benefit.serviceTypeCode] || {
        code: benefit.serviceTypeCode,
        display: benefit.serviceTypeDescription || `Service Type ${benefit.serviceTypeCode}`,
        system: 'https://x12.org/codes/service-type-codes'
      };
      
      const item: NonNullable<NonNullable<CoverageEligibilityResponse['insurance']>[0]['item']>[0] = {
        category: {
          coding: [{
            system: categoryMapping.system,
            code: categoryMapping.code,
            display: categoryMapping.display
          }]
        },
        excluded: benefit.eligibilityInfoCode === 'I' || benefit.eligibilityInfoCode === '6'
      };
      
      // Add authorization required extension
      if (benefit.authorizationRequired !== undefined) {
        item.authorizationRequired = benefit.authorizationRequired;
      }
      
      // Add network indicator
      if (benefit.inNetwork !== undefined) {
        item.network = {
          coding: [{
            system: 'http://terminology.hl7.org/CodeSystem/benefit-network',
            code: benefit.inNetwork ? 'in' : 'out',
            display: benefit.inNetwork ? 'In Network' : 'Out of Network'
          }]
        };
      }
      
      // Add benefit information
      const benefitItems: NonNullable<typeof item.benefit> = [];
      
      if (benefit.additionalInfo?.copay !== undefined) {
        benefitItems.push({
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/benefit-type',
              code: 'copay',
              display: 'Copay'
            }]
          },
          allowedMoney: {
            value: benefit.additionalInfo.copay,
            currency: 'USD'
          }
        });
      }
      
      if (benefit.additionalInfo?.coinsurance !== undefined) {
        benefitItems.push({
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/benefit-type',
              code: 'coinsurance',
              display: 'Coinsurance'
            }]
          },
          allowedUnsignedInt: benefit.additionalInfo.coinsurance
        });
      }
      
      if (benefit.additionalInfo?.deductible !== undefined) {
        benefitItems.push({
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/benefit-type',
              code: 'deductible',
              display: 'Deductible'
            }]
          },
          allowedMoney: {
            value: benefit.additionalInfo.deductible,
            currency: 'USD'
          },
          usedMoney: benefit.additionalInfo.deductibleRemaining !== undefined ? {
            value: benefit.additionalInfo.deductible - benefit.additionalInfo.deductibleRemaining,
            currency: 'USD'
          } : undefined
        });
      }
      
      if (benefit.additionalInfo?.outOfPocketMax !== undefined) {
        benefitItems.push({
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/benefit-type',
              code: 'out-of-pocket-maximum',
              display: 'Out of Pocket Maximum'
            }]
          },
          allowedMoney: {
            value: benefit.additionalInfo.outOfPocketMax,
            currency: 'USD'
          },
          usedMoney: benefit.additionalInfo.outOfPocketRemaining !== undefined ? {
            value: benefit.additionalInfo.outOfPocketMax - benefit.additionalInfo.outOfPocketRemaining,
            currency: 'USD'
          } : undefined
        });
      }
      
      if (benefitItems.length > 0) {
        item.benefit = benefitItems;
      }
      
      return item;
    });
  }
  
  private formatDateForX12(isoDate: string): string {
    if (!isoDate) return '';
    // Convert YYYY-MM-DD to CCYYMMDD
    const date = isoDate.split('T')[0];
    return date.replace(/-/g, '');
  }
  
  private formatDateForFHIR(x12Date: string): string {
    if (!x12Date) return '';
    // If already in YYYY-MM-DD format, return as-is
    if (x12Date.includes('-')) return x12Date.split('T')[0];
    // Convert CCYYMMDD to YYYY-MM-DD
    if (x12Date.length === 8) {
      return `${x12Date.substring(0, 4)}-${x12Date.substring(4, 6)}-${x12Date.substring(6, 8)}`;
    }
    return x12Date;
  }
  
  private mapGender(gender?: 'M' | 'F' | 'U'): Patient['gender'] {
    switch (gender) {
      case 'M': return 'male';
      case 'F': return 'female';
      case 'U': return 'unknown';
      default: return 'unknown';
    }
  }
  
  private getServiceTypeDisplay(code: string): string {
    const descriptions: Record<string, string> = {
      '1': 'Medical Care',
      '30': 'Health Benefit Plan Coverage',
      '33': 'Chiropractic',
      '35': 'Dental Care',
      '47': 'Hospital',
      '48': 'Hospital - Inpatient',
      '49': 'Hospital - Outpatient',
      '85': 'Emergency Services',
      '87': 'Pharmacy',
      'AK': 'Vision',
      'MH': 'Mental Health',
      'PT': 'Physical Therapy',
      'UC': 'Urgent Care'
    };
    return descriptions[code] || `Service Type ${code}`;
  }
}
