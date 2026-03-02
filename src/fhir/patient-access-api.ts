/**
 * Patient Access API - CMS-0057-F Final Rule Implementation
 * 
 * Implements Patient Access API aligning with CMS-0057-F requirements (effective Jan 1, 2026).
 * Provides FHIR R4 RESTful endpoints for patients to access their health data with proper
 * OAuth 2.0 patient consent via Azure AD.
 * 
 * References:
 * - CMS-0057-F Final Rule (Interoperability and Prior Authorization)
 * - CMS-9115-F Patient Access Final Rule
 * - HL7 Da Vinci Payer Data Exchange (PDex) Implementation Guide
 * - US Core Implementation Guide v3.1.1+
 * - FHIR R4.0.1 Specification
 * - OAuth 2.0 Authorization Framework
 * 
 * Key Features:
 * - FHIR R4 Patient Access endpoints (Patient, Claim, Encounter, EOB, CoverageEligibilityResponse)
 * - OAuth 2.0 patient authentication via Azure AD
 * - X12 837/835 to FHIR mapping (Claim, ExplanationOfBenefit)
 * - PHI redaction for sensitive data
 * - Da Vinci PDex compliance checks
 * - HIPAA audit logging
 */

import {
  Patient,
  Claim,
  Encounter,
  ExplanationOfBenefit,
  CoverageEligibilityResponse,
  Bundle,
  BundleEntry,
  OperationOutcome,
  Resource
} from 'fhir/r4';
import * as crypto from 'crypto';
import { redactPHI } from '../security/hipaaLogger';
import { 
  mapX12837ToFhirClaim, 
  mapX12835ToFhirEob 
} from './mapping/x12-to-fhir';
import { X12_837_Claim, X12_835_Remittance } from './x12ClaimTypes';

// ============================================================================
// Type Definitions
// ============================================================================

/**
 * OAuth 2.0 Token for Patient Authentication
 */
export interface OAuth2Token {
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
  /** Patient context (patient ID) */
  patient?: string;
  /** ID token (OpenID Connect) */
  id_token?: string;
}

/**
 * Patient consent record for data access
 */
export interface PatientConsent {
  /** Patient identifier */
  patientId: string;
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
  /** Page number */
  _page?: number;
  /** Results per page */
  _count?: number;
}

/**
 * Backend patient data structure
 */
export interface BackendPatient {
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

/**
 * Backend encounter data structure
 */
export interface BackendEncounter {
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
  /** User/Patient identifier */
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
  constructor(message: string, public patientId?: string) {
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
// Patient Access API Class
// ============================================================================

export class PatientAccessApi {
  private encryptionKey: string;
  private auditLogs: AuditLogEntry[] = [];

  /**
   * Initialize Patient Access API
   * @param encryptionKey - Key for PHI encryption (from Azure Key Vault in production)
   */
  constructor(encryptionKey?: string) {
    this.encryptionKey = encryptionKey || this.generateEncryptionKey();
  }

  // ==========================================================================
  // Authentication & Authorization (OAuth 2.0 via Azure AD)
  // ==========================================================================

  /**
   * Validate OAuth 2.0 token from Azure AD
   * In production, this would validate against Azure AD/OIDC provider
   */
  async validateOAuth2Token(token: string): Promise<OAuth2Token> {
    // In production: validate JWT signature, expiration, issuer, audience
    if (!token || token.length < 10) {
      this.logAudit({
        timestamp: new Date().toISOString(),
        eventType: 'auth_failure',
        userId: 'unknown',
        result: 'failure',
        details: 'Invalid or missing token'
      });
      throw new AuthenticationError('Invalid or missing OAuth 2.0 token');
    }

    // Mock token parsing - in production, decode and validate JWT
    const parsedToken: OAuth2Token = {
      access_token: token,
      token_type: 'Bearer',
      expires_in: 3600,
      scope: 'patient/*.read',
      patient: this.extractPatientFromToken(token)
    };

    return parsedToken;
  }

  /**
   * Check patient consent for data access
   */
  async checkConsent(patientId: string): Promise<boolean> {
    // In production: query consent database or FHIR Consent resources
    const consent = await this.getPatientConsent(patientId);
    
    if (!consent) {
      this.logAudit({
        timestamp: new Date().toISOString(),
        eventType: 'consent_denial',
        userId: patientId,
        patientId,
        result: 'failure',
        details: 'No active consent found'
      });
      throw new ConsentDeniedError('Patient consent not found or inactive', patientId);
    }
    
    if (consent.status !== 'active') {
      this.logAudit({
        timestamp: new Date().toISOString(),
        eventType: 'consent_denial',
        userId: patientId,
        patientId,
        result: 'failure',
        details: `Consent status: ${consent.status}`
      });
      throw new ConsentDeniedError(`Patient consent is ${consent.status}`, patientId);
    }
    
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'consent_check',
      userId: patientId,
      patientId,
      result: 'success',
      details: 'Consent validated'
    });
    
    return true;
  }

  /**
   * Get patient consent record
   * In production: query Azure Cosmos DB or FHIR Consent resources
   */
  private async getPatientConsent(patientId: string): Promise<PatientConsent | null> {
    // Mock implementation - in production, query database
    return {
      patientId,
      status: 'active',
      effectiveDate: '2024-01-01',
      scope: ['patient/*.read'],
      purpose: 'Patient Access API'
    };
  }

  // ==========================================================================
  // FHIR R4 Endpoints - Patient Access
  // ==========================================================================

  /**
   * Patient Resource Endpoint - GET /Patient/{id}
   * US Core Patient Profile v3.1.1
   */
  async getPatient(patientId: string, token: string): Promise<Patient> {
    const authToken = await this.validateOAuth2Token(token);
    await this.checkConsent(patientId);
    
    // Verify patient from token matches requested patient
    if (authToken.patient && authToken.patient !== patientId) {
      throw new AuthenticationError('Token patient does not match requested patient');
    }
    
    const backendPatient = await this.fetchBackendPatient(patientId);
    const fhirPatient = this.mapBackendPatientToFhir(backendPatient);
    
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'read',
      userId: patientId,
      patientId,
      resourceType: 'Patient',
      resourceId: patientId,
      result: 'success',
      details: 'Patient resource accessed'
    });
    
    return this.redactPhi(fhirPatient) as Patient;
  }

  /**
   * Claim Resource Endpoint - GET /Claim?patient={id}
   * Maps X12 837 to FHIR Claim
   */
  async searchClaims(params: SearchParameters, token: string): Promise<Bundle> {
    const authToken = await this.validateOAuth2Token(token);
    const patientId = params.patient || authToken.patient;
    
    if (!patientId) {
      throw new Error('Patient parameter is required');
    }
    
    await this.checkConsent(patientId);
    
    const x12Claims = await this.fetchBackendClaims(patientId, params);
    const fhirClaims = x12Claims.map(x12 => mapX12837ToFhirClaim(x12));
    
    const bundle = this.createBundle(
      fhirClaims.map(claim => ({
        fullUrl: `Claim/${claim.id}`,
        resource: this.redactPhi(claim) as Claim
      })),
      'Claim',
      params
    );
    
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'search',
      userId: patientId,
      patientId,
      resourceType: 'Claim',
      result: 'success',
      details: `Retrieved ${fhirClaims.length} claims`
    });
    
    return bundle;
  }

  /**
   * Encounter Resource Endpoint - GET /Encounter?patient={id}
   */
  async searchEncounters(params: SearchParameters, token: string): Promise<Bundle> {
    const authToken = await this.validateOAuth2Token(token);
    const patientId = params.patient || authToken.patient;
    
    if (!patientId) {
      throw new Error('Patient parameter is required');
    }
    
    await this.checkConsent(patientId);
    
    const backendEncounters = await this.fetchBackendEncounters(patientId, params);
    const fhirEncounters = backendEncounters.map(enc => this.mapBackendEncounterToFhir(enc));
    
    const bundle = this.createBundle(
      fhirEncounters.map(encounter => ({
        fullUrl: `Encounter/${encounter.id}`,
        resource: this.redactPhi(encounter) as Encounter
      })),
      'Encounter',
      params
    );
    
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'search',
      userId: patientId,
      patientId,
      resourceType: 'Encounter',
      result: 'success',
      details: `Retrieved ${fhirEncounters.length} encounters`
    });
    
    return bundle;
  }

  /**
   * ExplanationOfBenefit Resource Endpoint - GET /ExplanationOfBenefit?patient={id}
   * Maps X12 835 to FHIR EOB
   */
  async searchExplanationOfBenefit(params: SearchParameters, token: string): Promise<Bundle> {
    const authToken = await this.validateOAuth2Token(token);
    const patientId = params.patient || authToken.patient;
    
    if (!patientId) {
      throw new Error('Patient parameter is required');
    }
    
    await this.checkConsent(patientId);
    
    const x12Remittances = await this.fetchBackendRemittances(patientId, params);
    const fhirEobs = x12Remittances.map(x12 => mapX12835ToFhirEob(x12));
    
    const bundle = this.createBundle(
      fhirEobs.map(eob => ({
        fullUrl: `ExplanationOfBenefit/${eob.id}`,
        resource: this.redactPhi(eob) as ExplanationOfBenefit
      })),
      'ExplanationOfBenefit',
      params
    );
    
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'search',
      userId: patientId,
      patientId,
      resourceType: 'ExplanationOfBenefit',
      result: 'success',
      details: `Retrieved ${fhirEobs.length} EOBs`
    });
    
    return bundle;
  }

  /**
   * CoverageEligibilityResponse Endpoint - GET /CoverageEligibilityResponse?patient={id}
   */
  async searchCoverageEligibilityResponse(params: SearchParameters, token: string): Promise<Bundle> {
    const authToken = await this.validateOAuth2Token(token);
    const patientId = params.patient || authToken.patient;
    
    if (!patientId) {
      throw new Error('Patient parameter is required');
    }
    
    await this.checkConsent(patientId);
    
    const backendResponses = await this.fetchBackendEligibilityResponses(patientId, params);
    const fhirResponses = backendResponses.map(resp => this.mapBackendEligibilityToFhir(resp));
    
    const bundle = this.createBundle(
      fhirResponses.map(response => ({
        fullUrl: `CoverageEligibilityResponse/${response.id}`,
        resource: this.redactPhi(response) as CoverageEligibilityResponse
      })),
      'CoverageEligibilityResponse',
      params
    );
    
    this.logAudit({
      timestamp: new Date().toISOString(),
      eventType: 'search',
      userId: patientId,
      patientId,
      resourceType: 'CoverageEligibilityResponse',
      result: 'success',
      details: `Retrieved ${fhirResponses.length} eligibility responses`
    });
    
    return bundle;
  }

  // ==========================================================================
  // Data Mapping - Backend to FHIR
  // ==========================================================================

  /**
   * Map backend patient data to FHIR Patient resource
   * US Core Patient Profile v3.1.1
   */
  mapBackendPatientToFhir(patient: BackendPatient): Patient {
    const fhirPatient: Patient = {
      resourceType: 'Patient',
      id: patient.memberId,
      meta: {
        profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
      },
      identifier: [
        {
          system: 'http://example.org/fhir/member-id',
          value: patient.memberId
        }
      ],
      name: [
        {
          use: 'official',
          family: patient.lastName,
          given: patient.middleName ? [patient.firstName, patient.middleName] : [patient.firstName]
        }
      ],
      gender: patient.gender === 'male' ? 'male' : patient.gender === 'female' ? 'female' : 'other',
      birthDate: patient.dob,
      telecom: []
    };

    if (patient.phone) {
      fhirPatient.telecom!.push({
        system: 'phone',
        value: patient.phone,
        use: 'home'
      });
    }

    if (patient.email) {
      fhirPatient.telecom!.push({
        system: 'email',
        value: patient.email
      });
    }

    if (patient.address) {
      fhirPatient.address = [
        {
          use: 'home',
          line: patient.address.street2 
            ? [patient.address.street1 || '', patient.address.street2] 
            : [patient.address.street1 || ''],
          city: patient.address.city,
          state: patient.address.state,
          postalCode: patient.address.zip
        }
      ];
    }

    return fhirPatient;
  }

  /**
   * Map backend encounter to FHIR Encounter resource
   */
  mapBackendEncounterToFhir(encounter: BackendEncounter): Encounter {
    const fhirEncounter: Encounter = {
      resourceType: 'Encounter',
      id: encounter.encounterId,
      status: encounter.status === 'finished' ? 'finished' : 'in-progress',
      class: {
        system: 'http://terminology.hl7.org/CodeSystem/v3-ActCode',
        code: encounter.encounterType || 'AMB',
        display: 'ambulatory'
      },
      subject: {
        reference: `Patient/${encounter.memberId}`
      },
      period: {
        start: encounter.encounterDate
      }
    };

    if (encounter.diagnosisCodes && encounter.diagnosisCodes.length > 0) {
      fhirEncounter.diagnosis = encounter.diagnosisCodes.map((code, index) => ({
        condition: {
          reference: `Condition/${code}`
        },
        rank: index + 1
      }));
    }

    return fhirEncounter;
  }

  /**
   * Map backend eligibility response to FHIR CoverageEligibilityResponse
   */
  mapBackendEligibilityToFhir(response: { responseId: string; memberId: string; responseDate?: string; requestId?: string }): CoverageEligibilityResponse {
    return {
      resourceType: 'CoverageEligibilityResponse',
      id: response.responseId,
      status: 'active',
      purpose: ['benefits'],
      patient: {
        reference: `Patient/${response.memberId}`
      },
      created: response.responseDate || new Date().toISOString(),
      request: {
        reference: `CoverageEligibilityRequest/${response.requestId || 'unknown'}`
      },
      insurer: {
        display: 'Cloud Health Office Plan'
      },
      outcome: 'complete'
    };
  }

  // ==========================================================================
  // Backend Data Fetching (Mock - Replace with actual backend calls)
  // ==========================================================================

  private async fetchBackendPatient(patientId: string): Promise<BackendPatient> {
    // In production: call backend API or database
    return {
      memberId: patientId,
      firstName: 'John',
      lastName: 'Doe',
      dob: '1980-01-01',
      gender: 'male',
      address: {
        street1: '123 Main St',
        city: 'Boston',
        state: 'MA',
        zip: '02101'
      },
      phone: '555-1234',
      email: 'john.doe@example.com'
    };
  }

  private async fetchBackendClaims(_patientId: string, _params: SearchParameters): Promise<X12_837_Claim[]> {
    // In production: query backend claims database
    return [];
  }

  private async fetchBackendEncounters(_patientId: string, _params: SearchParameters): Promise<BackendEncounter[]> {
    // In production: query backend encounters database
    return [];
  }

  private async fetchBackendRemittances(_patientId: string, _params: SearchParameters): Promise<X12_835_Remittance[]> {
    // In production: query backend remittance database
    return [];
  }

  private async fetchBackendEligibilityResponses(_patientId: string, _params: SearchParameters): Promise<Array<{ responseId: string; memberId: string; responseDate?: string; requestId?: string }>> {
    // In production: query backend eligibility database
    return [];
  }

  // ==========================================================================
  // Helper Methods
  // ==========================================================================

  /**
   * Create FHIR Bundle from entries
   */
  private createBundle(entries: BundleEntry[], resourceType: string, params: SearchParameters): Bundle {
    return {
      resourceType: 'Bundle',
      type: 'searchset',
      total: entries.length,
      link: [
        {
          relation: 'self',
          url: `/${resourceType}?patient=${params.patient}`
        }
      ],
      entry: entries
    };
  }

  /**
   * Redact PHI from FHIR resource
   */
  redactPhi(resource: Resource): Resource {
    return redactPHI(resource);
  }

  /**
   * Extract patient ID from OAuth 2.0 token
   */
  private extractPatientFromToken(_token: string): string | undefined {
    // In production: decode JWT and extract patient claim
    return 'PAT001';
  }

  /**
   * Generate encryption key for PHI protection
   */
  private generateEncryptionKey(): string {
    return crypto.randomBytes(32).toString('hex');
  }

  /**
   * Log audit event for HIPAA compliance
   */
  logAudit(entry: AuditLogEntry): void {
    this.auditLogs.push(entry);
    // Sanitize user-controlled values to prevent log injection
    const safeUserId = String(entry.userId || '').replace(/[\r\n]/g, '');
    const safeDetails = String(entry.details || '').replace(/[\r\n]/g, '');
    console.log(`[AUDIT] ${entry.timestamp} - ${entry.eventType} - ${entry.result} - User: ${safeUserId} - ${safeDetails}`);
  }

  /**
   * Get audit logs (for monitoring and compliance)
   */
  getAuditLogs(): AuditLogEntry[] {
    return this.auditLogs;
  }

  /**
   * Create FHIR OperationOutcome for errors
   */
  createOperationOutcome(
    severity: 'fatal' | 'error' | 'warning' | 'information',
    code: string,
    details: string
  ): OperationOutcome {
    return {
      resourceType: 'OperationOutcome',
      issue: [
        {
          severity,
          code: code as OperationOutcome['issue'][0]['code'],
          diagnostics: details
        }
      ]
    };
  }

  /**
   * Validate Da Vinci PDex compliance
   */
  async validatePDexCompliance(_resource: Resource): Promise<boolean> {
    // In production: validate against Da Vinci PDex profiles
    // Check required elements, cardinality, terminology bindings
    return true;
  }
}
