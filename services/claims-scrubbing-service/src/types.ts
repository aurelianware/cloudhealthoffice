/**
 * Cloud Health Office - Claims Scrubbing Service Types
 * 
 * Type definitions for 837P (Professional), 837I (Institutional), and 837D (Dental)
 * claims processing with configurable validation rules.
 */

// ============================================================================
// X12 837 Claim Types
// ============================================================================

/**
 * Base claim structure common to all 837 transaction types
 */
export interface X12_837_Claim {
  /** Unique claim identifier */
  claimId: string;
  /** Claim type: P=Professional, I=Institutional, D=Dental */
  claimType: '837P' | '837I' | '837D';
  /** Transaction control number from ISA segment */
  transactionControlNumber: string;
  /** Interchange control number */
  interchangeControlNumber: string;
  /** Transaction date (CCYYMMDD) */
  transactionDate: string;
  /** Submitter information */
  submitter: ClaimSubmitter;
  /** Receiver information */
  receiver: ClaimReceiver;
  /** Billing provider information */
  billingProvider: BillingProvider;
  /** Subscriber (policyholder) information */
  subscriber: ClaimSubscriber;
  /** Patient information (if different from subscriber) */
  patient?: ClaimPatient;
  /** Claim header information */
  claimHeader: ClaimHeader;
  /** Service lines */
  serviceLines: ServiceLine[];
  /** Total claimed amount */
  totalClaimedAmount: number;
  /** Original raw EDI content */
  rawEdi?: string;
  /** Parsed timestamp */
  parsedAt: string;
}

/**
 * Submitter (Loop 1000A) information
 */
export interface ClaimSubmitter {
  /** Submitter name */
  name: string;
  /** Submitter identification */
  identificationCode: string;
  /** Identification qualifier (46=ETIN, EI=EIN) */
  identificationQualifier: string;
  /** Contact information */
  contact?: {
    name?: string;
    phone?: string;
    email?: string;
  };
}

/**
 * Receiver (Loop 1000B) information
 */
export interface ClaimReceiver {
  /** Receiver name (typically payer) */
  name: string;
  /** Receiver identification (payer ID) */
  identificationCode: string;
  /** Identification qualifier */
  identificationQualifier: string;
}

/**
 * Billing Provider (Loop 2010AA) information
 */
export interface BillingProvider {
  /** Provider NPI */
  npi: string;
  /** Provider name */
  name: string;
  /** Entity type (1=Person, 2=Non-Person) */
  entityType: '1' | '2';
  /** Tax identification number */
  taxId?: string;
  /** Tax ID qualifier (EI=EIN, SY=SSN) */
  taxIdQualifier?: 'EI' | 'SY';
  /** Address */
  address: ProviderAddress;
  /** Taxonomy code */
  taxonomyCode?: string;
  /** Pay-to provider (if different from billing) */
  payToProvider?: {
    name: string;
    npi?: string;
    address?: ProviderAddress;
  };
}

/**
 * Provider address structure
 */
export interface ProviderAddress {
  /** Address line 1 */
  line1: string;
  /** Address line 2 */
  line2?: string;
  /** City */
  city: string;
  /** State code (2-letter) */
  state: string;
  /** ZIP code */
  postalCode: string;
  /** Country code */
  countryCode?: string;
}

/**
 * Subscriber (Loop 2010BA) information
 */
export interface ClaimSubscriber {
  /** Member ID */
  memberId: string;
  /** First name */
  firstName: string;
  /** Last name */
  lastName: string;
  /** Middle name */
  middleName?: string;
  /** Name suffix */
  suffix?: string;
  /** Date of birth (CCYYMMDD) */
  dateOfBirth: string;
  /** Gender (M=Male, F=Female, U=Unknown) */
  gender?: 'M' | 'F' | 'U';
  /** Group number */
  groupNumber?: string;
  /** Address */
  address?: {
    line1?: string;
    line2?: string;
    city?: string;
    state?: string;
    postalCode?: string;
  };
  /** Payer assigned member ID */
  payerMemberId?: string;
}

/**
 * Patient (Loop 2010CA) information when different from subscriber
 */
export interface ClaimPatient {
  /** First name */
  firstName: string;
  /** Last name */
  lastName: string;
  /** Middle name */
  middleName?: string;
  /** Date of birth (CCYYMMDD) */
  dateOfBirth: string;
  /** Gender */
  gender?: 'M' | 'F' | 'U';
  /** Relationship to subscriber */
  relationshipCode: string;
  /** Address (if different from subscriber) */
  address?: {
    line1?: string;
    line2?: string;
    city?: string;
    state?: string;
    postalCode?: string;
  };
}

/**
 * Claim header (Loop 2300) information
 */
export interface ClaimHeader {
  /** Patient control number (claim reference) */
  patientControlNumber: string;
  /** Total claim charge amount */
  totalChargeAmount: number;
  /** Place of service code (professional claims) */
  placeOfServiceCode?: string;
  /** Facility type code (institutional claims) */
  facilityTypeCode?: string;
  /** Claim frequency code */
  frequencyCode?: string;
  /** Provider signature on file indicator */
  signatureOnFile?: boolean;
  /** Assignment of benefits indicator */
  assignmentOfBenefits?: boolean;
  /** Release of information code */
  releaseOfInformation?: string;
  /** Principal diagnosis code */
  principalDiagnosisCode?: string;
  /** Admitting diagnosis (institutional) */
  admittingDiagnosisCode?: string;
  /** All diagnosis codes */
  diagnosisCodes?: DiagnosisCode[];
  /** Admission date (institutional) */
  admissionDate?: string;
  /** Discharge date (institutional) */
  dischargeDate?: string;
  /** Admission type code */
  admissionTypeCode?: string;
  /** Admission source code */
  admissionSourceCode?: string;
  /** Patient status code */
  patientStatusCode?: string;
  /** DRG code (institutional) */
  drgCode?: string;
  /** Prior authorization number */
  priorAuthorizationNumber?: string;
  /** Referral number */
  referralNumber?: string;
  /** Accident information */
  accidentInfo?: {
    type?: 'auto' | 'employment' | 'other';
    date?: string;
    state?: string;
  };
  /** Referring provider */
  referringProvider?: {
    npi: string;
    name: string;
  };
  /** Rendering provider (if different from billing) */
  renderingProvider?: {
    npi: string;
    name: string;
    taxonomyCode?: string;
  };
  /** Service facility location */
  serviceFacilityLocation?: {
    name: string;
    npi?: string;
    address: ProviderAddress;
  };
}

/**
 * Diagnosis code structure
 */
export interface DiagnosisCode {
  /** ICD code */
  code: string;
  /** Code qualifier (ABK=ICD-10-CM, BK=ICD-9-CM) */
  qualifier: 'ABK' | 'BK' | 'ABF';
  /** Pointer for service line reference */
  pointer?: number;
  /** Present on admission indicator (institutional) */
  presentOnAdmission?: 'Y' | 'N' | 'U' | 'W';
}

/**
 * Service line (Loop 2400) information
 */
export interface ServiceLine {
  /** Line sequence number */
  lineNumber: number;
  /** Procedure code (CPT/HCPCS for professional, Revenue code for institutional) */
  procedureCode: string;
  /** Procedure code qualifier (HC=HCPCS, ZZ=Mutually defined) */
  procedureCodeQualifier?: string;
  /** Modifiers (up to 4) */
  modifiers?: string[];
  /** Description */
  description?: string;
  /** Service date or date range */
  serviceDate: string;
  /** Service end date (if date range) */
  serviceDateEnd?: string;
  /** Line charge amount */
  chargeAmount: number;
  /** Units of service */
  units: number;
  /** Unit type (UN=Units, MJ=Minutes, DA=Days) */
  unitType?: string;
  /** Place of service (professional claims) */
  placeOfService?: string;
  /** Revenue code (institutional claims) */
  revenueCode?: string;
  /** Diagnosis code pointers */
  diagnosisPointers?: number[];
  /** Rendering provider (if different from header) */
  renderingProvider?: {
    npi: string;
    name?: string;
  };
  /** National Drug Code for pharmacy-related services */
  ndcCode?: string;
  /** NDC quantity and unit of measure */
  ndcQuantity?: {
    quantity: number;
    unitOfMeasure: string;
  };
  /** Prior authorization number for line */
  priorAuthorizationNumber?: string;
  /** Emergency indicator */
  emergencyIndicator?: boolean;
  /** EPSDT indicator (pediatric services) */
  epsdtIndicator?: boolean;
  /** Family planning indicator */
  familyPlanningIndicator?: boolean;
  /** Tooth information (dental claims) */
  toothInfo?: {
    toothNumber?: string;
    toothSurfaces?: string[];
    oralCavityDesignation?: string;
  };
}

// ============================================================================
// Validation Rule Types
// ============================================================================

/**
 * Validation rule definition
 */
export interface ValidationRule {
  /** Unique rule identifier */
  ruleId: string;
  /** Rule name */
  ruleName: string;
  /** Rule description */
  description: string;
  /** Rule category */
  category: ValidationCategory;
  /** Severity level */
  severity: 'error' | 'warning' | 'info';
  /** Claim types this rule applies to */
  appliesTo: ('837P' | '837I' | '837D')[];
  /** Whether rule is enabled */
  enabled: boolean;
  /** Rule priority (lower = runs first) */
  priority: number;
  /** Rule type */
  type: 'standard' | 'custom' | 'payer-specific';
  /** Payer ID if payer-specific rule */
  payerId?: string;
  /** Effective date range */
  effectiveDateRange?: {
    startDate: string;
    endDate?: string;
  };
  /** Rule configuration */
  config?: Record<string, unknown>;
  /** Custom rule script (for custom rules) */
  customScript?: string;
  /** Auto-correct action if applicable */
  autoCorrect?: boolean;
}

/**
 * Validation rule categories
 */
export type ValidationCategory =
  | 'data-completeness'     // Required fields present
  | 'data-format'           // Field format validation
  | 'code-validity'         // ICD, CPT, HCPCS, Revenue code validation
  | 'code-combination'      // Procedure/diagnosis combinations
  | 'date-logic'            // Date range and sequence validation
  | 'amount-logic'          // Charge amount validation
  | 'provider-validation'   // NPI, taxonomy validation
  | 'member-validation'     // Member eligibility checks
  | 'authorization'         // Prior auth requirements
  | 'duplicate-detection'   // Duplicate claim detection
  | 'medical-necessity'     // Medical necessity rules
  | 'modifier-validation'   // Modifier usage rules
  | 'bundling-unbundling'   // NCCI edits, CCI rules
  | 'payer-specific'        // Payer-specific business rules
  | 'custom';               // Custom rules

/**
 * Validation result for a single rule
 */
export interface ValidationResult {
  /** Rule that was executed */
  ruleId: string;
  /** Rule name */
  ruleName: string;
  /** Whether validation passed */
  passed: boolean;
  /** Severity if failed */
  severity?: 'error' | 'warning' | 'info';
  /** Error/warning message */
  message?: string;
  /** Affected field(s) */
  fields?: string[];
  /** Affected service line number(s) */
  serviceLines?: number[];
  /** Additional context */
  context?: Record<string, unknown>;
  /** Edit code for categorization */
  editCode?: string;
  /** Suggested correction */
  suggestion?: string;
  /** Auto-corrected indicator */
  autoCorrected?: boolean;
  /** Execution time in milliseconds */
  executionTimeMs?: number;
}

/**
 * Complete claim validation result
 */
export interface ClaimValidationResult {
  /** Original claim ID */
  claimId: string;
  /** Claim type */
  claimType: '837P' | '837I' | '837D';
  /** Patient control number */
  patientControlNumber: string;
  /** Overall validation status */
  status: 'clean' | 'flagged' | 'rejected';
  /** Total rules executed */
  rulesExecuted: number;
  /** Rules that passed */
  rulesPassed: number;
  /** Rules that failed */
  rulesFailed: number;
  /** Error count */
  errorCount: number;
  /** Warning count */
  warningCount: number;
  /** Info count */
  infoCount: number;
  /** Individual rule results */
  results: ValidationResult[];
  /** Validation timestamp */
  validatedAt: string;
  /** Total validation time in milliseconds */
  totalValidationTimeMs: number;
  /** Routing decision */
  routing: ClaimRoutingDecision;
  /** First-pass rate indicator */
  firstPassEligible: boolean;
}

/**
 * Claim routing decision after validation
 */
export interface ClaimRoutingDecision {
  /** Destination */
  destination: 'adjudication' | 'work-queue' | 'reject';
  /** Queue name if routed to work queue */
  queueName?: string;
  /** Queue priority */
  priority?: 'high' | 'medium' | 'low';
  /** Reason for routing */
  reason: string;
  /** Edit codes triggered */
  editCodes?: string[];
  /** Requires manual review */
  requiresManualReview: boolean;
  /** Assigned team or user */
  assignedTo?: string;
  /** Due date for review */
  dueDate?: string;
}

// ============================================================================
// Standard Rule Definitions
// ============================================================================

/**
 * Standard validation rules that come pre-configured
 */
export interface StandardRuleSet {
  /** Data completeness rules */
  dataCompleteness: StandardDataCompletenessRules;
  /** Code validation rules */
  codeValidation: StandardCodeValidationRules;
  /** Date logic rules */
  dateLogic: StandardDateLogicRules;
  /** Amount logic rules */
  amountLogic: StandardAmountLogicRules;
  /** Provider validation rules */
  providerValidation: StandardProviderValidationRules;
  /** Modifier validation rules */
  modifierValidation: StandardModifierValidationRules;
}

export interface StandardDataCompletenessRules {
  /** Required member ID */
  memberIdRequired: boolean;
  /** Required subscriber DOB */
  subscriberDobRequired: boolean;
  /** Required billing provider NPI */
  billingProviderNpiRequired: boolean;
  /** Required diagnosis codes */
  diagnosisRequired: boolean;
  /** Minimum service lines */
  minServiceLines: number;
  /** Required service date */
  serviceDateRequired: boolean;
  /** Required charge amount */
  chargeAmountRequired: boolean;
}

export interface StandardCodeValidationRules {
  /** Validate ICD-10 codes */
  validateIcd10: boolean;
  /** Validate CPT codes */
  validateCpt: boolean;
  /** Validate HCPCS codes */
  validateHcpcs: boolean;
  /** Validate revenue codes (837I) */
  validateRevenueCodes: boolean;
  /** Validate place of service codes */
  validatePlaceOfService: boolean;
  /** Check for obsolete codes */
  checkObsoleteCodes: boolean;
  /** Check for gender-specific codes */
  checkGenderSpecificCodes: boolean;
  /** Check for age-specific codes */
  checkAgeSpecificCodes: boolean;
}

export interface StandardDateLogicRules {
  /** Service date not in future */
  serviceDateNotFuture: boolean;
  /** Service date within filing limit */
  serviceDateWithinFilingLimit: boolean;
  /** Filing limit days */
  filingLimitDays: number;
  /** Discharge date after admission (837I) */
  dischargeDateAfterAdmission: boolean;
  /** Patient DOB before service date */
  patientDobBeforeService: boolean;
  /** Service dates in logical sequence */
  serviceDatesInSequence: boolean;
}

export interface StandardAmountLogicRules {
  /** Charge amounts must be positive */
  chargeAmountsPositive: boolean;
  /** Total matches sum of lines */
  totalMatchesLineSum: boolean;
  /** Maximum single line amount */
  maxSingleLineAmount?: number;
  /** Maximum claim total */
  maxClaimTotal?: number;
  /** Units must be positive */
  unitsPositive: boolean;
  /** Maximum units per line */
  maxUnitsPerLine?: number;
}

export interface StandardProviderValidationRules {
  /** Validate NPI format */
  validateNpiFormat: boolean;
  /** Validate NPI against registry */
  validateNpiRegistry: boolean;
  /** Validate taxonomy code format */
  validateTaxonomyFormat: boolean;
  /** Validate tax ID format */
  validateTaxIdFormat: boolean;
  /** Rendering provider required */
  renderingProviderRequired: boolean;
}

export interface StandardModifierValidationRules {
  /** Validate modifier format */
  validateModifierFormat: boolean;
  /** Check duplicate modifiers */
  checkDuplicateModifiers: boolean;
  /** Validate modifier order */
  validateModifierOrder: boolean;
  /** Check mutually exclusive modifiers */
  checkMutuallyExclusiveModifiers: boolean;
}

// ============================================================================
// Custom Rule Framework
// ============================================================================

/**
 * Custom rule definition for payer-specific logic
 */
export interface CustomRule extends ValidationRule {
  /** Rule type is custom */
  type: 'custom';
  /** Custom validation function as serialized string */
  validationScript: string;
  /** Input parameters for the script */
  parameters?: Record<string, unknown>;
  /** Dependencies on other rules */
  dependsOn?: string[];
  /** Test cases for the rule */
  testCases?: CustomRuleTestCase[];
}

/**
 * Test case for custom rule validation
 */
export interface CustomRuleTestCase {
  /** Test case name */
  name: string;
  /** Test input (partial claim data) */
  input: Partial<X12_837_Claim>;
  /** Expected result */
  expectedPass: boolean;
  /** Expected message if failed */
  expectedMessage?: string;
}

// ============================================================================
// Service Configuration
// ============================================================================

/**
 * Claims scrubbing service configuration
 */
export interface ClaimsScrubberConfig {
  /** Service Bus configuration */
  serviceBus: {
    connectionString?: string;
    namespace?: string;
    /** Topic for incoming claims */
    inboundTopic: string;
    /** Topic for clean claims (to adjudication) */
    cleanClaimsTopic: string;
    /** Topic for flagged claims (to work queue) */
    flaggedClaimsTopic: string;
    /** Topic for rejected claims */
    rejectedClaimsTopic: string;
    /** Subscription name */
    subscriptionName: string;
  };
  /** Storage configuration for claim archival */
  storage: {
    connectionString?: string;
    accountName?: string;
    containerName: string;
    /** Archive path pattern */
    archivePathPattern: string;
  };
  /** Cosmos DB for rule storage and audit */
  cosmosDb: {
    endpoint: string;
    databaseName: string;
    rulesContainerName: string;
    auditContainerName: string;
  };
  /** Rule engine configuration */
  ruleEngine: {
    /** Enable parallel rule execution */
    parallelExecution: boolean;
    /** Maximum concurrent rules */
    maxConcurrency: number;
    /** Rule timeout in milliseconds */
    ruleTimeoutMs: number;
    /** Continue on rule error */
    continueOnError: boolean;
    /** Enable rule caching */
    cacheRules: boolean;
    /** Rule cache TTL in seconds */
    ruleCacheTtlSeconds: number;
  };
  /** Validation thresholds */
  thresholds: {
    /** Maximum errors before rejection */
    maxErrorsForRejection: number;
    /** Maximum warnings before flagging */
    maxWarningsForFlagging: number;
    /** First-pass rate target percentage */
    firstPassRateTarget: number;
  };
  /** Feature flags */
  features: {
    /** Enable duplicate detection */
    duplicateDetection: boolean;
    /** Enable medical necessity checks */
    medicalNecessityChecks: boolean;
    /** Enable NCCI edits */
    ncciEdits: boolean;
    /** Enable auto-correction */
    autoCorrection: boolean;
    /** Enable real-time NPI validation */
    realtimeNpiValidation: boolean;
  };
  /** Dapr configuration */
  dapr?: {
    enabled: boolean;
    httpPort: number;
    grpcPort: number;
    appId: string;
    pubSubName: string;
    stateStoreName: string;
  };
}

// ============================================================================
// Event Types
// ============================================================================

/**
 * Event published when a claim is validated
 */
export interface ClaimValidatedEvent {
  /** Event ID */
  id: string;
  /** Event type */
  eventType: 'ClaimValidated';
  /** Event subject (claim ID) */
  subject: string;
  /** Event time */
  eventTime: string;
  /** Data version */
  dataVersion: '1.0';
  /** Event data */
  data: {
    /** Claim ID */
    claimId: string;
    /** Claim type */
    claimType: '837P' | '837I' | '837D';
    /** Patient control number */
    patientControlNumber: string;
    /** Validation status */
    status: 'clean' | 'flagged' | 'rejected';
    /** Error count */
    errorCount: number;
    /** Warning count */
    warningCount: number;
    /** Routing destination */
    routingDestination: 'adjudication' | 'work-queue' | 'reject';
    /** Total claimed amount */
    totalClaimedAmount: number;
    /** Billing provider NPI */
    billingProviderNpi: string;
    /** Member ID */
    memberId: string;
    /** Validation time in milliseconds */
    validationTimeMs: number;
    /** First-pass eligible */
    firstPassEligible: boolean;
    /** Edit codes triggered */
    editCodes?: string[];
  };
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
    serviceBus: ComponentHealth;
    cosmosDb: ComponentHealth;
    storage: ComponentHealth;
    ruleEngine: ComponentHealth;
    dapr?: ComponentHealth;
  };
  metrics: {
    claimsProcessed: number;
    claimsClean: number;
    claimsFlagged: number;
    claimsRejected: number;
    averageValidationTimeMs: number;
    firstPassRate: number;
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
  details?: Record<string, unknown>;
}

// ============================================================================
// API Types
// ============================================================================

/**
 * API request to validate a claim
 */
export interface ValidateClaimRequest {
  /** Claim data */
  claim: X12_837_Claim;
  /** Optional rule set to use (defaults to all enabled rules) */
  ruleSetId?: string;
  /** Skip specific rules */
  skipRules?: string[];
  /** Only run specific rules */
  onlyRules?: string[];
  /** Enable auto-correction */
  autoCorrect?: boolean;
  /** Correlation ID for tracing */
  correlationId?: string;
}

/**
 * API response for claim validation
 */
export interface ValidateClaimResponse {
  /** Validation result */
  result: ClaimValidationResult;
  /** Corrected claim (if auto-correction enabled) */
  correctedClaim?: X12_837_Claim;
  /** Correlation ID */
  correlationId?: string;
  /** Response timestamp */
  timestamp: string;
}

/**
 * Batch validation request
 */
export interface BatchValidateRequest {
  /** Claims to validate */
  claims: X12_837_Claim[];
  /** Rule set ID */
  ruleSetId?: string;
  /** Skip rules */
  skipRules?: string[];
  /** Correlation ID */
  correlationId?: string;
}

/**
 * Batch validation response
 */
export interface BatchValidateResponse {
  /** Total claims processed */
  totalClaims: number;
  /** Clean claims count */
  cleanClaims: number;
  /** Flagged claims count */
  flaggedClaims: number;
  /** Rejected claims count */
  rejectedClaims: number;
  /** Individual results */
  results: ClaimValidationResult[];
  /** First-pass rate */
  firstPassRate: number;
  /** Total processing time */
  totalProcessingTimeMs: number;
  /** Correlation ID */
  correlationId?: string;
}
