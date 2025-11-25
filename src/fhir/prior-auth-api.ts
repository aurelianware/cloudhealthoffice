/**
 * Prior Authorization API - CMS-0057-F Compliance
 * 
 * Comprehensive implementation of CMS Prior Authorization Rule (CMS-0057-F)
 * supporting Da Vinci Implementation Guides:
 * - CRD (Coverage Requirements Discovery) 
 * - DTR (Documentation Templates and Rules)
 * - PAS (Prior Authorization Support)
 * 
 * Features:
 * - FHIR R4 PriorAuthorizationRequest/Response resources
 * - X12 278 request/response mapping
 * - 72-hour SLA decision tracking
 * - Attachment support via Binary resources
 * - CDS Hooks for provider-facing requirements discovery
 * - Azure Logic Apps orchestration
 * - Availity clearinghouse integration
 * - Patient consent management
 * 
 * References:
 * - CMS-0057-F: Prior Authorization Rule (March 2023)
 * - Da Vinci CRD IG: http://hl7.org/fhir/us/davinci-crd/
 * - Da Vinci DTR IG: http://hl7.org/fhir/us/davinci-dtr/
 * - Da Vinci PAS IG: http://hl7.org/fhir/us/davinci-pas/
 * - X12 278: Health Care Services Review (005010X217)
 */

import {
  Bundle,
  Claim,
  ClaimResponse,
  Coding,
  CodeableConcept,
  Reference,
  Extension,
  Identifier,
  Patient,
  Practitioner,
  Organization,
  ServiceRequest,
  DocumentReference,
  Binary,
  Consent,
  Appointment,
  Task
} from 'fhir/r4';

/**
 * X12 278 Request structure for prior authorization
 * Based on HIPAA 005010X217 implementation guide
 */
export interface X12_278_Request {
  /** Transaction Set Control Number */
  transactionSetControlNumber: string;
  /** Request category: AR=Admission Review, HS=Health Services, SC=Specialty Care */
  requestCategory: 'AR' | 'HS' | 'SC';
  /** Certification type: I=Initial, S=Renewal, 3=Cancel */
  certificationType: 'I' | 'S' | '3';
  
  /** Patient/Subscriber information */
  patient: {
    memberId: string;
    firstName: string;
    lastName: string;
    middleName?: string;
    dateOfBirth: string;
    gender: 'M' | 'F' | 'U';
    relationship?: string;
  };
  
  /** Subscriber information (if different from patient) */
  subscriber?: {
    memberId: string;
    firstName: string;
    lastName: string;
    dateOfBirth: string;
    gender: 'M' | 'F' | 'U';
  };
  
  /** Requesting provider */
  requestingProvider: {
    npi: string;
    firstName?: string;
    lastName?: string;
    organizationName?: string;
    taxId?: string;
  };
  
  /** Service provider (if different from requesting) */
  serviceProvider?: {
    npi: string;
    organizationName?: string;
  };
  
  /** Services being requested */
  services: Array<{
    serviceTypeCode: string;
    placeOfService?: string;
    procedureCode?: string;
    procedureCodeType?: 'CPT' | 'HCPCS' | 'ICD10';
    quantity?: number;
    quantityType?: 'UN' | 'DY' | 'VS' | 'MJ';
    fromDate: string;
    toDate?: string;
  }>;
  
  /** Diagnosis codes */
  diagnoses: Array<{
    code: string;
    codeType: 'ABK' | 'ABF'; // ABK=ICD-10-CM, ABF=ICD-9-CM
    qualifier?: 'ABK' | 'ABF';
  }>;
  
  /** Additional information */
  additionalInfo?: {
    admissionDate?: string;
    dischargeDate?: string;
    admissionType?: string;
    levelOfService?: string;
  };
  
  /** Authorization number (for renewals/cancellations) */
  authorizationNumber?: string;
  
  /** Payer/Health Plan ID */
  payerId: string;
  
  /** Trading partner ID (e.g., Availity) */
  tradingPartnerId?: string;
}

/**
 * X12 278 Response structure
 */
export interface X12_278_Response {
  /** Transaction Set Control Number */
  transactionSetControlNumber: string;
  
  /** Status: A1=Approved, A2=Modified, A3=Denied, A4=Pended, A6=Modified */
  statusCode: 'A1' | 'A2' | 'A3' | 'A4' | 'A6';
  
  /** Authorization/certification number */
  authorizationNumber?: string;
  
  /** Effective dates */
  effectiveDate?: string;
  expirationDate?: string;
  
  /** Approved services (if modified) */
  approvedServices?: Array<{
    serviceTypeCode: string;
    quantity?: number;
    fromDate?: string;
    toDate?: string;
  }>;
  
  /** Denial/pend reasons */
  reasons?: Array<{
    code: string;
    description?: string;
  }>;
  
  /** Follow-up actions required */
  followUpActions?: Array<{
    actionCode: string;
    description?: string;
  }>;
  
  /** Additional notes */
  notes?: string[];
}

/**
 * FHIR R4 Prior Authorization Request
 * Based on Da Vinci PAS IG profile
 */
export interface PriorAuthorizationRequest {
  resourceType: 'Claim';
  id?: string;
  meta?: {
    profile: string[];
  };
  
  /** Status: active | cancelled | draft */
  status: 'active' | 'cancelled' | 'draft';
  
  /** Type: professional | institutional */
  type: CodeableConcept;
  
  /** Use: preauthorization */
  use: 'preauthorization';
  
  /** Patient reference */
  patient: Reference;
  
  /** Created timestamp */
  created: string;
  
  /** Insurer/Payer */
  insurer: Reference;
  
  /** Provider (requesting) */
  provider: Reference;
  
  /** Priority: stat | normal | deferred */
  priority: CodeableConcept;
  
  /** Prescription reference (if applicable) */
  prescription?: Reference;
  
  /** Diagnosis */
  diagnosis: Array<{
    sequence: number;
    diagnosisCodeableConcept: CodeableConcept;
    type?: CodeableConcept[];
  }>;
  
  /** Items being requested */
  item: Array<{
    sequence: number;
    productOrService: CodeableConcept;
    servicedDate?: string;
    servicedPeriod?: {
      start?: string;
      end?: string;
    };
    locationCodeableConcept?: CodeableConcept;
    quantity?: {
      value: number;
      unit?: string;
    };
  }>;
  
  /** Supporting information */
  supportingInfo?: Array<{
    sequence: number;
    category: CodeableConcept;
    valueReference?: Reference;
    valueAttachment?: {
      contentType: string;
      data?: string;
      url?: string;
    };
  }>;
}

/**
 * Prior Authorization Response (ClaimResponse)
 */
export interface PriorAuthorizationResponse extends ClaimResponse {
  meta?: {
    profile: string[];
  };
  
  /** Outcome: queued | complete | error | partial */
  outcome: 'queued' | 'complete' | 'error' | 'partial';
  
  /** Disposition text */
  disposition?: string;
  
  /** Pre-auth reference */
  preAuthRef?: string;
  
  /** Pre-auth period */
  preAuthPeriod?: {
    start?: string;
    end?: string;
  };
  
  /** Item adjudication */
  item?: Array<{
    itemSequence: number;
    adjudication: Array<{
      category: CodeableConcept;
      reason?: CodeableConcept;
    }>;
  }>;
  
  /** Errors */
  error?: Array<{
    code: CodeableConcept;
    detailSequence?: number;
  }>;
}

/**
 * Da Vinci CRD Card (Coverage Requirements Discovery)
 * CDS Hooks Card structure for provider-facing requirements
 */
export interface CRDCard {
  /** UUID for the card */
  uuid?: string;
  
  /** Summary text */
  summary: string;
  
  /** Detail text */
  detail?: string;
  
  /** Indicator: info | warning | critical */
  indicator: 'info' | 'warning' | 'critical';
  
  /** Source */
  source: {
    label: string;
    url?: string;
    icon?: string;
  };
  
  /** Suggestions */
  suggestions?: Array<{
    label: string;
    uuid?: string;
    actions?: Array<{
      type: 'create' | 'update' | 'delete';
      description: string;
      resource?: any;
    }>;
  }>;
  
  /** Links */
  links?: Array<{
    label: string;
    url: string;
    type: 'absolute' | 'smart';
    appContext?: string;
  }>;
}

/**
 * Da Vinci DTR Questionnaire Response
 * Documentation Templates and Rules
 */
export interface DTRQuestionnaireResponse {
  resourceType: 'QuestionnaireResponse';
  id?: string;
  meta?: {
    profile: string[];
  };
  
  /** Questionnaire reference */
  questionnaire: string;
  
  /** Status: in-progress | completed | amended */
  status: 'in-progress' | 'completed' | 'amended';
  
  /** Subject (patient) */
  subject?: Reference;
  
  /** Authored date */
  authored?: string;
  
  /** Author (practitioner) */
  author?: Reference;
  
  /** Items */
  item?: Array<{
    linkId: string;
    text?: string;
    answer?: Array<{
      valueString?: string;
      valueBoolean?: boolean;
      valueInteger?: number;
      valueDate?: string;
      valueCoding?: Coding;
      valueReference?: Reference;
    }>;
  }>;
}

/**
 * SLA (Service Level Agreement) tracking for decision timelines
 * CMS-0057-F requires specific timelines (72 hours for urgent, 7 days for standard)
 */
export interface PriorAuthSLA {
  /** Request ID */
  requestId: string;
  
  /** Request submission timestamp */
  submittedAt: string;
  
  /** Request type: urgent | standard | expedited */
  requestType: 'urgent' | 'standard' | 'expedited';
  
  /** Required decision timestamp (based on CMS rules) */
  decisionDueBy: string;
  
  /** Actual decision timestamp */
  decidedAt?: string;
  
  /** Current status */
  status: 'pending' | 'decided' | 'overdue' | 'extended';
  
  /** Extension granted (if any) */
  extensionGranted?: boolean;
  extensionReason?: string;
  extendedDueBy?: string;
  
  /** SLA compliance */
  slaCompliant?: boolean;
  
  /** Timeline in hours */
  standardTimelineHours: number;
  actualTimelineHours?: number;
}

/**
 * Attachment reference for prior authorization
 * Supports FHIR Binary resources
 */
export interface PriorAuthAttachment {
  /** Attachment ID */
  id: string;
  
  /** Type: clinical-notes | lab-results | imaging | other */
  type: 'clinical-notes' | 'lab-results' | 'imaging' | 'other';
  
  /** Content type (MIME) */
  contentType: string;
  
  /** Binary resource reference */
  binaryReference: Reference;
  
  /** Document reference (if structured) */
  documentReference?: Reference;
  
  /** Attachment date */
  attachedAt: string;
  
  /** Description */
  description?: string;
  
  /** Required indicator */
  required?: boolean;
}

/**
 * Patient consent for prior authorization
 */
export interface PriorAuthConsent {
  /** Consent resource */
  resourceType: 'Consent';
  id?: string;
  
  /** Status: active | inactive | entered-in-error */
  status: 'active' | 'inactive' | 'entered-in-error';
  
  /** Scope: patient-privacy | research | treatment */
  scope: CodeableConcept;
  
  /** Category: HIPAA authorization */
  category: CodeableConcept[];
  
  /** Patient */
  patient: Reference;
  
  /** Date/time consent given */
  dateTime?: string;
  
  /** Performer (who gave consent) */
  performer?: Reference[];
  
  /** Organization */
  organization?: Reference[];
  
  /** Provision details */
  provision?: {
    type: 'deny' | 'permit';
    period?: {
      start?: string;
      end?: string;
    };
    purpose?: Coding[];
    actor?: Array<{
      role: CodeableConcept;
      reference: Reference;
    }>;
  };
}

/**
 * Maps X12 278 request to FHIR R4 Claim (PriorAuthorizationRequest)
 * 
 * @param x12Request X12 278 request structure
 * @returns FHIR R4 Claim resource for prior authorization
 */
export function mapX12278ToFHIRPriorAuth(x12Request: X12_278_Request): PriorAuthorizationRequest {
  const claim: PriorAuthorizationRequest = {
    resourceType: 'Claim',
    meta: {
      profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim']
    },
    status: x12Request.certificationType === '3' ? 'cancelled' : 'active',
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: x12Request.requestCategory === 'AR' ? 'institutional' : 'professional',
        display: x12Request.requestCategory === 'AR' ? 'Institutional' : 'Professional'
      }]
    },
    use: 'preauthorization',
    patient: {
      reference: `Patient/${x12Request.patient.memberId}`,
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: x12Request.patient.memberId
      }
    },
    created: new Date().toISOString(),
    insurer: {
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: x12Request.payerId
      }
    },
    provider: {
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: x12Request.requestingProvider.npi
      }
    },
    priority: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/processpriority',
        code: x12Request.requestCategory === 'AR' ? 'stat' : 'normal',
        display: x12Request.requestCategory === 'AR' ? 'Immediate' : 'Normal'
      }]
    },
    diagnosis: x12Request.diagnoses.map((dx, index) => ({
      sequence: index + 1,
      diagnosisCodeableConcept: {
        coding: [{
          system: dx.codeType === 'ABK' ? 'http://hl7.org/fhir/sid/icd-10-cm' : 'http://hl7.org/fhir/sid/icd-9-cm',
          code: dx.code
        }]
      }
    })),
    item: x12Request.services.map((service, index) => ({
      sequence: index + 1,
      productOrService: {
        coding: [{
          system: getServiceCodeSystem(service.procedureCodeType),
          code: service.procedureCode || service.serviceTypeCode
        }]
      },
      servicedPeriod: {
        start: service.fromDate,
        end: service.toDate
      },
      locationCodeableConcept: service.placeOfService ? {
        coding: [{
          system: 'https://www.cms.gov/Medicare/Coding/place-of-service-codes/Place_of_Service_Code_Set',
          code: service.placeOfService
        }]
      } : undefined,
      quantity: service.quantity ? {
        value: service.quantity,
        unit: getQuantityUnit(service.quantityType)
      } : undefined
    }))
  };
  
  return claim;
}

/**
 * Maps FHIR R4 ClaimResponse to X12 278 response
 * 
 * @param claimResponse FHIR ClaimResponse
 * @returns X12 278 response structure
 */
export function mapFHIRToX12278Response(claimResponse: PriorAuthorizationResponse): X12_278_Response {
  // Map FHIR outcome to X12 status code
  let statusCode: 'A1' | 'A2' | 'A3' | 'A4' | 'A6' = 'A4';
  if (claimResponse.outcome === 'complete') {
    statusCode = 'A1'; // Approved
  } else if (claimResponse.outcome === 'partial') {
    statusCode = 'A2'; // Modified/Partial approval
  } else if (claimResponse.outcome === 'error') {
    statusCode = 'A3'; // Denied
  }
  
  const response: X12_278_Response = {
    transactionSetControlNumber: claimResponse.id || 'UNKNOWN',
    statusCode,
    authorizationNumber: claimResponse.preAuthRef,
    effectiveDate: claimResponse.preAuthPeriod?.start,
    expirationDate: claimResponse.preAuthPeriod?.end,
    reasons: claimResponse.error?.map(err => ({
      code: err.code.coding?.[0]?.code || 'UNKNOWN',
      description: err.code.coding?.[0]?.display
    }))
  };
  
  return response;
}

/**
 * Calculates SLA timeline based on CMS-0057-F requirements
 * 
 * CMS Requirements:
 * - Urgent/Expedited: 72 hours
 * - Standard: 7 calendar days (168 hours)
 * - Extensions: 14 additional calendar days with justification
 * 
 * @param requestType Request urgency type
 * @param submittedAt Submission timestamp
 * @returns SLA tracking object
 */
export function calculatePriorAuthSLA(
  requestType: 'urgent' | 'standard' | 'expedited',
  submittedAt: string
): PriorAuthSLA {
  const submittedDate = new Date(submittedAt);
  
  // Determine timeline hours based on request type
  let standardTimelineHours: number;
  switch (requestType) {
    case 'urgent':
    case 'expedited':
      standardTimelineHours = 72; // 72 hours for urgent
      break;
    case 'standard':
    default:
      standardTimelineHours = 168; // 7 calendar days
      break;
  }
  
  // Calculate decision due date
  const decisionDueBy = new Date(submittedDate.getTime() + (standardTimelineHours * 60 * 60 * 1000));
  
  const sla: PriorAuthSLA = {
    requestId: `PA-${Date.now()}`,
    submittedAt,
    requestType,
    decisionDueBy: decisionDueBy.toISOString(),
    status: 'pending',
    standardTimelineHours
  };
  
  return sla;
}

/**
 * Updates SLA when decision is made
 * 
 * @param sla Current SLA object
 * @param decidedAt Decision timestamp
 * @returns Updated SLA with compliance status
 */
export function updateSLAWithDecision(sla: PriorAuthSLA, decidedAt: string): PriorAuthSLA {
  const decidedDate = new Date(decidedAt);
  const submittedDate = new Date(sla.submittedAt);
  const dueDate = new Date(sla.decisionDueBy);
  
  // Calculate actual timeline in hours
  const actualTimelineHours = (decidedDate.getTime() - submittedDate.getTime()) / (60 * 60 * 1000);
  
  // Determine if SLA was met
  const slaCompliant = decidedDate <= dueDate;
  
  return {
    ...sla,
    decidedAt,
    actualTimelineHours,
    status: decidedDate > dueDate ? 'overdue' : 'decided',
    slaCompliant
  };
}

/**
 * Creates a Da Vinci CRD card for coverage requirements
 * 
 * @param requirementType Type of requirement
 * @param summary Summary text
 * @param detail Detail text
 * @returns CRD Card for CDS Hooks response
 */
export function createCRDCard(
  requirementType: 'prior-auth-required' | 'documentation-needed' | 'alternative-available',
  summary: string,
  detail?: string
): CRDCard {
  let indicator: 'info' | 'warning' | 'critical';
  
  switch (requirementType) {
    case 'prior-auth-required':
      indicator = 'warning';
      break;
    case 'documentation-needed':
      indicator = 'info';
      break;
    case 'alternative-available':
      indicator = 'info';
      break;
    default:
      indicator = 'info';
  }
  
  const card: CRDCard = {
    uuid: `crd-${Date.now()}`,
    summary,
    detail,
    indicator,
    source: {
      label: 'Cloud Health Office - Coverage Requirements',
      url: 'https://cloudhealthoffice.com',
      icon: 'https://cloudhealthoffice.com/logo.png'
    }
  };
  
  return card;
}

/**
 * Creates FHIR Binary resource for attachment
 * 
 * @param contentType MIME type
 * @param data Base64-encoded data
 * @returns FHIR Binary resource
 */
export function createAttachmentBinary(
  contentType: string,
  data: string
): Binary {
  const binary: Binary = {
    resourceType: 'Binary',
    contentType,
    data
  };
  
  return binary;
}

/**
 * Creates FHIR DocumentReference for structured attachment
 * 
 * @param binary Binary resource reference
 * @param patient Patient reference
 * @param type Document type
 * @returns FHIR DocumentReference
 */
export function createAttachmentDocumentReference(
  binary: Reference,
  patient: Reference,
  type: 'clinical-notes' | 'lab-results' | 'imaging' | 'other'
): DocumentReference {
  const docRef: DocumentReference = {
    resourceType: 'DocumentReference',
    status: 'current',
    type: {
      coding: [{
        system: 'http://loinc.org',
        code: getDocumentTypeCode(type)
      }]
    },
    subject: patient,
    date: new Date().toISOString(),
    content: [{
      attachment: {
        contentType: 'application/pdf',
        url: (() => {
          const ref = binary.reference;
          if (typeof ref === 'string') {
            const parts = ref.split('/');
            if (parts.length === 2 && parts[0] === 'Binary' && parts[1]) {
              return `Binary/${parts[1]}`;
            }
            // fallback: use the whole reference if it starts with Binary/
            if (ref.startsWith('Binary/')) {
              return ref;
            }
          }
          // fallback: unknown
          return 'Binary/unknown';
        })()
      }
    }]
  };
  
  return docRef;
}

/**
 * Creates patient consent for prior authorization
 * 
 * @param patient Patient reference
 * @param performer Who gave consent
 * @returns FHIR Consent resource
 */
export function createPriorAuthConsent(
  patient: Reference,
  performer?: Reference
): PriorAuthConsent {
  const consent: PriorAuthConsent = {
    resourceType: 'Consent',
    status: 'active',
    scope: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/consentscope',
        code: 'patient-privacy',
        display: 'Privacy Consent'
      }]
    },
    category: [{
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode',
        code: 'HIPAA-Auth',
        display: 'HIPAA Authorization'
      }]
    }],
    patient,
    dateTime: new Date().toISOString(),
    performer: performer ? [performer] : undefined,
    provision: {
      type: 'permit',
      period: {
        start: new Date().toISOString()
      },
      purpose: [{
        system: 'http://terminology.hl7.org/CodeSystem/v3-ActReason',
        code: 'TREAT',
        display: 'Treatment'
      }]
    }
  };
  
  return consent;
}

/**
 * Creates a CDS Hooks request for Da Vinci CRD
 * Provider-facing hook for querying coverage requirements
 * 
 * @param hookType Hook type (order-select, order-sign, etc.)
 * @param context Hook context with patient and order details
 * @returns CDS Hooks request payload
 */
export function createCDSHooksRequest(
  hookType: 'order-select' | 'order-sign' | 'appointment-book' | 'encounter-start',
  context: {
    userId: string;
    patientId: string;
    encounterId?: string;
    draftOrders?: Bundle;
    selections?: string[];
  }
): any {
  // Extract patient ID if it's already in the form "Patient/123"
  const patientId = context.patientId.startsWith('Patient/')
    ? context.patientId.substring('Patient/'.length)
    : context.patientId;
    
  return {
    hookInstance: `hook-${Date.now()}`,
    hook: hookType,
    fhirServer: 'https://fhir.cloudhealthoffice.com',
    context,
    prefetch: {
      patient: `Patient/${patientId}`,
      coverage: `Coverage?patient=${patientId}`
    }
  };
}

/**
 * Validates prior authorization request for completeness
 * 
 * @param request X12 278 or FHIR Claim
 * @returns Validation result with errors if any
 */
export function validatePriorAuthRequest(
  request: X12_278_Request | PriorAuthorizationRequest
): { valid: boolean; errors: string[] } {
  const errors: string[] = [];
  
  if ('transactionSetControlNumber' in request) {
    // X12 278 validation
    if (!request.patient.memberId) errors.push('Patient member ID is required');
    if (!request.requestingProvider.npi) errors.push('Requesting provider NPI is required');
    if (!request.services || request.services.length === 0) errors.push('At least one service is required');
    if (!request.diagnoses || request.diagnoses.length === 0) errors.push('At least one diagnosis is required');
  } else {
    // FHIR Claim validation
    if (!request.patient.reference) errors.push('Patient reference is required');
    if (!request.provider.reference && !request.provider.identifier) errors.push('Provider reference is required');
    if (!request.item || request.item.length === 0) errors.push('At least one item is required');
    if (!request.diagnosis || request.diagnosis.length === 0) errors.push('At least one diagnosis is required');
  }
  
  return {
    valid: errors.length === 0,
    errors
  };
}

// Helper functions

function getServiceCodeSystem(codeType?: 'CPT' | 'HCPCS' | 'ICD10'): string {
  switch (codeType) {
    case 'CPT':
      return 'http://www.ama-assn.org/go/cpt';
    case 'HCPCS':
      return 'http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets';
    case 'ICD10':
      return 'http://hl7.org/fhir/sid/icd-10-cm';
    default:
      return 'https://x12.org/codes/service-type-codes';
  }
}

function getQuantityUnit(quantityType?: 'UN' | 'DY' | 'VS' | 'MJ'): string {
  switch (quantityType) {
    case 'UN':
      return 'units';
    case 'DY':
      return 'days';
    case 'VS':
      return 'visits';
    case 'MJ':
      return 'minutes';
    default:
      return 'units';
  }
}

function getDocumentTypeCode(type: 'clinical-notes' | 'lab-results' | 'imaging' | 'other'): string {
  switch (type) {
    case 'clinical-notes':
      return '11506-3'; // Progress note
    case 'lab-results':
      return '11502-2'; // Laboratory report
    case 'imaging':
      return '18748-4'; // Diagnostic imaging study
    case 'other':
    default:
      return '34133-9'; // Summary of episode note
  }
}

/**
 * Orchestration configuration for Azure Logic Apps
 * Defines workflow endpoints and integration patterns
 */
export interface PriorAuthOrchestrationConfig {
  /** Logic App endpoints */
  endpoints: {
    /** Submit new prior authorization request */
    submitRequest: string;
    /** Check authorization status */
    checkStatus: string;
    /** Submit additional documentation */
    submitDocumentation: string;
    /** Cancel authorization */
    cancelAuthorization: string;
  };
  
  /** Service Bus configuration */
  serviceBus: {
    /** Topic for prior auth requests */
    requestTopic: string;
    /** Topic for prior auth responses */
    responseTopic: string;
    /** Topic for attachments */
    attachmentTopic: string;
  };
  
  /** Availity clearinghouse configuration */
  availity: {
    /** Trading partner ID */
    tradingPartnerId: string;
    /** SFTP endpoint for outbound 278 */
    sftpEndpoint: string;
    /** SFTP folder for outbound */
    outboundFolder: string;
    /** SFTP folder for inbound responses */
    inboundFolder: string;
  };
  
  /** SLA monitoring */
  slaMonitoring: {
    /** Enable SLA tracking */
    enabled: boolean;
    /** Alert threshold (hours before due) */
    alertThresholdHours: number;
    /** Notification endpoint */
    notificationEndpoint?: string;
  };
}

/**
 * Creates default orchestration configuration for Azure Logic Apps
 * 
 * @param baseUrl Base URL for Logic App endpoints
 * @param environment Environment name (dev, uat, prod)
 * @returns Orchestration configuration
 */
export function createOrchestrationConfig(
  baseUrl: string,
  environment: 'dev' | 'uat' | 'prod'
): PriorAuthOrchestrationConfig {
  return {
    endpoints: {
      submitRequest: `${baseUrl}/api/prior-auth/submit`,
      checkStatus: `${baseUrl}/api/prior-auth/status`,
      submitDocumentation: `${baseUrl}/api/prior-auth/documentation`,
      cancelAuthorization: `${baseUrl}/api/prior-auth/cancel`
    },
    serviceBus: {
      requestTopic: `prior-auth-requests-${environment}`,
      responseTopic: `prior-auth-responses-${environment}`,
      attachmentTopic: `prior-auth-attachments-${environment}`
    },
    availity: {
      tradingPartnerId: 'AVAILITY',
      sftpEndpoint: `sftp.availity.com`,
      outboundFolder: `/outbound/278`,
      inboundFolder: `/inbound/278`
    },
    slaMonitoring: {
      enabled: true,
      alertThresholdHours: 24
    }
  };
}
