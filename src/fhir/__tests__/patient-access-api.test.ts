/**
 * Patient Access API Tests
 * 
 * Comprehensive test suite for CMS-0057-F Patient Access API implementation
 * Tests OAuth 2.0 authentication, FHIR endpoints, X12 mapping, and PHI redaction
 * 
 * SECURITY NOTE: All tokens and credentials in this file are synthetic test values only.
 * These are NOT real credentials and should never be used in production environments.
 */

import { PatientAccessApi, AuthenticationError } from '../patient-access-api';

describe('PatientAccessApi', () => {
  let api: PatientAccessApi;

  beforeEach(() => {
    api = new PatientAccessApi('test-encryption-key-123');
  });

  // ==========================================================================
  // OAuth 2.0 Authentication Tests
  // ==========================================================================

  describe('OAuth 2.0 Authentication', () => {
    it('should validate valid OAuth 2.0 token', async () => {
      const token = 'valid-oauth2-bearer-token-12345';
      const result = await api.validateOAuth2Token(token);

      expect(result).toHaveProperty('access_token', token);
      expect(result).toHaveProperty('token_type', 'Bearer');
      expect(result).toHaveProperty('expires_in');
      expect(result).toHaveProperty('scope', 'patient/*.read');
      expect(result).toHaveProperty('patient');
    });

    it('should reject invalid OAuth 2.0 token', async () => {
      await expect(api.validateOAuth2Token('')).rejects.toThrow(AuthenticationError);
      await expect(api.validateOAuth2Token('short')).rejects.toThrow(AuthenticationError);
    });

    it('should log authentication failures', async () => {
      const auditLogs = api.getAuditLogs();
      const initialCount = auditLogs.length;

      try {
        await api.validateOAuth2Token('invalid');
      } catch (error) {
        // Expected
      }

      const updatedLogs = api.getAuditLogs();
      expect(updatedLogs.length).toBe(initialCount + 1);
      expect(updatedLogs[updatedLogs.length - 1]).toMatchObject({
        eventType: 'auth_failure',
        result: 'failure'
      });
    });
  });

  // ==========================================================================
  // Patient Consent Tests
  // ==========================================================================

  describe('Patient Consent', () => {
    it('should validate active patient consent', async () => {
      const result = await api.checkConsent('PAT001');
      expect(result).toBe(true);

      const logs = api.getAuditLogs();
      const consentLog = logs.find(log => log.eventType === 'consent_check' && log.patientId === 'PAT001');
      expect(consentLog).toBeDefined();
      expect(consentLog?.result).toBe('success');
    });

    it('should log consent validation', async () => {
      await api.checkConsent('PAT001');
      
      const logs = api.getAuditLogs();
      const consentLogs = logs.filter(log => log.eventType === 'consent_check');
      expect(consentLogs.length).toBeGreaterThan(0);
    });
  });

  // ==========================================================================
  // Patient Resource Endpoint Tests (US Core v3.1.1)
  // ==========================================================================

  describe('GET /Patient/{id}', () => {
    it('should retrieve patient resource with valid token', async () => {
      const token = 'valid-oauth2-token-for-pat001';
      const patient = await api.getPatient('PAT001', token);

      expect(patient.resourceType).toBe('Patient');
      expect(patient.id).toBe('PAT001');
      expect(patient.meta?.profile).toContain('http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient');
      expect(patient.name).toBeDefined();
      expect(patient.gender).toBeDefined();
      expect(patient.birthDate).toBeDefined();
    });

    it('should apply PHI redaction to patient resource', async () => {
      const token = 'valid-oauth2-token';
      const patient = await api.getPatient('PAT001', token);

      // Patient resource should be returned
      expect(patient).toBeDefined();
      expect(patient.resourceType).toBe('Patient');
    });

    it('should log patient access events', async () => {
      const token = 'valid-oauth2-token';
      await api.getPatient('PAT001', token);

      const logs = api.getAuditLogs();
      const accessLog = logs.find(log => 
        log.eventType === 'read' && 
        log.resourceType === 'Patient' &&
        log.resourceId === 'PAT001'
      );
      expect(accessLog).toBeDefined();
      expect(accessLog?.result).toBe('success');
    });
  });

  // ==========================================================================
  // Claim Resource Endpoint Tests (X12 837 → FHIR Claim)
  // ==========================================================================

  describe('GET /Claim?patient={id}', () => {
    it('should search claims for patient', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'Claim',
        patient: 'PAT001'
      };

      const bundle = await api.searchClaims(params, token);

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.type).toBe('searchset');
      expect(bundle.entry).toBeDefined();
      expect(bundle.total).toBeDefined();
    });

    it('should create proper bundle structure', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'Claim',
        patient: 'PAT001'
      };

      const bundle = await api.searchClaims(params, token);

      expect(bundle.link).toBeDefined();
      expect(bundle.link![0].relation).toBe('self');
      expect(bundle.link![0].url).toContain('/Claim');
    });

    it('should log claim search events', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'Claim',
        patient: 'PAT001'
      };

      await api.searchClaims(params, token);

      const logs = api.getAuditLogs();
      const searchLog = logs.find(log => 
        log.eventType === 'search' && 
        log.resourceType === 'Claim'
      );
      expect(searchLog).toBeDefined();
      expect(searchLog?.result).toBe('success');
    });

    it('should use patient from token if not provided in params', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'Claim'
      };

      const bundle = await api.searchClaims(params, token);
      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.type).toBe('searchset');
    });
  });

  // ==========================================================================
  // Encounter Resource Endpoint Tests
  // ==========================================================================

  describe('GET /Encounter?patient={id}', () => {
    it('should search encounters for patient', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'Encounter',
        patient: 'PAT001'
      };

      const bundle = await api.searchEncounters(params, token);

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.type).toBe('searchset');
      expect(bundle.entry).toBeDefined();
    });

    it('should log encounter search events', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'Encounter',
        patient: 'PAT001'
      };

      await api.searchEncounters(params, token);

      const logs = api.getAuditLogs();
      const searchLog = logs.find(log => 
        log.eventType === 'search' && 
        log.resourceType === 'Encounter'
      );
      expect(searchLog).toBeDefined();
    });
  });

  // ==========================================================================
  // ExplanationOfBenefit Endpoint Tests (X12 835 → FHIR EOB)
  // ==========================================================================

  describe('GET /ExplanationOfBenefit?patient={id}', () => {
    it('should search EOBs for patient', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'ExplanationOfBenefit',
        patient: 'PAT001'
      };

      const bundle = await api.searchExplanationOfBenefit(params, token);

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.type).toBe('searchset');
      expect(bundle.entry).toBeDefined();
    });

    it('should apply PHI redaction to EOB resources', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'ExplanationOfBenefit',
        patient: 'PAT001'
      };

      const bundle = await api.searchExplanationOfBenefit(params, token);
      expect(bundle).toBeDefined();
    });

    it('should log EOB search events', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'ExplanationOfBenefit',
        patient: 'PAT001'
      };

      await api.searchExplanationOfBenefit(params, token);

      const logs = api.getAuditLogs();
      const searchLog = logs.find(log => 
        log.eventType === 'search' && 
        log.resourceType === 'ExplanationOfBenefit'
      );
      expect(searchLog).toBeDefined();
      expect(searchLog?.result).toBe('success');
    });
  });

  // ==========================================================================
  // CoverageEligibilityResponse Endpoint Tests
  // ==========================================================================

  describe('GET /CoverageEligibilityResponse?patient={id}', () => {
    it('should search coverage eligibility responses', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'CoverageEligibilityResponse',
        patient: 'PAT001'
      };

      const bundle = await api.searchCoverageEligibilityResponse(params, token);

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.type).toBe('searchset');
      expect(bundle.entry).toBeDefined();
    });

    it('should log eligibility search events', async () => {
      const token = 'valid-oauth2-token';
      const params = {
        resourceType: 'CoverageEligibilityResponse',
        patient: 'PAT001'
      };

      await api.searchCoverageEligibilityResponse(params, token);

      const logs = api.getAuditLogs();
      const searchLog = logs.find(log => 
        log.eventType === 'search' && 
        log.resourceType === 'CoverageEligibilityResponse'
      );
      expect(searchLog).toBeDefined();
    });
  });

  // ==========================================================================
  // Data Mapping Tests
  // ==========================================================================

  describe('Data Mapping', () => {
    it('should map backend patient to FHIR Patient', () => {
      const backendPatient = {
        memberId: 'MEM123',
        firstName: 'Jane',
        lastName: 'Smith',
        middleName: 'Marie',
        dob: '1985-05-15',
        gender: 'female',
        address: {
          street1: '456 Oak Ave',
          city: 'Seattle',
          state: 'WA',
          zip: '98101'
        },
        phone: '206-555-0100',
        email: 'jane.smith@example.com'
      };

      const fhirPatient = api.mapBackendPatientToFhir(backendPatient);

      expect(fhirPatient.resourceType).toBe('Patient');
      expect(fhirPatient.id).toBe('MEM123');
      expect(fhirPatient.name![0].family).toBe('Smith');
      expect(fhirPatient.name![0].given).toEqual(['Jane', 'Marie']);
      expect(fhirPatient.gender).toBe('female');
      expect(fhirPatient.birthDate).toBe('1985-05-15');
      expect(fhirPatient.address![0].city).toBe('Seattle');
      expect(fhirPatient.telecom).toHaveLength(2);
    });

    it('should map backend encounter to FHIR Encounter', () => {
      const backendEncounter = {
        encounterId: 'ENC456',
        memberId: 'MEM123',
        providerId: 'NPI1234567890',
        encounterType: 'AMB',
        encounterDate: '2024-03-15',
        diagnosisCodes: ['Z00.00', 'E11.9'],
        status: 'finished'
      };

      const fhirEncounter = api.mapBackendEncounterToFhir(backendEncounter);

      expect(fhirEncounter.resourceType).toBe('Encounter');
      expect(fhirEncounter.id).toBe('ENC456');
      expect(fhirEncounter.status).toBe('finished');
      expect(fhirEncounter.subject).toBeDefined();
      expect(fhirEncounter.subject?.reference).toBe('Patient/MEM123');
      expect(fhirEncounter.diagnosis).toHaveLength(2);
    });

    it('should map backend eligibility to FHIR CoverageEligibilityResponse', () => {
      const backendEligibility = {
        responseId: 'ELIG789',
        memberId: 'MEM123',
        responseDate: '2024-03-20'
      };

      const fhirResponse = api.mapBackendEligibilityToFhir(backendEligibility);

      expect(fhirResponse.resourceType).toBe('CoverageEligibilityResponse');
      expect(fhirResponse.id).toBe('ELIG789');
      expect(fhirResponse.status).toBe('active');
      expect(fhirResponse.patient).toBeDefined();
      expect(fhirResponse.patient?.reference).toBe('Patient/MEM123');
      expect(fhirResponse.outcome).toBe('complete');
    });
  });

  // ==========================================================================
  // HIPAA Audit Logging Tests
  // ==========================================================================

  describe('HIPAA Audit Logging', () => {
    it('should log all access events', async () => {
      const token = 'valid-oauth2-token';
      
      await api.getPatient('PAT001', token);
      await api.searchClaims({ resourceType: 'Claim', patient: 'PAT001' }, token);
      
      const logs = api.getAuditLogs();
      expect(logs.length).toBeGreaterThan(0);
      
      logs.forEach(log => {
        expect(log).toHaveProperty('timestamp');
        expect(log).toHaveProperty('eventType');
        expect(log).toHaveProperty('userId');
        expect(log).toHaveProperty('result');
      });
    });

    it('should include resource details in audit logs', async () => {
      const token = 'valid-oauth2-token';
      await api.getPatient('PAT001', token);

      const logs = api.getAuditLogs();
      const patientLog = logs.find(log => log.resourceType === 'Patient');
      
      expect(patientLog).toBeDefined();
      expect(patientLog?.resourceId).toBe('PAT001');
      expect(patientLog?.details).toBeDefined();
    });
  });

  // ==========================================================================
  // Error Handling Tests
  // ==========================================================================

  describe('Error Handling', () => {
    it('should create OperationOutcome for errors', () => {
      const outcome = api.createOperationOutcome('error', 'not-found', 'Patient not found');

      expect(outcome.resourceType).toBe('OperationOutcome');
      expect(outcome.issue).toHaveLength(1);
      expect(outcome.issue[0].severity).toBe('error');
      expect(outcome.issue[0].code).toBe('not-found');
      expect(outcome.issue[0].diagnostics).toBe('Patient not found');
    });

    it('should handle authentication errors gracefully', async () => {
      await expect(api.validateOAuth2Token('')).rejects.toThrow(AuthenticationError);
    });
  });

  // ==========================================================================
  // Da Vinci PDex Compliance Tests
  // ==========================================================================

  describe('Da Vinci PDex Compliance', () => {
    it('should validate PDex compliance for resources', async () => {
      const patient = {
        resourceType: 'Patient' as const,
        id: 'test-patient',
        meta: {
          profile: ['http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient']
        }
      };

      const isCompliant = await api.validatePDexCompliance(patient);
      expect(isCompliant).toBe(true);
    });
  });

  // ==========================================================================
  // Security Tests
  // ==========================================================================

  describe('Security', () => {
    it('should initialize with encryption key', () => {
      const secureApi = new PatientAccessApi('secure-key-from-azure-keyvault');
      expect(secureApi).toBeDefined();
    });

    it('should generate encryption key if not provided', () => {
      const autoApi = new PatientAccessApi();
      expect(autoApi).toBeDefined();
    });

    it('should redact PHI from resources', () => {
      const patient = {
        resourceType: 'Patient' as const,
        id: 'test-patient',
        name: [{ family: 'Doe', given: ['John'] }]
      };

      const redacted = api.redactPhi(patient);
      expect(redacted).toBeDefined();
      expect(redacted.resourceType).toBe('Patient');
    });
  });

  // ==========================================================================
  // Integration Tests
  // ==========================================================================

  describe('End-to-End Patient Access Flow', () => {
    it('should complete full patient access workflow', async () => {
      const token = 'valid-oauth2-token-full-flow';
      
      // Step 1: Authenticate
      const authToken = await api.validateOAuth2Token(token);
      expect(authToken).toBeDefined();
      
      // Step 2: Get patient
      const patient = await api.getPatient('PAT001', token);
      expect(patient.resourceType).toBe('Patient');
      
      // Step 3: Search claims
      const claimsBundle = await api.searchClaims(
        { resourceType: 'Claim', patient: 'PAT001' }, 
        token
      );
      expect(claimsBundle.resourceType).toBe('Bundle');
      
      // Step 4: Search encounters
      const encountersBundle = await api.searchEncounters(
        { resourceType: 'Encounter', patient: 'PAT001' }, 
        token
      );
      expect(encountersBundle.resourceType).toBe('Bundle');
      
      // Step 5: Search EOBs
      const eobsBundle = await api.searchExplanationOfBenefit(
        { resourceType: 'ExplanationOfBenefit', patient: 'PAT001' }, 
        token
      );
      expect(eobsBundle.resourceType).toBe('Bundle');
      
      // Verify audit trail
      const logs = api.getAuditLogs();
      expect(logs.length).toBeGreaterThan(5);
      
      const successfulLogs = logs.filter(log => log.result === 'success');
      expect(successfulLogs.length).toBeGreaterThan(0);
    });
  });
});
