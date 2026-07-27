#!/usr/bin/env bash
# Opt-in Azure Service Bus bootstrap for a local Kubernetes deployment.
#
# Provisions the claims topic/subscription, creates a namespace authorization
# rule with only Listen + Send rights, and installs its connection string in
# the local cluster. Nothing is written to disk and the connection string is
# never printed.
#
# Required env vars:
#   SERVICEBUS_NAMESPACE — Azure Service Bus namespace
#   RESOURCE_GROUP       — resource group containing the namespace
#   LOCATION             — Azure region (used only if the namespace is absent)
#
# Optional env vars:
#   K8S_NAMESPACE        — Kubernetes namespace (default: cloudhealthoffice)
#   AUTH_RULE_NAME       — authorization rule name (default: claims-service-local)
set -euo pipefail

: "${SERVICEBUS_NAMESPACE:?SERVICEBUS_NAMESPACE is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"
: "${LOCATION:?LOCATION is required}"

K8S_NAMESPACE="${K8S_NAMESPACE:-cloudhealthoffice}"
AUTH_RULE_NAME="${AUTH_RULE_NAME:-claims-service-local}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

command -v az >/dev/null 2>&1 || {
  echo "Azure CLI (az) is required" >&2
  exit 1
}
command -v kubectl >/dev/null 2>&1 || {
  echo "kubectl is required" >&2
  exit 1
}

"${SCRIPT_DIR}/provision-servicebus-claim-events.sh"

echo "==> Namespace authorization rule: ${AUTH_RULE_NAME} (Listen + Send)"
if az servicebus namespace authorization-rule show \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --name "$AUTH_RULE_NAME" \
    --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
  az servicebus namespace authorization-rule update \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --name "$AUTH_RULE_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --rights Listen Send >/dev/null
else
  az servicebus namespace authorization-rule create \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --name "$AUTH_RULE_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --rights Listen Send >/dev/null
fi

CONNECTION_STRING="$(
  az servicebus namespace authorization-rule keys list \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --name "$AUTH_RULE_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query primaryConnectionString \
    --output tsv
)"

if [[ -z "$CONNECTION_STRING" ]]; then
  echo "Azure CLI returned an empty Service Bus connection string" >&2
  exit 1
fi

kubectl create namespace "$K8S_NAMESPACE" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl create secret generic servicebus-secret \
  --namespace "$K8S_NAMESPACE" \
  --from-literal=connectionString="$CONNECTION_STRING" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

unset CONNECTION_STRING
echo "==> Installed servicebus-secret in Kubernetes namespace ${K8S_NAMESPACE}"
