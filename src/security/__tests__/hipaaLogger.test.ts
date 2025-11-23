/**
 * Tests for HIPAA Logger Module
 */

import {
  isPHI,
  redactValue,
  redactPHI,
  validateRedaction,
  createHIPAALogger,
} from '../hipaaLogger';

describe('HIPAA Logger', () => {
  describe('isPHI', () => {
    it('should detect SSN patterns', () => {
      expect(isPHI('123-45-6789')).toBe(true);
      expect(isPHI('123456789')).toBe(true);
      expect(isPHI('123-45-6789', 'SSN')).toBe(true);
    });

    it('should detect MRN patterns', () => {
      expect(isPHI('MRN123456')).toBe(true);
      expect(isPHI('mrn987654321')).toBe(true);
    });

    it('should detect phone patterns', () => {
      expect(isPHI('1234567890')).toBe(true);
      expect(isPHI('+11234567890')).toBe(true);
    });

    it('should detect email patterns', () => {
      expect(isPHI('test@example.com')).toBe(true);
      expect(isPHI('user.name@domain.org')).toBe(true);
    });

    it('should not flag non-PHI values', () => {
      expect(isPHI('CLM-2024-001')).toBe(false);
      expect(isPHI('approved')).toBe(false);
      expect(isPHI('')).toBe(false);
    });
  });

  describe('redactValue', () => {
    it('should redact values showing first and last characters', () => {
      const result = redactValue('john.doe@example.com');
      expect(result).toMatch(/^j\*+m$/);
    });

    it('should handle short values', () => {
      expect(redactValue('ab')).toBe('***');
    });

    it('should handle single character', () => {
      expect(redactValue('a')).toBe('***');
    });

    it('should return original value if not string', () => {
      expect(redactValue('' as any)).toBe('');
    });
  });

  describe('redactPHI', () => {
    it('should redact PHI fields in an object', () => {
      const patient = {
        firstName: 'John',
        lastName: 'Doe',
        ssn: '123-45-6789',
        claimNumber: 'CLM-2024-001',
      };

      const redacted = redactPHI(patient);

      expect(redacted.firstName).toMatch(/^J\*+n$/);
      expect(redacted.lastName).toMatch(/^D\*+e$/);
      expect(redacted.ssn).toMatch(/^1\*+9$/);
      expect(redacted.claimNumber).toBe('CLM-2024-001'); // Non-PHI preserved
    });

    it('should recursively redact nested objects', () => {
      const data = {
        claim: {
          id: 'CLM-001',
          patient: {
            firstName: 'Jane',
            email: 'jane@example.com',
          },
        },
      };

      const redacted = redactPHI(data);

      expect(redacted.claim.id).toBe('CLM-001');
      expect(redacted.claim.patient.firstName).toMatch(/^J\*+e$/);
      expect(redacted.claim.patient.email).toMatch(/^j\*+m$/);
    });

    it('should handle arrays', () => {
      const data = {
        patients: [
          { name: 'Alice', ssn: '111-22-3333' },
          { name: 'Bob', ssn: '444-55-6666' },
        ],
      };

      const redacted = redactPHI(data);

      expect(redacted.patients[0].name).toMatch(/^A\*+e$/);
      expect(redacted.patients[0].ssn).toMatch(/^1\*+3$/);
      expect(redacted.patients[1].name).toMatch(/^B\*+b$/);
    });

    it('should use custom PHI field list', () => {
      const data = {
        customField: 'sensitive',
        publicField: 'public',
      };

      const redacted = redactPHI(data, ['customField']);

      expect(redacted.customField).toMatch(/^s\*+e$/);
      expect(redacted.publicField).toBe('public');
    });
  });

  describe('validateRedaction', () => {
    it('should detect unredacted SSN', () => {
      const data = {
        claimId: 'CLM-001',
        ssn: '123-45-6789',
      };

      const result = validateRedaction(data);

      expect(result.isValid).toBe(false);
      expect(result.violations).toContain('root.ssn: Unredacted SSN detected');
    });

    it('should detect unredacted email', () => {
      const data = {
        email: 'test@example.com',
      };

      const result = validateRedaction(data);

      expect(result.isValid).toBe(false);
      expect(result.violations).toContain('root.email: Unredacted email detected');
    });

    it('should pass for properly redacted data', () => {
      const data = {
        claimId: 'CLM-001',
        status: 'approved',
        redactedSSN: '1********9',
      };

      const result = validateRedaction(data);

      expect(result.isValid).toBe(true);
      expect(result.violations).toHaveLength(0);
    });

    it('should validate nested objects', () => {
      const data = {
        claim: {
          patient: {
            ssn: '123-45-6789',
          },
        },
      };

      const result = validateRedaction(data);

      expect(result.isValid).toBe(false);
      expect(result.violations.length).toBeGreaterThan(0);
    });
  });

  describe('createHIPAALogger', () => {
    let consoleLogSpy: jest.SpyInstance;

    beforeEach(() => {
      consoleLogSpy = jest.spyOn(console, 'log').mockImplementation();
    });

    afterEach(() => {
      consoleLogSpy.mockRestore();
    });

    it('should log data access', () => {
      const logger = createHIPAALogger('user@example.com', '192.168.1.1');

      logger.logDataAccess('Patient', 'PAT-12345', 'VIEW');

      // userId and ipAddress should be redacted (they are PHI)
      expect(consoleLogSpy).toHaveBeenCalled();
      const logCall = consoleLogSpy.mock.calls[0][1];
      expect(logCall).toContain('PAT-12345'); // Non-PHI preserved
      expect(logCall).toContain('VIEW'); // Action preserved
      expect(logCall).not.toContain('user@example.com'); // Email redacted
      expect(logCall).not.toContain('192.168.1.1'); // IP redacted
    });

    it('should log access denied', () => {
      const logger = createHIPAALogger('user@example.com');

      logger.logAccessDenied('Claim', 'CLM-67890', 'Insufficient permissions');

      expect(consoleLogSpy).toHaveBeenCalledWith(
        '[HIPAA-AUDIT]',
        expect.stringContaining('ACCESS_DENIED')
      );
      expect(consoleLogSpy).toHaveBeenCalledWith(
        '[HIPAA-AUDIT]',
        expect.stringContaining('Insufficient permissions')
      );
    });

    it('should log data export', () => {
      const logger = createHIPAALogger('user@example.com');

      logger.logDataExport('Patient', 150, 'Analytics System');

      expect(consoleLogSpy).toHaveBeenCalledWith(
        '[HIPAA-AUDIT]',
        expect.stringContaining('DATA_EXPORT')
      );
      expect(consoleLogSpy).toHaveBeenCalledWith(
        '[HIPAA-AUDIT]',
        expect.stringContaining('150')
      );
      expect(consoleLogSpy).toHaveBeenCalledWith(
        '[HIPAA-AUDIT]',
        expect.stringContaining('Analytics System')
      );
    });

    it('should redact PHI in metadata', () => {
      const logger = createHIPAALogger('user@example.com');

      logger.logAccessDenied('Patient', 'PAT-001', 'Access issue');

      // Verify log was called
      expect(consoleLogSpy).toHaveBeenCalled();
      const logCall = consoleLogSpy.mock.calls[0][1];
      
      // Non-PHI should be preserved
      expect(logCall).toContain('PAT-001');
      expect(logCall).toContain('Access issue');
      expect(logCall).toContain('ACCESS_DENIED');
      
      // Email should be redacted
      expect(logCall).not.toContain('user@example.com');
    });
  });
});
