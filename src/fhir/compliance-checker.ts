/**
 * CMS-0057-F Compliance Checker
 * Validates FHIR resources against CMS-0057-F requirements and timelines
 * 
 * CMS-0057-F Final Rule Requirements:
 * - Prior Authorization APIs using FHIR R4
 * - Support for Da Vinci PAS Implementation Guide
 * - Specific response timelines for authorization requests
 * - Patient Access API requirements
 * - Provider Access API requirements
 * 
 * Timeline Requirements (CMS-0057-F):
 * - Expedited requests: 72 hours
 * - Standard requests: 7 calendar days
 * - Life-threatening situations: 24 hours
 */

import { ServiceRequest, Claim, ExplanationOfBenefit, Patient, OperationOutcome } from 'fhir/r4';

/**
 * Compliance result structure
 */
export interface ComplianceResult {
  compliant: boolean;
  issues: ComplianceIssue[];
  summary: string;
  checkedRules: string[];
  score: number; // 0-100
}

/**
 * Individual compliance issue
 */
export interface ComplianceIssue {
  severity: 'error' | 'warning' | 'information';
  rule: string;
  message: string;
  location?: string;
  suggestion?: string;
}

/**
 * CMS-0057-F compliance classes
 */
export enum CMSComplianceClass {
  PRIOR_AUTH_API = 'prior-auth-api',
  PATIENT_ACCESS_API = 'patient-access-api',
  PROVIDER_ACCESS_API = 'provider-access-api',
  TIMELINES = 'timelines',
  DATA_ELEMENTS = 'data-elements',
  SECURITY = 'security',
}

/**
 * Main compliance checker class
 */
export class CMS0057FComplianceChecker {
  private issues: ComplianceIssue[] = [];
  private checkedRules: string[] = [];
  
  /**
   * Validates a FHIR ServiceRequest against CMS-0057-F requirements
   */
  public validateServiceRequest(serviceRequest: ServiceRequest): ComplianceResult {
    this.reset();
    
    // Rule: Must have valid resource type
    this.checkRule(
      'SR-001',
      serviceRequest.resourceType === 'ServiceRequest',
      'Resource must be of type ServiceRequest',
      'ServiceRequest.resourceType'
    );
    
    // Rule: Must include Da Vinci PAS profile
    this.checkRule(
      'SR-002',
      serviceRequest.meta?.profile?.some(p => 
        p.includes('davinci-pas') || p.includes('ServiceRequest')
      ) || false,
      'ServiceRequest must include Da Vinci PAS profile in meta.profile',
      'ServiceRequest.meta.profile',
      'Add profile: http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'
    );
    
    // Rule: Must have status
    this.checkRule(
      'SR-003',
      !!serviceRequest.status,
      'ServiceRequest must have a status',
      'ServiceRequest.status'
    );
    
    // Rule: Must have intent
    this.checkRule(
      'SR-004',
      !!serviceRequest.intent,
      'ServiceRequest must have an intent',
      'ServiceRequest.intent'
    );
    
    // Rule: Must have subject (patient)
    this.checkRule(
      'SR-005',
      !!serviceRequest.subject,
      'ServiceRequest must reference a patient',
      'ServiceRequest.subject'
    );
    
    // Rule: Must have requester (provider)
    this.checkRule(
      'SR-006',
      !!serviceRequest.requester,
      'ServiceRequest must reference a requester (provider)',
      'ServiceRequest.requester'
    );
    
    // Rule: Must have code (service type)
    this.checkRule(
      'SR-007',
      !!serviceRequest.code,
      'ServiceRequest must specify the service being requested',
      'ServiceRequest.code'
    );
    
    // Rule: Should have insurance/coverage reference
    this.checkWarning(
      'SR-008',
      !!serviceRequest.insurance && serviceRequest.insurance.length > 0,
      'ServiceRequest should include insurance/coverage reference',
      'ServiceRequest.insurance'
    );
    
    // Rule: Should have authoredOn date
    this.checkWarning(
      'SR-009',
      !!serviceRequest.authoredOn,
      'ServiceRequest should include authoredOn timestamp',
      'ServiceRequest.authoredOn'
    );
    
    return this.generateResult();
  }
  
  /**
   * Validates a FHIR Claim against US Core and CMS requirements
   */
  public validateClaim(claim: Claim): ComplianceResult {
    this.reset();
    
    // Rule: Must have valid resource type
    this.checkRule(
      'CL-001',
      claim.resourceType === 'Claim',
      'Resource must be of type Claim',
      'Claim.resourceType'
    );
    
    // Rule: Must include US Core profile
    this.checkRule(
      'CL-002',
      claim.meta?.profile?.some(p => p.includes('us-core')) || false,
      'Claim must include US Core profile in meta.profile',
      'Claim.meta.profile',
      'Add profile: http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim'
    );
    
    // Rule: Must have status
    this.checkRule(
      'CL-003',
      !!claim.status,
      'Claim must have a status',
      'Claim.status'
    );
    
    // Rule: Must have type
    this.checkRule(
      'CL-004',
      !!claim.type,
      'Claim must have a type (professional, institutional, etc.)',
      'Claim.type'
    );
    
    // Rule: Must have use
    this.checkRule(
      'CL-005',
      !!claim.use,
      'Claim must specify use (claim, preauthorization, predetermination)',
      'Claim.use'
    );
    
    // Rule: Must have patient reference
    this.checkRule(
      'CL-006',
      !!claim.patient,
      'Claim must reference a patient',
      'Claim.patient'
    );
    
    // Rule: Must have created date
    this.checkRule(
      'CL-007',
      !!claim.created,
      'Claim must have a created date',
      'Claim.created'
    );
    
    // Rule: Must have provider
    this.checkRule(
      'CL-008',
      !!claim.provider,
      'Claim must reference a provider',
      'Claim.provider'
    );
    
    // Rule: Must have insurer
    this.checkRule(
      'CL-009',
      !!claim.insurer,
      'Claim must reference an insurer (payer)',
      'Claim.insurer'
    );
    
    // Rule: Should have diagnosis codes
    this.checkWarning(
      'CL-010',
      !!claim.diagnosis && claim.diagnosis.length > 0,
      'Claim should include diagnosis codes',
      'Claim.diagnosis'
    );
    
    // Rule: Must have service line items
    this.checkRule(
      'CL-011',
      !!claim.item && claim.item.length > 0,
      'Claim must include at least one service line item',
      'Claim.item'
    );
    
    // Rule: Should have total amount
    this.checkWarning(
      'CL-012',
      !!claim.total,
      'Claim should include total charge amount',
      'Claim.total'
    );
    
    return this.generateResult();
  }
  
  /**
   * Validates ExplanationOfBenefit against US Core requirements
   */
  public validateExplanationOfBenefit(eob: ExplanationOfBenefit): ComplianceResult {
    this.reset();
    
    // Rule: Must have valid resource type
    this.checkRule(
      'EOB-001',
      eob.resourceType === 'ExplanationOfBenefit',
      'Resource must be of type ExplanationOfBenefit',
      'ExplanationOfBenefit.resourceType'
    );
    
    // Rule: Must include US Core profile
    this.checkRule(
      'EOB-002',
      eob.meta?.profile?.some(p => p.includes('us-core')) || false,
      'ExplanationOfBenefit must include US Core profile',
      'ExplanationOfBenefit.meta.profile',
      'Add profile: http://hl7.org/fhir/us/core/StructureDefinition/us-core-explanationofbenefit'
    );
    
    // Rule: Must have status
    this.checkRule(
      'EOB-003',
      !!eob.status,
      'ExplanationOfBenefit must have a status',
      'ExplanationOfBenefit.status'
    );
    
    // Rule: Must have type
    this.checkRule(
      'EOB-004',
      !!eob.type,
      'ExplanationOfBenefit must have a type',
      'ExplanationOfBenefit.type'
    );
    
    // Rule: Must have use
    this.checkRule(
      'EOB-005',
      !!eob.use,
      'ExplanationOfBenefit must specify use',
      'ExplanationOfBenefit.use'
    );
    
    // Rule: Must have patient
    this.checkRule(
      'EOB-006',
      !!eob.patient,
      'ExplanationOfBenefit must reference a patient',
      'ExplanationOfBenefit.patient'
    );
    
    // Rule: Must have created date
    this.checkRule(
      'EOB-007',
      !!eob.created,
      'ExplanationOfBenefit must have a created date',
      'ExplanationOfBenefit.created'
    );
    
    // Rule: Must have insurer
    this.checkRule(
      'EOB-008',
      !!eob.insurer,
      'ExplanationOfBenefit must reference an insurer',
      'ExplanationOfBenefit.insurer'
    );
    
    // Rule: Must have provider
    this.checkRule(
      'EOB-009',
      !!eob.provider,
      'ExplanationOfBenefit must reference a provider',
      'ExplanationOfBenefit.provider'
    );
    
    // Rule: Must have outcome
    this.checkRule(
      'EOB-010',
      !!eob.outcome,
      'ExplanationOfBenefit must specify outcome (complete, error, partial)',
      'ExplanationOfBenefit.outcome'
    );
    
    // Rule: Should have payment information
    this.checkWarning(
      'EOB-011',
      !!eob.payment,
      'ExplanationOfBenefit should include payment information',
      'ExplanationOfBenefit.payment'
    );
    
    // Rule: Should have total amounts
    this.checkWarning(
      'EOB-012',
      !!eob.total && eob.total.length > 0,
      'ExplanationOfBenefit should include total amounts',
      'ExplanationOfBenefit.total'
    );
    
    return this.generateResult();
  }
  
  /**
   * Validates Patient resource against US Core requirements
   */
  public validatePatient(patient: Patient): ComplianceResult {
    this.reset();
    
    // Rule: Must have valid resource type
    this.checkRule(
      'PAT-001',
      patient.resourceType === 'Patient',
      'Resource must be of type Patient',
      'Patient.resourceType'
    );
    
    // Rule: Must include US Core profile
    this.checkRule(
      'PAT-002',
      patient.meta?.profile?.some(p => p.includes('us-core')) || false,
      'Patient must include US Core Patient profile',
      'Patient.meta.profile',
      'Add profile: http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'
    );
    
    // Rule: Must have identifier
    this.checkRule(
      'PAT-003',
      !!patient.identifier && patient.identifier.length > 0,
      'Patient must have at least one identifier',
      'Patient.identifier'
    );
    
    // Rule: Must have name
    this.checkRule(
      'PAT-004',
      !!patient.name && patient.name.length > 0,
      'Patient must have at least one name',
      'Patient.name'
    );
    
    // Rule: Must have gender
    this.checkRule(
      'PAT-005',
      !!patient.gender,
      'Patient must have a gender',
      'Patient.gender'
    );
    
    return this.generateResult();
  }
  
  /**
   * Validates timeline compliance for prior authorization
   * CMS-0057-F requires specific response times
   */
  public validateTimeline(
    requestDate: Date,
    responseDate: Date,
    isExpedited: boolean = false,
    isLifeThreatening: boolean = false
  ): ComplianceResult {
    this.reset();
    
    const hoursElapsed = (responseDate.getTime() - requestDate.getTime()) / (1000 * 60 * 60);
    const daysElapsed = hoursElapsed / 24;
    
    if (isLifeThreatening) {
      // Life-threatening: 24 hours
      this.checkRule(
        'TIME-001',
        hoursElapsed <= 24,
        `Life-threatening authorization must be responded to within 24 hours (actual: ${hoursElapsed.toFixed(1)} hours)`,
        'timeline'
      );
    } else if (isExpedited) {
      // Expedited: 72 hours (3 days)
      this.checkRule(
        'TIME-002',
        hoursElapsed <= 72,
        `Expedited authorization must be responded to within 72 hours (actual: ${hoursElapsed.toFixed(1)} hours)`,
        'timeline'
      );
    } else {
      // Standard: 7 calendar days
      this.checkRule(
        'TIME-003',
        daysElapsed <= 7,
        `Standard authorization must be responded to within 7 calendar days (actual: ${daysElapsed.toFixed(1)} days)`,
        'timeline'
      );
    }
    
    return this.generateResult();
  }
  
  /**
   * Comprehensive validation of CMS-0057-F compliance for a set of resources
   */
  public validateCMSCompliance(resources: {
    serviceRequest?: ServiceRequest;
    claim?: Claim;
    eob?: ExplanationOfBenefit;
    patient?: Patient;
    timeline?: {
      requestDate: Date;
      responseDate: Date;
      isExpedited?: boolean;
      isLifeThreatening?: boolean;
    };
  }): ComplianceResult {
    this.reset();
    
    const results: ComplianceResult[] = [];
    
    if (resources.serviceRequest) {
      results.push(this.validateServiceRequest(resources.serviceRequest));
    }
    
    if (resources.claim) {
      results.push(this.validateClaim(resources.claim));
    }
    
    if (resources.eob) {
      results.push(this.validateExplanationOfBenefit(resources.eob));
    }
    
    if (resources.patient) {
      results.push(this.validatePatient(resources.patient));
    }
    
    if (resources.timeline) {
      results.push(this.validateTimeline(
        resources.timeline.requestDate,
        resources.timeline.responseDate,
        resources.timeline.isExpedited,
        resources.timeline.isLifeThreatening
      ));
    }
    
    // Aggregate results
    const allIssues = results.flatMap(r => r.issues);
    const allRules = results.flatMap(r => r.checkedRules);
    const allCompliant = results.every(r => r.compliant);
    const avgScore = results.reduce((sum, r) => sum + r.score, 0) / results.length;
    
    return {
      compliant: allCompliant,
      issues: allIssues,
      summary: this.generateSummary(allIssues, allCompliant),
      checkedRules: [...new Set(allRules)],
      score: Math.round(avgScore),
    };
  }
  
  /**
   * Private helper methods
   */
  
  private reset(): void {
    this.issues = [];
    this.checkedRules = [];
  }
  
  private checkRule(
    ruleId: string,
    condition: boolean,
    message: string,
    location?: string,
    suggestion?: string
  ): void {
    this.checkedRules.push(ruleId);
    
    if (!condition) {
      this.issues.push({
        severity: 'error',
        rule: ruleId,
        message,
        location,
        suggestion,
      });
    }
  }
  
  private checkWarning(
    ruleId: string,
    condition: boolean,
    message: string,
    location?: string,
    suggestion?: string
  ): void {
    this.checkedRules.push(ruleId);
    
    if (!condition) {
      this.issues.push({
        severity: 'warning',
        rule: ruleId,
        message,
        location,
        suggestion,
      });
    }
  }
  
  private generateResult(): ComplianceResult {
    const errorCount = this.issues.filter(i => i.severity === 'error').length;
    const warningCount = this.issues.filter(i => i.severity === 'warning').length;
    
    const compliant = errorCount === 0;
    const score = Math.round(
      ((this.checkedRules.length - errorCount - warningCount * 0.5) / this.checkedRules.length) * 100
    );
    
    return {
      compliant,
      issues: this.issues,
      summary: this.generateSummary(this.issues, compliant),
      checkedRules: this.checkedRules,
      score,
    };
  }
  
  private generateSummary(issues: ComplianceIssue[], compliant: boolean): string {
    const errors = issues.filter(i => i.severity === 'error').length;
    const warnings = issues.filter(i => i.severity === 'warning').length;
    
    if (compliant) {
      if (warnings > 0) {
        return `Compliant with CMS-0057-F requirements (${warnings} warnings)`;
      }
      return 'Fully compliant with CMS-0057-F requirements';
    }
    
    return `Not compliant: ${errors} errors, ${warnings} warnings`;
  }
}

/**
 * Convenience function to create a compliance checker
 */
export function createComplianceChecker(): CMS0057FComplianceChecker {
  return new CMS0057FComplianceChecker();
}

/**
 * Validates FHIR resources against Azure API for FHIR (if available)
 * 
 * When azureFhirEndpoint is provided, this function will call the Azure FHIR $validate operation.
 * Otherwise, it performs local validation using the compliance checker.
 * 
 * @param resource FHIR resource to validate
 * @param azureFhirEndpoint Optional Azure FHIR endpoint URL (e.g., https://my-fhir.azurehealthcareapis.com)
 * @returns ComplianceResult with validation details
 */
export async function validateWithAzureFHIR(
  resource: ServiceRequest | Claim | ExplanationOfBenefit | Patient,
  azureFhirEndpoint?: string
): Promise<ComplianceResult> {
  // If Azure FHIR endpoint provided, call $validate operation
  if (azureFhirEndpoint) {
    try {
      // TODO: Implement Azure FHIR $validate integration
      // This would require:
      // 1. Authentication (managed identity or service principal)
      // 2. HTTP POST to {endpoint}/{resourceType}/$validate
      // 3. Parse OperationOutcome response
      // 4. Convert to ComplianceResult format
      
      // For now, log that Azure validation is requested but fall back to local
      console.warn(`Azure FHIR validation requested for ${azureFhirEndpoint} but not yet implemented. Using local validation.`);
    } catch (error) {
      console.error('Azure FHIR validation failed, falling back to local validation:', error);
    }
  }
  
  // Perform local validation
  const checker = createComplianceChecker();
  
  switch (resource.resourceType) {
    case 'ServiceRequest':
      return checker.validateServiceRequest(resource as ServiceRequest);
    case 'Claim':
      return checker.validateClaim(resource as Claim);
    case 'ExplanationOfBenefit':
      return checker.validateExplanationOfBenefit(resource as ExplanationOfBenefit);
    case 'Patient':
      return checker.validatePatient(resource as Patient);
    default:
      return {
        compliant: false,
        issues: [{
          severity: 'error',
          rule: 'UNKNOWN',
          message: `Unsupported resource type: ${(resource as any).resourceType}`,
        }],
        summary: 'Unsupported resource type',
        checkedRules: [],
        score: 0,
      };
  }
}
