# Local Claims Adjudication Quickstart

Run Cloud Health Office locally and take a claim all the way through adjudication, payment, and 835 ERA download — no Azure account needed.

## What this covers

1. Start MongoDB + Redis + core services with Docker Compose
2. Seed NCCI/MUE edits
3. Create a benefit plan
4. Submit a claim
5. Watch the adjudication pipeline adjudicate it (NCCI check → fee schedule → cost sharing)
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

## 2. Get a local token

Every service takes the tenant only from a validated bearer token, so each
request below carries one. Mint a development token for a tenant (any string
works locally):

```bash
TENANT="demo"
TOKEN=$(./scripts/dev-token.sh --tenant "$TENANT")
```

The token is a TenantAdmin token for that tenant, valid for 12 hours, signed
with the public development key the services trust only when
`ASPNETCORE_ENVIRONMENT` is `Development` (which the compose file sets). It
does not authenticate against any deployed environment. A request without it
gets `401`.

---

## 3. Seed NCCI/MUE edits

The NCCI engine runs as step 0 of adjudication. Seed the built-in Q1 2025 baseline:

```bash
curl -s -X POST http://localhost:5002/api/v1/ncci/seed \
  -H "Authorization: Bearer $TOKEN" | jq .
```

Expected response includes `editCount` with the number of NCCI pairs seeded.

---

## 4. Create a benefit plan

Create a PPO plan with a $1,500 individual deductible, $30 copay for office visits, and 20% coinsurance for lab work:

```bash
PLAN=$(curl -s -X POST http://localhost:5002/api/benefitplans \
  -H "Authorization: Bearer $TOKEN" \
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

Submit an 837P-equivalent claim — an office visit (CPT 99213) at POS 11.
Submitting is all you do: the claims-service adjudication pipeline
(consumer → `ClaimAdjudicationOrchestrator` → `PersistenceStage`) picks the
claim up and is the only thing that sets its adjudicated outcome.

```bash
submit_claim() {   # submit_claim <claimNumber> <claimLines JSON> <totalCharge>
  curl -s -X POST http://localhost:5001/api/claims \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" \
    -d "{
      \"claimNumber\": \"$1\",
      \"memberId\": \"MBR001\",
      \"subscriberId\": \"SUB001\",
      \"benefitPlanId\": \"$PLAN_ID\",
      \"billingProviderNPI\": \"1234567890\",
      \"placeOfServiceCode\": \"11\",
      \"serviceDateFrom\": \"2025-06-15T00:00:00Z\",
      \"serviceDateTo\": \"2025-06-15T00:00:00Z\",
      \"diagnosisCodes\": [{ \"code\": \"Z00.00\", \"pointerNumber\": 1 }],
      \"claimLines\": $2,
      \"totalChargeAmount\": $3
    }"
}

CLAIM=$(submit_claim LOCAL-TEST-001 '[
  { "lineNumber": 1, "procedureCode": "99213", "placeOfServiceCode": "11", "chargeAmount": 175.00,
    "units": 1, "diagnosisPointers": [1],
    "serviceDateFrom": "2025-06-15T00:00:00Z", "serviceDateTo": "2025-06-15T00:00:00Z" }
]' 175.00)

echo $CLAIM | jq .

CLAIM_ID=$(echo $CLAIM | jq -r '.id')
echo "Claim ID: $CLAIM_ID"
```

---

## 6. Wait for the adjudication outcome

Poll the claim until the pipeline has persisted a terminal status —
`Pended` (4), `Approved` (5), `Denied` (6), `Paid` (7), `Voided` (8) or
`PartiallyPaid` (9). The pipeline runs, in order:

1. **NCCI/MUE edit check** — CPT 99213 has no NCCI conflicts, passes
2. **Fee schedule lookup** — no local fee schedule seeded, falls back to billed charges ($175)
3. **Benefit calculation** — POS 11 resolves to service type "98" (Professional Visit); $30 copay applies, deductible not consumed

```bash
wait_for_outcome() {   # wait_for_outcome <claimId>
  for i in $(seq 1 60); do
    STATUS=$(curl -s "http://localhost:5001/api/claims/$1" -H "Authorization: Bearer $TOKEN" | jq -r '.status // empty')
    case "$STATUS" in
      4|5|6|7|8|9|Pended|Approved|Denied|Paid|Voided|PartiallyPaid) break ;;
    esac
    sleep 1
  done
  curl -s "http://localhost:5001/api/claims/$1" -H "Authorization: Bearer $TOKEN" \
    | jq '{id, status, pendCode: .pendDetails.pendCode, adjudicationResult: (.adjudicationResult | {allowedAmount, patientResponsibility, payerPayment, denialReasonCode})}'
}

wait_for_outcome "$CLAIM_ID"
```

**Expected result:**

```json
{
  "id": "<uuid>",
  "status": 5,
  "pendCode": null,
  "adjudicationResult": {
    "allowedAmount": 175.00,
    "patientResponsibility": 30.00,
    "payerPayment": 145.00,
    "denialReasonCode": null
  }
}
```

There is no endpoint that writes an adjudication result onto a claim: to
change a pended claim's outcome use the work queue
(`POST /api/claims/work-queue/{id}/resolve` or `/override`, which re-run the
pipeline); payment status comes from remittance; voids go through
`POST /api/claims/{id}/void`.

---

## 7. (Optional) What-if estimate

To price the same services without creating or changing a claim, ask the
benefit-plan service for a read-only estimate:

```bash
curl -s -X POST http://localhost:5002/api/v1/adjudication/estimate \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"memberId\": \"MBR001\",
    \"subscriberId\": \"SUB001\",
    \"benefitPlanId\": \"$PLAN_ID\",
    \"providerNpi\": \"1234567890\",
    \"serviceDate\": \"2025-06-15\",
    \"networkTier\": \"InNetwork\",
    \"lines\": [
      { \"lineNumber\": 1, \"procedureCode\": \"99213\", \"placeOfService\": \"11\", \"chargeAmount\": 175.00, \"units\": 1 }
    ]
  }" | jq .
```

---

## 8. Run a payment batch and generate an 835 ERA

Preflight check (recommended): make sure you have at least one approved claim.

```bash
curl -s "http://localhost:5001/api/claims/search?status=5&pageSize=5" \
  -H "Authorization: Bearer $TOKEN" | jq 'map({id, claimNumber, status})'
```

If the result is empty (`[]`), submit a quick test claim and wait for the
pipeline to approve it (uses the helpers from steps 5 and 6):

```bash
CLAIM_ID=$(submit_claim LOCAL-PAY-001 '[
  { "lineNumber": 1, "procedureCode": "99213", "placeOfServiceCode": "11", "chargeAmount": 175.00,
    "units": 1, "diagnosisPointers": [1],
    "serviceDateFrom": "2025-06-15T00:00:00Z", "serviceDateTo": "2025-06-15T00:00:00Z" }
]' 175.00 | jq -r '.id')

wait_for_outcome "$CLAIM_ID"
```

Execute a payment run that picks up all adjudicated claims and generates 835 EDI for each:

```bash
PAYRUN=$(curl -s -X POST http://localhost:5006/api/paymentruns/execute \
  -H "Authorization: Bearer $TOKEN" \
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
  -H "Authorization: Bearer $TOKEN" \
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
  -H "Authorization: Bearer $TOKEN" | jq '{id, status, totalClaims, totalPaymentAmount, paymentIds, claimIds}'
```

Expected:
- `status` is `Completed`
- `totalClaims` is `1` for this walkthrough
- `paymentIds` includes the `PAYMENT_ID`

### 10.2 Confirm payment record

```bash
curl -s "http://localhost:5006/api/payments/$PAYMENT_ID" \
  -H "Authorization: Bearer $TOKEN" | jq '{id, checkNumber, status, totalPaymentAmount, claimPayments}'
```

Expected:
- `totalPaymentAmount` = `145.00`
- one `claimPayments` entry for your test claim

### 10.3 Search and summary endpoints

```bash
# Search payments in a date range
curl -s "http://localhost:5006/api/payments?paymentDateFrom=2025-06-01&paymentDateTo=2025-12-31&page=1&pageSize=50" \
  -H "Authorization: Bearer $TOKEN" | jq '.[0:3]'

# Payment summary
curl -s "http://localhost:5006/api/payments/summary?from=2025-06-01&to=2025-12-31" \
  -H "Authorization: Bearer $TOKEN" | jq .
```

Expected:
- at least one payment in search results
- summary totals reflect your payment amount and claim count

### 10.4 Test status transitions: post and reconcile

```bash
# Mark as posted
curl -s -X POST "http://localhost:5006/api/payments/$PAYMENT_ID/post" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"postedBy":"local-tester","notes":"Posted during quickstart"}' | jq '{id, status, postedAt, postedBy}'

# Mark as reconciled
curl -s -X POST "http://localhost:5006/api/payments/$PAYMENT_ID/reconcile" \
  -H "Authorization: Bearer $TOKEN" \
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
  -H "Authorization: Bearer $TOKEN" \
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

- `401` on any call: the token is missing or expired. Mint a new one with `TOKEN=$(./scripts/dev-token.sh --tenant "$TENANT")`.
- `404` on `/api/payments/{id}/835`: verify `PAYMENT_ID` is non-empty and was created with a token for the same tenant.
- `500` during payment run execution: confirm claims-service has adjudicated claims — the claim from step 6 must have reached `Approved` (5) before the payment run.
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

CPT 11040 (debridement) is column 1 to CPT 97597 (selective debridement). Submitting them together triggers an NCCI bundling edit, and the pipeline pends the claim for review:

```bash
NCCI_CLAIM_ID=$(submit_claim NCCI-TEST-001 '[
  { "lineNumber": 1, "procedureCode": "11040", "placeOfServiceCode": "11", "chargeAmount": 200.00,
    "units": 1, "diagnosisPointers": [1],
    "serviceDateFrom": "2025-06-15T00:00:00Z", "serviceDateTo": "2025-06-15T00:00:00Z" },
  { "lineNumber": 2, "procedureCode": "97597", "placeOfServiceCode": "11", "chargeAmount": 150.00,
    "units": 1, "diagnosisPointers": [1],
    "serviceDateFrom": "2025-06-15T00:00:00Z", "serviceDateTo": "2025-06-15T00:00:00Z" }
]' 350.00 | jq -r '.id')

wait_for_outcome "$NCCI_CLAIM_ID"   # expect status 4 (Pended) with an NCCI pend code
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
