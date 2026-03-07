#!/usr/bin/env bash
# fix-aca-site-probes.sh
#
# Patches Azure Container App health probes to use port 8080.
#
# The site container (nginx-unprivileged) listens on port 8080, but ACA may
# have stale probes configured for port 80, causing new revisions to stay in
# "activating" state indefinitely.
#
# Usage: fix-aca-site-probes.sh <app-name> <resource-group> <image>
#
set -euo pipefail

APP_NAME="${1:?APP_NAME is required}"
RESOURCE_GROUP="${2:?RESOURCE_GROUP is required}"
IMAGE="${3:?IMAGE is required}"

echo "Fetching resource ID for ${APP_NAME}..."
RESOURCE_ID=$(az containerapp show \
  --name "$APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id -o tsv)

echo "Building probe patch (port 8080) for container 'site'..."
jq -n --arg image "$IMAGE" '{
  properties: {
    template: {
      containers: [{
        name: "site",
        image: $image,
        probes: [
          {type: "Liveness",  tcpSocket: {port: 8080}, failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, timeoutSeconds: 5},
          {type: "Readiness", tcpSocket: {port: 8080}, failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, timeoutSeconds: 5},
          {type: "Startup",   tcpSocket: {port: 8080}, failureThreshold: 240, initialDelaySeconds: 1, periodSeconds: 1, successThreshold: 1, timeoutSeconds: 3}
        ]
      }]
    }
  }
}' > /tmp/probe-patch.json

echo "Patching ${RESOURCE_ID} with port-8080 probes..."
az rest \
  --method PATCH \
  --url "https://management.azure.com${RESOURCE_ID}?api-version=2024-03-01" \
  --body @/tmp/probe-patch.json \
  --headers "Content-Type=application/json"

echo "Health probes updated to port 8080."
