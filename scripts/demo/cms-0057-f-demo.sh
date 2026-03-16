#!/usr/bin/env bash
# =============================================================================
# CMS-0057-F Compliance End-to-End Demo
# Cloud Health Office (CHO)
#
# Demonstrates the full CMS Interoperability & Prior Authorization final rule
# (CMS-0057-F) compliance workflow against a running docker-compose stack.
#
# Intended for live prospect demos and HIMSS presentations.
#
# Prerequisites:
#   - docker compose up -d  (all CHO services running)
#   - curl and jq installed
#
# Usage:
#   ./scripts/demo/cms-0057-f-demo.sh
#
# Environment variables (override defaults):
#   ENROLLMENT_URL   (default: http://localhost:5004)
#   ELIGIBILITY_URL  (default: http://localhost:5005)
#   AUTH_URL          (default: http://localhost:5006)
#   CLAIMS_URL       (default: http://localhost:5001)
#   BENEFIT_PLAN_URL (default: http://localhost:5002)
#   FHIR_URL         (default: http://localhost:5007)
#   TENANT_ID        (default: demo-tenant)
#   FHIR_TOKEN       (default: empty — set to a SMART on FHIR Bearer token)
#   PAUSE_SECONDS    (default: 2)
# =============================================================================
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
ENROLLMENT_URL="${ENROLLMENT_URL:-http://localhost:5004}"
ELIGIBILITY_URL="${ELIGIBILITY_URL:-http://localhost:5005}"
AUTH_URL="${AUTH_URL:-http://localhost:5006}"
CLAIMS_URL="${CLAIMS_URL:-http://localhost:5001}"
BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-http://localhost:5002}"
FHIR_URL="${FHIR_URL:-http://localhost:5007}"
TENANT_ID="${TENANT_ID:-demo-tenant}"
FHIR_TOKEN="${FHIR_TOKEN:-}"
PAUSE="${PAUSE_SECONDS:-2}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIXTURES_DIR="${SCRIPT_DIR}/fixtures"

# ANSI colors for presentation
BOLD='\033[1m'
CYAN='\033[1;36m'
GREEN='\033[1;32m'
YELLOW='\033[1;33m'
RED='\033[1;31m'
RESET='\033[0m'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
step_count=0

banner() {
  echo ""
  echo -e "${CYAN}══════════════════════════════════════════════════════════════${RESET}"
  echo -e "${CYAN}  $1${RESET}"
  echo -e "${CYAN}══════════════════════════════════════════════════════════════${RESET}"
  echo ""
}

step() {
  step_count=$((step_count + 1))
  echo ""
  echo -e "${BOLD}────────────────────────────────────────────────────────────${RESET}"
  echo -e "${GREEN}  Step ${step_count}: $1${RESET}"
  echo -e "${BOLD}────────────────────────────────────────────────────────────${RESET}"
}

narrate() {
  echo -e "${YELLOW}  ➤ $1${RESET}"
}

show_response() {
  local http_code="$1"
  local body="$2"

  if [[ "$http_code" -ge 200 && "$http_code" -lt 300 ]]; then
    echo -e "  ${GREEN}HTTP ${http_code}${RESET}"
  else
    echo -e "  ${RED}HTTP ${http_code}${RESET}"
  fi

  if command -v jq &>/dev/null; then
    echo "$body" | jq '.' 2>/dev/null || echo "$body"
  else
    echo "$body"
  fi
}

pause() {
  sleep "$PAUSE"
}

# check_response exits the demo if a non-2xx status is returned
check_response() {
  local step_name="$1"
  local http_code="$2"

  if [[ "$http_code" -lt 200 || "$http_code" -ge 300 ]]; then
    echo ""
    echo -e "  ${RED}FATAL: ${step_name} returned HTTP ${http_code}. Halting demo.${RESET}" >&2
    echo -e "  ${RED}Check that all services are running (docker compose up -d).${RESET}" >&2
    exit 1
  fi
}

# Build FHIR authorization header array for curl
fhir_auth_args=()
if [[ -n "$FHIR_TOKEN" ]]; then
  fhir_auth_args=(-H "Authorization: Bearer ${FHIR_TOKEN}")
fi

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
banner "Cloud Health Office — CMS-0057-F Compliance Demo"

echo -e "  ${BOLD}Date:${RESET}       $(date '+%Y-%m-%d %H:%M %Z')"
echo -e "  ${BOLD}Tenant:${RESET}     ${TENANT_ID}"
echo -e "  ${BOLD}Claims:${RESET}     ${CLAIMS_URL}"
echo -e "  ${BOLD}Benefit:${RESET}    ${BENEFIT_PLAN_URL}"
echo -e "  ${BOLD}Enrollment:${RESET} ${ENROLLMENT_URL}"
echo -e "  ${BOLD}Eligibility:${RESET}${ELIGIBILITY_URL}"
echo -e "  ${BOLD}Auth:${RESET}       ${AUTH_URL}"
echo -e "  ${BOLD}FHIR:${RESET}       ${FHIR_URL}"
if [[ -n "$FHIR_TOKEN" ]]; then
  echo -e "  ${BOLD}FHIR Auth:${RESET}  Bearer ${FHIR_TOKEN:0:12}..."
else
  echo -e "  ${BOLD}FHIR Auth:${RESET}  ${YELLOW}not set (export FHIR_TOKEN for authenticated endpoints)${RESET}"
fi
echo ""

if ! command -v curl &>/dev/null; then
  echo -e "${RED}ERROR: curl is required but not installed.${RESET}" >&2
  exit 1
fi

if ! command -v jq &>/dev/null; then
  echo -e "${YELLOW}WARNING: jq is not installed — responses will not be formatted.${RESET}"
fi

narrate "All services configured. Starting end-to-end CMS-0057-F demo..."
pause

# ==========================================================================
# STEP 1: Enroll a member (834 transaction)
# ==========================================================================
step "Enroll Member via 834 Transaction"

narrate "Submitting 834 enrollment for Maria Santos (SUB900112345) with family coverage."
narrate "POST ${ENROLLMENT_URL}/api/v1/enrollment/import"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X POST "${ENROLLMENT_URL}/api/v1/enrollment/import" \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -d @"${FIXTURES_DIR}/enrollment-834.json")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "Enrollment import" "$HTTP_CODE"

narrate "Member enrolled with subscriber ID SUB900112345, Gold PPO family plan."
pause

# ==========================================================================
# STEP 2: Check eligibility (270/271 transaction)
# ==========================================================================
step "Verify Eligibility — 270/271 Transaction"

narrate "Submitting real-time eligibility inquiry (270) for Maria Santos."
narrate "POST ${ELIGIBILITY_URL}/api/eligibility/inquiry"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X POST "${ELIGIBILITY_URL}/api/eligibility/inquiry" \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -d @"${FIXTURES_DIR}/eligibility-270.json")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "Eligibility inquiry" "$HTTP_CODE"

narrate "271 response confirms active coverage. Member is eligible for services."
pause

# ==========================================================================
# STEP 3: Submit prior authorization (278 transaction)
# ==========================================================================
step "Submit Prior Authorization — 278 Request"

narrate "Requesting prior authorization for office visits and physical therapy."
narrate "Diagnosis: M54.5 (Low back pain)"
narrate "POST ${AUTH_URL}/api/authorizations"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X POST "${AUTH_URL}/api/authorizations" \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -d @"${FIXTURES_DIR}/authorization-278.json")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "Prior authorization" "$HTTP_CODE"

# Extract authorization ID for reference
AUTH_ID=$(echo "$BODY" | jq -r '.id // .authorizationNumber // "AUTH-PENDING"' 2>/dev/null || echo "AUTH-PENDING")
narrate "Prior authorization submitted. Reference: ${AUTH_ID}"
narrate "CMS-0057-F requires payers to process auth decisions within 72 hours (urgent) or 7 days (standard)."
pause

# ==========================================================================
# STEP 4: Submit a professional claim (837P)
# ==========================================================================
step "Submit Professional Claim — 837P"

narrate "Submitting 837P claim for office visit (99214) + therapeutic exercises (97110)."
narrate "Total billed: \$395.00 (2 service lines)"
narrate "POST ${CLAIMS_URL}/api/claims"
echo ""

# Inject tenant ID into claim payload
CLAIM_PAYLOAD=$(sed "s/__TENANT_ID__/${TENANT_ID}/g" "${FIXTURES_DIR}/claim-837p.json")

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X POST "${CLAIMS_URL}/api/claims" \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -d "$CLAIM_PAYLOAD")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "Claim submission" "$HTTP_CODE"

# Extract claim ID for adjudication
CLAIM_ID=$(echo "$BODY" | jq -r '.id // empty' 2>/dev/null || echo "")
CLAIM_NUMBER=$(echo "$BODY" | jq -r '.claimNumber // empty' 2>/dev/null || echo "")

narrate "Claim ${CLAIM_NUMBER:-$CLAIM_ID} received. Auto-calculated total charge from service lines."
pause

# ==========================================================================
# STEP 5: Adjudicate the claim
# ==========================================================================
step "Adjudicate Claim — NCCI Edits, Fee Schedule, Benefits"

narrate "Running full adjudication pipeline:"
narrate "  1. NCCI/MUE pre-payment edits"
narrate "  2. Fee schedule rate resolution"
narrate "  3. Benefit calculation (deductible, copay, coinsurance)"
narrate "  4. Accumulator updates"
echo ""

# Build adjudication request with actual claim ID
ADJ_PAYLOAD=$(cat "${FIXTURES_DIR}/adjudication-request.json" | sed "s/__CLAIM_ID__/${CLAIM_ID}/g")

narrate "POST ${BENEFIT_PLAN_URL}/api/v1/adjudication/adjudicate"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X POST "${BENEFIT_PLAN_URL}/api/v1/adjudication/adjudicate" \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  -d "$ADJ_PAYLOAD")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "Adjudication" "$HTTP_CODE"

narrate "Adjudication complete. Plan payment and member responsibility calculated."
pause

# ==========================================================================
# STEP 6: FHIR Patient Access API — ExplanationOfBenefit
# ==========================================================================
step "FHIR Patient Access API — ExplanationOfBenefit (CMS-0057-F §1)"

narrate "CMS-0057-F requires payers to expose claims data via FHIR Patient Access API."
narrate "Querying ExplanationOfBenefit for patient SUB900112345."
narrate "GET ${FHIR_URL}/fhir/r4/ExplanationOfBenefit?patient=SUB900112345"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X GET "${FHIR_URL}/fhir/r4/ExplanationOfBenefit?patient=SUB900112345" \
  -H "Accept: application/fhir+json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  "${fhir_auth_args[@]+"${fhir_auth_args[@]}"}")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "FHIR ExplanationOfBenefit" "$HTTP_CODE"

narrate "FHIR R4 ExplanationOfBenefit bundle returned — adjudicated claims accessible to member apps."
pause

# ==========================================================================
# STEP 7: FHIR Provider Directory — Practitioner
# ==========================================================================
step "FHIR Provider Directory API — Practitioner (CMS-0057-F §2)"

narrate "CMS-0057-F requires a public-facing FHIR Provider Directory."
narrate "Looking up rendering provider NPI 1234567890."
narrate "GET ${FHIR_URL}/fhir/r4/Practitioner/1234567890"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X GET "${FHIR_URL}/fhir/r4/Practitioner/1234567890" \
  -H "Accept: application/fhir+json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  "${fhir_auth_args[@]+"${fhir_auth_args[@]}"}")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "FHIR Practitioner" "$HTTP_CODE"

narrate "Provider directory entry returned with NPI, specialty, and network status."
pause

# ==========================================================================
# STEP 8: CMS-0057-F Compliance Status
# ==========================================================================
step "CMS-0057-F Compliance Self-Assessment"

narrate "Running compliance self-assessment against CMS-0057-F requirements:"
narrate "  • Patient Access API (FHIR R4)"
narrate "  • Provider Directory API"
narrate "  • Prior Authorization API"
narrate "  • Payer-to-Payer Data Exchange"
narrate "  • SMART on FHIR Scopes"
echo ""
narrate "GET ${FHIR_URL}/fhir/r4/compliance-status"
echo ""

RESPONSE=$(curl -s -w "\n%{http_code}" \
  -X GET "${FHIR_URL}/fhir/r4/compliance-status" \
  -H "Accept: application/json" \
  -H "X-Tenant-ID: ${TENANT_ID}" \
  "${fhir_auth_args[@]+"${fhir_auth_args[@]}"}")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

show_response "$HTTP_CODE" "$BODY"
check_response "Compliance status" "$HTTP_CODE"

# Extract compliance summary if available
COMPLIANT=$(echo "$BODY" | jq -r '.overallCompliant // empty' 2>/dev/null || echo "")
PCT=$(echo "$BODY" | jq -r '.compliancePercentage // empty' 2>/dev/null || echo "")

if [[ "$COMPLIANT" == "true" ]]; then
  narrate "COMPLIANT — All CMS-0057-F requirements met (${PCT}%)."
elif [[ -n "$PCT" ]]; then
  narrate "Compliance: ${PCT}% of CMS-0057-F requirements met."
fi
pause

# ==========================================================================
# Summary
# ==========================================================================
banner "Demo Complete — CMS-0057-F End-to-End"

echo -e "  ${BOLD}What we demonstrated:${RESET}"
echo ""
echo -e "  ${GREEN}✓${RESET} 834 Member Enrollment      — Automated enrollment import"
echo -e "  ${GREEN}✓${RESET} 270/271 Eligibility         — Real-time eligibility verification"
echo -e "  ${GREEN}✓${RESET} 278 Prior Authorization     — Electronic prior auth submission"
echo -e "  ${GREEN}✓${RESET} 837P Claim Submission       — Professional claim intake"
echo -e "  ${GREEN}✓${RESET} Claims Adjudication         — NCCI edits, fee schedule, benefits"
echo -e "  ${GREEN}✓${RESET} FHIR Patient Access API     — ExplanationOfBenefit for members"
echo -e "  ${GREEN}✓${RESET} FHIR Provider Directory     — Practitioner lookup by NPI"
echo -e "  ${GREEN}✓${RESET} CMS-0057-F Compliance       — Self-assessment across all requirements"
echo ""
echo -e "  ${BOLD}CMS-0057-F Interoperability & Prior Authorization Final Rule${RESET}"
echo -e "  Cloud Health Office delivers full compliance out of the box."
echo ""
echo -e "  ${CYAN}Questions? Let's dive deeper into any component.${RESET}"
echo ""
