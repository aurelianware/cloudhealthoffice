#!/usr/bin/env bash
# Provisions the Azure resources used by BatchEligibility in production:
#   - Cosmos DB container `batch-jobs`, partition key /tenantId, TTL 7 days
#   - Service Bus queue `batch-eligibility` with MaxDeliveryCount=10
#   - Blob container `batch-eligibility` with a 7-day lifecycle rule
#
# Requires az CLI logged in. Reads the following env vars:
#   COSMOS_ACCOUNT, COSMOS_DB (default: cho)
#   SERVICEBUS_NAMESPACE
#   STORAGE_ACCOUNT
#   RESOURCE_GROUP
set -euo pipefail

COSMOS_DB="${COSMOS_DB:-cho}"
COSMOS_CONTAINER="${COSMOS_CONTAINER:-batch-jobs}"
SB_QUEUE="${SB_QUEUE:-batch-eligibility}"
BLOB_CONTAINER="${BLOB_CONTAINER:-batch-eligibility}"
TTL_SECONDS=$((7 * 24 * 3600))  # 7 days

: "${COSMOS_ACCOUNT:?COSMOS_ACCOUNT is required}"
: "${SERVICEBUS_NAMESPACE:?SERVICEBUS_NAMESPACE is required}"
: "${STORAGE_ACCOUNT:?STORAGE_ACCOUNT is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"

echo "==> Cosmos DB: container $COSMOS_CONTAINER in $COSMOS_ACCOUNT/$COSMOS_DB"
az cosmosdb sql container create \
  --account-name "$COSMOS_ACCOUNT" \
  --database-name "$COSMOS_DB" \
  --name "$COSMOS_CONTAINER" \
  --partition-key-path "/tenantId" \
  --ttl "$TTL_SECONDS" \
  --resource-group "$RESOURCE_GROUP"

echo "==> Service Bus: queue $SB_QUEUE in $SERVICEBUS_NAMESPACE"
az servicebus queue create \
  --namespace-name "$SERVICEBUS_NAMESPACE" \
  --name "$SB_QUEUE" \
  --max-delivery-count 10 \
  --enable-dead-lettering-on-message-expiration true \
  --resource-group "$RESOURCE_GROUP"

echo "==> Storage: container $BLOB_CONTAINER in $STORAGE_ACCOUNT"
az storage container create \
  --account-name "$STORAGE_ACCOUNT" \
  --name "$BLOB_CONTAINER" \
  --auth-mode login

echo "==> Storage: 7-day lifecycle rule on $BLOB_CONTAINER"
cat > /tmp/batch-eligibility-lifecycle.json <<JSON
{
  "rules": [{
    "enabled": true,
    "name": "expire-batch-eligibility",
    "type": "Lifecycle",
    "definition": {
      "actions": { "baseBlob": { "delete": { "daysAfterModificationGreaterThan": 7 } } },
      "filters": { "blobTypes": ["blockBlob"], "prefixMatch": ["${BLOB_CONTAINER}/"] }
    }
  }]
}
JSON
az storage account management-policy create \
  --account-name "$STORAGE_ACCOUNT" \
  --policy @/tmp/batch-eligibility-lifecycle.json \
  --resource-group "$RESOURCE_GROUP"

echo "==> Done."
