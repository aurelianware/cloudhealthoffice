import { AzureOpenAI } from "openai";
import { redactPHI, maskPHIFields } from "./redaction";

/**
 * Configuration for AI-driven error resolution
 */
export interface AIErrorResolutionConfig {
  endpoint?: string;
  apiKey?: string;
  deploymentName?: string;
  maxTokens?: number;
  temperature?: number;
  rateLimitMs?: number;
  enableMetrics?: boolean;
}

/**
 * EDI 277 (Healthcare Information Status Notification) payload structure
 * Used for claim status responses and rejection notifications
 */
export interface EDI277Payload {
  transactionId: string;
  payer: string;
  payerId?: string;
  memberId: string;
  claimNumber?: string;
  providerId?: string;
  providerNpi?: string;
  errorCode: string;
  errorDesc: string;
  statusCategory?: string; // Rejected, Denied, Pended, etc.
  serviceDate?: string;
  billAmount?: number;
  additionalInfo?: Record<string, any>;
}

/**
 * Resolution suggestion with detailed metadata
 */
export interface ResolutionSuggestion {
  transactionId: string;
  suggestions: string[];
  model: string;
  confidence?: number;
  processingTimeMs?: number;
  tokenCount?: number;
  scenario?: string;
}

/**
 * Metrics for tracking AI resolution performance
 */
export interface ResolutionMetrics {
  totalRequests: number;
  successfulRequests: number;
  failedRequests: number;
  averageProcessingTimeMs: number;
  averageTokenCount: number;
  rateLimitHits: number;
  mockModeRequests: number;
}

/**
 * Error scenario categorization for targeted prompts
 */
export enum ErrorScenario {
  MEMBER_ID_INVALID = "member_id_invalid",
  ELIGIBILITY_ISSUE = "eligibility_issue",
  PROVIDER_CREDENTIAL = "provider_credential",
  SERVICE_NOT_COVERED = "service_not_covered",
  PRIOR_AUTH_REQUIRED = "prior_auth_required",
  DUPLICATE_CLAIM = "duplicate_claim",
  TIMELY_FILING = "timely_filing",
  CODING_ERROR = "coding_error",
  MISSING_INFORMATION = "missing_information",
  GENERAL = "general"
}

// Global metrics tracking
const metrics: ResolutionMetrics = {
  totalRequests: 0,
  successfulRequests: 0,
  failedRequests: 0,
  averageProcessingTimeMs: 0,
  averageTokenCount: 0,
  rateLimitHits: 0,
  mockModeRequests: 0
};

// Rate limiting state
let lastRequest = 0;

/**
 * Categorize error based on error code and description
 */
function categorizeError(errorCode: string, errorDesc: string): ErrorScenario {
  const lowerDesc = errorDesc.toLowerCase();
  const code = errorCode.toUpperCase();

  // Member ID related
  if (lowerDesc.includes("member") && (lowerDesc.includes("invalid") || lowerDesc.includes("not found"))) {
    return ErrorScenario.MEMBER_ID_INVALID;
  }

  // Service coverage (check before general eligibility to avoid false positives)
  if (lowerDesc.includes("service") && lowerDesc.includes("not covered")) {
    return ErrorScenario.SERVICE_NOT_COVERED;
  }

  // Eligibility related
  if (lowerDesc.includes("eligib") || lowerDesc.includes("not covered") || lowerDesc.includes("not active")) {
    return ErrorScenario.ELIGIBILITY_ISSUE;
  }

  // Provider credential issues
  if (lowerDesc.includes("provider") && (lowerDesc.includes("credential") || lowerDesc.includes("not found"))) {
    return ErrorScenario.PROVIDER_CREDENTIAL;
  }

  // Prior authorization
  if (lowerDesc.includes("prior auth") || lowerDesc.includes("authorization required")) {
    return ErrorScenario.PRIOR_AUTH_REQUIRED;
  }

  // Duplicate claims
  if (lowerDesc.includes("duplicate") || code.includes("DUP")) {
    return ErrorScenario.DUPLICATE_CLAIM;
  }

  // Timely filing
  if (lowerDesc.includes("timely filing") || lowerDesc.includes("submission deadline")) {
    return ErrorScenario.TIMELY_FILING;
  }

  // Coding errors
  if (lowerDesc.includes("code") && (lowerDesc.includes("invalid") || lowerDesc.includes("incorrect"))) {
    return ErrorScenario.CODING_ERROR;
  }

  // Missing information
  if (lowerDesc.includes("missing") || lowerDesc.includes("required") || lowerDesc.includes("incomplete")) {
    return ErrorScenario.MISSING_INFORMATION;
  }

  return ErrorScenario.GENERAL;
}

/**
 * Get scenario-specific system prompt
 */
function getSystemPrompt(scenario: ErrorScenario): string {
  const basePrompt = "You are a healthcare EDI expert specializing in X12 277 claim status resolution. ";
  
  const scenarioPrompts: Record<ErrorScenario, string> = {
    [ErrorScenario.MEMBER_ID_INVALID]: 
      basePrompt + "Focus on member ID validation, format requirements, and database lookup procedures. Suggest checking subscriber vs dependent IDs, SSN vs member number formats, and verification processes.",
    
    [ErrorScenario.ELIGIBILITY_ISSUE]:
      basePrompt + "Focus on eligibility verification procedures, coverage date ranges, plan types, and benefit coordination. Suggest real-time eligibility checks and proper date validation.",
    
    [ErrorScenario.PROVIDER_CREDENTIAL]:
      basePrompt + "Focus on provider enrollment status, NPI validation, credentialing requirements, and network participation. Suggest verification of provider IDs and taxonomy codes.",
    
    [ErrorScenario.SERVICE_NOT_COVERED]:
      basePrompt + "Focus on benefit coverage rules, service authorization requirements, and plan exclusions. Suggest alternative procedure codes or prior authorization processes.",
    
    [ErrorScenario.PRIOR_AUTH_REQUIRED]:
      basePrompt + "Focus on prior authorization workflows, required documentation, and submission procedures. Suggest proper auth request processes and valid authorization numbers.",
    
    [ErrorScenario.DUPLICATE_CLAIM]:
      basePrompt + "Focus on duplicate detection logic, claim identifiers, and resubmission procedures. Suggest using corrected claims or voids when appropriate.",
    
    [ErrorScenario.TIMELY_FILING]:
      basePrompt + "Focus on submission deadline rules, acceptable delay reasons, and corrected claim procedures. Suggest documentation for late submissions.",
    
    [ErrorScenario.CODING_ERROR]:
      basePrompt + "Focus on CPT/HCPCS code validation, ICD-10 requirements, and modifier usage. Suggest proper coding combinations and documentation requirements.",
    
    [ErrorScenario.MISSING_INFORMATION]:
      basePrompt + "Focus on required data elements, X12 segment requirements, and data quality. Suggest specific fields that need to be completed.",
    
    [ErrorScenario.GENERAL]:
      basePrompt + "Analyze the error and provide specific, actionable resolution steps."
  };

  return scenarioPrompts[scenario] + 
    "\n\nProvide 3-5 specific, actionable suggestions in JSON array format. Each suggestion should be concise (max 100 chars) and prioritized by likelihood of resolution.";
}

/**
 * Main function: Accepts an EDI 277 payload, redacts PHI, and gets fix suggestions from Azure OpenAI
 * 
 * @param payload - The EDI 277 rejection payload
 * @param mockMode - If true, returns mock suggestions without calling OpenAI (for testing/validation)
 * @param config - Optional configuration overrides
 * @returns Resolution suggestions with metadata
 */
export async function resolveEdi277Claim(
  payload: EDI277Payload,
  mockMode = false,
  config?: AIErrorResolutionConfig
): Promise<ResolutionSuggestion> {
  const startTime = Date.now();
  metrics.totalRequests++;

  try {
    // Configuration with defaults
    const endpoint = config?.endpoint || process.env.AZURE_OPENAI_ENDPOINT || "";
    const apiKey = config?.apiKey || process.env.AZURE_OPENAI_API_KEY || "";
    const deploymentName = config?.deploymentName || process.env.AZURE_OPENAI_DEPLOYMENT || "gpt-4";
    const maxTokens = config?.maxTokens || 500;
    const temperature = config?.temperature || 0.3; // Lower temperature for more consistent outputs
    const rateLimitMs = config?.rateLimitMs || 4000;

    // Categorize error scenario
    const scenario = categorizeError(payload.errorCode, payload.errorDesc);

    // Mock mode for testing and validation (bypasses rate limiting)
    if (mockMode) {
      metrics.mockModeRequests++;
      metrics.successfulRequests++;
      
      const mockSuggestions = getMockSuggestions(scenario, payload);
      const processingTime = Math.random() * 100;
      
      // Update metrics
      metrics.averageProcessingTimeMs = 
        (metrics.averageProcessingTimeMs * (metrics.successfulRequests - 1) + processingTime) / metrics.successfulRequests;
      
      return {
        transactionId: payload.transactionId,
        suggestions: mockSuggestions,
        model: "mock",
        confidence: 0.85,
        processingTimeMs: processingTime,
        tokenCount: 0,
        scenario
      };
    }

    // Rate limiting (only for live API calls)
    const timeSinceLastRequest = Date.now() - lastRequest;
    if (timeSinceLastRequest < rateLimitMs) {
      metrics.rateLimitHits++;
      throw new Error(`Rate limit exceeded. Please wait ${rateLimitMs - timeSinceLastRequest}ms before next request.`);
    }
    lastRequest = Date.now();

    // Validate configuration
    if (!endpoint || !apiKey) {
      throw new Error("Azure OpenAI configuration missing. Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY environment variables.");
    }

    // Redact PHI from payload
    const safePayload = maskPHIFields(payload);

    // Get scenario-specific prompt
    const systemPrompt = getSystemPrompt(scenario);

    // Prepare user message with structured context
    const userMessage = `
Error Code: ${safePayload.errorCode}
Error Description: ${safePayload.errorDesc}
Status Category: ${safePayload.statusCategory || 'Unknown'}
Transaction ID: ${safePayload.transactionId}

Please analyze this claim rejection and provide specific resolution steps.`;

    // Call Azure OpenAI
    const client = new AzureOpenAI({
      endpoint,
      apiKey,
      deployment: deploymentName,
      apiVersion: "2024-08-01-preview"
    });

    const response = await client.chat.completions.create({
      model: deploymentName,
      messages: [
        { role: "system", content: systemPrompt },
        { role: "user", content: userMessage }
      ],
      max_tokens: maxTokens,
      temperature,
      top_p: 0.95,
      frequency_penalty: 0,
      presence_penalty: 0
    });

    const content = response.choices[0]?.message?.content ?? "";
    const tokenCount = response.usage?.total_tokens || 0;

    // Parse suggestions from response
    let suggestions: string[] = [];
    try {
      // Try to parse as JSON array first
      suggestions = JSON.parse(content);
      if (!Array.isArray(suggestions)) {
        suggestions = [content];
      }
    } catch {
      // If not valid JSON, split by newlines or bullet points
      suggestions = content
        .split(/[\n•\-]/)
        .map((s: string) => s.trim())
        .filter((s: string) => s.length > 0 && s.length < 200)
        .slice(0, 5); // Max 5 suggestions
    }

    // Ensure all suggestions are redacted
    const safeSuggestions = suggestions.map(s => redactPHI(s));

    const processingTime = Date.now() - startTime;

    // Update metrics
    metrics.successfulRequests++;
    metrics.averageProcessingTimeMs = 
      (metrics.averageProcessingTimeMs * (metrics.successfulRequests - 1) + processingTime) / metrics.successfulRequests;
    metrics.averageTokenCount = 
      (metrics.averageTokenCount * (metrics.successfulRequests - 1) + tokenCount) / metrics.successfulRequests;

    return {
      transactionId: payload.transactionId,
      suggestions: safeSuggestions,
      model: deploymentName,
      confidence: 0.75 + (Math.min(tokenCount, 300) / 300) * 0.2, // Higher token usage = more detailed = higher confidence
      processingTimeMs: processingTime,
      tokenCount,
      scenario
    };

  } catch (error) {
    metrics.failedRequests++;
    throw error;
  }
}

/**
 * Get mock suggestions based on scenario
 */
function getMockSuggestions(scenario: ErrorScenario, payload: EDI277Payload): string[] {
  const mockSuggestions: Record<ErrorScenario, string[]> = {
    [ErrorScenario.MEMBER_ID_INVALID]: [
      "Verify member ID format matches payer requirements (e.g., 9 digits vs alphanumeric)",
      "Check if using subscriber ID instead of dependent ID or vice versa",
      "Confirm member is active on service date through real-time eligibility",
      "Validate SSN-based vs member number-based identification",
      "Contact payer for correct member identifier format"
    ],
    [ErrorScenario.ELIGIBILITY_ISSUE]: [
      "Verify coverage dates align with service date",
      "Check if member has active coverage on date of service",
      "Confirm service is covered under member's specific plan type",
      "Run real-time eligibility verification before resubmitting",
      "Check for coordination of benefits or secondary insurance"
    ],
    [ErrorScenario.PROVIDER_CREDENTIAL]: [
      "Verify provider NPI is enrolled with payer",
      "Check provider's network participation status on service date",
      "Confirm provider taxonomy code matches service type",
      "Validate rendering vs billing provider credentials",
      "Complete provider credentialing process if pending"
    ],
    [ErrorScenario.SERVICE_NOT_COVERED]: [
      "Review plan's covered services and exclusions",
      "Check if prior authorization is required for this service",
      "Consider using alternative procedure codes that are covered",
      "Verify medical necessity documentation is included",
      "Appeal with supporting clinical documentation if appropriate"
    ],
    [ErrorScenario.PRIOR_AUTH_REQUIRED]: [
      "Obtain prior authorization before resubmitting claim",
      "Include valid authorization number in claim submission",
      "Verify authorization is still active (not expired)",
      "Confirm authorization covers specific service and dates",
      "Submit retrospective authorization if services are emergent"
    ],
    [ErrorScenario.DUPLICATE_CLAIM]: [
      "Check if original claim is still processing (wait for adjudication)",
      "Submit as corrected claim (frequency code 7) if updating information",
      "Void original claim first if completely replacing submission",
      "Verify different dates of service to avoid duplicate detection",
      "Contact payer to confirm claim status before resubmitting"
    ],
    [ErrorScenario.TIMELY_FILING]: [
      "Review payer's timely filing deadline (typically 90-365 days)",
      "Document reason for delay (e.g., coordination of benefits, retro eligibility)",
      "Submit appeal with supporting documentation for late submission",
      "Check if corrected claim is exempt from timely filing rules",
      "Verify service date and original submission date are accurate"
    ],
    [ErrorScenario.CODING_ERROR]: [
      "Validate CPT/HCPCS code is correct for service provided",
      "Check ICD-10 diagnosis code supports medical necessity",
      "Review modifier usage (e.g., -59, -25) for appropriateness",
      "Confirm code combination is not a NCCI edit",
      "Verify place of service code matches procedure code"
    ],
    [ErrorScenario.MISSING_INFORMATION]: [
      "Review rejected claim for specific missing data elements",
      "Include all required X12 segments per payer specifications",
      "Add supporting documentation or attachments if requested",
      "Verify all required identifiers (NPI, Tax ID, Member ID) are present",
      "Complete all mandatory fields before resubmission"
    ],
    [ErrorScenario.GENERAL]: [
      "Review detailed error description from 277 response",
      "Contact payer for clarification on rejection reason",
      "Verify all claim data matches payer requirements",
      "Check payer's companion guide for specific requirements",
      "Consider submitting corrected claim with updated information"
    ]
  };

  return mockSuggestions[scenario] || mockSuggestions[ErrorScenario.GENERAL];
}

/**
 * Get current metrics
 */
export function getMetrics(): ResolutionMetrics {
  return { ...metrics };
}

/**
 * Reset metrics (useful for testing)
 */
export function resetMetrics(): void {
  metrics.totalRequests = 0;
  metrics.successfulRequests = 0;
  metrics.failedRequests = 0;
  metrics.averageProcessingTimeMs = 0;
  metrics.averageTokenCount = 0;
  metrics.rateLimitHits = 0;
  metrics.mockModeRequests = 0;
}

/**
 * Reset rate limiter (useful for testing)
 */
export function resetRateLimiter(): void {
  lastRequest = 0;
}