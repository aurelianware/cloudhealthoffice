#!/bin/bash
#
# GitHub Secrets and Variables Validation Script
# 
# This script validates that all required GitHub Secrets and Repository Variables
# are configured for the HIPAA Attachments deployment.
#
# Prerequisites:
# - GitHub CLI (gh) installed and authenticated
# - Access to the repository with read permissions
#
# Usage:
#   ./validate-github-secrets.sh [repo-owner] [repo-name]
#
# Examples:
#   ./validate-github-secrets.sh
#   ./validate-github-secrets.sh myorg myrepo
#   REPO_OWNER=myorg REPO_NAME=myrepo ./validate-github-secrets.sh
#

set -euo pipefail

# Allow REPO_OWNER and REPO_NAME to be set via environment variables or command-line arguments
REPO_OWNER="${REPO_OWNER:-${1:-aurelianware}}"
REPO_NAME="${REPO_NAME:-${2:-hipaa-attachments}}"

echo "=========================================="
echo "GitHub Secrets & Variables Validation"
echo "=========================================="
echo ""
echo "Repository: $REPO_OWNER/$REPO_NAME"
echo ""

# Check if gh CLI is installed
if ! command -v gh &> /dev/null; then
    echo "❌ GitHub CLI (gh) is not installed"
    echo ""
    echo "Installation instructions:"
    echo "  macOS:   brew install gh"
    echo "  Linux:   https://github.com/cli/cli/blob/trunk/docs/install_linux.md"
    echo "  Windows: https://github.com/cli/cli/releases"
    echo ""
    exit 1
fi

# Check if authenticated
if ! gh auth status &> /dev/null; then
    echo "❌ Not authenticated with GitHub CLI"
    echo ""
    echo "Run: gh auth login"
    echo ""
    exit 1
fi

echo "✅ GitHub CLI is installed and authenticated"
echo ""

# Track overall validation status
OVERALL_STATUS=0

echo "=========================================="
echo "Checking DEV Environment Secrets"
echo "=========================================="
echo ""
echo "Note: Checking environment-scoped secrets for DEV environment"
echo ""

REQUIRED_DEV_SECRETS=("AZURE_CLIENT_ID" "AZURE_TENANT_ID" "AZURE_SUBSCRIPTION_ID")
DEV_FAILED=0

for secret in "${REQUIRED_DEV_SECRETS[@]}"; do
    # Check environment-scoped secrets first (preferred)
    if gh secret list -R "$REPO_OWNER/$REPO_NAME" --env DEV 2>/dev/null | grep -q "^$secret"; then
        echo "✅ $secret (environment: DEV)"
    # Fallback to repository-level secrets
    elif gh secret list -R "$REPO_OWNER/$REPO_NAME" 2>/dev/null | grep -q "^$secret"; then
        echo "⚠️  $secret (repository-level, but DEV environment preferred)"
    else
        echo "❌ $secret (missing)"
        DEV_FAILED=1
        OVERALL_STATUS=1
    fi
done

if [ $DEV_FAILED -eq 0 ]; then
    echo ""
    echo "✅ All DEV secrets are configured"
else
    echo ""
    echo "❌ Some DEV secrets are missing"
fi

echo ""
echo "=========================================="
echo "Checking UAT Environment Secrets"
echo "=========================================="
echo ""
echo "Note: Checking environment-scoped secrets for UAT environment"
echo ""

REQUIRED_UAT_SECRETS=("AZURE_CLIENT_ID_UAT" "AZURE_TENANT_ID_UAT" "AZURE_SUBSCRIPTION_ID_UAT")
UAT_FAILED=0

for secret in "${REQUIRED_UAT_SECRETS[@]}"; do
    # Check environment-scoped secrets first (preferred)
    if gh secret list -R "$REPO_OWNER/$REPO_NAME" --env UAT 2>/dev/null | grep -q "^$secret"; then
        echo "✅ $secret (environment: UAT)"
    # Fallback to repository-level secrets
    elif gh secret list -R "$REPO_OWNER/$REPO_NAME" 2>/dev/null | grep -q "^$secret"; then
        echo "⚠️  $secret (repository-level, but UAT environment preferred)"
    else
        echo "❌ $secret (missing)"
        UAT_FAILED=1
        OVERALL_STATUS=1
    fi
done

if [ $UAT_FAILED -eq 0 ]; then
    echo ""
    echo "✅ All UAT secrets are configured"
else
    echo ""
    echo "❌ Some UAT secrets are missing"
fi

echo ""
echo "=========================================="
echo "Checking PROD Environment Secrets"
echo "=========================================="
echo ""
echo "Note: Checking environment-scoped secrets for PROD environment"
echo ""

REQUIRED_PROD_SECRETS=("AZURE_CLIENT_ID" "AZURE_TENANT_ID" "AZURE_SUBSCRIPTION_ID" "SFTP_HOST" "SFTP_USERNAME" "SFTP_PASSWORD")
PROD_FAILED=0

for secret in "${REQUIRED_PROD_SECRETS[@]}"; do
    # Check environment-scoped secrets first (preferred)
    if gh secret list -R "$REPO_OWNER/$REPO_NAME" --env PROD 2>/dev/null | grep -q "^$secret"; then
        echo "✅ $secret (environment: PROD)"
    # Fallback to repository-level secrets
    elif gh secret list -R "$REPO_OWNER/$REPO_NAME" 2>/dev/null | grep -q "^$secret"; then
        echo "⚠️  $secret (repository-level, but PROD environment preferred)"
    else
        echo "❌ $secret (missing)"
        PROD_FAILED=1
        OVERALL_STATUS=1
    fi
done

if [ $PROD_FAILED -eq 0 ]; then
    echo ""
    echo "✅ All PROD secrets are configured"
else
    echo ""
    echo "❌ Some PROD secrets are missing"
fi

echo ""
echo "=========================================="
echo "Checking Repository Variables"
echo "=========================================="

REQUIRED_VARS=("AZURE_RG_NAME" "AZURE_LOCATION" "AZURE_CONNECTOR_LOCATION" "BASE_NAME" "IA_NAME" "SERVICE_BUS_NAME" "STORAGE_SKU")
VARS_FAILED=0

for var in "${REQUIRED_VARS[@]}"; do
    if gh variable list -R "$REPO_OWNER/$REPO_NAME" | grep -q "^$var"; then
        VALUE=$(gh variable list -R "$REPO_OWNER/$REPO_NAME" | grep "^$var" | awk '{print $2}')
        echo "✅ $var = $VALUE"
    else
        echo "❌ $var (missing)"
        VARS_FAILED=1
        OVERALL_STATUS=1
    fi
done

if [ $VARS_FAILED -eq 0 ]; then
    echo ""
    echo "✅ All repository variables are configured"
else
    echo ""
    echo "❌ Some repository variables are missing"
fi

echo ""
echo "=========================================="
echo "Validation Summary"
echo "=========================================="
echo ""
echo "ℹ️  About GitHub Secrets Scopes:"
echo "   - Environment-scoped secrets (preferred): Set per environment (DEV/UAT/PROD)"
echo "   - Repository-level secrets: Shared across all workflows"
echo "   - Environment secrets override repository secrets with the same name"
echo ""
echo "   This script checks environment-scoped secrets first, then falls back to"
echo "   repository-level secrets. For best security, use environment-scoped secrets."
echo ""

if [ $OVERALL_STATUS -eq 0 ]; then
    echo "🎉 SUCCESS: All secrets and variables are configured!"
    echo ""
    echo "Your repository is ready for deployment."
    echo ""
    echo "Next steps:"
    echo "  1. Review DEPLOYMENT.md for deployment procedures"
    echo "  2. Test a DEV deployment to verify configuration"
    echo "  3. Monitor Application Insights for any issues"
else
    echo "❌ FAILURE: Some secrets or variables are missing!"
    echo ""
    echo "Please refer to DEPLOYMENT-SECRETS-SETUP.md for configuration instructions:"
    echo "  https://github.com/$REPO_OWNER/$REPO_NAME/blob/main/DEPLOYMENT-SECRETS-SETUP.md"
    echo ""
    echo "Quick setup guide:"
    echo "  1. Configure Azure AD applications with OIDC (see setup guide)"
    echo "  2. Add secrets to GitHub: Settings → Secrets and variables → Actions"
    echo "  3. Add variables to GitHub: Settings → Secrets and variables → Actions → Variables"
    echo "  4. Re-run this validation script"
    echo ""
fi

exit $OVERALL_STATUS
