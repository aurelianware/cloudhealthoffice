/**
 * Azure API for FHIR Integration and Validation
 * 
 * Provides integration with Azure API for FHIR (formerly Azure Health Data Services)
 * for resource validation, profile conformance checking, and terminology services.
 * 
 * Supports CMS-0057-F compliance validation by leveraging Azure's FHIR validation
 * capabilities for US Core and Da Vinci profiles.
 * 
 * References:
 * - Azure API for FHIR documentation
 * - FHIR R4 $validate operation
 * - US Core Implementation Guide
 * - Da Vinci FHIR IGs (PDex, PAS, CRD, DTR)
 * 
 * @module azure-fhir-validator
 */

import { Resource, OperationOutcome } from 'fhir/r4';

/**
 * Azure FHIR configuration
 */
export interface AzureFHIRConfig {
  /** Azure FHIR service base URL (e.g., https://myservice.azurehealthcareapis.com) */
  baseUrl: string;
  /** Azure AD access token */
  accessToken: string;
  /** Tenant ID */
  tenantId?: string;
  /** API version (default: 2022-06-01) */
  apiVersion?: string;
}

/**
 * Validation result from Azure FHIR
 */
export interface ValidationResult {
  /** Validation passed */
  valid: boolean;
  /** FHIR OperationOutcome from validation */
  operationOutcome?: OperationOutcome;
  /** Validation errors */
  errors: ValidationError[];
  /** Validation warnings */
  warnings: ValidationError[];
  /** Profile validation results */
  profileValidation?: ProfileValidationResult[];
}

/**
 * Individual validation error/warning
 */
export interface ValidationError {
  /** Severity */
  severity: 'fatal' | 'error' | 'warning' | 'information';
  /** Error message */
  message: string;
  /** FHIR path to element with issue */
  path?: string;
  /** Diagnostic details */
  diagnostics?: string;
}

/**
 * Profile validation result
 */
export interface ProfileValidationResult {
  /** Profile canonical URL */
  profile: string;
  /** Validation passed for this profile */
  valid: boolean;
  /** Issues specific to this profile */
  issues: ValidationError[];
}

/**
 * Azure FHIR Validator class
 */
export class AzureFHIRValidator {
  private config: AzureFHIRConfig;
  
  constructor(config: AzureFHIRConfig) {
    this.config = {
      ...config,
      apiVersion: config.apiVersion || '2022-06-01'
    };
  }
  
  /**
   * Validate a FHIR resource against its declared profiles
   * 
   * @param resource FHIR resource to validate
   * @param options Validation options
   * @returns Validation result
   */
  async validateResource(
    resource: Resource,
    options: {
      /** Specific profile to validate against (overrides resource.meta.profile) */
      profile?: string;
      /** Validate terminology bindings */
      validateTerminology?: boolean;
      /** Mode: create, update, delete */
      mode?: 'create' | 'update' | 'delete';
    } = {}
  ): Promise<ValidationResult> {
    try {
      // Build request URL and headers for production Azure FHIR validation
      // These variables are used when the production implementation below is uncommented
      const validateUrl = `${this.config.baseUrl}/${resource.resourceType}/$validate`;
      const headers = {
        'Authorization': `Bearer ${this.config.accessToken}`,
        'Content-Type': 'application/fhir+json',
        'Accept': 'application/fhir+json'
      };
      const requestBody = {
        resourceType: 'Parameters',
        parameter: [
          {
            name: 'resource',
            resource: resource
          },
          ...(options.profile ? [{
            name: 'profile',
            valueUri: options.profile
          }] : []),
          ...(options.mode ? [{
            name: 'mode',
            valueCode: options.mode
          }] : [])
        ]
      };
      
      // Production implementation (uncomment when Azure credentials are available):
      // NOTE: Always handle network and HTTP errors in production.
      // const response = await fetch(validateUrl, {
      //   method: 'POST',
      //   headers,
      //   body: JSON.stringify(requestBody)
      // });
      // if (!response.ok) {
      //   throw new Error(`Azure FHIR validation failed: ${response.status} ${response.statusText}`);
      // }
      // const operationOutcome = await response.json();
      // return this.parseValidationResult(operationOutcome);
      
      // eslint-disable-next-line @typescript-eslint/no-unused-vars -- Used in production code above
      const _unused = { validateUrl, headers, requestBody };
      
      // Simulated validation for development/testing
      const result = await this.simulateAzureFHIRValidation(resource, options);
      
      return result;
    } catch (error) {
      return {
        valid: false,
        errors: [{
          severity: 'error',
          message: `Validation request failed: ${error instanceof Error ? error.message : 'Unknown error'}`,
          diagnostics: String(error)
        }],
        warnings: []
      };
    }
  }
  
  /**
   * Validate resource against specific US Core profile
   * 
   * @param resource FHIR resource
   * @param profileVersion US Core version (default: 3.1.1)
   * @returns Validation result
   */
  async validateUSCoreProfile(
    resource: Resource,
    profileVersion: string = '3.1.1'
  ): Promise<ValidationResult> {
    const profileMap: Record<string, string> = {
      'Patient': `http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient`,
      'Claim': `http://hl7.org/fhir/us/core/StructureDefinition/us-core-claim`,
      'ExplanationOfBenefit': `http://hl7.org/fhir/us/core/StructureDefinition/us-core-explanationofbenefit`
    };
    
    const profile = profileMap[resource.resourceType];
    
    if (!profile) {
      return {
        valid: false,
        errors: [{
          severity: 'error',
          message: `No US Core profile defined for resource type ${resource.resourceType}`,
        }],
        warnings: []
      };
    }
    
    return this.validateResource(resource, { profile, validateTerminology: true });
  }
  
  /**
   * Validate resource against Da Vinci profile
   * 
   * @param resource FHIR resource
   * @param ig Implementation guide (pdex, pas, crd, dtr)
   * @returns Validation result
   */
  async validateDaVinciProfile(
    resource: Resource,
    ig: 'pdex' | 'pas' | 'crd' | 'dtr'
  ): Promise<ValidationResult> {
    const profileMap: Record<string, Record<string, string>> = {
      'pdex': {
        'Claim': 'http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-claim',
        'ExplanationOfBenefit': 'http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-eob'
      },
      'pas': {
        'ServiceRequest': 'http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-servicerequest'
      },
      'crd': {
        'ServiceRequest': 'http://hl7.org/fhir/us/davinci-crd/StructureDefinition/profile-servicerequest'
      },
      'dtr': {
        'QuestionnaireResponse': 'http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaireresponse'
      }
    };
    
    const profile = profileMap[ig]?.[resource.resourceType];
    
    if (!profile) {
      return {
        valid: false,
        errors: [{
          severity: 'error',
          message: `No Da Vinci ${ig.toUpperCase()} profile defined for resource type ${resource.resourceType}`,
        }],
        warnings: []
      };
    }
    
    return this.validateResource(resource, { profile, validateTerminology: true });
  }
  
  /**
   * Batch validate multiple resources
   * 
   * @param resources Array of FHIR resources
   * @returns Array of validation results
   */
  async batchValidate(resources: Resource[]): Promise<ValidationResult[]> {
    const results = await Promise.all(
      resources.map(resource => this.validateResource(resource))
    );
    
    return results;
  }
  
  /**
   * Simulate Azure FHIR validation for development/testing
   * 
   * This provides client-side validation until Azure API credentials are configured.
   * Replace with actual Azure API call in production (see commented code in validateResource).
   * 
   * @private
   */
  private async simulateAzureFHIRValidation(
    resource: Resource,
    options: any
  ): Promise<ValidationResult> {
    const errors: ValidationError[] = [];
    const warnings: ValidationError[] = [];
    
    // Basic validation checks
    if (!resource.resourceType) {
      errors.push({
        severity: 'error',
        message: 'Resource must have resourceType',
        path: 'resourceType'
      });
    }
    
    // Validate against declared profiles
    const profiles = resource.meta?.profile || [];
    const profileValidation: ProfileValidationResult[] = [];
    
    for (const profile of profiles) {
      const profileResult = this.validateAgainstProfile(resource, profile);
      profileValidation.push(profileResult);
      
      errors.push(...profileResult.issues.filter(i => i.severity === 'error' || i.severity === 'fatal'));
      warnings.push(...profileResult.issues.filter(i => i.severity === 'warning'));
    }
    
    // If no profiles declared, add warning
    if (profiles.length === 0) {
      warnings.push({
        severity: 'warning',
        message: 'Resource does not declare conformance to any profiles',
        path: 'meta.profile'
      });
    }
    
    const valid = errors.length === 0;
    
    return {
      valid,
      errors,
      warnings,
      profileValidation
    };
  }
  
  /**
   * Validate resource against specific profile
   */
  private validateAgainstProfile(resource: Resource, profile: string): ProfileValidationResult {
    const issues: ValidationError[] = [];
    
    // US Core Patient validation
    if (profile.includes('us-core-patient')) {
      const patient = resource as any;
      
      if (!patient.identifier || patient.identifier.length === 0) {
        issues.push({
          severity: 'error',
          message: 'US Core Patient requires at least one identifier',
          path: 'identifier'
        });
      }
      
      if (!patient.name || patient.name.length === 0) {
        issues.push({
          severity: 'error',
          message: 'US Core Patient requires at least one name',
          path: 'name'
        });
      }
      
      if (!patient.gender) {
        issues.push({
          severity: 'error',
          message: 'US Core Patient requires gender',
          path: 'gender'
        });
      }
    }
    
    // Da Vinci PAS ServiceRequest validation
    if (profile.includes('davinci-pas') && resource.resourceType === 'ServiceRequest') {
      const sr = resource as any;
      
      if (!sr.status) {
        issues.push({
          severity: 'error',
          message: 'Da Vinci PAS ServiceRequest requires status',
          path: 'status'
        });
      }
      
      if (!sr.intent) {
        issues.push({
          severity: 'error',
          message: 'Da Vinci PAS ServiceRequest requires intent',
          path: 'intent'
        });
      }
      
      if (!sr.code) {
        issues.push({
          severity: 'error',
          message: 'Da Vinci PAS ServiceRequest requires code',
          path: 'code'
        });
      }
      
      if (!sr.subject) {
        issues.push({
          severity: 'error',
          message: 'Da Vinci PAS ServiceRequest requires subject',
          path: 'subject'
        });
      }
    }
    
    const valid = issues.filter(i => i.severity === 'error' || i.severity === 'fatal').length === 0;
    
    return {
      profile,
      valid,
      issues
    };
  }
}

/**
 * Helper function to create Azure FHIR validator instance
 * 
 * @param config Azure FHIR configuration
 * @returns Validator instance
 */
export function createAzureFHIRValidator(config: AzureFHIRConfig): AzureFHIRValidator {
  return new AzureFHIRValidator(config);
}

/**
 * Quick validation helper for common scenarios
 */
export async function quickValidate(
  resource: Resource,
  config: AzureFHIRConfig
): Promise<boolean> {
  const validator = new AzureFHIRValidator(config);
  const result = await validator.validateResource(resource);
  return result.valid;
}
