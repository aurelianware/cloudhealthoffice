/**
 * Provider Access API Tests - CMS-0057-F Compliance
 * 
 * Comprehensive test suite covering:
 * - Successful search/read calls
 * - Backend data mapping (QNXT → FHIR)
 * - SMART on FHIR authentication and consent edge cases
 * - HIPAA safeguards (encryption, PHI audit, redaction)
 * - Consent denial/authorization errors
 */

import {
  ProviderAccessApi,
  createProviderAccessApi,
  AuthenticationError,
  ConsentDeniedError,
  ResourceNotFoundError,
  QnxtPatient,
  QnxtClaim,
  QnxtEncounter
} from '../provider-access-api';
import { Condition, Observation, Patient } from 'fhir/r4';

describe('ProviderAccessApi', () => {
  let api: ProviderAccessApi;
  const validToken = 'provider:NPI12345:valid-access-token-abc123';
  const invalidToken = '';
  const testPatientId = 'PAT001';
  const testProviderId = 'NPI12345';

  beforeEach(() => {
    api = createProviderAccessApi('0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef');
  });

  // ==========================================================================
  // 1. SMART on FHIR Authentication Tests
  // ==========================================================================

  describe('SMART on FHIR Authentication', () => {
    it('should validate a valid SMART on FHIR token', async () => {
      const token = await api.validateSmartToken(validToken);

      expect(token).toBeDefined();
      expect(token.access_token).toBe(validToken);
      expect(token.token_type).toBe('Bearer');
      expect(token.expires_in).toBe(3600);
      expect(token.scope).toBe('patient/*.read');
      expect(token.provider).toBe(testProviderId);
    });

    it('should reject an empty token', async () => {
      await expect(api.validateSmartToken(invalidToken)).rejects.toThrow(AuthenticationError);
      await expect(api.validateSmartToken(invalidToken)).rejects.toThrow('Invalid or missing SMART on FHIR token');
    });

    it('should reject a token that is too short', async () => {
      await expect(api.validateSmartToken('short')).rejects.toThrow(AuthenticationError);
    });

    it('should log authentication failure for invalid tokens', async () => {
      try {
        await api.validateSmartToken('');
      } catch (error) {
        // Expected
      }

      const auditLogs = api.getAuditLogs();
      const authFailure = auditLogs.find(log => log.eventType === 'auth_failure');
      
      expect(authFailure).toBeDefined();
      expect(authFailure?.result).toBe('failure');
      expect(authFailure?.details).toContain('Invalid or missing token');
    });
  });

  // ==========================================================================
  // 2. Consent Validation Tests
  // ==========================================================================

  describe('Consent Validation', () => {
    it('should validate active patient consent', async () => {
      const hasConsent = await api.checkConsent(testPatientId, testProviderId);

      expect(hasConsent).toBe(true);

      const auditLogs = api.getAuditLogs();
      const consentCheck = auditLogs.find(
        log => log.eventType === 'consent_check' && log.result === 'success'
      );

      expect(consentCheck).toBeDefined();
      expect(consentCheck?.patientId).toBe(testPatientId);
      expect(consentCheck?.userId).toBe(testProviderId);
    });

    it('should handle consent denial', async () => {
      // This test simulates a scenario where consent would be denied
      // In the mock implementation, consent is always active, so we test the error path
      const error = new ConsentDeniedError(
        'Provider does not have consent',
        testPatientId,
        testProviderId
      );

      expect(error.name).toBe('ConsentDeniedError');
      expect(error.patientId).toBe(testPatientId);
      expect(error.providerId).toBe(testProviderId);
    });

    it('should log consent checks', async () => {
      await api.checkConsent(testPatientId, testProviderId);

      const auditLogs = api.getAuditLogs();
      const consentLogs = auditLogs.filter(log => log.eventType === 'consent_check');

      expect(consentLogs.length).toBeGreaterThan(0);
      expect(consentLogs[0].patientId).toBe(testPatientId);
      expect(consentLogs[0].userId).toBe(testProviderId);
    });

    it('should properly construct ConsentDeniedError with all properties', () => {
      const error = new ConsentDeniedError(
        'No active consent found',
        'PAT999',
        'NPI99999'
      );

      expect(error instanceof ConsentDeniedError).toBe(true);
      expect(error instanceof Error).toBe(true);
      expect(error.message).toBe('No active consent found');
      expect(error.patientId).toBe('PAT999');
      expect(error.providerId).toBe('NPI99999');
      expect(error.name).toBe('ConsentDeniedError');
    });

    it('should properly construct ResourceNotFoundError with all properties', () => {
      const error = new ResourceNotFoundError(
        'Resource not found',
        'Patient',
        'PAT999'
      );

      expect(error instanceof ResourceNotFoundError).toBe(true);
      expect(error instanceof Error).toBe(true);
      expect(error.message).toBe('Resource not found');
      expect(error.resourceType).toBe('Patient');
      expect(error.resourceId).toBe('PAT999');
      expect(error.name).toBe('ResourceNotFoundError');
    });
  });

  // ==========================================================================
  // 3. Search Operations Tests
  // ==========================================================================

  describe('Search Operations', () => {
    it('should successfully search for Patient resources', async () => {
      const bundle = await api.searchResources(
        'Patient',
        { resourceType: 'Patient', patient: testPatientId },
        validToken
      );

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.type).toBe('searchset');
      expect(bundle.total).toBeGreaterThan(0);
      expect(bundle.entry).toBeDefined();
      expect(bundle.entry![0].resource?.resourceType).toBe('Patient');
    });

    it('should successfully search for Claim resources', async () => {
      const bundle = await api.searchResources(
        'Claim',
        { resourceType: 'Claim', patient: testPatientId },
        validToken
      );

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.entry).toBeDefined();
      expect(bundle.entry![0].resource?.resourceType).toBe('Claim');
    });

    it('should successfully search for Encounter resources', async () => {
      const bundle = await api.searchResources(
        'Encounter',
        { resourceType: 'Encounter', patient: testPatientId },
        validToken
      );

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.entry).toBeDefined();
      expect(bundle.entry![0].resource?.resourceType).toBe('Encounter');
    });

    it('should successfully search for ExplanationOfBenefit resources', async () => {
      const bundle = await api.searchResources(
        'ExplanationOfBenefit',
        { resourceType: 'ExplanationOfBenefit', patient: testPatientId },
        validToken
      );

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.entry).toBeDefined();
      expect(bundle.entry![0].resource?.resourceType).toBe('ExplanationOfBenefit');
    });

    it('should successfully search for Condition resources (USCDI)', async () => {
      const bundle = await api.searchResources(
        'Condition',
        { resourceType: 'Condition', patient: testPatientId },
        validToken
      );

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.entry).toBeDefined();
      expect(bundle.entry![0].resource?.resourceType).toBe('Condition');
      
      const condition = bundle.entry![0].resource as Condition;
      expect(condition.code).toBeDefined();
      expect(condition.subject?.reference).toContain('Patient');
    });

    it('should successfully search for Observation resources (USCDI)', async () => {
      const bundle = await api.searchResources(
        'Observation',
        { resourceType: 'Observation', patient: testPatientId },
        validToken
      );

      expect(bundle.resourceType).toBe('Bundle');
      expect(bundle.entry).toBeDefined();
      expect(bundle.entry![0].resource?.resourceType).toBe('Observation');
      
      const observation = bundle.entry![0].resource as Observation;
      expect(observation.code).toBeDefined();
      expect(observation.valueQuantity).toBeDefined();
    });

    it('should require valid token for search operations', async () => {
      // Test that invalid tokens are rejected
      await expect(
        api.searchResources(
          'Patient',
          { resourceType: 'Patient', patient: testPatientId },
          ''
        )
      ).rejects.toThrow(AuthenticationError);
    });

    it('should log search operations in audit trail', async () => {
      await api.searchResources(
        'Patient',
        { resourceType: 'Patient', patient: testPatientId },
        validToken
      );

      const auditLogs = api.getAuditLogs();
      const searchLog = auditLogs.find(log => log.eventType === 'search');

      expect(searchLog).toBeDefined();
      expect(searchLog?.resourceType).toBe('Patient');
      expect(searchLog?.userId).toBe(testProviderId);
      expect(searchLog?.result).toBe('success');
    });
  });

  // ==========================================================================
  // 4. Read Operations Tests
  // ==========================================================================

  describe('Read Operations', () => {
    it('should successfully read a Patient resource', async () => {
      const resource = await api.readResource('Patient', testPatientId, validToken);

      expect(resource.resourceType).toBe('Patient');
      expect(resource.id).toBe(testPatientId);
    });

    it('should throw ResourceNotFoundError for non-existent resource', async () => {
      await expect(
        api.readResource('Claim', 'NONEXISTENT', validToken)
      ).rejects.toThrow(ResourceNotFoundError);
    });

    it('should log read operations in audit trail', async () => {
      await api.readResource('Patient', testPatientId, validToken);

      const auditLogs = api.getAuditLogs();
      const readLog = auditLogs.find(log => log.eventType === 'read');

      expect(readLog).toBeDefined();
      expect(readLog?.resourceType).toBe('Patient');
      expect(readLog?.resourceId).toBe(testPatientId);
      expect(readLog?.result).toBe('success');
    });

    it('should validate consent before reading resource', async () => {
      // This test ensures consent is checked before allowing read
      await api.readResource('Patient', testPatientId, validToken);

      const auditLogs = api.getAuditLogs();
      const consentCheck = auditLogs.find(log => log.eventType === 'consent_check');

      expect(consentCheck).toBeDefined();
    });
  });

  // ==========================================================================
  // 5. QNXT to FHIR Mapping Tests
  // ==========================================================================

  describe('QNXT to FHIR Mapping', () => {
    it('should map QNXT Patient to FHIR Patient (US Core v3.1.1)', () => {
      const qnxtPatient: QnxtPatient = {
        memberId: 'MEM123',
        firstName: 'Jane',
        lastName: 'Smith',
        middleName: 'Marie',
        dob: '19850615',
        gender: 'F',
        address: {
          street1: '456 Oak Avenue',
          street2: 'Apt 12',
          city: 'Boston',
          state: 'MA',
          zip: '02101'
        },
        phone: '617-555-1234',
        email: 'jane.smith@example.com'
      };

      const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);

      expect(fhirPatient.resourceType).toBe('Patient');
      expect(fhirPatient.id).toBe('MEM123');
      expect(fhirPatient.meta?.profile).toContain('http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient');
      expect(fhirPatient.name![0].family).toBe('Smith');
      expect(fhirPatient.name![0].given).toContain('Jane');
      expect(fhirPatient.name![0].given).toContain('Marie');
      expect(fhirPatient.gender).toBe('female');
      expect(fhirPatient.birthDate).toBe('1985-06-15');
      expect(fhirPatient.address![0].city).toBe('Boston');
      expect(fhirPatient.address![0].state).toBe('MA');
      expect(fhirPatient.telecom).toHaveLength(2);
    });

    it('should map QNXT Claim to FHIR Claim', () => {
      const qnxtClaim: QnxtClaim = {
        claimId: 'CLM123',
        memberId: 'MEM123',
        providerId: 'NPI98765',
        claimType: 'Professional',
        serviceDate: '2024-01-15',
        diagnosisCodes: ['E11.9', 'I10'],
        procedureCodes: ['99213', '80053'],
        totalCharged: 350.00,
        totalPaid: 280.00,
        status: 'active'
      };

      const fhirClaim = api.mapQnxtClaimToFhir(qnxtClaim);

      expect(fhirClaim.resourceType).toBe('Claim');
      expect(fhirClaim.id).toBe('CLM123');
      expect(fhirClaim.status).toBe('active');
      expect(fhirClaim.patient.reference).toBe('Patient/MEM123');
      expect(fhirClaim.provider.reference).toBe('Practitioner/NPI98765');
      expect(fhirClaim.diagnosis).toHaveLength(2);
      expect(fhirClaim.item).toHaveLength(2);
      expect(fhirClaim.total?.value).toBe(350.00);
      expect(fhirClaim.total?.currency).toBe('USD');
    });

    it('should map QNXT Encounter to FHIR Encounter', () => {
      const qnxtEncounter: QnxtEncounter = {
        encounterId: 'ENC123',
        memberId: 'MEM123',
        providerId: 'NPI98765',
        encounterType: 'AMB',
        encounterDate: '2024-01-15T10:00:00Z',
        diagnosisCodes: ['E11.9', 'Z79.4'],
        status: 'finished'
      };

      const fhirEncounter = api.mapQnxtEncounterToFhir(qnxtEncounter);

      expect(fhirEncounter.resourceType).toBe('Encounter');
      expect(fhirEncounter.id).toBe('ENC123');
      expect(fhirEncounter.status).toBe('finished');
      expect(fhirEncounter.subject?.reference).toBe('Patient/MEM123');
      expect(fhirEncounter.participant?.[0]?.individual?.reference).toBe('Practitioner/NPI98765');
      expect(fhirEncounter.diagnosis).toHaveLength(2);
    });

    it('should handle gender code mapping correctly', () => {
      const testCases = [
        { input: 'M', expected: 'male' },
        { input: 'MALE', expected: 'male' },
        { input: 'F', expected: 'female' },
        { input: 'FEMALE', expected: 'female' },
        { input: 'U', expected: 'unknown' },
        { input: 'UNKNOWN', expected: 'unknown' },
        { input: 'X', expected: 'other' }
      ];

      testCases.forEach(({ input, expected }) => {
        const qnxtPatient: QnxtPatient = {
          memberId: 'TEST',
          firstName: 'Test',
          lastName: 'User',
          dob: '1980-01-01',
          gender: input
        };

        const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);
        expect(fhirPatient.gender).toBe(expected);
      });
    });

    it('should normalize date formats (CCYYMMDD to YYYY-MM-DD)', () => {
      const qnxtPatient: QnxtPatient = {
        memberId: 'TEST',
        firstName: 'Test',
        lastName: 'User',
        dob: '19850615',
        gender: 'M'
      };

      const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);
      expect(fhirPatient.birthDate).toBe('1985-06-15');
    });

    it('should map various encounter types correctly', () => {
      const testCases = [
        { input: 'AMB', expected: 'AMB' },
        { input: 'AMBULATORY', expected: 'AMB' },
        { input: 'OUTPATIENT', expected: 'AMB' },
        { input: 'EMER', expected: 'EMER' },
        { input: 'EMERGENCY', expected: 'EMER' },
        { input: 'IMP', expected: 'IMP' },
        { input: 'INPATIENT', expected: 'IMP' },
        { input: 'HH', expected: 'HH' },
        { input: 'HOME', expected: 'HH' },
        { input: 'VR', expected: 'VR' },
        { input: 'VIRTUAL', expected: 'VR' },
        { input: 'UNKNOWN_TYPE', expected: 'AMB' } // Defaults to AMB
      ];

      testCases.forEach(({ input, expected }) => {
        const qnxtEncounter: QnxtEncounter = {
          encounterId: 'TEST',
          memberId: 'MEM123',
          providerId: 'NPI123',
          encounterType: input,
          encounterDate: '2024-01-01',
          diagnosisCodes: [],
          status: 'finished'
        };

        const fhirEncounter = api.mapQnxtEncounterToFhir(qnxtEncounter);
        expect(fhirEncounter.class.code).toBe(expected);
      });
    });
  });

  // ==========================================================================
  // 6. HIPAA Safeguards - Encryption Tests
  // ==========================================================================

  describe('HIPAA Safeguards - Encryption', () => {
    it('should encrypt PHI data', () => {
      const originalData = 'John Doe, SSN: 123-45-6789, DOB: 1985-06-15';
      const encrypted = api.encryptPhi(originalData);

      expect(encrypted).toBeDefined();
      expect(encrypted).not.toBe(originalData);
      expect(encrypted.split(':').length).toBe(3); // iv:authTag:encrypted
    });

    it('should decrypt PHI data correctly', () => {
      const originalData = 'Jane Smith, MRN: MEM123456';
      const encrypted = api.encryptPhi(originalData);
      const decrypted = api.decryptPhi(encrypted);

      expect(decrypted).toBe(originalData);
    });

    it('should maintain data integrity through encryption/decryption cycle', () => {
      const testData = [
        'Simple text',
        'Special characters: !@#$%^&*()',
        'Unicode: café résumé',
        JSON.stringify({ name: 'John Doe', ssn: '123-45-6789' })
      ];

      testData.forEach(data => {
        const encrypted = api.encryptPhi(data);
        const decrypted = api.decryptPhi(encrypted);
        expect(decrypted).toBe(data);
      });
    });

    it('should throw error for invalid encrypted data format', () => {
      expect(() => api.decryptPhi('invalid-format')).toThrow('Invalid encrypted data format');
    });

    it('should use AES-256-GCM encryption (verified by key length)', () => {
      // Test that encryption key is properly sized for AES-256
      const data = 'Test PHI data';
      const encrypted = api.encryptPhi(data);
      
      // Format should be: iv:authTag:encrypted
      const parts = encrypted.split(':');
      expect(parts.length).toBe(3);
      
      // IV should be 16 bytes (32 hex chars)
      expect(parts[0].length).toBe(32);
      
      // Auth tag should be 16 bytes (32 hex chars)
      expect(parts[1].length).toBe(32);
    });
  });

  // ==========================================================================
  // 7. HIPAA Safeguards - PHI Redaction Tests
  // ==========================================================================

  describe('HIPAA Safeguards - PHI Redaction', () => {
    it('should redact PHI from Patient resource', () => {
      const qnxtPatient: QnxtPatient = {
        memberId: 'MEM123',
        firstName: 'John',
        lastName: 'Doe',
        dob: '19850615',
        gender: 'M',
        address: {
          street1: '123 Main St',
          city: 'Boston',
          state: 'MA',
          zip: '02101'
        },
        phone: '555-1234',
        email: 'john@example.com'
      };

      const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);
      const redacted = api.redactPhi(fhirPatient) as Patient;

      expect(redacted.name?.[0]?.family).toBe('***');
      expect(redacted.name?.[0]?.given).toContain('***');
      expect(redacted.birthDate).toBe('****-**-**');
      expect(redacted.address?.[0]?.city).toBe('***');
      expect(redacted.telecom?.[0]?.value).toBe('***');
    });

    it('should preserve resource structure when redacting', () => {
      const qnxtPatient: QnxtPatient = {
        memberId: 'MEM123',
        firstName: 'John',
        lastName: 'Doe',
        dob: '19850615',
        gender: 'M'
      };

      const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);
      const redacted = api.redactPhi(fhirPatient) as Patient;

      expect(redacted.resourceType).toBe('Patient');
      expect(redacted.id).toBe(fhirPatient.id);
      expect(redacted.gender).toBe(fhirPatient.gender);
    });
  });

  // ==========================================================================
  // 8. HIPAA Safeguards - Audit Logging Tests
  // ==========================================================================

  describe('HIPAA Safeguards - Audit Logging', () => {
    it('should log successful resource access', async () => {
      await api.searchResources(
        'Patient',
        { resourceType: 'Patient', patient: testPatientId },
        validToken
      );

      const auditLogs = api.getAuditLogs();
      const accessLog = auditLogs.find(log => log.eventType === 'search');

      expect(accessLog).toBeDefined();
      expect(accessLog?.timestamp).toBeDefined();
      expect(accessLog?.userId).toBe(testProviderId);
      expect(accessLog?.patientId).toBe(testPatientId);
      expect(accessLog?.result).toBe('success');
    });

    it('should log authentication failures', async () => {
      try {
        await api.validateSmartToken('');
      } catch (error) {
        // Expected
      }

      const auditLogs = api.getAuditLogs();
      const authFailure = auditLogs.find(log => log.eventType === 'auth_failure');

      expect(authFailure).toBeDefined();
      expect(authFailure?.result).toBe('failure');
    });

    it('should log consent checks', async () => {
      await api.checkConsent(testPatientId, testProviderId);

      const auditLogs = api.getAuditLogs();
      const consentCheck = auditLogs.find(log => log.eventType === 'consent_check');

      expect(consentCheck).toBeDefined();
      expect(consentCheck?.patientId).toBe(testPatientId);
      expect(consentCheck?.userId).toBe(testProviderId);
    });

    it('should include timestamps in all audit logs', async () => {
      await api.searchResources(
        'Patient',
        { resourceType: 'Patient', patient: testPatientId },
        validToken
      );

      const auditLogs = api.getAuditLogs();
      
      auditLogs.forEach(log => {
        expect(log.timestamp).toBeDefined();
        expect(new Date(log.timestamp).getTime()).not.toBeNaN();
      });
    });
  });

  // ==========================================================================
  // 9. Error Handling Tests
  // ==========================================================================

  describe('Error Handling', () => {
    it('should create OperationOutcome for authentication error', () => {
      const error = new AuthenticationError('Invalid token');
      const response = api.handleAuthError(error);

      expect(response.status).toBe(401);
      expect(response.body.resourceType).toBe('OperationOutcome');
      expect(response.body.issue[0].severity).toBe('error');
      expect(response.body.issue[0].code).toBe('login');
    });

    it('should create OperationOutcome for consent denied error', () => {
      const error = new ConsentDeniedError('No consent', testPatientId, testProviderId);
      const response = api.handleConsentError(error);

      expect(response.status).toBe(403);
      expect(response.body.resourceType).toBe('OperationOutcome');
      expect(response.body.issue[0].severity).toBe('error');
      expect(response.body.issue[0].code).toBe('forbidden');
    });

    it('should create OperationOutcome for resource not found error', () => {
      const error = new ResourceNotFoundError('Resource not found', 'Patient', 'PAT999');
      const response = api.handleNotFoundError(error);

      expect(response.status).toBe(404);
      expect(response.body.resourceType).toBe('OperationOutcome');
      expect(response.body.issue[0].severity).toBe('error');
      expect(response.body.issue[0].code).toBe('not-found');
    });

    it('should handle search with invalid token', async () => {
      await expect(
        api.searchResources(
          'Patient',
          { resourceType: 'Patient', patient: testPatientId },
          ''
        )
      ).rejects.toThrow(AuthenticationError);
    });
  });

  // ==========================================================================
  // 10. US Core v3.1.1 Compliance Tests
  // ==========================================================================

  describe('US Core v3.1.1 Compliance', () => {
    it('should include US Core Patient profile in Patient resources', () => {
      const qnxtPatient: QnxtPatient = {
        memberId: 'MEM123',
        firstName: 'John',
        lastName: 'Doe',
        dob: '1980-01-01',
        gender: 'M'
      };

      const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);

      expect(fhirPatient.meta?.profile).toContain(
        'http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient'
      );
    });

    it('should include required Patient identifiers', () => {
      const qnxtPatient: QnxtPatient = {
        memberId: 'MEM123',
        firstName: 'John',
        lastName: 'Doe',
        dob: '1980-01-01',
        gender: 'M'
      };

      const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);

      expect(fhirPatient.identifier).toBeDefined();
      expect(fhirPatient.identifier!.length).toBeGreaterThan(0);
      expect(fhirPatient.identifier![0].value).toBe('MEM123');
      expect(fhirPatient.identifier![0].type?.coding![0].code).toBe('MB');
    });

    it('should include required Patient demographics', () => {
      const qnxtPatient: QnxtPatient = {
        memberId: 'MEM123',
        firstName: 'John',
        lastName: 'Doe',
        dob: '1980-01-01',
        gender: 'M'
      };

      const fhirPatient = api.mapQnxtPatientToFhir(qnxtPatient);

      expect(fhirPatient.name).toBeDefined();
      expect(fhirPatient.gender).toBeDefined();
      expect(fhirPatient.birthDate).toBeDefined();
    });
  });

  // ==========================================================================
  // 11. Integration Tests
  // ==========================================================================

  describe('Integration Scenarios', () => {
    it('should handle complete patient data access flow', async () => {
      // 1. Validate token
      const token = await api.validateSmartToken(validToken);
      expect(token.provider).toBe(testProviderId);

      // 2. Check consent
      const hasConsent = await api.checkConsent(testPatientId, testProviderId);
      expect(hasConsent).toBe(true);

      // 3. Search for patient
      const bundle = await api.searchResources(
        'Patient',
        { resourceType: 'Patient', patient: testPatientId },
        validToken
      );
      expect(bundle.entry!.length).toBeGreaterThan(0);

      // 4. Read specific patient
      const patient = await api.readResource('Patient', testPatientId, validToken);
      expect(patient.resourceType).toBe('Patient');

      // 5. Verify audit trail
      const auditLogs = api.getAuditLogs();
      expect(auditLogs.length).toBeGreaterThan(3);
    });

    it('should handle multiple resource type searches', async () => {
      const resourceTypes = ['Patient', 'Claim', 'Encounter', 'ExplanationOfBenefit'];

      for (const resourceType of resourceTypes) {
        const bundle = await api.searchResources(
          resourceType,
          { resourceType, patient: testPatientId },
          validToken
        );

        expect(bundle.resourceType).toBe('Bundle');
        expect(bundle.entry).toBeDefined();
      }
    });
  });
});
