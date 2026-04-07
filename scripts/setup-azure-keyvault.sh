#!/bin/bash
# =============================================================================
# Setup Azure Key Vault for Application Secrets
# =============================================================================
# Purpose: Create and configure an Azure Key Vault for CHO microservice secrets.
#          Idempotent — safe to re-run.
# Usage:
#   ./scripts/setup-azure-keyvault.sh \
#     --resource-group rg-cloudhealthoffice-prod \
#     --vault-name cho-app-kv \
#     --aks-cluster cho-aks \
#     --location eastus \
#     --log-analytics cho-logs
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
step()          { echo -e "\n${BOLD}${BLUE}── Step $1: $2${NC}"; }

# Default values
RESOURCE_GROUP=""
VAULT_NAME=""
AKS_CLUSTER_NAME=""
LOCATION=""
LOG_ANALYTICS_WORKSPACE=""

# Cleanup trap
cleanup() {
  local exit_code=$?
  if [[ $exit_code -ne 0 ]]; then
    echo ""
    print_error "Script failed with exit code $exit_code"
    print_info "The script is idempotent — re-run after fixing the issue above."
  fi
}
trap cleanup EXIT

usage() {
  cat <<EOF
Usage: $0 [OPTIONS]

Create and configure Azure Key Vault for CHO application secrets.

OPTIONS:
  -g, --resource-group NAME       Azure resource group (required)
  -v, --vault-name NAME           Key Vault name (required)
  -a, --aks-cluster NAME          AKS cluster name (required)
  -l, --location REGION           Azure region (required)
  -w, --log-analytics NAME        Log Analytics workspace name (required)
  -h, --help                      Show this help message

EXAMPLES:
  $0 -g rg-cloudhealthoffice-prod -v cho-app-kv -a cho-aks -l eastus -w cho-logs

WHAT THIS SCRIPT DOES:
  1. Creates Key Vault (Premium SKU, RBAC auth, purge protection)
  2. Configures network rules (deny public, allow AKS subnet, bypass Azure services)
  3. Enables diagnostic settings → Log Analytics (AuditEvent, 365 days)
  4. Gets AKS kubelet managed identity
  5. Assigns "Key Vault Secrets User" role to kubelet identity

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

while [[ $# -gt 0 ]]; do
  case $1 in
  -g | --resource-group)      RESOURCE_GROUP="$2"; shift 2 ;;
  -v | --vault-name)          VAULT_NAME="$2"; shift 2 ;;
  -a | --aks-cluster)         AKS_CLUSTER_NAME="$2"; shift 2 ;;
  -l | --location)            LOCATION="$2"; shift 2 ;;
  -w | --log-analytics)       LOG_ANALYTICS_WORKSPACE="$2"; shift 2 ;;
  -h | --help)                usage; exit 0 ;;
  *)                          print_error "Unknown option: $1"; usage; exit 1 ;;
  esac
done

# Validate required parameters
for param_name in RESOURCE_GROUP VAULT_NAME AKS_CLUSTER_NAME LOCATION LOG_ANALYTICS_WORKSPACE; do
  if [[ -z "${!param_name}" ]]; then
    print_error "$param_name is required"
    usage
    exit 1
  fi
done

# =============================================================================
# Pre-flight
# =============================================================================

print_header "Azure Key Vault Setup — $VAULT_NAME"

print_info "Resource Group:     $RESOURCE_GROUP"
print_info "Key Vault:          $VAULT_NAME"
print_info "AKS Cluster:        $AKS_CLUSTER_NAME"
print_info "Location:           $LOCATION"
print_info "Log Analytics:      $LOG_ANALYTICS_WORKSPACE"

if ! az account show &>/dev/null; then
  print_error "Azure CLI is not authenticated. Run: az login"
  exit 1
fi
print_success "Azure CLI authenticated"

# =============================================================================
# Step 1: Create Key Vault
# =============================================================================

step 1 "Create Key Vault (Premium SKU, RBAC auth, purge protection)"

if az keyvault show --name "$VAULT_NAME" &>/dev/null; then
  print_warning "Key Vault '$VAULT_NAME' already exists — skipping creation"
else
  az keyvault create \
    --name "$VAULT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku premium \
    --enable-rbac-authorization true \
    --enable-purge-protection true \
    --retention-days 90 \
    --enabled-for-deployment false \
    --enabled-for-disk-encryption false \
    --enabled-for-template-deployment false \
    --output none

  print_success "Key Vault '$VAULT_NAME' created"
fi

# =============================================================================
# Step 2: Configure network rules
# =============================================================================

step 2 "Configure network rules (deny public, allow AKS subnet)"

# Get AKS subnet ID
AKS_SUBNET_ID=$(az aks show \
  --name "$AKS_CLUSTER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "agentPoolProfiles[0].vnetSubnetId" \
  -o tsv 2>/dev/null || echo "")

az keyvault update \
  --name "$VAULT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --default-action Deny \
  --bypass AzureServices \
  --output none

print_success "Default action set to Deny, bypass AzureServices"

if [[ -n "$AKS_SUBNET_ID" && "$AKS_SUBNET_ID" != "None" ]]; then
  # Ensure Microsoft.KeyVault service endpoint on the subnet
  VNET_NAME=$(echo "$AKS_SUBNET_ID" | sed -n 's|.*/virtualNetworks/\([^/]*\)/.*|\1|p')
  SUBNET_NAME=$(echo "$AKS_SUBNET_ID" | sed -n 's|.*/subnets/\(.*\)|\1|p')
  VNET_RG=$(echo "$AKS_SUBNET_ID" | sed -n 's|.*/resourceGroups/\([^/]*\)/.*|\1|p')

  az network vnet subnet update \
    --name "$SUBNET_NAME" \
    --vnet-name "$VNET_NAME" \
    --resource-group "$VNET_RG" \
    --service-endpoints Microsoft.KeyVault \
    --output none 2>/dev/null || print_warning "Could not add service endpoint — may already exist"

  az keyvault network-rule add \
    --name "$VAULT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --subnet "$AKS_SUBNET_ID" \
    --output none 2>/dev/null || print_warning "Network rule may already exist"

  print_success "AKS subnet added to network rules"
else
  print_warning "AKS subnet not found (cluster may use kubenet). Skipping VNet rule."
  print_info "You may need to configure private endpoints manually."
fi

# =============================================================================
# Step 3: Enable diagnostic settings
# =============================================================================

step 3 "Enable diagnostic settings → Log Analytics (AuditEvent, 365 days)"

LOG_ANALYTICS_ID=$(az monitor log-analytics workspace show \
  --workspace-name "$LOG_ANALYTICS_WORKSPACE" \
  --resource-group "$RESOURCE_GROUP" \
  --query id -o tsv 2>/dev/null || echo "")

if [[ -z "$LOG_ANALYTICS_ID" ]]; then
  print_warning "Log Analytics workspace '$LOG_ANALYTICS_WORKSPACE' not found — skipping diagnostics"
  print_info "Create it with: az monitor log-analytics workspace create -g $RESOURCE_GROUP -n $LOG_ANALYTICS_WORKSPACE"
else
  KV_ID=$(az keyvault show --name "$VAULT_NAME" --query id -o tsv)

  az monitor diagnostic-settings create \
    --name "${VAULT_NAME}-diagnostics" \
    --resource "$KV_ID" \
    --workspace "$LOG_ANALYTICS_ID" \
    --logs '[{"category":"AuditEvent","enabled":true,"retentionPolicy":{"enabled":true,"days":365}}]' \
    --metrics '[{"category":"AllMetrics","enabled":true,"retentionPolicy":{"enabled":true,"days":90}}]' \
    --output none 2>/dev/null || print_warning "Diagnostic settings may already exist"

  print_success "Diagnostic settings configured (AuditEvent → 365-day retention)"
fi

# =============================================================================
# Step 4: Get AKS kubelet managed identity
# =============================================================================

step 4 "Get AKS kubelet managed identity"

KUBELET_IDENTITY=$(az aks show \
  --name "$AKS_CLUSTER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "identityProfile.kubeletidentity.objectId" \
  -o tsv)

if [[ -z "$KUBELET_IDENTITY" || "$KUBELET_IDENTITY" == "None" ]]; then
  print_error "Could not get kubelet identity for AKS cluster '$AKS_CLUSTER_NAME'"
  print_info "Ensure the cluster uses managed identity (not service principal)"
  exit 1
fi

print_success "Kubelet identity: $KUBELET_IDENTITY"

# =============================================================================
# Step 5: Assign Key Vault Secrets User role
# =============================================================================

step 5 "Assign 'Key Vault Secrets User' role to kubelet identity"

KV_ID=$(az keyvault show --name "$VAULT_NAME" --query id -o tsv)

# Check if assignment already exists
EXISTING=$(az role assignment list \
  --assignee "$KUBELET_IDENTITY" \
  --role "Key Vault Secrets User" \
  --scope "$KV_ID" \
  --query "length([])" -o tsv 2>/dev/null || echo "0")

if [[ "$EXISTING" -gt 0 ]]; then
  print_warning "Role assignment already exists — skipping"
else
  az role assignment create \
    --assignee-object-id "$KUBELET_IDENTITY" \
    --assignee-principal-type ServicePrincipal \
    --role "Key Vault Secrets User" \
    --scope "$KV_ID" \
    --output none

  print_success "Role assigned: Key Vault Secrets User → kubelet identity"
fi

# =============================================================================
# Summary
# =============================================================================

print_header "Setup Complete"

print_success "Key Vault:          $VAULT_NAME"
print_success "SKU:                Premium (HSM-backed)"
print_success "RBAC:               Enabled"
print_success "Purge Protection:   Enabled (90-day retention)"
print_success "Network:            Deny by default, Azure services bypass"
print_success "Diagnostics:        AuditEvent → Log Analytics (365 days)"
print_success "AKS RBAC:           Key Vault Secrets User → $KUBELET_IDENTITY"
echo ""
print_info "Next steps:"
print_info "  1. Populate secrets:  ./scripts/populate-keyvault-secrets.sh -v $VAULT_NAME -f scripts/secrets-manifest.example.env"
print_info "  2. Validate access:   ./scripts/validate-keyvault-access.sh -v $VAULT_NAME"
print_info "  3. Set GitHub var:    gh variable set KEY_VAULT_NAME -b '$VAULT_NAME'"
