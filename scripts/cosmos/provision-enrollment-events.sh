#!/usr/bin/env bash
#
# Provisions the Cosmos SQL container backing the enrollment-events stream.
#
# Requirements:
#   - Partition key path: /partitionKey (format "{tenantId}:{memberId}")
#   - Unique key policy on path /version → enforces at-most-one document per
#     (partitionKey, version) tuple so concurrent writers collide at index
#     time. EnrollmentEventRepository converts the 409/1009 result into a
#     version retry in EnrollmentEventPublisher.
#
# Unique key policies are IMMUTABLE after container creation. Migrating an
# existing container requires: create new container with the policy → dual-
# write or copy documents → swap reads → delete old container. In dev/staging
# a drop-and-recreate is acceptable.
#
# Usage:
#   scripts/cosmos/provision-enrollment-events.sh \
#     --account my-cosmos \
#     --resource-group rg-cho \
#     --database CloudHealthOffice \
#     [--container enrollment-events] \
#     [--throughput 400]
#
# Prereqs: Azure CLI logged in, cosmosdb-preview extension if needed.

set -euo pipefail

CONTAINER="enrollment-events"
THROUGHPUT=400

while [[ $# -gt 0 ]]; do
  case "$1" in
    --account)        ACCOUNT="$2"; shift 2 ;;
    --resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
    --database)       DATABASE="$2"; shift 2 ;;
    --container)      CONTAINER="$2"; shift 2 ;;
    --throughput)     THROUGHPUT="$2"; shift 2 ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

: "${ACCOUNT:?--account is required}"
: "${RESOURCE_GROUP:?--resource-group is required}"
: "${DATABASE:?--database is required}"

echo "==> Creating Cosmos SQL container '${CONTAINER}' on '${ACCOUNT}/${DATABASE}'"

az cosmosdb sql container create \
  --account-name "${ACCOUNT}" \
  --resource-group "${RESOURCE_GROUP}" \
  --database-name "${DATABASE}" \
  --name "${CONTAINER}" \
  --partition-key-path "/partitionKey" \
  --unique-key-policy '{"uniqueKeys":[{"paths":["/version"]}]}' \
  --throughput "${THROUGHPUT}"

echo "OK: ${CONTAINER} created with unique-key policy on /version"
