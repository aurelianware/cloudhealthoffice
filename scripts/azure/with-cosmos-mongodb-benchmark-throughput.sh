#!/usr/bin/env bash
# Temporarily scale an RU-based Cosmos DB for MongoDB database for a
# benchmark command, then restore the original throughput on every normal,
# failed, interrupted, or terminal-disconnect exit path.
#
# Required env vars:
#   COSMOS_MONGODB_ACCOUNT
#   RESOURCE_GROUP
#   TARGET_THROUGHPUT
#
# Optional env vars:
#   DATABASE_NAME       (default: cloudhealthoffice)
#   MAX_THROUGHPUT      (default: 20000)
#   THROUGHPUT_UPDATE_ATTEMPTS       (default: 6)
#   THROUGHPUT_UPDATE_RETRY_SECONDS  (default: 15)
#
# Usage:
#   COSMOS_MONGODB_ACCOUNT=... RESOURCE_GROUP=... TARGET_THROUGHPUT=10000 \
#     ./scripts/azure/with-cosmos-mongodb-benchmark-throughput.sh -- \
#     env CLAIMS=1000 ./scripts/run-mcc-local-k8s.sh
set -euo pipefail

: "${COSMOS_MONGODB_ACCOUNT:?COSMOS_MONGODB_ACCOUNT is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"
: "${TARGET_THROUGHPUT:?TARGET_THROUGHPUT is required}"

DATABASE_NAME="${DATABASE_NAME:-cloudhealthoffice}"
MAX_THROUGHPUT="${MAX_THROUGHPUT:-20000}"
THROUGHPUT_UPDATE_ATTEMPTS="${THROUGHPUT_UPDATE_ATTEMPTS:-6}"
THROUGHPUT_UPDATE_RETRY_SECONDS="${THROUGHPUT_UPDATE_RETRY_SECONDS:-15}"

if [[ "${1:-}" != "--" || "$#" -lt 2 ]]; then
  echo "Usage: $0 -- <benchmark command> [args...]" >&2
  exit 2
fi
shift

if ! [[ "$TARGET_THROUGHPUT" =~ ^[0-9]+$ ]] \
  || (( TARGET_THROUGHPUT < 1000 || TARGET_THROUGHPUT > MAX_THROUGHPUT || TARGET_THROUGHPUT % 100 != 0 )); then
  echo "TARGET_THROUGHPUT must be a multiple of 100 between 1000 and ${MAX_THROUGHPUT}" >&2
  exit 2
fi
if ! [[ "$THROUGHPUT_UPDATE_ATTEMPTS" =~ ^[1-9][0-9]*$ ]] \
  || ! [[ "$THROUGHPUT_UPDATE_RETRY_SECONDS" =~ ^[0-9]+$ ]]; then
  echo "THROUGHPUT_UPDATE_ATTEMPTS must be positive and THROUGHPUT_UPDATE_RETRY_SECONDS must be non-negative" >&2
  exit 2
fi

command -v az >/dev/null 2>&1 || {
  echo "Azure CLI (az) is required" >&2
  exit 1
}
az account show >/dev/null

update_throughput() {
  local requested_throughput="$1"
  local attempt

  for ((attempt = 1; attempt <= THROUGHPUT_UPDATE_ATTEMPTS; attempt++)); do
    if az cosmosdb mongodb database throughput update \
      --account-name "$COSMOS_MONGODB_ACCOUNT" \
      --resource-group "$RESOURCE_GROUP" \
      --name "$DATABASE_NAME" \
      --throughput "$requested_throughput" \
      --output none; then
      return 0
    fi

    if (( attempt < THROUGHPUT_UPDATE_ATTEMPTS )); then
      echo "Throughput update attempt ${attempt}/${THROUGHPUT_UPDATE_ATTEMPTS} failed; retrying in ${THROUGHPUT_UPDATE_RETRY_SECONDS}s" >&2
      sleep "$THROUGHPUT_UPDATE_RETRY_SECONDS"
    fi
  done

  return 1
}

current_throughput="$(
  az cosmosdb mongodb database throughput show \
    --account-name "$COSMOS_MONGODB_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$DATABASE_NAME" \
    --query resource.throughput \
    --output tsv
)"

if ! [[ "$current_throughput" =~ ^[0-9]+$ ]]; then
  echo "Could not determine current manual throughput for ${DATABASE_NAME}" >&2
  exit 1
fi

restore_throughput() {
  local exit_code=$?
  trap - EXIT INT TERM HUP

  if [[ "$current_throughput" != "$TARGET_THROUGHPUT" ]]; then
    echo "==> Restoring Cosmos MongoDB throughput to ${current_throughput} RU/s"
    update_throughput "$current_throughput" \
      || echo "WARNING: automatic throughput restore failed; restore ${DATABASE_NAME} to ${current_throughput} RU/s manually" >&2
  fi

  exit "$exit_code"
}
trap restore_throughput EXIT INT TERM HUP

if [[ "$current_throughput" != "$TARGET_THROUGHPUT" ]]; then
  echo "==> Scaling Cosmos MongoDB throughput: ${current_throughput} -> ${TARGET_THROUGHPUT} RU/s"
  update_throughput "$TARGET_THROUGHPUT"
fi

applied_throughput="$(
  az cosmosdb mongodb database throughput show \
    --account-name "$COSMOS_MONGODB_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" \
    --name "$DATABASE_NAME" \
    --query resource.throughput \
    --output tsv
)"

if [[ "$applied_throughput" != "$TARGET_THROUGHPUT" ]]; then
  echo "Cosmos MongoDB reported ${applied_throughput} RU/s after requesting ${TARGET_THROUGHPUT}" >&2
  exit 1
fi

echo "==> Running benchmark at ${applied_throughput} RU/s"
"$@"
