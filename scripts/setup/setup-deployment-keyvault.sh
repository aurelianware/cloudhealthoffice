#!/bin/bash
# =============================================================================
# Setup Deployment Key Vault Script
# =============================================================================
# Purpose: Populate Azure Key Vault with deployment secrets
# Usage: ./setup-deployment-keyvault.sh --vault-name <name> --environment <env>
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
ENVIRONMENT="PROD"
INTERACTIVE=true
DRY_RUN=false

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

Populate Azure Key Vault with deployment secrets for Cloud Health Office.

OPTIONS:
  -v, --vault-name NAME        Key Vault name (required)
  -e, --environment ENV        Environment: DEV, UAT, PROD (default: PROD)
  -i, --interactive            Interactive mode with prompts (default)
  -n, --non-interactive        Non-interactive mode (requires --sftp-* options)
  -d, --dry-run                Show what would be done without making changes
  --sftp-host HOST             SFTP hostname (non-interactive mode)
  --sftp-username USER         SFTP username (non-interactive mode)
  --sftp-password PASS         SFTP password (non-interactive mode)
  -h, --help                   Show this help message

EXAMPLES:
  # Interactive mode (prompts for secrets)
  $0 --vault-name cloud-health-office-prod-deploy-kv --environment PROD

  # Non-interactive mode (provide all secrets as arguments)
  $0 --vault-name cloud-health-office-dev-deploy-kv \\
     --environment DEV \\
     --non-interactive \\
     --sftp-host sftp.example.com \\
     --sftp-username user123 \\
     --sftp-password 'SecureP@ssw0rd!'

  # Dry run to preview changes
  $0 --vault-name cloud-health-office-uat-deploy-kv --dry-run

SECURITY NOTES:
  - Secrets are never logged or echoed to console
  - Non-interactive mode is for automation only - avoid hardcoding passwords
  - Consider using Azure Key Vault references instead of command-line arguments
  - Interactive mode uses secure input (passwords are hidden)

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

SFTP_HOST=""
SFTP_USERNAME=""
SFTP_PASSWORD=""

while [[ $# -gt 0 ]]; do
  case $1 in
  -v | --vault-name)
    VAULT_NAME="$2"
    shift 2
    ;;
  -e | --environment)
    ENVIRONMENT="$2"
    shift 2
    ;;
  -i | --interactive)
    INTERACTIVE=true
    shift
    ;;
  -n | --non-interactive)
    INTERACTIVE=false
    shift
    ;;
  -d | --dry-run)
    DRY_RUN=true
    shift
    ;;
  --sftp-host)
    SFTP_HOST="$2"
    shift 2
    ;;
  --sftp-username)
    SFTP_USERNAME="$2"
    shift 2
    ;;
  --sftp-password)
    SFTP_PASSWORD="$2"
    shift 2
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

# Validate environment
if [[ ! "$ENVIRONMENT" =~ ^(DEV|UAT|PROD)$ ]]; then
  print_error "Environment must be DEV, UAT, or PROD"
  exit 1
fi

# =============================================================================
# Main Script
# =============================================================================

print_header "Deployment Key Vault Setup"

print_info "Vault Name: $VAULT_NAME"
print_info "Environment: $ENVIRONMENT"
print_info "Dry Run: $DRY_RUN"
echo ""

# Step 1: Validate Azure CLI authentication
print_header "Step 1: Validating Azure CLI Authentication"

if ! az account show &>/dev/null; then
  print_error "Azure CLI is not authenticated. Please run 'az login' first."
  exit 1
fi

CURRENT_ACCOUNT=$(az account show --query "name" -o tsv)
CURRENT_SUB=$(az account show --query "id" -o tsv)
print_success "Authenticated as: $CURRENT_ACCOUNT"
print_info "Subscription ID: $CURRENT_SUB"
echo ""

# Step 2: Verify Key Vault exists and is accessible
print_header "Step 2: Verifying Key Vault Access"

if ! az keyvault show --name "$VAULT_NAME" &>/dev/null; then
  print_error "Key Vault '$VAULT_NAME' not found or not accessible"
  print_info "Please ensure:"
  print_info "  1. Key Vault exists in the current subscription"
  print_info "  2. You have permissions to access the Key Vault"
  print_info "  3. Key Vault name is spelled correctly"
  exit 1
fi

print_success "Key Vault '$VAULT_NAME' found and accessible"

# Check RBAC permissions
KV_ID=$(az keyvault show --name "$VAULT_NAME" --query "id" -o tsv)
print_info "Key Vault ID: $KV_ID"
echo ""

# Step 3: Gather secrets (interactive or from arguments)
print_header "Step 3: Gathering Deployment Secrets"

if [[ "$INTERACTIVE" == "true" ]]; then
  print_info "Interactive mode: You will be prompted for each secret"
  print_warning "Secrets will not be echoed to the console"
  echo ""

  # SFTP Host
  read -p "Enter SFTP Host (e.g., sftp.clearinghouse.example.com): " SFTP_HOST
  if [[ -z "$SFTP_HOST" ]]; then
    print_error "SFTP Host cannot be empty"
    exit 1
  fi

  # SFTP Username
  read -p "Enter SFTP Username: " SFTP_USERNAME
  if [[ -z "$SFTP_USERNAME" ]]; then
    print_error "SFTP Username cannot be empty"
    exit 1
  fi

  # SFTP Password (secure input)
  read -s -p "Enter SFTP Password: " SFTP_PASSWORD
  echo ""
  if [[ -z "$SFTP_PASSWORD" ]]; then
    print_error "SFTP Password cannot be empty"
    exit 1
  fi

  print_success "All secrets collected"
else
  print_info "Non-interactive mode: Using provided arguments"

  # Validate all required secrets are provided
  if [[ -z "$SFTP_HOST" || -z "$SFTP_USERNAME" || -z "$SFTP_PASSWORD" ]]; then
    print_error "In non-interactive mode, all secrets must be provided:"
    print_error "  --sftp-host, --sftp-username, --sftp-password"
    print_error "Alternatively, rerun with -i or --interactive to be prompted for missing secrets."
    exit 1
  fi

  print_success "All secrets provided via arguments"
fi

echo ""

# Step 4: Validate secret values
print_header "Step 4: Validating Secret Values"

# Validate SFTP Host format (basic check for hostname or IPv4 address)
if [[ ! "$SFTP_HOST" =~ ^(([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}|([0-9]{1,3}\.){3}[0-9]{1,3})$ ]]; then
  print_warning "SFTP Host format looks unusual: $SFTP_HOST"
  print_warning "Expected format: sftp.example.com or 192.168.1.100"
fi

# Validate SFTP Username (basic check)
if [[ ${#SFTP_USERNAME} -lt 3 ]]; then
  print_warning "SFTP Username is very short: ${#SFTP_USERNAME} characters"
fi

# Validate SFTP Password strength (basic check)
if [[ ${#SFTP_PASSWORD} -lt 8 ]]; then
  print_warning "SFTP Password is weak (less than 8 characters)"
  print_warning "Consider using a stronger password for production"
fi

print_success "Secret validation complete"
echo ""

# Step 5: Create secrets in Key Vault
print_header "Step 5: Creating Secrets in Key Vault"

if [[ "$DRY_RUN" == "true" ]]; then
  print_warning "DRY RUN MODE - No changes will be made"
  echo ""
  print_info "Would create the following secrets:"
  echo "  - sftp-host: ********"
  echo "  - sftp-username: ********"
  echo "  - sftp-password: ********"
  echo ""
else
  # Create sftp-host secret
  print_info "Creating secret: sftp-host"
  if az keyvault secret set \
    --vault-name "$VAULT_NAME" \
    --name sftp-host \
    --value "$SFTP_HOST" \
    --content-type "text/plain" \
    --output none; then
    print_success "Secret 'sftp-host' created successfully"
  else
    print_error "Failed to create secret 'sftp-host'"
    exit 1
  fi

  # Create sftp-username secret
  print_info "Creating secret: sftp-username"
  if az keyvault secret set \
    --vault-name "$VAULT_NAME" \
    --name sftp-username \
    --value "$SFTP_USERNAME" \
    --content-type "text/plain" \
    --output none; then
    print_success "Secret 'sftp-username' created successfully"
  else
    print_error "Failed to create secret 'sftp-username'"
    exit 1
  fi

  # Create sftp-password secret
  print_info "Creating secret: sftp-password"
  if az keyvault secret set \
    --vault-name "$VAULT_NAME" \
    --name sftp-password \
    --value "$SFTP_PASSWORD" \
    --content-type "text/plain" \
    --output none; then
    print_success "Secret 'sftp-password' created successfully"
  else
    print_error "Failed to create secret 'sftp-password'"
    exit 1
  fi
fi

echo ""

# Step 6: Verify secrets were created
print_header "Step 6: Verifying Secret Creation"

if [[ "$DRY_RUN" == "false" ]]; then
  SECRET_COUNT=0

  if az keyvault secret show --vault-name "$VAULT_NAME" --name sftp-host --query "name" -o tsv &>/dev/null; then
    print_success "Secret 'sftp-host' verified"
    ((SECRET_COUNT++))
  else
    print_error "Secret 'sftp-host' not found"
  fi

  if az keyvault secret show --vault-name "$VAULT_NAME" --name sftp-username --query "name" -o tsv &>/dev/null; then
    print_success "Secret 'sftp-username' verified"
    ((SECRET_COUNT++))
  else
    print_error "Secret 'sftp-username' not found"
  fi

  if az keyvault secret show --vault-name "$VAULT_NAME" --name sftp-password --query "name" -o tsv &>/dev/null; then
    print_success "Secret 'sftp-password' verified"
    ((SECRET_COUNT++))
  else
    print_error "Secret 'sftp-password' not found"
  fi

  echo ""
  print_success "Verified $SECRET_COUNT out of 3 secrets"

  if [[ $SECRET_COUNT -ne 3 ]]; then
    print_error "Not all secrets were created successfully"
    exit 1
  fi
else
  print_info "Skipping verification in dry-run mode"
fi

echo ""

# Clear sensitive variables from memory
unset SFTP_HOST
unset SFTP_USERNAME
unset SFTP_PASSWORD

# Summary
print_header "Setup Complete!"

if [[ "$DRY_RUN" == "false" ]]; then
  print_success "All deployment secrets have been created in Key Vault: $VAULT_NAME"
  echo ""
  print_info "Next Steps:"
  echo "  1. Grant GitHub Service Principal 'Key Vault Secrets User' role"
  echo "     az role assignment create \\"
  echo "       --assignee <AZURE_CLIENT_ID> \\"
  echo "       --role 'Key Vault Secrets User' \\"
  echo "       --scope $KV_ID"
  echo ""
  echo "  2. Update GitHub workflows to retrieve secrets from Key Vault"
  echo "     See: docs/SECRETS-MIGRATION-GUIDE.md"
  echo ""
  echo "  3. Test secret retrieval with validate-keyvault-access.sh"
  echo ""
  echo "  4. Remove old GitHub Secrets after successful migration (30-day buffer)"
else
  print_success "Dry run completed - no changes were made"
  echo ""
  print_info "To actually create secrets, run without --dry-run flag"
fi

echo ""
print_success "Setup script finished successfully"
