#!/usr/bin/env bash
# load-synthea.sh — Starts HAPI FHIR server and loads Synthea bundles on first boot.
# The data-loaded flag file prevents re-loading on subsequent restarts.

set -euo pipefail

DATA_DIR="/data/synthea"
LOADED_FLAG="/data/.synthea-loaded"
HAPI_BASE_URL="http://localhost:8080/fhir"
MAX_WAIT=120  # seconds to wait for HAPI to become ready

# ── Start HAPI in background ──────────────────────────────────────────────────
echo "[load-synthea] Starting HAPI FHIR server..."
/usr/local/bin/entrypoint.sh &
HAPI_PID=$!

# ── Wait for HAPI to be ready ─────────────────────────────────────────────────
echo "[load-synthea] Waiting for HAPI FHIR to be ready (up to ${MAX_WAIT}s)..."
elapsed=0
until curl -sf "${HAPI_BASE_URL}/metadata" -o /dev/null; do
  if [ $elapsed -ge $MAX_WAIT ]; then
    echo "[load-synthea] ERROR: HAPI FHIR did not start within ${MAX_WAIT} seconds."
    exit 1
  fi
  sleep 5
  elapsed=$((elapsed + 5))
  echo "[load-synthea] Still waiting... (${elapsed}s)"
done
echo "[load-synthea] HAPI FHIR is ready."

# ── Load Synthea bundles (once only) ─────────────────────────────────────────
if [ -f "${LOADED_FLAG}" ]; then
  echo "[load-synthea] Synthetic data already loaded. Skipping."
else
  if [ -d "${DATA_DIR}" ] && compgen -G "${DATA_DIR}/*.json" > /dev/null 2>&1; then
    echo "[load-synthea] Loading Synthea FHIR bundles from ${DATA_DIR}..."
    failed_bundles=0
    for bundle in "${DATA_DIR}"/*.json; do
      echo "[load-synthea]   POST ${bundle}"
      if ! curl -sf \
        -X POST "${HAPI_BASE_URL}" \
        -H "Content-Type: application/fhir+json" \
        --data-binary "@${bundle}" \
        -o /dev/null; then
        echo "[load-synthea]   ERROR: Failed to POST bundle ${bundle}"
        failed_bundles=$((failed_bundles + 1))
      fi
    done
    if [ "${failed_bundles}" -gt 0 ]; then
      echo "[load-synthea] Synthetic data load completed with ${failed_bundles} bundle error(s). Flag not set — will retry on next start."
    else
      touch "${LOADED_FLAG}"
      echo "[load-synthea] Synthetic data loaded successfully."
    fi
  else
    echo "[load-synthea] No Synthea bundles found in ${DATA_DIR}. Skipping data load (flag not set; will retry on next start)."
  fi
fi

# ── Hand off to HAPI foreground process ───────────────────────────────────────
echo "[load-synthea] Handing off to HAPI FHIR (PID ${HAPI_PID})..."
wait "${HAPI_PID}"
