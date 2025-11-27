/**
 * CMS-0057-F Compliance Checker
 * 
 * Validates FHIR resources against CMS-0057-F Prior Authorization Rule requirements:
 * - Data class validation (USCDI v1/v2)
 * - Timeline requirements (response times)
 * - Da Vinci IG conformance (PDex, CRD, DTR, PAS)
 * - US Core IG conformance
 * 
 * References:
 * - CMS-0057-F: Advancing Interoperability and Improving Prior Authorization Processes (March 2023)
 * - USCDI v1 & v2: United States Core Data for Interoperability
 * - Da Vinci PDex: Payer Data Exchange Implementation Guide
 * - Da Vinci PAS: Prior Authorization Support Implementation Guide
 * - Da Vinci CRD: Coverage Requirements Discovery
 * - Da Vinci DTR: Documentation Templates and Rules
 * - US Core IG v3.1.1+
 */

import { Resource, ServiceRequest, ExplanationOfBenefit, Claim, Patient } from 'fhir/r4';

export interface ComplianceResult {
  compliant: boolean;
  issues: ComplianceIssue[];
  warnings: ComplianceWarning[];
  summary: ComplianceSummary;
}

export interface ComplianceIssue {
  severity: 'error' | 'warning' | 'info';
  code: string;
  message: string;
  location?: string;
  requirement?: string;
}

export interface ComplianceWarning {
  code: string;
  message: string;
  recommendation?: string;
}

export interface ComplianceSummary {
  resourceType: string;
  requiredElementsPresent: number;
  totalRequiredElements: number;
  usCoreSections: string[];
  daVinciProfiles: string[];
  uscdiDataClasses: string[];
  timelineCompliance: TimelineCompliance;
}

export interface TimelineCompliance {
  applicable: boolean;
  requirement?: string;
  deadline?: string;
  compliant?: boolean;
}

/**
 * Main compliance validation function
 * Validates any FHIR resource against CMS-0057-F requirements
 */
export function validateCMS0057FCompliance(resource: Resource): ComplianceResult {
  const issues: ComplianceIssue[] = [];
  const warnings: ComplianceWarning[] = [];
  
  // Resource-specific validation
  switch (resource.resourceType) {
    case 'ServiceRequest':
      return validateServiceRequest(resource as ServiceRequest);
    case 'ExplanationOfBenefit':
      return validateExplanationOfBenefit(resource as ExplanationOfBenefit);
    case 'Claim':
      return validateClaim(resource as Claim);
    case 'Patient':
      return validatePatient(resource as Patient);
    default:
      issues.push({
        severity: 'warning',
        code: 'UNSUPPORTED_RESOURCE',
        message: `Resource type ${resource.resourceType} is not specifically validated for CMS-0057-F`
      });
  }
  
  return {
    compliant: issues.filter(i => i.severity === 'error').length === 0,
    issues,
    warnings,
    summary: {
      resourceType: resource.resourceType || 'Unknown',
      requiredElementsPresent: 0,
      totalRequiredElements: 0,
      usCoreSections: [],
      daVinciProfiles: [],
      uscdiDataClasses: [],
      timelineCompliance: { applicable: false }
    }
  };
}

/**
 * Validate ServiceRequest for Prior Authorization (Da Vinci PAS)
 */
function validateServiceRequest(resource: ServiceRequest): ComplianceResult {
  const issues: ComplianceIssue[] = [];
  const warnings: ComplianceWarning[] = [];
  const uscdiClasses: string[] = [];
  let requiredPresent = 0;
  const totalRequired = 10;
  
  // Required elements for Da Vinci PAS
  if (!resource.status) {
    issues.push({
      severity: 'error',
      code: 'MISSING_STATUS',
      message: 'ServiceRequest.status is required',
      requirement: 'Da Vinci PAS'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.intent) {
    issues.push({
      severity: 'error',
      code: 'MISSING_INTENT',
      message: 'ServiceRequest.intent is required',
      requirement: 'Da Vinci PAS'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.subject) {
    issues.push({
      severity: 'error',
      code: 'MISSING_SUBJECT',
      message: 'ServiceRequest.subject (patient reference) is required',
      requirement: 'Da Vinci PAS'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Patient Demographics');
  }
  
  if (!resource.authoredOn) {
    issues.push({
      severity: 'warning',
      code: 'MISSING_AUTHORED_ON',
      message: 'ServiceRequest.authoredOn should be present for timeline tracking',
      requirement: 'CMS-0057-F Timeline'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.requester) {
    issues.push({
      severity: 'error',
      code: 'MISSING_REQUESTER',
      message: 'ServiceRequest.requester is required',
      requirement: 'Da Vinci PAS'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Provenance');
  }
  
  if (!resource.insurance || resource.insurance.length === 0) {
    issues.push({
      severity: 'warning',
      code: 'MISSING_INSURANCE',
      message: 'ServiceRequest.insurance should reference coverage information',
      requirement: 'Da Vinci PAS'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Coverage');
  }
  
  // Check for order detail (procedure codes)
  if (!resource.code && (!resource.orderDetail || resource.orderDetail.length === 0)) {
    issues.push({
      severity: 'error',
      code: 'MISSING_SERVICE_CODE',
      message: 'ServiceRequest must have either code or orderDetail with procedure codes',
      requirement: 'Da Vinci PAS'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Procedures');
  }
  
  // Check for diagnosis/reason
  if (!resource.reasonCode && !resource.reasonReference) {
    warnings.push({
      code: 'MISSING_REASON',
      message: 'ServiceRequest should include reasonCode (diagnosis) for clinical context',
      recommendation: 'Add ICD-10 diagnosis codes in reasonCode'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Problems');
  }
  
  // Check for timing
  if (!resource.occurrencePeriod && !resource.occurrenceDateTime) {
    warnings.push({
      code: 'MISSING_TIMING',
      message: 'ServiceRequest should include occurrence timing',
      recommendation: 'Add occurrencePeriod for date range'
    });
  } else {
    requiredPresent++;
  }
  
  // Check priority for urgency
  if (resource.priority && resource.priority === 'urgent') {
    uscdiClasses.push('Clinical Notes');
    requiredPresent++;
  }
  
  // Timeline compliance check
  const timelineCompliance = checkPriorAuthTimeline(resource.authoredOn);
  
  return {
    compliant: issues.filter(i => i.severity === 'error').length === 0,
    issues,
    warnings,
    summary: {
      resourceType: 'ServiceRequest',
      requiredElementsPresent: requiredPresent,
      totalRequiredElements: totalRequired,
      usCoreSections: ['ServiceRequest'],
      daVinciProfiles: ['PAS ServiceRequest'],
      uscdiDataClasses: [...new Set(uscdiClasses)],
      timelineCompliance
    }
  };
}

/**
 * Validate ExplanationOfBenefit
 */
function validateExplanationOfBenefit(resource: ExplanationOfBenefit): ComplianceResult {
  const issues: ComplianceIssue[] = [];
  const warnings: ComplianceWarning[] = [];
  const uscdiClasses: string[] = [];
  let requiredPresent = 0;
  const totalRequired = 12;
  
  // Required elements
  if (!resource.status) {
    issues.push({
      severity: 'error',
      code: 'MISSING_STATUS',
      message: 'ExplanationOfBenefit.status is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.type) {
    issues.push({
      severity: 'error',
      code: 'MISSING_TYPE',
      message: 'ExplanationOfBenefit.type is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.use) {
    issues.push({
      severity: 'error',
      code: 'MISSING_USE',
      message: 'ExplanationOfBenefit.use is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.patient) {
    issues.push({
      severity: 'error',
      code: 'MISSING_PATIENT',
      message: 'ExplanationOfBenefit.patient is required',
      requirement: 'US Core'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Patient Demographics');
  }
  
  if (!resource.created) {
    issues.push({
      severity: 'warning',
      code: 'MISSING_CREATED',
      message: 'ExplanationOfBenefit.created should be present',
      requirement: 'CMS-0057-F'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.insurer) {
    issues.push({
      severity: 'error',
      code: 'MISSING_INSURER',
      message: 'ExplanationOfBenefit.insurer is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Coverage');
  }
  
  if (!resource.provider) {
    issues.push({
      severity: 'error',
      code: 'MISSING_PROVIDER',
      message: 'ExplanationOfBenefit.provider is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Provenance');
  }
  
  if (!resource.outcome) {
    issues.push({
      severity: 'warning',
      code: 'MISSING_OUTCOME',
      message: 'ExplanationOfBenefit.outcome should be present',
      requirement: 'US Core'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.item || resource.item.length === 0) {
    issues.push({
      severity: 'error',
      code: 'MISSING_ITEMS',
      message: 'ExplanationOfBenefit must have at least one item',
      requirement: 'US Core'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Procedures');
    
    // Validate item adjudications
    const itemsWithAdjudication = resource.item.filter(item => item.adjudication && item.adjudication.length > 0);
    if (itemsWithAdjudication.length === 0) {
      warnings.push({
        code: 'MISSING_ADJUDICATION',
        message: 'Items should include adjudication details',
        recommendation: 'Add adjudication with submitted, eligible, and benefit amounts'
      });
    } else {
      requiredPresent++;
      uscdiClasses.push('Financial');
    }
  }
  
  if (!resource.payment) {
    warnings.push({
      code: 'MISSING_PAYMENT',
      message: 'ExplanationOfBenefit.payment should include payment information',
      recommendation: 'Add payment details for transparency'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Financial');
  }
  
  if (!resource.total || resource.total.length === 0) {
    warnings.push({
      code: 'MISSING_TOTAL',
      message: 'ExplanationOfBenefit.total should include total amounts',
      recommendation: 'Add total submitted and benefit amounts'
    });
  } else {
    requiredPresent++;
  }
  
  return {
    compliant: issues.filter(i => i.severity === 'error').length === 0,
    issues,
    warnings,
    summary: {
      resourceType: 'ExplanationOfBenefit',
      requiredElementsPresent: requiredPresent,
      totalRequiredElements: totalRequired,
      usCoreSections: ['ExplanationOfBenefit'],
      daVinciProfiles: ['PDex ExplanationOfBenefit'],
      uscdiDataClasses: [...new Set(uscdiClasses)],
      timelineCompliance: { applicable: false }
    }
  };
}

/**
 * Validate Claim resource
 */
function validateClaim(resource: Claim): ComplianceResult {
  const issues: ComplianceIssue[] = [];
  const warnings: ComplianceWarning[] = [];
  const uscdiClasses: string[] = [];
  let requiredPresent = 0;
  const totalRequired = 10;
  
  if (!resource.status) {
    issues.push({
      severity: 'error',
      code: 'MISSING_STATUS',
      message: 'Claim.status is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.type) {
    issues.push({
      severity: 'error',
      code: 'MISSING_TYPE',
      message: 'Claim.type is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.use) {
    issues.push({
      severity: 'error',
      code: 'MISSING_USE',
      message: 'Claim.use is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.patient) {
    issues.push({
      severity: 'error',
      code: 'MISSING_PATIENT',
      message: 'Claim.patient is required',
      requirement: 'US Core'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Patient Demographics');
  }
  
  if (!resource.created) {
    issues.push({
      severity: 'warning',
      code: 'MISSING_CREATED',
      message: 'Claim.created should be present',
      requirement: 'CMS-0057-F'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.provider) {
    issues.push({
      severity: 'error',
      code: 'MISSING_PROVIDER',
      message: 'Claim.provider is required',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Provenance');
  }
  
  if (!resource.priority) {
    warnings.push({
      code: 'MISSING_PRIORITY',
      message: 'Claim.priority should be specified',
      recommendation: 'Add priority code'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.insurance || resource.insurance.length === 0) {
    issues.push({
      severity: 'error',
      code: 'MISSING_INSURANCE',
      message: 'Claim must have at least one insurance',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Coverage');
  }
  
  if (!resource.item || resource.item.length === 0) {
    issues.push({
      severity: 'error',
      code: 'MISSING_ITEMS',
      message: 'Claim must have at least one item',
      requirement: 'FHIR R4'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Procedures');
  }
  
  if (!resource.diagnosis || resource.diagnosis.length === 0) {
    warnings.push({
      code: 'MISSING_DIAGNOSIS',
      message: 'Claim should include diagnosis codes',
      recommendation: 'Add ICD-10 diagnosis codes'
    });
  } else {
    requiredPresent++;
    uscdiClasses.push('Problems');
  }
  
  return {
    compliant: issues.filter(i => i.severity === 'error').length === 0,
    issues,
    warnings,
    summary: {
      resourceType: 'Claim',
      requiredElementsPresent: requiredPresent,
      totalRequiredElements: totalRequired,
      usCoreSections: ['Claim'],
      daVinciProfiles: ['PDex Claim'],
      uscdiDataClasses: [...new Set(uscdiClasses)],
      timelineCompliance: { applicable: false }
    }
  };
}

/**
 * Validate Patient resource (US Core conformance)
 */
function validatePatient(resource: Patient): ComplianceResult {
  const issues: ComplianceIssue[] = [];
  const warnings: ComplianceWarning[] = [];
  const uscdiClasses: string[] = ['Patient Demographics'];
  let requiredPresent = 0;
  const totalRequired = 7;
  
  // US Core Patient requirements
  if (!resource.identifier || resource.identifier.length === 0) {
    issues.push({
      severity: 'error',
      code: 'MISSING_IDENTIFIER',
      message: 'Patient must have at least one identifier',
      requirement: 'US Core Patient'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.name || resource.name.length === 0) {
    issues.push({
      severity: 'error',
      code: 'MISSING_NAME',
      message: 'Patient must have at least one name',
      requirement: 'US Core Patient'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.gender) {
    issues.push({
      severity: 'error',
      code: 'MISSING_GENDER',
      message: 'Patient.gender is required',
      requirement: 'US Core Patient'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.birthDate) {
    warnings.push({
      code: 'MISSING_BIRTHDATE',
      message: 'Patient.birthDate should be present',
      recommendation: 'Include birth date for demographic matching'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.address || resource.address.length === 0) {
    warnings.push({
      code: 'MISSING_ADDRESS',
      message: 'Patient should have at least one address',
      recommendation: 'Include address for coordination of care'
    });
  } else {
    requiredPresent++;
  }
  
  if (!resource.telecom || resource.telecom.length === 0) {
    warnings.push({
      code: 'MISSING_TELECOM',
      message: 'Patient should have contact information',
      recommendation: 'Include phone or email'
    });
  } else {
    requiredPresent++;
  }
  
  // Check for US Core profile assertion
  const hasUSCoreProfile = resource.meta?.profile?.some(
    p => p.includes('us-core-patient')
  );
  
  if (!hasUSCoreProfile) {
    warnings.push({
      code: 'MISSING_US_CORE_PROFILE',
      message: 'Patient should declare US Core profile',
      recommendation: 'Add http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient to meta.profile'
    });
  } else {
    requiredPresent++;
  }
  
  return {
    compliant: issues.filter(i => i.severity === 'error').length === 0,
    issues,
    warnings,
    summary: {
      resourceType: 'Patient',
      requiredElementsPresent: requiredPresent,
      totalRequiredElements: totalRequired,
      usCoreSections: ['US Core Patient'],
      daVinciProfiles: ['PDex Patient'],
      uscdiDataClasses: uscdiClasses,
      timelineCompliance: { applicable: false }
    }
  };
}

/**
 * Check prior authorization timeline compliance
 * CMS-0057-F requires response within specific timeframes
 * Note: This checks elapsed time since submission. In production, also check ServiceRequest.priority field.
 */
function checkPriorAuthTimeline(authoredOn?: string): TimelineCompliance {
  if (!authoredOn) {
    return { applicable: false };
  }
  
  const authoredDate = new Date(authoredOn);
  const now = new Date();
  const hoursDiff = (now.getTime() - authoredDate.getTime()) / (1000 * 60 * 60);
  
  // CMS-0057-F standard: 72 hours for urgent, 7 calendar days for standard
  // This is a time-based check; production systems should also verify ServiceRequest.priority
  // If request is still within 72-hour urgent window, apply urgent deadline; otherwise apply standard 7-day deadline
  const withinUrgentWindow = hoursDiff <= 72;
  const deadline = withinUrgentWindow ? '72 hours' : '7 calendar days';
  // For urgent requests (within 72 hours), check against 72-hour deadline
  // For standard requests (past 72 hours), check against 7-day (168 hours) deadline
  const maxAllowedHours = withinUrgentWindow ? 72 : 168;
  const compliant = hoursDiff <= maxAllowedHours;
  
  return {
    applicable: true,
    requirement: `CMS-0057-F: Response within ${deadline} for ${withinUrgentWindow ? 'urgent' : 'standard'} requests`,
    deadline,
    compliant
  };
}

/**
 * Validate batch of resources
 */
export function validateBatchCompliance(resources: Resource[]): ComplianceResult[] {
  return resources.map(resource => validateCMS0057FCompliance(resource));
}

/**
 * Generate compliance report summary
 */
export function generateComplianceReport(results: ComplianceResult[]): string {
  const totalResources = results.length;
  const compliantResources = results.filter(r => r.compliant).length;
  const totalIssues = results.reduce((sum, r) => sum + r.issues.length, 0);
  const totalWarnings = results.reduce((sum, r) => sum + r.warnings.length, 0);
  
  const compliancePercentage = totalResources > 0 
    ? Math.round(compliantResources / totalResources * 100) 
    : 0;
  
  const report = `
CMS-0057-F Compliance Report
============================

Overall Summary:
- Total Resources Validated: ${totalResources}
- Compliant Resources: ${compliantResources} (${compliancePercentage}%)
- Total Issues: ${totalIssues}
- Total Warnings: ${totalWarnings}

Resource Breakdown:
${results.map((r, i) => `
  ${i + 1}. ${r.summary.resourceType}
     - Compliant: ${r.compliant ? '✓' : '✗'}
     - Required Elements: ${r.summary.requiredElementsPresent}/${r.summary.totalRequiredElements}
     - USCDI Data Classes: ${r.summary.uscdiDataClasses.join(', ') || 'None'}
     - Da Vinci Profiles: ${r.summary.daVinciProfiles.join(', ') || 'None'}
     - Issues: ${r.issues.length}
     - Warnings: ${r.warnings.length}
`).join('')}

Recommendations:
${totalIssues > 0 ? '- Address critical errors to achieve compliance' : '✓ No critical issues found'}
${totalWarnings > 0 ? `- Review ${totalWarnings} warnings for best practices` : '✓ No warnings'}
`;
  
  return report;
}
