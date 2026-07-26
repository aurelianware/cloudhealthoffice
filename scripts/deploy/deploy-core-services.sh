#!/usr/bin/env bash
# deploy-core-services.sh
# Builds, pushes, and deploys the 10 core CHO backend services to AKS.
#
# Prerequisites:
#   - az cli authenticated (az login)
#   - kubectl configured for the target AKS cluster
#   - Docker running locally
#   - ACR credentials (az acr login)
#
# Usage:
#   ./scripts/deploy/deploy-core-services.sh                  # full deploy
#   ./scripts/deploy/deploy-core-services.sh --skip-build     # apply manifests only
#   ./scripts/deploy/deploy-core-services.sh --skip-seed      # skip seed data
#
# Environment variables:
#   ACR_NAME        — ACR login server (default: clouhealthoffice.azurecr.io)
#   K8S_NAMESPACE   — Kubernetes namespace (default: cloudhealthoffice)
#   IMAGE_TAG       — Image tag (default: latest)
#   SEED_TENANT_ID  — Tenant ID for seed data (default: dev-tenant)
#   SEED_GROUP      — Group number for seed data (default: GRP-DEV-2026)

set -euo pipefail

# ── Configuration ────────────────────────────────────────────────────────

ACR_NAME="${ACR_NAME:-clouhealthoffice.azurecr.io}"
K8S_NAMESPACE="${K8S_NAMESPACE:-cloudhealthoffice}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
SEED_TENANT_ID="${SEED_TENANT_ID:-dev-tenant}"
SEED_GROUP="${SEED_GROUP:-GRP-DEV-2026}"
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

SKIP_BUILD=false
SKIP_SEED=false
for arg in "$@"; do
  case "$arg" in
    --skip-build) SKIP_BUILD=true ;;
    --skip-seed)  SKIP_SEED=true ;;
  esac
done

# Core services in dependency order
SERVICES=(
  claims-service
  benefit-plan-service
  member-service
  provider-service
  authorization-service
  payment-service
  tenant-service
  eligibility-service
  coverage-service
  enrollment-import-service
  premium-billing-service
  appeals-service
  rfai-service
  claims-scrubbing-service
)

info()  { echo "▸ $*"; }
ok()    { echo "✓ $*"; }
fail()  { echo "✗ $*" >&2; exit 1; }

# ── Step 1: Build Docker images ─────────────────────────────────────────

if [ "$SKIP_BUILD" = false ]; then
  info "Building Docker images..."
  for svc in "${SERVICES[@]}"; do
    dockerfile="${REPO_ROOT}/src/services/${svc}/Dockerfile"
    image="${ACR_NAME}/cloudhealthoffice-${svc}:${IMAGE_TAG}"

    if [ ! -f "$dockerfile" ]; then
      fail "Dockerfile not found: ${dockerfile}"
    fi

    info "  Building ${svc}..."
    docker build \
      -t "$image" \
      -f "$dockerfile" \
      "$REPO_ROOT" \
      --quiet || fail "Build failed: ${svc}"
  done
  ok "All images built"

  # ── Step 2: Push to ACR ──────────────────────────────────────────────

  info "Pushing images to ACR (${ACR_NAME})..."
  for svc in "${SERVICES[@]}"; do
    image="${ACR_NAME}/cloudhealthoffice-${svc}:${IMAGE_TAG}"
    info "  Pushing ${svc}..."
    docker push "$image" --quiet || fail "Push failed: ${svc}"
  done
  ok "All images pushed"
else
  info "Skipping build (--skip-build)"
fi

# ── Step 3: Apply Kubernetes manifests ───────────────────────────────────

info "Applying Kubernetes manifests to namespace ${K8S_NAMESPACE}..."

# Ensure namespace exists
kubectl get namespace "$K8S_NAMESPACE" &>/dev/null || \
  kubectl create namespace "$K8S_NAMESPACE"

for svc in "${SERVICES[@]}"; do
  manifest="${REPO_ROOT}/src/services/${svc}/k8s/${svc}-deployment.yaml"

  if [ ! -f "$manifest" ]; then
    echo "  ⚠ Manifest not found: ${manifest} — skipping"
    continue
  fi

  # Replace image tag if not 'latest'
  if [ "$IMAGE_TAG" != "latest" ]; then
    sed "s|:latest|:${IMAGE_TAG}|g" "$manifest" | kubectl apply -f - -n "$K8S_NAMESPACE"
  else
    kubectl apply -f "$manifest" -n "$K8S_NAMESPACE"
  fi
  ok "  Applied ${svc}"
done

# Apply portal ConfigMap update (service URLs)
portal_manifest="${REPO_ROOT}/src/portal/CloudHealthOffice.Portal/k8s/portal-deployment.yaml"
if [ -f "$portal_manifest" ]; then
  kubectl apply -f "$portal_manifest" -n "$K8S_NAMESPACE"
  ok "  Applied portal (ConfigMap + Deployment)"
fi

ok "All manifests applied"

# ── Step 4: Wait for deployments ─────────────────────────────────────────

info "Waiting for deployments to become ready (timeout: 5m per service)..."
FAILED_DEPLOYS=()
for svc in "${SERVICES[@]}"; do
  if kubectl get deployment "$svc" -n "$K8S_NAMESPACE" &>/dev/null; then
    if kubectl rollout status deployment/"$svc" -n "$K8S_NAMESPACE" --timeout=300s; then
      ok "  ${svc} ready"
    else
      echo "  ⚠ ${svc} not ready within timeout"
      FAILED_DEPLOYS+=("$svc")
    fi
  fi
done

if [ ${#FAILED_DEPLOYS[@]} -gt 0 ]; then
  echo ""
  echo "⚠ The following deployments did not become ready:"
  for d in "${FAILED_DEPLOYS[@]}"; do echo "  - $d"; done
  echo ""
  echo "Check logs with: kubectl logs -n ${K8S_NAMESPACE} -l app=<service-name>"
fi

# ── Step 5: Seed data ───────────────────────────────────────────────────

if [ "$SKIP_SEED" = false ]; then
  info "Running seed data script..."

  # Find a running MongoDB pod (in-cluster or standalone)
  MONGO_POD=$(kubectl get pods -n "$K8S_NAMESPACE" -l app=mongodb -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)

  if [ -n "$MONGO_POD" ]; then
    # Copy seed scripts to the MongoDB pod
    kubectl cp "${REPO_ROOT}/scripts/setup/seed-demo-data.js" \
      "${K8S_NAMESPACE}/${MONGO_POD}:/tmp/seed-demo-data.js"
    kubectl cp "${REPO_ROOT}/scripts/setup/seed-demo-users.js" \
      "${K8S_NAMESPACE}/${MONGO_POD}:/tmp/seed-demo-users.js"

    # Run seed scripts
    kubectl exec -n "$K8S_NAMESPACE" "$MONGO_POD" -- \
      mongosh cloudhealthoffice --quiet \
      --eval "var tenantId=\"${SEED_TENANT_ID}\", groupNumber=\"${SEED_GROUP}\"" \
      /tmp/seed-demo-data.js

    kubectl exec -n "$K8S_NAMESPACE" "$MONGO_POD" -- \
      mongosh cloudhealthoffice --quiet \
      --eval "var tenantId=\"${SEED_TENANT_ID}\"" \
      /tmp/seed-demo-users.js

    ok "Seed data loaded for tenant: ${SEED_TENANT_ID}"
  else
    echo "  ⚠ No MongoDB pod found in namespace ${K8S_NAMESPACE} — skipping seed"
    echo "    Run manually: mongosh <connection> scripts/setup/seed-demo-data.js --eval 'var tenantId=\"${SEED_TENANT_ID}\", groupNumber=\"${SEED_GROUP}\"'"
  fi
else
  info "Skipping seed data (--skip-seed)"
fi

# ── Step 6: Verify health endpoints ─────────────────────────────────────

info "Verifying health endpoints..."
echo ""
printf "  %-28s %-10s %s\n" "SERVICE" "STATUS" "ENDPOINT"
printf "  %-28s %-10s %s\n" "-------" "------" "--------"

for svc in "${SERVICES[@]}"; do
  # Port-forward briefly to check health
  LOCAL_PORT=$((30000 + RANDOM % 10000))
  kubectl port-forward -n "$K8S_NAMESPACE" "svc/${svc}" "${LOCAL_PORT}:80" &>/dev/null &
  PF_PID=$!
  sleep 1

  HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "http://localhost:${LOCAL_PORT}/health/live" 2>/dev/null || echo "000")
  kill "$PF_PID" 2>/dev/null || true
  wait "$PF_PID" 2>/dev/null || true

  if [ "$HTTP_CODE" = "200" ]; then
    STATUS="✓ Healthy"
  elif [ "$HTTP_CODE" = "000" ]; then
    STATUS="⚠ No route"
  else
    STATUS="✗ HTTP ${HTTP_CODE}"
  fi

  printf "  %-28s %-10s %s\n" "$svc" "$STATUS" "http://${svc}.${K8S_NAMESPACE}/health/live"
done

echo ""
ok "Deployment complete"
echo ""
echo "Portal ConfigMap updated — restart portal to pick up new service URLs:"
echo "  kubectl rollout restart deployment/portal -n ${K8S_NAMESPACE}"
