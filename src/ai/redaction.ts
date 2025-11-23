/**
 * HIPAA-compliant PHI redaction for Cloud Health Office
 * Implements comprehensive detection and masking of Protected Health Information
 */

/**
 * PHI field names that should always be masked
 */
const PHI_FIELD_NAMES = [
  'ssn', 'socialSecurityNumber', 'social_security_number',
  'memberId', 'member_id', 'subscriberId', 'subscriber_id',
  'mrn', 'medicalRecordNumber', 'medical_record_number',
  'patientId', 'patient_id', 'patientName', 'patient_name',
  'firstName', 'first_name', 'lastName', 'last_name', 'name',
  'dob', 'dateOfBirth', 'date_of_birth', 'birthDate', 'birth_date',
  'phone', 'phoneNumber', 'phone_number', 'telephone',
  'email', 'emailAddress', 'email_address',
  'address', 'streetAddress', 'street_address', 'addressLine1', 'address_line_1',
  'city', 'state', 'zip', 'zipCode', 'zip_code', 'postalCode', 'postal_code',
  'accountNumber', 'account_number', 'claimNumber', 'claim_number',
  'licenseNumber', 'license_number', 'certificateNumber', 'certificate_number',
  'vehicleId', 'vehicle_id', 'deviceId', 'device_id', 'ipAddress', 'ip_address',
  'url', 'fax', 'faxNumber', 'fax_number'
];

/**
 * Patterns for detecting PHI in string values
 * Note: These are conservative patterns to avoid false positives with business identifiers
 */
const PHI_PATTERNS = {
  // SSN: 123-45-6789 (with dashes) or standalone 9-digit numbers that look like SSN
  ssn: /\b\d{3}-\d{2}-\d{4}\b/g,
  
  // Email: user@example.com
  email: /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b/g,
  
  // Phone: (123) 456-7890, 123-456-7890, 123 456 7890
  // Requires at least one separator or parens to avoid matching random numbers
  phone: /(?:\(\d{3}\)\s*\d{3}[-.\s]\d{4}|\d{3}[-.\s]\d{3}[-.\s]\d{4})\b/g,
  
  // Date of birth: MM/DD/YYYY format specifically
  dob: /\b(?:0?[1-9]|1[0-2])\/(?:0?[1-9]|[12][0-9]|3[01])\/(?:19|20)\d{2}\b/g,
  
  // ZIP code: Only match when not part of longer identifiers (5 or 9 digit)
  // Using negative lookbehind/ahead to avoid matching parts of longer IDs
  zip: /\b\d{5}(?:-\d{4})?\b(?!\d)/g,
  
  // Credit card: 16 digits with optional separators
  creditCard: /\b(?:\d{4}[-\s]){3}\d{4}\b/g,
  
  // IP Address: Full format only
  ipAddress: /\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b/g,
  
  // URL with potential PHI
  url: /https?:\/\/[^\s<>"{}|\\^[\]`]+/g
};

/**
 * Check if a string value contains PHI patterns
 */
export function isPHI(val: string): boolean {
  if (!val || typeof val !== 'string') {
    return false;
  }

  // Check against all PHI patterns
  // Reset lastIndex for global regexes to avoid state issues
  for (const pattern of Object.values(PHI_PATTERNS)) {
    pattern.lastIndex = 0; // Reset regex state
    if (pattern.test(val)) {
      return true;
    }
  }

  return false;
}

/**
 * Check if a field name indicates it contains PHI
 */
export function isPHIFieldName(fieldName: string): boolean {
  if (!fieldName) return false;
  
  const lowerFieldName = fieldName.toLowerCase();
  return PHI_FIELD_NAMES.some(phiName => 
    lowerFieldName === phiName.toLowerCase() || 
    lowerFieldName.includes(phiName.toLowerCase())
  );
}

/**
 * Mask a PHI string value with redaction
 */
export function maskValue(val: string, maskChar: string = '*', visibleChars: number = 0): string {
  if (!val || typeof val !== 'string') {
    return val;
  }

  if (visibleChars === 0) {
    return '***REDACTED***';
  }

  // Show last N characters (useful for debugging while maintaining privacy)
  const masked = maskChar.repeat(Math.max(val.length - visibleChars, 3));
  const visible = val.slice(-visibleChars);
  return masked + visible;
}

/**
 * Redact PHI patterns from a string
 */
export function redactPHI(text: string | any): string {
  if (typeof text !== 'string') {
    return text;
  }

  let redacted = text;

  // Replace all PHI patterns
  redacted = redacted.replace(PHI_PATTERNS.ssn, '***-**-XXXX');
  redacted = redacted.replace(PHI_PATTERNS.email, '***@***.***');
  redacted = redacted.replace(PHI_PATTERNS.phone, '(***) ***-XXXX');
  redacted = redacted.replace(PHI_PATTERNS.dob, 'MM/DD/YYYY');
  redacted = redacted.replace(PHI_PATTERNS.zip, 'XXXXX');
  redacted = redacted.replace(PHI_PATTERNS.creditCard, '****-****-****-XXXX');
  redacted = redacted.replace(PHI_PATTERNS.ipAddress, 'XXX.XXX.XXX.XXX');
  redacted = redacted.replace(PHI_PATTERNS.url, '[URL-REDACTED]');

  return redacted;
}

/**
 * Recursively mask PHI fields in an object based on field names
 * This is the primary function for anonymizing EDI payloads
 */
export function maskPHIFields<T>(obj: T, options?: {
  maskChar?: string;
  visibleChars?: number;
  preserveStructure?: boolean;
}): T {
  if (!obj || typeof obj !== 'object') {
    return obj;
  }

  const {
    maskChar = '*',
    visibleChars = 0,
    preserveStructure = true
  } = options || {};

  // Handle arrays
  if (Array.isArray(obj)) {
    return obj.map(item => maskPHIFields(item, options)) as unknown as T;
  }

  // Handle objects
  const masked: any = preserveStructure ? { ...obj } : {};

  for (const key in obj) {
    const value = (obj as any)[key];

    // Check if field name indicates PHI
    if (isPHIFieldName(key)) {
      if (typeof value === 'string') {
        masked[key] = maskValue(value, maskChar, visibleChars);
      } else if (preserveStructure) {
        masked[key] = '***REDACTED***';
      }
    } 
    // Check if value contains PHI patterns
    else if (typeof value === 'string' && isPHI(value)) {
      masked[key] = redactPHI(value);
    }
    // Recursively process nested objects
    else if (typeof value === 'object' && value !== null) {
      masked[key] = maskPHIFields(value, options);
    }
    // Preserve non-PHI values
    else {
      masked[key] = value;
    }
  }

  return masked as T;
}

/**
 * Create a safe version of an object for logging or AI processing
 * This combines field-based and pattern-based redaction
 */
export function createSafePayload<T>(obj: T, options?: {
  allowedFields?: string[];
  maskChar?: string;
  visibleChars?: number;
}): T {
  const {
    allowedFields = [],
    maskChar = '*',
    visibleChars = 0
  } = options || {};

  if (!obj || typeof obj !== 'object') {
    return obj;
  }

  // First pass: mask PHI fields
  let safe = maskPHIFields(obj, { maskChar, visibleChars, preserveStructure: true });

  // Second pass: preserve allowed fields (even if they look like PHI)
  if (allowedFields.length > 0) {
    const restore = (safeObj: any, origObj: any, path: string = ''): any => {
      if (typeof origObj !== 'object' || origObj === null) {
        return safeObj;
      }

      const result = Array.isArray(origObj) ? [...safeObj] : { ...safeObj };

      for (const key in origObj) {
        const fieldPath = path ? `${path}.${key}` : key;
        
        if (allowedFields.includes(fieldPath) || allowedFields.includes(key)) {
          result[key] = origObj[key];
        } else if (typeof origObj[key] === 'object' && origObj[key] !== null) {
          result[key] = restore(safeObj[key], origObj[key], fieldPath);
        }
      }

      return result;
    };

    safe = restore(safe, obj);
  }

  return safe;
}

/**
 * Validate that a payload has been properly redacted
 * Returns true if no PHI patterns are detected
 */
export function validateRedaction(obj: any): { isValid: boolean; violations: string[] } {
  const violations: string[] = [];

  const checkValue = (value: any, path: string = ''): void => {
    if (typeof value === 'string') {
      for (const [patternName, pattern] of Object.entries(PHI_PATTERNS)) {
        if (pattern.test(value)) {
          violations.push(`${path}: Detected ${patternName} pattern in value`);
        }
      }
    } else if (typeof value === 'object' && value !== null) {
      for (const key in value) {
        const newPath = path ? `${path}.${key}` : key;
        
        if (isPHIFieldName(key) && value[key] !== '***REDACTED***' && !value[key]?.toString().includes('REDACTED')) {
          violations.push(`${newPath}: PHI field not redacted`);
        }
        
        checkValue(value[key], newPath);
      }
    }
  };

  checkValue(obj);

  return {
    isValid: violations.length === 0,
    violations
  };
}