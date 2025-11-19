/**
 * Configuration Validator
 * Validates payer configuration against schema and business rules
 */

import Ajv, { ValidateFunction } from 'ajv';
import { PayerConfig, ValidationResult, ValidationError, ValidationWarning } from '../types/payer-config';

export class ConfigValidator {
  private ajv: Ajv;
  private validateFunction?: ValidateFunction;

  constructor() {
    this.ajv = new Ajv({ allErrors: true, strict: false });
    this.initializeSchema();
  }

  private initializeSchema(): void {
    const schema = {
      type: 'object',
      required: ['payerId', 'payerName', 'organizationName', 'contactInfo', 'enabledModules', 'infrastructure', 'monitoring'],
      properties: {
        payerId: { type: 'string', pattern: '^[A-Z0-9]+$', minLength: 3, maxLength: 20 },
        payerName: { type: 'string', minLength: 1, maxLength: 100 },
        organizationName: { type: 'string', minLength: 1, maxLength: 100 },
        contactInfo: {
          type: 'object',
          required: ['primaryContact', 'email', 'phone'],
          properties: {
            primaryContact: { type: 'string' },
            email: { type: 'string', format: 'email' },
            phone: { type: 'string' },
            supportEmail: { type: 'string', format: 'email' },
          },
        },
        enabledModules: {
          type: 'object',
          required: ['appeals', 'ecs', 'attachments', 'authorizations'],
          properties: {
            appeals: { type: 'boolean' },
            ecs: { type: 'boolean' },
            attachments: { type: 'boolean' },
            authorizations: { type: 'boolean' },
          },
        },
      },
    };

    this.validateFunction = this.ajv.compile(schema);
  }

  public validate(config: PayerConfig): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // JSON Schema validation
    if (this.validateFunction && !this.validateFunction(config)) {
      if (this.validateFunction.errors) {
        this.validateFunction.errors.forEach(err => {
          errors.push({
            field: err.instancePath || err.schemaPath,
            message: err.message || 'Validation error',
            value: err.data,
          });
        });
      }
    }

    // Business rule validations
    this.validateBusinessRules(config, errors, warnings);

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  private validateBusinessRules(config: PayerConfig, errors: ValidationError[], warnings: ValidationWarning[]): void {
    // Validate enabled modules have corresponding config
    if (config.enabledModules.appeals && !config.appeals) {
      errors.push({
        field: 'appeals',
        message: 'Appeals module is enabled but configuration is missing',
      });
    }

    if (config.enabledModules.ecs && !config.ecs) {
      errors.push({
        field: 'ecs',
        message: 'ECS module is enabled but configuration is missing',
      });
    }

    if (config.enabledModules.attachments && !config.attachments) {
      errors.push({
        field: 'attachments',
        message: 'Attachments module is enabled but configuration is missing',
      });
    }

    // Validate API endpoints if modules are enabled
    if (config.appeals?.enabled) {
      if (!config.appeals.apiEndpoints.test || !config.appeals.apiEndpoints.prod) {
        errors.push({
          field: 'appeals.apiEndpoints',
          message: 'Both test and prod API endpoints are required',
        });
      }
    }

    if (config.ecs?.enabled) {
      if (!config.ecs.apiEndpoints.test || !config.ecs.apiEndpoints.prod) {
        errors.push({
          field: 'ecs.apiEndpoints',
          message: 'Both test and prod API endpoints are required',
        });
      }
    }

    // Validate resource naming
    if (config.infrastructure.resourceNamePrefix.length > 20) {
      errors.push({
        field: 'infrastructure.resourceNamePrefix',
        message: 'Resource name prefix must be 20 characters or less',
      });
    }

    // Validate environment
    const validEnvironments = ['dev', 'uat', 'prod'];
    if (!validEnvironments.includes(config.infrastructure.environment)) {
      errors.push({
        field: 'infrastructure.environment',
        message: 'Environment must be one of: dev, uat, prod',
        value: config.infrastructure.environment,
      });
    }

    // Add warnings for best practices
    if (config.monitoring.applicationInsights.samplingPercentage < 100) {
      warnings.push({
        field: 'monitoring.applicationInsights.samplingPercentage',
        message: 'Sampling percentage is less than 100%, some telemetry may be lost',
        suggestion: 'Consider using 100% sampling in non-production environments',
      });
    }

    if (config.infrastructure.logicAppConfig.workerCount < 2 && config.infrastructure.environment === 'prod') {
      warnings.push({
        field: 'infrastructure.logicAppConfig.workerCount',
        message: 'Production environment should have at least 2 workers for high availability',
        suggestion: 'Increase workerCount to 2 or more',
      });
    }
  }
}

export class DeploymentValidator extends ConfigValidator {
  public validateForGeneration(config: PayerConfig): ValidationResult {
    const result = this.validate(config);

    // Additional validation for generation
    const errors: ValidationError[] = [...result.errors];
    const warnings: ValidationWarning[] = [...result.warnings];

    // Ensure at least one module is enabled
    const hasEnabledModule = Object.values(config.enabledModules).some(enabled => enabled);
    if (!hasEnabledModule) {
      errors.push({
        field: 'enabledModules',
        message: 'At least one module must be enabled',
      });
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  public validateRequiredModules(config: PayerConfig): ValidationResult {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Check for module-specific requirements
    if (config.enabledModules.attachments && config.attachments) {
      if (!config.attachments.sftpConfig.host) {
        errors.push({
          field: 'attachments.sftpConfig.host',
          message: 'SFTP host is required when attachments module is enabled',
        });
      }
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  public async validateEndpoints(config: PayerConfig): Promise<ValidationResult> {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Validate endpoint URLs format
    if (config.appeals?.apiEndpoints) {
      if (!this.isValidUrl(config.appeals.apiEndpoints.test)) {
        errors.push({
          field: 'appeals.apiEndpoints.test',
          message: 'Invalid URL format',
          value: config.appeals.apiEndpoints.test,
        });
      }
      if (!this.isValidUrl(config.appeals.apiEndpoints.prod)) {
        errors.push({
          field: 'appeals.apiEndpoints.prod',
          message: 'Invalid URL format',
          value: config.appeals.apiEndpoints.prod,
        });
      }
    }

    if (config.ecs?.apiEndpoints) {
      if (!this.isValidUrl(config.ecs.apiEndpoints.test)) {
        errors.push({
          field: 'ecs.apiEndpoints.test',
          message: 'Invalid URL format',
          value: config.ecs.apiEndpoints.test,
        });
      }
      if (!this.isValidUrl(config.ecs.apiEndpoints.prod)) {
        errors.push({
          field: 'ecs.apiEndpoints.prod',
          message: 'Invalid URL format',
          value: config.ecs.apiEndpoints.prod,
        });
      }
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  public async validateConnectivity(config: PayerConfig): Promise<ValidationResult> {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    // Note: Actual connectivity tests would go here
    // For now, just validate that endpoints are configured
    warnings.push({
      field: 'connectivity',
      message: 'Connectivity validation not implemented - manual verification required',
      suggestion: 'Test API endpoints after deployment',
    });

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  public generateValidationReport(config: PayerConfig): string {
    const result = this.validateForGeneration(config);
    
    let report = '=== Payer Configuration Validation Report ===\n\n';
    report += `Payer ID: ${config.payerId}\n`;
    report += `Payer Name: ${config.payerName}\n`;
    report += `Organization: ${config.organizationName}\n`;
    report += `Environment: ${config.infrastructure.environment}\n\n`;

    report += `Enabled Modules:\n`;
    report += `  - Appeals: ${config.enabledModules.appeals ? '✓' : '✗'}\n`;
    report += `  - ECS: ${config.enabledModules.ecs ? '✓' : '✗'}\n`;
    report += `  - Attachments: ${config.enabledModules.attachments ? '✓' : '✗'}\n`;
    report += `  - Authorizations: ${config.enabledModules.authorizations ? '✓' : '✗'}\n\n`;

    if (result.errors.length > 0) {
      report += `❌ ERRORS (${result.errors.length}):\n`;
      result.errors.forEach((error, idx) => {
        report += `  ${idx + 1}. [${error.field}] ${error.message}\n`;
        if (error.value) {
          report += `     Value: ${JSON.stringify(error.value)}\n`;
        }
      });
      report += '\n';
    }

    if (result.warnings.length > 0) {
      report += `⚠️  WARNINGS (${result.warnings.length}):\n`;
      result.warnings.forEach((warning, idx) => {
        report += `  ${idx + 1}. [${warning.field}] ${warning.message}\n`;
        if (warning.suggestion) {
          report += `     Suggestion: ${warning.suggestion}\n`;
        }
      });
      report += '\n';
    }

    if (result.valid) {
      report += '✅ Configuration is valid and ready for generation\n';
    } else {
      report += '❌ Configuration has errors that must be fixed before generation\n';
    }

    return report;
  }

  private isValidUrl(url: string): boolean {
    try {
      new URL(url);
      return true;
    } catch {
      return false;
    }
  }
}
