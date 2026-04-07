#!/bin/bash
# =============================================================================
# Check Azure Key Vault Secret Expiration
# =============================================================================
# Purpose: List secrets expiring within N days. Designed for scheduled alerting
#          (e.g. cron GitHub Action). Does NOT auto-generate new values.
# Usage:
#   ./scripts/rotate-keyvault-secrets.sh --vault-name cho-app-kv
#   ./scripts/rotate-keyvault-secrets.sh --vault-name cho-app-kv --days 30 --format json
# =============================================================================

set -euo pipefail

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

print_header() {
  echo ""
  echo -e "${BLUE}========================================${NC}"
  echo -e "${BLUE}$1${NC}"
  echo -e "${BLUE}========================================${NC}"
  echo ""
}

print_success() { echo -e "${GREEN}✓${NC} $1"; }
print_error()   { echo -e "${RED}✗${NC} $1"; }
print_warning() { echo -e "${YELLOW}⚠${NC} $1"; }
print_info()    { echo -e "${BLUE}ℹ${NC} $1"; }

# Defaults
VAULT_NAME=""
DAYS=14
FORMAT="table"   # table or json

usage() {
  cat <<EOF
Usage: $0 [OPTIONS]

List Azure Key Vault secrets expiring within N days.

OPTIONS:
  -v, --vault-name NAME   Key Vault name (required)
  -d, --days N            Warning threshold in days (default: 14)
  -f, --format FORMAT     Output format: table or json (default: table)
  -h, --help              Show this help message

EXAMPLES:
  # Check for secrets expiring within 14 days (default)
  $0 --vault-name cho-app-kv

  # Check 30-day window, JSON output for CI/CD alerting
  $0 --vault-name cho-app-kv --days 30 --format json

EXIT CODES:
  0 - No secrets expiring within the threshold
  1 - Error or at least one secret expiring soon

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

while [[ $# -gt 0 ]]; do
  case $1 in
  -v | --vault-name) VAULT_NAME="$2"; shift 2 ;;
  -d | --days)       DAYS="$2"; shift 2 ;;
  -f | --format)     FORMAT="$2"; shift 2 ;;
  -h | --help)       usage; exit 0 ;;
  *)                 print_error "Unknown option: $1"; usage; exit 1 ;;
  esac
done

if [[ -z "$VAULT_NAME" ]]; then
  print_error "Vault name is required (use --vault-name)"
  usage
  exit 1
fi

# =============================================================================
# Pre-flight
# =============================================================================

if ! az account show &>/dev/null; then
  print_error "Azure CLI is not authenticated. Run: az login"
  exit 1
fi

# Calculate threshold date
NOW_EPOCH=$(date -u +%s)
if [[ "$(uname)" == "Darwin" ]]; then
  THRESHOLD_DATE=$(date -v+${DAYS}d -u +%Y-%m-%dT%H:%M:%SZ)
  THRESHOLD_EPOCH=$(date -v+${DAYS}d -u +%s)
else
  THRESHOLD_DATE=$(date -u -d "+${DAYS} days" +%Y-%m-%dT%H:%M:%SZ)
  THRESHOLD_EPOCH=$(date -u -d "+${DAYS} days" +%s)
fi

# =============================================================================
# Fetch secrets and check expiry
# =============================================================================

if [[ "$FORMAT" == "table" ]]; then
  print_header "Secret Expiration Check — $VAULT_NAME"
  print_info "Threshold: ${DAYS} days (before $THRESHOLD_DATE)"
  echo ""
fi

# Get all secret properties
SECRETS_JSON=$(az keyvault secret list \
  --vault-name "$VAULT_NAME" \
  --query "[].{name:name, expires:attributes.expires, enabled:attributes.enabled}" \
  -o json 2>/dev/null)

EXPIRING_COUNT=0
TOTAL_COUNT=0
NO_EXPIRY_COUNT=0
JSON_RESULTS="[]"

while IFS= read -r secret_line; do
  NAME=$(echo "$secret_line" | cut -d'|' -f1)
  EXPIRES=$(echo "$secret_line" | cut -d'|' -f2)
  ENABLED=$(echo "$secret_line" | cut -d'|' -f3)

  [[ -z "$NAME" ]] && continue
  TOTAL_COUNT=$((TOTAL_COUNT + 1))

  # Skip disabled secrets
  [[ "$ENABLED" == "false" ]] && continue

  # No expiration set
  if [[ -z "$EXPIRES" || "$EXPIRES" == "null" || "$EXPIRES" == "None" ]]; then
    NO_EXPIRY_COUNT=$((NO_EXPIRY_COUNT + 1))
    continue
  fi

  # Parse expiry — handle both ISO 8601 formats
  if [[ "$(uname)" == "Darwin" ]]; then
    EXPIRY_EPOCH=$(date -j -f "%Y-%m-%dT%H:%M:%S" "${EXPIRES%%+*}" +%s 2>/dev/null || \
                   date -j -f "%Y-%m-%dT%H:%M:%SZ" "$EXPIRES" +%s 2>/dev/null || echo "0")
  else
    EXPIRY_EPOCH=$(date -u -d "$EXPIRES" +%s 2>/dev/null || echo "0")
  fi

  # Already expired
  if [[ "$EXPIRY_EPOCH" -le "$NOW_EPOCH" ]]; then
    EXPIRING_COUNT=$((EXPIRING_COUNT + 1))
    DAYS_AGO=$(( (NOW_EPOCH - EXPIRY_EPOCH) / 86400 ))
    if [[ "$FORMAT" == "table" ]]; then
      print_error "EXPIRED ${DAYS_AGO}d ago: $NAME (expired $EXPIRES)"
    fi
    JSON_RESULTS=$(echo "$JSON_RESULTS" | python3 -c "
import sys, json
data = json.load(sys.stdin)
data.append({'name':'$NAME','expires':'$EXPIRES','status':'expired','daysRemaining':-${DAYS_AGO}})
print(json.dumps(data))" 2>/dev/null || echo "$JSON_RESULTS")
    continue
  fi

  # Expiring within threshold
  if [[ "$EXPIRY_EPOCH" -le "$THRESHOLD_EPOCH" ]]; then
    EXPIRING_COUNT=$((EXPIRING_COUNT + 1))
    DAYS_LEFT=$(( (EXPIRY_EPOCH - NOW_EPOCH) / 86400 ))
    if [[ "$FORMAT" == "table" ]]; then
      print_warning "Expiring in ${DAYS_LEFT}d: $NAME (expires $EXPIRES)"
    fi
    JSON_RESULTS=$(echo "$JSON_RESULTS" | python3 -c "
import sys, json
data = json.load(sys.stdin)
data.append({'name':'$NAME','expires':'$EXPIRES','status':'expiring_soon','daysRemaining':${DAYS_LEFT}})
print(json.dumps(data))" 2>/dev/null || echo "$JSON_RESULTS")
  fi

done < <(echo "$SECRETS_JSON" | python3 -c "
import sys, json
for s in json.load(sys.stdin):
    print(f\"{s['name']}|{s.get('expires','')}|{s.get('enabled',True)}\")
" 2>/dev/null)

# =============================================================================
# Output
# =============================================================================

if [[ "$FORMAT" == "json" ]]; then
  echo "$JSON_RESULTS" | python3 -c "
import sys, json
data = json.load(sys.stdin)
print(json.dumps({
    'vault': '$VAULT_NAME',
    'thresholdDays': $DAYS,
    'totalSecrets': $TOTAL_COUNT,
    'expiringCount': $EXPIRING_COUNT,
    'noExpiryCount': $NO_EXPIRY_COUNT,
    'secrets': data
}, indent=2))" 2>/dev/null
else
  print_header "Summary"
  print_info "Total secrets:        $TOTAL_COUNT"
  print_info "No expiry set:        $NO_EXPIRY_COUNT"

  if [[ $EXPIRING_COUNT -gt 0 ]]; then
    print_error "Expiring/expired:     $EXPIRING_COUNT"
    echo ""
    print_info "Action required: rotate the secrets listed above."
    print_info "After rotating, update the secret in Key Vault:"
    print_info "  az keyvault secret set --vault-name $VAULT_NAME --name <name> --value '<new-value>'"
  else
    print_success "No secrets expiring within $DAYS days"
  fi
fi

# Exit non-zero if any secrets are expiring
if [[ $EXPIRING_COUNT -gt 0 ]]; then
  exit 1
fi
