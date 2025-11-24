import { 
  resolveEdi277Claim, 
  EDI277Payload, 
  ErrorScenario,
  getMetrics,
  resetMetrics,
  resetRateLimiter
} from "../edi277Resolution";
import { maskPHIFields, validateRedaction } from "../redaction";

describe("AI EDI 277 Error Resolution", () => {
  beforeEach(() => {
    resetMetrics();
    resetRateLimiter();
  });

  describe("Mock Mode - Basic Functionality", () => {
    it("should resolve in mock mode with basic payload", async () => {
      const samplePayload: EDI277Payload = {
        transactionId: "TRX555",
        payer: "BestMed",
        memberId: "MBR123456789",
        errorCode: "123X",
        errorDesc: "INVALID MEMBER ID",
      };
      
      const result = await resolveEdi277Claim(samplePayload, true);
      
      expect(result.suggestions.length).toBeGreaterThan(0);
      expect(result.model).toBe("mock");
      expect(result.transactionId).toBe("TRX555");
      expect(result.processingTimeMs).toBeGreaterThanOrEqual(0);
      expect(result.confidence).toBeGreaterThan(0);
    });

    it("should return appropriate suggestions for member ID errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX001",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "ID01",
        errorDesc: "Invalid Member ID format",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.suggestions.length).toBeGreaterThan(0);
      expect(result.scenario).toBe(ErrorScenario.MEMBER_ID_INVALID);
      expect(result.suggestions.some(s => s.toLowerCase().includes('member'))).toBe(true);
    });

    it("should return appropriate suggestions for eligibility errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX002",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "EL01",
        errorDesc: "Member not eligible on date of service",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.scenario).toBe(ErrorScenario.ELIGIBILITY_ISSUE);
      expect(result.suggestions.some(s => 
        s.toLowerCase().includes('eligib') || s.toLowerCase().includes('coverage')
      )).toBe(true);
    });

    it("should return appropriate suggestions for prior auth errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX003",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "PA01",
        errorDesc: "Prior authorization required",
        statusCategory: "Denied"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.scenario).toBe(ErrorScenario.PRIOR_AUTH_REQUIRED);
      expect(result.suggestions.some(s => 
        s.toLowerCase().includes('authorization') || s.toLowerCase().includes('auth')
      )).toBe(true);
    });

    it("should return appropriate suggestions for duplicate claim errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX004",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "DUP01",
        errorDesc: "Duplicate claim submission",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.scenario).toBe(ErrorScenario.DUPLICATE_CLAIM);
      expect(result.suggestions.some(s => 
        s.toLowerCase().includes('duplicate') || s.toLowerCase().includes('corrected')
      )).toBe(true);
    });

    it("should return appropriate suggestions for timely filing errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX005",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TF01",
        errorDesc: "Timely filing deadline exceeded",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.scenario).toBe(ErrorScenario.TIMELY_FILING);
      expect(result.suggestions.some(s => 
        s.toLowerCase().includes('timely') || s.toLowerCase().includes('deadline')
      )).toBe(true);
    });

    it("should return appropriate suggestions for coding errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX006",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "CD01",
        errorDesc: "Invalid procedure code",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.scenario).toBe(ErrorScenario.CODING_ERROR);
      expect(result.suggestions.some(s => 
        s.toLowerCase().includes('code') || s.toLowerCase().includes('cpt')
      )).toBe(true);
    });
  });

  describe("PHI Redaction", () => {
    it("should redact PHI from payload before processing", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX007",
        payer: "TestPayer",
        memberId: "MBR123456789", // Test member ID (not real PHI)
        claimNumber: "CLM123456",
        providerNpi: "1234567890",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      const masked = maskPHIFields(payload);
      
      expect(masked.memberId).toBe("***REDACTED***");
      expect(masked.transactionId).toBe("TRX007"); // Not PHI
      expect(masked.errorCode).toBe("TEST"); // Not PHI
    });

    it("should validate that redacted payloads contain no PHI", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX008",
        payer: "TestPayer",
        memberId: "MBR123456789",
        errorCode: "TEST",
        errorDesc: "Member MBR123456789 not found"
      };

      const masked = maskPHIFields(payload);
      const validation = validateRedaction(masked);
      
      expect(validation.isValid).toBe(true);
      expect(validation.violations.length).toBe(0);
    });

    it("should not redact non-PHI fields", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX009",
        payer: "HealthPlan123",
        payerId: "HP001",
        memberId: "M123456", // Not SSN format
        errorCode: "ERR123",
        errorDesc: "Test error description",
        statusCategory: "Rejected"
      };

      const masked = maskPHIFields(payload);
      
      expect(masked.transactionId).toBe("TRX009");
      expect(masked.payer).toBe("HealthPlan123");
      expect(masked.payerId).toBe("HP001");
      expect(masked.errorCode).toBe("ERR123");
      expect(masked.errorDesc).toBe("Test error description");
    });
  });

  describe("Rate Limiting", () => {
    it("should not apply rate limiting in mock mode", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX010",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      // First request in mock mode should succeed
      const result1 = await resolveEdi277Claim(payload, true);
      expect(result1.model).toBe("mock");

      // Immediate second request in mock mode should also succeed (no rate limiting)
      const result2 = await resolveEdi277Claim(payload, true);
      expect(result2.model).toBe("mock");
      
      const metrics = getMetrics();
      expect(metrics.rateLimitHits).toBe(0); // No rate limit hits in mock mode
    });

    it("should track mock mode requests separately", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX011",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      const initialMetrics = getMetrics();
      const initialMockRequests = initialMetrics.mockModeRequests;

      await resolveEdi277Claim(payload, true);

      const finalMetrics = getMetrics();
      expect(finalMetrics.mockModeRequests).toBeGreaterThan(initialMockRequests);
    });
  });

  describe("Metrics Tracking", () => {
    it("should track successful requests", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX012",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      await resolveEdi277Claim(payload, true);
      
      const metrics = getMetrics();
      expect(metrics.totalRequests).toBe(1);
      expect(metrics.successfulRequests).toBe(1);
      expect(metrics.mockModeRequests).toBe(1);
    });

    it("should track processing time", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX013",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.processingTimeMs).toBeGreaterThanOrEqual(0);
      
      const metrics = getMetrics();
      expect(metrics.averageProcessingTimeMs).toBeGreaterThanOrEqual(0);
    });

    it("should reset metrics correctly", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX014",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      await resolveEdi277Claim(payload, true);
      
      resetMetrics();
      
      const metrics = getMetrics();
      expect(metrics.totalRequests).toBe(0);
      expect(metrics.successfulRequests).toBe(0);
    });
  });

  describe("Configuration", () => {
    it("should accept custom rate limit configuration", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX015",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      const config = {
        rateLimitMs: 100 // Very short for testing
      };

      await resolveEdi277Claim(payload, true, config);
      
      // Should be able to make another request after 100ms
      await new Promise(resolve => setTimeout(resolve, 150));
      await expect(
        resolveEdi277Claim(payload, true, config)
      ).resolves.toBeDefined();
    });

    it("should throw error when Azure OpenAI config is missing in live mode", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX016",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error"
      };

      // Temporarily clear environment variables
      const originalEndpoint = process.env.AZURE_OPENAI_ENDPOINT;
      const originalKey = process.env.AZURE_OPENAI_API_KEY;
      
      delete process.env.AZURE_OPENAI_ENDPOINT;
      delete process.env.AZURE_OPENAI_API_KEY;

      await expect(
        resolveEdi277Claim(payload, false) // mockMode = false
      ).rejects.toThrow(/configuration missing/i);

      // Restore environment variables
      if (originalEndpoint) process.env.AZURE_OPENAI_ENDPOINT = originalEndpoint;
      if (originalKey) process.env.AZURE_OPENAI_API_KEY = originalKey;
    });
  });

  describe("Error Scenario Categorization", () => {
    it("should correctly categorize provider credential errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX017",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "PR01",
        errorDesc: "Provider not found in network",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      expect(result.scenario).toBe(ErrorScenario.PROVIDER_CREDENTIAL);
    });

    it("should correctly categorize service not covered errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX018",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "SV01",
        errorDesc: "Service is not covered under plan",
        statusCategory: "Denied"
      };

      const result = await resolveEdi277Claim(payload, true);
      expect(result.scenario).toBe(ErrorScenario.SERVICE_NOT_COVERED);
    });

    it("should correctly categorize missing information errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX019",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "MI01",
        errorDesc: "Missing required diagnosis code",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      expect(result.scenario).toBe(ErrorScenario.MISSING_INFORMATION);
    });

    it("should use general category for unrecognized errors", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX020",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "UNK",
        errorDesc: "Unknown error type",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      expect(result.scenario).toBe(ErrorScenario.GENERAL);
    });
  });

  describe("Response Quality", () => {
    it("should return actionable suggestions", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX021",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error for suggestions",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      // All suggestions should be non-empty and reasonable length
      result.suggestions.forEach(suggestion => {
        expect(suggestion.length).toBeGreaterThan(10);
        expect(suggestion.length).toBeLessThan(200);
      });
    });

    it("should return confidence score", async () => {
      const payload: EDI277Payload = {
        transactionId: "TRX022",
        payer: "TestPayer",
        memberId: "M123456",
        errorCode: "TEST",
        errorDesc: "Test error",
        statusCategory: "Rejected"
      };

      const result = await resolveEdi277Claim(payload, true);
      
      expect(result.confidence).toBeDefined();
      expect(result.confidence).toBeGreaterThan(0);
      expect(result.confidence).toBeLessThanOrEqual(1);
    });
  });
});