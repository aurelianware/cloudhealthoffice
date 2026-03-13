#!/usr/bin/env bash
# seed-local.sh — Seed a local Cloud Health Office dev stack and run a
# full claim through adjudication end-to-end.
#
# Prerequisites: docker compose stack running (docker compose up -d)
#
# Usage:
#   ./scripts/seed-local.sh
#   ./scripts/seed-local.sh --tenant demo-tenant

set -euo pipefail

TENANT_ID="${1:-demo}"
CLAIMS_URL="${CLAIMS_URL:-http://localhost:5001}"
BENEFIT_URL="${BENEFIT_URL:-http://localhost:5002}"

log() { echo "▶ $*"; }
ok()  { echo "✓ $*"; }
err() { echo "✗ $*" >&2; exit 1; }

# ── Parse flags ──────────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
  case $1 in
    --tenant) TENANT_ID="$2"; shift 2 ;;
    *) shift ;;
  esac
done

log "Using tenant: $TENANT_ID"
log "Claims service:      $CLAIMS_URL"
log "Benefit-plan service: $BENEFIT_URL"
echo

# ── Wait for services ─────────────────────────────────────────────────────────
log "Waiting for services to be healthy..."
for url in "$CLAIMS_URL/health" "$BENEFIT_URL/health"; do
  for i in $(seq 1 30); do
    if curl -sf "$url" > /dev/null 2>&1; then
      ok "$url"
      break
    fi
    if [[ $i -eq 30 ]]; then
      err "$url is not healthy after 60s. Is the stack running? Try: docker compose up -d"
    fi
    sleep 2
  done
done
echo

# ── 1. Seed NCCI baseline edits ───────────────────────────────────────────────
log "Seeding NCCI/MUE Q1 2025 baseline..."
NCCI_SEED=$(curl -sf -X POST "$BENEFIT_URL/api/v1/ncci/seed" \
  -H "X-Tenant-ID: $TENANT_ID" \
  -H "Content-Type: application/json") || err "NCCI seed failed"
ok "NCCI seeded: $(echo "$NCCI_SEED" | grep -o '"editCount":[0-9]*' || echo 'done')"
echo

# ── 2. Create a benefit plan ──────────────────────────────────────────────────
log "Creating PPO benefit plan..."
PLAN_RESPONSE=$(curl -sf -X POST "$BENEFIT_URL/api/benefitplans" \
  -H "X-Tenant-ID: $TENANT_ID" \
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
      },
      {
        "serviceCategory": "47",
        "description": "Hospital Inpatient",
        "inNetworkCoinsurance": 0.20,
        "deductibleApplies": true,
        "priorAuthRequired": true
      }
    ]
  }') || err "Benefit plan creation failed"

PLAN_ID=$(echo "$PLAN_RESPONSE" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
[[ -n "$PLAN_ID" ]] || err "Could not extract plan ID from response: $PLAN_RESPONSE"
ok "Benefit plan created: $PLAN_ID"
echo

# ── 3. Submit a claim ─────────────────────────────────────────────────────────
log "Submitting test claim..."
CLAIM_RESPONSE=$(curl -sf -X POST "$CLAIMS_URL/api/claims" \
  -H "X-Tenant-ID: $TENANT_ID" \
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
  }') || err "Claim submission failed"

CLAIM_ID=$(echo "$CLAIM_RESPONSE" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
[[ -n "$CLAIM_ID" ]] || err "Could not extract claim ID from response: $CLAIM_RESPONSE"
ok "Claim submitted: $CLAIM_ID"
echo

# ── 4. Adjudicate the claim ───────────────────────────────────────────────────
log "Adjudicating claim against benefit plan $PLAN_ID..."
ADJ_RESPONSE=$(curl -sf -X POST "$BENEFIT_URL/api/v1/adjudication/adjudicate" \
  -H "X-Tenant-ID: $TENANT_ID" \
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
  }") || err "Adjudication failed"

ok "Adjudication result:"
echo "$ADJ_RESPONSE" | python3 -m json.tool 2>/dev/null || echo "$ADJ_RESPONSE"
echo

# ── 5. Update claim with adjudication result ──────────────────────────────────
PLAN_PAYMENT=$(echo "$ADJ_RESPONSE" | grep -o '"planPayment":[0-9.]*' | head -1 | cut -d: -f2)
MEMBER_RESP=$(echo "$ADJ_RESPONSE" | grep -o '"memberResponsibility":[0-9.]*' | head -1 | cut -d: -f2)
ALLOWED=$(echo "$ADJ_RESPONSE" | grep -o '"allowedAmount":[0-9.]*' | head -1 | cut -d: -f2)

log "Updating claim status to Adjudicated..."
curl -sf -X PUT "$CLAIMS_URL/api/claims/$CLAIM_ID/adjudication" \
  -H "X-Tenant-ID: $TENANT_ID" \
  -H "Content-Type: application/json" \
  -d "{
    \"status\": \"Adjudicated\",
    \"allowedAmount\": ${ALLOWED:-175.00},
    \"memberLiability\": ${MEMBER_RESP:-30.00},
    \"planPayment\": ${PLAN_PAYMENT:-145.00},
    \"adjudicationDate\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"
  }" > /dev/null || err "Claim update failed"

ok "Claim updated to Adjudicated"
echo

# ── Summary ───────────────────────────────────────────────────────────────────
echo "═══════════════════════════════════════════"
echo " Local adjudication test complete!"
echo "═══════════════════════════════════════════"
echo " Tenant:       $TENANT_ID"
echo " Plan ID:      $PLAN_ID"
echo " Claim ID:     $CLAIM_ID"
echo " Allowed:      \$${ALLOWED:-n/a}"
echo " Plan pays:    \$${PLAN_PAYMENT:-n/a}"
echo " Member owes:  \$${MEMBER_RESP:-n/a}"
echo
echo " Swagger UIs:"
echo "   Claims:       $CLAIMS_URL/swagger"
echo "   Benefit plan: $BENEFIT_URL/swagger"
echo "═══════════════════════════════════════════"
