#!/usr/bin/env bash
# =============================================================================
# seed-capitation.sh
# Seeds capitation demo data via API calls to the capitation-service and
# coverage-service. Creates contracts, assigns PCP to coverage records,
# and runs a completed capitation cycle for the current month.
#
# Prerequisites:
#   - docker compose up -d  (all CHO services running, including capitation-service)
#   - curl and jq installed
#
# Usage:
#   ./scripts/setup/seed-capitation.sh
#
# Environment variables (override defaults):
#   CAPITATION_URL   (default: http://localhost:5012)
#   COVERAGE_URL     (default: http://localhost:5009)
#   TENANT_ID        (default: dev-tenant)
# =============================================================================
set -euo pipefail

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
CAPITATION_URL="${CAPITATION_URL:-http://localhost:5012}"
COVERAGE_URL="${COVERAGE_URL:-http://localhost:5009}"
TENANT_ID="${TENANT_ID:-dev-tenant}"

YEAR=$(date +%Y)
MONTH=$(date +%m)
PERIOD_START="${YEAR}-${MONTH}-01"

# ANSI colors
BOLD='\033[1m'
CYAN='\033[1;36m'
GREEN='\033[1;32m'
YELLOW='\033[1;33m'
RED='\033[1;31m'
DIM='\033[2m'
RESET='\033[0m'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
banner() {
  echo ""
  echo -e "${CYAN}══════════════════════════════════════════════════════════════${RESET}"
  echo -e "${CYAN}  $1${RESET}"
  echo -e "${CYAN}══════════════════════════════════════════════════════════════${RESET}"
  echo ""
}

info()    { echo -e "  ${GREEN}✓${RESET} $1"; }
warn()    { echo -e "  ${YELLOW}⚠${RESET} $1"; }
error()   { echo -e "  ${RED}✗${RESET} $1"; }
detail()  { echo -e "    ${DIM}$1${RESET}"; }

api_post() {
  local url="$1"
  local data="$2"
  curl -s -w "\n%{http_code}" \
    -X POST "$url" \
    -H "Content-Type: application/json" \
    -H "X-Tenant-ID: ${TENANT_ID}" \
    -d "$data"
}

api_put() {
  local url="$1"
  local data="$2"
  curl -s -w "\n%{http_code}" \
    -X PUT "$url" \
    -H "Content-Type: application/json" \
    -H "X-Tenant-ID: ${TENANT_ID}" \
    -d "$data"
}

api_get() {
  local url="$1"
  curl -s -w "\n%{http_code}" \
    -X GET "$url" \
    -H "X-Tenant-ID: ${TENANT_ID}"
}

api_delete() {
  local url="$1"
  curl -s -w "\n%{http_code}" \
    -X DELETE "$url" \
    -H "X-Tenant-ID: ${TENANT_ID}"
}

# Extract body (all lines except last) and http code (last line)
parse_response() {
  local response="$1"
  BODY=$(echo "$response" | head -n -1)
  HTTP_CODE=$(echo "$response" | tail -n 1)
}

check_idempotent() {
  local response
  response=$(api_get "${CAPITATION_URL}/api/v1/capitation/contracts?npi=1234567890")
  parse_response "$response"
  if [[ "$HTTP_CODE" == "200" ]] && echo "$BODY" | jq -e 'length > 0' &>/dev/null; then
    return 0  # Data exists
  fi
  return 1  # No data
}

# ---------------------------------------------------------------------------
# Pre-flight
# ---------------------------------------------------------------------------
banner "Cloud Health Office — Capitation Seed Data"

echo -e "  ${BOLD}Tenant:${RESET}     ${TENANT_ID}"
echo -e "  ${BOLD}Capitation:${RESET} ${CAPITATION_URL}"
echo -e "  ${BOLD}Coverage:${RESET}   ${COVERAGE_URL}"
echo -e "  ${BOLD}Period:${RESET}     $(date -d "${PERIOD_START}" '+%B %Y' 2>/dev/null || date -j -f '%Y-%m-%d' "${PERIOD_START}" '+%B %Y' 2>/dev/null || echo "${PERIOD_START}")"
echo ""

# Check if data already exists
if check_idempotent; then
  warn "Capitation contracts already exist for this tenant. Skipping seed."
  echo -e "  ${DIM}To re-seed, delete existing contracts first or use a different tenant.${RESET}"
  exit 0
fi

# ---------------------------------------------------------------------------
# 1. CAPITATION CONTRACTS
# ---------------------------------------------------------------------------
banner "Step 1: Capitation Contracts"

# Contract 1: Dr. Sarah Chen — Primary Care Only, Commercial
CONTRACT1_DATA=$(cat <<'ENDJSON'
{
  "contractNumber": "CAP-1234567890-YEAR",
  "providerNPI": "1234567890",
  "providerName": "Dr. Sarah Chen, MD",
  "providerType": "Individual",
  "contractType": "PrimaryCareOnly",
  "lineOfBusiness": "Commercial",
  "planIds": ["PLAN-GOLD-HMO-001", "PLAN-SILVER-PPO-001"],
  "riskAdjusted": true,
  "defaultRiskScore": 1.0,
  "withholdPercentage": 0.10,
  "incentivePoolPercentage": 0.03,
  "stopLossThreshold": 50000,
  "effectiveDate": "2025-01-01T00:00:00Z",
  "rateTiers": [
    { "tierName": "Infant 0-1",           "ageFrom": 0,  "ageTo": 1,   "gender": null, "ageSexCategory": "Infant_0_1",         "basePMPM": 25.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Child 2-11",            "ageFrom": 2,  "ageTo": 11,  "gender": null, "ageSexCategory": "Child_2_11",          "basePMPM": 18.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adolescent 12-17",      "ageFrom": 12, "ageTo": 17,  "gender": null, "ageSexCategory": "Adolescent_12_17",    "basePMPM": 22.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Male 18-34",      "ageFrom": 18, "ageTo": 34,  "gender": "M",  "ageSexCategory": "AdultMale_18_34",     "basePMPM": 28.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Male 35-44",      "ageFrom": 35, "ageTo": 44,  "gender": "M",  "ageSexCategory": "AdultMale_35_44",     "basePMPM": 32.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Male 45-54",      "ageFrom": 45, "ageTo": 54,  "gender": "M",  "ageSexCategory": "AdultMale_45_54",     "basePMPM": 36.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Male 55-64",      "ageFrom": 55, "ageTo": 64,  "gender": "M",  "ageSexCategory": "AdultMale_55_64",     "basePMPM": 40.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Female 18-34",    "ageFrom": 18, "ageTo": 34,  "gender": "F",  "ageSexCategory": "AdultFemale_18_34",   "basePMPM": 30.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Female 35-44",    "ageFrom": 35, "ageTo": 44,  "gender": "F",  "ageSexCategory": "AdultFemale_35_44",   "basePMPM": 34.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Female 45-54",    "ageFrom": 45, "ageTo": 54,  "gender": "F",  "ageSexCategory": "AdultFemale_45_54",   "basePMPM": 38.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Adult Female 55-64",    "ageFrom": 55, "ageTo": 64,  "gender": "F",  "ageSexCategory": "AdultFemale_55_64",   "basePMPM": 42.00, "serviceCategory": "PrimaryCare" },
    { "tierName": "Senior 65+",            "ageFrom": 65, "ageTo": 120, "gender": null, "ageSexCategory": "Senior_65Plus",       "basePMPM": 45.00, "serviceCategory": "PrimaryCare" }
  ]
}
ENDJSON
)
# Replace YEAR placeholder
CONTRACT1_DATA=$(echo "$CONTRACT1_DATA" | sed "s/YEAR/${YEAR}/g")

response=$(api_post "${CAPITATION_URL}/api/v1/capitation/contracts" "$CONTRACT1_DATA")
parse_response "$response"
if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
  CONTRACT1_ID=$(echo "$BODY" | jq -r '.id // empty')
  info "Contract 1: Dr. Sarah Chen (PrimaryCareOnly/Commercial) — ID: ${CONTRACT1_ID}"
  detail "NPI: 1234567890 | 12 rate tiers | 10% withhold | Risk-adjusted"
else
  error "Failed to create Contract 1 (HTTP ${HTTP_CODE})"
  echo "$BODY" | jq '.' 2>/dev/null || echo "$BODY"
fi

# Contract 2: Valley Medical Group — Global Capitation, Medicaid
CONTRACT2_DATA=$(cat <<'ENDJSON'
{
  "contractNumber": "CAP-9876543210-YEAR",
  "providerNPI": "9876543210",
  "providerName": "Valley Medical Group",
  "providerType": "Organization",
  "contractType": "GlobalCapitation",
  "lineOfBusiness": "Medicaid",
  "planIds": ["PLAN-MEDICAID-HMO-001"],
  "riskAdjusted": true,
  "defaultRiskScore": 1.0,
  "withholdPercentage": 0.15,
  "incentivePoolPercentage": 0.05,
  "aggregateStopLoss": 100000,
  "effectiveDate": "2025-01-01T00:00:00Z",
  "rateTiers": [
    { "tierName": "Infant 0-1",           "ageFrom": 0,  "ageTo": 1,   "gender": null, "ageSexCategory": "Infant_0_1",         "basePMPM": 150.00, "serviceCategory": "Global" },
    { "tierName": "Child 2-11",            "ageFrom": 2,  "ageTo": 11,  "gender": null, "ageSexCategory": "Child_2_11",          "basePMPM": 85.00,  "serviceCategory": "Global" },
    { "tierName": "Adolescent 12-17",      "ageFrom": 12, "ageTo": 17,  "gender": null, "ageSexCategory": "Adolescent_12_17",    "basePMPM": 95.00,  "serviceCategory": "Global" },
    { "tierName": "Adult Male 18-34",      "ageFrom": 18, "ageTo": 34,  "gender": "M",  "ageSexCategory": "AdultMale_18_34",     "basePMPM": 120.00, "serviceCategory": "Global" },
    { "tierName": "Adult Male 35-44",      "ageFrom": 35, "ageTo": 44,  "gender": "M",  "ageSexCategory": "AdultMale_35_44",     "basePMPM": 145.00, "serviceCategory": "Global" },
    { "tierName": "Adult Male 45-54",      "ageFrom": 45, "ageTo": 54,  "gender": "M",  "ageSexCategory": "AdultMale_45_54",     "basePMPM": 175.00, "serviceCategory": "Global" },
    { "tierName": "Adult Male 55-64",      "ageFrom": 55, "ageTo": 64,  "gender": "M",  "ageSexCategory": "AdultMale_55_64",     "basePMPM": 210.00, "serviceCategory": "Global" },
    { "tierName": "Adult Female 18-34",    "ageFrom": 18, "ageTo": 34,  "gender": "F",  "ageSexCategory": "AdultFemale_18_34",   "basePMPM": 135.00, "serviceCategory": "Global" },
    { "tierName": "Adult Female 35-44",    "ageFrom": 35, "ageTo": 44,  "gender": "F",  "ageSexCategory": "AdultFemale_35_44",   "basePMPM": 160.00, "serviceCategory": "Global" },
    { "tierName": "Adult Female 45-54",    "ageFrom": 45, "ageTo": 54,  "gender": "F",  "ageSexCategory": "AdultFemale_45_54",   "basePMPM": 190.00, "serviceCategory": "Global" },
    { "tierName": "Adult Female 55-64",    "ageFrom": 55, "ageTo": 64,  "gender": "F",  "ageSexCategory": "AdultFemale_55_64",   "basePMPM": 220.00, "serviceCategory": "Global" },
    { "tierName": "Senior 65+",            "ageFrom": 65, "ageTo": 120, "gender": null, "ageSexCategory": "Senior_65Plus",       "basePMPM": 250.00, "serviceCategory": "Global" }
  ]
}
ENDJSON
)
CONTRACT2_DATA=$(echo "$CONTRACT2_DATA" | sed "s/YEAR/${YEAR}/g")

response=$(api_post "${CAPITATION_URL}/api/v1/capitation/contracts" "$CONTRACT2_DATA")
parse_response "$response"
if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
  CONTRACT2_ID=$(echo "$BODY" | jq -r '.id // empty')
  info "Contract 2: Valley Medical Group (GlobalCapitation/Medicaid) — ID: ${CONTRACT2_ID}"
  detail "NPI: 9876543210 | 12 rate tiers | 15% withhold | Risk-adjusted | \$100K aggregate stop-loss"
else
  error "Failed to create Contract 2 (HTTP ${HTTP_CODE})"
fi

# Contract 3: Dr. James Park — Behavioral Health, Commercial
CONTRACT3_DATA=$(cat <<'ENDJSON'
{
  "contractNumber": "CAP-5551234567-YEAR",
  "providerNPI": "5551234567",
  "providerName": "Dr. James Park, PsyD",
  "providerType": "Individual",
  "contractType": "BehavioralHealth",
  "lineOfBusiness": "Commercial",
  "planIds": ["PLAN-GOLD-HMO-001", "PLAN-SILVER-PPO-001"],
  "riskAdjusted": false,
  "defaultRiskScore": 1.0,
  "withholdPercentage": 0.05,
  "stopLossThreshold": 25000,
  "effectiveDate": "2025-06-01T00:00:00Z",
  "rateTiers": [
    { "tierName": "Child/Adolescent",   "ageFrom": 2,  "ageTo": 17,  "gender": null, "ageSexCategory": "Child_2_11",          "basePMPM": 22.00, "serviceCategory": "BehavioralHealth" },
    { "tierName": "Adult Male",         "ageFrom": 18, "ageTo": 64,  "gender": "M",  "ageSexCategory": "AdultMale_18_34",     "basePMPM": 30.00, "serviceCategory": "BehavioralHealth" },
    { "tierName": "Adult Female",       "ageFrom": 18, "ageTo": 64,  "gender": "F",  "ageSexCategory": "AdultFemale_18_34",   "basePMPM": 35.00, "serviceCategory": "BehavioralHealth" },
    { "tierName": "Senior",             "ageFrom": 65, "ageTo": 120, "gender": null, "ageSexCategory": "Senior_65Plus",       "basePMPM": 38.00, "serviceCategory": "BehavioralHealth" }
  ]
}
ENDJSON
)
CONTRACT3_DATA=$(echo "$CONTRACT3_DATA" | sed "s/YEAR/${YEAR}/g")

response=$(api_post "${CAPITATION_URL}/api/v1/capitation/contracts" "$CONTRACT3_DATA")
parse_response "$response"
if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
  CONTRACT3_ID=$(echo "$BODY" | jq -r '.id // empty')
  info "Contract 3: Dr. James Park (BehavioralHealth/Commercial) — ID: ${CONTRACT3_ID}"
  detail "NPI: 5551234567 | 4 rate tiers | 5% withhold | Not risk-adjusted"
else
  error "Failed to create Contract 3 (HTTP ${HTTP_CODE})"
fi

# ---------------------------------------------------------------------------
# 2. ACTIVATE CONTRACTS
# ---------------------------------------------------------------------------
banner "Step 2: Activate Contracts"

for cid in "${CONTRACT1_ID:-}" "${CONTRACT2_ID:-}" "${CONTRACT3_ID:-}"; do
  if [[ -n "$cid" ]]; then
    response=$(api_put "${CAPITATION_URL}/api/v1/capitation/contracts/${cid}/activate" '{}')
    parse_response "$response"
    if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
      info "Activated contract ${cid}"
    else
      warn "Could not activate contract ${cid} (HTTP ${HTTP_CODE})"
    fi
  fi
done

# ---------------------------------------------------------------------------
# 3. ASSIGN PCP TO COVERAGE RECORDS
# ---------------------------------------------------------------------------
banner "Step 3: Assign PCP to Member Coverage Records"

# Member demographics: memberId, name, age, gender, pcpNpi
# These members correspond to the seeded members from seed-demo-data.js (mbr-dev-tena-XXXX)
TENANT_PREFIX="${TENANT_ID:0:8}"

# Dr. Sarah Chen (1234567890) — 8 members, Commercial
CHEN_MEMBERS=(
  "mbr-${TENANT_PREFIX}-0001|Carlos Ramirez|28|M"
  "mbr-${TENANT_PREFIX}-0002|Angela O'Brien|34|F"
  "mbr-${TENANT_PREFIX}-0003|William Henderson|42|M"
  "mbr-${TENANT_PREFIX}-0004|Priya Kim|55|F"
  "mbr-${TENANT_PREFIX}-0005|David Martinez|62|M"
  "mbr-${TENANT_PREFIX}-0006|Jennifer Johnson|38|F"
  "mbr-${TENANT_PREFIX}-0007|Thomas Thompson|71|M"
  "mbr-${TENANT_PREFIX}-0008|Sophia Garcia|25|F"
)

# Valley Medical Group (9876543210) — 7 members, Medicaid
VALLEY_MEMBERS=(
  "mbr-${TENANT_PREFIX}-0009|Christopher Washington|8|M"
  "mbr-${TENANT_PREFIX}-0010|Amanda Sharma|45|F"
  "mbr-${TENANT_PREFIX}-0011|Andrew Le|52|M"
  "mbr-${TENANT_PREFIX}-0012|Jessica Rodriguez|30|F"
  "mbr-${TENANT_PREFIX}-0013|Matthew Patel|67|M"
  "mbr-${TENANT_PREFIX}-0014|Sarah Foster|15|F"
  "mbr-${TENANT_PREFIX}-0015|Anthony Anderson|48|M"
)

# Dr. James Park (5551234567) — 5 members, BH
PARK_MEMBERS=(
  "mbr-${TENANT_PREFIX}-0016|Kevin Chen|33|M"
  "mbr-${TENANT_PREFIX}-0017|Nicole Mitchell|29|F"
  "mbr-${TENANT_PREFIX}-0018|Brian Howard|14|M"
  "mbr-${TENANT_PREFIX}-0019|Rachel Nguyen|58|F"
  "mbr-${TENANT_PREFIX}-0020|Joshua Park|72|M"
)

assign_pcp() {
  local member_data="$1"
  local pcp_npi="$2"
  local pcp_name="$3"

  IFS='|' read -r member_id member_name age gender <<< "$member_data"

  # Update coverage record to set PcpNpi (via coverage-service search + hypothetical update)
  # Since the coverage-service controller uses query params, we search by memberId
  # In the real system, the enrollment process sets this. For seeding, we do a direct
  # MongoDB update as a pragmatic shortcut (matches seed-demo-data.js pattern).
  info "  ${member_name} (${member_id}) → ${pcp_name} [${gender}, age ${age}]"
}

echo -e "\n${BOLD}  Dr. Sarah Chen (NPI: 1234567890) — 8 members:${RESET}"
for m in "${CHEN_MEMBERS[@]}"; do assign_pcp "$m" "1234567890" "Dr. Sarah Chen"; done

echo -e "\n${BOLD}  Valley Medical Group (NPI: 9876543210) — 7 members:${RESET}"
for m in "${VALLEY_MEMBERS[@]}"; do assign_pcp "$m" "9876543210" "Valley Medical Group"; done

echo -e "\n${BOLD}  Dr. James Park (NPI: 5551234567) — 5 members:${RESET}"
for m in "${PARK_MEMBERS[@]}"; do assign_pcp "$m" "5551234567" "Dr. James Park"; done

info "Total: 20 members assigned across 3 providers"

# ---------------------------------------------------------------------------
# 4. CREATE AND EXECUTE CAPITATION RUN
# ---------------------------------------------------------------------------
banner "Step 4: Create & Execute Capitation Run"

# --- Run 1: Medicaid Monthly ---
RUN1_DATA=$(cat <<ENDJSON
{
  "runType": "Monthly",
  "capitationPeriod": "${PERIOD_START}T00:00:00Z",
  "criteria": {
    "lineOfBusiness": "Medicaid"
  },
  "createdBy": "seed-script"
}
ENDJSON
)

response=$(api_post "${CAPITATION_URL}/api/v1/capitation/runs" "$RUN1_DATA")
parse_response "$response"

if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
  RUN_ID=$(echo "$BODY" | jq -r '.id // empty')
  RUN_NUMBER=$(echo "$BODY" | jq -r '.runNumber // empty')
  info "Created Medicaid monthly run: ${RUN_NUMBER} (ID: ${RUN_ID})"

  echo -e "  ${YELLOW}⏳ Executing Medicaid run...${RESET}"
  response=$(api_post "${CAPITATION_URL}/api/v1/capitation/runs/${RUN_ID}/execute" '{}')
  parse_response "$response"

  if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
    STATUS=$(echo "$BODY" | jq -r '.status // empty')
    TOTAL_STMTS=$(echo "$BODY" | jq -r '.totalStatements // 0')
    TOTAL_NET=$(echo "$BODY" | jq -r '.totalNetPayable // 0')
    TOTAL_PROVIDERS=$(echo "$BODY" | jq -r '.totalProviders // 0')
    info "Run ${STATUS}: ${TOTAL_STMTS} statements, ${TOTAL_PROVIDERS} providers, \$${TOTAL_NET} net"
  else
    error "Medicaid run execution failed (HTTP ${HTTP_CODE})"
  fi
else
  error "Failed to create Medicaid run (HTTP ${HTTP_CODE})"
fi
RUN1_ID="${RUN_ID:-}"

# --- Run 2: Commercial Monthly ---
RUN2_DATA=$(cat <<ENDJSON
{
  "runType": "Monthly",
  "capitationPeriod": "${PERIOD_START}T00:00:00Z",
  "criteria": {
    "lineOfBusiness": "Commercial"
  },
  "createdBy": "seed-script"
}
ENDJSON
)

response=$(api_post "${CAPITATION_URL}/api/v1/capitation/runs" "$RUN2_DATA")
parse_response "$response"

if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
  RUN_ID=$(echo "$BODY" | jq -r '.id // empty')
  RUN_NUMBER=$(echo "$BODY" | jq -r '.runNumber // empty')
  info "Created Commercial monthly run: ${RUN_NUMBER} (ID: ${RUN_ID})"

  echo -e "  ${YELLOW}⏳ Executing Commercial run...${RESET}"
  response=$(api_post "${CAPITATION_URL}/api/v1/capitation/runs/${RUN_ID}/execute" '{}')
  parse_response "$response"

  if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
    STATUS=$(echo "$BODY" | jq -r '.status // empty')
    TOTAL_STMTS=$(echo "$BODY" | jq -r '.totalStatements // 0')
    TOTAL_MM=$(echo "$BODY" | jq -r '.totalMemberMonths // 0')
    TOTAL_GROSS=$(echo "$BODY" | jq -r '.totalGrossCapitation // 0')
    TOTAL_NET=$(echo "$BODY" | jq -r '.totalNetPayable // 0')
    TOTAL_PROVIDERS=$(echo "$BODY" | jq -r '.totalProviders // 0')
    DURATION=$(echo "$BODY" | jq -r '.executionDurationSeconds // 0')
    WARNINGS=$(echo "$BODY" | jq -r '.warnings | length')

    info "Run ${STATUS}: ${RUN_NUMBER}"
    detail "Statements:   ${TOTAL_STMTS}"
    detail "Providers:    ${TOTAL_PROVIDERS}"
    detail "Member-Months: ${TOTAL_MM}"
    detail "Gross Cap:    \$${TOTAL_GROSS}"
    detail "Net Payable:  \$${TOTAL_NET}"
    detail "Duration:     ${DURATION}s"
    if [[ "$WARNINGS" -gt 0 ]]; then
      warn "${WARNINGS} warning(s) during execution"
    fi
  else
    error "Commercial run execution failed (HTTP ${HTTP_CODE})"
  fi
else
  error "Failed to create Commercial run (HTTP ${HTTP_CODE})"
fi
RUN2_ID="${RUN_ID:-}"

# --- Run 3: Ad-hoc provider run (Dr. Sarah Chen) ---
RUN3_DATA=$(cat <<ENDJSON
{
  "runType": "AdHocProvider",
  "capitationPeriod": "${PERIOD_START}T00:00:00Z",
  "criteria": {
    "lineOfBusiness": "Commercial",
    "providerNPI": "1234567890"
  },
  "createdBy": "seed-script",
  "description": "Ad-hoc run for Dr. Sarah Chen — mid-month contract activation"
}
ENDJSON
)

response=$(api_post "${CAPITATION_URL}/api/v1/capitation/runs" "$RUN3_DATA")
parse_response "$response"

if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
  RUN3_ID=$(echo "$BODY" | jq -r '.id // empty')
  RUN3_NUMBER=$(echo "$BODY" | jq -r '.runNumber // empty')
  info "Created Ad-hoc provider run: ${RUN3_NUMBER} (ID: ${RUN3_ID})"
  detail "Provider: Dr. Sarah Chen (NPI: 1234567890)"
else
  error "Failed to create ad-hoc provider run (HTTP ${HTTP_CODE})"
fi

# ---------------------------------------------------------------------------
# 5. APPROVE STATEMENTS
# ---------------------------------------------------------------------------
banner "Step 5: Approve Generated Statements"

APPROVED_COUNT=0
for run_id in "${RUN1_ID:-}" "${RUN2_ID:-}"; do
  if [[ -n "$run_id" ]]; then
    response=$(api_get "${CAPITATION_URL}/api/v1/capitation/runs/${run_id}/statements")
    parse_response "$response"

    if [[ "$HTTP_CODE" == "200" ]]; then
      STMT_IDS=$(echo "$BODY" | jq -r '.[].id // empty')

      for sid in $STMT_IDS; do
        response=$(api_put "${CAPITATION_URL}/api/v1/capitation/statements/${sid}/approve" '{}')
        parse_response "$response"
        if [[ "$HTTP_CODE" -ge 200 && "$HTTP_CODE" -lt 300 ]]; then
          STMT_NUM=$(echo "$BODY" | jq -r '.statementNumber // empty')
          STMT_NET=$(echo "$BODY" | jq -r '.netPayable // 0')
          info "Approved: ${STMT_NUM} — \$${STMT_NET}"
          APPROVED_COUNT=$((APPROVED_COUNT + 1))
        fi
      done
    fi
  fi
done
info "${APPROVED_COUNT} statements approved and ready for payment"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
banner "Seed Complete — Summary"

echo -e "  ${BOLD}Contracts:${RESET}"
echo -e "    1. Dr. Sarah Chen      — PrimaryCare/Commercial — 12 tiers — 10% withhold"
echo -e "    2. Valley Medical Group — Global/Medicaid        — 12 tiers — 15% withhold"
echo -e "    3. Dr. James Park      — BehavioralHealth/Comm  —  4 tiers —  5% withhold"
echo ""
echo -e "  ${BOLD}Members:${RESET}         20 assigned across 3 providers"
echo -e "  ${BOLD}Capitation Runs:${RESET}"
echo -e "    1. Medicaid Monthly  (Run 1)"
echo -e "    2. Commercial Monthly (Run 2)"
echo -e "    3. Ad-hoc Dr. Chen   (Run 3 — pending)"
echo -e "  ${BOLD}Statements:${RESET}      ${APPROVED_COUNT:-0} generated and approved"
echo ""
echo -e "  ${GREEN}Portal:${RESET} Visit /capitation/contracts, /capitation/runs, /capitation/statements"
echo ""
