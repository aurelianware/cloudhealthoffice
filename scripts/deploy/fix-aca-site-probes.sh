#!/usr/bin/env bash
# fix-aca-site-probes.sh
#
# Non-destructively patches the 'site' container's health probes to use port
# 8080.  The site container (nginx-unprivileged) listens on port 8080, but ACA
# may have stale probes configured for port 80, causing new revisions to stay
# in "activating" state indefinitely.
#
# Existing container settings (resources, env, volumeMounts, etc.) are
# preserved — only the probes array for the 'site' container is changed.
#
# Usage: fix-aca-site-probes.sh <app-name> <resource-group>
#
set -euo pipefail

APP_NAME="${1:?APP_NAME is required}"
RESOURCE_GROUP="${2:?RESOURCE_GROUP is required}"

# Use a temp file that is automatically cleaned up on exit.
PATCH_FILE=$(mktemp "${TMPDIR:-/tmp}/probe-patch.XXXXXX.json")
trap 'rm -f "$PATCH_FILE"' EXIT

echo "Fetching resource ID for ${APP_NAME}..."
RESOURCE_ID=$(az containerapp show \
  --name "$APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query id -o tsv)

echo "Fetching existing container definitions..."
EXISTING_CONTAINERS=$(az containerapp show \
  --name "$APP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --query "properties.template.containers" \
  -o json)

echo "Building non-destructive probe patch (port 8080) for container 'site'..."
jq -n \
  --argjson existing_containers "$EXISTING_CONTAINERS" \
  '{
    properties: {
      template: {
        containers: (
          $existing_containers
          | map(
              if .name == "site" then
                .probes = [
                  {type: "Liveness",  tcpSocket: {port: 8080}, failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, timeoutSeconds: 5},
                  {type: "Readiness", tcpSocket: {port: 8080}, failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, timeoutSeconds: 5},
                  {type: "Startup",   tcpSocket: {port: 8080}, failureThreshold: 240, initialDelaySeconds: 1, periodSeconds: 1, successThreshold: 1, timeoutSeconds: 3}
                ]
              else
                .
              end
            )
        )
      }
    }
  }' > "$PATCH_FILE"

echo "Patching ${RESOURCE_ID} with port-8080 probes..."
az rest \
  --method PATCH \
  --url "https://management.azure.com${RESOURCE_ID}?api-version=2024-03-01" \
  --body "@${PATCH_FILE}" \
  --headers "Content-Type=application/json"

echo "Health probes updated to port 8080."
