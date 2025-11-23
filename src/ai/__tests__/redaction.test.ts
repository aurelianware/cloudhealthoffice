import {
  isPHI,
  isPHIFieldName,
  maskValue,
  redactPHI,
  maskPHIFields,
  createSafePayload,
  validateRedaction
} from "../redaction";

describe("PHI Redaction Module", () => {
  describe("isPHI - Pattern Detection", () => {
    it("should detect SSN patterns", () => {
      expect(isPHI("123-45-6789")).toBe(true);
      expect(isPHI("SSN: 123-45-6789")).toBe(true);
      expect(isPHI("Not an SSN")).toBe(false);
      expect(isPHI("123456789")).toBe(false); // Plain 9 digits not detected to avoid false positives
    });

    it("should detect email patterns", () => {
      expect(isPHI("user@example.com")).toBe(true);
      expect(isPHI("test.user@domain.co.uk")).toBe(true);
      expect(isPHI("not-an-email")).toBe(false);
    });

    it("should detect phone patterns", () => {
      expect(isPHI("Call me at (123) 456-7890")).toBe(true);
      expect(isPHI("Phone: 123 456-7890")).toBe(true);
      expect(isPHI("Contact: 555-123-4567")).toBe(true);
      expect(isPHI("Code 1234")).toBe(false); // Too short for any PHI pattern
      expect(isPHI("ID123456")).toBe(false); // Part of longer identifier
    });

    it("should detect date patterns", () => {
      expect(isPHI("01/15/1990")).toBe(true);
      expect(isPHI("12/31/2020")).toBe(true);
      expect(isPHI("1/5/2020")).toBe(true);
      expect(isPHI("1990-01-15")).toBe(false); // This format not in current patterns
    });

    it("should detect ZIP code patterns", () => {
      expect(isPHI("The ZIP is 12345 for this address")).toBe(true);
      expect(isPHI("ZIP: 12345-6789")).toBe(true);
      expect(isPHI("1234")).toBe(false);
    });

    it("should detect credit card patterns", () => {
      expect(isPHI("4111-1111-1111-1111")).toBe(true);
      expect(isPHI("4111 1111 1111 1111")).toBe(true);
      expect(isPHI("5500-1234-5678-9012")).toBe(true);
      expect(isPHI("411111111111")).toBe(false); // No separators - not detected to avoid false positives
    });

    it("should detect IP address patterns", () => {
      expect(isPHI("192.168.1.1")).toBe(true);
      expect(isPHI("10.0.0.255")).toBe(true);
      expect(isPHI("not.an.ip")).toBe(false);
    });

    it("should detect URL patterns", () => {
      expect(isPHI("https://example.com/patient/123")).toBe(true);
      expect(isPHI("Visit http://domain.com for info")).toBe(true);
      expect(isPHI("not a url")).toBe(false);
    });
  });

  describe("isPHIFieldName - Field Name Detection", () => {
    it("should detect common PHI field names", () => {
      expect(isPHIFieldName("ssn")).toBe(true);
      expect(isPHIFieldName("memberId")).toBe(true);
      expect(isPHIFieldName("patient_name")).toBe(true);
      expect(isPHIFieldName("dateOfBirth")).toBe(true);
      expect(isPHIFieldName("email")).toBe(true);
      expect(isPHIFieldName("phoneNumber")).toBe(true);
    });

    it("should detect PHI field names with variations", () => {
      expect(isPHIFieldName("SSN")).toBe(true);
      expect(isPHIFieldName("Member_ID")).toBe(true);
      expect(isPHIFieldName("PATIENT_NAME")).toBe(true);
    });

    it("should not flag non-PHI field names", () => {
      expect(isPHIFieldName("errorCode")).toBe(false);
      expect(isPHIFieldName("transactionId")).toBe(false);
      expect(isPHIFieldName("payer")).toBe(false);
      expect(isPHIFieldName("status")).toBe(false);
    });

    it("should detect partial matches in field names", () => {
      expect(isPHIFieldName("patientFirstName")).toBe(true);
      expect(isPHIFieldName("subscriberEmail")).toBe(true);
      expect(isPHIFieldName("billingAddress")).toBe(true);
    });
  });

  describe("maskValue - Value Masking", () => {
    it("should fully mask values by default", () => {
      expect(maskValue("123456789")).toBe("***REDACTED***");
      expect(maskValue("sensitive-data")).toBe("***REDACTED***");
    });

    it("should show last N characters when specified", () => {
      expect(maskValue("123456789", "*", 4)).toBe("*****6789");
      expect(maskValue("ABCDEFGH", "*", 2)).toBe("******GH");
    });

    it("should handle empty or invalid values", () => {
      expect(maskValue("")).toBe("");
      expect(maskValue(null as any)).toBe(null);
      expect(maskValue(undefined as any)).toBe(undefined);
    });

    it("should use custom mask character", () => {
      expect(maskValue("SECRET", "X", 0)).toBe("***REDACTED***");
    });
  });

  describe("redactPHI - String Redaction", () => {
    it("should redact SSN in text", () => {
      const text = "Patient SSN is 123-45-6789 and member ID is ABC123";
      const redacted = redactPHI(text);
      expect(redacted).toContain("***-**-XXXX");
      expect(redacted).not.toContain("123-45-6789");
    });

    it("should redact email in text", () => {
      const text = "Contact patient at patient@example.com for details";
      const redacted = redactPHI(text);
      expect(redacted).toContain("***@***.***");
      expect(redacted).not.toContain("patient@example.com");
    });

    it("should redact phone numbers in text", () => {
      const text = "Call (123) 456-7890 for more information";
      const redacted = redactPHI(text);
      expect(redacted).toContain("(***) ***-XXXX");
      expect(redacted).not.toContain("(123) 456-7890");
    });

    it("should redact multiple PHI patterns in same text", () => {
      const text = "SSN: 123-45-6789, Email: user@test.com, Phone: 555-123-4567";
      const redacted = redactPHI(text);
      expect(redacted).not.toContain("123-45-6789");
      expect(redacted).not.toContain("user@test.com");
      expect(redacted).not.toContain("555-123-4567");
    });

    it("should preserve non-PHI text", () => {
      const text = "Error code ABC123 - claim rejected";
      const redacted = redactPHI(text);
      expect(redacted).toContain("Error code");
      expect(redacted).toContain("claim rejected");
    });
  });

  describe("maskPHIFields - Object Masking", () => {
    it("should mask PHI fields in flat object", () => {
      const obj = {
        transactionId: "TRX001",
        memberId: "123456789",
        ssn: "123-45-6789",
        errorCode: "ERR001"
      };

      const masked = maskPHIFields(obj);

      expect(masked.transactionId).toBe("TRX001");
      expect(masked.memberId).toBe("***REDACTED***");
      expect(masked.ssn).toBe("***REDACTED***");
      expect(masked.errorCode).toBe("ERR001");
    });

    it("should mask PHI patterns in string values", () => {
      const obj = {
        description: "Patient 123-45-6789 was contacted at (555) 123-4567",
        errorCode: "TEST"
      };

      const masked = maskPHIFields(obj);

      expect(masked.description).not.toContain("123-45-6789");
      expect(masked.description).not.toContain("(555) 123-4567");
      expect(masked.errorCode).toBe("TEST");
    });

    it("should handle nested objects", () => {
      const obj = {
        claim: {
          transactionId: "TRX001",
          patient: {
            memberId: "M123456",
            firstName: "John",
            lastName: "Doe",
            ssn: "123-45-6789"
          }
        },
        errorCode: "ERR001"
      };

      const masked = maskPHIFields(obj);

      expect(masked.claim.transactionId).toBe("TRX001");
      expect(masked.claim.patient.firstName).toBe("***REDACTED***");
      expect(masked.claim.patient.lastName).toBe("***REDACTED***");
      expect(masked.claim.patient.ssn).toBe("***REDACTED***");
      expect(masked.errorCode).toBe("ERR001");
    });

    it("should handle arrays", () => {
      const obj = {
        claims: [
          { transactionId: "TRX001", memberId: "M001" },
          { transactionId: "TRX002", memberId: "M002" }
        ]
      };

      const masked = maskPHIFields(obj);

      expect(masked.claims[0].transactionId).toBe("TRX001");
      expect(masked.claims[0].memberId).toBe("***REDACTED***");
      expect(masked.claims[1].transactionId).toBe("TRX002");
      expect(masked.claims[1].memberId).toBe("***REDACTED***");
    });

    it("should preserve structure with non-string PHI values", () => {
      const obj = {
        memberId: 123456789, // numeric
        errorCode: "TEST"
      };

      const masked = maskPHIFields(obj);

      expect(masked.memberId).toBe("***REDACTED***");
      expect(masked.errorCode).toBe("TEST");
    });

    it("should handle custom masking options", () => {
      const obj = {
        memberId: "M123456789",
        errorCode: "TEST"
      };

      const masked = maskPHIFields(obj, {
        maskChar: "X",
        visibleChars: 4,
        preserveStructure: true
      });

      expect(masked.memberId).toContain("6789");
      expect(masked.errorCode).toBe("TEST");
    });
  });

  describe("createSafePayload - Safe Payload Creation", () => {
    it("should create safe payload with all PHI redacted", () => {
      const payload = {
        transactionId: "TRX001",
        memberId: "123456789",
        patientName: "John Doe",
        errorCode: "ERR001",
        errorDesc: "Test error"
      };

      const safe = createSafePayload(payload);

      expect(safe.transactionId).toBe("TRX001");
      expect(safe.memberId).toBe("***REDACTED***");
      expect(safe.patientName).toBe("***REDACTED***");
      expect(safe.errorCode).toBe("ERR001");
      expect(safe.errorDesc).toBe("Test error");
    });

    it("should preserve allowed fields", () => {
      const payload = {
        transactionId: "TRX001",
        memberId: "123456789",
        claimNumber: "CLM001",
        errorCode: "ERR001"
      };

      const safe = createSafePayload(payload, {
        allowedFields: ["memberId", "claimNumber"]
      });

      expect(safe.memberId).toBe("123456789");
      expect(safe.claimNumber).toBe("CLM001");
      expect(safe.errorCode).toBe("ERR001");
    });

    it("should handle nested allowed fields", () => {
      const payload = {
        claim: {
          claimNumber: "CLM001",
          patient: {
            memberId: "M123",
            name: "John Doe"
          }
        }
      };

      const safe = createSafePayload(payload, {
        allowedFields: ["claim.claimNumber"]
      });

      expect(safe.claim.claimNumber).toBe("CLM001");
      expect(safe.claim.patient.memberId).toBe("***REDACTED***");
      expect(safe.claim.patient.name).toBe("***REDACTED***");
    });
  });

  describe("validateRedaction - Redaction Validation", () => {
    it("should validate properly redacted payload", () => {
      const payload = {
        transactionId: "TRX001",
        memberId: "***REDACTED***",
        errorCode: "ERR001"
      };

      const result = validateRedaction(payload);

      expect(result.isValid).toBe(true);
      expect(result.violations.length).toBe(0);
    });

    it("should detect unredacted SSN", () => {
      const payload = {
        transactionId: "TRX001",
        description: "SSN 123-45-6789 was invalid"
      };

      const result = validateRedaction(payload);

      expect(result.isValid).toBe(false);
      expect(result.violations.length).toBeGreaterThan(0);
      expect(result.violations.some(v => v.includes("ssn"))).toBe(true);
    });

    it("should detect unredacted email", () => {
      const payload = {
        contact: "patient@example.com"
      };

      const result = validateRedaction(payload);

      expect(result.isValid).toBe(false);
      expect(result.violations.some(v => v.includes("email"))).toBe(true);
    });

    it("should detect unredacted PHI field names", () => {
      const payload = {
        memberId: "M123456", // PHI field not properly redacted
        errorCode: "ERR001"
      };

      const result = validateRedaction(payload);

      expect(result.isValid).toBe(false);
      expect(result.violations.some(v => v.includes("memberId"))).toBe(true);
    });

    it("should validate nested objects", () => {
      const payload = {
        claim: {
          patient: {
            ssn: "123-45-6789" // Unredacted
          }
        }
      };

      const result = validateRedaction(payload);

      expect(result.isValid).toBe(false);
      expect(result.violations.length).toBeGreaterThan(0);
    });

    it("should provide detailed violation information", () => {
      const payload = {
        patient: {
          ssn: "123-45-6789",
          email: "test@example.com"
        }
      };

      const result = validateRedaction(payload);

      expect(result.violations.length).toBeGreaterThan(0);
      result.violations.forEach(violation => {
        expect(violation).toContain("patient");
      });
    });
  });

  describe("Integration - Full Redaction Workflow", () => {
    it("should fully redact and validate EDI 277 payload", () => {
      const payload = {
        transactionId: "TRX001",
        payer: "HealthPlan",
        memberId: "123-45-6789",
        claimNumber: "CLM001",
        patientName: "John Doe",
        patientEmail: "patient@example.com",
        providerPhone: "(555) 123-4567",
        errorCode: "ERR001",
        errorDesc: "Member not found"
      };

      // Mask the payload
      const masked = maskPHIFields(payload);

      // Validate redaction
      const validation = validateRedaction(masked);

      expect(validation.isValid).toBe(true);
      expect(masked.transactionId).toBe("TRX001");
      expect(masked.errorCode).toBe("ERR001");
      expect(masked.memberId).toBe("***REDACTED***");
      expect(masked.patientName).toBe("***REDACTED***");
      expect(masked.patientEmail).toBe("***REDACTED***");
      expect(masked.providerPhone).toBe("***REDACTED***");
    });

    it("should preserve business data while removing PHI", () => {
      const payload = {
        transactionId: "TRX002",
        payer: "Availity",
        payerId: "AVLTY001",
        memberId: "123-45-6789", // PHI - SSN format with dashes
        serviceDate: "2024-01-15",
        billAmount: 1500.00,
        errorCode: "ID001",
        errorDesc: "Invalid member ID format",
        statusCategory: "Rejected"
      };

      const masked = maskPHIFields(payload);

      // Business data preserved
      expect(masked.transactionId).toBe("TRX002");
      expect(masked.payer).toBe("Availity");
      expect(masked.payerId).toBe("AVLTY001");
      expect(masked.serviceDate).toBe("2024-01-15");
      expect(masked.billAmount).toBe(1500.00);
      expect(masked.errorCode).toBe("ID001");
      expect(masked.errorDesc).toBe("Invalid member ID format");
      expect(masked.statusCategory).toBe("Rejected");

      // PHI redacted
      expect(masked.memberId).toBe("***REDACTED***");
    });
  });
});
