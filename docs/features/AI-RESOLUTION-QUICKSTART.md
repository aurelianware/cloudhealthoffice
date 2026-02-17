# AI Error Resolution - Quick Start Guide

Get up and running with AI-driven EDI 277 error resolution in under 5 minutes.

## Prerequisites

- Azure OpenAI resource with GPT-4 deployment
- Node.js 18+ and npm
- Cloud Health Office repository cloned

## Step 1: Install Dependencies

```bash
npm install
```

This will install:
- `openai` - OpenAI JavaScript/TypeScript library
- `@azure/openai` - Azure OpenAI companion library

## Step 2: Configure Environment

Create a `.env` file in the project root:

```bash
# Azure OpenAI Configuration
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key-here
AZURE_OPENAI_DEPLOYMENT=gpt-4

# Optional: Application Insights
APPLICATIONINSIGHTS_CONNECTION_STRING=your-connection-string
```

**Security Note**: Never commit `.env` files to version control!

## Step 3: Test with Mock Mode

Create a test file `test-ai-resolution.ts`:

```typescript
import { resolveEdi277Claim, EDI277Payload } from './src/ai/edi277Resolution';

async function test() {
  const rejectedClaim: EDI277Payload = {
    transactionId: "TEST001",
    payer: "TestPayer",
    memberId: "M123456",
    errorCode: "ID001",
    errorDesc: "Invalid member ID format",
    statusCategory: "Rejected"
  };

  // Test with mock mode (no API call)
  console.log("Testing Mock Mode...");
  const mockResult = await resolveEdi277Claim(rejectedClaim, true);
  
  console.log(`\nScenario: ${mockResult.scenario}`);
  console.log(`Confidence: ${mockResult.confidence}`);
  console.log(`Processing Time: ${mockResult.processingTimeMs}ms`);
  console.log("\nSuggestions:");
  mockResult.suggestions.forEach((s, i) => {
    console.log(`${i + 1}. ${s}`);
  });
}

test().catch(console.error);
```

Run the test:

```bash
npx ts-node test-ai-resolution.ts
```

Expected output:

```
Testing Mock Mode...

Scenario: member_id_invalid
Confidence: 0.85
Processing Time: 0ms

Suggestions:
1. Verify member ID format matches payer requirements (e.g., 9 digits vs alphanumeric)
2. Check if using subscriber ID instead of dependent ID or vice versa
3. Confirm member is active on service date through real-time eligibility
4. Validate SSN-based vs member number-based identification
5. Contact payer for correct member identifier format
```

## Step 4: Test with Live API

Update your test file to use the live API:

```typescript
// Change mockMode to false
const liveResult = await resolveEdi277Claim(rejectedClaim, false);
```

Run again:

```bash
npx ts-node test-ai-resolution.ts
```

This will call Azure OpenAI GPT-4 and return AI-generated suggestions.

## Step 5: Verify PHI Redaction

Test PHI redaction:

```typescript
import { maskPHIFields, validateRedaction } from './src/ai/redaction';

const payload = {
  transactionId: "TEST002",
  memberId: "123-45-6789", // SSN format - will be redacted
  patientName: "John Doe", // Will be redacted
  errorCode: "TEST",
  errorDesc: "Test error"
};

// Mask PHI
const safe = maskPHIFields(payload);
console.log("Masked:", safe);
// Output: { transactionId: "TEST002", memberId: "***REDACTED***", patientName: "***REDACTED***", errorCode: "TEST", errorDesc: "Test error" }

// Validate redaction
const validation = validateRedaction(safe);
console.log("Valid:", validation.isValid); // true
console.log("Violations:", validation.violations); // []
```

## Step 6: Check Metrics

Monitor performance:

```typescript
import { getMetrics } from './src/ai/edi277Resolution';

const metrics = getMetrics();
console.log("Total Requests:", metrics.totalRequests);
console.log("Success Rate:", 
  `${(metrics.successfulRequests / metrics.totalRequests * 100).toFixed(2)}%`);
console.log("Avg Processing Time:", `${metrics.averageProcessingTimeMs.toFixed(2)}ms`);
console.log("Avg Tokens:", metrics.averageTokenCount.toFixed(0));
```

## Step 7: Run Test Suite

Verify everything works:

```bash
npm test -- src/ai
```

Expected output:

```
PASS src/ai/__tests__/edi277Resolution.test.ts
PASS src/ai/__tests__/redaction.test.ts

Test Suites: 2 passed, 2 total
Tests:       61 passed, 61 total
```

## Common Scenarios

### Scenario 1: Member ID Invalid

```typescript
const payload = {
  transactionId: "TRX001",
  payer: "HealthPlan",
  memberId: "M123456",
  errorCode: "ID001",
  errorDesc: "Member ID not found in system",
  statusCategory: "Rejected"
};

const resolution = await resolveEdi277Claim(payload, false);
// Returns member ID-specific suggestions
```

### Scenario 2: Prior Authorization Required

```typescript
const payload = {
  transactionId: "TRX002",
  payer: "HealthPlan",
  memberId: "M789012",
  errorCode: "PA001",
  errorDesc: "Prior authorization required for this service",
  statusCategory: "Denied"
};

const resolution = await resolveEdi277Claim(payload, false);
// Returns prior auth-specific suggestions
```

### Scenario 3: Timely Filing

```typescript
const payload = {
  transactionId: "TRX003",
  payer: "HealthPlan",
  memberId: "M345678",
  errorCode: "TF001",
  errorDesc: "Claim submitted beyond timely filing deadline",
  statusCategory: "Rejected",
  serviceDate: "2023-01-15"
};

const resolution = await resolveEdi277Claim(payload, false);
// Returns timely filing-specific suggestions
```

## Integration Examples

### Azure Function

```typescript
import { AzureFunction, Context, HttpRequest } from "@azure/functions";
import { resolveEdi277Claim } from "../src/ai/edi277Resolution";

const httpTrigger: AzureFunction = async function (context: Context, req: HttpRequest) {
  try {
    const resolution = await resolveEdi277Claim(req.body, false);
    context.res = {
      status: 200,
      body: resolution
    };
  } catch (error) {
    context.res = {
      status: 500,
      body: { error: error.message }
    };
  }
};

export default httpTrigger;
```

### Express API

```typescript
import express from 'express';
import { resolveEdi277Claim } from './src/ai/edi277Resolution';

const app = express();
app.use(express.json());

app.post('/api/resolve', async (req, res) => {
  try {
    const resolution = await resolveEdi277Claim(req.body, false);
    res.json(resolution);
  } catch (error) {
    res.status(500).json({ error: error.message });
  }
});

app.listen(3000);
```

### Logic App Action

Add to your Logic App workflow:

```json
{
  "AI_Resolution": {
    "type": "Function",
    "inputs": {
      "body": {
        "transactionId": "@{triggerBody()?['transactionId']}",
        "errorCode": "@{triggerBody()?['errorCode']}",
        "errorDesc": "@{triggerBody()?['errorDesc']}",
        "memberId": "@{triggerBody()?['memberId']}"
      },
      "function": {
        "id": "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Web/sites/{func}/functions/AIResolution"
      }
    }
  }
}
```

## Troubleshooting

### Error: "Module not found"

```bash
npm install
npm run build
```

### Error: "Azure OpenAI configuration missing"

Check your `.env` file and ensure all required variables are set:

```bash
echo $AZURE_OPENAI_ENDPOINT
echo $AZURE_OPENAI_DEPLOYMENT
```

### Error: "Rate limit exceeded"

Wait 4 seconds between requests or increase the rate limit:

```typescript
const config = {
  rateLimitMs: 5000 // 5 seconds
};
await resolveEdi277Claim(payload, false, config);
```

### Low Quality Suggestions

1. Check error description is detailed enough
2. Verify correct scenario categorization
3. Try lowering temperature (0.2-0.3)
4. Ensure GPT-4 deployment (not GPT-3.5)

## Next Steps

1. **Review Documentation**: See [AI-ERROR-RESOLUTION.md](./AI-ERROR-RESOLUTION.md) for complete guide
2. **Explore Scenarios**: Test all 10 error scenarios
3. **Configure Monitoring**: Set up Application Insights tracking
4. **Implement Caching**: Add resolution caching for identical errors
5. **Production Deploy**: Deploy to Azure Functions or Logic Apps

## Best Practices

✅ **DO**:
- Always test with mock mode first
- Validate PHI redaction before API calls
- Monitor metrics and set up alerts
- Cache resolutions for identical errors
- Use environment variables for secrets

❌ **DON'T**:
- Commit API keys to version control
- Log unredacted PHI
- Skip PHI validation
- Exceed rate limits
- Use in production without monitoring

## Getting Help

- **Documentation**: `/docs/AI-ERROR-RESOLUTION.md`
- **GitHub Issues**: https://github.com/aurelianware/cloudhealthoffice/issues
- **Email**: support@aurelianware.com

---

**Ready to scale?** Deploy to production and process thousands of rejected claims with AI-powered intelligence!
