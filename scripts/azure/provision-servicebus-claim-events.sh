#!/usr/bin/env bash
# Provisions the Azure Service Bus namespace + the claim-version-events
# topic/subscription/rule that claims-service's adjudication pipeline
# (capability 5.5) consumes via IMessageBus. Mirrors the resource shapes
# already declared in infrastructure/azure/main.bicep (sbTopicClaimVersionEvents
# / sbTopicClaimVersionEventsSubAdjudication / ...Rule) so a targeted CLI
# provision here and a future full Bicep deploy converge on the same config.
#
# Idempotent: re-running against existing resources updates mutable fields
# without erroring.
#
# Required env vars:
#   SERVICEBUS_NAMESPACE  — the SB namespace name (created if missing)
#   RESOURCE_GROUP        — the resource group to create it in
#   LOCATION              — region for a new namespace (ignored if it exists)
set -euo pipefail

: "${SERVICEBUS_NAMESPACE:?SERVICEBUS_NAMESPACE is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"
: "${LOCATION:?LOCATION is required}"

TOPIC_NAME="claim-version-events"
SUBSCRIPTION_NAME="adjudication-orchestrator"
RULE_NAME="submitted-only"
DEDUP_WINDOW_ISO="PT1H"
MESSAGE_TTL_ISO="P14D"
LOCK_DURATION_ISO="PT5M"
MAX_DELIVERY=10

echo "==> Service Bus namespace: ${SERVICEBUS_NAMESPACE} (${RESOURCE_GROUP}/${LOCATION})"
if az servicebus namespace show \
    --name "$SERVICEBUS_NAMESPACE" \
    --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "    exists"
else
  echo "    creating (Standard tier — required for topics/subscriptions)"
  az servicebus namespace create \
    --name "$SERVICEBUS_NAMESPACE" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku Standard >/dev/null
fi

echo "==> Topic: ${TOPIC_NAME}"
if az servicebus topic show \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --name "$TOPIC_NAME" \
    --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "    exists; updating mutable fields"
  az servicebus topic update \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --name "$TOPIC_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --max-size 1024 \
    --default-message-time-to-live "$MESSAGE_TTL_ISO" >/dev/null
else
  echo "    creating"
  az servicebus topic create \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --name "$TOPIC_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --max-size 1024 \
    --default-message-time-to-live "$MESSAGE_TTL_ISO" \
    --enable-duplicate-detection true \
    --duplicate-detection-history-time-window "$DEDUP_WINDOW_ISO" >/dev/null
fi

echo "==> Subscription: ${SUBSCRIPTION_NAME}"
if az servicebus topic subscription show \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --topic-name "$TOPIC_NAME" \
    --name "$SUBSCRIPTION_NAME" \
    --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "    exists; updating mutable fields"
  az servicebus topic subscription update \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --topic-name "$TOPIC_NAME" \
    --name "$SUBSCRIPTION_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --max-delivery-count "$MAX_DELIVERY" \
    --lock-duration "$LOCK_DURATION_ISO" \
    --enable-dead-lettering-on-message-expiration true \
    --dead-letter-on-filter-exceptions true >/dev/null
else
  echo "    creating"
  az servicebus topic subscription create \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --topic-name "$TOPIC_NAME" \
    --name "$SUBSCRIPTION_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --max-delivery-count "$MAX_DELIVERY" \
    --lock-duration "$LOCK_DURATION_ISO" \
    --enable-dead-lettering-on-message-expiration true \
    --dead-letter-on-filter-exceptions true >/dev/null
fi

# The default $Default rule (match-all) ships with every new subscription.
# Replace it with the MessageType correlation filter so this subscription
# only ever receives ClaimVersionSubmitted messages -- Adjudicated/Reversed
# messages on the same topic are for future subscriptions (5.10/5.12), not
# this one.
echo "==> Subscription rule: ${RULE_NAME} (MessageType=ClaimVersionSubmitted)"
if az servicebus topic subscription rule show \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --topic-name "$TOPIC_NAME" \
    --subscription-name "$SUBSCRIPTION_NAME" \
    --name "$RULE_NAME" \
    --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "    exists"
else
  echo "    creating"
  az servicebus topic subscription rule create \
    --namespace-name "$SERVICEBUS_NAMESPACE" \
    --topic-name "$TOPIC_NAME" \
    --subscription-name "$SUBSCRIPTION_NAME" \
    --name "$RULE_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --filter-type CorrelationFilter \
    --correlation-filter-property MessageType=ClaimVersionSubmitted >/dev/null

  if az servicebus topic subscription rule show \
      --namespace-name "$SERVICEBUS_NAMESPACE" \
      --topic-name "$TOPIC_NAME" \
      --subscription-name "$SUBSCRIPTION_NAME" \
      --name '$Default' \
      --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
    echo "    removing default match-all rule"
    az servicebus topic subscription rule delete \
      --namespace-name "$SERVICEBUS_NAMESPACE" \
      --topic-name "$TOPIC_NAME" \
      --subscription-name "$SUBSCRIPTION_NAME" \
      --name '$Default' \
      --resource-group "$RESOURCE_GROUP" >/dev/null
  fi
fi

echo "==> Done."
