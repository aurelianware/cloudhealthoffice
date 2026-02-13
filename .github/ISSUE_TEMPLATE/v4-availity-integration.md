---
name: 'Availity Clearinghouse Integration'
about: Integrate with Availity for real-time eligibility and claims processing
title: '[v4.0] Availity Clearinghouse Integration - Beta MVP'
labels: 'integration, clearinghouse, priority:critical'
assignees: ''
---

## 🎯 Objective

Integrate Cloud Health Office with Availity clearinghouse for production-ready eligibility verification (270/271) and claims submission (837). This is the **minimum viable integration** for Beta launch with paying customers.

**Priority:** 🔴 **CRITICAL** (Beta blocker - can't process real transactions without this)  
**Effort:** 3-4 weeks (2 developers)  
**Depends On:** Azure Key Vault integration (#TBD)  
**Blocks:** Beta launch, revenue generation

---

## 📋 Success Criteria

- [ ] Availity sandbox account created and credentials stored in Key Vault
- [ ] SFTP connection to Availity established with retry logic
- [ ] 270 Eligibility requests sent to Availity, 271 responses parsed
- [ ] 837 Professional claims submitted to Availity
- [ ] Transaction metering enabled for Stripe billing
- [ ] <500ms p95 latency for eligibility checks (including clearinghouse round-trip)
- [ ] 100 test transactions processed successfully (50 eligibility, 50 claims)
- [ ] Error handling for all Availity error codes (AAA rejection codes)
- [ ] Monitoring and alerting for failed transactions

---

## 🏗️ Architecture Overview

```
┌─────────────────┐
│   Portal/API    │
│  (Member/       │
│   Provider)     │
└────────┬────────┘
         │ HTTP POST /api/v1/eligibility
         ▼
┌─────────────────────────┐
│  eligibility-service    │  ← New microservice or extend existing
│  (validates request)    │
└────────┬────────────────┘
         │ Publish to Kafka
         ▼
┌─────────────────────────┐
│   Argo Workflow         │
│  (270-eligibility.yaml) │
└────────┬────────────────┘
         │ Step 1: Build X12 270
         │ Step 2: SFTP upload to Availity
         │ Step 3: Poll for 271 response
         │ Step 4: Parse and store in Cosmos DB
         ▼
┌─────────────────────────┐
│   Availity SFTP         │
│  sftp.availity.com:22   │
│  /inbound/270/          │
│  /outbound/271/         │
└─────────────────────────┘
```

**Key Design Decisions:**
- Use existing Argo Workflow pattern (proven for 275/278)
- SFTP for eligibility (Availity doesn't offer real-time REST API for 270/271 in all regions)
- Async processing with Kafka for audit trail
- Cosmos DB for response caching (avoid duplicate clearinghouse calls)

---

## 🔧 Implementation Steps

### Phase 1: Availity Account Setup (Days 1-3)

**1.1 Create Availity Sandbox Account**

**Action Items:**
- [ ] Register at https://www.availity.com/essentials-providers
- [ ] Select "Payer Solutions" (not "Provider Solutions")
- [ ] Request sandbox access via https://availity.com/support
- [ ] Obtain credentials:
  - Submitter ID (e.g., `TEST123456`)
  - SFTP username (e.g., `cho_sandbox`)
  - SFTP password
  - Submitter NPI (for testing)

**Expected Timeline:** 3-5 business days for approval

**1.2 Store Credentials in Azure Key Vault**

```bash
# Availity sandbox credentials
az keyvault secret set \
  --vault-name "kv-cho-prod-eastus" \
  --name "Availity--Sandbox--SubmitterId" \
  --value "TEST123456"

az keyvault secret set \
  --vault-name "kv-cho-prod-eastus" \
  --name "Availity--Sandbox--SftpUsername" \
  --value "cho_sandbox"

az keyvault secret set \
  --vault-name "kv-cho-prod-eastus" \
  --name "Availity--Sandbox--SftpPassword" \
  --value "${AVAILITY_SFTP_PASSWORD}"

az keyvault secret set \
  --vault-name "kv-cho-prod-eastus" \
  --name "Availity--Sandbox--SftpHost" \
  --value "sftp-sandbox.availity.com"

# Submitter NPI for X12 headers
az keyvault secret set \
  --vault-name "kv-cho-prod-eastus" \
  --name "Availity--Sandbox--SubmitterNpi" \
  --value "1234567890"
```

**1.3 Test SFTP Connection**

```bash
# Test connectivity
sftp cho_sandbox@sftp-sandbox.availity.com

# Expected directory structure:
# /inbound/270/    <- Upload eligibility requests here
# /outbound/271/   <- Download responses here
# /inbound/837/    <- Upload claims here
# /outbound/999/   <- Download acknowledgements here
```

---

### Phase 2: X12 270 Eligibility Request Builder (Days 4-7)

**2.1 Create X12 270 Generator**

**New File:** `containers/x12-270-generator/generate-270.py`

```python
#!/usr/bin/env python3
"""
Generate X12 270 Eligibility Inquiry from JSON API request.
Follows 5010 X12 270 implementation guide.
"""
import json
import sys
from datetime import datetime

def generate_270(request_data):
    """Build X12 270 from API request."""
    
    # ISA - Interchange Control Header
    isa = f"ISA*00*          *00*          *30*{request_data['submitter_id']:<15}*30*{request_data['receiver_id']:<15}*{datetime.now():%y%m%d}*{datetime.now():%H%M}*^*00501*{request_data['control_number']:09d}*0*P*:~"
    
    # GS - Functional Group Header
    gs = f"GS*HS*{request_data['submitter_id']}*{request_data['receiver_id']}*{datetime.now():%Y%m%d}*{datetime.now():%H%M}*{request_data['group_control_number']}*X*005010X279A1~"
    
    # ST - Transaction Set Header
    st = f"ST*270*{request_data['transaction_control_number']:04d}*005010X279A1~"
    
    # BHT - Beginning of Hierarchical Transaction
    bht = f"BHT*0022*13*{request_data['reference_id']}*{datetime.now():%Y%m%d}*{datetime.now():%H%M%S}~"
    
    # HL - Information Source (Payer)
    hl_payer = "HL*1**20*1~"
    nm1_payer = f"NM1*PR*2*{request_data['payer_name']}*****PI*{request_data['payer_id']}~"
    
    # HL - Information Receiver (Provider)
    hl_provider = "HL*2*1*21*1~"
    nm1_provider = f"NM1*1P*2*{request_data['provider_name']}*****XX*{request_data['provider_npi']}~"
    
    # HL - Subscriber
    hl_subscriber = "HL*3*2*22*0~"
    trn = f"TRN*1*{request_data['trace_number']}*{request_data['submitter_id']}~"
    nm1_subscriber = f"NM1*IL*1*{request_data['subscriber_last_name']}*{request_data['subscriber_first_name']}****MI*{request_data['member_id']}~"
    dmg = f"DMG*D8*{request_data['date_of_birth']}*{request_data.get('gender', 'U')}~"
    dtp = f"DTP*291*D8*{datetime.now():%Y%m%d}~"
    
    # EQ - Eligibility/Benefit Inquiry (30 = All available)
    eq = "EQ*30~"
    
    # SE - Transaction Set Trailer
    segment_count = 13  # Count all segments between ST and SE
    se = f"SE*{segment_count}*{request_data['transaction_control_number']:04d}~"
    
    # GE - Functional Group Trailer
    ge = f"GE*1*{request_data['group_control_number']}~"
    
    # IEA - Interchange Control Trailer
    iea = f"IEA*1*{request_data['control_number']:09d}~"
    
    # Combine all segments
    x12_270 = "\n".join([
        isa, gs, st, bht,
        hl_payer, nm1_payer,
        hl_provider, nm1_provider,
        hl_subscriber, trn, nm1_subscriber, dmg, dtp, eq,
        se, ge, iea
    ])
    
    return x12_270

if __name__ == "__main__":
    # Read JSON from stdin
    request_data = json.load(sys.stdin)
    
    # Generate X12 270
    x12_output = generate_270(request_data)
    
    # Write to stdout
    print(x12_output)
```

**2.2 Containerize X12 270 Generator**

**New File:** `containers/x12-270-generator/Dockerfile`

```dockerfile
FROM python:3.11-slim

WORKDIR /app

COPY generate-270.py /app/
RUN chmod +x /app/generate-270.py

# No dependencies needed for basic X12 generation
ENTRYPOINT ["python3", "/app/generate-270.py"]
```

**Build and Push:**
```bash
docker build -t ghcr.io/aurelianware/cloudhealthoffice-x12-270-generator:latest containers/x12-270-generator/
docker push ghcr.io/aurelianware/cloudhealthoffice-x12-270-generator:latest
```

---

### Phase 3: Argo Workflow for Availity (Days 8-12)

**3.1 Create Argo Workflow for 270 Submission**

**New File:** `argo-workflows/270-eligibility-availity.yaml`

```yaml
apiVersion: argoproj.io/v1alpha1
kind: WorkflowTemplate
metadata:
  name: x12-270-eligibility-availity
  namespace: cho-svcs
spec:
  entrypoint: process-270-eligibility
  
  templates:
  - name: process-270-eligibility
    inputs:
      parameters:
      - name: tenant-id
      - name: request-json  # JSON payload from API
    
    dag:
      tasks:
      # Step 1: Generate X12 270 from JSON request
      - name: generate-x12-270
        template: x12-270-generator
        arguments:
          parameters:
          - name: request-json
            value: "{{inputs.parameters.request-json}}"
      
      # Step 2: Upload to Availity SFTP
      - name: upload-to-availity
        template: sftp-upload
        dependencies: [generate-x12-270]
        arguments:
          parameters:
          - name: x12-content
            value: "{{tasks.generate-x12-270.outputs.result}}"
          - name: remote-path
            value: "/inbound/270/{{inputs.parameters.tenant-id}}_270_{{workflow.creationTimestamp}}.x12"
      
      # Step 3: Poll for 271 response (with retry)
      - name: poll-for-response
        template: sftp-poll
        dependencies: [upload-to-availity]
        arguments:
          parameters:
          - name: remote-path
            value: "/outbound/271/"
          - name: filename-pattern
            value: "{{inputs.parameters.tenant-id}}_271_*.x12"
      
      # Step 4: Parse 271 response
      - name: parse-271-response
        template: x12-271-parser
        dependencies: [poll-for-response]
        arguments:
          parameters:
          - name: x12-content
            value: "{{tasks.poll-for-response.outputs.result}}"
      
      # Step 5: Store in Cosmos DB
      - name: store-response
        template: cosmos-db-upsert
        dependencies: [parse-271-response]
        arguments:
          parameters:
          - name: tenant-id
            value: "{{inputs.parameters.tenant-id}}"
          - name: response-json
            value: "{{tasks.parse-271-response.outputs.result}}"

  # Template: X12 270 Generator
  - name: x12-270-generator
    inputs:
      parameters:
      - name: request-json
    container:
      image: ghcr.io/aurelianware/cloudhealthoffice-x12-270-generator:latest
      command: ["python3", "/app/generate-270.py"]
      stdin: "{{inputs.parameters.request-json}}"
  
  # Template: SFTP Upload
  - name: sftp-upload
    inputs:
      parameters:
      - name: x12-content
      - name: remote-path
    script:
      image: alpine:latest
      command: [sh]
      source: |
        apk add --no-cache openssh-client
        
        # Get credentials from Key Vault (via env vars injected by pod identity)
        SFTP_HOST="${AVAILITY_SFTP_HOST}"
        SFTP_USER="${AVAILITY_SFTP_USERNAME}"
        SFTP_PASS="${AVAILITY_SFTP_PASSWORD}"
        
        # Upload file
        echo "{{inputs.parameters.x12-content}}" > /tmp/270.x12
        sshpass -p "$SFTP_PASS" sftp -o StrictHostKeyChecking=no "$SFTP_USER@$SFTP_HOST" <<EOF
        put /tmp/270.x12 {{inputs.parameters.remote-path}}
        bye
        EOF
  
  # Template: SFTP Poll (wait for response file)
  - name: sftp-poll
    inputs:
      parameters:
      - name: remote-path
      - name: filename-pattern
    retryStrategy:
      limit: 60  # Retry for up to 5 minutes (5s * 60)
      backoff:
        duration: "5s"
        factor: 1
    script:
      image: alpine:latest
      command: [sh]
      source: |
        apk add --no-cache openssh-client
        
        SFTP_HOST="${AVAILITY_SFTP_HOST}"
        SFTP_USER="${AVAILITY_SFTP_USERNAME}"
        SFTP_PASS="${AVAILITY_SFTP_PASSWORD}"
        
        # List files matching pattern
        sshpass -p "$SFTP_PASS" sftp -o StrictHostKeyChecking=no "$SFTP_USER@$SFTP_HOST" <<EOF > /tmp/file_list.txt
        ls {{inputs.parameters.remote-path}}{{inputs.parameters.filename-pattern}}
        bye
        EOF
        
        # Check if file exists
        if grep -q "271" /tmp/file_list.txt; then
          # Download first matching file
          FILENAME=$(grep "271" /tmp/file_list.txt | head -1)
          sshpass -p "$SFTP_PASS" sftp -o StrictHostKeyChecking=no "$SFTP_USER@$SFTP_HOST" <<EOF
          get {{inputs.parameters.remote-path}}$FILENAME /tmp/271_response.x12
          bye
          EOF
          cat /tmp/271_response.x12
        else
          echo "Response file not found yet, will retry..."
          exit 1  # Trigger retry
        fi
  
  # Template: X12 271 Parser (reuse existing container)
  - name: x12-271-parser
    inputs:
      parameters:
      - name: x12-content
    container:
      image: ghcr.io/aurelianware/cloudhealthoffice-x12-parser:latest
      command: ["python3", "/app/parse-271.py"]
      stdin: "{{inputs.parameters.x12-content}}"
  
  # Template: Cosmos DB Upsert
  - name: cosmos-db-upsert
    inputs:
      parameters:
      - name: tenant-id
      - name: response-json
    script:
      image: mcr.microsoft.com/azure-cli:latest
      command: [sh]
      source: |
        # Use Azure CLI to insert into Cosmos DB
        # (In production, use proper SDK container)
        echo "{{inputs.parameters.response-json}}" > /tmp/response.json
        
        az cosmosdb sql container item create \
          --account-name "cosmos-cho-prod" \
          --database-name "CloudHealthOffice" \
          --container-name "EligibilityResponses" \
          --partition-key-value "{{inputs.parameters.tenant-id}}" \
          --body @/tmp/response.json
```

---

### Phase 4: API Endpoint & Testing (Days 13-18)

**4.1 Create Eligibility API Endpoint**

**Update:** `services/eligibility-service/Controllers/EligibilityController.cs`

```csharp
[ApiController]
[Route("api/v1/eligibility")]
public class EligibilityController : ControllerBase
{
    private readonly IArgoWorkflowService _argoWorkflowService;
    private readonly ITenantContextService _tenantContextService;
    
    [HttpPost("check")]
    [ProducesResponseType(typeof(EligibilityResponse), 200)]
    [ProducesResponseType(typeof(ErrorResponse), 400)]
    public async Task<IActionResult> CheckEligibility([FromBody] EligibilityRequest request)
    {
        var tenantId = await _tenantContextService.GetTenantIdAsync();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest(new ErrorResponse { Message = "Missing tenant context" });
        }
        
        // Build JSON payload for Argo Workflow
        var workflowInput = new
        {
            tenant_id = tenantId,
            request_json = JsonSerializer.Serialize(new
            {
                submitter_id = "TEST123456",  // From Key Vault
                receiver_id = "AVAILITY",
                payer_id = request.PayerId,
                payer_name = request.PayerName,
                provider_npi = request.ProviderNpi,
                provider_name = request.ProviderName,
                subscriber_last_name = request.SubscriberLastName,
                subscriber_first_name = request.SubscriberFirstName,
                member_id = request.MemberId,
                date_of_birth = request.DateOfBirth.ToString("yyyyMMdd"),
                gender = request.Gender,
                control_number = GenerateControlNumber(),
                group_control_number = GenerateGroupControlNumber(),
                transaction_control_number = GenerateTransactionControlNumber(),
                reference_id = Guid.NewGuid().ToString(),
                trace_number = $"CHO{DateTime.UtcNow:yyyyMMddHHmmss}"
            })
        };
        
        // Submit Argo Workflow
        var workflowName = await _argoWorkflowService.SubmitWorkflowAsync(
            "x12-270-eligibility-availity",
            workflowInput
        );
        
        // Return immediate response (async processing)
        return Accepted(new
        {
            workflowName = workflowName,
            status = "processing",
            statusUrl = $"/api/v1/eligibility/status/{workflowName}"
        });
    }
    
    [HttpGet("status/{workflowName}")]
    public async Task<IActionResult> GetStatus(string workflowName)
    {
        var status = await _argoWorkflowService.GetWorkflowStatusAsync(workflowName);
        
        if (status.Phase == "Succeeded")
        {
            // Fetch parsed 271 response from Cosmos DB
            var response = await _cosmosDbService.GetEligibilityResponseAsync(workflowName);
            return Ok(response);
        }
        else if (status.Phase == "Failed")
        {
            return StatusCode(500, new ErrorResponse { Message = "Eligibility check failed", Details = status.Message });
        }
        else
        {
            return Ok(new { status = status.Phase, message = "Processing..." });
        }
    }
}
```

**4.2 Integration Testing**

```bash
# Test 270 generation
curl -X POST https://api-staging.cloudhealthoffice.com/api/v1/eligibility/check \
  -H "X-Tenant-ID: tenant-123" \
  -H "Content-Type: application/json" \
  -d '{
    "payerId": "BCBSFL",
    "payerName": "Blue Cross Blue Shield of Florida",
    "providerNpi": "1234567890",
    "providerName": "Test Medical Group",
    "subscriberLastName": "Doe",
    "subscriberFirstName": "John",
    "memberId": "ABC123456789",
    "dateOfBirth": "1980-05-15",
    "gender": "M"
  }'

# Expected response:
# {
#   "workflowName": "x12-270-eligibility-availity-abc123",
#   "status": "processing",
#   "statusUrl": "/api/v1/eligibility/status/x12-270-eligibility-availity-abc123"
# }

# Poll for completion (after ~30 seconds)
curl https://api-staging.cloudhealthoffice.com/api/v1/eligibility/status/x12-270-eligibility-availity-abc123
```

**4.3 Load Testing (100 Transactions)**

```bash
# Use k6 for load testing
k6 run scripts/load-test-270-eligibility.js

# Expected:
# - 100 requests submitted
# - <500ms p95 latency for API response (not including clearinghouse round-trip)
# - 0 errors
# - All workflows complete within 2 minutes
```

---

### Phase 5: Production Deployment & Monitoring (Days 19-21)

**5.1 Configure Stripe Metering**

```csharp
// In Argo Workflow completion handler
public async Task OnWorkflowCompleted(string workflowName, string tenantId)
{
    // Record transaction usage for Stripe billing
    await _stripeService.RecordUsageAsync(
        tenantId,
        "transaction-meter",
        quantity: 1,  // 1 eligibility check
        timestamp: DateTime.UtcNow
    );
}
```

**5.2 Monitoring & Alerting**

**Application Insights Queries:**
```kusto
// Failed Availity transactions (last 24 hours)
traces
| where timestamp > ago(24h)
| where message contains "Availity" and severityLevel >= 3
| summarize count() by bin(timestamp, 1h), message

// Eligibility check latency (p95)
dependencies
| where timestamp > ago(1h)
| where name contains "270"
| summarize percentile(duration, 95) by bin(timestamp, 5m)
```

**Alerts:**
- Availity SFTP connection failure (> 5 failures in 10 minutes)
- 271 polling timeout (> 10% of workflows timeout)
- Eligibility check p95 latency > 2 seconds (including clearinghouse)

**5.3 Production Deployment**

```bash
# Deploy to production namespace
kubectl apply -f argo-workflows/270-eligibility-availity.yaml -n cho-svcs

# Verify workflow template exists
argo template list -n cho-svcs | grep 270-eligibility

# Submit test workflow manually
argo submit -n cho-svcs \
  --from workflowtemplate/x12-270-eligibility-availity \
  -p tenant-id=tenant-123 \
  -p request-json='{"subscriber_last_name":"Test","subscriber_first_name":"Patient",...}'
```

---

## 📚 Availity API Documentation

- **Developer Portal:** https://developer.availity.com/
- **270/271 Implementation Guide:** https://www.availity.com/documents/270-271-companion-guide.pdf
- **SFTP Connection Guide:** https://availity.com/support/sftp-setup
- **Error Codes:** https://availity.com/resources/error-codes

---

## 🚨 Error Handling

**Availity-Specific Error Codes (AAA segment in 271):**
- `AAA*N*42` - Subscriber not found
- `AAA*N*56` - Coverage not in effect on service date
- `AAA*N*58` - Invalid member ID format
- `AAA*N*71` - Patient birth date mismatch
- `AAA*N*T4` - Payer ID not recognized

**Retry Strategy:**
- SFTP connection errors: Exponential backoff (5s, 10s, 20s, 40s)
- 271 polling timeout: Fail after 5 minutes (Availity SLA is 30 seconds)
- Invalid request (4xx): Don't retry, return error to caller
- Clearinghouse outage (5xx): Retry up to 3 times with 1-minute intervals

---

## ✅ Definition of Done

- [ ] Availity sandbox account provisioned
- [ ] SFTP connection tested and credentials in Key Vault
- [ ] X12 270 generator containerized and tested
- [ ] Argo Workflow for 270 → 271 deployed
- [ ] API endpoint `/api/v1/eligibility/check` functional
- [ ] 100 test transactions processed successfully (95%+ success rate)
- [ ] Transaction metering sending data to Stripe
- [ ] Monitoring dashboards created in Application Insights
- [ ] Alerts configured for failures
- [ ] Documentation updated (API docs, runbooks)

---

## 📅 Timeline

| Days | Task | Owner | Status |
|------|------|-------|--------|
| 1-3 | Availity account setup | DevOps | ⬜ Not Started |
| 4-7 | Build X12 270 generator | Backend Dev | ⬜ Not Started |
| 8-12 | Create Argo Workflow | Backend Dev | ⬜ Not Started |
| 13-18 | API endpoint + testing | Backend Dev | ⬜ Not Started |
| 19-21 | Production deployment + monitoring | DevOps | ⬜ Not Started |

**Target Completion:** 3 weeks from start  
**Beta Launch:** Week 4 (with Availity integration live)
