#!/bin/bash
# =============================================================================
# Populate Azure Key Vault from Secrets Manifest
# =============================================================================
# Purpose: Upload secrets from a manifest file (.env format) to Azure Key Vault.
#          Idempotent — skips secrets that already exist with the same value.
# Usage:
#   ./scripts/populate-keyvault-secrets.sh \
#     --vault-name cho-app-kv \
#     --file scripts/secrets-manifest.example.env
#
#   # Preview without uploading:
#   ./scripts/populate-keyvault-secrets.sh --vault-name cho-app-kv --file manifest.env --dry-run
# =============================================================================

set -euo pipefail

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
BOLD='\033[1m'
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
MANIFEST_FILE=""
DRY_RUN=false
EXPIRY_DAYS=90
CONTENT_TYPE="text/plain"

# Counters
CREATED=0
SKIPPED=0
FAILED=0

cleanup() {
  local exit_code=$?
  if [[ $exit_code -ne 0 && $CREATED -eq 0 && $SKIPPED -eq 0 ]]; then
    print_error "Script failed before processing any secrets"
  fi
}
trap cleanup EXIT

usage() {
  cat <<EOF
Usage: $0 [OPTIONS]

Upload secrets from a manifest file to Azure Key Vault.

OPTIONS:
  -v, --vault-name NAME     Key Vault name (required)
  -f, --file PATH           Secrets manifest file in .env format (required)
  -e, --expiry-days N       Days until expiration (default: 90)
  -d, --dry-run             Preview changes without uploading
  -h, --help                Show this help message

MANIFEST FORMAT (.env):
  # Lines starting with # are comments (skipped)
  # Empty lines are skipped
  # Format: SECRET_NAME=secret_value
  CosmosDb--ConnectionString=mongodb+srv://user:pass@host/db
  Stripe--SecretKey=sk_live_...

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

while [[ $# -gt 0 ]]; do
  case $1 in
  -v | --vault-name)   VAULT_NAME="$2"; shift 2 ;;
  -f | --file)         MANIFEST_FILE="$2"; shift 2 ;;
  -e | --expiry-days)  EXPIRY_DAYS="$2"; shift 2 ;;
  -d | --dry-run)      DRY_RUN=true; shift ;;
  -h | --help)         usage; exit 0 ;;
  *)                   print_error "Unknown option: $1"; usage; exit 1 ;;
  esac
done

if [[ -z "$VAULT_NAME" ]]; then
  print_error "Vault name is required (use --vault-name)"
  usage
  exit 1
fi

if [[ -z "$MANIFEST_FILE" ]]; then
  print_error "Manifest file is required (use --file)"
  usage
  exit 1
fi

if [[ ! -f "$MANIFEST_FILE" ]]; then
  print_error "Manifest file not found: $MANIFEST_FILE"
  exit 1
fi

# =============================================================================
# Pre-flight
# =============================================================================

print_header "Populate Key Vault — $VAULT_NAME"

if [[ "$DRY_RUN" == true ]]; then
  print_warning "DRY-RUN MODE — no secrets will be uploaded"
fi

print_info "Vault:       $VAULT_NAME"
print_info "Manifest:    $MANIFEST_FILE"
print_info "Expiry:      $EXPIRY_DAYS days from now"
echo ""

if ! az account show &>/dev/null; then
  print_error "Azure CLI is not authenticated. Run: az login"
  exit 1
fi

# Calculate expiry date
if [[ "$(uname)" == "Darwin" ]]; then
  EXPIRY_DATE=$(date -v+${EXPIRY_DAYS}d -u +%Y-%m-%dT%H:%M:%SZ)
else
  EXPIRY_DATE=$(date -u -d "+${EXPIRY_DAYS} days" +%Y-%m-%dT%H:%M:%SZ)
fi

print_info "Expiry date: $EXPIRY_DATE"
echo ""

# =============================================================================
# Process manifest
# =============================================================================

while IFS= read -r line || [[ -n "$line" ]]; do
  # Skip comments and empty lines
  [[ -z "$line" || "$line" =~ ^[[:space:]]*# ]] && continue

  # Parse NAME=VALUE (first = is the delimiter)
  SECRET_NAME="${line%%=*}"
  SECRET_VALUE="${line#*=}"

  # Trim whitespace from name
  SECRET_NAME=$(echo "$SECRET_NAME" | xargs)

  # Skip if name is empty or still contains no value
  if [[ -z "$SECRET_NAME" || "$SECRET_NAME" == "$line" ]]; then
    print_warning "Skipping malformed line: ${line:0:40}..."
    continue
  fi

  # Dry-run: just print what would happen
  if [[ "$DRY_RUN" == true ]]; then
    print_info "[DRY-RUN] Would set: $SECRET_NAME (${#SECRET_VALUE} chars, expires $EXPIRY_DATE)"
    CREATED=$((CREATED + 1))
    continue
  fi

  # Check if secret already exists with the same value
  EXISTING_VALUE=$(az keyvault secret show \
    --vault-name "$VAULT_NAME" \
    --name "$SECRET_NAME" \
    --query value -o tsv 2>/dev/null || echo "")

  if [[ "$EXISTING_VALUE" == "$SECRET_VALUE" ]]; then
    print_warning "Skipped (unchanged): $SECRET_NAME"
    SKIPPED=$((SKIPPED + 1))
    continue
  fi

  # Upload secret
  if az keyvault secret set \
    --vault-name "$VAULT_NAME" \
    --name "$SECRET_NAME" \
    --value "$SECRET_VALUE" \
    --content-type "$CONTENT_TYPE" \
    --expires "$EXPIRY_DATE" \
    --tags "managed-by=populate-keyvault-secrets" "app=cloudhealthoffice" \
    --output none 2>/dev/null; then
    print_success "Created: $SECRET_NAME (expires $EXPIRY_DATE)"
    CREATED=$((CREATED + 1))
  else
    print_error "Failed: $SECRET_NAME"
    FAILED=$((FAILED + 1))
  fi

done < "$MANIFEST_FILE"

# =============================================================================
# Summary
# =============================================================================

print_header "Summary"

echo -e "  ${GREEN}Created:${NC}  $CREATED"
echo -e "  ${YELLOW}Skipped:${NC}  $SKIPPED  (already exist with same value)"
echo -e "  ${RED}Failed:${NC}   $FAILED"
echo ""

if [[ "$DRY_RUN" == true ]]; then
  print_info "Re-run without --dry-run to apply changes."
fi

if [[ $FAILED -gt 0 ]]; then
  exit 1
fi
