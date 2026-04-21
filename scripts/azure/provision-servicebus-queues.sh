#!/usr/bin/env bash
# Provisions the Azure Service Bus queues consumed by IMessageBus.
#
# Creates the two queues the platform uses today:
#   - batch-eligibility      (eligibility-service IBatchQueue)
#   - qnxt-idcard-requests   (idcard-service IQnxtMirrorQueue)
#
# Each queue is created with:
#   - RequiresDuplicateDetection = true  (SendOptions.MessageId dedup)
#   - Duplicate detection window = 1 hour (matches MessagingOptions default)
#   - MaxDeliveryCount = 10              (Azure default; matches current code)
#   - DeadLetteringOnMessageExpiration = true
#
# Idempotent: re-running on an existing queue updates mutable fields
# without errors.
#
# Required env vars:
#   SERVICEBUS_NAMESPACE  — the SB namespace name
#   RESOURCE_GROUP        — the resource group containing it
set -euo pipefail

: "${SERVICEBUS_NAMESPACE:?SERVICEBUS_NAMESPACE is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"

DEDUP_WINDOW_ISO="PT1H"
MAX_DELIVERY=10

# Queue names are frozen production names. Do NOT rename without a
# coordinated migration — they appear in live ServiceBusSender calls.
QUEUES=(
  "batch-eligibility"
  "qnxt-idcard-requests"
)

for queue in "${QUEUES[@]}"; do
  echo "==> Service Bus: queue ${queue} in ${SERVICEBUS_NAMESPACE}"
  if az servicebus queue show \
      --namespace-name "$SERVICEBUS_NAMESPACE" \
      --name "$queue" \
      --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
    echo "    exists; updating mutable fields"
    az servicebus queue update \
      --namespace-name "$SERVICEBUS_NAMESPACE" \
      --name "$queue" \
      --resource-group "$RESOURCE_GROUP" \
      --max-delivery-count "$MAX_DELIVERY" \
      --enable-dead-lettering-on-message-expiration true >/dev/null
  else
    echo "    creating"
    az servicebus queue create \
      --namespace-name "$SERVICEBUS_NAMESPACE" \
      --name "$queue" \
      --resource-group "$RESOURCE_GROUP" \
      --max-delivery-count "$MAX_DELIVERY" \
      --enable-duplicate-detection true \
      --duplicate-detection-history-time-window "$DEDUP_WINDOW_ISO" \
      --enable-dead-lettering-on-message-expiration true >/dev/null
  fi
done

echo "==> Done."
