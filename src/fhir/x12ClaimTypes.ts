/**
 * X12 837 Professional, Institutional, and Dental Claims
 * Based on HIPAA X12 005010X222, 005010X223, 005010X224 implementation guides
 * Maps to FHIR R4 Claim resource
 */

export interface X12_837_Claim {
  /** Unique claim identifier (CLM segment) */
  claimId: string;
  
  /** Claim type: P=Professional, I=Institutional, D=Dental */
  claimType: 'P' | 'I' | 'D';
  
  /** Total claim charge amount */
  totalChargeAmount: number;
  
  /** Place of service code (POS) */
  placeOfServiceCode?: string;
  
  /** Claim frequency code: 1=Original, 7=Replacement, 8=Void */
  claimFrequencyCode?: string;
  
  /** Bill type code (Institutional only) */
  billTypeCode?: string;
  
  /** Patient information */
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
    phone?: string;
  };
  
  /** Subscriber (if different from patient) */
  subscriber?: {
    memberId: string;
    firstName: string;
    lastName: string;
    middleName?: string;
    dob: string;
    gender?: 'M' | 'F' | 'U';
    relationshipCode?: string; // 18=Self, 01=Spouse, 19=Child
    address?: {
      street1?: string;
      street2?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Billing provider */
  billingProvider: {
    npi: string;
    organizationName?: string;
    taxId?: string;
    address?: {
      street1?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Rendering provider (service provider) */
  renderingProvider?: {
    npi: string;
    firstName?: string;
    lastName?: string;
    taxId?: string;
  };
  
  /** Payer information */
  payer: {
    payerId: string;
    payerName?: string;
    address?: {
      street1?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Service lines */
  serviceLines: Array<{
    lineNumber: number;
    procedureCode: string; // CPT/HCPCS
    procedureModifiers?: string[];
    diagnosisPointers?: number[]; // References to diagnosis codes
    serviceDate: string; // CCYYMMDD or YYYY-MM-DD
    units: number;
    chargeAmount: number;
    placeOfServiceCode?: string;
    emergencyIndicator?: boolean;
    renderingProviderNpi?: string;
  }>;
  
  /** Diagnosis codes (ICD-10) */
  diagnosisCodes: Array<{
    sequence: number;
    code: string;
    type?: 'principal' | 'admitting' | 'other';
  }>;
  
  /** Statement dates */
  statementDates?: {
    fromDate: string;
    toDate?: string;
  };
  
  /** Admission information (Institutional) */
  admissionInfo?: {
    admissionDate?: string;
    admissionTypeCode?: string;
    admissionSourceCode?: string;
    patientStatusCode?: string;
  };
  
  /** Reference numbers */
  referenceNumbers?: {
    priorAuthorizationNumber?: string;
    referralNumber?: string;
    claimControlNumber?: string;
  };
}

/**
 * X12 278 Health Care Services Review Information (Prior Authorization)
 * Based on HIPAA X12 005010X217 implementation guide
 * Maps to FHIR R4 ServiceRequest/CoverageEligibilityRequest for Prior Authorization
 */
export interface X12_278_Request {
  /** Unique transaction identifier */
  transactionId: string;
  
  /** Review type: AR=Authorization Request, AI=Authorization Inquiry */
  reviewType: 'AR' | 'AI';
  
  /** Certification type: I=Initial, R=Renewal, S=Revised */
  certificationType?: 'I' | 'R' | 'S';
  
  /** Service type code */
  serviceTypeCode: string;
  
  /** Level of service: U=Urgent, E=Elective */
  levelOfService?: 'U' | 'E';
  
  /** Patient information */
  patient: {
    memberId: string;
    firstName: string;
    lastName: string;
    middleName?: string;
    dob: string;
    gender?: 'M' | 'F' | 'U';
  };
  
  /** Subscriber (if different from patient) */
  subscriber?: {
    memberId: string;
    firstName: string;
    lastName: string;
    relationshipCode?: string;
  };
  
  /** Requesting provider */
  requestingProvider: {
    npi: string;
    firstName?: string;
    lastName?: string;
    organizationName?: string;
    taxId?: string;
  };
  
  /** Servicing provider (if different) */
  servicingProvider?: {
    npi: string;
    firstName?: string;
    lastName?: string;
    organizationName?: string;
    taxonomyCode?: string;
  };
  
  /** Payer information */
  payer: {
    payerId: string;
    payerName?: string;
  };
  
  /** Services requested for authorization */
  requestedServices: Array<{
    serviceTypeCode: string;
    procedureCode?: string; // CPT/HCPCS
    procedureModifiers?: string[];
    diagnosisCode?: string; // ICD-10
    quantity?: number;
    measurementUnit?: string;
    serviceDate?: string;
    serviceDateRange?: {
      startDate: string;
      endDate?: string;
    };
    placeOfServiceCode?: string;
    requestedAmount?: number;
  }>;
  
  /** Diagnosis codes */
  diagnosisCodes?: Array<{
    code: string;
    type?: 'principal' | 'admitting' | 'other';
  }>;
  
  /** Admission information */
  admissionInfo?: {
    admissionDate?: string;
    dischargeDate?: string;
    admissionTypeCode?: string;
    lengthOfStay?: number;
  };
  
  /** Reference numbers */
  referenceNumbers?: {
    priorAuthorizationNumber?: string;
    referralNumber?: string;
    traceNumber?: string;
  };
  
  /** Additional information */
  additionalInfo?: {
    attachmentControlNumber?: string;
    attachmentTransmissionCode?: string;
    description?: string;
  };
}

/**
 * X12 835 Health Care Claim Payment/Advice (Remittance)
 * Based on HIPAA X12 005010X221 implementation guide
 * Maps to FHIR R4 ExplanationOfBenefit resource
 */
export interface X12_835_Remittance {
  /** Transaction set control number */
  transactionId: string;
  
  /** Payer information */
  payer: {
    payerId: string;
    payerName?: string;
    address?: {
      street1?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
    contactInfo?: {
      name?: string;
      phone?: string;
      email?: string;
    };
  };
  
  /** Payee (provider receiving payment) */
  payee: {
    npi: string;
    organizationName?: string;
    taxId?: string;
    address?: {
      street1?: string;
      city?: string;
      state?: string;
      zip?: string;
    };
  };
  
  /** Payment information */
  payment: {
    paymentMethodCode: string; // ACH, CHK, NON
    paymentAmount: number;
    paymentDate: string; // CCYYMMDD or YYYY-MM-DD
    checkOrEftNumber?: string;
    traceNumber?: string;
  };
  
  /** Claim payment information */
  claims: Array<{
    claimId: string;
    patientAccountNumber?: string;
    
    /** Patient information */
    patient: {
      memberId: string;
      firstName: string;
      lastName: string;
      dob?: string;
    };
    
    /** Claim level amounts */
    claimAmounts: {
      billedAmount: number;
      allowedAmount?: number;
      paidAmount: number;
      patientResponsibility?: number;
      deductible?: number;
      coinsurance?: number;
      copay?: number;
      contractualAdjustment?: number;
    };
    
    /** Claim status code */
    claimStatusCode: string; // 1=Processed as Primary, 2=Processed as Secondary, etc.
    
    /** Claim dates */
    claimDates: {
      statementFromDate: string;
      statementToDate?: string;
      receivedDate?: string;
      processedDate?: string;
    };
    
    /** Service lines */
    serviceLines: Array<{
      lineNumber: number;
      procedureCode: string;
      procedureModifiers?: string[];
      serviceDate: string;
      units: number;
      
      /** Service line amounts */
      amounts: {
        billedAmount: number;
        allowedAmount?: number;
        paidAmount: number;
        deductible?: number;
        coinsurance?: number;
        copay?: number;
      };
      
      /** Adjustment codes and amounts */
      adjustments?: Array<{
        groupCode: string; // CO=Contractual, PR=Patient Responsibility, OA=Other
        reasonCode: string;
        amount: number;
        quantity?: number;
      }>;
      
      /** Remark codes */
      remarkCodes?: string[];
    }>;
    
    /** Claim level adjustments */
    claimAdjustments?: Array<{
      groupCode: string;
      reasonCode: string;
      amount: number;
    }>;
    
    /** Claim level remark codes */
    remarkCodes?: string[];
    
    /** Reference numbers */
    referenceNumbers?: {
      priorAuthorizationNumber?: string;
      referralNumber?: string;
      payerClaimControlNumber?: string;
    };
  }>;
  
  /** Summary information */
  summary?: {
    numberOfClaims: number;
    totalBilledAmount: number;
    totalPaidAmount: number;
    interestAmount?: number;
  };
}
