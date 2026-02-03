#!/bin/bash
#
# validate-deployment-auth.sh
# Validates Azure federated credential setup for GitHub Actions deployment
#
# Usage:
#   ./scripts/validate-deployment-auth.sh
#   ./scripts/validate-deployment-auth.sh --app-id <client-id>
#   ./scripts/validate-deployment-auth.sh --help
#
# Exit codes:
#   0 - All validations passed
#   1 - One or more validations failed
#   2 - Prerequisites not met (missing tools)

set -euo pipefail

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Symbols
CHECK="✅"
CROSS="❌"
WARN="⚠️"
INFO="ℹ️"

# Script configuration
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Auto-detect repository info from git remote or use environment variables
if [[ -n "${GITHUB_REPOSITORY:-}" ]]; then
    # Running in GitHub Actions - use GITHUB_REPOSITORY (format: owner/repo)
    REPO_OWNER="${GITHUB_REPOSITORY%%/*}"
    REPO_NAME="${GITHUB_REPOSITORY##*/}"
elif git remote get-url origin &>/dev/null; then
    # Extract from git remote URL
    REMOTE_URL=$(git remote get-url origin)
    # Handle both HTTPS and SSH URLs
    if [[ "$REMOTE_URL" == *"github.com"* ]]; then
        # Remove .git suffix if present
        REMOTE_URL="${REMOTE_URL%.git}"
        # Extract owner/repo from URL
        if [[ "$REMOTE_URL" == git@* ]]; then
            # SSH format: git@github.com:owner/repo
            REPO_PATH="${REMOTE_URL#*:}"
        else
            # HTTPS format: https://github.com/owner/repo
            REPO_PATH="${REMOTE_URL#*github.com/}"
        fi
        REPO_OWNER="${REPO_PATH%%/*}"
        REPO_NAME="${REPO_PATH##*/}"
    fi
fi

# Fallback to defaults if auto-detection failed
REPO_OWNER="${REPO_OWNER:-aurelianware}"
REPO_NAME="${REPO_NAME:-cloudhealthoffice}"
EXPECTED_BRANCH="main"

# Variables
APP_ID=""
VALIDATION_FAILED=0

# Usage information
usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Validates Azure federated credential setup for GitHub Actions deployment.

OPTIONS:
    --app-id <id>       Azure Application (Client) ID to validate
                        If not provided, will attempt to read from AZURE_CLIENT_ID env var
    --help              Show this help message

ENVIRONMENT VARIABLES:
    AZURE_CLIENT_ID             Azure Application (Client) ID
    AZURE_TENANT_ID             Azure Directory (Tenant) ID
    AZURE_SUBSCRIPTION_ID       Azure Subscription ID
    AZURE_RG_NAME              Azure Resource Group name
    BASE_NAME                   Resource naming prefix

EXAMPLES:
    # Validate using environment variables
    export AZURE_CLIENT_ID="12345678-1234-1234-1234-123456789abc"
    $0

    # Validate specific application
    $0 --app-id "12345678-1234-1234-1234-123456789abc"

    # Validate in CI/CD (reads from GitHub Actions secrets)
    $0

EOF
}

# Print section header
print_header() {
    echo ""
    echo -e "${BLUE}========================================${NC}"
    echo -e "${BLUE}$1${NC}"
    echo -e "${BLUE}========================================${NC}"
    echo ""
}

# Print success message
print_success() {
    echo -e "${GREEN}${CHECK} $1${NC}"
}

# Print error message
print_error() {
    echo -e "${RED}${CROSS} $1${NC}"
    VALIDATION_FAILED=1
}

# Print warning message
print_warning() {
    echo -e "${YELLOW}${WARN} $1${NC}"
}

# Print info message
print_info() {
    echo -e "${BLUE}${INFO} $1${NC}"
}

# Parse command line arguments
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --app-id)
                APP_ID="$2"
                shift 2
                ;;
            --help)
                usage
                exit 0
                ;;
            *)
                echo "Unknown option: $1"
                usage
                exit 1
                ;;
        esac
    done
}

# Check prerequisites
check_prerequisites() {
    print_header "Checking Prerequisites"

    local prereq_failed=0

    # Check Azure CLI
    if command -v az &> /dev/null; then
        local az_version=$(az --version | head -n 1 | awk '{print $2}')
        print_success "Azure CLI installed (version $az_version)"
    else
        print_error "Azure CLI not found. Install from: https://docs.microsoft.com/cli/azure/install-azure-cli"
        prereq_failed=1
    fi

    # Check GitHub CLI (optional but helpful)
    if command -v gh &> /dev/null; then
        local gh_version=$(gh --version | head -n 1 | awk '{print $3}')
        print_success "GitHub CLI installed (version $gh_version)"
    else
        print_warning "GitHub CLI not found (optional). Install from: https://cli.github.com/"
    fi

    # Check jq (required for JSON parsing of Azure CLI output)
    if command -v jq &> /dev/null; then
        print_success "jq installed"
    else
        print_error "jq not found. Install jq to enable required JSON parsing: https://stedolan.github.io/jq/download/"
        prereq_failed=1
    fi

    if [[ $prereq_failed -eq 1 ]]; then
        echo ""
        print_error "Prerequisites not met. Please install required tools."
        exit 2
    fi

    echo ""
}

# Check if logged into Azure
check_azure_login() {
    print_header "Checking Azure Authentication"

    if az account show &> /dev/null; then
        local subscription_name=$(az account show --query name -o tsv)
        local subscription_id=$(az account show --query id -o tsv)
        local tenant_id=$(az account show --query tenantId -o tsv)
        
        print_success "Logged into Azure"
        print_info "Subscription: $subscription_name"
        print_info "Subscription ID: $subscription_id"
        print_info "Tenant ID: $tenant_id"
    else
        print_error "Not logged into Azure. Run: az login"
        exit 1
    fi

    echo ""
}

# Validate GitHub secrets (environment variables)
validate_github_secrets() {
    print_header "Validating GitHub Secrets (Environment Variables)"

    local secrets_valid=1

    # Check AZURE_CLIENT_ID
    if [[ -n "${AZURE_CLIENT_ID:-}" ]]; then
        print_success "AZURE_CLIENT_ID is set"
        # Use this if --app-id not provided
        if [[ -z "$APP_ID" ]]; then
            APP_ID="$AZURE_CLIENT_ID"
        fi
    else
        print_error "AZURE_CLIENT_ID is not set"
        secrets_valid=0
    fi

    # Check AZURE_TENANT_ID
    if [[ -n "${AZURE_TENANT_ID:-}" ]]; then
        print_success "AZURE_TENANT_ID is set"
    else
        print_error "AZURE_TENANT_ID is not set"
        secrets_valid=0
    fi

    # Check AZURE_SUBSCRIPTION_ID
    if [[ -n "${AZURE_SUBSCRIPTION_ID:-}" ]]; then
        print_success "AZURE_SUBSCRIPTION_ID is set"
    else
        print_error "AZURE_SUBSCRIPTION_ID is not set"
        secrets_valid=0
    fi

    if [[ $secrets_valid -eq 0 ]]; then
        echo ""
        print_warning "Required GitHub secrets not found in environment variables."
        print_info "If running locally, set them manually:"
        echo "  export AZURE_CLIENT_ID='<your-client-id>'"
        echo "  export AZURE_TENANT_ID='<your-tenant-id>'"
        echo "  export AZURE_SUBSCRIPTION_ID='<your-subscription-id>'"
        echo ""
        print_info "If running in GitHub Actions, ensure secrets are configured:"
        echo "  Settings → Secrets and variables → Actions → Repository secrets"
    fi

    echo ""
}

# Validate GitHub variables
validate_github_variables() {
    print_header "Validating GitHub Variables (Environment Variables)"

    # Check AZURE_RG_NAME
    if [[ -n "${AZURE_RG_NAME:-}" ]]; then
        print_success "AZURE_RG_NAME is set: ${AZURE_RG_NAME}"
    else
        print_warning "AZURE_RG_NAME is not set (required for deployment)"
    fi

    # Check BASE_NAME
    if [[ -n "${BASE_NAME:-}" ]]; then
        print_success "BASE_NAME is set: ${BASE_NAME}"
    else
        print_warning "BASE_NAME is not set (required for deployment)"
    fi

    echo ""
}

# Validate Azure AD Application
validate_azure_app() {
    print_header "Validating Azure AD Application"

    if [[ -z "$APP_ID" ]]; then
        print_error "Application ID not provided. Use --app-id or set AZURE_CLIENT_ID"
        return
    fi

    print_info "Application ID: $APP_ID"
    echo ""

    # Check if application exists
    if az ad app show --id "$APP_ID" &> /dev/null; then
        local app_name=$(az ad app show --id "$APP_ID" --query displayName -o tsv)
        print_success "Application exists: $app_name"
    else
        print_error "Application not found with ID: $APP_ID"
        print_info "Create application with:"
        echo "  az ad app create --display-name '${REPO_NAME}-static-site-deployment'"
        return
    fi

    # Check if service principal exists
    if az ad sp show --id "$APP_ID" &> /dev/null; then
        print_success "Service principal exists"
    else
        print_error "Service principal not found for application"
        print_info "Create service principal with:"
        echo "  az ad sp create --id '$APP_ID'"
        return
    fi

    echo ""
}

# Validate federated credentials
validate_federated_credentials() {
    print_header "Validating Federated Credentials"

    if [[ -z "$APP_ID" ]]; then
        print_error "Application ID not available. Skipping federated credential check."
        return
    fi

    # List federated credentials
    local creds_json=$(az ad app federated-credential list --id "$APP_ID" 2>/dev/null || echo "[]")
    local creds_count=$(echo "$creds_json" | jq 'length' 2>/dev/null || echo "0")

    if [[ "$creds_count" -eq 0 ]]; then
        print_error "No federated credentials found"
        echo ""
        print_info "Create federated credential with:"
        cat << EOF
  az ad app federated-credential create \\
    --id "\$APP_ID" \\
    --parameters '{
      "name": "${REPO_NAME}-main-branch",
      "issuer": "https://token.actions.githubusercontent.com",
      "subject": "repo:${REPO_OWNER}/${REPO_NAME}:ref:refs/heads/main",
      "audiences": ["api://AzureADTokenExchange"]
    }'
EOF
        echo ""
        return
    fi

    print_success "Found $creds_count federated credential(s)"
    echo ""

    # Expected subject for main branch
    local expected_subject="repo:$REPO_OWNER/$REPO_NAME:ref:refs/heads/$EXPECTED_BRANCH"
    local expected_issuer="https://token.actions.githubusercontent.com"
    local expected_audience="api://AzureADTokenExchange"

    # Check each credential
    local main_branch_found=false
    local i=0
    while [[ $i -lt $creds_count ]]; do
        local cred_name=$(echo "$creds_json" | jq -r ".[$i].name")
        local cred_subject=$(echo "$creds_json" | jq -r ".[$i].subject")
        local cred_issuer=$(echo "$creds_json" | jq -r ".[$i].issuer")
        local cred_audiences=$(echo "$creds_json" | jq -r ".[$i].audiences[]")

        echo "Credential: $cred_name"
        echo "  Subject: $cred_subject"
        echo "  Issuer: $cred_issuer"
        echo "  Audiences: $cred_audiences"

        # Validate main branch credential
        if [[ "$cred_subject" == "$expected_subject" ]]; then
            main_branch_found=true
            print_success "  ✓ Main branch credential found"

            # Check issuer
            if [[ "$cred_issuer" == "$expected_issuer" ]]; then
                print_success "  ✓ Issuer is correct"
            else
                print_error "  ✗ Issuer is incorrect. Expected: $expected_issuer"
            fi

            # Check audience
            if [[ "$cred_audiences" == "$expected_audience" ]]; then
                print_success "  ✓ Audience is correct"
            else
                print_error "  ✗ Audience is incorrect. Expected: $expected_audience"
            fi
        fi

        echo ""
        ((i++))
    done

    # Summary
    if [[ "$main_branch_found" == true ]]; then
        print_success "Main branch federated credential is properly configured"
    else
        print_error "Main branch federated credential not found"
        print_info "Expected subject: $expected_subject"
        echo ""
        print_info "Current credentials do not match the main branch pattern."
        print_info "Add the correct credential or update existing one."
    fi

    echo ""
}

# Validate Azure role assignments
validate_role_assignments() {
    print_header "Validating Azure Role Assignments"

    if [[ -z "$APP_ID" ]]; then
        print_error "Application ID not available. Skipping role assignment check."
        return
    fi

    # Get role assignments
    local roles_json=$(az role assignment list --assignee "$APP_ID" 2>/dev/null || echo "[]")
    local roles_count=$(echo "$roles_json" | jq 'length' 2>/dev/null || echo "0")

    if [[ "$roles_count" -eq 0 ]]; then
        print_error "No role assignments found"
        echo ""
        print_info "Assign Contributor role with:"
        echo "  az role assignment create \\"
        echo "    --assignee '$APP_ID' \\"
        echo "    --role 'Contributor' \\"
        echo "    --scope '/subscriptions/\$SUBSCRIPTION_ID/resourceGroups/\$RESOURCE_GROUP'"
        echo ""
        return
    fi

    print_success "Found $roles_count role assignment(s)"
    echo ""

    # Check for Contributor role
    local has_contributor=false
    local i=0
    while [[ $i -lt $roles_count ]]; do
        local role_name=$(echo "$roles_json" | jq -r ".[$i].roleDefinitionName")
        local scope=$(echo "$roles_json" | jq -r ".[$i].scope")

        echo "Role: $role_name"
        echo "  Scope: $scope"

        if [[ "$role_name" == "Contributor" ]] || [[ "$role_name" == "Owner" ]]; then
            has_contributor=true
            print_success "  ✓ Has deployment permissions"
        fi

        echo ""
        ((i++))
    done

    # Summary
    if [[ "$has_contributor" == true ]]; then
        print_success "Service principal has sufficient permissions for deployment"
    else
        print_warning "Service principal may not have sufficient permissions"
        print_info "Recommended: Contributor role at resource group or subscription level"
    fi

    echo ""
}

# Validate workflow file
validate_workflow_file() {
    print_header "Validating Workflow File"

    local workflow_file="$REPO_ROOT/.github/workflows/deploy-static-site.yml"

    if [[ ! -f "$workflow_file" ]]; then
        print_error "Workflow file not found: $workflow_file"
        return
    fi

    print_success "Workflow file exists"

    # Check for required permissions
    if grep -q "id-token: write" "$workflow_file"; then
        print_success "Workflow has 'id-token: write' permission (required for OIDC)"
    else
        print_error "Workflow missing 'id-token: write' permission"
        print_info "Add to workflow file under 'permissions:' section:"
        echo "  permissions:"
        echo "    id-token: write"
        echo "    contents: read"
    fi

    # Check for Azure login action
    if grep -q "azure/login@" "$workflow_file"; then
        print_success "Workflow uses azure/login action"
    else
        print_warning "Workflow may not be using azure/login action"
    fi

    echo ""
}

# Test OIDC token acquisition (if running in GitHub Actions)
test_oidc_token() {
    print_header "Testing OIDC Token Acquisition"

    if [[ -z "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" ]]; then
        print_info "Not running in GitHub Actions environment"
        print_info "OIDC token test only works in GitHub Actions workflows"
        echo ""
        return
    fi

    print_info "Running in GitHub Actions environment"

    # Check if token request URL is available
    if [[ -n "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" ]] && [[ -n "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:-}" ]]; then
        print_success "OIDC token request environment available"
        
        # We can't actually test token exchange without making the Azure API call
        # which requires the workflow to have proper permissions
        print_info "Token exchange will be tested during actual workflow execution"
    else
        print_warning "OIDC token request environment not fully configured"
    fi

    echo ""
}

# Print summary
print_summary() {
    print_header "Validation Summary"

    if [[ $VALIDATION_FAILED -eq 0 ]]; then
        print_success "All validations passed!"
        echo ""
        print_info "Your federated credential setup appears to be correctly configured."
        print_info "You can proceed with GitHub Actions deployment."
    else
        print_error "Some validations failed."
        echo ""
        print_info "Review the errors above and consult the documentation:"
        echo "  docs/FEDERATED-CREDENTIALS-SETUP.md"
        echo ""
        print_info "For troubleshooting help, see:"
        echo "  https://github.com/$REPO_OWNER/$REPO_NAME/blob/main/docs/FEDERATED-CREDENTIALS-SETUP.md#troubleshooting-common-issues"
    fi

    echo ""
}

# Main function
main() {
    parse_args "$@"

    echo ""
    echo "================================================"
    echo "Azure Federated Credential Validation"
    echo "================================================"
    echo "Repository: $REPO_OWNER/$REPO_NAME"
    echo "Branch: $EXPECTED_BRANCH"
    echo "================================================"

    check_prerequisites
    check_azure_login
    validate_github_secrets
    validate_github_variables
    validate_azure_app
    validate_federated_credentials
    validate_role_assignments
    validate_workflow_file
    test_oidc_token
    print_summary

    exit $VALIDATION_FAILED
}

# Run main function
main "$@"
