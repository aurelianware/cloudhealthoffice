# Local Claims Adjudication Quickstart

Run Cloud Health Office locally and adjudicate a claim end-to-end in under 10 minutes.

## What this covers

1. Start MongoDB + Redis + both core services with Docker Compose
2. Seed NCCI/MUE edits
3. Create a benefit plan
4. Submit a claim
5. Adjudicate it (NCCI check → fee schedule → cost sharing)
6. Verify results

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
docker compose up -d
```

This starts:

| Service | Local URL |
|---------|-----------|
| MongoDB | `localhost:27017` |
| Redis | `localhost:6379` |
| claims-service | `http://localhost:5001` |
| benefit-plan-service | `http://localhost:5002` |

Wait for both services to be healthy (≈ 30 seconds):

```bash
curl -s http://localhost:5001/health
curl -s http://localhost:5002/health
```

Both should return `Healthy`.

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

## Automated script

Run the entire flow in one command:

```bash
./scripts/seed-local.sh --tenant demo
```

---

## Swagger UIs

Explore all endpoints interactively:

- **Claims service:** http://localhost:5001/swagger
- **Benefit-plan / adjudication:** http://localhost:5002/swagger

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
