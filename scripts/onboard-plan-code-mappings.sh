#!/usr/bin/env bash
# onboard-plan-code-mappings.sh — Load an employer's 834 plan-code crosswalk
# before their first production enrollment file lands.
#
# Wraps the onboarding loop across enrollment-import-service (gap detection)
# and benefit-plan-service (mapping storage):
#
#   1. gap-report  Scan a trading partner's sample 834 file for every
#                  distinct (group, insurance line, plan code) triple it
#                  uses, and report which are already mapped vs. missing.
#                  Writes a fill-in-the-blank CSV template for the gaps.
#   2. (ops fills the planId column in the CSV using the employer's/broker's
#      plan crosswalk)
#   3. load        Bulk-load the filled-in CSV into benefit-plan-service.
#   4. verify       Re-run the gap report; exits non-zero if anything is
#                   still unmapped, so this can gate a go-live checklist.
#
# Usage:
#   ./scripts/onboard-plan-code-mappings.sh gap-report \
#     --tenant-id acme-health --environment dev \
#     --file sample-834s/acme-test.edi --out acme-mappings.csv
#
#   # ... fill in the planId column in acme-mappings.csv ...
#
#   ./scripts/onboard-plan-code-mappings.sh load \
#     --tenant-id acme-health --environment dev \
#     --file acme-mappings.csv
#
#   ./scripts/onboard-plan-code-mappings.sh verify \
#     --tenant-id acme-health --environment dev \
#     --file sample-834s/acme-test.edi
#
# Environment variables (override service URLs):
#   BENEFIT_PLAN_URL        (default: derived from --environment)
#   ENROLLMENT_IMPORT_URL   (default: derived from --environment)
#
# Requires: curl, jq

set -euo pipefail

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
step() { echo -e "\n${BOLD}${CYAN}── $1${NC}"; }
dry()  { echo -e "${YELLOW}[DRY-RUN]${NC} $*"; }

for bin in curl jq; do
  command -v "$bin" >/dev/null 2>&1 || die "Required tool not found: $bin"
done

# ── Usage ────────────────────────────────────────────────────────────────────
usage() {
  cat <<USAGE
Usage: $(basename "$0") <command> [OPTIONS]

Commands:
  gap-report   Scan a sample 834 file for plan codes; report mapped/unmapped
               and write a fill-in-the-blank CSV template for the gaps.
  load         Bulk-load a filled-in plan-code-mapping CSV.
  verify       Re-run the gap report; exit non-zero if anything is unmapped.

Common options:
  --tenant-id ID       Tenant ID (sent as X-Tenant-ID)                [required]
  --environment ENV    dev, staging, or prod                          [required]
  --dry-run            Show what would be sent without executing
  --verbose            Print full API request/response bodies
  --help               Show this help message

gap-report / verify options:
  --file PATH          Path to a sample X12 834 .edi file             [required]
  --out PATH           Where to write the unmapped-codes CSV template
                        (gap-report only; default: ./plan-code-mappings.csv)

load options:
  --file PATH          CSV with header: groupNumber,insuranceLineCode,externalPlanCode,planId
                        (the exact format gap-report writes)           [required]

Examples:
  $(basename "$0") gap-report --tenant-id acme-health --environment dev \\
    --file sample-834s/acme-test.edi --out acme-mappings.csv

  $(basename "$0") load --tenant-id acme-health --environment dev \\
    --file acme-mappings.csv

  $(basename "$0") verify --tenant-id acme-health --environment prod \\
    --file sample-834s/acme-test.edi

Environment variable overrides:
  BENEFIT_PLAN_URL, ENROLLMENT_IMPORT_URL
USAGE
  exit 0
}

[[ $# -eq 0 ]] && usage

COMMAND="$1"; shift
case "$COMMAND" in
  gap-report|load|verify) ;;
  --help|-h) usage ;;
  *) die "Unknown command: $COMMAND. Use --help for usage." ;;
esac

# ── Defaults ─────────────────────────────────────────────────────────────────
TENANT_ID=""
ENVIRONMENT=""
FILE=""
OUT_FILE="plan-code-mappings.csv"
DRY_RUN=false
VERBOSE=false

# ── Parse arguments ──────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tenant-id)   TENANT_ID="$2";   shift 2 ;;
    --environment) ENVIRONMENT="$2"; shift 2 ;;
    --file)        FILE="$2";        shift 2 ;;
    --out)         OUT_FILE="$2";    shift 2 ;;
    --dry-run)     DRY_RUN=true;     shift ;;
    --verbose)     VERBOSE=true;     shift ;;
    --help|-h)     usage ;;
    *) die "Unknown option: $1. Use --help for usage." ;;
  esac
done

[[ -z "$TENANT_ID" ]]    && die "Missing required parameter: --tenant-id"
[[ -z "$ENVIRONMENT" ]]  && die "Missing required parameter: --environment"
[[ -z "$FILE" ]]         && die "Missing required parameter: --file"
[[ -f "$FILE" ]]         || die "File not found: $FILE"

case "$ENVIRONMENT" in
  dev|staging|prod) ;;
  *) die "Invalid environment: $ENVIRONMENT. Must be dev, staging, or prod." ;;
esac

# ── Resolve service URLs ────────────────────────────────────────────────────
case "$ENVIRONMENT" in
  dev)
    BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-http://localhost:5002}"
    ENROLLMENT_IMPORT_URL="${ENROLLMENT_IMPORT_URL:-http://localhost:5011}"
    ;;
  staging)
    BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-https://benefit-plan-service.staging.cloudhealthoffice.com}"
    ENROLLMENT_IMPORT_URL="${ENROLLMENT_IMPORT_URL:-https://enrollment-import-service.staging.cloudhealthoffice.com}"
    ;;
  prod)
    BENEFIT_PLAN_URL="${BENEFIT_PLAN_URL:-https://benefit-plan-service.cloudhealthoffice.com}"
    ENROLLMENT_IMPORT_URL="${ENROLLMENT_IMPORT_URL:-https://enrollment-import-service.cloudhealthoffice.com}"
    ;;
esac

# ── Helpers ──────────────────────────────────────────────────────────────────

# JSON POST. Usage: api_call METHOD URL [DATA]. Sets RESPONSE_BODY, HTTP_STATUS.
api_call() {
  local method="$1" url="$2" data="${3:-}"
  local curl_args=(-s -w '\n%{http_code}' -X "$method" -H "Content-Type: application/json" -H "X-Tenant-ID: ${TENANT_ID}")
  [[ -n "$data" ]] && curl_args+=(-d "$data")

  if $VERBOSE; then
    log ">>> $method $url"
    [[ -n "$data" ]] && echo "$data" | head -c 2000
  fi

  local raw_response
  raw_response=$(curl "${curl_args[@]}" "$url" 2>&1) || true
  HTTP_STATUS=$(echo "$raw_response" | tail -1)
  RESPONSE_BODY=$(echo "$raw_response" | sed '$d')

  if $VERBOSE; then
    log "<<< HTTP $HTTP_STATUS"
    echo "$RESPONSE_BODY" | head -c 2000
    echo ""
  fi
}

# Multipart file upload. Usage: api_call_upload URL FILE_FIELD FILE_PATH.
# Sets RESPONSE_BODY, HTTP_STATUS.
api_call_upload() {
  local url="$1" field="$2" path="$3"

  if $VERBOSE; then
    log ">>> POST $url (file=$path)"
  fi

  local raw_response
  raw_response=$(curl -s -w '\n%{http_code}' -X POST \
    -H "X-Tenant-ID: ${TENANT_ID}" \
    -F "${field}=@${path}" \
    "$url" 2>&1) || true
  HTTP_STATUS=$(echo "$raw_response" | tail -1)
  RESPONSE_BODY=$(echo "$raw_response" | sed '$d')

  if $VERBOSE; then
    log "<<< HTTP $HTTP_STATUS"
    echo "$RESPONSE_BODY" | head -c 2000
    echo ""
  fi
}

is_success() { [[ "$HTTP_STATUS" =~ ^2[0-9][0-9]$ ]]; }

# ── gap-report / verify ──────────────────────────────────────────────────────
run_gap_report() {
  local write_csv="$1"

  if $DRY_RUN; then
    dry "POST ${ENROLLMENT_IMPORT_URL}/api/v1/enrollment/plan-code-gap-report/raw834 (file=$FILE)"
    return 0
  fi

  log "Scanning $FILE for plan codes via ${ENROLLMENT_IMPORT_URL} ..."
  api_call_upload "${ENROLLMENT_IMPORT_URL}/api/v1/enrollment/plan-code-gap-report/raw834" "file" "$FILE"
  is_success || die "Gap report failed (HTTP $HTTP_STATUS): $RESPONSE_BODY"

  local mapped_count unmapped_count incomplete_count
  mapped_count=$(echo "$RESPONSE_BODY" | jq '.mapped | length')
  unmapped_count=$(echo "$RESPONSE_BODY" | jq '.unmapped | length')
  incomplete_count=$(echo "$RESPONSE_BODY" | jq '.incompleteCount')

  echo ""
  echo "  Mapped:      $mapped_count"
  echo "  Unmapped:    $unmapped_count"
  echo "  Incomplete:  $incomplete_count  (coverage lines missing a group number or plan code — not fixable by mapping)"
  echo ""

  if [[ "$unmapped_count" -gt 0 ]]; then
    warn "Unmapped plan codes:"
    echo "$RESPONSE_BODY" | jq -r '.unmapped[] | "    \(.groupNumber)  \(.insuranceLineCode)  \(.externalPlanCode)"'
  fi

  if [[ "$write_csv" == "true" ]]; then
    echo "groupNumber,insuranceLineCode,externalPlanCode,planId" > "$OUT_FILE"
    echo "$RESPONSE_BODY" | jq -r '.unmapped[] | [.groupNumber, .insuranceLineCode, .externalPlanCode, ""] | @csv' >> "$OUT_FILE"
    if [[ "$unmapped_count" -gt 0 ]]; then
      ok "Wrote $unmapped_count row(s) to $OUT_FILE — fill in the planId column, then run: $(basename "$0") load --tenant-id $TENANT_ID --environment $ENVIRONMENT --file $OUT_FILE"
    else
      ok "No gaps — $OUT_FILE not written."
    fi
  fi

  UNMAPPED_COUNT="$unmapped_count"
}

# ── load ─────────────────────────────────────────────────────────────────────
run_load() {
  local mappings_json="[]"
  local skipped=0
  local total_rows=0

  while IFS=',' read -r group line code planid; do
    [[ "$group" == "groupNumber" ]] && continue
    [[ -z "$group" ]] && continue
    total_rows=$((total_rows + 1))

    # Strip CSV quoting that jq's @csv (used by gap-report) may have added.
    group=$(echo "$group" | sed 's/^"//; s/"$//')
    line=$(echo "$line" | sed 's/^"//; s/"$//')
    code=$(echo "$code" | sed 's/^"//; s/"$//')
    planid=$(echo "$planid" | sed 's/^"//; s/"$//')

    if [[ -z "$planid" ]]; then
      warn "Skipping $group / $line / $code — planId column is empty"
      skipped=$((skipped + 1))
      continue
    fi

    local entry
    entry=$(jq -n --arg g "$group" --arg l "$line" --arg c "$code" --arg p "$planid" \
      '{groupNumber:$g, insuranceLineCode:$l, externalPlanCode:$c, planId:$p}')
    mappings_json=$(echo "$mappings_json" | jq --argjson e "$entry" '. + [$e]')
  done < <(tr -d '\r' < "$FILE")

  local load_count
  load_count=$(echo "$mappings_json" | jq 'length')
  [[ "$load_count" -eq 0 ]] && die "No rows with a filled-in planId found in $FILE ($skipped skipped, $total_rows total)."

  if $DRY_RUN; then
    dry "POST ${BENEFIT_PLAN_URL}/api/v1/plan-code-mappings/bulk ($load_count mapping(s), $skipped skipped)"
    echo "$mappings_json" | jq .
    return 0
  fi

  log "Loading $load_count mapping(s) via ${BENEFIT_PLAN_URL} ($skipped row(s) skipped for missing planId) ..."
  api_call POST "${BENEFIT_PLAN_URL}/api/v1/plan-code-mappings/bulk" "$mappings_json"
  is_success || die "Bulk load failed (HTTP $HTTP_STATUS): $RESPONSE_BODY"

  local created_count error_count
  created_count=$(echo "$RESPONSE_BODY" | jq '.created | length')
  error_count=$(echo "$RESPONSE_BODY" | jq '.errors | length')

  ok "Created $created_count mapping(s)."
  if [[ "$error_count" -gt 0 ]]; then
    warn "$error_count row(s) rejected:"
    echo "$RESPONSE_BODY" | jq -r '.errors[] | "    [\(.index)] \(.groupNumber) / \(.externalPlanCode): \(.error)"'
  fi
}

# ── Dispatch ─────────────────────────────────────────────────────────────────
echo -e "${BOLD}"
echo "══════════════════════════════════════════════════════════════"
echo "  Cloud Health Office — 834 Plan-Code Mapping Onboarding"
echo "══════════════════════════════════════════════════════════════"
echo -e "${NC}"
echo "  Command:      $COMMAND"
echo "  Tenant ID:    $TENANT_ID"
echo "  Environment:  $ENVIRONMENT"
echo "  File:         $FILE"
$DRY_RUN && echo -e "  ${YELLOW}Dry Run:      true${NC}"
echo ""

case "$COMMAND" in
  gap-report)
    step "Scanning for plan-code gaps"
    run_gap_report "true"
    ;;
  load)
    step "Loading plan-code mappings"
    run_load
    ;;
  verify)
    step "Verifying plan-code mappings are complete"
    run_gap_report "false"
    if $DRY_RUN; then
      : # nothing to verify in dry-run
    elif [[ "${UNMAPPED_COUNT:-0}" -gt 0 ]]; then
      die "$UNMAPPED_COUNT plan code(s) still unmapped — not ready for production 834 processing."
    else
      ok "All plan codes in $FILE are mapped."
    fi
    ;;
esac
