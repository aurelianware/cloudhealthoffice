#!/bin/bash
# =============================================================================
# Ensure Service Principal Script
# =============================================================================
# Purpose: Create or verify Azure AD service principal with required RBAC roles
# Usage: ./ensure-service-principal.sh <app-id> <subscription-id> <resource-group>
#
# This script is idempotent - safe to run multiple times
# Creates service principal if it doesn't exist
# Assigns required Azure RBAC roles:
# - Contributor on resource group (for infrastructure deployment)
# - Website Contributor on Static Web App (if exists)
# - Key Vault Secrets User on deployment Key Vault (if exists)
#
# Returns: Service Principal Object ID
# =============================================================================

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Script parameters
APP_ID="${1:-}"
SUBSCRIPTION_ID="${2:-}"
RESOURCE_GROUP_PARAM="${3:-}"
BASE_NAME_PARAM="${4:-}"
KEY_VAULT_NAME="${5:-}"

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
echo -e "${BLUE}Cloud Health Office - Service Principal${NC}"
echo -e "${BLUE}=========================================${NC}"
echo ""

# Validate parameters
if [ -z "$APP_ID" ]; then
    echo -e "${RED}❌ Error: Application ID is required${NC}"
    echo "Usage: $0 <app-id> [subscription-id] [resource-group] [base-name] [key-vault-name]"
    echo ""
    echo "Example:"
    echo "  $0 12345678-1234-1234-1234-123456789abc"
    exit 1
fi

# Validate Azure CLI login
if ! az account show &>/dev/null; then
    echo -e "${RED}❌ Error: Not logged in to Azure${NC}"
    echo "Please run: az login"
    exit 1
fi

# Get subscription ID if not provided
if [ -z "$SUBSCRIPTION_ID" ]; then
    SUBSCRIPTION_ID=$(az account show --query id -o tsv)
    echo -e "${BLUE}Using current subscription: ${SUBSCRIPTION_ID}${NC}"
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
    RESOURCE_GROUP=$(get_or_set_vault_config "$KEY_VAULT_NAME" "resource-group-name" "${RESOURCE_GROUP_PARAM:-rg-cloudhealthoffice-prod}" "Azure Resource Group Name")
    BASE_NAME=$(get_or_set_vault_config "$KEY_VAULT_NAME" "base-name" "${BASE_NAME_PARAM:-cloudhealthoffice}" "Base Name for Resources")
else
    RESOURCE_GROUP="${RESOURCE_GROUP_PARAM:-rg-cloudhealthoffice-prod}"
    BASE_NAME="${BASE_NAME_PARAM:-cloudhealthoffice}"
    echo -e "${YELLOW}⚠️  Using hardcoded defaults (no Key Vault)${NC}"
fi

echo "Application ID: $APP_ID"
echo "Subscription ID: $SUBSCRIPTION_ID"
echo "Resource Group: $RESOURCE_GROUP"
echo "Base Name: $BASE_NAME"
echo ""

# Check if service principal already exists
echo -e "${BLUE}Checking if service principal exists...${NC}"
SP_OBJECT_ID=$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null || echo "")

if [ -n "$SP_OBJECT_ID" ]; then
    echo -e "${GREEN}✅ Service principal already exists${NC}"
    echo "Object ID: $SP_OBJECT_ID"
else
    echo -e "${YELLOW}⚠️  Service principal not found - creating new one${NC}"
    
    # Create service principal
    echo -e "${BLUE}Creating service principal for app: $APP_ID${NC}"
    
    SP_OBJECT_ID=$(az ad sp create --id "$APP_ID" --query "id" -o tsv 2>/dev/null || echo "")
    
    if [ -z "$SP_OBJECT_ID" ]; then
        echo -e "${RED}❌ Failed to create service principal${NC}"
        echo "Ensure the app registration exists and you have permissions to create service principals"
        exit 1
    fi
    
    echo -e "${GREEN}✅ Service principal created${NC}"
    echo "Object ID: $SP_OBJECT_ID"
    
    # Wait for service principal to propagate
    echo "Waiting for service principal to propagate..."
    sleep 10
fi

# Ensure resource group exists
echo ""
echo -e "${BLUE}Ensuring resource group exists...${NC}"
if ! az group show --name "$RESOURCE_GROUP" &>/dev/null; then
    echo -e "${YELLOW}⚠️  Resource group not found - creating${NC}"
    
    # Default location (can be overridden)
    LOCATION="${AZURE_LOCATION:-eastus}"
    
    az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --tags \
        "Environment=Production" \
        "ManagedBy=CloudHealthOffice" \
        "Purpose=HIPAACompliance"
    
    echo -e "${GREEN}✅ Resource group created: $RESOURCE_GROUP${NC}"
else
    echo -e "${GREEN}✅ Resource group exists: $RESOURCE_GROUP${NC}"
fi

# Assign RBAC roles
echo ""
echo -e "${BLUE}Configuring RBAC role assignments...${NC}"

# 1. Contributor role on resource group (for infrastructure deployment)
echo ""
echo "Assigning 'Contributor' role on resource group..."
RESOURCE_GROUP_SCOPE="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"

# Check if role assignment already exists
EXISTING_ASSIGNMENT=$(az role assignment list \
    --assignee "$SP_OBJECT_ID" \
    --role "Contributor" \
    --scope "$RESOURCE_GROUP_SCOPE" \
    --query "[0].id" -o tsv 2>/dev/null || echo "")

if [ -n "$EXISTING_ASSIGNMENT" ]; then
    echo -e "${GREEN}✅ Contributor role already assigned${NC}"
else
    az role assignment create \
        --assignee "$SP_OBJECT_ID" \
        --role "Contributor" \
        --scope "$RESOURCE_GROUP_SCOPE" \
        --description "GitHub Actions deployment for Cloud Health Office"
    
    echo -e "${GREEN}✅ Contributor role assigned${NC}"
fi

# 2. Website Contributor role on Static Web App (if exists)
echo ""
echo "Checking for Static Web App..."
SWA_NAME="${BASE_NAME}-swa"
SWA_ID=$(az staticwebapp show --name "$SWA_NAME" --resource-group "$RESOURCE_GROUP" \
    --query "id" -o tsv 2>/dev/null || echo "")

if [ -n "$SWA_ID" ]; then
    echo -e "${GREEN}✅ Static Web App found: $SWA_NAME${NC}"
    
    # Check if role assignment already exists
    EXISTING_SWA_ASSIGNMENT=$(az role assignment list \
        --assignee "$SP_OBJECT_ID" \
        --role "Website Contributor" \
        --scope "$SWA_ID" \
        --query "[0].id" -o tsv 2>/dev/null || echo "")
    
    if [ -n "$EXISTING_SWA_ASSIGNMENT" ]; then
        echo -e "${GREEN}✅ Website Contributor role already assigned${NC}"
    else
        echo "Assigning 'Website Contributor' role on Static Web App..."
        az role assignment create \
            --assignee "$SP_OBJECT_ID" \
            --role "Website Contributor" \
            --scope "$SWA_ID" \
            --description "Static Web App deployment for Cloud Health Office"
        
        echo -e "${GREEN}✅ Website Contributor role assigned${NC}"
    fi
else
    echo -e "${YELLOW}⚠️  Static Web App not found - will be created during infrastructure deployment${NC}"
    echo "Role will be assigned after Static Web App creation"
fi

# 3. Key Vault Secrets User role on deployment Key Vault (if exists)
echo ""
echo "Checking for deployment Key Vault..."
KV_NAME="${BASE_NAME}-deploy-kv"
KV_ID=$(az keyvault show --name "$KV_NAME" --query "id" -o tsv 2>/dev/null || echo "")

if [ -n "$KV_ID" ]; then
    echo -e "${GREEN}✅ Key Vault found: $KV_NAME${NC}"
    
    # Check if role assignment already exists
    EXISTING_KV_ASSIGNMENT=$(az role assignment list \
        --assignee "$SP_OBJECT_ID" \
        --role "Key Vault Secrets User" \
        --scope "$KV_ID" \
        --query "[0].id" -o tsv 2>/dev/null || echo "")
    
    if [ -n "$EXISTING_KV_ASSIGNMENT" ]; then
        echo -e "${GREEN}✅ Key Vault Secrets User role already assigned${NC}"
    else
        echo "Assigning 'Key Vault Secrets User' role on Key Vault..."
        az role assignment create \
            --assignee "$SP_OBJECT_ID" \
            --role "Key Vault Secrets User" \
            --scope "$KV_ID" \
            --description "Key Vault access for GitHub Actions deployment"
        
        echo -e "${GREEN}✅ Key Vault Secrets User role assigned${NC}"
    fi
else
    echo -e "${YELLOW}⚠️  Deployment Key Vault not found - will be created during infrastructure deployment${NC}"
    echo "Role will be assigned after Key Vault creation"
fi

# Summary
echo ""
echo -e "${GREEN}=========================================${NC}"
echo -e "${GREEN}Service Principal Setup Complete${NC}"
echo -e "${GREEN}=========================================${NC}"
echo ""
echo "Service Principal Object ID: $SP_OBJECT_ID"
echo "Application ID: $APP_ID"
echo ""
echo -e "${BLUE}Assigned Roles:${NC}"
echo "✅ Contributor on resource group: $RESOURCE_GROUP"
if [ -n "$SWA_ID" ]; then
    echo "✅ Website Contributor on Static Web App: $SWA_NAME"
else
    echo "⏳ Website Contributor (pending Static Web App creation)"
fi
if [ -n "$KV_ID" ]; then
    echo "✅ Key Vault Secrets User on Key Vault: $KV_NAME"
else
    echo "⏳ Key Vault Secrets User (pending Key Vault creation)"
fi
echo ""
echo -e "${BLUE}Next Steps:${NC}"
echo "1. Add GitHub secret AZURE_CLIENT_ID: $APP_ID"
echo "2. Add GitHub secret AZURE_SUBSCRIPTION_ID: $SUBSCRIPTION_ID"
echo "3. Get tenant ID: az account show --query tenantId -o tsv"
echo "4. Add GitHub secret AZURE_TENANT_ID: <tenant-id>"
echo "5. Deploy infrastructure using GitHub Actions workflow"
echo ""

# Output service principal object ID for use in scripts/workflows
echo "$SP_OBJECT_ID"
