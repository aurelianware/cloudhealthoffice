# AI Module - EDI 277 Error Resolution

## Overview

This module provides AI-powered resolution suggestions for rejected EDI 277 (Healthcare Information Status Notification) transactions using Azure OpenAI GPT-4.

## Features

- ✅ 10 specialized error scenario categorizations
- ✅ Azure OpenAI GPT-4 integration
- ✅ Comprehensive HIPAA-compliant PHI redaction
- ✅ Mock mode for testing and development
- ✅ Built-in rate limiting
- ✅ Performance metrics tracking
- ✅ 61 comprehensive tests (100% pass rate)

## Files

### Core Implementation

- **`edi277Resolution.ts`** - Main AI resolution engine
  - Error categorization (10 scenarios)
  - Azure OpenAI integration
  - Mock mode implementation
  - Metrics tracking
  - Rate limiting

- **`redaction.ts`** - PHI detection and masking
  - Pattern-based detection (SSN, email, phone, etc.)
  - Field name-based masking
  - Validation utilities
  - Safe payload creation

### Tests

- **`__tests__/edi277Resolution.test.ts`** - Resolution engine tests (31 tests)
  - Mock mode validation
  - Scenario categorization
  - PHI redaction
  - Rate limiting
  - Metrics tracking
  - Configuration

- **`__tests__/redaction.test.ts`** - PHI redaction tests (30 tests)
  - Pattern detection
  - Field masking
  - Object redaction
  - Validation
  - Integration scenarios

## Quick Start

```typescript
import { resolveEdi277Claim, EDI277Payload } from './edi277Resolution';

const rejectedClaim: EDI277Payload = {
  transactionId: "TRX001",
  payer: "HealthPlan",
  memberId: "M123456",
  errorCode: "ID001",
  errorDesc: "Invalid member ID format",
  statusCategory: "Rejected"
};

// Mock mode (testing)
const mockResult = await resolveEdi277Claim(rejectedClaim, true);

// Live mode (production)
const liveResult = await resolveEdi277Claim(rejectedClaim, false);

console.log(`Scenario: ${liveResult.scenario}`);
console.log(`Confidence: ${liveResult.confidence}`);
liveResult.suggestions.forEach((s, i) => {
  console.log(`${i + 1}. ${s}`);
});
```

## Error Scenarios

| Scenario | Code | Description |
|----------|------|-------------|
| `member_id_invalid` | MEMBER_ID_INVALID | Invalid or not found member identifiers |
| `eligibility_issue` | ELIGIBILITY_ISSUE | Coverage dates, active status, plan problems |
| `provider_credential` | PROVIDER_CREDENTIAL | Network participation, NPI, credentials |
| `service_not_covered` | SERVICE_NOT_COVERED | Benefit coverage, authorization requirements |
| `prior_auth_required` | PRIOR_AUTH_REQUIRED | Missing or invalid authorization |
| `duplicate_claim` | DUPLICATE_CLAIM | Resubmission, corrected claim handling |
| `timely_filing` | TIMELY_FILING | Submission deadline violations |
| `coding_error` | CODING_ERROR | CPT/HCPCS/ICD-10 issues |
| `missing_information` | MISSING_INFORMATION | Required data elements, documentation |
| `general` | GENERAL | All other error types |

## PHI Redaction

### Automatic Detection

The module detects and redacts:

- **Personal Identifiers**: SSN, Member IDs, MRN, names, account numbers
- **Contact Info**: Email, phone, fax, addresses, ZIP codes
- **Dates**: Date of birth, dates in MM/DD/YYYY format
- **Financial**: Credit cards, bank accounts
- **Technical**: IP addresses, URLs with PHI

### Usage

```typescript
import { maskPHIFields, validateRedaction } from './redaction';

// Mask PHI
const payload = {
  memberId: "123-45-6789", // Will be masked
  errorCode: "TEST" // Won't be masked
};

const safe = maskPHIFields(payload);
// { memberId: "***REDACTED***", errorCode: "TEST" }

// Validate complete redaction
const validation = validateRedaction(safe);
if (!validation.isValid) {
  console.error('PHI detected:', validation.violations);
}
```

## Configuration

### Environment Variables

```bash
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key
AZURE_OPENAI_DEPLOYMENT=gpt-4
```

### Programmatic Configuration

```typescript
import { AIErrorResolutionConfig } from './edi277Resolution';

const config: AIErrorResolutionConfig = {
  endpoint: "https://your-resource.openai.azure.com/",
  apiKey: "your-api-key",
  deploymentName: "gpt-4-32k",
  maxTokens: 500,
  temperature: 0.3,
  rateLimitMs: 4000
};

const resolution = await resolveEdi277Claim(payload, false, config);
```

## Metrics

### Accessing Metrics

```typescript
import { getMetrics, resetMetrics } from './edi277Resolution';

const metrics = getMetrics();
console.log(`Total: ${metrics.totalRequests}`);
console.log(`Success Rate: ${(metrics.successfulRequests / metrics.totalRequests * 100).toFixed(2)}%`);
console.log(`Avg Time: ${metrics.averageProcessingTimeMs.toFixed(2)}ms`);
console.log(`Avg Tokens: ${metrics.averageTokenCount}`);

// Reset (e.g., daily)
resetMetrics();
```

### Tracked Metrics

- `totalRequests` - Total resolution attempts
- `successfulRequests` - Successful resolutions
- `failedRequests` - Failed attempts
- `averageProcessingTimeMs` - Average processing time
- `averageTokenCount` - Average tokens per request
- `rateLimitHits` - Number of rate limit violations
- `mockModeRequests` - Requests in mock mode

## Testing

### Run Tests

```bash
# Run all AI module tests
npm test -- src/ai

# Run specific test file
npm test -- src/ai/__tests__/edi277Resolution.test.ts
npm test -- src/ai/__tests__/redaction.test.ts

# Run with coverage
npm test -- src/ai --coverage
```

### Test Coverage

- **edi277Resolution.ts**: 31 tests
  - Mock mode validation (7 tests)
  - Scenario categorization (9 tests)
  - PHI redaction (3 tests)
  - Rate limiting (2 tests)
  - Metrics tracking (3 tests)
  - Configuration (2 tests)
  - Response quality (2 tests)
  - Error scenarios (3 tests)

- **redaction.ts**: 30 tests
  - Pattern detection (8 tests)
  - Field name detection (4 tests)
  - Value masking (3 tests)
  - String redaction (5 tests)
  - Object masking (6 tests)
  - Safe payload creation (2 tests)
  - Validation (2 tests)

## Performance

### Throughput

- **Mock Mode**: Unlimited (no API calls)
- **Live Mode**: 15 requests/minute (4-second rate limit)
- **Processing Time**: 
  - Mock: 0-1ms
  - Live: 2,000-5,000ms (depends on OpenAI API)

### Token Usage

- **Average Input**: 80-120 tokens
- **Average Output**: 150-250 tokens
- **Total Average**: 200-350 tokens per request

### Cost (GPT-4)

- **Input**: $0.03 per 1K tokens
- **Output**: $0.06 per 1K tokens
- **Average Cost**: $0.015-$0.025 per resolution

## Best Practices

### 1. Always Test with Mock Mode First

```typescript
// Development
const result = await resolveEdi277Claim(payload, true);

// Production
const result = await resolveEdi277Claim(payload, false);
```

### 2. Validate PHI Redaction

```typescript
const safe = maskPHIFields(payload);
const validation = validateRedaction(safe);
if (!validation.isValid) {
  throw new Error('PHI detected');
}
```

### 3. Implement Caching

```typescript
const cache = new Map<string, ResolutionSuggestion>();

function getCacheKey(payload: EDI277Payload) {
  return `${payload.errorCode}:${payload.errorDesc}`;
}

async function resolveWithCache(payload: EDI277Payload) {
  const key = getCacheKey(payload);
  if (cache.has(key)) return cache.get(key);
  
  const resolution = await resolveEdi277Claim(payload, false);
  cache.set(key, resolution);
  return resolution;
}
```

### 4. Handle Rate Limits

```typescript
async function resolveWithRetry(payload: EDI277Payload, maxRetries = 3) {
  for (let i = 0; i < maxRetries; i++) {
    try {
      return await resolveEdi277Claim(payload, false);
    } catch (error) {
      if (error.message.includes('Rate limit')) {
        await new Promise(r => setTimeout(r, 5000));
        continue;
      }
      throw error;
    }
  }
}
```

### 5. Monitor Metrics

```typescript
setInterval(() => {
  const metrics = getMetrics();
  const failureRate = metrics.failedRequests / metrics.totalRequests;
  
  if (failureRate > 0.1) {
    sendAlert('High failure rate: ' + (failureRate * 100) + '%');
  }
}, 3600000); // Hourly
```

## Dependencies

### Production

- `openai` - ^6.9.1 - OpenAI JavaScript/TypeScript library
- `@azure/openai` - ^2.0.0 - Azure OpenAI companion library

### Development

- `@types/jest` - ^29.0.0 - TypeScript definitions for Jest
- `jest` - ^29.0.0 - Testing framework
- `ts-jest` - ^29.0.0 - TypeScript support for Jest

## Security

### PHI Protection

- ✅ All PHI automatically redacted before API calls
- ✅ Validation ensures no PHI in responses
- ✅ Comprehensive pattern matching
- ✅ Field name-based masking
- ✅ No PHI in logs or metrics

### API Security

- ✅ API keys stored in environment variables
- ✅ Rate limiting enforced
- ✅ Input validation on all payloads
- ✅ No secrets in error messages
- ✅ Secure transport (HTTPS only)

## Troubleshooting

### Configuration Missing

**Error**: "Azure OpenAI configuration missing"

**Solution**: Set environment variables:
```bash
export AZURE_OPENAI_ENDPOINT="https://..."
export AZURE_OPENAI_API_KEY="..."
export AZURE_OPENAI_DEPLOYMENT="gpt-4"
```

### Rate Limit Exceeded

**Error**: "Rate limit exceeded. Please wait Xms"

**Solution**: Wait or increase `rateLimitMs` in configuration.

### Low Quality Suggestions

**Issue**: Generic or non-actionable suggestions

**Solution**:
1. Verify error description is detailed
2. Check scenario categorization
3. Lower temperature (0.2-0.3)
4. Use GPT-4 (not GPT-3.5)

## Documentation

- **[Complete Guide](../../docs/AI-ERROR-RESOLUTION.md)** - Full documentation
- **[Quick Start](../../docs/AI-RESOLUTION-QUICKSTART.md)** - Get started in 5 minutes
- **[Configuration Example](../../config/ai-resolution-config.example.json)** - Sample config

## Support

- **GitHub Issues**: https://github.com/aurelianware/cloudhealthoffice/issues
- **Email**: support@aurelianware.com

## License

Apache License 2.0

---

**Version**: 1.0.0  
**Last Updated**: December 2024  
**Maintainer**: Aurelianware
