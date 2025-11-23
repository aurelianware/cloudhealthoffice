import { OpenAIClient, AzureKeyCredential } from "@azure/openai";
import { isPHI, redactPHI } from "./redaction"; // stubbed utilities

export interface EDI277Payload {
  // Structure can be extended for your workflow
  transactionId: string;
  payer: string;
  memberId: string;
  errorCode: string;
  errorDesc: string;
  // ...other fields
}

export interface ResolutionSuggestion {
  transactionId: string;
  suggestions: string[];
  model: string;
}

const endpoint = process.env.AZURE_OPENAI_ENDPOINT!;
const key = process.env.AZURE_OPENAI_API_KEY!;
const model = "gpt-4"; // or your deployed model version

// Basic rate limiting stub
let lastRequest = 0;
const minInterval = 4000; // in ms

/**
 * Accepts an EDI 277 payload, redacts PHI, and gets a fix suggestion from Azure OpenAI.
 * Set mockMode=true to skip API call.
 */
export async function resolveEdi277Claim(
  payload: EDI277Payload,
  mockMode = false
): Promise<ResolutionSuggestion> {
  // Rate limit
  if (Date.now() - lastRequest < minInterval)
    throw new Error("Too many requests.");

  lastRequest = Date.now();

  // Anonymize PHI in payload
  const safePayload = redactPHI(payload);

  const systemPrompt =
    "You are a health insurance EDI expert. Review the rejection reason and suggest claim fixes in JSON array format.";

  if (mockMode) {
    return {
      transactionId: payload.transactionId,
      suggestions: [
        "Correct member ID format.",
        "Check eligibility dates.",
        "Resubmit with valid payer code.",
      ],
      model: "mock",
    };
  }

  const client = new OpenAIClient(endpoint, new AzureKeyCredential(key));
  const messages = [
    { role: "system", content: systemPrompt },
    { role: "user", content: JSON.stringify(safePayload) },
  ];
  const response = await client.getChatCompletions(model, messages);
  const suggestionText = response.choices[0]?.message?.content ?? "";

  // Anonymize any PHI returned (if present)
  const safeSuggestions: string[] = Array.isArray(suggestionText)
    ? suggestionText
    : [redactPHI(suggestionText)];

  return {
    transactionId: payload.transactionId,
    suggestions: safeSuggestions,
    model,
  };
}