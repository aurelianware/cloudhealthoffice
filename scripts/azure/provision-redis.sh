#!/usr/bin/env bash
# Provisions the Azure Cache for Redis instance consumed by ICacheProvider
# and by the two direct IConnectionMultiplexer callers (RedisAccumulatorService
# for atomic HINCRBYFLOAT, RedisPaRuleRepository for SCAN-based state flush).
#
# Single Standard-tier instance serves all three workloads; see
# docs/architecture/shared-cache.md for the decision tree that keeps the
# two exceptions off the ICacheProvider abstraction but on the same
# physical Redis.
#
# Idempotent: re-running on an existing instance updates mutable fields
# without errors.
#
# Required env vars:
#   REDIS_NAME            — the Azure Redis resource name (DNS-safe, globally unique)
#   RESOURCE_GROUP        — the resource group containing it
#   LOCATION              — Azure region (e.g. "eastus2") — only used on create
#
# Optional env vars:
#   REDIS_SKU             — Basic | Standard | Premium (default Standard;
#                            Standard gives us replica + SLA, Premium adds
#                            clustering/geo-replication which we don't need yet)
#   REDIS_VM_SIZE         — C0..C6 / P1..P5 (default C1 — 1 GiB, ~250 ops/s headroom)
set -euo pipefail

: "${REDIS_NAME:?REDIS_NAME is required}"
: "${RESOURCE_GROUP:?RESOURCE_GROUP is required}"

REDIS_SKU="${REDIS_SKU:-Standard}"
REDIS_VM_SIZE="${REDIS_VM_SIZE:-C1}"

echo "==> Azure Cache for Redis: ${REDIS_NAME} in ${RESOURCE_GROUP} (${REDIS_SKU} ${REDIS_VM_SIZE})"

if az redis show \
    --name "$REDIS_NAME" \
    --resource-group "$RESOURCE_GROUP" >/dev/null 2>&1; then
  echo "    exists; updating mutable fields"
  az redis update \
    --name "$REDIS_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --set enableNonSslPort=false minimumTlsVersion=1.2 >/dev/null
else
  : "${LOCATION:?LOCATION is required on first create}"
  echo "    creating in ${LOCATION}"
  az redis create \
    --name "$REDIS_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --sku "$REDIS_SKU" \
    --vm-size "$REDIS_VM_SIZE" \
    --enable-non-ssl-port false \
    --minimum-tls-version 1.2 >/dev/null
fi

echo "==> Done."
