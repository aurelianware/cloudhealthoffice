/**
 * HIPAA-Compliant Logging & PHI Redaction Module
 * 
 * This module provides audit logging and PHI redaction capabilities
 * to ensure HIPAA compliance when handling Protected Health Information.
 * 
 * Best Practices:
 * - Always redact PHI before logging
 * - Log access attempts to PHI data
 * - Maintain audit trails for compliance
 * - Never log raw PHI in production
 */

export interface AuditLogEntry {
  timestamp: string;
  userId: string;
  action: string;
  resourceType: string;
  resourceId?: string;
  ipAddress?: string;
  success: boolean;
  metadata?: Record<string, unknown>;
}

export interface PHIField {
  fieldName: string;
  value: string;
  category: 'SSN' | 'MRN' | 'DOB' | 'NAME' | 'ADDRESS' | 'PHONE' | 'EMAIL' | 'OTHER';
}

/**
 * Patterns for identifying PHI fields
 */
const PHI_PATTERNS = {
  SSN: /^\d{3}-?\d{2}-?\d{4}$/,
  MRN: /^MRN[A-Z0-9]{6,12}$/i,
  DOB: /^\d{4}-\d{2}-\d{2}$/,
  PHONE: /^(\+?1)?(\d{10})$/,
  EMAIL: /^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$/,
};

/**
 * Check if a value matches a PHI pattern
 */
export function isPHI(value: string, category?: keyof typeof PHI_PATTERNS): boolean {
  if (!value || typeof value !== 'string') return false;

  if (category) {
    return PHI_PATTERNS[category]?.test(value) ?? false;
  }

  // Check against all patterns
  return Object.values(PHI_PATTERNS).some(pattern => pattern.test(value));
}

/**
 * Redact PHI from a single value
 */
export function redactValue(value: string): string {
  if (!value || typeof value !== 'string') return value;

  // Show only first and last character for context, redact the rest
  if (value.length <= 2) return '***';
  
  const firstChar = value.charAt(0);
  const lastChar = value.charAt(value.length - 1);
  const redactedMiddle = '*'.repeat(Math.min(value.length - 2, 10));
  
  return `${firstChar}${redactedMiddle}${lastChar}`;
}

/**
 * Recursively redact PHI from an object
 */
export function redactPHI<T>(obj: T, phiFields?: string[]): T {
  if (!obj || typeof obj !== 'object') return obj;

  const fieldsToRedact = phiFields || [
    'ssn', 'socialSecurityNumber',
    'mrn', 'medicalRecordNumber',
    'dob', 'dateOfBirth',
    'firstName', 'lastName', 'fullName', 'name',
    'address', 'street', 'city', 'zipCode',
    'phone', 'phoneNumber', 'mobile',
    'email', 'emailAddress',
  ];

  // Handle arrays
  if (Array.isArray(obj)) {
    return obj.map(item => redactPHI(item, phiFields)) as T;
  }

  const clone: any = { ...obj };

  for (const key in clone) {
    const value = clone[key];

    // Check if field name indicates PHI
    const isPHIField = fieldsToRedact.some(field => 
      key.toLowerCase().includes(field.toLowerCase())
    );

    if (typeof value === 'string') {
      if (isPHIField || isPHI(value)) {
        clone[key] = redactValue(value);
      }
    } else if (typeof value === 'object' && value !== null) {
      // Recursively redact nested objects and arrays
      clone[key] = redactPHI(value, phiFields);
    }
  }

  return clone as T;
}

/**
 * Log PHI access for audit trail
 */
export function logPHIAccess(entry: AuditLogEntry): void {
  // Redact the entire entry to ensure all PHI is scrubbed
  const redactedEntry = redactPHI({
    ...entry,
    metadata: entry.metadata ? redactPHI(entry.metadata) : undefined,
  });

  // In production, send to secure audit log service
  // For now, log to console (should be replaced with proper audit system)
  console.log('[HIPAA-AUDIT]', JSON.stringify(redactedEntry));

  // TODO: Integrate with Azure Monitor/Application Insights for production
  // TODO: Store in immutable audit log storage (e.g., Azure Data Explorer)
}

/**
 * Create a HIPAA-compliant logger wrapper
 */
export function createHIPAALogger(userId: string, ipAddress?: string) {
  return {
    logDataAccess: (resourceType: string, resourceId: string, action: string) => {
      logPHIAccess({
        timestamp: new Date().toISOString(),
        userId,
        action,
        resourceType,
        resourceId,
        ipAddress,
        success: true,
      });
    },

    logAccessDenied: (resourceType: string, resourceId: string, reason: string) => {
      logPHIAccess({
        timestamp: new Date().toISOString(),
        userId,
        action: 'ACCESS_DENIED',
        resourceType,
        resourceId,
        ipAddress,
        success: false,
        metadata: { reason },
      });
    },

    logDataExport: (resourceType: string, recordCount: number, destination: string) => {
      logPHIAccess({
        timestamp: new Date().toISOString(),
        userId,
        action: 'DATA_EXPORT',
        resourceType,
        ipAddress,
        success: true,
        metadata: { recordCount, destination },
      });
    },
  };
}

/**
 * Validate that an object is properly redacted before logging
 */
export function validateRedaction<T>(obj: T): { isValid: boolean; violations: string[] } {
  const violations: string[] = [];

  const checkValue = (value: any, path: string): void => {
    if (typeof value === 'string') {
      // Check for common PHI patterns
      if (PHI_PATTERNS.SSN.test(value)) {
        violations.push(`${path}: Unredacted SSN detected`);
      }
      if (PHI_PATTERNS.MRN.test(value)) {
        violations.push(`${path}: Unredacted MRN detected`);
      }
      if (PHI_PATTERNS.PHONE.test(value)) {
        violations.push(`${path}: Unredacted phone number detected`);
      }
      if (PHI_PATTERNS.EMAIL.test(value)) {
        violations.push(`${path}: Unredacted email detected`);
      }
    } else if (typeof value === 'object' && value !== null) {
      for (const key in value) {
        checkValue(value[key], `${path}.${key}`);
      }
    }
  };

  checkValue(obj, 'root');

  return {
    isValid: violations.length === 0,
    violations,
  };
}
