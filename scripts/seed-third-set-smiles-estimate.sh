#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${CHO_ESTIMATE_BASE_URL:?CHO_ESTIMATE_BASE_URL is required}"
API_KEY="${CHO_ESTIMATE_API_KEY:?CHO_ESTIMATE_API_KEY is required}"
TENANT_ID="third-set-smiles"
PLAN_ID="3e8c59e8-47dd-4aa9-b318-9828fbdcb072"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLAN_FILE="$SCRIPT_DIR/seed-data/third-set-smiles-dental-plan.json"
MAPPINGS_FILE="$SCRIPT_DIR/seed-data/third-set-smiles-dental-mappings.json"

headers=(
  -H "X-Api-Key: $API_KEY"
  -H "X-Tenant-ID: $TENANT_ID"
  -H 'Content-Type: application/json'
)

plan_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  "${BASE_URL%/}/api/v1/plans/$PLAN_ID" "${headers[@]}")
if [[ "$plan_status" == "404" ]]; then
  curl --fail-with-body -sS \
    -X POST "${BASE_URL%/}/api/v1/plans" \
    "${headers[@]}" \
    --data-binary "@$PLAN_FILE" >/dev/null
elif [[ "$plan_status" != "200" ]]; then
  echo "Unexpected plan lookup status: $plan_status" >&2
  exit 1
fi

existing_mappings=$(curl --fail-with-body -sS \
  "${BASE_URL%/}/api/v1/service-category-mappings?planId=$PLAN_ID" \
  "${headers[@]}")

while IFS= read -r mapping; do
  service_type=$(jq -r '.serviceTypeCode' <<<"$mapping")
  if ! jq -e --arg service_type "$service_type" \
    'any(.[]; .serviceTypeCode == $service_type)' <<<"$existing_mappings" >/dev/null; then
    curl --fail-with-body -sS \
      -X POST "${BASE_URL%/}/api/v1/service-category-mappings" \
      "${headers[@]}" \
      --data-binary "$mapping" >/dev/null
  fi
done < <(jq -c '.[]' "$MAPPINGS_FILE")

echo "$PLAN_ID"
