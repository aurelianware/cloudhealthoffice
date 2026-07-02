# Local Claims Adjudication Quickstart

Run Cloud Health Office locally and take a claim all the way through adjudication, payment, and 835 ERA download — no Azure account needed.

## What this covers

1. Start MongoDB + Redis + core services with Docker Compose
2. Seed NCCI/MUE edits
3. Create a benefit plan
4. Submit a claim
5. Adjudicate it (NCCI check → fee schedule → cost sharing)
6. Run a payment batch and generate an 835 ERA
7. Download the raw X12 835 file

---

## Prerequisites

| Tool | Version |
|------|---------|
| Docker Desktop | 4.x+ |
| curl | any |
| (optional) [jq](https://jqlang.github.io/jq/) | for pretty JSON |

---

## 1. Start the stack

From the repo root:

```bash
docker compose --profile core --profile finance up -d
```

This starts:

| Service | Local URL |
|---------|-----------|
| MongoDB | `localhost:27017` |
| Redis | `localhost:6379` |
| claims-service | `http://localhost:5001` |
| benefit-plan-service | `http://localhost:5002` |
| payment-service | `http://localhost:5006` |

Wait for all services to be healthy (≈ 45 seconds):

```bash
curl -s http://localhost:5001/health
curl -s http://localhost:5002/health
curl -s http://localhost:5006/health
```

All should return `Healthy`.

---

## 2. Set your tenant ID

All requests require `X-Tenant-ID`. Use any string for local dev:

```bash
TENANT="demo"
```

---

## 3. Seed NCCI/MUE edits

The NCCI engine runs as step 0 of adjudication. Seed the built-in Q1 2025 baseline:

```bash
curl -s -X POST http://localhost:5002/api/v1/ncci/seed \
  -H "X-Tenant-ID: $TENANT" | jq .
```

Expected response includes `editCount` with the number of NCCI pairs seeded.

---

## 4. Create a benefit plan

Create a PPO plan with a $1,500 individual deductible, $30 copay for office visits, and 20% coinsurance for lab work:

```bash
PLAN=$(curl -s -X POST http://localhost:5002/api/benefitplans \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{
    "planId": "LOCAL-PPO-2025",
    "planName": "Local Dev PPO 2025",
    "payer": "Demo Health",
    "effectiveDate": "2025-01-01T00:00:00Z",
    "planType": "PPO",
    "lineOfBusiness": "Commercial",
    "costSharing": {
      "individualDeductible": 1500.00,
      "familyDeductible": 3000.00,
      "individualOutOfPocketMax": 5000.00,
      "familyOutOfPocketMax": 10000.00,
      "outOfNetworkDeductible": 3000.00,
      "outOfNetworkOutOfPocketMax": 10000.00
    },
    "benefits": [
      {
        "serviceCategory": "98",
        "description": "Professional Office Visit",
        "inNetworkCopay": 30.00,
        "deductibleApplies": false,
        "priorAuthRequired": false
      },
      {
        "serviceCategory": "73",
        "description": "Diagnostic Lab",
        "inNetworkCoinsurance": 0.20,
        "deductibleApplies": true,
        "priorAuthRequired": false
      }
    ]
  }')

echo $PLAN | jq .

# Extract the plan ID
PLAN_ID=$(echo $PLAN | jq -r '.id')
echo "Plan ID: $PLAN_ID"
```

---

## 5. Submit a claim

Submit an 837P-equivalent claim — an office visit (CPT 99213) at POS 11:

```bash
CLAIM=$(curl -s -X POST http://localhost:5001/api/claims \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{
    "claimNumber": "LOCAL-TEST-001",
    "memberId": "MBR001",
    "subscriberId": "SUB001",
    "providerNpi": "1234567890",
    "serviceDate": "2025-06-15T00:00:00Z",
    "diagnosisCodes": ["Z00.00"],
    "serviceLines": [
      {
        "procedureCode": "99213",
        "placeOfServiceCode": "11",
        "billedAmount": 175.00,
        "units": 1,
        "diagnosisCodes": ["Z00.00"]
      }
    ],
    "totalBilledAmount": 175.00,
    "status": "Submitted"
  }')

echo $CLAIM | jq .

CLAIM_ID=$(echo $CLAIM | jq -r '.id')
echo "Claim ID: $CLAIM_ID"
```

---

## 6. Adjudicate the claim

Call the combined adjudication endpoint. This runs in order:

1. **NCCI/MUE edit check** — CPT 99213 has no NCCI conflicts, passes
2. **Fee schedule lookup** — no local fee schedule seeded, falls back to billed charges ($175)
3. **Benefit calculation** — POS 11 resolves to service type "98" (Professional Visit); $30 copay applies, deductible not consumed

```bash
ADJ=$(curl -s -X POST http://localhost:5002/api/v1/adjudication/adjudicate \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d "{
    \"claimId\": \"$CLAIM_ID\",
    \"memberId\": \"MBR001\",
    \"subscriberId\": \"SUB001\",
    \"benefitPlanId\": \"$PLAN_ID\",
    \"serviceDate\": \"2025-06-15\",
    \"providerNpi\": \"1234567890\",
    \"networkTier\": \"InNetwork\",
    \"lines\": [
      {
        \"lineNumber\": 1,
        \"procedureCode\": \"99213\",
        \"placeOfService\": \"11\",
        \"billedAmount\": 175.00,
        \"units\": 1,
        \"diagnosisCodes\": [\"Z00.00\"]
      }
    ]
  }")

echo $ADJ | jq .
```

**Expected result:**

```json
{
  "claimId": "<uuid>",
  "success": true,
  "totals": {
    "billedAmount": 175.00,
    "allowedAmount": 175.00,
    "contractualAdjustment": 0.00,
    "deductibleAmount": 0.00,
    "copayAmount": 30.00,
    "coinsuranceAmount": 0.00,
    "memberResponsibility": 30.00,
    "planPayment": 145.00
  }
}
```

---

## 7. Update the claim with adjudication results

```bash
curl -s -X PUT "http://localhost:5001/api/claims/$CLAIM_ID/adjudication" \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{
    "status": "Adjudicated",
    "allowedAmount": 175.00,
    "memberLiability": 30.00,
    "planPayment": 145.00,
    "adjudicationDate": "2025-06-15T12:00:00Z"
  }' | jq .
```

---

## 8. Run a payment batch and generate an 835 ERA

Preflight check (recommended): make sure you have at least one approved claim.

```bash
curl -s "http://localhost:5001/api/claims/search?status=5&pageSize=5" \
  -H "X-Tenant-ID: $TENANT" | jq 'map({id, claimNumber, status})'
```

If the result is empty (`[]`), create and approve a quick test claim first:

```bash
CLAIM=$(curl -s -X POST http://localhost:5001/api/claims \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{
    "claimNumber": "LOCAL-PAY-001",
    "memberId": "MBR001",
    "subscriberId": "SUB001",
    "providerNpi": "1234567890",
    "serviceDate": "2025-06-15T00:00:00Z",
    "serviceLines": [
      {
        "procedureCode": "99213",
        "placeOfServiceCode": "11",
        "billedAmount": 175.00,
        "units": 1
      }
    ],
    "totalBilledAmount": 175.00,
    "status": "Submitted"
  }')

CLAIM_ID=$(echo "$CLAIM" | jq -r '.id')

curl -s -X PUT "http://localhost:5001/api/claims/$CLAIM_ID/status" \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{"status":5}' | jq '{id, status}'
```

Execute a payment run that picks up all adjudicated claims and generates 835 EDI for each:

```bash
PAYRUN=$(curl -s -X POST http://localhost:5006/api/paymentruns/execute \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d "{
    \"paymentRunNumber\": \"RUN-$(date +%Y%m%d)-001\",
    \"description\": \"Local dev payment run\",
    \"paymentMethod\": \"ACH\",
    \"paymentDate\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",
    \"criteria\": {
      \"includeClaimIds\": [\"$CLAIM_ID\"]
    }
  }")

echo $PAYRUN | jq '{id: .id, status: .status, totalClaims: .totalClaims, totalAmount: .totalPaymentAmount}'

PAYRUN_ID=$(echo $PAYRUN | jq -r '.id')
PAYMENT_ID=$(echo $PAYRUN | jq -r '.paymentIds[0]')
echo "Payment run: $PAYRUN_ID"
echo "Payment ID:  $PAYMENT_ID"
```

---

## 9. Download the 835 ERA

```bash
curl -s "http://localhost:5006/api/payments/$PAYMENT_ID/835" \
  -H "X-Tenant-ID: $TENANT" \
  -o era-$(date +%Y%m%d).835

echo "Saved ERA to era-$(date +%Y%m%d).835"
head -5 era-$(date +%Y%m%d).835
```

The file is a valid X12 005010X221A1 835 with ISA/GS envelope, BPR payment amount, CLP claim loop, SVC service lines, and CAS adjustment segments.

---

## 10. Validate payment features (beyond 835 download)

Use these checks to verify payment run persistence, payment lifecycle endpoints, and summary reporting.

### 10.1 Confirm payment run details

```bash
curl -s "http://localhost:5006/api/paymentruns/$PAYRUN_ID" \
  -H "X-Tenant-ID: $TENANT" | jq '{id, status, totalClaims, totalPaymentAmount, paymentIds, claimIds}'
```

Expected:
- `status` is `Completed`
- `totalClaims` is `1` for this walkthrough
- `paymentIds` includes the `PAYMENT_ID`

### 10.2 Confirm payment record

```bash
curl -s "http://localhost:5006/api/payments/$PAYMENT_ID" \
  -H "X-Tenant-ID: $TENANT" | jq '{id, checkNumber, status, totalPaymentAmount, claimPayments}'
```

Expected:
- `totalPaymentAmount` = `145.00`
- one `claimPayments` entry for your test claim

### 10.3 Search and summary endpoints

```bash
# Search payments in a date range
curl -s "http://localhost:5006/api/payments?paymentDateFrom=2025-06-01&paymentDateTo=2025-12-31&page=1&pageSize=50" \
  -H "X-Tenant-ID: $TENANT" | jq '.[0:3]'

# Payment summary
curl -s "http://localhost:5006/api/payments/summary?from=2025-06-01&to=2025-12-31" \
  -H "X-Tenant-ID: $TENANT" | jq .
```

Expected:
- at least one payment in search results
- summary totals reflect your payment amount and claim count

### 10.4 Test status transitions: post and reconcile

```bash
# Mark as posted
curl -s -X POST "http://localhost:5006/api/payments/$PAYMENT_ID/post" \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{"postedBy":"local-tester","notes":"Posted during quickstart"}' | jq '{id, status, postedAt, postedBy}'

# Mark as reconciled
curl -s -X POST "http://localhost:5006/api/payments/$PAYMENT_ID/reconcile" \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{"notes":"Bank reconciliation test"}' | jq '{id, status, reconciledAt}'
```

Expected:
- status changes to `Posted`, then `Reconciled`
- `postedAt` and `reconciledAt` are populated

### 10.5 Optional: direct payment ingestion test

If you want to test manual ERA ingestion without payment-run execution:

```bash
curl -s -X POST http://localhost:5006/api/payments \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{
    "checkNumber": "MANUAL-TEST-001",
    "paymentMethod": "ACH",
    "totalPaymentAmount": 25.00,
    "paymentDate": "2025-06-15T00:00:00Z",
    "payerName": "Demo Health",
    "payeeName": "Demo Clinic",
    "claimPayments": [
      {
        "claimId": "manual-claim-001",
        "patientControlNumber": "manual-claim-001",
        "claimStatusCode": "1",
        "chargeAmount": 25.00,
        "paymentAmount": 25.00,
        "patientResponsibilityAmount": 0.00
      }
    ]
  }' | jq '{id, checkNumber, totalPaymentAmount, status}'
```

---

## Payment quick troubleshooting

- `400 TenantId field is required`: ensure you're on latest code where tenant comes from `X-Tenant-ID`; if not, include `"tenantId":"$TENANT"` in payload as temporary fallback.
- `404` on `/api/payments/{id}/835`: verify `PAYMENT_ID` is non-empty and was created under the same tenant header.
- `500` during payment run execution: confirm claims-service has adjudicated claims and `PUT /api/claims/{id}/adjudication` completed first.
- Intermittent failures right after `docker compose --profile core --profile finance up -d`: wait for health checks to pass (`curl http://localhost:5006/health`) before running API calls.

---

## Automated script

Run the submit → adjudicate flow in one command:

```bash
./scripts/seed-local.sh --tenant demo
```

---

## Swagger UIs

Explore all endpoints interactively:

- **Claims service:** [http://localhost:5001/swagger](http://localhost:5001/swagger)
- **Benefit-plan / adjudication:** [http://localhost:5002/swagger](http://localhost:5002/swagger)
- **Payment / 835 ERA:** [http://localhost:5006/swagger](http://localhost:5006/swagger)

---

## Test NCCI edit failure

CPT 11040 (debridement) is column 1 to CPT 97597 (selective debridement). Submitting them together triggers an NCCI bundling edit and returns HTTP 422:

```bash
curl -s -X POST http://localhost:5002/api/v1/adjudication/adjudicate \
  -H "X-Tenant-ID: $TENANT" \
  -H "Content-Type: application/json" \
  -d "{
    \"claimId\": \"ncci-test-001\",
    \"memberId\": \"MBR001\",
    \"subscriberId\": \"SUB001\",
    \"benefitPlanId\": \"$PLAN_ID\",
    \"serviceDate\": \"2025-06-15\",
    \"providerNpi\": \"1234567890\",
    \"networkTier\": \"InNetwork\",
    \"lines\": [
      { \"lineNumber\": 1, \"procedureCode\": \"11040\", \"placeOfService\": \"11\", \"billedAmount\": 200.00, \"units\": 1 },
      { \"lineNumber\": 2, \"procedureCode\": \"97597\", \"placeOfService\": \"11\", \"billedAmount\": 150.00, \"units\": 1 }
    ]
  }" | jq '{status: .error, message: .message}'
```

---

## Stop the stack

```bash
docker compose down
```

Data persists in the `mongo_data` Docker volume. To wipe and start fresh:

```bash
docker compose down -v
```
