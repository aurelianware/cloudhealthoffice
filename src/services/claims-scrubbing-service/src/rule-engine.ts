/**
 * Cloud Health Office - Claims Scrubbing Service
 * Validation Rule Engine
 * 
 * Core engine for executing validation rules against 837 claims.
 * Supports standard rules, custom rules, and payer-specific rules.
 */

import {
  X12_837_Claim,
  ValidationRule,
  ValidationResult,
  ClaimValidationResult,
  ClaimRoutingDecision,
  StandardRuleSet,
  CustomRule,
} from './types';

/**
 * Default standard rule set configuration
 */
export const DEFAULT_STANDARD_RULES: StandardRuleSet = {
  dataCompleteness: {
    memberIdRequired: true,
    subscriberDobRequired: true,
    billingProviderNpiRequired: true,
    diagnosisRequired: true,
    minServiceLines: 1,
    serviceDateRequired: true,
    chargeAmountRequired: true,
  },
  codeValidation: {
    validateIcd10: true,
    validateCpt: true,
    validateHcpcs: true,
    validateRevenueCodes: true,
    validatePlaceOfService: true,
    checkObsoleteCodes: true,
    checkGenderSpecificCodes: true,
    checkAgeSpecificCodes: true,
  },
  dateLogic: {
    serviceDateNotFuture: true,
    serviceDateWithinFilingLimit: true,
    filingLimitDays: 365,
    dischargeDateAfterAdmission: true,
    patientDobBeforeService: true,
    serviceDatesInSequence: true,
  },
  amountLogic: {
    chargeAmountsPositive: true,
    totalMatchesLineSum: true,
    maxSingleLineAmount: 1000000,
    maxClaimTotal: 10000000,
    unitsPositive: true,
    maxUnitsPerLine: 9999,
  },
  providerValidation: {
    validateNpiFormat: true,
    validateNpiRegistry: false, // Requires external API
    validateTaxonomyFormat: true,
    validateTaxIdFormat: true,
    renderingProviderRequired: false,
  },
  modifierValidation: {
    validateModifierFormat: true,
    checkDuplicateModifiers: true,
    validateModifierOrder: true,
    checkMutuallyExclusiveModifiers: true,
  },
};

/**
 * Validation Rule Engine
 */
export class ValidationRuleEngine {
  private rules: Map<string, ValidationRule> = new Map();
  private standardRules: StandardRuleSet;
  private ruleCache: Map<string, ValidationResult[]> = new Map();

  constructor(standardRules: StandardRuleSet = DEFAULT_STANDARD_RULES) {
    this.standardRules = standardRules;
    this.initializeStandardRules();
  }

  /**
   * Initialize built-in standard validation rules
   */
  private initializeStandardRules(): void {
    // Data Completeness Rules
    this.addRule({
      ruleId: 'DC001',
      ruleName: 'Subscriber Identifier Required',
      description: 'Validates that subscriber identifier is present on the claim',
      category: 'data-completeness',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dataCompleteness.memberIdRequired,
      priority: 1,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'DC002',
      ruleName: 'Subscriber DOB Required',
      description: 'Validates that subscriber date of birth is present',
      category: 'data-completeness',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dataCompleteness.subscriberDobRequired,
      priority: 1,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'DC003',
      ruleName: 'Billing Provider NPI Required',
      description: 'Validates that billing provider NPI is present',
      category: 'data-completeness',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dataCompleteness.billingProviderNpiRequired,
      priority: 1,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'DC004',
      ruleName: 'Diagnosis Code Required',
      description: 'Validates that at least one diagnosis code is present',
      category: 'data-completeness',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dataCompleteness.diagnosisRequired,
      priority: 1,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'DC005',
      ruleName: 'Minimum Service Lines',
      description: 'Validates that claim has minimum required service lines',
      category: 'data-completeness',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: true,
      priority: 1,
      type: 'standard',
      config: { minLines: this.standardRules.dataCompleteness.minServiceLines },
    });

    this.addRule({
      ruleId: 'DC006',
      ruleName: 'Service Date Required',
      description: 'Validates that service date is present on all lines',
      category: 'data-completeness',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dataCompleteness.serviceDateRequired,
      priority: 1,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'DC007',
      ruleName: 'Charge Amount Required',
      description: 'Validates that charge amount is present on all lines',
      category: 'data-completeness',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dataCompleteness.chargeAmountRequired,
      priority: 1,
      type: 'standard',
    });

    // Code Validation Rules
    this.addRule({
      ruleId: 'CV001',
      ruleName: 'Valid ICD-10 Code Format',
      description: 'Validates ICD-10 diagnosis code format',
      category: 'code-validity',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.codeValidation.validateIcd10,
      priority: 10,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'CV002',
      ruleName: 'Valid CPT Code Format',
      description: 'Validates CPT procedure code format',
      category: 'code-validity',
      severity: 'error',
      appliesTo: ['837P'],
      enabled: this.standardRules.codeValidation.validateCpt,
      priority: 10,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'CV003',
      ruleName: 'Valid HCPCS Code Format',
      description: 'Validates HCPCS code format',
      category: 'code-validity',
      severity: 'error',
      appliesTo: ['837P', '837I'],
      enabled: this.standardRules.codeValidation.validateHcpcs,
      priority: 10,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'CV004',
      ruleName: 'Valid Revenue Code Format',
      description: 'Validates revenue code format for institutional claims',
      category: 'code-validity',
      severity: 'error',
      appliesTo: ['837I'],
      enabled: this.standardRules.codeValidation.validateRevenueCodes,
      priority: 10,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'CV005',
      ruleName: 'Valid Place of Service Code',
      description: 'Validates place of service code',
      category: 'code-validity',
      severity: 'error',
      appliesTo: ['837P'],
      enabled: this.standardRules.codeValidation.validatePlaceOfService,
      priority: 10,
      type: 'standard',
    });

    // Date Logic Rules
    this.addRule({
      ruleId: 'DL001',
      ruleName: 'Service Date Not Future',
      description: 'Validates that service date is not in the future',
      category: 'date-logic',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dateLogic.serviceDateNotFuture,
      priority: 5,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'DL002',
      ruleName: 'Service Date Within Filing Limit',
      description: 'Validates that claim is filed within timely filing limit',
      category: 'date-logic',
      severity: 'warning',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dateLogic.serviceDateWithinFilingLimit,
      priority: 5,
      type: 'standard',
      config: { filingLimitDays: this.standardRules.dateLogic.filingLimitDays },
    });

    this.addRule({
      ruleId: 'DL003',
      ruleName: 'Discharge After Admission',
      description: 'Validates discharge date is after admission date',
      category: 'date-logic',
      severity: 'error',
      appliesTo: ['837I'],
      enabled: this.standardRules.dateLogic.dischargeDateAfterAdmission,
      priority: 5,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'DL004',
      ruleName: 'Patient DOB Before Service',
      description: 'Validates patient date of birth is before service date',
      category: 'date-logic',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.dateLogic.patientDobBeforeService,
      priority: 5,
      type: 'standard',
    });

    // Amount Logic Rules
    this.addRule({
      ruleId: 'AL001',
      ruleName: 'Charge Amounts Positive',
      description: 'Validates that all charge amounts are positive',
      category: 'amount-logic',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.amountLogic.chargeAmountsPositive,
      priority: 5,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'AL002',
      ruleName: 'Total Matches Line Sum',
      description: 'Validates total claim amount matches sum of service lines',
      category: 'amount-logic',
      severity: 'warning',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.amountLogic.totalMatchesLineSum,
      priority: 5,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'AL003',
      ruleName: 'Units Positive',
      description: 'Validates that units of service are positive',
      category: 'amount-logic',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.amountLogic.unitsPositive,
      priority: 5,
      type: 'standard',
    });

    // Provider Validation Rules
    this.addRule({
      ruleId: 'PV001',
      ruleName: 'Valid NPI Format',
      description: 'Validates NPI number format using Luhn algorithm',
      category: 'provider-validation',
      severity: 'error',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.providerValidation.validateNpiFormat,
      priority: 10,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'PV002',
      ruleName: 'Valid Tax ID Format',
      description: 'Validates tax identification number format',
      category: 'provider-validation',
      severity: 'warning',
      appliesTo: ['837P', '837I', '837D'],
      enabled: this.standardRules.providerValidation.validateTaxIdFormat,
      priority: 10,
      type: 'standard',
    });

    // Modifier Validation Rules
    this.addRule({
      ruleId: 'MV001',
      ruleName: 'Valid Modifier Format',
      description: 'Validates modifier code format',
      category: 'modifier-validation',
      severity: 'error',
      appliesTo: ['837P', '837I'],
      enabled: this.standardRules.modifierValidation.validateModifierFormat,
      priority: 10,
      type: 'standard',
    });

    this.addRule({
      ruleId: 'MV002',
      ruleName: 'No Duplicate Modifiers',
      description: 'Checks for duplicate modifiers on service lines',
      category: 'modifier-validation',
      severity: 'error',
      appliesTo: ['837P', '837I'],
      enabled: this.standardRules.modifierValidation.checkDuplicateModifiers,
      priority: 10,
      type: 'standard',
    });
  }

  /**
   * Add a validation rule
   */
  addRule(rule: ValidationRule): void {
    this.rules.set(rule.ruleId, rule);
  }

  /**
   * Add a custom rule
   */
  addCustomRule(rule: CustomRule): void {
    this.rules.set(rule.ruleId, rule);
  }

  /**
   * Get all rules
   */
  getRules(): ValidationRule[] {
    return Array.from(this.rules.values());
  }

  /**
   * Get rules by category
   */
  getRulesByCategory(category: string): ValidationRule[] {
    return Array.from(this.rules.values()).filter(r => r.category === category);
  }

  /**
   * Get enabled rules for a claim type
   */
  getEnabledRulesForClaimType(claimType: '837P' | '837I' | '837D'): ValidationRule[] {
    return Array.from(this.rules.values())
      .filter(r => r.enabled && r.appliesTo.includes(claimType))
      .sort((a, b) => a.priority - b.priority);
  }

  /**
   * Validate a claim against all applicable rules
   */
  async validateClaim(
    claim: X12_837_Claim,
    options?: {
      skipRules?: string[];
      onlyRules?: string[];
      parallelExecution?: boolean;
    }
  ): Promise<ClaimValidationResult> {
    const startTime = Date.now();
    const applicableRules = this.getApplicableRules(claim.claimType, options);
    
    const results: ValidationResult[] = [];
    
    if (options?.parallelExecution) {
      // Execute rules in parallel
      const rulePromises = applicableRules.map(rule => this.executeRule(rule, claim));
      const ruleResults = await Promise.all(rulePromises);
      results.push(...ruleResults);
    } else {
      // Execute rules sequentially
      for (const rule of applicableRules) {
        const result = await this.executeRule(rule, claim);
        results.push(result);
      }
    }

    const errorCount = results.filter(r => !r.passed && r.severity === 'error').length;
    const warningCount = results.filter(r => !r.passed && r.severity === 'warning').length;
    const infoCount = results.filter(r => !r.passed && r.severity === 'info').length;

    const routing = this.determineRouting(results, errorCount, warningCount);

    return {
      claimId: claim.claimId,
      claimType: claim.claimType,
      patientControlNumber: claim.claimHeader.patientControlNumber,
      status: this.determineStatus(errorCount, warningCount),
      rulesExecuted: results.length,
      rulesPassed: results.filter(r => r.passed).length,
      rulesFailed: results.filter(r => !r.passed).length,
      errorCount,
      warningCount,
      infoCount,
      results,
      validatedAt: new Date().toISOString(),
      totalValidationTimeMs: Date.now() - startTime,
      routing,
      firstPassEligible: errorCount === 0 && warningCount === 0,
    };
  }

  /**
   * Get applicable rules based on options
   */
  private getApplicableRules(
    claimType: '837P' | '837I' | '837D',
    options?: {
      skipRules?: string[];
      onlyRules?: string[];
    }
  ): ValidationRule[] {
    let rules = this.getEnabledRulesForClaimType(claimType);

    if (options?.onlyRules && options.onlyRules.length > 0) {
      rules = rules.filter(r => options.onlyRules!.includes(r.ruleId));
    }

    if (options?.skipRules && options.skipRules.length > 0) {
      rules = rules.filter(r => !options.skipRules!.includes(r.ruleId));
    }

    return rules;
  }

  /**
   * Execute a single validation rule
   */
  private async executeRule(rule: ValidationRule, claim: X12_837_Claim): Promise<ValidationResult> {
    const startTime = Date.now();
    
    try {
      // Route to appropriate validation method based on rule ID
      const result = await this.executeRuleLogic(rule, claim);
      return {
        ...result,
        executionTimeMs: Date.now() - startTime,
      };
    } catch (error) {
      return {
        ruleId: rule.ruleId,
        ruleName: rule.ruleName,
        passed: false,
        severity: 'error',
        message: `Rule execution error: ${error instanceof Error ? error.message : 'Unknown error'}`,
        executionTimeMs: Date.now() - startTime,
      };
    }
  }

  /**
   * Execute the actual rule logic
   */
  private async executeRuleLogic(rule: ValidationRule, claim: X12_837_Claim): Promise<ValidationResult> {
    switch (rule.ruleId) {
      // Data Completeness Rules
      case 'DC001':
        return this.validateMemberIdRequired(rule, claim);
      case 'DC002':
        return this.validateSubscriberDobRequired(rule, claim);
      case 'DC003':
        return this.validateBillingProviderNpiRequired(rule, claim);
      case 'DC004':
        return this.validateDiagnosisRequired(rule, claim);
      case 'DC005':
        return this.validateMinServiceLines(rule, claim);
      case 'DC006':
        return this.validateServiceDateRequired(rule, claim);
      case 'DC007':
        return this.validateChargeAmountRequired(rule, claim);
      
      // Code Validation Rules
      case 'CV001':
        return this.validateIcd10Format(rule, claim);
      case 'CV002':
        return this.validateCptFormat(rule, claim);
      case 'CV003':
        return this.validateHcpcsFormat(rule, claim);
      case 'CV004':
        return this.validateRevenueCodeFormat(rule, claim);
      case 'CV005':
        return this.validatePlaceOfServiceCode(rule, claim);
      
      // Date Logic Rules
      case 'DL001':
        return this.validateServiceDateNotFuture(rule, claim);
      case 'DL002':
        return this.validateServiceDateWithinFilingLimit(rule, claim);
      case 'DL003':
        return this.validateDischargeAfterAdmission(rule, claim);
      case 'DL004':
        return this.validatePatientDobBeforeService(rule, claim);
      
      // Amount Logic Rules
      case 'AL001':
        return this.validateChargeAmountsPositive(rule, claim);
      case 'AL002':
        return this.validateTotalMatchesLineSum(rule, claim);
      case 'AL003':
        return this.validateUnitsPositive(rule, claim);
      
      // Provider Validation Rules
      case 'PV001':
        return this.validateNpiFormat(rule, claim);
      case 'PV002':
        return this.validateTaxIdFormat(rule, claim);
      
      // Modifier Validation Rules
      case 'MV001':
        return this.validateModifierFormat(rule, claim);
      case 'MV002':
        return this.validateNoDuplicateModifiers(rule, claim);
      
      default:
        // Handle custom rules
        if (rule.type === 'custom') {
          return this.executeCustomRule(rule as CustomRule, claim);
        }
        return this.createPassResult(rule);
    }
  }

  // ============================================================================
  // Data Completeness Validation Methods
  // ============================================================================

  private validateMemberIdRequired(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const memberId = claim.subscriber.memberId;
    if (!memberId || memberId.trim() === '') {
      return this.createFailResult(rule, 'Member ID is required', ['subscriber.memberId'], 'DC001');
    }
    return this.createPassResult(rule);
  }

  private validateSubscriberDobRequired(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const dob = claim.subscriber.dateOfBirth;
    if (!dob || dob.trim() === '') {
      return this.createFailResult(rule, 'Subscriber date of birth is required', ['subscriber.dateOfBirth'], 'DC002');
    }
    return this.createPassResult(rule);
  }

  private validateBillingProviderNpiRequired(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const npi = claim.billingProvider.npi;
    if (!npi || npi.trim() === '') {
      return this.createFailResult(rule, 'Billing provider NPI is required', ['billingProvider.npi'], 'DC003');
    }
    return this.createPassResult(rule);
  }

  private validateDiagnosisRequired(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const diagCodes = claim.claimHeader.diagnosisCodes;
    if (!diagCodes || diagCodes.length === 0) {
      return this.createFailResult(rule, 'At least one diagnosis code is required', ['claimHeader.diagnosisCodes'], 'DC004');
    }
    return this.createPassResult(rule);
  }

  private validateMinServiceLines(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const minLines = (rule.config?.minLines as number) || 1;
    if (claim.serviceLines.length < minLines) {
      return this.createFailResult(
        rule, 
        `Claim must have at least ${minLines} service line(s)`, 
        ['serviceLines'],
        'DC005'
      );
    }
    return this.createPassResult(rule);
  }

  private validateServiceDateRequired(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const missingLines: number[] = [];
    for (const line of claim.serviceLines) {
      if (!line.serviceDate || line.serviceDate.trim() === '') {
        missingLines.push(line.lineNumber);
      }
    }
    if (missingLines.length > 0) {
      return this.createFailResult(
        rule,
        `Service date is required on line(s): ${missingLines.join(', ')}`,
        ['serviceLines.serviceDate'],
        'DC006',
        missingLines
      );
    }
    return this.createPassResult(rule);
  }

  private validateChargeAmountRequired(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const missingLines: number[] = [];
    for (const line of claim.serviceLines) {
      if (line.chargeAmount === undefined || line.chargeAmount === null) {
        missingLines.push(line.lineNumber);
      }
    }
    if (missingLines.length > 0) {
      return this.createFailResult(
        rule,
        `Charge amount is required on line(s): ${missingLines.join(', ')}`,
        ['serviceLines.chargeAmount'],
        'DC007',
        missingLines
      );
    }
    return this.createPassResult(rule);
  }

  // ============================================================================
  // Code Validation Methods
  // ============================================================================

  private validateIcd10Format(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const diagCodes = claim.claimHeader.diagnosisCodes || [];
    const invalidCodes: string[] = [];
    
    // ICD-10-CM format: letter followed by 2-7 alphanumeric characters
    const icd10Pattern = /^[A-TV-Z][0-9][0-9AB]\.?[0-9A-Z]{0,4}$/i;
    
    for (const diag of diagCodes) {
      if (diag.qualifier === 'ABK' || diag.qualifier === 'ABF') {
        const code = diag.code.replace('.', '');
        if (!icd10Pattern.test(diag.code) && !icd10Pattern.test(code)) {
          invalidCodes.push(diag.code);
        }
      }
    }
    
    if (invalidCodes.length > 0) {
      return this.createFailResult(
        rule,
        `Invalid ICD-10 code format: ${invalidCodes.join(', ')}`,
        ['claimHeader.diagnosisCodes'],
        'CV001'
      );
    }
    return this.createPassResult(rule);
  }

  private validateCptFormat(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const invalidLines: number[] = [];
    // CPT format: 5 numeric digits or 4 digits + 1 letter
    const cptPattern = /^[0-9]{4}[0-9A-Z]$/;
    
    for (const line of claim.serviceLines) {
      if (line.procedureCodeQualifier === 'HC' || !line.procedureCodeQualifier) {
        if (!cptPattern.test(line.procedureCode)) {
          // Check if it might be HCPCS (starts with letter)
          if (!/^[A-Z][0-9]{4}$/.test(line.procedureCode)) {
            invalidLines.push(line.lineNumber);
          }
        }
      }
    }
    
    if (invalidLines.length > 0) {
      return this.createFailResult(
        rule,
        `Invalid CPT code format on line(s): ${invalidLines.join(', ')}`,
        ['serviceLines.procedureCode'],
        'CV002',
        invalidLines
      );
    }
    return this.createPassResult(rule);
  }

  private validateHcpcsFormat(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const invalidLines: number[] = [];
    // HCPCS format: letter followed by 4 digits
    const hcpcsPattern = /^[A-Z][0-9]{4}$/;
    
    for (const line of claim.serviceLines) {
      const code = line.procedureCode;
      // Only validate if it looks like an HCPCS code (starts with letter)
      if (code && /^[A-Z]/.test(code)) {
        if (!hcpcsPattern.test(code)) {
          invalidLines.push(line.lineNumber);
        }
      }
    }
    
    if (invalidLines.length > 0) {
      return this.createFailResult(
        rule,
        `Invalid HCPCS code format on line(s): ${invalidLines.join(', ')}`,
        ['serviceLines.procedureCode'],
        'CV003',
        invalidLines
      );
    }
    return this.createPassResult(rule);
  }

  private validateRevenueCodeFormat(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    if (claim.claimType !== '837I') {
      return this.createPassResult(rule);
    }
    
    const invalidLines: number[] = [];
    // Revenue code format: 4 digits (0001-9999)
    const revenuePattern = /^[0-9]{4}$/;
    
    for (const line of claim.serviceLines) {
      if (line.revenueCode && !revenuePattern.test(line.revenueCode)) {
        invalidLines.push(line.lineNumber);
      }
    }
    
    if (invalidLines.length > 0) {
      return this.createFailResult(
        rule,
        `Invalid revenue code format on line(s): ${invalidLines.join(', ')}`,
        ['serviceLines.revenueCode'],
        'CV004',
        invalidLines
      );
    }
    return this.createPassResult(rule);
  }

  private validatePlaceOfServiceCode(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    if (claim.claimType !== '837P') {
      return this.createPassResult(rule);
    }
    
    // Valid POS codes (common ones)
    const validPosCodes = new Set([
      '01', '02', '03', '04', '05', '06', '07', '08', '09', '10',
      '11', '12', '13', '14', '15', '16', '17', '18', '19', '20',
      '21', '22', '23', '24', '25', '26', '31', '32', '33', '34',
      '41', '42', '49', '50', '51', '52', '53', '54', '55', '56',
      '57', '58', '60', '61', '62', '65', '71', '72', '81', '99'
    ]);
    
    const pos = claim.claimHeader.placeOfServiceCode;
    if (pos && !validPosCodes.has(pos)) {
      return this.createFailResult(
        rule,
        `Invalid place of service code: ${pos}`,
        ['claimHeader.placeOfServiceCode'],
        'CV005'
      );
    }
    return this.createPassResult(rule);
  }

  // ============================================================================
  // Date Logic Validation Methods
  // ============================================================================

  private validateServiceDateNotFuture(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const today = this.getCurrentDateString();
    const futureLines: number[] = [];
    
    for (const line of claim.serviceLines) {
      if (line.serviceDate > today) {
        futureLines.push(line.lineNumber);
      }
    }
    
    if (futureLines.length > 0) {
      return this.createFailResult(
        rule,
        `Service date is in the future on line(s): ${futureLines.join(', ')}`,
        ['serviceLines.serviceDate'],
        'DL001',
        futureLines
      );
    }
    return this.createPassResult(rule);
  }

  private validateServiceDateWithinFilingLimit(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const filingLimitDays = (rule.config?.filingLimitDays as number) || 365;
    const limitDate = this.getDateMinusDays(filingLimitDays);
    const lateLines: number[] = [];
    
    for (const line of claim.serviceLines) {
      if (line.serviceDate < limitDate) {
        lateLines.push(line.lineNumber);
      }
    }
    
    if (lateLines.length > 0) {
      return this.createFailResult(
        rule,
        `Service date exceeds ${filingLimitDays}-day filing limit on line(s): ${lateLines.join(', ')}`,
        ['serviceLines.serviceDate'],
        'DL002',
        lateLines
      );
    }
    return this.createPassResult(rule);
  }

  private validateDischargeAfterAdmission(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    if (claim.claimType !== '837I') {
      return this.createPassResult(rule);
    }
    
    const admission = claim.claimHeader.admissionDate;
    const discharge = claim.claimHeader.dischargeDate;
    
    if (admission && discharge && discharge < admission) {
      return this.createFailResult(
        rule,
        'Discharge date cannot be before admission date',
        ['claimHeader.admissionDate', 'claimHeader.dischargeDate'],
        'DL003'
      );
    }
    return this.createPassResult(rule);
  }

  private validatePatientDobBeforeService(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const patientDob = claim.patient?.dateOfBirth || claim.subscriber.dateOfBirth;
    const invalidLines: number[] = [];
    
    for (const line of claim.serviceLines) {
      if (line.serviceDate < patientDob) {
        invalidLines.push(line.lineNumber);
      }
    }
    
    if (invalidLines.length > 0) {
      return this.createFailResult(
        rule,
        `Service date is before patient date of birth on line(s): ${invalidLines.join(', ')}`,
        ['serviceLines.serviceDate'],
        'DL004',
        invalidLines
      );
    }
    return this.createPassResult(rule);
  }

  // ============================================================================
  // Amount Logic Validation Methods
  // ============================================================================

  private validateChargeAmountsPositive(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const invalidLines: number[] = [];
    
    for (const line of claim.serviceLines) {
      if (line.chargeAmount <= 0) {
        invalidLines.push(line.lineNumber);
      }
    }
    
    if (invalidLines.length > 0) {
      return this.createFailResult(
        rule,
        `Charge amount must be positive on line(s): ${invalidLines.join(', ')}`,
        ['serviceLines.chargeAmount'],
        'AL001',
        invalidLines
      );
    }
    return this.createPassResult(rule);
  }

  private validateTotalMatchesLineSum(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const lineSum = claim.serviceLines.reduce((sum, line) => sum + line.chargeAmount, 0);
    const tolerance = 0.01; // Allow for floating point differences
    
    if (Math.abs(claim.totalClaimedAmount - lineSum) > tolerance) {
      return this.createFailResult(
        rule,
        `Total claimed amount (${claim.totalClaimedAmount}) does not match sum of line charges (${lineSum})`,
        ['totalClaimedAmount', 'serviceLines.chargeAmount'],
        'AL002'
      );
    }
    return this.createPassResult(rule);
  }

  private validateUnitsPositive(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const invalidLines: number[] = [];
    
    for (const line of claim.serviceLines) {
      if (line.units <= 0) {
        invalidLines.push(line.lineNumber);
      }
    }
    
    if (invalidLines.length > 0) {
      return this.createFailResult(
        rule,
        `Units must be positive on line(s): ${invalidLines.join(', ')}`,
        ['serviceLines.units'],
        'AL003',
        invalidLines
      );
    }
    return this.createPassResult(rule);
  }

  // ============================================================================
  // Provider Validation Methods
  // ============================================================================

  private validateNpiFormat(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const npi = claim.billingProvider.npi;
    
    if (!this.isValidNpi(npi)) {
      return this.createFailResult(
        rule,
        `Invalid NPI format: ${npi}. NPI must be 10 digits and pass Luhn check.`,
        ['billingProvider.npi'],
        'PV001'
      );
    }
    return this.createPassResult(rule);
  }

  private validateTaxIdFormat(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const taxId = claim.billingProvider.taxId;
    const qualifier = claim.billingProvider.taxIdQualifier;
    
    if (taxId) {
      const cleanTaxId = taxId.replace(/[-\s]/g, '');
      
      if (qualifier === 'EI') {
        // EIN format: 9 digits
        if (!/^[0-9]{9}$/.test(cleanTaxId)) {
          return this.createFailResult(
            rule,
            'Invalid EIN format. Must be 9 digits.',
            ['billingProvider.taxId'],
            'PV002'
          );
        }
      } else if (qualifier === 'SY') {
        // SSN format: 9 digits
        if (!/^[0-9]{9}$/.test(cleanTaxId)) {
          return this.createFailResult(
            rule,
            'Invalid SSN format. Must be 9 digits.',
            ['billingProvider.taxId'],
            'PV002'
          );
        }
      }
    }
    return this.createPassResult(rule);
  }

  // ============================================================================
  // Modifier Validation Methods
  // ============================================================================

  private validateModifierFormat(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const invalidLines: number[] = [];
    // Modifier format: 2 alphanumeric characters
    const modifierPattern = /^[A-Z0-9]{2}$/;
    
    for (const line of claim.serviceLines) {
      if (line.modifiers) {
        for (const mod of line.modifiers) {
          if (!modifierPattern.test(mod)) {
            if (!invalidLines.includes(line.lineNumber)) {
              invalidLines.push(line.lineNumber);
            }
          }
        }
      }
    }
    
    if (invalidLines.length > 0) {
      return this.createFailResult(
        rule,
        `Invalid modifier format on line(s): ${invalidLines.join(', ')}`,
        ['serviceLines.modifiers'],
        'MV001',
        invalidLines
      );
    }
    return this.createPassResult(rule);
  }

  private validateNoDuplicateModifiers(rule: ValidationRule, claim: X12_837_Claim): ValidationResult {
    const duplicateLines: number[] = [];
    
    for (const line of claim.serviceLines) {
      if (line.modifiers && line.modifiers.length > 1) {
        const uniqueMods = new Set(line.modifiers);
        if (uniqueMods.size !== line.modifiers.length) {
          duplicateLines.push(line.lineNumber);
        }
      }
    }
    
    if (duplicateLines.length > 0) {
      return this.createFailResult(
        rule,
        `Duplicate modifiers found on line(s): ${duplicateLines.join(', ')}`,
        ['serviceLines.modifiers'],
        'MV002',
        duplicateLines
      );
    }
    return this.createPassResult(rule);
  }

  // ============================================================================
  // Custom Rule Execution
  // ============================================================================

  private async executeCustomRule(rule: CustomRule, claim: X12_837_Claim): Promise<ValidationResult> {
    // Custom rules would be executed via a sandboxed script engine
    // For now, return a pass result - in production this would use
    // a secure JavaScript sandbox or WebAssembly for custom logic
    return this.createPassResult(rule);
  }

  // ============================================================================
  // Helper Methods
  // ============================================================================

  private createPassResult(rule: ValidationRule): ValidationResult {
    return {
      ruleId: rule.ruleId,
      ruleName: rule.ruleName,
      passed: true,
    };
  }

  private createFailResult(
    rule: ValidationRule,
    message: string,
    fields: string[],
    editCode: string,
    serviceLines?: number[]
  ): ValidationResult {
    return {
      ruleId: rule.ruleId,
      ruleName: rule.ruleName,
      passed: false,
      severity: rule.severity,
      message,
      fields,
      serviceLines,
      editCode,
    };
  }

  private isValidNpi(npi: string): boolean {
    if (!/^[0-9]{10}$/.test(npi)) {
      return false;
    }
    
    // Luhn algorithm check with NPI prefix
    const prefixed = '80840' + npi;
    let sum = 0;
    let alternate = false;
    
    for (let i = prefixed.length - 1; i >= 0; i--) {
      let digit = parseInt(prefixed[i], 10);
      if (alternate) {
        digit *= 2;
        if (digit > 9) {
          digit -= 9;
        }
      }
      sum += digit;
      alternate = !alternate;
    }
    
    return sum % 10 === 0;
  }

  private getCurrentDateString(): string {
    return new Date().toISOString().slice(0, 10).replace(/-/g, '');
  }

  private getDateMinusDays(days: number): string {
    const date = new Date();
    date.setDate(date.getDate() - days);
    return date.toISOString().slice(0, 10).replace(/-/g, '');
  }

  private determineStatus(errorCount: number, warningCount: number): 'clean' | 'flagged' | 'rejected' {
    if (errorCount > 0) {
      return 'rejected';
    }
    if (warningCount > 0) {
      return 'flagged';
    }
    return 'clean';
  }

  private determineRouting(
    results: ValidationResult[],
    errorCount: number,
    warningCount: number
  ): ClaimRoutingDecision {
    const editCodes = results
      .filter(r => !r.passed && r.editCode)
      .map(r => r.editCode as string);

    if (errorCount > 0) {
      return {
        destination: 'work-queue',
        queueName: 'claims-errors',
        priority: 'high',
        reason: `Claim has ${errorCount} validation error(s) requiring review`,
        editCodes,
        requiresManualReview: true,
      };
    }

    if (warningCount > 0) {
      return {
        destination: 'work-queue',
        queueName: 'claims-warnings',
        priority: 'medium',
        reason: `Claim has ${warningCount} warning(s) requiring review`,
        editCodes,
        requiresManualReview: true,
      };
    }

    return {
      destination: 'adjudication',
      reason: 'Claim passed all validation rules',
      editCodes: [],
      requiresManualReview: false,
    };
  }
}
