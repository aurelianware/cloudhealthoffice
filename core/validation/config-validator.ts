/**
 * Availity Integration Configuration Validator
 * 
 * Validates configuration JSON against the schema and performs additional
 * business rule validations.
 */

import Ajv from 'ajv';
import addFormats from 'ajv-formats';

/**
 * Validation error with field path
 */
export interface ValidationError {
  /** Field path where the error occurred */
  field: string;
  /** Error message */
  message: string;
  /** Error severity */
  severity: 'error' | 'warning';
}

/**
 * Validation result
 */
export interface ValidationResult {
  /** Whether validation passed */
  valid: boolean;
  /** List of validation errors */
  errors: ValidationError[];
  /** List of validation warnings */
  warnings: ValidationError[];
}

/**
 * Valid US state codes
 */
const VALID_US_STATES = [
  'AL', 'AK', 'AZ', 'AR', 'CA', 'CO', 'CT', 'DE', 'FL', 'GA',
  'HI', 'ID', 'IL', 'IN', 'IA', 'KS', 'KY', 'LA', 'ME', 'MD',
  'MA', 'MI', 'MN', 'MS', 'MO', 'MT', 'NE', 'NV', 'NH', 'NJ',
  'NM', 'NY', 'NC', 'ND', 'OH', 'OK', 'OR', 'PA', 'RI', 'SC',
  'SD', 'TN', 'TX', 'UT', 'VT', 'VA', 'WA', 'WV', 'WI', 'WY',
  'DC', 'PR', 'VI', 'GU', 'AS', 'MP'
];

/**
 * Configuration validator class
 */
export class ConfigValidator {
  private ajv: Ajv.default;
  private schema: any;

  constructor(schema: any) {
    this.ajv = new Ajv({
      allErrors: true,
      strict: false,
      validateFormats: true
    });
    addFormats(this.ajv);
    this.schema = schema;
  }

  /**
   * Validate configuration against schema and business rules
   */
  public validate(config: any): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationError[] = [];

    // Schema validation
    const schemaValid = this.ajv.validate(this.schema, config);
    if (!schemaValid && this.ajv.errors) {
      for (const error of this.ajv.errors) {
        errors.push({
          field: error.instancePath || 'root',
          message: `${error.message} (${error.keyword})`,
          severity: 'error'
        });
      }
    }

    // Business rule validations
    this.validateBusinessRules(config, errors, warnings);

    return {
      valid: errors.length === 0,
      errors,
      warnings
    };
  }

  /**
   * Validate business rules
   */
  private validateBusinessRules(
    config: any,
    errors: ValidationError[],
    warnings: ValidationError[]
  ): void {
    // Validate state codes
    if (config.geography && !config.geography.nationwide && config.geography.states) {
      this.validateStateCodes(config.geography.states, errors, warnings);
    }

    // Validate appeals module business rules
    if (config.modules?.appeals?.enabled) {
      this.validateAppealsModule(config.modules.appeals, errors, warnings);
    }

    // Validate claims837 module business rules
    if (config.modules?.claims837?.enabled) {
      this.validateClaims837Module(config.modules.claims837, errors, warnings);
    }

    // Validate eligibility270271 module business rules
    if (config.modules?.eligibility270271?.enabled) {
      this.validateEligibilityModule(config.modules.eligibility270271, errors, warnings);
    }

    // Validate attachments275 module business rules
    if (config.modules?.attachments275?.enabled) {
      this.validateAttachmentsModule(config.modules.attachments275, errors, warnings);
    }

    // Validate ECS module business rules
    if (config.modules?.ecs?.enabled) {
      this.validateECSModule(config.modules.ecs, errors, warnings);
    }

    // Validate contact emails
    this.validateContactEmails(config.contacts, errors);

    // Validate at least one module is enabled
    this.validateModulesEnabled(config.modules, warnings);
  }

  /**
   * Validate state codes against valid US states
   */
  private validateStateCodes(states: string[], errors: ValidationError[], warnings: ValidationError[]): void {
    for (const state of states) {
      if (!VALID_US_STATES.includes(state.toUpperCase())) {
        errors.push({
          field: 'geography.states',
          message: `Invalid state code: ${state}. Must be a valid 2-letter US state code.`,
          severity: 'error'
        });
      }
    }

    // Check for duplicates
    const uniqueStates = new Set(states.map(s => s.toUpperCase()));
    if (uniqueStates.size !== states.length) {
      warnings.push({
        field: 'geography.states',
        message: 'Duplicate state codes found',
        severity: 'warning'
      });
    }
  }

  /**
   * Validate appeals module business rules
   */
  private validateAppealsModule(
    appeals: any,
    errors: ValidationError[],
    warnings: ValidationError[]
  ): void {
    // If additionalClaimsSupported is true, maxAdditionalClaims must be > 0
    if (appeals.additionalClaimsSupported && (!appeals.maxAdditionalClaims || appeals.maxAdditionalClaims === 0)) {
      errors.push({
        field: 'modules.appeals.maxAdditionalClaims',
        message: 'maxAdditionalClaims must be greater than 0 when additionalClaimsSupported is true',
        severity: 'error'
      });
    }

    // If additionalClaimsSupported is false, maxAdditionalClaims should be 0
    if (!appeals.additionalClaimsSupported && appeals.maxAdditionalClaims > 0) {
      warnings.push({
        field: 'modules.appeals.maxAdditionalClaims',
        message: 'maxAdditionalClaims should be 0 when additionalClaimsSupported is false',
        severity: 'warning'
      });
    }

    // requestReasons is required when appeals is enabled
    if (!appeals.requestReasons || appeals.requestReasons.length === 0) {
      errors.push({
        field: 'modules.appeals.requestReasons',
        message: 'At least one request reason is required when appeals module is enabled',
        severity: 'error'
      });
    }

    // Validate at least one transaction mode is enabled
    if (!this.hasEnabledTransactionMode(appeals.transactionModes)) {
      errors.push({
        field: 'modules.appeals.transactionModes',
        message: 'At least one transaction mode must be enabled for appeals module',
        severity: 'error'
      });
    }
  }

  /**
   * Validate claims837 module business rules
   */
  private validateClaims837Module(
    claims837: any,
    errors: ValidationError[],
    warnings: ValidationError[]
  ): void {
    // At least one claim type must be supported
    const claimTypes = claims837.claimTypes || {};
    const hasClaimType = claimTypes.professional || claimTypes.institutional || 
                         claimTypes.dental || claimTypes.encounters;
    
    if (!hasClaimType) {
      errors.push({
        field: 'modules.claims837.claimTypes',
        message: 'At least one claim type must be enabled',
        severity: 'error'
      });
    }

    // Validate edits array size
    if (claims837.edits && claims837.edits.length > 8) {
      errors.push({
        field: 'modules.claims837.edits',
        message: 'Maximum 8 custom edits allowed',
        severity: 'error'
      });
    }

    // Validate enrichments array size
    if (claims837.enrichments && claims837.enrichments.length > 5) {
      errors.push({
        field: 'modules.claims837.enrichments',
        message: 'Maximum 5 custom enrichments allowed',
        severity: 'error'
      });
    }

    // Validate at least one transaction mode is enabled
    if (!this.hasEnabledTransactionMode(claims837.transactionModes)) {
      errors.push({
        field: 'modules.claims837.transactionModes',
        message: 'At least one transaction mode must be enabled for claims837 module',
        severity: 'error'
      });
    }
  }

  /**
   * Validate eligibility module business rules
   */
  private validateEligibilityModule(
    eligibility: any,
    errors: ValidationError[],
    warnings: ValidationError[]
  ): void {
    // searchOptions must have at least one option
    if (!eligibility.searchOptions || eligibility.searchOptions.length === 0) {
      errors.push({
        field: 'modules.eligibility270271.searchOptions',
        message: 'At least one search option must be specified',
        severity: 'error'
      });
    }

    // Validate at least one transaction mode is enabled
    if (!this.hasEnabledTransactionMode(eligibility.transactionModes)) {
      errors.push({
        field: 'modules.eligibility270271.transactionModes',
        message: 'At least one transaction mode must be enabled for eligibility270271 module',
        severity: 'error'
      });
    }
  }

  /**
   * Validate attachments module business rules
   */
  private validateAttachmentsModule(
    attachments: any,
    errors: ValidationError[],
    warnings: ValidationError[]
  ): void {
    // Validate at least one transaction mode is enabled
    if (!this.hasEnabledTransactionMode(attachments.transactionModes)) {
      errors.push({
        field: 'modules.attachments275.transactionModes',
        message: 'At least one transaction mode must be enabled for attachments275 module',
        severity: 'error'
      });
    }
  }

  /**
   * Validate ECS module business rules
   */
  private validateECSModule(
    ecs: any,
    errors: ValidationError[],
    warnings: ValidationError[]
  ): void {
    // queryMethods must have at least one method
    if (!ecs.queryMethods || ecs.queryMethods.length === 0) {
      errors.push({
        field: 'modules.ecs.queryMethods',
        message: 'At least one query method must be specified',
        severity: 'error'
      });
    }

    // If cacheEnabled is true, cacheTTL should be set
    if (ecs.cacheEnabled && !ecs.cacheTTL) {
      warnings.push({
        field: 'modules.ecs.cacheTTL',
        message: 'cacheTTL should be specified when cacheEnabled is true',
        severity: 'warning'
      });
    }

    // Validate at least one transaction mode is enabled
    if (!this.hasEnabledTransactionMode(ecs.transactionModes)) {
      errors.push({
        field: 'modules.ecs.transactionModes',
        message: 'At least one transaction mode must be enabled for ecs module',
        severity: 'error'
      });
    }
  }

  /**
   * Validate contact emails
   */
  private validateContactEmails(contacts: any, errors: ValidationError[]): void {
    if (!contacts) return;

    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    const contactTypes = ['technical', 'accountManager', 'escalation', 'additional'];

    for (const type of contactTypes) {
      if (contacts[type] && contacts[type].email) {
        if (!emailRegex.test(contacts[type].email)) {
          errors.push({
            field: `contacts.${type}.email`,
            message: `Invalid email format: ${contacts[type].email}`,
            severity: 'error'
          });
        }
      }
    }
  }

  /**
   * Validate that at least one module is enabled
   */
  private validateModulesEnabled(modules: any, warnings: ValidationError[]): void {
    if (!modules) {
      warnings.push({
        field: 'modules',
        message: 'No modules configured. At least one module should be enabled.',
        severity: 'warning'
      });
      return;
    }

    const hasEnabledModule = 
      modules.claims837?.enabled ||
      modules.eligibility270271?.enabled ||
      modules.claimStatus276277?.enabled ||
      modules.appeals?.enabled ||
      modules.attachments275?.enabled ||
      modules.ecs?.enabled;

    if (!hasEnabledModule) {
      warnings.push({
        field: 'modules',
        message: 'No modules are enabled. At least one module should be enabled.',
        severity: 'warning'
      });
    }
  }

  /**
   * Check if at least one transaction mode is enabled
   */
  private hasEnabledTransactionMode(transactionModes: any): boolean {
    if (!transactionModes) return false;

    return (
      transactionModes.realtime_web?.enabled ||
      transactionModes.realtime_b2b?.enabled ||
      transactionModes.edi_batch?.enabled
    );
  }

  /**
   * Format validation result as human-readable string
   */
  public static formatValidationResult(result: ValidationResult): string {
    const lines: string[] = [];

    if (result.valid) {
      lines.push('✓ Configuration is valid');
    } else {
      lines.push('✗ Configuration validation failed');
    }

    if (result.errors.length > 0) {
      lines.push('\nErrors:');
      for (const error of result.errors) {
        lines.push(`  [${error.field}] ${error.message}`);
      }
    }

    if (result.warnings.length > 0) {
      lines.push('\nWarnings:');
      for (const warning of result.warnings) {
        lines.push(`  [${warning.field}] ${warning.message}`);
      }
    }

    return lines.join('\n');
  }
}

/**
 * Convenience function to validate a configuration
 */
export function validateConfig(
  config: any,
  schema: any
): ValidationResult {
  const validator = new ConfigValidator(schema);
  return validator.validate(config);
}
