/**
 * CMS-0057-F Prior Authorization API Implementation
 * 
 * FHIR R4-compliant prior authorization API supporting CMS-0057-F Final Rule (Effective 2027)
 * 
 * Key Features:
 * - FHIR R4 endpoints for prior authorization workflows
 * - Azure Logic Apps orchestration integration
 * - Da Vinci CRD (Coverage Requirements Discovery) IG support
 * - Da Vinci DTR (Documentation Templates & Rules) IG support
 * - Da Vinci PAS (Prior Authorization Support) IG support
 * - X12 278 request/response mapping to FHIR resources
 * - Real-time FHIR workflow with provider hooks
 * - Attachment support (Binary resource handling)
 * - Decision timeline logic (72-hour SLA compliance)
 * - Clearinghouse integration (Availity)
 * 
 * References:
 * - CMS-0057-F: Prior Authorization Final Rule
 * - Da Vinci CRD IG: http://hl7.org/fhir/us/davinci-crd/
 * - Da Vinci DTR IG: http://hl7.org/fhir/us/davinci-dtr/
 * - Da Vinci PAS IG: http://hl7.org/fhir/us/davinci-pas/
 * - US Core IG v3.1.1+: http://hl7.org/fhir/us/core/
 * - HL7 FHIR R4: http://hl7.org/fhir/R4/
 * - X12 278: Health Care Services Review Information (005010X217)
 * 
 * @module prior-auth-api
 */

import { randomUUID } from 'crypto';

import {
  Binary,
  Bundle,
  Claim,
  ClaimResponse,
  CodeableConcept,
  Coding,
  DocumentReference,
  Extension,
  Meta,
  Reference
} from 'fhir/r4';

/**
 * X12 278 Health Care Services Review structure
 * Based on HIPAA X12 005010X217 implementation guide
 */
export interface X12_278 {
  /** Transaction type: request, response, inquiry, cancel */
  transactionType: '11' | '13' | '30' | '33';
  
  /** Unique transaction identifier */
  transactionId: string;
  
  /** Interchange control number */
  interchangeControlNumber?: string;
  
  /** Transaction timestamp (ISO 8601) */
  timestamp?: string;
  
  /** Certification Type: 1=Initial, 2=Renewal, 3=Revised, 4=Cancel */
  certificationType: '1' | '2' | '3' | '4';
  
  /** Service Type Code (e.g., 48=Hospital Inpatient, 49=Hospital Outpatient) */
  serviceTypeCode: string;
  
  /** Level of Service: U=Urgent, E=Elective */
  levelOfService?: 'U' | 'E';
  
  /** Requester information (Provider) */
  requester: {
    npi: string;
    name?: string;
    organizationName?: string;
    taxId?: string;
    address?: {
      street1?: string;
      street2?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
    phone?: string;
    fax?: string;
  };
  
  /** Patient/Member information */
  patient: {
    memberId: string;
    firstName: string;
    lastName: string;
    middleName?: string;
    dob: string;
    gender?: 'M' | 'F' | 'U';
    ssn?: string;
    address?: {
      street1?: string;
      street2?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Subscriber (if different from patient) */
  subscriber?: {
    memberId: string;
    firstName: string;
    lastName: string;
    relationshipCode?: string;
  };
  
  /** Payer/Health Plan */
  payer: {
    id: string;
    name?: string;
    tradingPartnerId?: string;
  };
  
  /** Admission details (for inpatient) */
  admission?: {
    admissionDate?: string;
    dischargeDate?: string;
    admissionType?: string;
    admissionSource?: string;
  };
  
  /** Service request details */
  serviceRequest: {
    procedureCodes: Array<{
      code: string;
      codeType: 'CPT' | 'HCPCS' | 'ICD10' | 'DRG';
      description?: string;
      modifier?: string[];
      quantity?: number;
      /** References to diagnosis codes (0-indexed positions in diagnosisCodes array) */
      diagnosisPointers?: number[];
    }>;
    diagnosisCodes?: Array<{
      code: string;
      codeType: 'ICD10' | 'ICD9';
      description?: string;
      pointerPosition?: number;
    }>;
    serviceStartDate: string;
    serviceEndDate?: string;
    facility?: {
      npi?: string;
      name?: string;
      typeCode?: string;
    };
  };
  
  /** Review/Authorization response (for type 13, 33) */
  reviewResponse?: {
    authorizationNumber?: string;
    responseCode: '01' | '02' | '03' | '04' | '05' | '06';
    responseDescription?: string;
    reviewOutcome?: 'A1' | 'A2' | 'A3' | 'A4' | 'A6' | 'CT' | 'NA';
    certifiedQuantity?: number;
    certifiedPeriod?: {
      startDate?: string;
      endDate?: string;
    };
    reviewNotes?: string[];
    additionalInfoRequired?: boolean;
    additionalInfoDeadline?: string;
  };
  
  /** Attachments references */
  attachments?: Array<{
    controlNumber: string;
    transmissionCode?: string;
    description?: string;
  }>;
}

/**
 * FHIR R4 Prior Authorization Request (using Claim resource per Da Vinci PAS IG)
 * Profile: http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim
 */
export interface PriorAuthorizationRequest extends Omit<Claim, 'use' | 'meta'> {
  /** Must be 'preauthorization' for prior auth */
  use: 'preauthorization';
  
  /** Meta must reference Da Vinci PAS profile */
  meta: Meta & {
    profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim'];
  };
  
  /** Extension for certification type */
  extension?: Extension[];
}

/**
 * FHIR R4 Prior Authorization Response (using ClaimResponse resource per Da Vinci PAS IG)
 * Profile: http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse
 */
export interface PriorAuthorizationResponse extends Omit<ClaimResponse, 'outcome' | 'meta'> {
  /** Meta must reference Da Vinci PAS profile */
  meta: Meta & {
    profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse'];
  };
  
  /** Outcome: queued, complete, error, partial */
  outcome: 'queued' | 'complete' | 'error' | 'partial';
  
  /** Disposition: approval status text */
  disposition?: string;
  
  /** Extension for review outcome */
  extension?: Extension[];
}

/**
 * Decision timeline configuration for SLA compliance
 */
export interface DecisionTimeline {
  /** Request received timestamp */
  receivedAt: Date;
  
  /** Request type determines SLA */
  requestType: 'standard' | 'urgent' | 'expedited';
  
  /** SLA in hours (72 for standard, 24 for urgent) */
  slaHours: number;
  
  /** Decision due timestamp */
  dueAt: Date;
  
  /** Actual decision timestamp */
  decidedAt?: Date;
  
  /** SLA compliance status */
  slaStatus: 'pending' | 'compliant' | 'breached';
}

/**
 * Maps X12 278 Prior Authorization Request to FHIR R4 Claim resource
 * Implements Da Vinci PAS IG profile requirements
 * 
 * @param input X12 278 structure (type 11 - request)
 * @returns FHIR R4 PriorAuthorizationRequest (Claim resource)
 */
export function mapX12278ToFhirPriorAuth(input: X12_278): PriorAuthorizationRequest {
  if (input.transactionType !== '11') {
    throw new Error(`Invalid transaction type for request: ${input.transactionType}. Expected '11' (request).`);
  }

  const claim: PriorAuthorizationRequest = {
    resourceType: 'Claim',
    id: input.transactionId,
    meta: {
      profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claim'],
      lastUpdated: input.timestamp || new Date().toISOString()
    },
    status: 'active',
    use: 'preauthorization',
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: 'professional',
        display: 'Professional'
      }]
    },
    patient: {
      reference: `Patient/${input.patient.memberId}`,
      identifier: {
        system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
        value: input.patient.memberId
      }
    },
    created: input.timestamp || new Date().toISOString(),
    provider: {
      reference: `Practitioner/${input.requester.npi}`,
      identifier: {
        system: 'http://hl7.org/fhir/sid/us-npi',
        value: input.requester.npi
      },
      display: input.requester.name || input.requester.organizationName
    },
    priority: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/processpriority',
        code: input.levelOfService === 'U' ? 'urgent' : 'normal',
        display: input.levelOfService === 'U' ? 'Urgent' : 'Normal'
      }]
    },
    insurer: {
      reference: `Organization/${input.payer.id}`,
      identifier: {
        value: input.payer.id
      },
      display: input.payer.name
    },
    insurance: [{
      sequence: 1,
      focal: true,
      coverage: {
        reference: `Coverage/${input.patient.memberId}`
      }
    }],
    item: input.serviceRequest.procedureCodes.map((proc, index) => ({
      sequence: index + 1,
      productOrService: {
        coding: [{
          system: proc.codeType === 'CPT' ? 'http://www.ama-assn.org/go/cpt' :
                  proc.codeType === 'HCPCS' ? 'https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets' :
                  proc.codeType === 'ICD10' ? 'http://hl7.org/fhir/sid/icd-10' :
                  'http://terminology.hl7.org/CodeSystem/ex-diagnosisrelatedgroup',
          code: proc.code,
          display: proc.description
        }]
      },
      quantity: {
        value: proc.quantity || 1
      },
      servicedPeriod: {
        start: input.serviceRequest.serviceStartDate,
        end: input.serviceRequest.serviceEndDate
      },
      diagnosisSequence: Array.isArray(proc.diagnosisPointers) && proc.diagnosisPointers.length > 0
        ? proc.diagnosisPointers.map(ptr => ptr + 1)
        : undefined
    })),
    diagnosis: input.serviceRequest.diagnosisCodes?.map((diag, index) => ({
      sequence: index + 1,
      diagnosisCodeableConcept: {
        coding: [{
          system: diag.codeType === 'ICD10' ? 'http://hl7.org/fhir/sid/icd-10' :
                  'http://hl7.org/fhir/sid/icd-9-cm',
          code: diag.code,
          display: diag.description
        }]
      }
    })),
    extension: [
      {
        url: 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-certificationType',
        valueCoding: {
          system: 'https://codesystem.x12.org/005010/1322',
          code: input.certificationType,
          display: input.certificationType === '1' ? 'Initial' :
                   input.certificationType === '2' ? 'Renewal' :
                   input.certificationType === '3' ? 'Revised' : 'Cancel'
        }
      },
      {
        url: 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-serviceType',
        valueCoding: {
          system: 'https://codesystem.x12.org/005010/1365',
          code: input.serviceTypeCode,
          display: getServiceTypeDisplay(input.serviceTypeCode)
        }
      }
    ]
  };

  // Add admission details if present (for inpatient)
  if (input.admission) {
    claim.extension?.push({
      url: 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-admissionDates',
      valuePeriod: {
        start: input.admission.admissionDate,
        end: input.admission.dischargeDate
      }
    });
  }

  // Update insurance with subscriber information if different from patient
  if (input.subscriber) {
    claim.insurance[0].identifier = {
      value: input.subscriber.memberId
    };
    claim.insurance[0].coverage = {
      reference: `Coverage/${input.subscriber.memberId}`
    };
  }

  return claim;
}

/**
 * Maps FHIR R4 ClaimResponse back to X12 278 Response format
 * Implements Da Vinci PAS IG response mapping
 * 
 * @param response FHIR R4 PriorAuthorizationResponse (ClaimResponse resource)
 * @param originalRequest Original X12 278 request for context
 * @returns X12 278 structure (type 13 - response)
 */
export function mapFhirPriorAuthToX12278(
  response: PriorAuthorizationResponse,
  originalRequest: X12_278
): X12_278 {
  const responseCode = getX12ResponseCode(response.outcome, response.disposition);
  
  // Extract certified period from extension (avoiding redundant lookups)
  const reviewPeriodExtension = response.item?.[0]?.extension?.find(
    ext => ext.url === 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewPeriod'
  );
  const certifiedPeriod = reviewPeriodExtension?.valuePeriod 
    ? {
        startDate: reviewPeriodExtension.valuePeriod.start,
        endDate: reviewPeriodExtension.valuePeriod.end
      }
    : undefined;

  const x12Response: X12_278 = {
    ...originalRequest,
    transactionType: '13', // Response
    transactionId: response.id || `RESP-${originalRequest.transactionId}`,
    timestamp: response.created || new Date().toISOString(),
    reviewResponse: {
      authorizationNumber: response.preAuthRef,
      responseCode,
      responseDescription: response.disposition,
      reviewOutcome: getReviewOutcome(response.outcome),
      certifiedQuantity: response.item?.[0]?.adjudication?.find(
        adj => adj.category.coding?.[0]?.code === 'approved'
      )?.value,
      certifiedPeriod,
      reviewNotes: response.processNote?.map(note => note.text) || [],
      additionalInfoRequired: response.outcome === 'queued',
      additionalInfoDeadline: response.outcome === 'queued' ? 
        calculateDeadline(72) : undefined // 72-hour deadline
    }
  };

  return x12Response;
}

/**
 * Creates a Decision Timeline for SLA tracking
 * Standard requests: 72 hours
 * Urgent requests: 24 hours
 * 
 * @param request Prior authorization request
 * @param requestType Type of request (standard, urgent, expedited)
 * @returns DecisionTimeline object
 */
export function createDecisionTimeline(
  request: X12_278 | PriorAuthorizationRequest,
  requestType: 'standard' | 'urgent' | 'expedited' = 'standard'
): DecisionTimeline {
  const receivedAt = new Date();
  const slaHours = requestType === 'urgent' ? 24 : requestType === 'expedited' ? 48 : 72;
  const dueAt = new Date(receivedAt.getTime() + slaHours * 60 * 60 * 1000);

  return {
    receivedAt,
    requestType,
    slaHours,
    dueAt,
    slaStatus: 'pending'
  };
}

/**
 * Checks SLA compliance for a decision timeline
 * 
 * @param timeline Decision timeline to check
 * @param decisionTime Time when decision was made (defaults to now)
 * @returns Updated timeline with compliance status
 */
export function checkSlaCompliance(
  timeline: DecisionTimeline,
  decisionTime: Date = new Date()
): DecisionTimeline {
  const updated = { ...timeline };
  updated.decidedAt = decisionTime;
  
  if (decisionTime <= timeline.dueAt) {
    updated.slaStatus = 'compliant';
  } else {
    updated.slaStatus = 'breached';
  }
  
  return updated;
}

/**
 * Generates a unique ID for resources
 * Uses crypto.randomUUID when available, falls back to timestamp + random
 */
function generateUniqueId(prefix: string): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return `${prefix}-${crypto.randomUUID()}`;
  }
  // Fallback: timestamp + random string for environments without crypto.randomUUID
  const timestamp = Date.now().toString(36);
  const randomPart = Math.random().toString(36).substring(2, 10);
  return `${prefix}-${timestamp}-${randomPart}`;
}

/**
 * Allowed MIME types for clinical attachments per CMS-0057-F
 */
const ALLOWED_ATTACHMENT_MIME_TYPES = [
  'application/pdf',
  'image/jpeg',
  'image/png',
  'image/tiff',
  'image/gif',
  'text/plain',
  'text/xml',
  'application/xml',
  'application/json',
  'application/dicom',
  'application/hl7-v2',
  'application/fhir+json',
  'application/fhir+xml'
];

/**
 * Maximum attachment size in bytes (10 MB default for DoS protection)
 */
const MAX_ATTACHMENT_SIZE_BYTES = 10 * 1024 * 1024;

/**
 * Validates base64 string format using atob() for robust validation
 */
function isValidBase64(str: string): boolean {
  if (!str || str.length === 0) {
    return false;
  }
  
  // First check basic format requirements
  if (str.length % 4 !== 0) {
    return false;
  }
  
  // Use regex to check character set
  const base64Regex = /^[A-Za-z0-9+/]*={0,2}$/;
  if (!base64Regex.test(str)) {
    return false;
  }

  // Try to decode to verify it's actually valid base64
  try {
    // In Node.js environment, use Buffer; in browser, use atob
    if (typeof Buffer !== 'undefined') {
      Buffer.from(str, 'base64');
    } else if (typeof atob !== 'undefined') {
      atob(str);
    }
    return true;
  } catch {
    return false;
  }
}

/**
 * Calculates the decoded byte size of a base64 string
 * Accounts for padding characters correctly
 */
function getBase64DecodedSize(base64: string): number {
  // Remove padding to get accurate count
  const padding = (base64.match(/=+$/) || [''])[0].length;
  return Math.floor((base64.length * 3) / 4) - padding;
}

/**
 * Validates attachment data and content type
 * @throws Error if validation fails
 */
function validateAttachmentInput(data: string, contentType: string): void {
  // Validate content type is in allowed list
  if (!ALLOWED_ATTACHMENT_MIME_TYPES.includes(contentType)) {
    throw new Error(
      `Invalid content type: ${contentType}. Allowed types: ${ALLOWED_ATTACHMENT_MIME_TYPES.join(', ')}`
    );
  }

  // Validate base64 encoding
  if (!isValidBase64(data)) {
    throw new Error('Invalid attachment data: must be valid base64-encoded content');
  }

  // Check size limit using accurate decoded size calculation
  const decodedBytes = getBase64DecodedSize(data);
  if (decodedBytes > MAX_ATTACHMENT_SIZE_BYTES) {
    throw new Error(
      `Attachment too large: ${decodedBytes} bytes exceeds maximum of ${MAX_ATTACHMENT_SIZE_BYTES} bytes (${MAX_ATTACHMENT_SIZE_BYTES / 1024 / 1024} MB)`
    );
  }
}

/**
 * Creates a FHIR Binary resource for attachment handling
 * Supports documents, images, and other clinical attachments
 * 
 * Validates input data for:
 * - Valid base64 encoding
 * - Allowed MIME content types
 * - Size limits to prevent DoS attacks
 * 
 * @param data Base64-encoded attachment data
 * @param contentType MIME type (e.g., 'application/pdf', 'image/jpeg')
 * @param description Human-readable description
 * @returns FHIR R4 Binary resource
 * @throws Error if validation fails
 */
export function createAttachmentBinary(
  data: string,
  contentType: string,
  description?: string
): Binary {
  // Validate input before creating resource
  validateAttachmentInput(data, contentType);

  return {
    resourceType: 'Binary',
    id: generateUniqueId('attachment'),
    meta: {
      profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-binary'],
      lastUpdated: new Date().toISOString()
    },
    contentType,
    data,
    securityContext: description ? {
      display: description
    } : undefined
  };
}

const DOCUMENT_TYPE_MAP: Record<string, { code: string; display: string }> = {
  'clinical-note': { code: '11506-3', display: 'Progress note' },
  'clinical-notes': { code: '11506-3', display: 'Progress note' },
  'lab-results': { code: '11502-2', display: 'Laboratory report' },
  imaging: { code: '18748-4', display: 'Diagnostic imaging study' }
};

/**
 * Creates a DocumentReference linking a Binary attachment to a Claim
 * 
 * @param binary Binary resource containing the attachment
 * @param claimReference Reference to the prior auth Claim
 * @param category Document category code
 * @returns FHIR R4 DocumentReference resource
 */
export function createAttachmentDocumentReference(
  binary: Binary,
  claimReference: Reference,
  category: string = 'clinical-note'
): DocumentReference {
  const documentType = DOCUMENT_TYPE_MAP[category] ?? DOCUMENT_TYPE_MAP['clinical-note'];

  return {
    resourceType: 'DocumentReference',
    id: `docref-${binary.id}`,
    meta: {
      profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-documentreference'],
      lastUpdated: new Date().toISOString()
    },
    status: 'current',
    type: {
      coding: [{
        system: 'http://loinc.org',
        code: documentType.code,
        display: documentType.display
      }]
    },
    category: [{
      coding: [{
        system: 'http://hl7.org/fhir/us/core/CodeSystem/us-core-documentreference-category',
        code: category,
        display: category
      }]
    }],
    subject: claimReference,
    date: new Date().toISOString(),
    content: [{
      attachment: {
        contentType: binary.contentType,
        url: `Binary/${binary.id}`,
        title: binary.securityContext?.display
      }
    }]
  };
}

/**
 * CDS Hooks request payload interface for CRD validation
 * Based on CDS Hooks specification: https://cds-hooks.org/
 */
export interface CDSHookRequest {
  /** The hook that triggered this request */
  hook?: string;
  /** Unique identifier for this hook invocation */
  hookInstance?: string;
  /** Context data specific to the hook type */
  context?: {
    userId?: string;
    patientId?: string;
    draftOrders?: unknown;
    appointments?: unknown;
    [key: string]: unknown;
  };
  /** FHIR server base URL */
  fhirServer?: string;
  /** OAuth2 access token (all fields optional when parent is provided) */
  fhirAuthorization?: {
    access_token?: string;
    token_type?: string;
    scope?: string;
  };
  /** Prefetch data provided by the EHR */
  prefetch?: Record<string, unknown>;
}

/**
 * Validates Da Vinci CRD hook request structure
 * Implements Coverage Requirements Discovery (CRD) IG
 * 
 * @param hookRequest CDS Hooks request payload
 * @returns Validation result
 */
export function validateCRDHookRequest(hookRequest: CDSHookRequest): { valid: boolean; errors: string[] } {
  const errors: string[] = [];

  if (!hookRequest.hook) {
    errors.push('Missing required field: hook');
  }

  if (!hookRequest.hookInstance) {
    errors.push('Missing required field: hookInstance');
  }

  if (!hookRequest.context) {
    errors.push('Missing required field: context');
  }

  if (hookRequest.hook === 'order-sign' && !hookRequest.context?.draftOrders) {
    errors.push('order-sign hook requires context.draftOrders');
  }

  if (hookRequest.hook === 'appointment-book' && !hookRequest.context?.appointments) {
    errors.push('appointment-book hook requires context.appointments');
  }

  return {
    valid: errors.length === 0,
    errors
  };
}

/**
 * Helper: Get X12 response code from FHIR outcome
 * Maps FHIR ClaimResponse outcome to X12 278 response codes
 */
function getX12ResponseCode(outcome: string, disposition?: string): '01' | '02' | '03' | '04' | '05' | '06' {
  if (outcome === 'complete') {
    // For complete outcomes, check disposition for approval/denial status
    // Use exact word boundary checks to avoid false positives (e.g., "not approved")
    const lowerDisposition = disposition?.toLowerCase() ?? '';
    const approvedPatterns = [/\bapproved\b/, /\bauthorized\b/, /\bcertified\b/];
    const deniedPatterns = [/\bdenied\b/, /\brejected\b/, /\bnot approved\b/, /\bnot authorized\b/];
    
    // Check denial patterns first (more specific, handles "not approved")
    if (deniedPatterns.some(pattern => pattern.test(lowerDisposition))) return '02'; // Denied
    if (approvedPatterns.some(pattern => pattern.test(lowerDisposition))) return '01'; // Approved
    
    // Default to approved for complete outcomes without explicit disposition
    return '01'; // Approved (complete without disposition implies success)
  }
  if (outcome === 'queued') return '03'; // Pended/Additional Info Required
  if (outcome === 'partial') return '04'; // Partial Approval
  if (outcome === 'error') return '06'; // Invalid/Error
  return '05'; // Modified (fallback for unknown outcomes)
}

/**
 * Helper: Get review outcome code from FHIR outcome
 */
function getReviewOutcome(outcome: string): 'A1' | 'A2' | 'A3' | 'A4' | 'A6' | 'CT' | 'NA' {
  switch (outcome) {
    case 'complete': return 'A1'; // Certified
    case 'partial': return 'A2'; // Modified
    case 'queued': return 'A4'; // Additional Information Requested
    case 'error': return 'A6'; // Contact Payer
    default: return 'A3'; // Denied
  }
}

/**
 * Helper: Get service type display text
 */
function getServiceTypeDisplay(code: string): string {
  const serviceTypes: Record<string, string> = {
    '1': 'Medical Care',
    '30': 'Health Benefit Plan Coverage',
    '33': 'Chiropractic',
    '35': 'Dental Care',
    '47': 'Hospital',
    '48': 'Hospital - Inpatient',
    '49': 'Hospital - Outpatient',
    '50': 'Hospital - Emergency',
    '86': 'Emergency Services',
    '88': 'Pharmacy',
    '98': 'Professional (Physician)',
    'UC': 'Urgent Care'
  };
  return serviceTypes[code] || `Service Type ${code}`;
}

/**
 * Helper: Calculate deadline timestamp
 */
function calculateDeadline(hoursFromNow: number): string {
  const deadline = new Date();
  deadline.setHours(deadline.getHours() + hoursFromNow);
  return deadline.toISOString();
}

/**
 * Clearinghouse integration configuration for Availity
 */
export interface ClearinghouseConfig {
  /** Clearinghouse name */
  name: 'Availity' | 'Change Healthcare' | 'Waystar' | 'Other';
  
  /** Trading partner ID */
  tradingPartnerId: string;
  
  /** SFTP connection details */
  sftp?: {
    host: string;
    port: number;
    username: string;
    inboundPath: string;
    outboundPath: string;
  };
  
  /** API connection details (if supported) */
  api?: {
    baseUrl: string;
    authType: 'oauth2' | 'apikey' | 'basic';
  };
}

/**
 * Packages a prior authorization request for clearinghouse submission
 * 
 * @note This is a placeholder implementation that returns JSON format.
 *       Production implementations should integrate with an actual X12 EDI
 *       encoder library (e.g., x12-parser, edi-parser) to generate compliant
 *       X12 278 EDI segments.
 * 
 * @todo Integrate with X12 EDI encoder library for production use
 * 
 * @param fhirRequest FHIR Prior Authorization Request
 * @param x12Request X12 278 structure
 * @param config Clearinghouse configuration
 * @returns Packaged submission ready for transmission
 */
export function packageForClearinghouse(
  fhirRequest: PriorAuthorizationRequest,
  x12Request: X12_278,
  config: ClearinghouseConfig
): {
  x12Payload: string;
  metadata: Record<string, unknown>;
  destination: string;
} {
  // Note: In production, this would generate actual X12 EDI format
  // using an EDI encoder library. Current implementation returns JSON.
  const x12Payload = JSON.stringify(x12Request, null, 2);
  
  return {
    x12Payload,
    metadata: {
      transactionId: x12Request.transactionId,
      tradingPartnerId: config.tradingPartnerId,
      clearinghouse: config.name,
      timestamp: new Date().toISOString(),
      direction: 'outbound'
    },
    destination: config.sftp?.outboundPath || config.api?.baseUrl || 'unknown'
  };
}

/**
 * Processes incoming response from clearinghouse
 * 
 * @param x12Payload X12 278 response payload (type 13)
 * @param config Clearinghouse configuration
 * @returns Processed FHIR ClaimResponse
 */
export function processFromClearinghouse(
  x12Payload: string,
  config: ClearinghouseConfig
): {
  x12Response: X12_278;
  fhirResponse: PriorAuthorizationResponse;
} {
  // In production, this would parse actual X12 EDI format
  // For now, we assume JSON format
  const x12Response: X12_278 = JSON.parse(x12Payload);
  
  if (x12Response.transactionType !== '13') {
    throw new Error('Invalid response transaction type. Expected type 13 (response).');
  }
  
  // Convert to FHIR ClaimResponse
  const fhirResponse: PriorAuthorizationResponse = {
    resourceType: 'ClaimResponse',
    id: x12Response.transactionId,
    meta: {
      profile: ['http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse'],
      lastUpdated: x12Response.timestamp || new Date().toISOString()
    },
    status: 'active',
    type: {
      coding: [{
        system: 'http://terminology.hl7.org/CodeSystem/claim-type',
        code: 'professional'
      }]
    },
    use: 'preauthorization',
    patient: {
      reference: `Patient/${x12Response.patient.memberId}`
    },
    created: x12Response.timestamp || new Date().toISOString(),
    insurer: {
      reference: `Organization/${x12Response.payer.id}`
    },
    outcome: x12Response.reviewResponse?.responseCode === '01' ? 'complete' :
             x12Response.reviewResponse?.responseCode === '03' ? 'queued' :
             x12Response.reviewResponse?.responseCode === '04' ? 'partial' : 'error',
    disposition: x12Response.reviewResponse?.responseDescription,
    preAuthRef: x12Response.reviewResponse?.authorizationNumber,
    processNote: x12Response.reviewResponse?.reviewNotes?.map((note, index) => ({
      number: index + 1,
      type: 'display' as const,
      text: note
    }))
  };
  
  return {
    x12Response,
    fhirResponse
  };
}

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
    insurance: [{
      sequence: 1,
      focal: true,
      coverage: {
        reference: `Coverage/${x12Request.patient.memberId}`
      }
    }],
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

  if (x12Request.subscriber) {
    claim.insurance[0].identifier = {
      value: x12Request.subscriber.memberId
    };
    claim.insurance[0].coverage = {
      reference: `Coverage/${x12Request.subscriber.memberId}`
    };
  }
  
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
    requestId: `PA-${randomUUID()}`,
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
    uuid: randomUUID(),
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
 * @param fhirServer Optional FHIR server URL (defaults to cloudhealthoffice.com)
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
  },
  fhirServer: string = 'https://fhir.cloudhealthoffice.com'
): any {
  // Extract patient ID if it's already in the form "Patient/123"
  const patientId = context.patientId.startsWith('Patient/')
    ? context.patientId.substring('Patient/'.length)
    : context.patientId;
    
  return {
    hookInstance: randomUUID(),
    hook: hookType,
    fhirServer,
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
