#!/usr/bin/env bash
# Provisions the Azure resources used by BatchEligibility in production:
#   - Service Bus queue `batch-eligibility` with MaxDeliveryCount=10
#   - Blob container `batch-eligibility` with a 7-day lifecycle rule
#
# The job store is the MongoDB collection BatchEligibility:MongoDb:Collection
# (default `batch-jobs`); eligibility-service creates it and its TTL index at
# start-up, so there is nothing to provision for it here.
#
# Requires az CLI logged in. Reads the following env vars:
#   SERVICEBUS_NAMESPACE
#   STORAGE_ACCOUNT
#   RESOURCE_GROUP
set -euo pipefail

SB_QUEUE="${SB_QUEUE:-batch-eligibility}"
BLOB_CONTAINER="${BLOB_CONTAINER:-batch-eligibility}"

: "${SERVICEBUS_NAMESPACE:?SERVICEBUS_NAMESPACE is required}"
: "${STORAGE_ACCOUNT:?STORAGE_ACCOUNT is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"

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
