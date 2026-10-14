#!/usr/bin/env bash
# port-forward.sh — Open local ports to the Cloud Health Office demo on GKE.
#
# The demo cluster has no public ingress (it trusts the public development
# signing key), so the presenter reaches it through kubectl port-forward.
# Leave this running in its own terminal during the demo; Ctrl-C closes all.
#
#   Portal                http://localhost:8080
#   claims-service        http://localhost:5001   (swagger: /swagger)
#   benefit-plan-service  http://localhost:5002
#   fhir-service          http://localhost:5023   (/fhir/r4/adapter-status)

set -euo pipefail
NAMESPACE="${NAMESPACE:-cloudhealthoffice}"

forwards=(
  "portal 8080"
  "claims-service 5001"
  "benefit-plan-service 5002"
  "fhir-service 5023"
)

pids=()
cleanup() { kill "${pids[@]}" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

for entry in "${forwards[@]}"; do
  read -r svc port <<<"$entry"
  # Restart a forward if its connection drops (common on long-idle sessions).
  ( while true; do
      kubectl port-forward -n "$NAMESPACE" "svc/$svc" "$port:80" >/dev/null 2>&1 || true
      sleep 1
    done ) &
  pids+=($!)
  echo "  $svc → http://localhost:$port"
done

echo
echo "Forwarding. Ctrl-C to stop."
wait
