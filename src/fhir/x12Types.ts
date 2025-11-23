/**
 * Comprehensive X12 270 EDI Eligibility Inquiry structure
 * Based on HIPAA X12 005010X279A1 implementation guide
 * Maps to FHIR R4 Patient and EligibilityRequest resources
 */
export interface X12_270 {
  /** Unique identifier for the eligibility inquiry transaction */
  inquiryId: string;
  
  /** Interchange control number for tracking */
  interchangeControlNumber?: string;
  
  /** Transaction date and time (CCYYMMDD-HHMM) */
  transactionDate?: string;
  
  /** Information Source (Payer/Health Plan) */
  informationSource: {
    /** National Provider Identifier (NPI) or other identifier */
    id: string;
    /** Organization name */
    name?: string;
    /** Tax ID (EIN) */
    taxId?: string;
  };
  
  /** Information Receiver (Provider requesting eligibility) */
  informationReceiver?: {
    /** National Provider Identifier (NPI) */
    npi?: string;
    /** Provider organization name */
    organizationName?: string;
    /** Tax ID */
    taxId?: string;
  };
  
  /** Subscriber (may be same as member or different for dependents) */
  subscriber: {
    /** Member ID assigned by payer */
    memberId: string;
    /** First name */
    firstName: string;
    /** Last name */
    lastName: string;
    /** Middle name or initial */
    middleName?: string;
    /** Date of birth (CCYYMMDD or YYYY-MM-DD) */
    dob: string;
    /** Gender code: M=Male, F=Female, U=Unknown */
    gender?: 'M' | 'F' | 'U' | 'male' | 'female' | 'unknown' | 'other';
    /** Social Security Number (for matching, not stored) */
    ssn?: string;
    /** Address information */
    address?: {
      street1?: string;
      street2?: string;
      city?: string;
      state?: string;
      zip?: string;
      country?: string;
    };
    /** Contact information */
    phone?: string;
    email?: string;
  };
  
  /** Dependent/Patient (if different from subscriber) */
  dependent?: {
    /** Relationship to subscriber: 01=Spouse, 19=Child, etc. */
    relationshipCode?: string;
    /** First name */
    firstName: string;
    /** Last name */
    lastName: string;
    /** Middle name */
    middleName?: string;
    /** Date of birth */
    dob: string;
    /** Gender */
    gender?: 'M' | 'F' | 'U' | 'male' | 'female' | 'unknown' | 'other';
  };
  
  /** Service type codes being inquired about */
  serviceTypeCodes?: string[];
  
  /** Date range for eligibility inquiry */
  serviceDateRange?: {
    startDate?: string;
    endDate?: string;
  };
  
  /** Additional eligibility inquiry details */
  inquiryDetails?: {
    /** Certification type */
    certificationType?: string;
    /** Service type description */
    serviceTypeDescription?: string;
  };
  
  /** Payer/Health Plan identifier */
  insurerId: string;
  
  /** Trading partner ID (e.g., Availity sender ID) */
  tradingPartnerId?: string;
}