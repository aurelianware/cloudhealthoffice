/**
 * Provider Access API - CMS-0057-F Final Rule Implementation
 * 
 * Implements Provider Access API aligning with CMS-0057-F requirements for provider data access.
 * Supports FHIR R4 endpoints for healthcare providers to access patient data with proper
 * authentication, authorization, and consent management.
 * 
 * References:
 * - CMS-0057-F Final Rule (Prior Authorization and Provider Access)
 * - HL7 Da Vinci Payer Data Exchange (PDex) Implementation Guide
 * - US Core Implementation Guide v3.1.1+
 * - FHIR R4.0.1 Specification
 * - SMART on FHIR Authorization Guide
 * 
 * Key Features:
 * - FHIR R4 endpoint support (search and read)
 * - SMART on FHIR authentication (OpenID Connect/OAuth2)
 * - Patient consent validation
 * - HIPAA safeguards (encryption, audit logging, PHI redaction)
 * - Backend data mapping (QNXT to FHIR)
 * - US Core and Da Vinci PDex compliance
 */

import {
  Patient,
  Claim,
  Encounter,
  ExplanationOfBenefit,
  Condition,
  Observation,
  Bundle,
  OperationOutcome,
  Resource,
  Reference
} from 'fhir/r4';
import * as crypto from 'crypto';

// ============================================================================
// Type Definitions
// ============================================================================

/**
 * SMART on FHIR Token - OpenID Connect/OAuth2 for EHR/Provider authentication
 */
export interface SmartOnFhirToken {
  /** Access token for API requests */
  access_token: string;
  /** Token type (typically 'Bearer') */
  token_type: string;
  /** Expiration time in seconds */
  expires_in: number;
  /** Refresh token for token renewal */
  refresh_token?: string;
  /** Scopes granted to the token */
  scope: string;
  /** Patient context (if patient-specific scope) */
  patient?: string;
  /** Provider NPI or identifier */
  provider?: string;
  /** ID token (OpenID Connect) */
  id_token?: string;
}

/**
 * Patient consent record for data access
 */
export interface PatientConsent {
  /** Patient identifier */
  patientId: string;
  /** Provider identifier (NPI) */
  providerId: string;
  /** Consent status */
  status: 'active' | 'inactive' | 'revoked' | 'pending';
  /** Consent effective date */
  effectiveDate: string;
  /** Consent expiration date (if applicable) */
  expirationDate?: string;
  /** Scope of data access granted */
  scope: string[];
  /** Purpose of data access */
  purpose?: string;
}

/**
 * Search parameters for FHIR resources
 */
export interface SearchParameters {
  /** Resource type */
  resourceType: string;
  /** Patient identifier */
  patient?: string;
  /** Date range filter (start) */
  date?: string;
  /** Date range filter (end) */
  'date-end'?: string;
  /** Status filter */
  status?: string;
  /** Category filter */
  category?: string;
  /** Type filter */
  type?: string;
  /** Clinical status */
  'clinical-status'?: string;
  /** Page number */
  _page?: number;
  /** Results per page */
  _count?: number;
}

/**
 * QNXT backend data structure (sample - customize per actual QNXT API)
 */
export interface QnxtPatient {
  memberId: string;
  firstName: string;
  lastName: string;
  middleName?: string;
  dob: string;
  gender: string;
  ssn?: string;
  address?: {
    street1?: string;
    street2?: string;
    city?: string;
    state?: string;
    zip?: string;
  };
  phone?: string;
  email?: string;
}

export interface QnxtClaim {
  claimId: string;
  memberId: string;
  providerId: string;
  claimType: string;
  serviceDate: string;
  diagnosisCodes: string[];
  procedureCodes: string[];
  totalCharged: number;
  totalPaid: number;
  status: string;
}

export interface QnxtEncounter {
  encounterId: string;
  memberId: string;
  providerId: string;
  encounterType: string;
  encounterDate: string;
  diagnosisCodes: string[];
  status: string;
}

/**
 * Audit log entry for HIPAA compliance
 */
export interface AuditLogEntry {
  /** Timestamp of the event */
  timestamp: string;
  /** Event type */
  eventType: 'access' | 'search' | 'read' | 'consent_check' | 'auth_failure' | 'consent_denial';
  /** User/Provider identifier */
  userId: string;
  /** Patient identifier (if applicable) */
  patientId?: string;
  /** Resource type accessed */
  resourceType?: string;
  /** Resource ID accessed */
  resourceId?: string;
  /** Action result */
  result: 'success' | 'failure';
  /** IP address */
  ipAddress?: string;
  /** Additional details */
  details?: string;
}

/**
 * FHIR OperationOutcome issue type codes
 * Based on FHIR R4 IssueType value set
 */
export type OperationOutcomeCode = 
  | 'invalid' 
  | 'structure' 
  | 'required' 
  | 'value' 
  | 'invariant' 
  | 'security' 
  | 'login' 
  | 'unknown' 
  | 'expired' 
  | 'forbidden' 
  | 'suppressed' 
  | 'processing' 
  | 'not-supported' 
  | 'duplicate' 
  | 'multiple-matches' 
  | 'not-found' 
  | 'deleted' 
  | 'too-long' 
  | 'code-invalid' 
  | 'extension' 
  | 'too-costly' 
  | 'business-rule' 
  | 'conflict' 
  | 'transient' 
  | 'lock-error' 
  | 'no-store' 
  | 'exception' 
  | 'timeout' 
  | 'incomplete' 
  | 'throttled' 
  | 'informational';

// ============================================================================
// Error Classes
// ============================================================================

export class AuthenticationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'AuthenticationError';
  }
}

export class ConsentDeniedError extends Error {
  constructor(message: string, public patientId?: string, public providerId?: string) {
    super(message);
    this.name = 'ConsentDeniedError';
  }
}

export class ResourceNotFoundError extends Error {
  constructor(message: string, public resourceType?: string, public resourceId?: string) {
    super(message);
    this.name = 'ResourceNotFoundError';
  }
}

// ============================================================================
// Provider Access API Class
// ============================================================================

export class ProviderAccessApi {
  private encryptionKey: string;
  private auditLogs: AuditLogEntry[] = [];

  /**
   * Initialize Provider Access API
   * @param encryptionKey - Key for PHI encryption (from Azure Key Vault in production)
   */
  constructor(encryptionKey?: string) {
    this.encryptionKey = encryptionKey || this.generateEncryptionKey();
  }

  // ==========================================================================
  // Authentication & Authorization
  // ==========================================================================

  /**
   * Validate SMART on FHIR token
   * In production, this would validate against Azure AD/OIDC provider
   */
  async validateSmartToken(token: string): Promise<SmartOnFhirToken> {
    // In production: validate JWT signature, expiration, issuer, audience
    // For now, basic validation
    if (!token || token.length < 10) {
      this.logAudit({
        timestamp: new Date().toISOString(),
        eventType: 'auth_failure',
        userId: 'unknown',
        result: 'failure',
        details: 'Invalid or missing token'
      });
      throw new AuthenticationError('Invalid or missing SMART on FHIR token');
    }

    // Mock token parsing - in production, decode JWT
    const parsedToken: SmartOnFhirToken = {
      access_token: token,
      token_type: 'Bearer',
      expires_in: 3600,
      scope: 'patient/*.read',
      provider: this.extractProviderFromToken(token)
    };

    return parsedToken;
  }

  /**
   * Check patient consent for provider access
   */
  async checkConsent(patientId: string, providerId: string): Promise<boolean> {
    // In production: query consent database or FHIR Consent resources
    // For this implementation, we'll simulate consent checking
    
    const consent = await this.getPatientConsent(patientId, providerId);
    
    if (!consent) {
      this.logAudit({
        timestamp: new Date().toISOString(),
        eventType: 'consent_check',
        userId: providerId,
        patientId: patientId,
        result: 'failure',
        details: 'No consent record found'
      });
      return false;
    }

    if (consent.status !== 'active') {
      this.logAudit({
        timestamp: new Date().toISOString(),
        eventType: 'consent_denial',
        userId: providerId,
        patientId: patientId,
        result: 'failure',
        details: `Consent status: ${consent.status}`
      });
      return false;
    }

    // Check if consent is expired
    if (consent.expirationDate) {
      const expiration = new Date(consent.expirationDate);
      if (expiration < new Date()) {
        this.logAudit({
          timestamp: new Date().toISOString(),
          eventType: 'consent_denial',
          userId: providerId,
          patientId: patientId,
          result: 'failure',
          details: 'Consent expired'
        });
        return false;
      }
    }

    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'consent_check',
      userId: providerId,
      patientId: patientId,
      result: 'success',
      details: 'Consent validated'
    });

    return true;
  }

  /**
   * Get patient consent record
   * In production: query from database or FHIR Consent resource
   */
  private async getPatientConsent(patientId: string, providerId: string): Promise<PatientConsent | null> {
    // Mock implementation - in production, query actual consent database
    // For testing purposes, we'll return a mock consent
    return {
      patientId,
      providerId,
      status: 'active',
      effectiveDate: new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString(), // 30 days ago
      expirationDate: new Date(Date.now() + 335 * 24 * 60 * 60 * 1000).toISOString(), // ~1 year from now
      scope: ['patient/*.read', 'patient/Claim.read', 'patient/ExplanationOfBenefit.read'],
      purpose: 'Treatment'
    };
  }

  // ==========================================================================
  // FHIR Resource Operations
  // ==========================================================================

  /**
   * Search for FHIR resources
   * Implements GET /[ResourceType]?params
   */
  async searchResources(
    resourceType: string,
    params: SearchParameters,
    token: string
  ): Promise<Bundle> {
    // Validate token
    const smartToken = await this.validateSmartToken(token);
    const providerId = smartToken.provider || 'unknown';

    // Validate patient consent if patient-specific search
    if (params.patient) {
      const hasConsent = await this.checkConsent(params.patient, providerId);
      if (!hasConsent) {
        throw new ConsentDeniedError(
          `Provider ${providerId} does not have consent to access patient ${params.patient} data`,
          params.patient,
          providerId
        );
      }
    }

    // Log access
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'search',
      userId: providerId,
      patientId: params.patient,
      resourceType,
      result: 'success',
      details: `Search: ${resourceType} with params ${JSON.stringify(params)}`
    });

    // Perform search based on resource type
    let entries: Resource[] = [];

    switch (resourceType) {
      case 'Patient':
        entries = await this.searchPatients(params);
        break;
      case 'Claim':
        entries = await this.searchClaims(params);
        break;
      case 'Encounter':
        entries = await this.searchEncounters(params);
        break;
      case 'ExplanationOfBenefit':
        entries = await this.searchExplanationOfBenefits(params);
        break;
      case 'Condition':
        entries = await this.searchConditions(params);
        break;
      case 'Observation':
        entries = await this.searchObservations(params);
        break;
      default:
        throw new Error(`Resource type ${resourceType} not supported`);
    }

    // Build FHIR Bundle
    const bundle: Bundle = {
      resourceType: 'Bundle',
      type: 'searchset',
      total: entries.length,
      entry: entries.map(resource => ({
        fullUrl: `${resourceType}/${resource.id}`,
        resource: resource as any
      }))
    };

    return bundle;
  }

  /**
   * Read a specific FHIR resource
   * Implements GET /[ResourceType]/[id]
   */
  async readResource(
    resourceType: string,
    resourceId: string,
    token: string
  ): Promise<Resource> {
    // Validate token
    const smartToken = await this.validateSmartToken(token);
    const providerId = smartToken.provider || 'unknown';

    // Get resource to check patient ID
    const resource = await this.getResourceById(resourceType, resourceId);
    
    if (!resource) {
      throw new ResourceNotFoundError(
        `${resourceType}/${resourceId} not found`,
        resourceType,
        resourceId
      );
    }

    // Extract patient ID from resource
    const patientId = this.extractPatientId(resource);
    
    if (patientId) {
      // Check consent
      const hasConsent = await this.checkConsent(patientId, providerId);
      if (!hasConsent) {
        throw new ConsentDeniedError(
          `Provider ${providerId} does not have consent to access patient ${patientId} data`,
          patientId,
          providerId
        );
      }
    }

    // Log access
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'read',
      userId: providerId,
      patientId,
      resourceType,
      resourceId,
      result: 'success',
      details: `Read: ${resourceType}/${resourceId}`
    });

    return resource;
  }

  // ==========================================================================
  // QNXT to FHIR Mapping
  // ==========================================================================

  /**
   * Map QNXT patient data to FHIR Patient resource
   */
  mapQnxtPatientToFhir(qnxtPatient: QnxtPatient): Patient {
    const patient: Patient = {
      resourceType: 'Patient',
      id: qnxtPatient.memberId,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
      },
      identifier: [
        {
          use: 'official',
          type: {
            coding: [{
              system: 'http://terminology.hl7.org/CodeSystem/v2-0203',
              code: 'MB',
              display: 'Member Number'
            }]
          },
          value: qnxtPatient.memberId
        }
      ],
      active: true,
      name: [
        {
          use: 'official',
          family: qnxtPatient.lastName,
          given: [qnxtPatient.firstName, qnxtPatient.middleName].filter(Boolean) as string[]
        }
      ],
      gender: this.mapGenderToFhir(qnxtPatient.gender),
      birthDate: this.normalizeDateFormat(qnxtPatient.dob)
    };

    // Add address if available
    if (qnxtPatient.address) {
      patient.address = [{
        use: 'home',
        line: [qnxtPatient.address.street1, qnxtPatient.address.street2].filter(Boolean) as string[],
        city: qnxtPatient.address.city,
        state: qnxtPatient.address.state,
        postalCode: qnxtPatient.address.zip
      }];
    }

    // Add telecom if available
    const telecom = [];
    if (qnxtPatient.phone) {
      telecom.push({
        system: 'phone' as const,
        value: qnxtPatient.phone,
        use: 'home' as const
      });
    }
    if (qnxtPatient.email) {
      telecom.push({
        system: 'email' as const,
        value: qnxtPatient.email,
        use: 'home' as const
      });
    }
    if (telecom.length > 0) {
      patient.telecom = telecom;
    }

    return patient;
  }

  /**
   * Map QNXT claim data to FHIR Claim resource
   */
  mapQnxtClaimToFhir(qnxtClaim: QnxtClaim): Claim {
    const claim: Claim = {
      resourceType: 'Claim',
      id: qnxtClaim.claimId,
      status: this.mapClaimStatus(qnxtClaim.status),
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/claim-type',
          code: qnxtClaim.claimType.toLowerCase(),
          display: qnxtClaim.claimType
        }]
      },
      use: 'claim',
      patient: {
        reference: `Patient/${qnxtClaim.memberId}`
      },
      created: new Date().toISOString(),
      provider: {
        reference: `Practitioner/${qnxtClaim.providerId}`
      },
      priority: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/processpriority',
          code: 'normal'
        }]
      },
      insurance: [{
        sequence: 1,
        focal: true,
        coverage: {
          reference: `Coverage/${qnxtClaim.memberId}`
        }
      }],
      diagnosis: qnxtClaim.diagnosisCodes.map((code, index) => ({
        sequence: index + 1,
        diagnosisCodeableConcept: {
          coding: [{
            system: 'http://hl7.org/fhir/sid/icd-10',
            code: code
          }]
        }
      })),
      item: qnxtClaim.procedureCodes.map((code, index) => ({
        sequence: index + 1,
        productOrService: {
          coding: [{
            system: 'http://www.ama-assn.org/go/cpt',
            code: code
          }]
        },
        servicedDate: qnxtClaim.serviceDate
      })),
      total: {
        value: qnxtClaim.totalCharged,
        currency: 'USD'
      }
    };

    return claim;
  }

  /**
   * Map QNXT encounter to FHIR Encounter resource
   */
  mapQnxtEncounterToFhir(qnxtEncounter: QnxtEncounter): Encounter {
    const encounter: Encounter = {
      resourceType: 'Encounter',
      id: qnxtEncounter.encounterId,
      status: this.mapEncounterStatus(qnxtEncounter.status),
      class: {
        system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode',
        code: this.mapEncounterClass(qnxtEncounter.encounterType)
      },
      subject: {
        reference: `Patient/${qnxtEncounter.memberId}`
      },
      participant: [{
        individual: {
          reference: `Practitioner/${qnxtEncounter.providerId}`
        }
      }],
      period: {
        start: qnxtEncounter.encounterDate
      },
      diagnosis: qnxtEncounter.diagnosisCodes.map((code, index) => ({
        condition: {
          reference: `Condition/${qnxtEncounter.encounterId}-COND${index + 1}`,
          display: code
        },
        rank: index + 1
      }))
    };

    return encounter;
  }

  // ==========================================================================
  // HIPAA Safeguards
  // ==========================================================================

  /**
   * Encrypt PHI data
   * Uses AES-256-GCM encryption
   */
  encryptPhi(data: string): string {
    const iv = crypto.randomBytes(16);
    const cipher = crypto.createCipheriv('aes-256-gcm', Buffer.from(this.encryptionKey, 'hex'), iv);
    
    let encrypted = cipher.update(data, 'utf8', 'hex');
    encrypted += cipher.final('hex');
    
    const authTag = cipher.getAuthTag();
    
    // Return: iv:authTag:encryptedData
    return `${iv.toString('hex')}:${authTag.toString('hex')}:${encrypted}`;
  }

  /**
   * Decrypt PHI data
   */
  decryptPhi(encryptedData: string): string {
    const parts = encryptedData.split(':');
    if (parts.length !== 3) {
      throw new Error('Invalid encrypted data format');
    }

    const [ivHex, authTagHex, encrypted] = parts;
    const iv = Buffer.from(ivHex, 'hex');
    const authTag = Buffer.from(authTagHex, 'hex');
    
    const decipher = crypto.createDecipheriv('aes-256-gcm', Buffer.from(this.encryptionKey, 'hex'), iv);
    decipher.setAuthTag(authTag);
    
    let decrypted = decipher.update(encrypted, 'hex', 'utf8');
    decrypted += decipher.final('utf8');
    
    return decrypted;
  }

  /**
   * Redact PHI from resource for logging
   */
  redactPhi<T extends Resource>(resource: T): T {
    const redacted = JSON.parse(JSON.stringify(resource)) as T;
    const mutable = redacted as unknown as Record<string, any>;

    // Redact common PHI fields
    if (mutable.resourceType === 'Patient') {
      if (mutable.name) {
        mutable.name = mutable.name.map((n: any) => ({ ...n, family: '***', given: ['***'] }));
      }
      if (mutable.birthDate) {
        mutable.birthDate = '****-**-**';
      }
      if (mutable.address) {
        mutable.address = mutable.address.map((a: any) => ({ ...a, line: ['***'], city: '***', postalCode: '***' }));
      }
      if (mutable.telecom) {
        mutable.telecom = mutable.telecom.map((t: any) => ({ ...t, value: '***' }));
      }
    }

    return redacted;
  }

  /**
   * Log audit entry
   */
  logAudit(entry: AuditLogEntry): void {
    // In production: send to Azure Application Insights or Log Analytics
    this.auditLogs.push(entry);
    
    // For development: log to console
    console.log(`[AUDIT] ${entry.timestamp} - ${entry.eventType} - ${entry.result} - User: ${entry.userId} - ${entry.details || ''}`);
  }

  /**
   * Get audit logs (for testing and compliance reporting)
   */
  getAuditLogs(): AuditLogEntry[] {
    return [...this.auditLogs];
  }

  // ==========================================================================
  // Error Handling
  // ==========================================================================

  /**
   * Create FHIR OperationOutcome for errors
   */
  createErrorOutcome(
    severity: 'fatal' | 'error' | 'warning' | 'information',
    code: OperationOutcomeCode,
    message: string
  ): OperationOutcome {
    return {
      resourceType: 'OperationOutcome',
      issue: [{
        severity,
        code,
        diagnostics: message
      }]
    };
  }

  /**
   * Handle authentication error
   */
  handleAuthError(error: AuthenticationError): { status: number; body: OperationOutcome } {
    return {
      status: 401,
      body: this.createErrorOutcome('error', 'login', error.message)
    };
  }

  /**
   * Handle consent denied error
   */
  handleConsentError(error: ConsentDeniedError): { status: number; body: OperationOutcome } {
    return {
      status: 403,
      body: this.createErrorOutcome('error', 'forbidden', error.message)
    };
  }

  /**
   * Handle resource not found error
   */
  handleNotFoundError(error: ResourceNotFoundError): { status: number; body: OperationOutcome } {
    return {
      status: 404,
      body: this.createErrorOutcome('error', 'not-found', error.message)
    };
  }

  // ==========================================================================
  // Private Helper Methods
  // ==========================================================================

  private generateEncryptionKey(): string {
    // Generate 32-byte (256-bit) key for AES-256
    return crypto.randomBytes(32).toString('hex');
  }

  private extractProviderFromToken(token: string): string {
    // In production: decode JWT and extract provider identifier
    // For mock: extract from token string if formatted as "provider:NPI123:..."
    const parts = token.split(':');
    if (parts.length >= 2 && parts[0] === 'provider') {
      return parts[1];
    }
    return 'PROVIDER_UNKNOWN';
  }

  private extractPatientId(resource: Resource): string | undefined {
    // Extract patient reference from various resource types
    if ('subject' in resource && resource.subject) {
      const ref = (resource.subject as Reference).reference;
      return ref?.replace('Patient/', '');
    }
    if ('patient' in resource && resource.patient) {
      const ref = (resource.patient as Reference).reference;
      return ref?.replace('Patient/', '');
    }
    if (resource.resourceType === 'Patient' && resource.id) {
      return resource.id;
    }
    return undefined;
  }

  private async getResourceById(resourceType: string, resourceId: string): Promise<Resource | null> {
    // In production: query from FHIR server or backend database
    // For testing: return mock data
    
    if (resourceType === 'Patient') {
      const mockQnxtPatient: QnxtPatient = {
        memberId: resourceId,
        firstName: 'John',
        lastName: 'Doe',
        dob: '1980-01-01',
        gender: 'M'
      };
      return this.mapQnxtPatientToFhir(mockQnxtPatient);
    }

    return null;
  }

  private async searchPatients(params: SearchParameters): Promise<Patient[]> {
    // In production: query QNXT or FHIR server
    // Mock implementation
    if (!params.patient) {
      return [];
    }

    const mockQnxtPatient: QnxtPatient = {
      memberId: params.patient,
      firstName: 'John',
      lastName: 'Doe',
      dob: '1980-01-01',
      gender: 'M',
      address: {
        street1: '123 Main St',
        city: 'Boston',
        state: 'MA',
        zip: '02101'
      },
      phone: '555-1234',
      email: 'john.doe@example.com'
    };

    return [this.mapQnxtPatientToFhir(mockQnxtPatient)];
  }

  private async searchClaims(params: SearchParameters): Promise<Claim[]> {
    // Mock implementation
    if (!params.patient) {
      return [];
    }

    const mockQnxtClaim: QnxtClaim = {
      claimId: 'CLM001',
      memberId: params.patient,
      providerId: 'NPI123',
      claimType: 'Professional',
      serviceDate: '2024-01-15',
      diagnosisCodes: ['E11.9', 'I10'],
      procedureCodes: ['99213', '80053'],
      totalCharged: 250.00,
      totalPaid: 200.00,
      status: 'active'
    };

    return [this.mapQnxtClaimToFhir(mockQnxtClaim)];
  }

  private async searchEncounters(params: SearchParameters): Promise<Encounter[]> {
    // Mock implementation
    if (!params.patient) {
      return [];
    }

    const mockQnxtEncounter: QnxtEncounter = {
      encounterId: 'ENC001',
      memberId: params.patient,
      providerId: 'NPI123',
      encounterType: 'AMB',
      encounterDate: '2024-01-15T10:00:00Z',
      diagnosisCodes: ['E11.9'],
      status: 'finished'
    };

    return [this.mapQnxtEncounterToFhir(mockQnxtEncounter)];
  }

  private async searchExplanationOfBenefits(params: SearchParameters): Promise<ExplanationOfBenefit[]> {
    // Mock implementation for EOB
    if (!params.patient) {
      return [];
    }

    const eob: ExplanationOfBenefit = {
      resourceType: 'ExplanationOfBenefit',
      id: 'EOB001',
      status: 'active',
      type: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/claim-type',
          code: 'professional'
        }]
      },
      use: 'claim',
      patient: {
        reference: `Patient/${params.patient}`
      },
      created: '2024-01-20',
      insurer: {
        display: 'Example Health Plan'
      },
      provider: {
        reference: 'Practitioner/NPI123'
      },
      outcome: 'complete',
      insurance: [{
        focal: true,
        coverage: {
          reference: `Coverage/${params.patient}`
        }
      }]
    };

    return [eob];
  }

  private async searchConditions(params: SearchParameters): Promise<Condition[]> {
    // Mock implementation for Condition (clinical USCDI data)
    if (!params.patient) {
      return [];
    }

    const condition: Condition = {
      resourceType: 'Condition',
      id: 'COND001',
      clinicalStatus: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/condition-clinical',
          code: 'active'
        }]
      },
      verificationStatus: {
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/condition-ver-status',
          code: 'confirmed'
        }]
      },
      category: [{
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/condition-category',
          code: 'encounter-diagnosis'
        }]
      }],
      code: {
        coding: [{
          system: 'http://snomed.info/sct',
          code: '44054006',
          display: 'Type 2 diabetes mellitus'
        }]
      },
      subject: {
        reference: `Patient/${params.patient}`
      },
      onsetDateTime: '2020-06-15'
    };

    return [condition];
  }

  private async searchObservations(params: SearchParameters): Promise<Observation[]> {
    // Mock implementation for Observation (clinical USCDI data)
    if (!params.patient) {
      return [];
    }

    const observation: Observation = {
      resourceType: 'Observation',
      id: 'OBS001',
      status: 'final',
      category: [{
        coding: [{
          system: 'http://terminology.hl7.org/CodeSystem/observation-category',
          code: 'laboratory'
        }]
      }],
      code: {
        coding: [{
          system: 'http://loinc.org',
          code: '2339-0',
          display: 'Glucose [Mass/volume] in Blood'
        }]
      },
      subject: {
        reference: `Patient/${params.patient}`
      },
      effectiveDateTime: '2024-01-15T10:00:00Z',
      valueQuantity: {
        value: 95,
        unit: 'mg/dL',
        system: 'http://unitsofmeasure.org',
        code: 'mg/dL'
      }
    };

    return [observation];
  }

  private mapGenderToFhir(gender: string): 'male' | 'female' | 'other' | 'unknown' {
    const normalized = gender.toUpperCase();
    switch (normalized) {
      case 'M':
      case 'MALE':
        return 'male';
      case 'F':
      case 'FEMALE':
        return 'female';
      case 'U':
      case 'UNKNOWN':
        return 'unknown';
      default:
        return 'other';
    }
  }

  private normalizeDateFormat(date: string): string {
    // Handle CCYYMMDD format
    if (date.length === 8 && /^\d+$/.test(date)) {
      return `${date.slice(0, 4)}-${date.slice(4, 6)}-${date.slice(6, 8)}`;
    }
    return date;
  }

  private mapClaimStatus(status: string): 'active' | 'cancelled' | 'draft' | 'entered-in-error' {
    const normalized = status.toLowerCase();
    switch (normalized) {
      case 'active':
      case 'pending':
      case 'submitted':
        return 'active';
      case 'cancelled':
      case 'denied':
        return 'cancelled';
      case 'draft':
        return 'draft';
      default:
        return 'active';
    }
  }

  private mapEncounterStatus(status: string): 'planned' | 'arrived' | 'triaged' | 'in-progress' | 'onleave' | 'finished' | 'cancelled' | 'entered-in-error' | 'unknown' {
    const normalized = status.toLowerCase();
    switch (normalized) {
      case 'finished':
      case 'completed':
        return 'finished';
      case 'in-progress':
      case 'active':
        return 'in-progress';
      case 'planned':
        return 'planned';
      case 'cancelled':
        return 'cancelled';
      default:
        return 'unknown';
    }
  }

  private mapEncounterClass(encounterType: string): string {
    // Map QNXT encounter types to valid v3-ActCode values
    const normalized = encounterType.toUpperCase();
    switch (normalized) {
      case 'AMB':
      case 'AMBULATORY':
      case 'OUTPATIENT':
        return 'AMB'; // ambulatory
      case 'EMER':
      case 'EMERGENCY':
        return 'EMER'; // emergency
      case 'FLD':
      case 'FIELD':
        return 'FLD'; // field
      case 'HH':
      case 'HOME':
      case 'HOME HEALTH':
        return 'HH'; // home health
      case 'IMP':
      case 'INPATIENT':
        return 'IMP'; // inpatient encounter
      case 'ACUTE':
        return 'ACUTE'; // inpatient acute
      case 'NONAC':
        return 'NONAC'; // inpatient non-acute
      case 'OBSENC':
      case 'OBSERVATION':
        return 'OBSENC'; // observation encounter
      case 'PRENC':
      case 'PREENROLLMENT':
        return 'PRENC'; // pre-admission
      case 'SS':
      case 'SHORT STAY':
        return 'SS'; // short stay
      case 'VR':
      case 'VIRTUAL':
        return 'VR'; // virtual
      default:
        // If not recognized, default to ambulatory (most common)
        return 'AMB';
    }
  }
}

/**
 * Factory function to create Provider Access API instance
 */
export function createProviderAccessApi(encryptionKey?: string): ProviderAccessApi {
  return new ProviderAccessApi(encryptionKey);
}
