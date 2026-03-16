#!/usr/bin/env bash
# onboard-tenant.sh — Provision a new Cloud Health Office tenant end-to-end.
#
# Automates: tenant creation, reference data seeding, benefit plan setup,
# fee schedule seeding, operating mode configuration, and SFTP provisioning.
#
# Usage:
#   ./scripts/onboard-tenant.sh \
#     --tenant-id acme-health \
#     --tenant-name "Acme Health Plan" \
#     --admin-email admin@acme.com \
#     --environment dev
#
#   # Preview without executing:
#   ./scripts/onboard-tenant.sh --dry-run \
#     --tenant-id acme-health \
#     --tenant-name "Acme Health Plan" \
#     --admin-email admin@acme.com \
#     --environment staging
#
# Environment variables (override service URLs):
#   TENANT_SERVICE_URL     (default: derived from --environment)
#   BENEFIT_PLAN_URL       (default: derived from --environment)
#   REFERENCE_DATA_URL     (default: derived from --environment)
#   CLAIMS_SERVICE_URL     (default: derived from --environment)

set -euo pipefail

# ── Constants ────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SEED_DATA_DIR="${SCRIPT_DIR}/seed-data"
EFFECTIVE_DATE="$(date -u +%Y)-01-01T00:00:00Z"
EFFECTIVE_YYYYMMDD="$(date -u +%Y)0101"

# ── Defaults ─────────────────────────────────────────────────────────────────
TENANT_ID=""
TENANT_NAME=""
ADMIN_EMAIL=""
ENVIRONMENT=""
DRY_RUN=false
ENABLE_SFTP=false
SUBSCRIPTION_TIER="professional"
VERBOSE=false

# ── Colors / logging ────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

log()  { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()   { echo -e "${GREEN}[OK]${NC}    $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC}  $*"; }
err()  { echo -e "${RED}[ERROR]${NC} $*" >&2; }
die()  { err "$*"; exit 1; }
step() { echo -e "\n${BOLD}${CYAN}── Step $1: $2${NC}"; }
dry()  { echo -e "${YELLOW}[DRY-RUN]${NC} $*"; }

# ── Usage ────────────────────────────────────────────────────────────────────
usage() {
  cat <<USAGE
Usage: $(basename "$0") [OPTIONS]

Provision a new Cloud Health Office tenant.

Required:
  --tenant-id ID          Unique tenant identifier (lowercase, hyphens OK)
  --tenant-name NAME      Display name for the tenant/health plan
  --admin-email EMAIL     Administrator email address
  --environment ENV       Target environment: dev, staging, or prod

Optional:
  --subscription-tier T   Subscription tier: starter, professional, enterprise
                          (default: professional)
  --enable-sftp           Provision SFTP credentials for EDI ingestion
  --dry-run               Show what would be created without executing
  --verbose               Print full API request/response bodies
  --help                  Show this help message

Examples:
  # Provision in dev
  $(basename "$0") --tenant-id acme-health --tenant-name "Acme Health" \\
    --admin-email admin@acme.com --environment dev

  # Preview a production onboarding
  $(basename "$0") --dry-run --tenant-id acme-health \\
    --tenant-name "Acme Health" --admin-email admin@acme.com \\
    --environment prod --enable-sftp

Environment variable overrides:
  TENANT_SERVICE_URL, BENEFIT_PLAN_URL, REFERENCE_DATA_URL, CLAIMS_SERVICE_URL
USAGE
  exit 0
}

# ── Parse arguments ──────────────────────────────────────────────────────────
[[ $# -eq 0 ]] && usage

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tenant-id)         TENANT_ID="$2";         shift 2 ;;
    --tenant-name)       TENANT_NAME="$2";       shift 2 ;;
    --admin-email)       ADMIN_EMAIL="$2";       shift 2 ;;
    --environment)       ENVIRONMENT="$2";       shift 2 ;;
    --subscription-tier) SUBSCRIPTION_TIER="$2"; shift 2 ;;
    --enable-sftp)       ENABLE_SFTP=true;       shift ;;
    --dry-run)           DRY_RUN=true;           shift ;;
    --verbose)           VERBOSE=true;           shift ;;
    --help|-h)           usage ;;
    *) die "Unknown option: $1. Use --help for usage." ;;
  esac
done

# ── Validate required parameters ────────────────────────────────────────────
[[ -z "$TENANT_ID" ]]    && die "Missing required parameter: --tenant-id"
[[ -z "$TENANT_NAME" ]]  && die "Missing required parameter: --tenant-name"
[[ -z "$ADMIN_EMAIL" ]]  && die "Missing required parameter: --admin-email"
[[ -z "$ENVIRONMENT" ]]  && die "Missing required parameter: --environment"

# Validate tenant ID format (lowercase alphanumeric + hyphens)
if ! echo "$TENANT_ID" | grep -qE '^[a-z0-9][a-z0-9-]*[a-z0-9]$'; then
  die "Invalid tenant-id format. Use lowercase letters, numbers, and hyphens (e.g., acme-health)"
fi

# Validate environment
case "$ENVIRONMENT" in
  dev|staging|prod) ;;
  *) die "Invalid environment: $ENVIRONMENT. Must be dev, staging, or prod." ;;
esac

# Validate email format (basic check)
if ! echo "$ADMIN_EMAIL" | grep -qE '^[^@]+@[^@]+\.[^@]+$'; then
  die "Invalid email format: $ADMIN_EMAIL"
fi

# Validate subscription tier
case "$SUBSCRIPTION_TIER" in
  starter|professional|enterprise) ;;
  *) die "Invalid subscription tier: $SUBSCRIPTION_TIER. Must be starter, professional, or enterprise." ;;
esac

# ── Resolve service URLs ────────────────────────────────────────────────────
resolve_urls() {
  case "$ENVIRONMENT" in
    dev)
      TENANT_SERVICE_URL="${TENANT_SERVICE_URL:-http://localhost:5004}"
      BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-http://localhost:5002}"
      REFERENCE_DATA_URL="${REFERENCE_DATA_URL:-http://localhost:5005}"
      CLAIMS_SERVICE_URL="${CLAIMS_SERVICE_URL:-http://localhost:5001}"
      PORTAL_URL="http://localhost:3000"
      ;;
    staging)
      TENANT_SERVICE_URL="${TENANT_SERVICE_URL:-https://tenant-service.staging.cloudhealthoffice.com}"
      BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-https://benefit-plan-service.staging.cloudhealthoffice.com}"
      REFERENCE_DATA_URL="${REFERENCE_DATA_URL:-https://reference-data-service.staging.cloudhealthoffice.com}"
      CLAIMS_SERVICE_URL="${CLAIMS_SERVICE_URL:-https://claims-service.staging.cloudhealthoffice.com}"
      PORTAL_URL="https://portal.staging.cloudhealthoffice.com"
      ;;
    prod)
      TENANT_SERVICE_URL="${TENANT_SERVICE_URL:-https://tenant-service.cloudhealthoffice.com}"
      BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-https://benefit-plan-service.cloudhealthoffice.com}"
      REFERENCE_DATA_URL="${REFERENCE_DATA_URL:-https://reference-data-service.cloudhealthoffice.com}"
      CLAIMS_SERVICE_URL="${CLAIMS_SERVICE_URL:-https://claims-service.cloudhealthoffice.com}"
      PORTAL_URL="https://portal.cloudhealthoffice.com"
      ;;
  esac
}

resolve_urls

# ── Helpers ──────────────────────────────────────────────────────────────────

# Make an HTTP request with curl; capture response body and HTTP status code.
# Usage: api_call METHOD URL [DATA]
# Sets: RESPONSE_BODY, HTTP_STATUS
api_call() {
  local method="$1"
  local url="$2"
  local data="${3:-}"

  local curl_args=(
    -s -w '\n%{http_code}'
    -X "$method"
    -H "Content-Type: application/json"
    -H "X-Tenant-ID: ${TENANT_ID}"
  )

  if [[ -n "$data" ]]; then
    curl_args+=(-d "$data")
  fi

  if $VERBOSE; then
    log ">>> $method $url"
    [[ -n "$data" ]] && echo "$data" | head -5
  fi

  local raw_response
  raw_response=$(curl "${curl_args[@]}" "$url" 2>&1) || true

  HTTP_STATUS=$(echo "$raw_response" | tail -1)
  RESPONSE_BODY=$(echo "$raw_response" | sed '$d')

  if $VERBOSE; then
    log "<<< HTTP $HTTP_STATUS"
    echo "$RESPONSE_BODY" | head -10
  fi
}

# Check if a curl response indicates success (2xx)
is_success() {
  [[ "$HTTP_STATUS" =~ ^2[0-9][0-9]$ ]]
}

# ── Banner ───────────────────────────────────────────────────────────────────
echo -e "${BOLD}"
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║          Cloud Health Office — Tenant Onboarding            ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo -e "${NC}"
echo "  Tenant ID:      $TENANT_ID"
echo "  Tenant Name:    $TENANT_NAME"
echo "  Admin Email:    $ADMIN_EMAIL"
echo "  Environment:    $ENVIRONMENT"
echo "  Subscription:   $SUBSCRIPTION_TIER"
echo "  SFTP:           $ENABLE_SFTP"
echo "  Dry Run:        $DRY_RUN"
echo ""

if $DRY_RUN; then
  echo -e "${YELLOW}${BOLD}*** DRY-RUN MODE — No changes will be made ***${NC}"
  echo ""
fi

# ── Verify seed data files exist ─────────────────────────────────────────────
for f in icd10-top100.json cpt-top200.json place-of-service-codes.json claim-status-codes.json default-benefit-plan.json sample-fee-schedule.json; do
  [[ -f "${SEED_DATA_DIR}/${f}" ]] || die "Seed data file not found: ${SEED_DATA_DIR}/${f}"
done
ok "Seed data files verified"

# ══════════════════════════════════════════════════════════════════════════════
# STEP 1: Create tenant record
# ══════════════════════════════════════════════════════════════════════════════
step 1 "Create tenant record"

TENANT_PAYLOAD=$(cat <<EOF
{
  "tenantName": "${TENANT_NAME}",
  "organizationName": "${TENANT_NAME}",
  "subscriptionTier": "${SUBSCRIPTION_TIER}",
  "contactInfo": {
    "primaryContact": "Admin",
    "email": "${ADMIN_EMAIL}",
    "supportEmail": "${ADMIN_EMAIL}"
  },
  "enabledModules": ["claims", "authorizations", "eligibility", "attachments", "appeals"],
  "environments": ["${ENVIRONMENT}"]
}
EOF
)

if $DRY_RUN; then
  dry "POST ${TENANT_SERVICE_URL}/api/v1/tenants"
  dry "Payload:"
  echo "$TENANT_PAYLOAD" | sed 's/^/    /'
else
  log "Creating tenant via ${TENANT_SERVICE_URL}/api/v1/tenants ..."
  api_call POST "${TENANT_SERVICE_URL}/api/v1/tenants" "$TENANT_PAYLOAD"

  if is_success; then
    CREATED_TENANT_ID=$(echo "$RESPONSE_BODY" | grep -o '"tenantId":"[^"]*"' | head -1 | cut -d'"' -f4)
    ok "Tenant created: ${CREATED_TENANT_ID:-$TENANT_ID}"
  else
    die "Failed to create tenant (HTTP $HTTP_STATUS): $RESPONSE_BODY"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════════
# STEP 2: Seed reference data
# ══════════════════════════════════════════════════════════════════════════════
step 2 "Seed reference data"

seed_reference_data() {
  local code_type="$1"
  local file="$2"
  local endpoint="$3"
  local description="$4"

  local record_count
  record_count=$(grep -c '"code"' "${SEED_DATA_DIR}/${file}" || echo "0")

  if $DRY_RUN; then
    dry "POST ${REFERENCE_DATA_URL}${endpoint} — ${description} (${record_count} records from ${file})"
    return
  fi

  log "Seeding ${description} (${record_count} records)..."

  local data
  data=$(cat "${SEED_DATA_DIR}/${file}")

  api_call POST "${REFERENCE_DATA_URL}${endpoint}" "$data"

  if is_success; then
    ok "${description}: ${record_count} records loaded"
  else
    warn "Failed to seed ${description} (HTTP $HTTP_STATUS) — service may handle seeding differently"
    warn "Response: $(echo "$RESPONSE_BODY" | head -3)"
  fi
}

# 2a. ICD-10 codes (top 100)
seed_reference_data "icd10" "icd10-top100.json" "/api/referencedata/icd10/bulk" "ICD-10 code set (top 100 codes)"

# 2b. CPT codes (top 200)
seed_reference_data "cpt" "cpt-top200.json" "/api/referencedata/cpt/bulk" "CPT code set (top 200 codes)"

# 2c. Place of Service codes
seed_reference_data "pos" "place-of-service-codes.json" "/api/referencedata/place-of-service/bulk" "Place of Service codes"

# 2d. Claim status codes
seed_reference_data "status" "claim-status-codes.json" "/api/referencedata/claim-status/bulk" "Claim status codes"

# ══════════════════════════════════════════════════════════════════════════════
# STEP 3: Create default benefit plan
# ══════════════════════════════════════════════════════════════════════════════
step 3 "Create default benefit plan"

PLAN_PAYLOAD=$(cat "${SEED_DATA_DIR}/default-benefit-plan.json" \
  | sed "s/__TENANT_ID__/${TENANT_ID}/g" \
  | sed "s/__TENANT_NAME__/${TENANT_NAME}/g" \
  | sed "s/__EFFECTIVE_DATE__/${EFFECTIVE_DATE}/g")

if $DRY_RUN; then
  dry "POST ${BENEFIT_PLAN_URL}/api/v1/plans"
  dry "Plan: ${TENANT_ID}-DEFAULT-PPO (PPO, Commercial, \$1500 deductible, 10 benefit categories)"
else
  log "Creating default benefit plan..."
  api_call POST "${BENEFIT_PLAN_URL}/api/v1/plans" "$PLAN_PAYLOAD"

  if is_success; then
    PLAN_ID=$(echo "$RESPONSE_BODY" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
    ok "Benefit plan created: ${PLAN_ID:-${TENANT_ID}-DEFAULT-PPO}"
  else
    die "Failed to create benefit plan (HTTP $HTTP_STATUS): $RESPONSE_BODY"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════════
# STEP 4: Seed sample fee schedule
# ══════════════════════════════════════════════════════════════════════════════
step 4 "Seed sample fee schedule"

FEE_SCHEDULE_PAYLOAD=$(cat "${SEED_DATA_DIR}/sample-fee-schedule.json" \
  | sed "s/__TENANT_ID__/${TENANT_ID}/g" \
  | sed "s/__EFFECTIVE_DATE__/${EFFECTIVE_DATE}/g" \
  | sed "s/__EFFECTIVE_YYYYMMDD__/${EFFECTIVE_YYYYMMDD}/g")

FEE_LINE_COUNT=$(echo "$FEE_SCHEDULE_PAYLOAD" | grep -c '"procedureCode"' || echo "0")

if $DRY_RUN; then
  dry "POST ${BENEFIT_PLAN_URL}/api/v1/fee-schedules"
  dry "Fee schedule: ${TENANT_ID}-Commercial-Default (${FEE_LINE_COUNT} procedure lines, Commercial flat-rate)"
else
  log "Creating fee schedule with ${FEE_LINE_COUNT} procedure lines..."
  api_call POST "${BENEFIT_PLAN_URL}/api/v1/fee-schedules" "$FEE_SCHEDULE_PAYLOAD"

  if is_success; then
    FEE_SCHEDULE_ID=$(echo "$RESPONSE_BODY" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
    ok "Fee schedule created: ${FEE_SCHEDULE_ID:-${TENANT_ID}-Commercial-Default}"
  else
    warn "Fee schedule creation returned HTTP $HTTP_STATUS — may require separate setup"
    warn "Response: $(echo "$RESPONSE_BODY" | head -3)"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════════
# STEP 5: Configure default operating mode (all engines in "augment" mode)
# ══════════════════════════════════════════════════════════════════════════════
step 5 "Configure default operating mode"

OPERATING_MODE_PAYLOAD=$(cat <<EOF
{
  "engines": {
    "benefitCalculation": "augment",
    "rateResolution": "augment",
    "ncciEdits": "augment",
    "claimsScrubbing": "augment",
    "cobCalculation": "augment",
    "riskAdjustment": "augment"
  }
}
EOF
)

if $DRY_RUN; then
  dry "PUT ${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/operating-mode"
  dry "All 6 engines set to 'augment' mode:"
  dry "  benefitCalculation, rateResolution, ncciEdits,"
  dry "  claimsScrubbing, cobCalculation, riskAdjustment"
else
  log "Setting all engines to augment mode..."
  api_call PUT "${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/operating-mode" "$OPERATING_MODE_PAYLOAD"

  if is_success; then
    ok "Operating mode configured: all engines in augment mode"
  else
    warn "Operating mode configuration returned HTTP $HTTP_STATUS"
    warn "Response: $(echo "$RESPONSE_BODY" | head -3)"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════════
# STEP 6: Generate SFTP credentials (if EDI ingestion is needed)
# ══════════════════════════════════════════════════════════════════════════════
step 6 "SFTP credential provisioning"

if $ENABLE_SFTP; then
  SFTP_PAYLOAD=$(cat <<EOF
{
  "tenantId": "${TENANT_ID}",
  "tenantName": "${TENANT_NAME}",
  "environments": ["${ENVIRONMENT}"]
}
EOF
  )

  if $DRY_RUN; then
    dry "POST ${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/sftp"
    dry "SFTP directories: /inbound/${TENANT_ID}/{837,835,834,270,276}"
    dry "                  /outbound/${TENANT_ID}/{835,999,277,271}"
  else
    log "Provisioning SFTP credentials..."
    api_call POST "${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/sftp" "$SFTP_PAYLOAD"

    if is_success; then
      SFTP_HOST=$(echo "$RESPONSE_BODY" | grep -o '"sftpHost":"[^"]*"' | head -1 | cut -d'"' -f4)
      SFTP_USER=$(echo "$RESPONSE_BODY" | grep -o '"sftpUsername":"[^"]*"' | head -1 | cut -d'"' -f4)
      ok "SFTP provisioned: ${SFTP_HOST:-sftp.cloudhealthoffice.com} (user: ${SFTP_USER:-${TENANT_ID}-edi})"
    else
      warn "SFTP provisioning returned HTTP $HTTP_STATUS"
      warn "Response: $(echo "$RESPONSE_BODY" | head -3)"
    fi
  fi
else
  log "SFTP provisioning skipped (use --enable-sftp to provision)"
fi

# ══════════════════════════════════════════════════════════════════════════════
# STEP 7: Create API key for tenant
# ══════════════════════════════════════════════════════════════════════════════
step 7 "Generate API key"

API_KEY_PAYLOAD=$(cat <<EOF
{
  "name": "${TENANT_ID}-default-key",
  "scopes": ["claims:read", "claims:write", "eligibility:read", "plans:read", "referencedata:read"]
}
EOF
)

if $DRY_RUN; then
  dry "POST ${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/api-keys"
  dry "Scopes: claims:read, claims:write, eligibility:read, plans:read, referencedata:read"
else
  log "Creating API key..."
  api_call POST "${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/api-keys" "$API_KEY_PAYLOAD"

  if is_success; then
    API_KEY=$(echo "$RESPONSE_BODY" | grep -o '"apiKey":"[^"]*"' | head -1 | cut -d'"' -f4)
    API_KEY_ID=$(echo "$RESPONSE_BODY" | grep -o '"keyId":"[^"]*"' | head -1 | cut -d'"' -f4)
    ok "API key created: ${API_KEY_ID:-generated}"
  else
    warn "API key creation returned HTTP $HTTP_STATUS"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════════
# STEP 8: Activate tenant
# ══════════════════════════════════════════════════════════════════════════════
step 8 "Activate tenant"

if $DRY_RUN; then
  dry "POST ${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/activate"
else
  log "Activating tenant..."
  api_call POST "${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}/activate" ""

  if is_success; then
    ok "Tenant activated"
  else
    warn "Tenant activation returned HTTP $HTTP_STATUS"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════════
# Summary
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo -e "${BOLD}"
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║               Tenant Onboarding Summary                     ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo -e "${NC}"

if $DRY_RUN; then
  echo -e "  ${YELLOW}Mode:${NC}            DRY RUN (no changes made)"
else
  echo -e "  ${GREEN}Status:${NC}          Provisioned"
fi

echo ""
echo "  Tenant ID:       ${TENANT_ID}"
echo "  Tenant Name:     ${TENANT_NAME}"
echo "  Admin Email:     ${ADMIN_EMAIL}"
echo "  Environment:     ${ENVIRONMENT}"
echo "  Subscription:    ${SUBSCRIPTION_TIER}"
echo ""
echo "  API Endpoints:"
echo "    Tenant Service:     ${TENANT_SERVICE_URL}/api/v1/tenants/${TENANT_ID}"
echo "    Claims Service:     ${CLAIMS_SERVICE_URL}/api/claims"
echo "    Benefit Plans:      ${BENEFIT_PLAN_URL}/api/v1/plans"
echo "    Reference Data:     ${REFERENCE_DATA_URL}/api/referencedata"
echo ""
echo "  Portal URL:      ${PORTAL_URL}"
echo ""

if ! $DRY_RUN; then
  echo "  Admin Credentials:"
  echo "    Email:           ${ADMIN_EMAIL}"
  echo "    Initial access:  Configure via identity provider (not provisioned by this script)"
  if [[ -n "${API_KEY:-}" ]]; then
    echo ""
    echo -e "  ${YELLOW}API Key (save this — shown only once):${NC}"
    echo "    Key ID:          ${API_KEY_ID}"
    echo "    API Key:         ${API_KEY}"
  fi
  if $ENABLE_SFTP && [[ -n "${SFTP_HOST:-}" ]]; then
    echo ""
    echo "  SFTP Credentials:"
    echo "    Host:            ${SFTP_HOST}"
    echo "    Username:        ${SFTP_USER}"
    echo "    Auth:            SSH key (stored in Key Vault)"
  fi
fi

echo ""
echo "  Operating Mode:  All engines in AUGMENT mode"
echo "    benefitCalculation  = augment"
echo "    rateResolution      = augment"
echo "    ncciEdits           = augment"
echo "    claimsScrubbing     = augment"
echo "    cobCalculation      = augment"
echo "    riskAdjustment      = augment"
echo ""
echo "  Seeded Data:"
echo "    ICD-10 codes:        100 (top diagnoses)"
echo "    CPT codes:           200 (top procedures)"
echo "    Place of Service:     50 (CMS standard)"
echo "    Claim Status Codes:   40 (standard + extended)"
echo "    Benefit Plan:          1 (Default PPO)"
echo "    Fee Schedule:          1 (Commercial, 50 procedure lines)"
echo ""
echo -e "${BOLD}══════════════════════════════════════════════════════════════${NC}"

if $DRY_RUN; then
  echo ""
  echo "To execute this onboarding, re-run without --dry-run."
fi
