#!/usr/bin/env bash
# Provision the opt-in Azure Cosmos DB for MongoDB lifetime free-tier account.
# No keys or connection strings are printed or written to disk.
#
# Required env vars:
#   RESOURCE_GROUP — Azure resource group
#   LOCATION       — Azure region
#
# Optional env vars:
#   BASE_NAME      — resource name prefix (default: cho-mcc)
#   THROUGHPUT     — shared database RU/s (default: 1000; values above
#                    1000 are billable)
set -euo pipefail

: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"
: "${LOCATION:?LOCATION is required}"

BASE_NAME="${BASE_NAME:-cho-mcc}"
THROUGHPUT="${THROUGHPUT:-1000}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
TEMPLATE_FILE="${REPO_ROOT}/infrastructure/azure/modules/cosmos-mongodb-free-tier.bicep"
DEPLOYMENT_NAME="cosmos-mongodb-free-tier"

command -v az >/dev/null 2>&1 || {
  echo "Azure CLI (az) is required" >&2
  exit 1
}

az account show >/dev/null

if ! az group show --name "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "==> Creating resource group ${RESOURCE_GROUP} in ${LOCATION}"
  az group create \
    --name "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --output none
fi

existing_free_tier="$(
  az cosmosdb list \
    --query "[?enableFreeTier == \`true\`].{name:name,resourceGroup:resourceGroup,purpose:tags.Purpose}" \
    --output tsv
)"

if [[ -n "$existing_free_tier" ]]; then
  managed_free_tier="$(
    az cosmosdb list \
      --query "[?enableFreeTier == \`true\` && tags.Purpose == 'MccCosmosMongo' && resourceGroup == '${RESOURCE_GROUP}'].name | [0]" \
      --output tsv
  )"

  if [[ -z "$managed_free_tier" ]]; then
    echo "==> Subscription already contains a different Cosmos DB free-tier account:"
    echo "$existing_free_tier"
    echo "Azure permits one free-tier Cosmos DB account per subscription." >&2
    echo "Delete that account or reuse it before running this deployment." >&2
    exit 1
  fi

  echo "==> Reusing managed free-tier account ${managed_free_tier}"
fi

echo "==> Provisioning Cosmos DB for MongoDB free tier"
account_name="$(
  az deployment group create \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$TEMPLATE_FILE" \
    --parameters baseName="$BASE_NAME" location="$LOCATION" throughput="$THROUGHPUT" \
    --query properties.outputs.cosmosMongoAccountName.value \
    --output tsv
)"

if [[ -z "$account_name" ]]; then
  echo "Deployment completed without returning a Cosmos DB account name" >&2
  exit 1
fi

echo "==> Cosmos DB for MongoDB free-tier account ready: ${account_name}"
echo "==> Database: cloudhealthoffice; shared throughput: ${THROUGHPUT} RU/s"
echo
echo "Connect local Kubernetes with:"
echo "  COSMOS_MONGODB_ACCOUNT=${account_name} \\"
echo "  RESOURCE_GROUP=${RESOURCE_GROUP} \\"
echo "  ./scripts/azure/bootstrap-local-cosmos-mongodb.sh"
