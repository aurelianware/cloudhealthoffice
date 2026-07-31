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
LOCAL_CLAIMS_REPLICAS="${LOCAL_CLAIMS_REPLICAS:-2}"
LOCAL_BENEFIT_PLAN_REPLICAS="${LOCAL_BENEFIT_PLAN_REPLICAS:-3}"
LOCAL_ENABLE_AI_CLAIMS_EXAMINER="${LOCAL_ENABLE_AI_CLAIMS_EXAMINER:-false}"

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
LOCAL_SERVICEBUS_NAMESPACE=""
LOCAL_SERVICEBUS_RESOURCE_GROUP=""
LOCAL_SERVICEBUS_LOCATION=""
LOCAL_COSMOS_MONGODB_ACCOUNT=""
LOCAL_COSMOS_MONGODB_RESOURCE_GROUP=""

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
CURRENT_CONTEXT="$(kubectl config current-context)"
if [[ "$CURRENT_CONTEXT" == kind-* ]]; then
  KIND_CLUSTER_NAME="${CURRENT_CONTEXT#kind-}"
  ok "kind cluster detected: $KIND_CLUSTER_NAME"
elif [[ "$CURRENT_CONTEXT" != "docker-desktop" ]]; then
  warn "Context is '$CURRENT_CONTEXT' — expected 'docker-desktop' or 'kind-*'"
fi
kubectl cluster-info > /dev/null 2>&1 || err "Kubernetes cluster not reachable"
ok "Cluster reachable"

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
  [accumulator-service]="src/services/accumulator-service/Dockerfile"
  [ar-service]="src/services/ar-service/Dockerfile"
  [member-service]="src/services/member-service/Dockerfile"
  [coverage-service]="src/services/coverage-service/Dockerfile"
  [claims-service]="src/services/claims-service/Dockerfile"
  [claims-examiner-service]="src/services/claims-examiner-service/Dockerfile"
  [eligibility-service]="src/services/eligibility-service/Dockerfile"
  [authorization-service]="src/services/authorization-service/Dockerfile"
  [attachment-service]="src/services/attachment-service/Dockerfile"
  [consent-service]="src/services/consent-service/Dockerfile"
  [provider-service]="src/services/provider-service/Dockerfile"
  [provider-contracts-service]="src/services/provider-contracts-service/Dockerfile"
  [provider-verification-service]="src/services/provider-verification-service/Dockerfile"
  [reference-data-service]="src/services/reference-data-service/Dockerfile"
  [sponsor-service]="src/services/sponsor-service/Dockerfile"
  [enrollment-import-service]="src/services/enrollment-import-service/Dockerfile"
  [trading-partner-service]="src/services/trading-partner-service/Dockerfile"
  [tenant-service]="src/services/tenant-service/Dockerfile"
  [benefit-plan-service]="src/services/benefit-plan-service/Dockerfile"
  [payment-service]="src/services/payment-service/Dockerfile"
  [fhir-service]="src/services/fhir-service/Dockerfile"
  [smart-auth-service]="src/services/smart-auth-service/Dockerfile"
  [encounter-service]="src/services/encounter-service/Dockerfile"
  [encounter-submission-service]="src/services/encounter-submission-service/Dockerfile"
  [appeals-service]="src/services/appeals-service/Dockerfile"
  [capitation-service]="src/services/capitation-service/Dockerfile"
  [premium-billing-service]="src/services/premium-billing-service/Dockerfile"
  [risk-adjustment-service]="src/services/risk-adjustment-service/Dockerfile"
  [rfai-service]="src/services/rfai-service/Dockerfile"
  [idcard-service]="src/services/idcard-service/Dockerfile"
  [member-document-service]="src/services/member-document-service/Dockerfile"
  [personal-representative-service]="src/services/personal-representative-service/Dockerfile"
  [terminology-service]="src/services/CHO.TerminologyService/Dockerfile"
  [pricing-api]="src/services/CloudHealthOffice.PricingApi/Dockerfile"
)

# ── Build images ──────────────────────────────────────────────────────────────
if [[ "$SKIP_BUILD" == false ]]; then
  log "Building Docker images (this will take a while the first time)"
  failed_builds=()

  # Portal — uses repo root as build context
  log "Building portal"
  docker build -t ghcr.io/aurelianware/${IMAGE_PREFIX}-portal:latest \
    --build-arg REGISTRY="$LOCAL_DOTNET_REGISTRY" \
    -f src/portal/CloudHealthOffice.Portal/Dockerfile . \
    && { ok "portal"; load_kind_image "ghcr.io/aurelianware/${IMAGE_PREFIX}-portal:latest"; } \
    || { warn "portal build failed"; failed_builds+=("portal"); }

  # Site
  log "Building site"
  docker build -t ${IMAGE_PREFIX}-site:latest \
    -f src/site/Dockerfile src/site/ \
    && { ok "site"; load_kind_image "${IMAGE_PREFIX}-site:latest"; } \
    || { warn "site build failed"; failed_builds+=("site"); }

  # Microservices — all use repo root as build context (Dockerfiles COPY from src/services/...)
  log "Building microservices"
  for svc in "${!SERVICES[@]}"; do
    dockerfile="${SERVICES[$svc]}"
    if [[ ! -f "$dockerfile" ]]; then
      warn "$svc — Dockerfile not found at $dockerfile"
      failed_builds+=("$svc")
      continue
    fi

    # Tag with both ACR and GHCR names so k8s manifests find the image
    acr_tag="clouhealthoffice.azurecr.io/${IMAGE_PREFIX}-${svc}:latest"
    ghcr_tag="ghcr.io/aurelianware/${IMAGE_PREFIX}-${svc}:latest"

    docker build -t "$acr_tag" -t "$ghcr_tag" \
      --build-arg REGISTRY="$LOCAL_DOTNET_REGISTRY" \
      -f "$dockerfile" . \
      && { ok "$svc"; load_kind_image "$acr_tag"; load_kind_image "$ghcr_tag"; } \
      || { warn "$svc build failed"; failed_builds+=("$svc"); }
  done

  if (( ${#failed_builds[@]} > 0 )); then
    err "Image build failed for: ${failed_builds[*]}"
  fi
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

# ── Configure claims messaging ───────────────────────────────────────────────
# Azure Service Bus is opt-in for local development. Configure all three
# LOCAL_SERVICEBUS_* values in .env.local to provision the claims entities,
# create a least-privilege Listen/Send rule, and install servicebus-secret.
# With no configuration, claims-service explicitly uses its in-process bus.
CLAIMS_MESSAGING_BACKEND="InMemory"
if [[ -n "$LOCAL_SERVICEBUS_NAMESPACE" ||
      -n "$LOCAL_SERVICEBUS_RESOURCE_GROUP" ||
      -n "$LOCAL_SERVICEBUS_LOCATION" ]]; then
  if [[ -z "$LOCAL_SERVICEBUS_NAMESPACE" ||
        -z "$LOCAL_SERVICEBUS_RESOURCE_GROUP" ||
        -z "$LOCAL_SERVICEBUS_LOCATION" ]]; then
    err "Set LOCAL_SERVICEBUS_NAMESPACE, LOCAL_SERVICEBUS_RESOURCE_GROUP, and LOCAL_SERVICEBUS_LOCATION together"
  fi

  log "Configuring Azure Service Bus for local claims messaging"
  SERVICEBUS_NAMESPACE="$LOCAL_SERVICEBUS_NAMESPACE" \
    RESOURCE_GROUP="$LOCAL_SERVICEBUS_RESOURCE_GROUP" \
    LOCATION="$LOCAL_SERVICEBUS_LOCATION" \
    K8S_NAMESPACE="$NAMESPACE" \
    ./scripts/azure/bootstrap-local-servicebus.sh
  CLAIMS_MESSAGING_BACKEND="ServiceBus"
  ok "claims messaging: Azure Service Bus"
else
  ok "claims messaging: InMemory (set LOCAL_SERVICEBUS_* in .env.local to opt in)"
fi

# ── Create secrets ────────────────────────────────────────────────────────────
log "Creating secrets"
MONGO_CONN="mongodb://${MONGO_USER}:${MONGO_PASS}@mongodb.${NAMESPACE}.svc.cluster.local:27017/?authSource=admin"

if [[ -n "$LOCAL_COSMOS_MONGODB_ACCOUNT" || -n "$LOCAL_COSMOS_MONGODB_RESOURCE_GROUP" ]]; then
  [[ -n "$LOCAL_COSMOS_MONGODB_ACCOUNT" ]] \
    || err "LOCAL_COSMOS_MONGODB_ACCOUNT is required when LOCAL_COSMOS_MONGODB_RESOURCE_GROUP is set"
  [[ -n "$LOCAL_COSMOS_MONGODB_RESOURCE_GROUP" ]] \
    || err "LOCAL_COSMOS_MONGODB_RESOURCE_GROUP is required when LOCAL_COSMOS_MONGODB_ACCOUNT is set"
  command -v az >/dev/null 2>&1 || err "Azure CLI is required for Cosmos DB for MongoDB"

  account_kind="$(az cosmosdb show \
    --name "$LOCAL_COSMOS_MONGODB_ACCOUNT" \
    --resource-group "$LOCAL_COSMOS_MONGODB_RESOURCE_GROUP" \
    --query kind --output tsv)"
  [[ "$account_kind" == "MongoDB" ]] \
    || err "Cosmos account '$LOCAL_COSMOS_MONGODB_ACCOUNT' is '$account_kind', not 'MongoDB'"

  MONGO_CONN="$(az cosmosdb keys list \
    --name "$LOCAL_COSMOS_MONGODB_ACCOUNT" \
    --resource-group "$LOCAL_COSMOS_MONGODB_RESOURCE_GROUP" \
    --type connection-strings \
    --query 'connectionStrings[0].connectionString' \
    --output tsv)"
  [[ -n "$MONGO_CONN" ]] || err "Azure CLI returned an empty Cosmos DB for MongoDB connection string"
  ok "MongoDB persistence: Azure Cosmos DB for MongoDB"
else
  ok "MongoDB persistence: local StatefulSet"
fi

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
  --from-literal=endpoint= \
  --from-literal=key= \
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

# eligibility-service's BatchEligibility feature. Left empty on purpose --
# ASPNETCORE_ENVIRONMENT is patched to Development below, so an empty
# connection string here just means it resolves to the InMemory backend
# (see BatchEligibilityServiceCollectionExtensions) rather than crashing.
# The secret still needs to exist, though: a missing secretKeyRef target
# fails pod startup outright (CreateContainerConfigError), regardless of
# what the application code would otherwise tolerate.
kubectl create secret generic batch-eligibility-secret \
  --namespace "$NAMESPACE" \
  --from-literal=cosmosConnectionString="" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "batch-eligibility-secret"

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

# Local service stubs for optional integrations used by background workflows.
if [[ "$LOCAL_ENABLE_AI_CLAIMS_EXAMINER" == "true" && -z "${ANTHROPIC_API_KEY:-}" ]]; then
  err "ANTHROPIC_API_KEY is required when LOCAL_ENABLE_AI_CLAIMS_EXAMINER=true"
fi
kubectl create secret generic anthropic-secret \
  --namespace "$NAMESPACE" \
  --from-literal=apiKey="${ANTHROPIC_API_KEY:-local-dev}" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "anthropic-secret"

kubectl create secret generic kafka-secret \
  --namespace "$NAMESPACE" \
  --from-literal=bootstrapServers="${KAFKA_BOOTSTRAP_SERVERS:-}" \
  --dry-run=client -o yaml | kubectl apply -f -
ok "kafka-secret"

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
# Local Docker Desktop runs use Redis as regeneratable cache/hot storage.
# Keep the shared AKS manifest persistence-safe, but avoid local RDB growth
# causing OOMKilled crash loops during MCC accumulator-heavy runs.
kubectl patch deployment redis-dataprotection \
  --namespace "$NAMESPACE" \
  --type='json' \
  --patch='[
    {
      "op": "add",
      "path": "/spec/template/spec/containers/0/args",
      "value": [
        "redis-server",
        "--save",
        "",
        "--appendonly",
        "no",
        "--maxmemory",
        "768mb",
        "--maxmemory-policy",
        "volatile-lru"
      ]
    }
  ]' >/dev/null
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

if [[ "$(kubectl get pvc terminology-maps-pvc -n "$NAMESPACE" -o jsonpath='{.status.phase}:{.spec.storageClassName}' 2>/dev/null || true)" == "Pending:azurefile-csi" ]]; then
  kubectl delete pvc terminology-maps-pvc -n "$NAMESPACE" >/dev/null
  ok "removed Azure Files terminology PVC for local storage"
fi

# Patch manifests to use Never pull policy (images are local)
deploy_service() {
  local manifest="$1"
  local name="$2"
  if [[ -f "$manifest" ]]; then
    if ! grep -q '^[[:space:]]*apiVersion:' "$manifest"; then
      warn "$name — manifest has no Kubernetes objects: $manifest"
      return 0
    fi

    # Replace imagePullPolicy: Always with Never for local images
    local backend="$CLAIMS_MESSAGING_BACKEND"
    sed \
      -e 's/imagePullPolicy: Always/imagePullPolicy: IfNotPresent/g' \
      -e 's/storageClassName: azurefile-csi/storageClassName: standard/g' \
      -e 's/ReadWriteMany/ReadWriteOnce/g' \
      -e "s/Messaging__Backend: \"Auto\"/Messaging__Backend: \"$backend\"/g" \
      "$manifest" \
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

# Services whose manifests live outside src/services/<name>/k8s.
deploy_service "infrastructure/k8s/services/attachment-service.yaml" "attachment-service"
deploy_service "infrastructure/k8s/sponsor-service-deployment.yaml" "sponsor-service"
deploy_service "infrastructure/k8s/provider-verification-service-deployment.yaml" "provider-verification-service"
deploy_service "k8s/idcard-service.yaml" "idcard-service"

# Local-only environment relaxations. Claims stays Production because it has
# Development DI scope validation issues in its hosted index initializers.
kubectl patch configmap member-service-config -n "$NAMESPACE" --type merge \
  -p '{"data":{"ASPNETCORE_ENVIRONMENT":"Development"}}' >/dev/null 2>&1 || true
kubectl patch configmap benefit-plan-service-config -n "$NAMESPACE" --type merge \
  -p '{"data":{"ASPNETCORE_ENVIRONMENT":"Development"}}' >/dev/null 2>&1 || true
for cm in $(kubectl get configmaps -n "$NAMESPACE" -o name | grep -- '-config$' | grep -Ev '(claims|provider|fhir)-service-config$'); do
  kubectl patch "$cm" -n "$NAMESPACE" --type merge \
    -p '{"data":{"ASPNETCORE_ENVIRONMENT":"Development"}}' >/dev/null 2>&1 || true
done
kubectl patch configmap pricing-api-config -n "$NAMESPACE" --type merge \
  -p "{\"data\":{\"PricingApi__MongoConnectionString\":\"$MONGO_CONN\"}}" >/dev/null 2>&1 || true
kubectl patch configmap smart-auth-service-config -n "$NAMESPACE" --type merge \
  -p '{"data":{"SmartAuth__DevMode":"true"}}' >/dev/null 2>&1 || true

kubectl create secret generic attachment-service-secret \
  --namespace "$NAMESPACE" \
  --from-literal=MongoDb__ConnectionString="$MONGO_CONN" \
  --from-literal=BlobStorage__ConnectionString="$AZURE_STORAGE_CONNECTION_STRING" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl create secret generic sponsor-service-secrets \
  --namespace "$NAMESPACE" \
  --from-literal=MongoDb__ConnectionString="$MONGO_CONN" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl create secret generic smart-auth-service-secrets \
  --namespace "$NAMESPACE" \
  --from-literal=MongoDb__ConnectionString="$MONGO_CONN" \
  --from-literal=MongoDb__DatabaseName=cloudhealthoffice \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null

for dep in $(kubectl get deployments -n "$NAMESPACE" -o name); do
  kubectl patch "$dep" -n "$NAMESPACE" --type json \
    -p='[{"op":"add","path":"/spec/template/spec/containers/0/imagePullPolicy","value":"IfNotPresent"}]' \
    >/dev/null 2>&1 || true
done
kubectl set env deployment --all -n "$NAMESPACE" SecretProvider__Provider=None >/dev/null 2>&1 || true
kubectl set env deployment/pricing-api -n "$NAMESPACE" \
  PricingApi__MongoConnectionString="$MONGO_CONN" >/dev/null 2>&1 || true
for dep in appeals-service consent-service personal-representative-service; do
  kubectl patch "deployment/$dep" -n "$NAMESPACE" --type json \
    -p='[{"op":"replace","path":"/spec/template/spec/containers/0/readinessProbe/httpGet/path","value":"/health/live"}]' \
    >/dev/null 2>&1 || true
done
for dep in encounter-service sponsor-service; do
  kubectl patch "deployment/$dep" -n "$NAMESPACE" --type json \
    -p='[{"op":"replace","path":"/spec/template/spec/containers/0/livenessProbe/httpGet/path","value":"/health/live"},{"op":"replace","path":"/spec/template/spec/containers/0/readinessProbe/httpGet/path","value":"/health/live"}]' \
    >/dev/null 2>&1 || true
done
for dep in attachment-service fhir-service reference-data-service; do
  kubectl set env "deployment/$dep" -n "$NAMESPACE" \
    AzureAd__ClientId="$AZURE_AD_CLIENT_ID" \
    AzureAd__TenantId="$AZURE_AD_TENANT_ID" \
    AzureAd__Instance="$AZURE_AD_INSTANCE" \
    AzureAd__Audience="$AZURE_AD_AUDIENCE" \
    >/dev/null 2>&1 || true
done
kubectl scale deployment --all -n "$NAMESPACE" --replicas=1 >/dev/null 2>&1 || true
kubectl scale deployment/claims-service -n "$NAMESPACE" --replicas="$LOCAL_CLAIMS_REPLICAS" >/dev/null 2>&1 || true
kubectl scale deployment/benefit-plan-service -n "$NAMESPACE" --replicas="$LOCAL_BENEFIT_PLAN_REPLICAS" >/dev/null 2>&1 || true
if [[ "$LOCAL_ENABLE_AI_CLAIMS_EXAMINER" == "true" ]]; then
  kubectl set env deployment/claims-service -n "$NAMESPACE" \
    Adjudication__Enforcement__AiMode=BestEffort >/dev/null 2>&1 || true
  kubectl scale deployment/claims-examiner-service -n "$NAMESPACE" --replicas=1 >/dev/null 2>&1 || true
  ok "AI Claims Examiner enabled for local development"
else
  kubectl set env deployment/claims-service -n "$NAMESPACE" \
    Adjudication__Enforcement__AiMode=Disabled >/dev/null 2>&1 || true
  kubectl scale deployment/claims-examiner-service -n "$NAMESPACE" --replicas=0 >/dev/null 2>&1 || true
  ok "AI Claims Examiner disabled (set LOCAL_ENABLE_AI_CLAIMS_EXAMINER=true to opt in)"
fi
kubectl delete hpa --all -n "$NAMESPACE" >/dev/null 2>&1 || true

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
