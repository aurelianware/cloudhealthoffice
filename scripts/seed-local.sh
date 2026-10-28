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

# Services take the tenant only from a validated token; mint a local one.
TOKEN="$("$(dirname "$0")/dev-token.sh" --tenant "$TENANT_ID")"

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
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json") || err "NCCI seed failed"
ok "NCCI seeded: $(echo "$NCCI_SEED" | grep -o '"editCount":[0-9]*' || echo 'done')"
echo

# ── 2. Create a benefit plan ──────────────────────────────────────────────────
log "Creating PPO benefit plan..."
PLAN_RESPONSE=$(curl -sf -X POST "$BENEFIT_URL/api/benefitplans" \
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
# Submission is the only input: the claims-service adjudication pipeline
# (consumer → ClaimAdjudicationOrchestrator → PersistenceStage) picks the
# claim up and is the only writer of its adjudicated outcome.
log "Submitting test claim..."
CLAIM_RESPONSE=$(curl -sf -X POST "$CLAIMS_URL/api/claims" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"claimNumber\": \"LOCAL-TEST-001\",
    \"memberId\": \"MBR001\",
    \"subscriberId\": \"SUB001\",
    \"benefitPlanId\": \"$PLAN_ID\",
    \"billingProviderNPI\": \"1234567890\",
    \"placeOfServiceCode\": \"11\",
    \"serviceDateFrom\": \"2025-06-15T00:00:00Z\",
    \"serviceDateTo\": \"2025-06-15T00:00:00Z\",
    \"diagnosisCodes\": [{ \"code\": \"Z00.00\", \"pointerNumber\": 1 }],
    \"claimLines\": [
      {
        \"lineNumber\": 1,
        \"procedureCode\": \"99213\",
        \"placeOfServiceCode\": \"11\",
        \"chargeAmount\": 175.00,
        \"units\": 1,
        \"diagnosisPointers\": [1],
        \"serviceDateFrom\": \"2025-06-15T00:00:00Z\",
        \"serviceDateTo\": \"2025-06-15T00:00:00Z\"
      }
    ],
    \"totalChargeAmount\": 175.00
  }") || err "Claim submission failed"

CLAIM_ID=$(echo "$CLAIM_RESPONSE" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
[[ -n "$CLAIM_ID" ]] || err "Could not extract claim ID from response: $CLAIM_RESPONSE"
ok "Claim submitted: $CLAIM_ID"
echo

# ── 4. Wait for the adjudication pipeline's persisted outcome ────────────────
# Poll GET /api/claims/{id} until the claim reaches a terminal status
# (Pended=4, Approved=5, Denied=6, Paid=7, Voided=8, PartiallyPaid=9 — the
# status may serialize as the enum number or its name).
log "Waiting for the adjudication pipeline to adjudicate $CLAIM_ID..."
STATUS=""
CLAIM_JSON=""
for i in $(seq 1 60); do
  CLAIM_JSON=$(curl -sf "$CLAIMS_URL/api/claims/$CLAIM_ID" -H "Authorization: Bearer $TOKEN" || true)
  STATUS=$(echo "$CLAIM_JSON" | grep -o '"status":"\{0,1\}[A-Za-z0-9]*' | head -1 | sed 's/"status":"\{0,1\}//')
  case "$STATUS" in
    4|5|6|7|8|9|Pended|Approved|Denied|Paid|Voided|PartiallyPaid) break ;;
  esac
  if [[ $i -eq 60 ]]; then
    err "Claim $CLAIM_ID did not reach a terminal status within 60s (last status: ${STATUS:-unknown}). Is the claims-service adjudication consumer running?"
  fi
  sleep 1
done

case "$STATUS" in
  4) STATUS="Pended" ;; 5) STATUS="Approved" ;; 6) STATUS="Denied" ;;
  7) STATUS="Paid" ;; 8) STATUS="Voided" ;; 9) STATUS="PartiallyPaid" ;;
esac
ok "Pipeline outcome: $STATUS"

ALLOWED=$(echo "$CLAIM_JSON" | grep -o '"allowedAmount":[0-9.]*' | head -1 | cut -d: -f2)
PLAN_PAYMENT=$(echo "$CLAIM_JSON" | grep -o '"payerPayment":[0-9.]*' | head -1 | cut -d: -f2)
MEMBER_RESP=$(echo "$CLAIM_JSON" | grep -o '"patientResponsibility":[0-9.]*' | head -1 | cut -d: -f2)
echo

# ── Summary ───────────────────────────────────────────────────────────────────
echo "═══════════════════════════════════════════"
echo " Local adjudication test complete!"
echo "═══════════════════════════════════════════"
echo " Tenant:       $TENANT_ID"
echo " Plan ID:      $PLAN_ID"
echo " Claim ID:     $CLAIM_ID"
echo " Status:       $STATUS"
echo " Allowed:      \$${ALLOWED:-n/a}"
echo " Plan pays:    \$${PLAN_PAYMENT:-n/a}"
echo " Member owes:  \$${MEMBER_RESP:-n/a}"
echo
echo " Swagger UIs:"
echo "   Claims:       $CLAIMS_URL/swagger"
echo "   Benefit plan: $BENEFIT_URL/swagger"
echo "═══════════════════════════════════════════"
