# X12 276/277 Claim Status Test Files

Test EDI files for 276 Claim Status Request and 277 Claim Status Response transactions.

## Files

### test-x12-276-claim-status-request.edi

**Transaction**: 276 - Health Care Claim Status Request  
**HIPAA Standard**: 005010X212  
**Direction**: Inbound (Clearinghouse → Payer)

**Content**:
- Sender: CLEARINGHOUSE
- Receiver: BCBSFLORIDA (Blue Cross Blue Shield of Florida)
- Subscriber: JOHN A SMITH, DOB 05/15/1980, Member ID MEM123456
- Provider: SAMPLE MEDICAL CENTER, NPI 1234567890
- Claim Inquiry:
  - Claim Number: CLM987654321
  - Service Date: 01/15/2026
  - Total Charge: $250.00
  - Trace Number: TRACE123456789

**Usage**:
```bash
# Parse 276 request
python containers/x12-276-parser/parse-276.py test-x12-276-claim-status-request.edi

# Test 276 ingestion workflow
kubectl create -f argo-workflows/x12-276-ingest.yaml
argo submit -n cloudhealthoffice --from workflowtemplate/x12-276-ingest
```

### test-x12-277-claim-status-response.edi

**Transaction**: 277 - Health Care Claim Status Response  
**HIPAA Standard**: 005010X212  
**Direction**: Outbound (Payer → Clearinghouse)

**Content**:
- Sender: BCBSFLORIDA (Blue Cross Blue Shield of Florida)
- Receiver: CLEARINGHOUSE
- Subscriber: JOHN A SMITH, DOB 05/15/1980, Member ID MEM123456
- Provider: SAMPLE MEDICAL CENTER, NPI 1234567890
- Claim Status:
  - Claim Number: CLM987654321
  - Service Date: 01/15/2026
  - Status Code: F1:1:22 (Finalized - Approved - Exact match not found)
  - Status Category: F1 (Finalized)
  - Entity Code: 22 (Payer)
  - Total Charge: $250.00
  - Approved Amount: $200.00
  - Adjudication Date: 01/16/2026
  - Trace Number: TRACE123456789

**Usage**:
```bash
# Generate 277 response (from claim status data)
python containers/x12-encoder/generate_277.py \
  --claim-number CLM987654321 \
  --status approved \
  --output test-277-output.edi

# Test 277 generation workflow
argo submit -n cloudhealthoffice --from workflowtemplate/x12-277-claim-status
```

## Status Codes Reference

### Status Category Codes (STC01-1)

- **A1** - Acknowledgement/Forwarded
- **A2** - Acknowledgement/Receipt
- **A3** - Acknowledgement/Returned as unprocessable claim
- **A4** - Acknowledgement/Not found
- **F1** - Finalized/Payment
- **F2** - Finalized/Denial
- **F3** - Finalized/Adjusted
- **P1** - Pended/In process
- **P2** - Pended/Suspended
- **P3** - Pended/Partial approval

### Status Codes (STC01-2)

- **1** - Processed - primary
- **2** - Processed - secondary
- **3** - Processed - tertiary
- **4** - Denied
- **16** - More information needed
- **20** - Additional information requested
- **22** - Exact match not found

### Entity Identifier Codes (STC01-3)

- **20** - Information Source (Payer)
- **21** - Information Receiver (Provider/Submitter)
- **22** - Payer
- **41** - Submitter
- **AO** - Provider

## Workflow Integration

### 276 Ingest → 277 Response Flow

```
1. Provider/Clearinghouse uploads 276 to SFTP: /inbound/276/
2. Argo CronWorkflow triggers x12-276-ingest every 15 minutes
3. 276 parsed → claims queried → 277 generated
4. 277 uploaded to SFTP: /outbound/277/
5. Both archived to blob storage: raw/276/, processed/277/
6. Events published to Kafka: claim-status-requests, claim-status-responses
```

## Testing Scenarios

### Scenario 1: Approved Claim
- Request: 276 with claim CLM987654321
- Response: 277 with status F1:1:22 (Finalized/Approved)
- Expected: $200 approved out of $250 charged

### Scenario 2: Denied Claim
- Request: 276 with claim CLM999999999
- Response: 277 with status F2:4:22 (Finalized/Denied)
- Expected: Reason code with denial explanation

### Scenario 3: Pending Claim
- Request: 276 with claim CLM888888888
- Response: 277 with status P1:16:22 (Pended/More info needed)
- Expected: RFAI code indicating what's needed

### Scenario 4: Not Found
- Request: 276 with claim CLM000000000
- Response: 277 with status A4 (Acknowledgement/Not found)
- Expected: No matching claim in system
