import { resolveEdi277Claim } from "../edi277Resolution";

describe("AI EDI 277 Error Resolution", () => {
  it("should resolve in mock mode", async () => {
    const samplePayload = {
      transactionId: "TRX555",
      payer: "BestMed",
      memberId: "123-45-6789", // PHI format for demonstration
      errorCode: "123X",
      errorDesc: "INVALID MEMBER ID",
    };
    const result = await resolveEdi277Claim(samplePayload, true);
    expect(result.suggestions.length).toBeGreaterThan(0);
    expect(result.model).toBe("mock");
  });
});