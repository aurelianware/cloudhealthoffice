#!/usr/bin/env bash
# Point the Million Claim Challenge services in a local Kubernetes cluster at
# an Azure Cosmos DB for MongoDB account. The connection string is retrieved
# directly from Azure, installed as Kubernetes secrets, and never printed.
#
# Required env vars:
#   COSMOS_MONGODB_ACCOUNT — Cosmos DB for MongoDB account name
#   RESOURCE_GROUP         — resource group containing the account
#
# Optional env vars:
#   K8S_NAMESPACE      — Kubernetes namespace (default: cloudhealthoffice)
#   RESTART_WORKLOADS  — restart MCC deployments after secret update (default: true)
set -euo pipefail

: "${COSMOS_MONGODB_ACCOUNT:?COSMOS_MONGODB_ACCOUNT is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"

K8S_NAMESPACE="${K8S_NAMESPACE:-cloudhealthoffice}"
RESTART_WORKLOADS="${RESTART_WORKLOADS:-true}"

command -v az >/dev/null 2>&1 || {
  echo "Azure CLI (az) is required" >&2
  exit 1
}
command -v kubectl >/dev/null 2>&1 || {
  echo "kubectl is required" >&2
  exit 1
}

account_kind="$(
  az cosmosdb show \
    --name "$COSMOS_MONGODB_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" \
    --query kind \
    --output tsv
)"

if [[ "$account_kind" != "MongoDB" ]]; then
  echo "Cosmos DB account ${COSMOS_MONGODB_ACCOUNT} is '${account_kind}', not 'MongoDB'" >&2
  exit 1
fi

connection_string="$(
  az cosmosdb keys list \
    --name "$COSMOS_MONGODB_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" \
    --type connection-strings \
    --query 'connectionStrings[0].connectionString' \
    --output tsv
)"

if [[ -z "$connection_string" ]]; then
  echo "Azure CLI returned an empty Cosmos DB for MongoDB connection string" >&2
  exit 1
fi

kubectl create namespace "$K8S_NAMESPACE" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

kubectl create secret generic cosmos-mongodb-secret \
  --namespace "$K8S_NAMESPACE" \
  --from-literal=connectionString="$connection_string" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

# The core services consume database-secret. Keep endpoint/key keys for
# manifests that declare them even though the Mongo path uses connectionString.
kubectl create secret generic database-secret \
  --namespace "$K8S_NAMESPACE" \
  --from-literal=connectionString="$connection_string" \
  --from-literal=endpoint= \
  --from-literal=key= \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

unset connection_string

if [[ "$RESTART_WORKLOADS" == "true" ]]; then
  mcc_deployments=(
    authorization-service
    benefit-plan-service
    claims-examiner-service
    claims-service
    coverage-service
    eligibility-service
    member-service
    provider-service
  )

  for deployment in "${mcc_deployments[@]}"; do
    if kubectl get deployment "$deployment" --namespace "$K8S_NAMESPACE" >/dev/null 2>&1; then
      kubectl rollout restart deployment "$deployment" \
        --namespace "$K8S_NAMESPACE" >/dev/null
    fi
  done
fi

echo "==> Installed Cosmos DB for MongoDB connection in namespace ${K8S_NAMESPACE}"
if [[ "$RESTART_WORKLOADS" == "true" ]]; then
  echo "==> Restarted installed Million Claim Challenge services"
fi
