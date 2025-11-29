/**
 * Cloud Health Office - Eligibility Service Types
 * 
 * Type definitions for the dual-interface eligibility service supporting
 * both X12 270/271 EDI and FHIR R4 CoverageEligibilityRequest/Response.
 */

import { CoverageEligibilityRequest, CoverageEligibilityResponse, Patient, Coverage, Organization } from 'fhir/r4';

// ============================================================================
// X12 270/271 Types (based on HIPAA X12 005010X279A1)
// ============================================================================

/**
 * X12 270 Eligibility Inquiry Request
 */
export interface X12_270_Request {
  /** Unique transaction control number */
  transactionControlNumber: string;
  /** Interchange control number */
  interchangeControlNumber: string;
  /** Transaction date (CCYYMMDD) */
  transactionDate: string;
  /** Transaction time (HHMM) */
  transactionTime?: string;
  
  /** Information source (payer) */
  informationSource: {
    entityIdentifier: string;
    entityType: '1' | '2'; // 1=Person, 2=Non-Person Entity
    name: string;
    identificationCode: string;
    identificationCodeQualifier: string;
  };
  
  /** Information receiver (provider) */
  informationReceiver: {
    entityIdentifier: string;
    entityType: '1' | '2';
    name?: string;
    npi?: string;
    identificationCode?: string;
    identificationCodeQualifier?: string;
  };
  
  /** Subscriber (policy holder) */
  subscriber: {
    memberId: string;
    firstName: string;
    lastName: string;
    middleName?: string;
    suffix?: string;
    dateOfBirth: string; // CCYYMMDD
    gender?: 'M' | 'F' | 'U';
    ssn?: string;
    groupNumber?: string;
    address?: {
      line1?: string;
      line2?: string;
      city?: string;
      state?: string;
      postalCode?: string;
      countryCode?: string;
    };
  };
  
  /** Dependent (if different from subscriber) */
  dependent?: {
    firstName: string;
    lastName: string;
    middleName?: string;
    suffix?: string;
    dateOfBirth: string;
    gender?: 'M' | 'F' | 'U';
    relationshipCode: string; // 01=Spouse, 19=Child, etc.
  };
  
  /** Date range for eligibility inquiry */
  eligibilityDateRange?: {
    startDate: string; // CCYYMMDD
    endDate?: string;
  };
  
  /** Service types being inquired (can be multiple) */
  serviceTypeCodes?: string[];
}

/**
 * X12 271 Eligibility Response
 */
export interface X12_271_Response {
  /** Transaction control number (echoed from 270) */
  transactionControlNumber: string;
  /** Response transaction control number */
  responseControlNumber: string;
  /** Transaction date */
  transactionDate: string;
  /** Transaction time */
  transactionTime?: string;
  
  /** Information source (payer) */
  informationSource: {
    entityIdentifier: string;
    name: string;
    identificationCode: string;
  };
  
  /** Information receiver (provider) - echoed */
  informationReceiver: {
    entityIdentifier: string;
    name?: string;
    npi?: string;
  };
  
  /** Subscriber information */
  subscriber: {
    memberId: string;
    firstName: string;
    lastName: string;
    dateOfBirth: string;
    gender?: 'M' | 'F' | 'U';
    groupNumber?: string;
    groupName?: string;
    planName?: string;
    policyStartDate?: string;
    policyEndDate?: string;
  };
  
  /** Dependent (if queried) */
  dependent?: {
    firstName: string;
    lastName: string;
    dateOfBirth: string;
    relationshipCode: string;
  };
  
  /** Overall eligibility status */
  eligibilityStatus: 'active' | 'inactive' | 'terminated' | 'pending' | 'unknown';
  
  /** Benefit information by service type */
  benefits: EligibilityBenefit[];
  
  /** Errors/rejections if any */
  errors?: EligibilityError[];
  
  /** Additional information messages */
  additionalInfo?: string[];
}

/**
 * Individual benefit information from 271 response
 */
export interface EligibilityBenefit {
  /** Service type code */
  serviceTypeCode: string;
  /** Service type description */
  serviceTypeDescription?: string;
  /** Eligibility/benefit information code */
  eligibilityInfoCode: string; // 1=Active, 6=Inactive, etc.
  /** Coverage level code */
  coverageLevelCode?: string; // IND=Individual, FAM=Family, etc.
  /** Insurance type code */
  insuranceTypeCode?: string;
  /** Plan coverage description */
  planCoverageDescription?: string;
  /** Time period qualifier */
  timePeriodQualifier?: string;
  /** Benefit amount */
  benefitAmount?: number;
  /** Benefit percentage */
  benefitPercent?: number;
  /** Quantity qualifier */
  quantityQualifier?: string;
  /** Quantity */
  quantity?: number;
  /** Authorization required indicator */
  authorizationRequired?: boolean;
  /** In-network indicator */
  inNetwork?: boolean;
  /** Additional benefit info */
  additionalInfo?: {
    deductible?: number;
    deductibleRemaining?: number;
    outOfPocketMax?: number;
    outOfPocketRemaining?: number;
    copay?: number;
    coinsurance?: number;
  };
}

/**
 * Eligibility error from 271 response
 */
export interface EligibilityError {
  /** Error code (AAA segment) */
  errorCode: string;
  /** Error description */
  errorDescription: string;
  /** Follow-up action code */
  followUpActionCode?: string;
}

// ============================================================================
// Eligibility Cache Types
// ============================================================================

/**
 * Cached eligibility record stored in Cosmos DB
 */
export interface EligibilityCacheRecord {
  /** Unique document ID */
  id: string;
  /** Partition key - member ID */
  memberId: string;
  /** Payer ID */
  payerId: string;
  /** Cache key (composite of member, payer, service date) */
  cacheKey: string;
  /** Original request (X12 or FHIR) */
  requestType: 'X12_270' | 'FHIR_CoverageEligibilityRequest';
  /** Original request data */
  originalRequest: X12_270_Request | CoverageEligibilityRequest;
  /** Cached response */
  response: EligibilityCacheResponse;
  /** Created timestamp */
  createdAt: string;
  /** Last accessed timestamp */
  lastAccessedAt: string;
  /** Access count */
  accessCount: number;
  /** TTL in seconds (for Cosmos DB auto-expiry) */
  ttl: number;
  /** Source of eligibility data */
  source: 'QNXT' | 'FHIR_SERVER' | 'CLEARINGHOUSE' | 'MANUAL';
}

/**
 * Cached response data
 */
export interface EligibilityCacheResponse {
  /** X12 271 response (if X12 request) */
  x12Response?: X12_271_Response;
  /** FHIR response (if FHIR request) */
  fhirResponse?: CoverageEligibilityResponse;
  /** Summary eligibility status */
  eligibilityStatus: 'active' | 'inactive' | 'terminated' | 'pending' | 'unknown';
  /** Effective date of eligibility */
  effectiveDate?: string;
  /** Termination date (if applicable) */
  terminationDate?: string;
  /** Plan information */
  planInfo?: {
    planId: string;
    planName: string;
    groupNumber?: string;
    groupName?: string;
  };
}

// ============================================================================
// QNXT Eligibility Rules Types
// ============================================================================

/**
 * QNXT eligibility rule imported from CSV
 */
export interface QNXTEligibilityRule {
  /** Unique rule ID */
  ruleId: string;
  /** Rule name */
  ruleName: string;
  /** Rule description */
  description?: string;
  /** Plan code this rule applies to */
  planCode: string;
  /** Service type code (X12) */
  serviceTypeCode: string;
  /** Benefit category */
  benefitCategory: string;
  /** Coverage indicator */
  coverageIndicator: 'covered' | 'not_covered' | 'limited' | 'excluded';
  /** Prior authorization required */
  priorAuthRequired: boolean;
  /** Referral required */
  referralRequired: boolean;
  /** In-network requirements */
  inNetworkRequirements?: {
    copay?: number;
    coinsurance?: number;
    deductibleApplies?: boolean;
  };
  /** Out-of-network requirements */
  outOfNetworkRequirements?: {
    copay?: number;
    coinsurance?: number;
    deductibleApplies?: boolean;
    coveragePercent?: number;
  };
  /** Quantity limits */
  quantityLimits?: {
    maxQuantity?: number;
    quantityPeriod?: 'day' | 'week' | 'month' | 'year' | 'lifetime';
  };
  /** Dollar limits */
  dollarLimits?: {
    maxAmount?: number;
    amountPeriod?: 'day' | 'week' | 'month' | 'year' | 'lifetime';
  };
  /** Age limits */
  ageLimits?: {
    minAge?: number;
    maxAge?: number;
  };
  /** Gender restrictions */
  genderRestrictions?: ('M' | 'F')[];
  /** Effective date range */
  effectiveDateRange: {
    startDate: string;
    endDate?: string;
  };
  /** Rule priority (lower = higher priority) */
  priority: number;
  /** Active indicator */
  isActive: boolean;
}

// ============================================================================
// Event Grid Types
// ============================================================================

/**
 * EligibilityChecked event published to Event Grid
 */
export interface EligibilityCheckedEvent {
  /** Event ID */
  id: string;
  /** Event type */
  eventType: 'EligibilityChecked';
  /** Event subject (member ID) */
  subject: string;
  /** Event time */
  eventTime: string;
  /** Data version */
  dataVersion: '1.0';
  /** Event data */
  data: {
    /** Member ID */
    memberId: string;
    /** Payer ID */
    payerId: string;
    /** Provider NPI (if available) */
    providerNpi?: string;
    /** Request type */
    requestType: 'X12_270' | 'FHIR_CoverageEligibilityRequest';
    /** Response status */
    eligibilityStatus: 'active' | 'inactive' | 'terminated' | 'pending' | 'unknown';
    /** Service date */
    serviceDate: string;
    /** Service types checked */
    serviceTypeCodes?: string[];
    /** Was response from cache */
    fromCache: boolean;
    /** Response time in milliseconds */
    responseTimeMs: number;
    /** Additional metadata */
    metadata?: Record<string, string>;
  };
}

// ============================================================================
// Service Configuration Types
// ============================================================================

/**
 * Eligibility service configuration
 */
export interface EligibilityServiceConfig {
  /** Cosmos DB configuration */
  cosmosDb: {
    endpoint: string;
    databaseName: string;
    containerName: string;
    /** Default TTL for cache records (seconds) */
    defaultTtlSeconds: number;
  };
  /** Event Grid configuration */
  eventGrid: {
    topicEndpoint: string;
    topicKey?: string; // Use managed identity if not provided
  };
  /** QNXT API configuration */
  qnxt?: {
    baseUrl: string;
    apiKey?: string;
    timeout: number;
  };
  /** FHIR server configuration */
  fhirServer?: {
    baseUrl: string;
    authType: 'none' | 'basic' | 'bearer' | 'managed_identity';
    credentials?: string;
  };
  /** Cache settings */
  cache: {
    enabled: boolean;
    /** TTL for active member records (seconds) */
    activeMemberTtl: number;
    /** TTL for inactive/terminated records (seconds) */
    inactiveMemberTtl: number;
    /** Maximum cache age before refresh (seconds) */
    maxCacheAge: number;
  };
  /** Dapr configuration */
  dapr: {
    enabled: boolean;
    httpPort: number;
    grpcPort: number;
    appId: string;
    stateStoreName: string;
    pubSubName: string;
  };
}

// ============================================================================
// API Request/Response Types
// ============================================================================

/**
 * Unified eligibility check request
 */
export interface EligibilityCheckRequest {
  /** Request format */
  format: 'X12' | 'FHIR';
  /** X12 270 request (if format is X12) */
  x12Request?: X12_270_Request;
  /** FHIR request (if format is FHIR) */
  fhirRequest?: CoverageEligibilityRequest;
  /** Skip cache and get fresh data */
  skipCache?: boolean;
  /** Correlation ID for tracing */
  correlationId?: string;
}

/**
 * Unified eligibility check response
 */
export interface EligibilityCheckResponse {
  /** Request format (echoed) */
  format: 'X12' | 'FHIR';
  /** X12 271 response (if format is X12) */
  x12Response?: X12_271_Response;
  /** FHIR response (if format is FHIR) */
  fhirResponse?: CoverageEligibilityResponse;
  /** Was response from cache */
  fromCache: boolean;
  /** Response timestamp */
  timestamp: string;
  /** Response time in milliseconds */
  responseTimeMs: number;
  /** Cache key (for debugging) */
  cacheKey?: string;
  /** Correlation ID (echoed) */
  correlationId?: string;
}

// ============================================================================
// Health Check Types
// ============================================================================

/**
 * Service health status
 */
export interface HealthStatus {
  status: 'healthy' | 'degraded' | 'unhealthy';
  version: string;
  uptime: number;
  timestamp: string;
  checks: {
    cosmosDb: ComponentHealth;
    eventGrid: ComponentHealth;
    qnxt?: ComponentHealth;
    fhirServer?: ComponentHealth;
    dapr?: ComponentHealth;
  };
}

/**
 * Individual component health
 */
export interface ComponentHealth {
  status: 'healthy' | 'degraded' | 'unhealthy';
  latencyMs?: number;
  lastCheck: string;
  error?: string;
}
