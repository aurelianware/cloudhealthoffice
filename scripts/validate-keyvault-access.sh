#!/bin/bash
# =============================================================================
# Validate Key Vault Access Script
# =============================================================================
# Purpose: Test Key Vault access from GitHub Actions workflow or local machine
# Usage: ./validate-keyvault-access.sh --vault-name <name> --test-secret <secret-name>
# =============================================================================

set -euo pipefail

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Default values
VAULT_NAME=""
TEST_SECRET="sftp-host"
VERBOSE=false

# =============================================================================
# Helper Functions
# =============================================================================

print_header() {
  echo -e "${BLUE}========================================${NC}"
  echo -e "${BLUE}$1${NC}"
  echo -e "${BLUE}========================================${NC}"
  echo ""
}

print_success() {
  echo -e "${GREEN}✓${NC} $1"
}

print_error() {
  echo -e "${RED}✗${NC} $1"
}

print_warning() {
  echo -e "${YELLOW}⚠${NC} $1"
}

print_info() {
  echo -e "${BLUE}ℹ${NC} $1"
}

usage() {
  cat <<EOF
Usage: $0 [OPTIONS]

Validate Azure Key Vault access for deployment workflows.

OPTIONS:
  -v, --vault-name NAME       Key Vault name (required)
  -s, --test-secret NAME      Secret name to test retrieval (default: sftp-host)
  -V, --verbose               Enable verbose output
  -h, --help                  Show this help message

EXAMPLES:
  # Test access to PROD deployment Key Vault
  $0 --vault-name cloud-health-office-prod-deploy-kv

  # Test specific secret retrieval
  $0 --vault-name cloud-health-office-dev-deploy-kv --test-secret sftp-username

  # Verbose mode for troubleshooting
  $0 --vault-name cloud-health-office-uat-deploy-kv --verbose

WHAT THIS SCRIPT CHECKS:
  1. Azure CLI is authenticated
  2. Current subscription and account
  3. Key Vault exists and is accessible
  4. RBAC permissions to read secrets
  5. Test secret can be retrieved
  6. Network connectivity to Key Vault

EXIT CODES:
  0 - All validation checks passed
  1 - Validation failed (check error messages)

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

while [[ $# -gt 0 ]]; do
  case $1 in
  -v | --vault-name)
    VAULT_NAME="$2"
    shift 2
    ;;
  -s | --test-secret)
    TEST_SECRET="$2"
    shift 2
    ;;
  -V | --verbose)
    VERBOSE=true
    shift
    ;;
  -h | --help)
    usage
    exit 0
    ;;
  *)
    print_error "Unknown option: $1"
    usage
    exit 1
    ;;
  esac
done

# Validate required parameters
if [[ -z "$VAULT_NAME" ]]; then
  print_error "Vault name is required (use --vault-name)"
  usage
  exit 1
fi

# =============================================================================
# Main Validation
# =============================================================================

VALIDATION_FAILED=0

print_header "Key Vault Access Validation"

print_info "Vault Name: $VAULT_NAME"
print_info "Test Secret: $TEST_SECRET"
print_info "Verbose Mode: $VERBOSE"
echo ""

# Check 1: Azure CLI is authenticated
print_header "Check 1: Azure CLI Authentication"

if ! az account show &>/dev/null; then
  print_error "Azure CLI is not authenticated"
  print_info "Please run: az login"
  exit 1
fi

print_success "Azure CLI is authenticated"

CURRENT_ACCOUNT=$(az account show --query "name" -o tsv)
CURRENT_SUB=$(az account show --query "id" -o tsv)
CURRENT_USER=$(az account show --query "user.name" -o tsv)

print_info "Account: $CURRENT_ACCOUNT"
print_info "Subscription: $CURRENT_SUB"
print_info "User: $CURRENT_USER"
echo ""

# Check 2: Key Vault exists
print_header "Check 2: Key Vault Existence"

if ! az keyvault show --name "$VAULT_NAME" &>/dev/null; then
  print_error "Key Vault '$VAULT_NAME' not found"
  VALIDATION_FAILED=1

  print_info "Troubleshooting steps:"
  print_info "  1. Check the Key Vault name spelling"
  print_info "  2. Ensure Key Vault is in the current subscription"
  print_info "  3. List all Key Vaults: az keyvault list --query '[].name' -o table"

  echo ""
else
  print_success "Key Vault '$VAULT_NAME' exists"

  # Get Key Vault details
  KV_LOCATION=$(az keyvault show --name "$VAULT_NAME" --query "location" -o tsv)
  KV_RG=$(az keyvault show --name "$VAULT_NAME" --query "resourceGroup" -o tsv)
  KV_ID=$(az keyvault show --name "$VAULT_NAME" --query "id" -o tsv)

  print_info "Location: $KV_LOCATION"
  print_info "Resource Group: $KV_RG"

  if [[ "$VERBOSE" == "true" ]]; then
    print_info "Resource ID: $KV_ID"
  fi

  echo ""
fi

# Check 3: RBAC Configuration
print_header "Check 3: RBAC Configuration"

RBAC_ENABLED=$(az keyvault show --name "$VAULT_NAME" --query "properties.enableRbacAuthorization" -o tsv 2>/dev/null || echo "false")

if [[ "$RBAC_ENABLED" == "true" ]]; then
  print_success "RBAC authorization is enabled"
else
  print_warning "RBAC authorization is NOT enabled (using Access Policies)"
  print_info "For GitHub Actions, RBAC is recommended"
fi

echo ""

# Check 4: Public Network Access
print_header "Check 4: Network Access Configuration"

PUBLIC_ACCESS=$(az keyvault show --name "$VAULT_NAME" --query "properties.publicNetworkAccess" -o tsv 2>/dev/null || echo "Enabled")
NETWORK_ACL_DEFAULT=$(az keyvault show --name "$VAULT_NAME" --query "properties.networkAcls.defaultAction" -o tsv 2>/dev/null || echo "Allow")

print_info "Public Network Access: $PUBLIC_ACCESS"
print_info "Network ACL Default Action: $NETWORK_ACL_DEFAULT"

if [[ "$PUBLIC_ACCESS" == "Disabled" ]]; then
  print_warning "Public network access is disabled"
  print_info "GitHub Actions may not be able to access this Key Vault"
  print_info "Consider using private endpoints or enabling public access"
fi

if [[ "$NETWORK_ACL_DEFAULT" == "Deny" ]]; then
  print_warning "Network ACL default action is Deny"
  print_info "GitHub Actions IP may need to be whitelisted"
fi

echo ""

# Check 5: List Secrets (verify read permission)
print_header "Check 5: List Secrets Permission"

if az keyvault secret list --vault-name "$VAULT_NAME" --query "[].name" -o tsv &>/dev/null; then
  print_success "Can list secrets in Key Vault"

  SECRET_COUNT=$(az keyvault secret list --vault-name "$VAULT_NAME" --query "length([])" -o tsv)
  print_info "Found $SECRET_COUNT secrets"

  if [[ "$VERBOSE" == "true" ]]; then
    print_info "Secrets:"
    az keyvault secret list --vault-name "$VAULT_NAME" --query "[].name" -o tsv | while read -r secret; do
      echo "    - $secret"
    done
  fi
else
  print_error "Cannot list secrets (permission denied)"
  VALIDATION_FAILED=1

  print_info "Required RBAC role: Key Vault Secrets User"
  print_info "Grant access with:"
  print_info "  az role assignment create \\"
  print_info "    --assignee <user-or-sp-id> \\"
  print_info "    --role 'Key Vault Secrets User' \\"
  print_info "    --scope /subscriptions/$CURRENT_SUB/resourceGroups/$KV_RG/providers/Microsoft.KeyVault/vaults/$VAULT_NAME"
fi

echo ""

# Check 6: Test Secret Retrieval
print_header "Check 6: Test Secret Retrieval"

if az keyvault secret show --vault-name "$VAULT_NAME" --name "$TEST_SECRET" --query "name" -o tsv &>/dev/null; then
  print_success "Secret '$TEST_SECRET' exists and can be retrieved"

  if [[ "$VERBOSE" == "true" ]]; then
    SECRET_VALUE=$(az keyvault secret show --vault-name "$VAULT_NAME" --name "$TEST_SECRET" --query "value" -o tsv)
    SECRET_LENGTH=${#SECRET_VALUE}
    print_info "Secret length: $SECRET_LENGTH characters"
  fi
else
  print_error "Secret '$TEST_SECRET' not found or cannot be retrieved"
  VALIDATION_FAILED=1

  print_info "Available secrets in Key Vault:"
  az keyvault secret list --vault-name "$VAULT_NAME" --query "[].name" -o table 2>/dev/null || echo "  Unable to list secrets"

  print_info ""
  print_info "If secret doesn't exist, create it with:"
  print_info "  az keyvault secret set --vault-name $VAULT_NAME --name $TEST_SECRET --value '<value>'"
fi

echo ""

# Check 7: Diagnostic Settings (if enabled)
print_header "Check 7: Audit Logging Configuration"

DIAG_SETTINGS=$(az monitor diagnostic-settings list --resource "$KV_ID" --query "value | length(@)" -o tsv 2>/dev/null || echo "0")

if [[ "$DIAG_SETTINGS" -gt 0 ]]; then
  print_success "Diagnostic settings are configured ($DIAG_SETTINGS setting(s))"

  if [[ "$VERBOSE" == "true" ]]; then
    print_info "Diagnostic settings:"
    az monitor diagnostic-settings list --resource "$KV_ID" --query "value[].name" -o tsv 2>/dev/null | while read -r setting; do
      echo "    - $setting"
    done
  fi
else
  print_warning "No diagnostic settings configured"
  print_info "For HIPAA compliance, enable audit logging"
  print_info "See: infra/modules/deployment-keyvault.bicep for configuration"
fi

echo ""

# =============================================================================
# Summary
# =============================================================================

print_header "Validation Summary"

if [[ $VALIDATION_FAILED -eq 0 ]]; then
  print_success "All validation checks passed!"
  echo ""
  print_info "This Key Vault is properly configured for GitHub Actions access"
  print_info ""
  print_info "Next steps:"
  print_info "  1. Update GitHub workflows to retrieve secrets from this Key Vault"
  print_info "  2. Test workflow deployment with: workflow_dispatch"
  print_info "  3. Monitor for successful secret retrieval in workflow logs"
  echo ""
  exit 0
else
  print_error "Validation failed - see errors above"
  echo ""
  print_info "Common issues and solutions:"
  print_info "  1. Permission denied → Grant 'Key Vault Secrets User' role"
  print_info "  2. Network access → Enable public access or configure private endpoint"
  print_info "  3. Secret not found → Run setup-deployment-keyvault.sh to create secrets"
  print_info "  4. Key Vault not found → Check vault name and subscription"
  echo ""
  print_info "For detailed guidance, see: docs/SECRETS-MIGRATION-GUIDE.md"
  echo ""
  exit 1
fi
