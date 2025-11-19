/**
 * Availity Integration Configuration Interfaces
 * 
 * TypeScript interfaces for the unified Availity integration configuration schema.
 * Supports all transaction types: 837 Claims, 270/271 Eligibility, 276/277 Claim Status,
 * Appeals, Attachments 275, and Enhanced Claim Status (ECS).
 */

/**
 * Contact information for various roles
 */
export interface Contact {
  /** Contact person's full name */
  name: string;
  /** Contact email address */
  email: string;
  /** 10-digit phone number (no formatting) */
  phone?: string;
}

/**
 * Collection of contacts for different roles
 */
export interface Contacts {
  /** Technical contact for integration issues */
  technical: Contact;
  /** Account manager contact */
  accountManager: Contact;
  /** Escalation contact for critical issues */
  escalation: Contact;
  /** Additional contact (optional) */
  additional?: Contact;
}

/**
 * Geographic coverage configuration
 */
export interface Geography {
  /** Whether the payer operates nationwide */
  nationwide: boolean;
  /** Array of 2-letter state codes (required if nationwide=false) */
  states?: string[];
}

/**
 * ISA Authorization Qualifier values
 */
export enum ISAAuthorizationQualifier {
  NO_AUTH = "00",
  PASSWORD = "03"
}

/**
 * ISA Security Qualifier values
 */
export enum ISASecurityQualifier {
  NO_SECURITY = "00",
  PASSWORD = "01"
}

/**
 * ISA Interchange ID Qualifier values
 */
export enum ISAInterchangeIdQualifier {
  DUNS = "01",
  TELEPHONE = "14",
  UCC = "20",
  CARRIER_ID = "27",
  FISCAL_ID = "28",
  DEA = "29",
  STATE_LICENSE = "30",
  NCPDP = "33",
  MUTUALLY_DEFINED = "ZZ"
}

/**
 * ISA Usage Indicator values
 */
export enum ISAUsageIndicator {
  PRODUCTION = "P",
  TEST = "T"
}

/**
 * GS Version Code values
 */
export enum GSVersionCode {
  V005010 = "005010",
  V005010X212 = "005010X212",
  V005010X217 = "005010X217",
  V005010X221 = "005010X221",
  V005010X222 = "005010X222"
}

/**
 * ISA/GS segment enveloping configuration
 */
export interface Enveloping {
  /** ISA01 - Authorization Information Qualifier */
  isaAuthorizationQualifier?: ISAAuthorizationQualifier;
  /** ISA03 - Security Information Qualifier */
  isaSecurityQualifier?: ISASecurityQualifier;
  /** ISA05/ISA07 - Interchange ID Qualifier */
  isaInterchangeIdQualifier?: ISAInterchangeIdQualifier;
  /** ISA15 - Usage Indicator */
  isaUsageIndicator?: ISAUsageIndicator;
  /** GS02 - Application Sender's Code */
  gsApplicationSenderCode?: string;
  /** GS03 - Application Receiver's Code */
  gsApplicationReceiverCode?: string;
  /** GS08 - Version/Release/Industry Identifier Code */
  gsVersionCode?: GSVersionCode;
}

/**
 * Connectivity protocol options
 */
export enum ConnectivityProtocol {
  HTTPS = "HTTPS",
  SFTP = "SFTP",
  FTP = "FTP"
}

/**
 * Connectivity configuration for HTTPS/FTP
 */
export interface Connectivity {
  /** Connection protocol */
  protocol?: ConnectivityProtocol;
  /** Connection timeout in seconds (10-300) */
  timeout?: number;
  /** Number of retry attempts on failure (0-5) */
  retryAttempts?: number;
  /** Maximum number of concurrent threads (1-50) */
  maxThreads?: number;
  /** Whether to maintain persistent connections */
  keepAlive?: boolean;
}

/**
 * Testing environment and requirements
 */
export interface Testing {
  /** Whether a test environment is available */
  testEnvironmentAvailable?: boolean;
  /** Whether certification is required before production */
  certificationRequired?: boolean;
  /** Description of the approval process */
  approvalProcess?: string;
  /** Whether test data is provided by payer */
  testDataProvided?: boolean;
  /** Expected timeline for certification */
  certificationTimeline?: string;
}

/**
 * Character encoding options
 */
export enum CharacterEncoding {
  ASCII = "ASCII",
  UTF8 = "UTF-8",
  ISO_8859_1 = "ISO-8859-1"
}

/**
 * Character set and encoding requirements
 */
export interface CharacterSet {
  /** Whether only uppercase characters are allowed */
  uppercaseOnly?: boolean;
  /** Whether spaces are allowed in fields */
  spacesAllowed?: boolean;
  /** Whether extended ASCII characters are supported */
  extendedCharactersSupported?: boolean;
  /** Character encoding */
  encoding?: CharacterEncoding;
}

/**
 * Error handling and acknowledgment configuration
 */
export interface ErrorHandling {
  /** Whether TA1 acknowledgments are required */
  ta1Required?: boolean;
  /** Whether 997 functional acknowledgments are required */
  functional997Required?: boolean;
  /** Whether 999 implementation acknowledgments are required */
  functional999Required?: boolean;
  /** Whether unused segments are allowed in transactions */
  unusedSegmentsAllowed?: boolean;
  /** Email address for error notifications */
  errorNotificationEmail?: string;
}

/**
 * FTP configuration for batch transaction modes
 */
export interface FTPConfig {
  /** FTP server hostname */
  host?: string;
  /** FTP server port (1-65535) */
  port?: number;
  /** FTP username */
  username?: string;
  /** Folder for inbound files */
  inboundFolder?: string;
  /** Folder for outbound files */
  outboundFolder?: string;
  /** Folder for archived files */
  archiveFolder?: string;
}

/**
 * Transaction mode connectivity configuration
 */
export interface TransactionMode {
  /** Whether this transaction mode is enabled */
  enabled?: boolean;
  /** Test environment URL */
  testUrl?: string;
  /** Production environment URL */
  prodUrl?: string;
  /** FTP configuration for batch modes */
  ftpConfig?: FTPConfig;
  /** Request timeout in seconds (10-300) */
  timeout?: number;
  /** System availability schedule */
  systemAvailability?: string;
  /** Number of continuous processing threads (1-50) */
  continuousThreads?: number;
}

/**
 * Collection of transaction modes for a module
 */
export interface TransactionModes {
  /** Real-time web transaction mode */
  realtime_web?: TransactionMode;
  /** Real-time B2B transaction mode */
  realtime_b2b?: TransactionMode;
  /** EDI batch transaction mode */
  edi_batch?: TransactionMode;
}

/**
 * Claim type support configuration
 */
export interface ClaimTypes {
  /** 837P - Professional claims */
  professional?: boolean;
  /** 837I - Institutional claims */
  institutional?: boolean;
  /** 837D - Dental claims */
  dental?: boolean;
  /** Encounter data */
  encounters?: boolean;
}

/**
 * Claim aggregation strategy
 */
export enum ClaimAggregation {
  SINGLE = "single",
  BATCH = "batch"
}

/**
 * File structure requirements for claims
 */
export interface FileStructure {
  /** Maximum claims per file (1-10000) */
  maxClaimsPerFile?: number;
  /** Maximum claims per transaction set (1-100) */
  maxClaimsPerTransaction?: number;
  /** Claim aggregation strategy */
  aggregation?: ClaimAggregation;
}

/**
 * File naming conventions for different claim types
 */
export interface FileNaming {
  /** Pattern for professional claim files */
  professional?: string;
  /** Pattern for institutional claim files */
  institutional?: string;
  /** Pattern for dental claim files */
  dental?: string;
}

/**
 * Edit severity levels
 */
export enum EditSeverity {
  ERROR = "error",
  WARNING = "warning",
  INFO = "info"
}

/**
 * Custom claim edit configuration
 */
export interface ClaimEdit {
  /** Unique identifier for the edit */
  editId?: string;
  /** Description of the edit rule */
  description?: string;
  /** Edit severity level */
  severity?: EditSeverity;
}

/**
 * Custom data enrichment configuration
 */
export interface ClaimEnrichment {
  /** Unique identifier for the enrichment */
  enrichmentId?: string;
  /** Description of the enrichment */
  description?: string;
  /** Source of enrichment data */
  dataSource?: string;
}

/**
 * Response report configuration for claims
 */
export interface ResponseReports {
  /** Whether 999 acknowledgments are supported */
  functional999Supported?: boolean;
  /** Whether 277CA claim acknowledgments are supported */
  claimAck277CASupported?: boolean;
  /** Expected response timeframe */
  responseTimeframe?: string;
}

/**
 * Claims 837 module configuration
 */
export interface Claims837Module {
  /** Whether the Claims 837 module is enabled */
  enabled?: boolean;
  /** Supported transaction modes */
  transactionModes?: TransactionModes;
  /** Supported claim types */
  claimTypes?: ClaimTypes;
  /** Whether secondary claims are supported */
  secondaryClaims?: boolean;
  /** Whether tertiary claims are supported */
  tertiaryClaims?: boolean;
  /** File structure requirements */
  fileStructure?: FileStructure;
  /** File naming conventions */
  fileNaming?: FileNaming;
  /** Custom claim edits (max 8) */
  edits?: ClaimEdit[];
  /** Custom data enrichments (max 5) */
  enrichments?: ClaimEnrichment[];
  /** Response report configuration */
  responseReports?: ResponseReports;
}

/**
 * Eligibility search options
 */
export enum EligibilitySearchOption {
  MEMBER_ID_DOB = "member_id_dob",
  MEMBER_ID_NAME = "member_id_name",
  SSN_DOB = "ssn_dob",
  SSN_NAME = "ssn_name",
  NAME_DOB_GENDER = "name_dob_gender",
  SUBSCRIBER_ID = "subscriber_id"
}

/**
 * Patient ID format validation configuration
 */
export interface PatientIdFormat {
  /** Regex pattern for patient ID validation */
  pattern?: string;
  /** Minimum length (1-50) */
  minLength?: number;
  /** Maximum length (1-50) */
  maxLength?: number;
}

/**
 * Eligibility 270/271 module configuration
 */
export interface Eligibility270271Module {
  /** Whether the Eligibility 270/271 module is enabled */
  enabled?: boolean;
  /** Supported transaction modes */
  transactionModes?: TransactionModes;
  /** Supported search patterns */
  searchOptions?: EligibilitySearchOption[];
  /** Supported service type codes or 'all' */
  serviceCodes?: string | string[];
  /** Patient ID format validation */
  patientIdFormat?: PatientIdFormat;
  /** Whether gender is required for eligibility requests */
  requiresGender?: boolean;
  /** Whether XML wrapper is used for requests */
  xmlWrapper?: boolean;
}

/**
 * Service date range constraints
 */
export interface ServiceDateRange {
  /** Earliest service date (relative or absolute) */
  earliest?: string;
  /** Latest service date (relative or absolute) */
  latest?: string;
}

/**
 * Claim Status 276/277 module configuration
 */
export interface ClaimStatus276277Module {
  /** Whether the Claim Status 276/277 module is enabled */
  enabled?: boolean;
  /** Supported transaction modes */
  transactionModes?: TransactionModes;
  /** Whether atypical providers are allowed */
  atypicalProvidersAllowed?: boolean;
  /** Default patient account number if not provided */
  patientAccountNumberDefault?: string;
  /** Service date range constraints */
  serviceDateRange?: ServiceDateRange;
}

/**
 * Appeal sub-status values
 */
export enum AppealSubStatus {
  DENIED = "Denied",
  PARTIAL_APPROVAL = "Partial Approval",
  OVERTURNED = "Overturned",
  UPHELD = "Upheld",
  IN_PROGRESS = "In Progress",
  ADDITIONAL_INFO_NEEDED = "Additional Information Needed",
  WITHDRAWN = "Withdrawn",
  CLOSED = "Closed"
}

/**
 * Appeal decision values
 */
export enum AppealDecision {
  APPROVED = "Approved",
  DENIED = "Denied",
  PARTIALLY_APPROVED = "Partially Approved",
  WITHDRAWN = "Withdrawn"
}

/**
 * Appeal decision with reasons
 */
export interface AppealDecisionConfig {
  /** Appeal decision */
  decision: AppealDecision;
  /** Reasons for the decision */
  decisionReasons?: string[];
}

/**
 * Attachment pattern options
 */
export enum AttachmentPattern {
  PRE_APPEAL = "pre-appeal",
  POST_APPEAL = "post-appeal"
}

/**
 * Allowed file types for appeals
 */
export enum AppealsFileType {
  GIF = ".gif",
  JPG = ".jpg",
  JPEG = ".jpeg",
  PDF = ".pdf",
  TIF = ".tif",
  TIFF = ".tiff",
  PNG = ".png",
  DOC = ".doc",
  DOCX = ".docx"
}

/**
 * Required contact information fields for appeals
 */
export interface ContactInfoRequired {
  /** Whether phone is required */
  phone?: boolean;
  /** Whether fax is required */
  fax?: boolean;
  /** Whether email is required */
  email?: boolean;
}

/**
 * Appeals module configuration
 */
export interface AppealsModule {
  /** Whether the Appeals module is enabled */
  enabled?: boolean;
  /** Supported transaction modes (no batch mode for appeals) */
  transactionModes?: Omit<TransactionModes, 'edi_batch'>;
  /** Supported appeal request reasons (1-10) */
  requestReasons?: string[];
  /** Supported appeal sub-statuses */
  subStatuses?: AppealSubStatus[];
  /** Supported appeal decisions and their reasons */
  decisions?: AppealDecisionConfig[];
  /** When attachments can be submitted */
  attachmentPattern?: AttachmentPattern;
  /** Days allowed for additional attachments after initial submission (0-90) */
  additionalAttachmentsWindow?: number;
  /** Maximum file size in bytes (1MB-100MB) */
  maxFileSize?: number;
  /** Maximum number of files per appeal (1-50) */
  maxFilesPerAppeal?: number;
  /** Allowed file extensions */
  allowedFileTypes?: AppealsFileType[];
  /** Required contact information fields */
  contactInfoRequired?: ContactInfoRequired;
  /** Whether submitting provider type is required */
  submittingProviderTypeRequired?: boolean;
  /** Whether provider address is required */
  providerAddressRequired?: boolean;
  /** Whether additional claims can be included */
  additionalClaimsSupported?: boolean;
  /** Maximum number of additional claims (0-25) */
  maxAdditionalClaims?: number;
  /** Whether multi-claim verification is enabled */
  multiClaimVerification?: boolean;
  /** Whether submission on behalf of member is allowed */
  submitOnBehalfOfMember?: boolean;
  /** Whether document download is supported */
  documentDownloadSupported?: boolean;
  /** Whether ECS integration is enabled for appeals */
  ecsIntegrationEnabled?: boolean;
}

/**
 * Required fields for attachment submissions
 */
export enum AttachmentRequiredField {
  CLAIM_NUMBER = "claimNumber",
  MEMBER_NAME = "memberName",
  MEMBER_ID = "memberId",
  DATE_OF_BIRTH = "dateOfBirth",
  PROVIDER_NAME = "providerName",
  PROVIDER_NPI = "providerNPI",
  SERVICE_DATE = "serviceDate",
  ATTACHMENT_TYPE = "attachmentType"
}

/**
 * Allowed file types for attachments
 */
export enum AttachmentsFileType {
  PDF = ".pdf",
  TIF = ".tif",
  TIFF = ".tiff",
  JPG = ".jpg",
  JPEG = ".jpeg",
  PNG = ".png",
  GIF = ".gif"
}

/**
 * Attachments 275 module configuration
 */
export interface Attachments275Module {
  /** Whether the Attachments 275 module is enabled */
  enabled?: boolean;
  /** Supported transaction modes */
  transactionModes?: TransactionModes;
  /** Required fields for attachment submissions */
  requiredFields?: AttachmentRequiredField[];
  /** Allowed file extensions */
  allowedFileTypes?: AttachmentsFileType[];
  /** Maximum file size in bytes (1MB-100MB) */
  maxFileSize?: number;
  /** Maximum number of files per transaction (1-50) */
  maxFilesPerTransaction?: number;
  /** Whether password-protected files are allowed */
  passwordProtectedAllowed?: boolean;
}

/**
 * ECS query methods
 */
export enum ECSQueryMethod {
  SERVICE_DATE = "ServiceDate",
  MEMBER = "Member",
  CHECK_NUMBER = "CheckNumber",
  CLAIM_HISTORY = "ClaimHistory"
}

/**
 * Additional ECS properties
 */
export enum ECSIncludeProperty {
  CLAIM_STATUS = "claimStatus",
  PAYMENT_AMOUNT = "paymentAmount",
  PAYMENT_DATE = "paymentDate",
  CHECK_NUMBER = "checkNumber",
  REMITTANCE_ADVICE = "remittanceAdvice",
  DENIAL_REASON = "denialReason",
  ADJUSTMENT_REASON = "adjustmentReason",
  PAID_TO_PROVIDER = "paidToProvider",
  PATIENT_RESPONSIBILITY = "patientResponsibility"
}

/**
 * Enhanced Claim Status (ECS) module configuration
 */
export interface ECSModule {
  /** Whether the ECS module is enabled */
  enabled?: boolean;
  /** Supported transaction modes (no batch mode for ECS) */
  transactionModes?: Omit<TransactionModes, 'edi_batch'>;
  /** Supported query methods */
  queryMethods?: ECSQueryMethod[];
  /** Additional ECS properties to include in responses */
  includeProperties?: ECSIncludeProperty[];
  /** Whether caching is enabled for ECS responses */
  cacheEnabled?: boolean;
  /** Cache time-to-live in seconds (60-86400) */
  cacheTTL?: number;
}

/**
 * All module configurations
 */
export interface Modules {
  /** Claims 837 module configuration */
  claims837?: Claims837Module;
  /** Eligibility 270/271 module configuration */
  eligibility270271?: Eligibility270271Module;
  /** Claim Status 276/277 module configuration */
  claimStatus276277?: ClaimStatus276277Module;
  /** Appeals module configuration */
  appeals?: AppealsModule;
  /** Attachments 275 module configuration */
  attachments275?: Attachments275Module;
  /** Enhanced Claim Status (ECS) module configuration */
  ecs?: ECSModule;
}

/**
 * Root Availity Integration Configuration
 * 
 * Complete configuration for onboarding a health plan to the Availity integration platform.
 */
export interface AvailityIntegrationConfig {
  /** Health plan organization name */
  organizationName: string;
  /** Primary payer ID for Availity routing */
  payerId: string;
  /** Display name for provider portals */
  payerName: string;
  /** URL to payer logo (234x60px PNG, <20KB) */
  logo?: string;
  /** Trading partner ID for connectivity */
  tradingPartnerId?: string;
  /** Whether deployment is restricted to specific providers */
  controlledDeployment?: boolean;
  /** Contact information for various roles */
  contacts: Contacts;
  /** Geographic coverage configuration */
  geography?: Geography;
  /** ISA/GS segment enveloping configuration */
  enveloping?: Enveloping;
  /** Connectivity configuration for HTTPS/FTP */
  connectivity?: Connectivity;
  /** Testing environment and requirements */
  testing?: Testing;
  /** Character set and encoding requirements */
  characterSet?: CharacterSet;
  /** Error handling and acknowledgment configuration */
  errorHandling?: ErrorHandling;
  /** Configuration for each Availity transaction module */
  modules?: Modules;
}
