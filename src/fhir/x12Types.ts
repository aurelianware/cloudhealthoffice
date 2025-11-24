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

/**
 * X12 837 Professional Claim structure
 * Based on HIPAA X12 005010X222A1 implementation guide
 * Maps to FHIR R4 Claim resource
 */
export interface X12_837 {
  /** Unique claim identifier */
  claimId: string;
  
  /** Interchange control number */
  interchangeControlNumber?: string;
  
  /** Transaction date (CCYYMMDD) */
  transactionDate?: string;
  
  /** Billing provider information */
  billingProvider: {
    /** National Provider Identifier (NPI) */
    npi: string;
    /** Organization or individual name */
    name: string;
    /** Tax ID (EIN) */
    taxId?: string;
    /** Address */
    address?: {
      street1?: string;
      street2?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Subscriber/Patient information */
  subscriber: {
    /** Member ID */
    memberId: string;
    /** First name */
    firstName: string;
    /** Last name */
    lastName: string;
    /** Middle name */
    middleName?: string;
    /** Date of birth (CCYYMMDD or YYYY-MM-DD) */
    dob: string;
    /** Gender: M=Male, F=Female */
    gender?: 'M' | 'F' | 'U';
    /** Address */
    address?: {
      street1?: string;
      street2?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Patient (if different from subscriber) */
  patient?: {
    /** Relationship to subscriber */
    relationshipCode?: string;
    /** First name */
    firstName: string;
    /** Last name */
    lastName: string;
    /** Date of birth */
    dob: string;
    /** Gender */
    gender?: 'M' | 'F' | 'U';
  };
  
  /** Payer/Insurer information */
  payer: {
    /** Payer ID */
    payerId: string;
    /** Payer name */
    name?: string;
  };
  
  /** Claim information */
  claim: {
    /** Total charge amount */
    totalChargeAmount: number;
    /** Service facility location */
    facilityCode?: string;
    /** Place of service code */
    placeOfServiceCode?: string;
    /** Claim frequency code */
    claimFrequencyCode?: string;
    /** Service lines */
    serviceLines: Array<{
      /** Line number */
      lineNumber: number;
      /** Procedure code (CPT/HCPCS) */
      procedureCode: string;
      /** Procedure modifiers */
      modifiers?: string[];
      /** Service date (from) */
      serviceDateFrom: string;
      /** Service date (to) */
      serviceDateTo?: string;
      /** Charge amount */
      chargeAmount: number;
      /** Units */
      units?: number;
      /** Diagnosis pointers */
      diagnosisPointers?: number[];
    }>;
  };
  
  /** Diagnosis codes */
  diagnoses?: Array<{
    /** Sequence number */
    sequence: number;
    /** ICD-10 code */
    code: string;
  }>;
}

/**
 * X12 278 Prior Authorization Request structure
 * Based on HIPAA X12 005010X217 implementation guide
 * Maps to FHIR R4 ServiceRequest resource
 */
export interface X12_278 {
  /** Unique authorization request identifier */
  authorizationId: string;
  
  /** Transaction date and time */
  transactionDate?: string;
  
  /** Requester (Provider) information */
  requester: {
    /** National Provider Identifier (NPI) */
    npi: string;
    /** Provider name */
    name?: string;
    /** Organization name */
    organizationName?: string;
  };
  
  /** Subscriber/Patient information */
  subscriber: {
    /** Member ID */
    memberId: string;
    /** First name */
    firstName: string;
    /** Last name */
    lastName: string;
    /** Date of birth */
    dob: string;
    /** Gender */
    gender?: 'M' | 'F' | 'U';
  };
  
  /** Patient (if different from subscriber) */
  patient?: {
    /** Relationship to subscriber */
    relationshipCode?: string;
    /** First name */
    firstName: string;
    /** Last name */
    lastName: string;
    /** Date of birth */
    dob: string;
    /** Gender */
    gender?: 'M' | 'F' | 'U';
  };
  
  /** Payer information */
  payer: {
    /** Payer ID */
    payerId: string;
    /** Payer name */
    name?: string;
  };
  
  /** Service being requested */
  serviceRequest: {
    /** Service type code */
    serviceTypeCode: string;
    /** Certification type (AR=Admission Review, HS=Health Services Review) */
    certificationType: string;
    /** Service date (from) */
    serviceDateFrom: string;
    /** Service date (to) */
    serviceDateTo?: string;
    /** Procedure code */
    procedureCode?: string;
    /** Diagnosis code */
    diagnosisCode?: string;
    /** Requested units */
    units?: number;
    /** Admission type (for inpatient) */
    admissionType?: string;
  };
}

/**
 * X12 835 Electronic Remittance Advice structure
 * Based on HIPAA X12 005010X221A1 implementation guide
 * Maps to FHIR R4 ExplanationOfBenefit resource
 */
export interface X12_835 {
  /** Unique remittance identifier */
  remittanceId: string;
  
  /** Transaction date (CCYYMMDD) */
  transactionDate: string;
  
  /** Payer information */
  payer: {
    /** Payer ID */
    payerId: string;
    /** Payer name */
    name: string;
    /** Address */
    address?: {
      street1?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Payee (Provider) information */
  payee: {
    /** National Provider Identifier (NPI) */
    npi: string;
    /** Provider name */
    name: string;
    /** Tax ID */
    taxId?: string;
  };
  
  /** Claim payment information */
  claims: Array<{
    /** Claim ID (Patient Control Number) */
    claimId: string;
    
    /** Patient information */
    patient: {
      /** Member ID */
      memberId: string;
      /** First name */
      firstName: string;
      /** Last name */
      lastName: string;
    };
    
    /** Claim status code */
    claimStatusCode: string;
    
    /** Total claim charge amount */
    totalChargeAmount: number;
    
    /** Total claim paid amount */
    totalPaidAmount: number;
    
    /** Patient responsibility amount */
    patientResponsibilityAmount?: number;
    
    /** Service date (from) */
    serviceDateFrom: string;
    
    /** Service date (to) */
    serviceDateTo?: string;
    
    /** Service lines */
    serviceLines: Array<{
      /** Line number */
      lineNumber: number;
      /** Procedure code */
      procedureCode: string;
      /** Procedure modifiers */
      modifiers?: string[];
      /** Service date */
      serviceDate: string;
      /** Line charge amount */
      chargeAmount: number;
      /** Line paid amount */
      paidAmount: number;
      /** Allowed amount */
      allowedAmount?: number;
      /** Adjustment codes and amounts */
      adjustments?: Array<{
        /** Adjustment group code (CO, PR, OA, PI) */
        groupCode: string;
        /** Adjustment reason code */
        reasonCode: string;
        /** Adjustment amount */
        amount: number;
      }>;
    }>;
    
    /** Claim-level adjustments */
    claimAdjustments?: Array<{
      /** Adjustment group code */
      groupCode: string;
      /** Adjustment reason code */
      reasonCode: string;
      /** Adjustment amount */
      amount: number;
    }>;
  }>;
}