#!/bin/bash
# =============================================================================
# Ensure App Registration Script
# =============================================================================
# Purpose: Create or verify Azure AD app registration for Cloud Health Office
# Usage: ./ensure-app-registration.sh <app-name> <tenant-id>
#
# This script is idempotent - safe to run multiple times
# Creates app registration if it doesn't exist, or retrieves existing one
# Configures:
# - Multi-tenant support
# - Web redirect URIs
# - API permissions (Microsoft Graph)
# - Federated credentials for GitHub Actions OIDC
#
# Returns: Application (Client) ID
# =============================================================================

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Script parameters
APP_NAME="${1:-cloudhealthoffice-prod}"
TENANT_ID="${2:-}"
GITHUB_REPO="${3:-aurelianware/cloudhealthoffice}"

# Banner
echo -e "${BLUE}=========================================${NC}"
echo -e "${BLUE}Cloud Health Office - App Registration${NC}"
echo -e "${BLUE}=========================================${NC}"
echo ""

# Validate Azure CLI login
if ! az account show &>/dev/null; then
    echo -e "${RED}❌ Error: Not logged in to Azure${NC}"
    echo "Please run: az login"
    exit 1
fi

# Get tenant ID if not provided
if [ -z "$TENANT_ID" ]; then
    TENANT_ID=$(az account show --query tenantId -o tsv)
    echo -e "${BLUE}Using current tenant: ${TENANT_ID}${NC}"
fi

echo "App Name: $APP_NAME"
echo "Tenant ID: $TENANT_ID"
echo "GitHub Repo: $GITHUB_REPO"
echo ""

# Check if app registration already exists
echo -e "${BLUE}Checking if app registration exists...${NC}"
APP_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv 2>/dev/null || echo "")

if [ -n "$APP_ID" ]; then
    echo -e "${GREEN}✅ App registration already exists${NC}"
    echo "App Name: $APP_NAME"
    echo "Application ID: $APP_ID"
    
    # Verify it's configured correctly
    echo ""
    echo -e "${BLUE}Verifying configuration...${NC}"
    
    # Check sign-in audience (multi-tenant)
    AUDIENCE=$(az ad app show --id "$APP_ID" --query "signInAudience" -o tsv)
    if [ "$AUDIENCE" != "AzureADMultipleOrgs" ] && [ "$AUDIENCE" != "AzureADandPersonalMicrosoftAccount" ]; then
        echo -e "${YELLOW}⚠️  Updating sign-in audience to multi-tenant${NC}"
        az ad app update --id "$APP_ID" --sign-in-audience "AzureADMultipleOrgs"
    else
        echo -e "${GREEN}✅ Multi-tenant configuration verified${NC}"
    fi
else
    echo -e "${YELLOW}⚠️  App registration not found - creating new one${NC}"
    
    # Create app registration with multi-tenant support
    echo -e "${BLUE}Creating app registration: $APP_NAME${NC}"
    
    APP_ID=$(az ad app create \
        --display-name "$APP_NAME" \
        --sign-in-audience "AzureADMultipleOrgs" \
        --query "appId" -o tsv)
    
    if [ -z "$APP_ID" ]; then
        echo -e "${RED}❌ Failed to create app registration${NC}"
        exit 1
    fi
    
    echo -e "${GREEN}✅ App registration created${NC}"
    echo "Application ID: $APP_ID"
    
    # Wait for app to be fully created
    echo "Waiting for app registration to propagate..."
    sleep 5
fi

# Configure redirect URIs for Static Web Apps
echo ""
echo -e "${BLUE}Configuring redirect URIs...${NC}"

# Get app object ID
APP_OBJECT_ID=$(az ad app show --id "$APP_ID" --query "id" -o tsv)

# Define redirect URIs
REDIRECT_URIS='["https://cloudhealthoffice.com/.auth/login/aad/callback","https://kind-wave-053ff9e1e.azurestaticapps.net/.auth/login/aad/callback","http://localhost:3000/.auth/login/aad/callback"]'

# Update web redirect URIs
az ad app update --id "$APP_ID" \
    --web-redirect-uris ${REDIRECT_URIS}

echo -e "${GREEN}✅ Redirect URIs configured${NC}"

# Configure API permissions (Microsoft Graph)
echo ""
echo -e "${BLUE}Configuring API permissions...${NC}"

# Microsoft Graph API ID
GRAPH_API_ID="00000003-0000-0000-c000-000000000000"

# Required permissions:
# - User.Read (delegated): Sign in and read user profile
# - openid, profile, email (delegated): OpenID Connect scopes
REQUIRED_PERMISSIONS='[
  {
    "resourceAppId": "00000003-0000-0000-c000-000000000000",
    "resourceAccess": [
      {
        "id": "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
        "type": "Scope"
      },
      {
        "id": "37f7f235-527c-4136-accd-4a02d197296e",
        "type": "Scope"
      },
      {
        "id": "14dad69e-099b-42c9-810b-d002981feec1",
        "type": "Scope"
      },
      {
        "id": "64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0",
        "type": "Scope"
      }
    ]
  }
]'

az ad app update --id "$APP_ID" \
    --required-resource-accesses "$REQUIRED_PERMISSIONS"

echo -e "${GREEN}✅ API permissions configured${NC}"

# Configure federated credentials for GitHub Actions OIDC
echo ""
echo -e "${BLUE}Configuring federated credentials for GitHub Actions...${NC}"

# Define federated credential for main branch
FEDERATED_CRED_NAME="github-actions-main"
FEDERATED_SUBJECT="repo:${GITHUB_REPO}:ref:refs/heads/main"

# Check if federated credential already exists
EXISTING_CRED=$(az ad app federated-credential list --id "$APP_ID" \
    --query "[?name=='${FEDERATED_CRED_NAME}'].name" -o tsv 2>/dev/null || echo "")

if [ -n "$EXISTING_CRED" ]; then
    echo -e "${GREEN}✅ Federated credential already exists: $FEDERATED_CRED_NAME${NC}"
else
    echo "Creating federated credential: $FEDERATED_CRED_NAME"
    
    az ad app federated-credential create \
        --id "$APP_ID" \
        --parameters "{
            \"name\": \"${FEDERATED_CRED_NAME}\",
            \"issuer\": \"https://token.actions.githubusercontent.com\",
            \"subject\": \"${FEDERATED_SUBJECT}\",
            \"audiences\": [\"api://AzureADTokenExchange\"],
            \"description\": \"GitHub Actions OIDC for main branch\"
        }"
    
    echo -e "${GREEN}✅ Federated credential created${NC}"
fi

# Add federated credential for PR and feature branches (optional)
FEDERATED_CRED_PR_NAME="github-actions-pr"
FEDERATED_SUBJECT_PR="repo:${GITHUB_REPO}:pull_request"

EXISTING_CRED_PR=$(az ad app federated-credential list --id "$APP_ID" \
    --query "[?name=='${FEDERATED_CRED_PR_NAME}'].name" -o tsv 2>/dev/null || echo "")

if [ -z "$EXISTING_CRED_PR" ]; then
    echo "Creating federated credential for pull requests..."
    
    az ad app federated-credential create \
        --id "$APP_ID" \
        --parameters "{
            \"name\": \"${FEDERATED_CRED_PR_NAME}\",
            \"issuer\": \"https://token.actions.githubusercontent.com\",
            \"subject\": \"${FEDERATED_SUBJECT_PR}\",
            \"audiences\": [\"api://AzureADTokenExchange\"],
            \"description\": \"GitHub Actions OIDC for pull requests\"
        }"
    
    echo -e "${GREEN}✅ Federated credential for PR created${NC}"
fi

# Summary
echo ""
echo -e "${GREEN}=========================================${NC}"
echo -e "${GREEN}App Registration Setup Complete${NC}"
echo -e "${GREEN}=========================================${NC}"
echo ""
echo "App Name: $APP_NAME"
echo "Application (Client) ID: $APP_ID"
echo "Object ID: $APP_OBJECT_ID"
echo "Tenant ID: $TENANT_ID"
echo "Sign-in Audience: Multi-tenant (AzureADMultipleOrgs)"
echo ""
echo -e "${BLUE}Next Steps:${NC}"
echo "1. Create service principal: ./scripts/ensure-service-principal.sh $APP_ID"
echo "2. Add GitHub secret AZURE_CLIENT_ID: $APP_ID"
echo "3. Add GitHub secret AZURE_TENANT_ID: $TENANT_ID"
echo ""
echo -e "${BLUE}Configured Features:${NC}"
echo "✅ Multi-tenant authentication"
echo "✅ Web redirect URIs for Static Web App"
echo "✅ Microsoft Graph API permissions (User.Read, OpenID)"
echo "✅ GitHub Actions OIDC federated credentials (main branch)"
echo "✅ GitHub Actions OIDC federated credentials (pull requests)"
echo ""

# Output app ID for use in scripts/workflows
echo "$APP_ID"
