#!/opt/homebrew/bin/bash
# deploy-local.sh — Build and deploy the full CloudHealthOffice platform
# to Docker Desktop Kubernetes for local development.
#
# Usage:
#   ./scripts/deploy-local.sh              # build + deploy everything
#   ./scripts/deploy-local.sh --skip-build # deploy only (images already built)
#   ./scripts/deploy-local.sh --only-build # build images without deploying
#
# Prerequisites:
#   - Docker Desktop with Kubernetes enabled
#   - kubectl context set to docker-desktop

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

SKIP_BUILD=false
ONLY_BUILD=false
NAMESPACE="cloudhealthoffice"
IMAGE_PREFIX="cloudhealthoffice"
KIND_CLUSTER_NAME=""
LOCAL_DOTNET_REGISTRY="${LOCAL_DOTNET_REGISTRY:-mcr.microsoft.com}"

# Defaults — overridden by .env.local if present
MONGO_USER="admin"
MONGO_PASS="localdev123"
STRIPE_PUBLISHABLE_KEY="pk_test_local"
STRIPE_SECRET_KEY="sk_test_local"
STRIPE_STARTER_PRICE_ID="price_local_starter"
STRIPE_PROFESSIONAL_PRICE_ID="price_local_pro"
AZURE_AD_CLIENT_ID="local-dev"
AZURE_AD_CLIENT_SECRET="local-dev"
AZURE_AD_TENANT_ID="common"
AZURE_AD_INSTANCE="https://login.microsoftonline.com/"
AZURE_AD_AUDIENCE="api://local-dev"
AZURE_STORAGE_CONNECTION_STRING="UseDevelopmentStorage=true"
PRICING_API_KEY="local-dev-key"
PRICING_API_ADMIN_SECRET="local-dev-admin"
REDIS_CONNECTION_STRING=""  # auto-set below if empty

# Source local overrides (real credentials for auth/payments)
if [[ -f .env.local ]]; then
  set -a
  source .env.local
  set +a
  echo "Loaded credentials from .env.local"
fi

while [[ $# -gt 0 ]]; do
  case $1 in
    --skip-build) SKIP_BUILD=true; shift ;;
    --only-build) ONLY_BUILD=true; shift ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

log()  { echo -e "\n\033[1;36m▶ $*\033[0m"; }
ok()   { echo "  ✓ $*"; }
err()  { echo "  ✗ $*" >&2; exit 1; }
warn() { echo "  ⚠ $*"; }

# ── Preflight checks ─────────────────────────────────────────────────────────
log "Preflight checks"
kubectl config current-context | grep -q "docker-desktop" \
  || warn "Context is '$(kubectl config current-context)' — expected 'docker-desktop'"
kubectl cluster-info > /dev/null 2>&1 || err "Kubernetes cluster not reachable"
ok "Cluster reachable"

CURRENT_CONTEXT="$(kubectl config current-context)"
if [[ "$CURRENT_CONTEXT" == kind-* ]]; then
  KIND_CLUSTER_NAME="${CURRENT_CONTEXT#kind-}"
  ok "kind cluster detected: $KIND_CLUSTER_NAME"
fi

load_kind_image() {
  local image="$1"
  if [[ -n "$KIND_CLUSTER_NAME" ]]; then
    kind load docker-image "$image" --name "$KIND_CLUSTER_NAME" \
      && ok "loaded $image into kind" || warn "failed to load $image into kind"
  fi
}

# ── Services to build ─────────────────────────────────────────────────────────
# Maps: service-name -> Dockerfile path (relative to repo root)
declare -A SERVICES=(
  [member-service]="src/services/member-service/Dockerfile"
  [coverage-service]="src/services/coverage-service/Dockerfile"
  [claims-service]="src/services/claims-service/Dockerfile"
  [eligibility-service]="src/services/eligibility-service/Dockerfile"
  [authorization-service]="src/services/authorization-service/Dockerfile"
  [attachment-service]="src/services/attachment-service/Dockerfile"
  [provider-service]="src/services/provider-service/Dockerfile"
  [reference-data-service]="src/services/reference-data-service/Dockerfile"
  [sponsor-service]="src/services/sponsor-service/Dockerfile"
  [claims-scrubbing-service]="src/services/claims-scrubbing-service/Dockerfile"
  [enrollment-import-service]="src/services/enrollment-import-service/Dockerfile"
  [trading-partner-service]="src/services/trading-partner-service/Dockerfile"
  [tenant-service]="src/services/tenant-service/Dockerfile"
  [benefit-plan-service]="src/services/benefit-plan-service/Dockerfile"
  [payment-service]="src/services/payment-service/Dockerfile"
  [fhir-service]="src/services/fhir-service/Dockerfile"
  [smart-auth-service]="src/services/smart-auth-service/Dockerfile"
  [encounter-service]="src/services/encounter-service/Dockerfile"
  [appeals-service]="src/services/appeals-service/Dockerfile"
  [capitation-service]="src/services/capitation-service/Dockerfile"
  [premium-billing-service]="src/services/premium-billing-service/Dockerfile"
  [risk-adjustment-service]="src/services/risk-adjustment-service/Dockerfile"
  [rfai-service]="src/services/rfai-service/Dockerfile"
  [pricing-api]="src/services/CloudHealthOffice.PricingApi/Dockerfile"
)

# ── Build images ──────────────────────────────────────────────────────────────
if [[ "$SKIP_BUILD" == false ]]; then
  log "Building Docker images (this will take a while the first time)"

  # Portal — uses repo root as build context
  log "Building portal"
  docker build -t ghcr.io/aurelianware/${IMAGE_PREFIX}-portal:latest \
    --build-arg REGISTRY="$LOCAL_DOTNET_REGISTRY" \
    -f src/portal/CloudHealthOffice.Portal/Dockerfile . \
    && { ok "portal"; load_kind_image "ghcr.io/aurelianware/${IMAGE_PREFIX}-portal:latest"; } || warn "portal build failed"

  # Site
  log "Building site"
  docker build -t ${IMAGE_PREFIX}-site:latest \
    -f src/site/Dockerfile src/site/ \
    && { ok "site"; load_kind_image "${IMAGE_PREFIX}-site:latest"; } || warn "site build failed"

  # Microservices — all use repo root as build context (Dockerfiles COPY from src/services/...)
  log "Building microservices"
  for svc in "${!SERVICES[@]}"; do
    dockerfile="${SERVICES[$svc]}"
    if [[ ! -f "$dockerfile" ]]; then
      warn "$svc — Dockerfile not found at $dockerfile, skipping"
      continue
    fi

    # Tag with both ACR and GHCR names so k8s manifests find the image
    acr_tag="choacrhy6h2vdulfru6.azurecr.io/${IMAGE_PREFIX}-${svc}:latest"
    ghcr_tag="ghcr.io/aurelianware/${IMAGE_PREFIX}-${svc}:latest"

    docker build -t "$acr_tag" -t "$ghcr_tag" \
      --build-arg REGISTRY="$LOCAL_DOTNET_REGISTRY" \
      -f "$dockerfile" . \
      && { ok "$svc"; load_kind_image "$acr_tag"; } || warn "$svc build failed"
  done
fi

[[ "$ONLY_BUILD" == true ]] && { echo -e "\n✅ Images built. Run with --skip-build to deploy."; exit 0; }

# ── Create namespace ──────────────────────────────────────────────────────────
log "Creating namespace"
kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f -
ok "$NAMESPACE"

# Remove prod-only restrictions that break local dev
kubectl delete resourcequota compute-resources -n "$NAMESPACE" 2>/dev/null || true
kubectl delete networkpolicy allow-ingress-from-portal -n "$NAMESPACE" 2>/dev/null || true
ok "removed ResourceQuota and NetworkPolicy (not needed locally)"

# ── Create secrets ────────────────────────────────────────────────────────────
log "Creating secrets"
MONGO_CONN="mongodb://${MONGO_USER}:${MONGO_PASS}@mongodb.${NAMESPACE}.svc.cluster.local:27017/?authSource=admin"

# MongoDB auth
kubectl create secret generic mongodb-auth \
  --namespace "$NAMESPACE" \
  --from-literal=username="$MONGO_USER" \
  --from-literal=password="$MONGO_PASS" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "mongodb-auth"

# Database connection string (used by all services — some reference 'key' or 'endpoint')
kubectl create secret generic database-secret \
  --namespace "$NAMESPACE" \
  --from-literal=connectionString="$MONGO_CONN" \
  --from-literal=endpoint="$MONGO_CONN" \
  --from-literal=key="$MONGO_PASS" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "database-secret"

# CosmosDB secret alias (portal uses this key name)
kubectl create secret generic cosmosdb-secret \
  --namespace "$NAMESPACE" \
  --from-literal=connectionString="$MONGO_CONN" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "cosmosdb-secret"

# Redis connection
[[ -z "$REDIS_CONNECTION_STRING" ]] && REDIS_CONNECTION_STRING="redis-dataprotection.${NAMESPACE}.svc.cluster.local:6379"
kubectl create secret generic redis-secret \
  --namespace "$NAMESPACE" \
  --from-literal=connectionString="$REDIS_CONNECTION_STRING" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "redis-secret"

# Azure AD config
kubectl create secret generic azure-ad-config \
  --namespace "$NAMESPACE" \
  --from-literal=clientId="$AZURE_AD_CLIENT_ID" \
  --from-literal=ClientId="$AZURE_AD_CLIENT_ID" \
  --from-literal=clientSecret="$AZURE_AD_CLIENT_SECRET" \
  --from-literal=ClientSecret="$AZURE_AD_CLIENT_SECRET" \
  --from-literal=tenantId="$AZURE_AD_TENANT_ID" \
  --from-literal=TenantId="$AZURE_AD_TENANT_ID" \
  --from-literal=Instance="$AZURE_AD_INSTANCE" \
  --from-literal=Audience="$AZURE_AD_AUDIENCE" \
  --from-literal=audience="$AZURE_AD_AUDIENCE" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "azure-ad-config"

# Azure Storage
kubectl create secret generic azure-storage-secret \
  --namespace "$NAMESPACE" \
  --from-literal=connectionString="$AZURE_STORAGE_CONNECTION_STRING" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "azure-storage-secret"

# Stripe API keys
kubectl create secret generic stripe-api-keys \
  --namespace "$NAMESPACE" \
  --from-literal=publishable-key="$STRIPE_PUBLISHABLE_KEY" \
  --from-literal=secret-key="$STRIPE_SECRET_KEY" \
  --from-literal=starter-price-id="$STRIPE_STARTER_PRICE_ID" \
  --from-literal=professional-price-id="$STRIPE_PROFESSIONAL_PRICE_ID" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "stripe-api-keys"

# Pricing API secret
kubectl create secret generic pricing-api-secret \
  --namespace "$NAMESPACE" \
  --from-literal=apiKey="$PRICING_API_KEY" \
  --from-literal=AdminSecret="$PRICING_API_ADMIN_SECRET" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "pricing-api-secret"

# Portal email stubs for local development
kubectl create secret generic email-config \
  --namespace "$NAMESPACE" \
  --from-literal=SmtpHost=localhost \
  --from-literal=SmtpPort=1025 \
  --from-literal=EnableSsl=false \
  --from-literal=FromAddress=local-dev@cloudhealthoffice.local \
  --from-literal=SalesTeamAddress=sales@cloudhealthoffice.local \
  --from-literal=Username=local-dev \
  --from-literal=Password=local-dev \
  --dry-run=client -o yaml | kubectl apply -f -
ok "email-config"

# ── Deploy infrastructure ────────────────────────────────────────────────────
log "Deploying MongoDB"
kubectl apply -f infrastructure/k8s/mongodb-deployment.yaml
ok "mongodb"

log "Deploying Redis"
kubectl apply -f infrastructure/k8s/redis-dataprotection.yaml
ok "redis"

log "Waiting for MongoDB to be ready"
kubectl rollout status statefulset/mongodb -n "$NAMESPACE" --timeout=120s || warn "MongoDB not ready yet"

# ── Seed demo data ────────────────────────────────────────────────────────────
log "Seeding demo data into MongoDB"
kubectl exec -n "$NAMESPACE" mongodb-0 -- mongosh \
  --username "$MONGO_USER" --password "$MONGO_PASS" --authenticationDatabase admin \
  --eval '
    db = db.getSiblingDB("cloudhealthoffice");

    // Demo tenant
    db.tenants.updateOne(
      { azureTenantId: "demo" },
      { $set: {
          azureTenantId: "demo",
          organizationName: "Demo Health Plan",
          tier: "enterprise",
          isDemo: true,
          subscriptionStatus: "Active",
          createdAt: new Date()
      }},
      { upsert: true }
    );

    // Demo member
    db.members.updateOne(
      { memberId: "MBR001" },
      { $set: {
          memberId: "MBR001",
          subscriberId: "SUB001",
          firstName: "Jane",
          lastName: "Doe",
          dateOfBirth: ISODate("1985-03-15"),
          gender: "female",
          tenantId: "demo",
          status: "Active",
          createdAt: new Date()
      }},
      { upsert: true }
    );

    print("✓ Demo data seeded");
  ' 2>/dev/null && ok "seeded" || warn "seed failed (MongoDB may still be starting)"

# Local MongoDB can retain malformed seed rows from previous interrupted runs.
kubectl exec -n "$NAMESPACE" mongodb-0 -- mongosh \
  --username "$MONGO_USER" --password "$MONGO_PASS" --authenticationDatabase admin --quiet \
  --eval 'db = db.getSiblingDB("cloudhealthoffice"); db.prior_auth_rules.deleteMany({ _id: "" });' \
  >/dev/null 2>&1 || true

# ── Deploy all services ───────────────────────────────────────────────────────
log "Deploying microservices"

# Patch manifests to use Never pull policy (images are local)
deploy_service() {
  local manifest="$1"
  local name="$2"
  if [[ -f "$manifest" ]]; then
    # Replace imagePullPolicy: Always with Never for local images
    sed 's/imagePullPolicy: Always/imagePullPolicy: IfNotPresent/g' "$manifest" \
      | kubectl apply -f - 2>/dev/null \
      && ok "$name" || warn "$name failed"
  else
    warn "$name — manifest not found: $manifest"
  fi
}

# Services with k8s manifests in src/services/
for svc_dir in src/services/*/k8s/; do
  svc_name=$(basename "$(dirname "$svc_dir")")
  manifest=$(find "$svc_dir" -name "*.yaml" -o -name "*.yml" | head -1)
  [[ -n "$manifest" ]] && deploy_service "$manifest" "$svc_name"
done

# Pricing API (different path)
deploy_service "src/services/CloudHealthOffice.PricingApi/k8s/pricing-api-deployment.yaml" "pricing-api"

# Local-only environment relaxations. Claims stays Production because it has
# Development DI scope validation issues in its hosted index initializers.
kubectl patch configmap member-service-config -n "$NAMESPACE" --type merge \
  -p '{"data":{"ASPNETCORE_ENVIRONMENT":"Development"}}' >/dev/null 2>&1 || true
kubectl patch configmap benefit-plan-service-config -n "$NAMESPACE" --type merge \
  -p '{"data":{"ASPNETCORE_ENVIRONMENT":"Development"}}' >/dev/null 2>&1 || true

# ── Deploy portal ─────────────────────────────────────────────────────────────
log "Deploying portal"
sed 's/imagePullPolicy: Always/imagePullPolicy: IfNotPresent/g' \
  src/portal/CloudHealthOffice.Portal/k8s/portal-deployment.yaml \
  | kubectl apply -f - \
  && ok "portal" || warn "portal deploy failed"

kubectl patch configmap portal-config -n "$NAMESPACE" --type merge \
  -p '{"data":{"ASPNETCORE_ENVIRONMENT":"Development"}}' >/dev/null 2>&1 || true
kubectl delete hpa portal-hpa -n "$NAMESPACE" 2>/dev/null || true
kubectl set env deployment/portal -n "$NAMESPACE" XDG_DATA_HOME=/tmp >/dev/null 2>&1 || true
kubectl scale deployment/portal -n "$NAMESPACE" --replicas=1 >/dev/null 2>&1 || true

# ── Wait for rollouts ────────────────────────────────────────────────────────
log "Waiting for core services to start"
for dep in member-service claims-service benefit-plan-service portal; do
  kubectl rollout status deployment/$dep -n "$NAMESPACE" --timeout=90s 2>/dev/null \
    && ok "$dep" || warn "$dep not ready yet"
done

# ── Port forwarding ──────────────────────────────────────────────────────────
log "Setting up port forwards"
echo "  Run these in separate terminals (or use '&' to background):"
echo ""
echo "  # Portal (Blazor Server)"
echo "  kubectl port-forward -n $NAMESPACE svc/portal 8080:80"
echo ""
echo "  # Claims Service"
echo "  kubectl port-forward -n $NAMESPACE svc/claims-service 5001:80"
echo ""
echo "  # Benefit Plan Service"
echo "  kubectl port-forward -n $NAMESPACE svc/benefit-plan-service 5002:80"
echo ""
echo "  # Member Service"
echo "  kubectl port-forward -n $NAMESPACE svc/member-service 5003:80"
echo ""
echo "  # MongoDB (for direct access)"
echo "  kubectl port-forward -n $NAMESPACE svc/mongodb 27017:27017"
echo ""

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo "═══════════════════════════════════════════════════════"
echo " CloudHealthOffice deployed to docker-desktop"
echo "═══════════════════════════════════════════════════════"
echo ""
echo " Namespace: $NAMESPACE"
echo ""
echo " Quick checks:"
echo "   kubectl get pods -n $NAMESPACE"
echo "   kubectl logs -n $NAMESPACE -l app=portal --tail=20"
echo ""
echo " After port-forwarding:"
echo "   Portal:  http://localhost:8080"
echo "   Claims:  http://localhost:5001/swagger"
echo "   Members: http://localhost:5003/swagger"
echo ""
echo " Seed + test adjudication:"
echo "   CLAIMS_URL=http://localhost:5001 BENEFIT_URL=http://localhost:5002 ./scripts/seed-local.sh"
echo ""
echo "═══════════════════════════════════════════════════════"
