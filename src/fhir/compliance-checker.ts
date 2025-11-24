/**
 * CMS-0057-F Compliance Checker
 * 
 * Validates FHIR resources and processes against CMS-0057-F requirements:
 * - CMS Interoperability and Prior Authorization Final Rule (CMS-0057-F)
 * - Data class requirements
 * - Timeline requirements (USCDI v3 data classes)
 * - Prior authorization API requirements
 * 
 * References:
 * - CMS-0057-F Final Rule
 * - USCDI v3 (United States Core Data for Interoperability)
 * - HL7 FHIR R4 Specification
 * - Da Vinci FHIR Implementation Guides (PAS, CRD, DTR)
 * 
 * @module compliance-checker
 */

import { Resource } from 'fhir/r4';

/**
 * Compliance check result
 */
export interface ComplianceResult {
  /** Overall compliance status */
  compliant: boolean;
  /** List of compliance issues found */
  issues: ComplianceIssue[];
  /** Compliance score (0-100) */
  score: number;
  /** Detailed breakdown by category */
  breakdown: {
    dataClasses: ComplianceCategory;
    timelines: ComplianceCategory;
    apiRequirements: ComplianceCategory;
  };
}

/**
 * Individual compliance issue
 */
export interface ComplianceIssue {
  /** Severity: error, warning, or info */
  severity: 'error' | 'warning' | 'info';
  /** Category of the issue */
  category: 'data-class' | 'timeline' | 'api' | 'profile' | 'terminology';
  /** Human-readable description */
  message: string;
  /** FHIR resource element path (if applicable) */
  path?: string;
  /** CMS rule reference */
  ruleReference?: string;
}

/**
 * Compliance category result
 */
export interface ComplianceCategory {
  /** Category is compliant */
  compliant: boolean;
  /** Number of checks passed */
  passed: number;
  /** Total number of checks */
  total: number;
  /** Issues in this category */
  issues: ComplianceIssue[];
}

/**
 * CMS-0057-F data class requirements
 */
const CMS_DATA_CLASSES = [
  'Patient Demographics',
  'Clinical Notes',
  'Laboratory Results',
  'Medications',
  'Allergies and Intolerances',
  'Immunizations',
  'Procedures',
  'Vital Signs',
  'Problems',
  'Assessment and Plan of Treatment',
  'Goals',
  'Health Concerns'
];

/**
 * CMS-0057-F timeline requirements
 */
const CMS_TIMELINES = {
  priorAuthDecision: 7, // 7 calendar days for standard requests
  urgentPriorAuthDecision: 3, // 3 calendar days (72 hours) for urgent requests
  dataAvailability: 1, // 1 business day for data availability
  notificationTimeframe: 1 // 1 business day (24 hours) for decision notification
};

/**
 * Main compliance checker function
 * 
 * @param resource FHIR resource to check
 * @param options Compliance check options
 * @returns Compliance check result
 */
export function checkCMSCompliance(
  resource: Resource,
  options: {
    checkDataClasses?: boolean;
    checkTimelines?: boolean;
    checkAPIRequirements?: boolean;
  } = {}
): ComplianceResult {
  const issues: ComplianceIssue[] = [];
  
  // Default: check all
  const checkDataClasses = options.checkDataClasses !== false;
  const checkTimelines = options.checkTimelines !== false;
  const checkAPIRequirements = options.checkAPIRequirements !== false;
  
  // Check resource type support
  if (!isSupportedResourceType(resource.resourceType)) {
    issues.push({
      severity: 'warning',
      category: 'api',
      message: `Resource type ${resource.resourceType} may not be covered by CMS-0057-F`,
      ruleReference: 'CMS-0057-F §438.242'
    });
  }
  
  // Check FHIR profiles
  const profileIssues = checkFHIRProfiles(resource);
  issues.push(...profileIssues);
  
  // Check data class compliance
  const dataClassResult = checkDataClasses 
    ? checkDataClassCompliance(resource)
    : { compliant: true, passed: 0, total: 0, issues: [] };
  issues.push(...dataClassResult.issues);
  
  // Check timeline compliance
  const timelineResult = checkTimelines
    ? checkTimelineCompliance(resource)
    : { compliant: true, passed: 0, total: 0, issues: [] };
  issues.push(...timelineResult.issues);
  
  // Check API requirements
  const apiResult = checkAPIRequirements
    ? checkAPIRequirementsCompliance(resource)
    : { compliant: true, passed: 0, total: 0, issues: [] };
  issues.push(...apiResult.issues);
  
  // Calculate overall compliance
  const errorCount = issues.filter(i => i.severity === 'error').length;
  const compliant = errorCount === 0;
  
  // Calculate score (100 = perfect, 0 = many errors)
  const warningCount = issues.filter(i => i.severity === 'warning').length;
  const score = Math.max(0, 100 - (errorCount * 10) - (warningCount * 2));
  
  return {
    compliant,
    issues,
    score,
    breakdown: {
      dataClasses: dataClassResult,
      timelines: timelineResult,
      apiRequirements: apiResult
    }
  };
}

/**
 * Check if resource type is supported by CMS-0057-F
 */
function isSupportedResourceType(resourceType: string): boolean {
  const supportedTypes = [
    'Patient',
    'Claim',
    'ServiceRequest',
    'ExplanationOfBenefit',
    'Coverage',
    'CoverageEligibilityRequest',
    'CoverageEligibilityResponse',
    'Observation',
    'Condition',
    'Procedure',
    'MedicationRequest',
    'Immunization',
    'AllergyIntolerance',
    'DocumentReference',
    'DiagnosticReport'
  ];
  
  return supportedTypes.includes(resourceType);
}

/**
 * Check FHIR profile conformance
 */
function checkFHIRProfiles(resource: Resource): ComplianceIssue[] {
  const issues: ComplianceIssue[] = [];
  
  // Check if resource has meta.profile
  if (!resource.meta?.profile || resource.meta.profile.length === 0) {
    issues.push({
      severity: 'warning',
      category: 'profile',
      message: 'Resource does not declare conformance to any FHIR profiles',
      path: 'meta.profile',
      ruleReference: 'US Core IG'
    });
  } else {
    // Check for US Core or Da Vinci profiles
    const hasUSCoreProfile = resource.meta.profile.some(p => 
      p.includes('us/core') || p.includes('davinci')
    );
    
    if (!hasUSCoreProfile) {
      issues.push({
        severity: 'warning',
        category: 'profile',
        message: 'Resource should conform to US Core or Da Vinci profiles for CMS-0057-F',
        path: 'meta.profile',
        ruleReference: 'CMS-0057-F §438.242(b)(2)'
      });
    }
  }
  
  return issues;
}

/**
 * Check data class compliance (USCDI v3)
 */
function checkDataClassCompliance(resource: Resource): ComplianceCategory {
  const issues: ComplianceIssue[] = [];
  let passed = 0;
  let total = 0;
  
  // ServiceRequest (Prior Authorization)
  if (resource.resourceType === 'ServiceRequest') {
    total = 5;
    
    const sr = resource as any;
    
    // Check required elements for prior auth
    if (sr.status) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ServiceRequest must have status',
      path: 'status',
      ruleReference: 'CMS-0057-F §438.210(d)'
    });
    
    if (sr.intent) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ServiceRequest must have intent',
      path: 'intent',
      ruleReference: 'Da Vinci PAS IG'
    });
    
    if (sr.code) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ServiceRequest must have code (service being requested)',
      path: 'code',
      ruleReference: 'CMS-0057-F §438.210(d)'
    });
    
    if (sr.subject) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ServiceRequest must have subject (patient reference)',
      path: 'subject',
      ruleReference: 'FHIR R4 Core'
    });
    
    if (sr.authoredOn) passed++;
    else issues.push({
      severity: 'warning',
      category: 'data-class',
      message: 'ServiceRequest should have authoredOn date',
      path: 'authoredOn',
      ruleReference: 'Da Vinci PAS IG'
    });
  }
  
  // Claim
  if (resource.resourceType === 'Claim') {
    total = 6;
    
    const claim = resource as any;
    
    if (claim.status) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'Claim must have status',
      path: 'status'
    });
    
    if (claim.type) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'Claim must have type',
      path: 'type'
    });
    
    if (claim.use) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'Claim must have use',
      path: 'use'
    });
    
    if (claim.patient) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'Claim must have patient reference',
      path: 'patient'
    });
    
    if (claim.provider) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'Claim must have provider',
      path: 'provider'
    });
    
    if (claim.item && claim.item.length > 0) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'Claim must have at least one service line item',
      path: 'item'
    });
  }
  
  // ExplanationOfBenefit
  if (resource.resourceType === 'ExplanationOfBenefit') {
    total = 5;
    
    const eob = resource as any;
    
    if (eob.status) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ExplanationOfBenefit must have status',
      path: 'status'
    });
    
    if (eob.type) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ExplanationOfBenefit must have type',
      path: 'type'
    });
    
    if (eob.patient) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ExplanationOfBenefit must have patient reference',
      path: 'patient'
    });
    
    if (eob.insurer) passed++;
    else issues.push({
      severity: 'error',
      category: 'data-class',
      message: 'ExplanationOfBenefit must have insurer',
      path: 'insurer'
    });
    
    if (eob.item && eob.item.length > 0) passed++;
    else issues.push({
      severity: 'warning',
      category: 'data-class',
      message: 'ExplanationOfBenefit should have service line items',
      path: 'item'
    });
  }
  
  const compliant = issues.filter(i => i.severity === 'error').length === 0;
  
  return {
    compliant,
    passed,
    total,
    issues
  };
}

/**
 * Check timeline compliance
 */
function checkTimelineCompliance(resource: Resource): ComplianceCategory {
  const issues: ComplianceIssue[] = [];
  let passed = 0;
  let total = 0;
  
  // Check ServiceRequest timelines
  if (resource.resourceType === 'ServiceRequest') {
    total = 2;
    
    const sr = resource as any;
    
    // Check if authoredOn is present
    if (sr.authoredOn) {
      passed++;
      
      // Check if request is recent (within acceptable timeframe)
      const authoredDate = new Date(sr.authoredOn);
      const now = new Date();
      const daysDiff = (now.getTime() - authoredDate.getTime()) / (1000 * 60 * 60 * 24);
      
      if (daysDiff > 30) {
        issues.push({
          severity: 'warning',
          category: 'timeline',
          message: `ServiceRequest is ${Math.floor(daysDiff)} days old - consider updating status`,
          path: 'authoredOn',
          ruleReference: 'CMS-0057-F §438.210(d)'
        });
      } else {
        passed++;
      }
    } else {
      issues.push({
        severity: 'warning',
        category: 'timeline',
        message: 'ServiceRequest should have authoredOn date for timeline tracking',
        path: 'authoredOn',
        ruleReference: 'CMS-0057-F §438.210(d)'
      });
    }
  }
  
  // Check ExplanationOfBenefit timelines
  if (resource.resourceType === 'ExplanationOfBenefit') {
    total = 1;
    
    const eob = resource as any;
    
    // Check if created date is present
    if (eob.created) {
      passed++;
    } else {
      issues.push({
        severity: 'warning',
        category: 'timeline',
        message: 'ExplanationOfBenefit should have created date',
        path: 'created'
      });
    }
  }
  
  const compliant = issues.filter(i => i.severity === 'error').length === 0;
  
  return {
    compliant,
    passed,
    total,
    issues
  };
}

/**
 * Check API requirements compliance
 */
function checkAPIRequirementsCompliance(resource: Resource): ComplianceCategory {
  const issues: ComplianceIssue[] = [];
  let passed = 0;
  let total = 3;
  
  // Check resource has ID
  if (resource.id) {
    passed++;
  } else {
    issues.push({
      severity: 'error',
      category: 'api',
      message: 'Resource must have an ID for API access',
      path: 'id',
      ruleReference: 'FHIR R4 RESTful API'
    });
  }
  
  // Check resource has meta
  if (resource.meta) {
    passed++;
  } else {
    issues.push({
      severity: 'warning',
      category: 'api',
      message: 'Resource should have meta element with version and profile information',
      path: 'meta',
      ruleReference: 'FHIR R4 Core'
    });
  }
  
  // Check for proper identifiers
  const hasIdentifier = (resource as any).identifier;
  if (hasIdentifier && Array.isArray(hasIdentifier) && hasIdentifier.length > 0) {
    passed++;
  } else {
    issues.push({
      severity: 'warning',
      category: 'api',
      message: 'Resource should have at least one business identifier',
      path: 'identifier',
      ruleReference: 'CMS-0057-F §438.242(b)(2)'
    });
  }
  
  const compliant = issues.filter(i => i.severity === 'error').length === 0;
  
  return {
    compliant,
    passed,
    total,
    issues
  };
}

/**
 * Batch compliance check for multiple resources
 */
export function checkBatchCompliance(resources: Resource[]): ComplianceResult {
  const allIssues: ComplianceIssue[] = [];
  let totalScore = 0;
  
  const breakdowns = resources.map(resource => {
    const result = checkCMSCompliance(resource);
    allIssues.push(...result.issues);
    totalScore += result.score;
    return result.breakdown;
  });
  
  // Aggregate breakdowns
  const aggregateBreakdown = {
    dataClasses: aggregateCategory(breakdowns.map(b => b.dataClasses)),
    timelines: aggregateCategory(breakdowns.map(b => b.timelines)),
    apiRequirements: aggregateCategory(breakdowns.map(b => b.apiRequirements))
  };
  
  const errorCount = allIssues.filter(i => i.severity === 'error').length;
  const avgScore = resources.length > 0 ? totalScore / resources.length : 0;
  
  return {
    compliant: errorCount === 0,
    issues: allIssues,
    score: Math.round(avgScore),
    breakdown: aggregateBreakdown
  };
}

/**
 * Aggregate compliance categories
 */
function aggregateCategory(categories: ComplianceCategory[]): ComplianceCategory {
  const totalPassed = categories.reduce((sum, cat) => sum + cat.passed, 0);
  const totalTotal = categories.reduce((sum, cat) => sum + cat.total, 0);
  const allIssues = categories.flatMap(cat => cat.issues);
  const compliant = categories.every(cat => cat.compliant);
  
  return {
    compliant,
    passed: totalPassed,
    total: totalTotal,
    issues: allIssues
  };
}
