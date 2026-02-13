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
APP_NAME_PARAM="${1:-}"
TENANT_ID="${2:-}"
GITHUB_REPO_PARAM="${3:-}"
KEY_VAULT_NAME="${4:-}"

# =============================================================================
# Helper Functions for Key Vault Configuration
# =============================================================================

get_or_set_vault_config() {
    local vault_name="$1"
    local secret_name="$2"
    local default_value="$3"
    local description="$4"
    
    # Try to get the value from Key Vault
    local value=$(az keyvault secret show --vault-name "$vault_name" --name "$secret_name" --query "value" -o tsv 2>/dev/null || echo "")
    
    if [ -z "$value" ]; then
        echo -e "${YELLOW}⚠️  Config '$secret_name' not found in Key Vault${NC}"
        echo -e "${BLUE}Setting default value for $description: $default_value${NC}"
        
        # Set the default value in Key Vault
        az keyvault secret set --vault-name "$vault_name" --name "$secret_name" --value "$default_value" --description "$description" >/dev/null 2>&1 || {
            echo -e "${YELLOW}⚠️  Could not set in Key Vault, using default${NC}"
        }
        
        value="$default_value"
    else
        echo -e "${GREEN}✓${NC} Using config from Key Vault: $description"
    fi
    
    echo "$value"
}

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

# Determine Key Vault name if not provided
if [ -z "$KEY_VAULT_NAME" ]; then
    # Try to find a deployment key vault
    KEY_VAULT_NAME=$(az keyvault list --query "[?contains(name, 'deploy-kv')].name | [0]" -o tsv 2>/dev/null || echo "")
    
    if [ -z "$KEY_VAULT_NAME" ]; then
        echo -e "${YELLOW}⚠️  No deployment Key Vault found${NC}"
        echo -e "${YELLOW}   Using hardcoded defaults (will not persist)${NC}"
        USE_VAULT=false
    else
        echo -e "${GREEN}✓${NC} Using Key Vault: $KEY_VAULT_NAME"
        USE_VAULT=true
    fi
else
    USE_VAULT=true
    echo -e "${GREEN}✓${NC} Using Key Vault: $KEY_VAULT_NAME"
fi

# Get configuration values from vault or use defaults
if [ "$USE_VAULT" = true ]; then
    APP_NAME=$(get_or_set_vault_config "$KEY_VAULT_NAME" "app-registration-name" "${APP_NAME_PARAM:-cloudhealthoffice-prod}" "Azure AD App Registration Name")
    GITHUB_REPO=$(get_or_set_vault_config "$KEY_VAULT_NAME" "github-repository" "${GITHUB_REPO_PARAM:-aurelianware/cloudhealthoffice}" "GitHub Repository")
    OIDC_ISSUER=$(get_or_set_vault_config "$KEY_VAULT_NAME" "oidc-issuer" "https://token.actions.githubusercontent.com" "OIDC Issuer for GitHub Actions")
else
    APP_NAME="${APP_NAME_PARAM:-cloudhealthoffice-prod}"
    GITHUB_REPO="${GITHUB_REPO_PARAM:-aurelianware/cloudhealthoffice}"
    OIDC_ISSUER="https://token.actions.githubusercontent.com"
    echo -e "${YELLOW}⚠️  Using hardcoded defaults (no Key Vault)${NC}"
fi

echo ""
echo "App Name: $APP_NAME"
echo "Tenant ID: $TENANT_ID"
echo "GitHub Repo: $GITHUB_REPO"
echo "OIDC Issuer: $OIDC_ISSUER"
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

# Try to dynamically determine the SWA hostname
SWA_HOSTNAME=""
RG_NAME="${RESOURCE_GROUP_PARAM:-rg-${APP_NAME_PARAM:-cloudhealthoffice}-prod}"
SWA_NAME="${APP_NAME_PARAM:-cloudhealthoffice}-swa"

# Attempt to get hostname from Azure (if SWA exists)
SWA_HOSTNAME=$(az staticwebapp show --name "$SWA_NAME" --resource-group "$RG_NAME" \
    --query "defaultHostname" -o tsv 2>/dev/null || echo "")

if [ -z "$SWA_HOSTNAME" ]; then
    echo -e "${YELLOW}⚠️  Static Web App not found, using placeholder for Azure redirect URI${NC}"
    SWA_HOSTNAME="${SWA_NAME}.azurestaticapps.net"
fi

# Determine production domain from APP_NAME or default
PROD_DOMAIN="${CUSTOM_DOMAIN:-cloudhealthoffice.com}"

# Get redirect URIs from Key Vault or set defaults
if [ "$USE_VAULT" = true ]; then
    REDIRECT_URI_1=$(get_or_set_vault_config "$KEY_VAULT_NAME" "redirect-uri-production" "https://${PROD_DOMAIN}/.auth/login/aad/callback" "Production redirect URI")
    REDIRECT_URI_2=$(get_or_set_vault_config "$KEY_VAULT_NAME" "redirect-uri-azure" "https://${SWA_HOSTNAME}/.auth/login/aad/callback" "Azure Static Web App redirect URI")
    REDIRECT_URI_3=$(get_or_set_vault_config "$KEY_VAULT_NAME" "redirect-uri-local" "http://localhost:3000/.auth/login/aad/callback" "Local development redirect URI")
else
    REDIRECT_URI_1="https://${PROD_DOMAIN}/.auth/login/aad/callback"
    REDIRECT_URI_2="https://${SWA_HOSTNAME}/.auth/login/aad/callback"
    REDIRECT_URI_3="http://localhost:3000/.auth/login/aad/callback"
fi

# Define redirect URIs
echo "Redirect URIs:"
echo "  - $REDIRECT_URI_1"
echo "  - $REDIRECT_URI_2"
echo "  - $REDIRECT_URI_3"

# Check if redirect URIs are already configured
EXISTING_URIS=$(az ad app show --id "$APP_ID" --query "web.redirectUris" -o tsv 2>/dev/null || echo "")

if echo "$EXISTING_URIS" | grep -q "$REDIRECT_URI_1" && \
   echo "$EXISTING_URIS" | grep -q "$REDIRECT_URI_2" && \
   echo "$EXISTING_URIS" | grep -q "$REDIRECT_URI_3"; then
    echo -e "${GREEN}✅ Redirect URIs already configured${NC}"
else
    # Update web redirect URIs (pass each URI as a separate argument)
    az ad app update --id "$APP_ID" \
        --web-redirect-uris "$REDIRECT_URI_1" "$REDIRECT_URI_2" "$REDIRECT_URI_3"
    
    echo -e "${GREEN}✅ Redirect URIs configured${NC}"
fi

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
            \"issuer\": \"${OIDC_ISSUER}\",
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
            \"issuer\": \"${OIDC_ISSUER}\",
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
