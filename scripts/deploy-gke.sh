#!/usr/bin/env bash
# deploy-gke.sh — Deploy the Cloud Health Office demo to a GKE Autopilot cluster.
#
# Demo-grade. Synthetic data only. No public ingress: reach it with
# scripts/demo/gcp/port-forward.sh. See docs/deploy/GCP-DEMO.md.
#
# Images must already be in Artifact Registry (the deploy-gcp-gke workflow
# builds and pushes them). This script installs Kafka (Strimzi), MongoDB and
# Redis in the cluster, creates the demo secrets, renders the service
# manifests for GKE (deploy/gcp/render_manifests.py) and applies them.
#
# Required environment:
#   GAR_REPOSITORY   Artifact Registry prefix, e.g. us-west1-docker.pkg.dev/my-project/cho
#   IMAGE_TAG        Image tag to deploy (the workflow uses the commit SHA)
#
# Optional environment:
#   ANTHROPIC_API_KEY        Enables the advisory AI claims examiner
#   ENABLE_KAFKA=true        Set to false to skip Kafka (services fall back to in-process messaging)
#   CLAIMS_REPLICAS=2  BENEFIT_PLAN_REPLICAS=2
#   ROLLOUT_TIMEOUT=900s

set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

NAMESPACE="cloudhealthoffice"
KAFKA_NAMESPACE="kafka"
STRIMZI_VERSION="0.45.2"
KAFKA_CLUSTER="cloudhealthoffice-kafka"
KAFKA_BOOTSTRAP="${KAFKA_CLUSTER}-kafka-bootstrap.${KAFKA_NAMESPACE}.svc:9092"
ENABLE_KAFKA="${ENABLE_KAFKA:-true}"
CLAIMS_REPLICAS="${CLAIMS_REPLICAS:-2}"
BENEFIT_PLAN_REPLICAS="${BENEFIT_PLAN_REPLICAS:-2}"
ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-900s}"
MONGO_USER="admin"

log()  { echo -e "\n\033[1;36m▶ $*\033[0m"; }
ok()   { echo "  ✓ $*"; }
warn() { echo "  ⚠ $*"; }
err()  { echo "  ✗ $*" >&2; exit 1; }

# ── Preflight ────────────────────────────────────────────────────────────────
log "Preflight checks"
[[ -n "${GAR_REPOSITORY:-}" ]] || err "GAR_REPOSITORY is required (e.g. us-west1-docker.pkg.dev/PROJECT/cho)"
[[ -n "${IMAGE_TAG:-}" ]] || err "IMAGE_TAG is required"
for tool in kubectl python3 openssl; do
  command -v "$tool" >/dev/null || err "$tool is required"
done
if [[ "$ENABLE_KAFKA" == "true" ]]; then
  command -v helm >/dev/null || err "helm is required to install the Strimzi Kafka operator (or set ENABLE_KAFKA=false)"
fi
python3 -c "import yaml" 2>/dev/null || err "PyYAML is required (pip install pyyaml)"
context="$(kubectl config current-context)"
[[ "$context" == gke_* ]] || warn "kubectl context is '$context', not a GKE context — continuing anyway"
kubectl cluster-info >/dev/null || err "cluster not reachable"
ok "context $context, images ${GAR_REPOSITORY}:${IMAGE_TAG}"

kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
ok "namespace $NAMESPACE"

# ── Secrets ──────────────────────────────────────────────────────────────────
# Passwords for the in-cluster databases are generated once and reused on
# every redeploy (the databases keep the password they were initialized with).
existing_secret_value() {
  kubectl get secret "$1" -n "$NAMESPACE" -o "jsonpath={.data.$2}" 2>/dev/null | base64 -d 2>/dev/null || true
}
random_secret() { openssl rand -hex 24; }

upsert_secret() {
  local name="$1"; shift
  local args=()
  for kv in "$@"; do args+=(--from-literal="$kv"); done
  kubectl create secret generic "$name" --namespace "$NAMESPACE" "${args[@]}" \
    --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  ok "$name"
}

log "Creating demo secrets"
MONGO_PASS="$(existing_secret_value mongodb-auth password)"
[[ -n "$MONGO_PASS" ]] || MONGO_PASS="$(random_secret)"
POSTGRES_PASSWORD="$(existing_secret_value reference-data-service-secret POSTGRES_PASSWORD)"
[[ -n "$POSTGRES_PASSWORD" ]] || POSTGRES_PASSWORD="$(random_secret)"
PRICING_ADMIN_SECRET="$(existing_secret_value pricing-api-secret AdminSecret)"
[[ -n "$PRICING_ADMIN_SECRET" ]] || PRICING_ADMIN_SECRET="$(random_secret)"
MONGO_CONN="mongodb://${MONGO_USER}:${MONGO_PASS}@mongodb.${NAMESPACE}.svc.cluster.local:27017/?authSource=admin"
REDIS_CONN="redis-dataprotection.${NAMESPACE}.svc.cluster.local:6379"
STORAGE_CONN="UseDevelopmentStorage=true"
KAFKA_VALUE=""
[[ "$ENABLE_KAFKA" == "true" ]] && KAFKA_VALUE="$KAFKA_BOOTSTRAP"
AI_EXAMINER=false
[[ -n "${ANTHROPIC_API_KEY:-}" ]] && AI_EXAMINER=true

upsert_secret mongodb-auth username="$MONGO_USER" password="$MONGO_PASS"
upsert_secret database-secret connectionString="$MONGO_CONN" endpoint= key=
upsert_secret cosmosdb-secret connectionString="$MONGO_CONN"
upsert_secret redis-secret connectionString="$REDIS_CONN"
upsert_secret reference-data-service-secret POSTGRES_PASSWORD="$POSTGRES_PASSWORD"
upsert_secret azure-storage-secret connectionString="$STORAGE_CONN"
upsert_secret batch-eligibility-secret cosmosConnectionString=
upsert_secret kafka-secret bootstrapServers="$KAFKA_VALUE" saslUsername= saslPassword=
upsert_secret anthropic-secret apiKey="${ANTHROPIC_API_KEY:-not-configured}"
# Entra ID is not used in the demo (development tokens); placeholders keep pods starting.
upsert_secret azure-ad-config clientId=demo ClientId=demo clientSecret=demo ClientSecret=demo \
  tenantId=common TenantId=common Instance=https://login.microsoftonline.com/ \
  Audience=api://cho-demo audience=api://cho-demo
upsert_secret stripe-api-keys publishable-key=pk_test_demo secret-key=sk_test_demo \
  starter-price-id=price_demo_starter professional-price-id=price_demo_pro webhook-secret=whsec_demo
upsert_secret pricing-api-secret apiKey=demo-pricing-key AdminSecret="$PRICING_ADMIN_SECRET"
upsert_secret email-config SmtpHost=localhost SmtpPort=1025 EnableSsl=false \
  FromAddress=demo@cloudhealthoffice.local SalesTeamAddress=sales@cloudhealthoffice.local \
  Username=demo Password=demo
upsert_secret attachment-service-secret MongoDb__ConnectionString="$MONGO_CONN" \
  BlobStorage__ConnectionString="$STORAGE_CONN"
upsert_secret member-document-service-secret MongoDb__ConnectionString="$MONGO_CONN" \
  BlobStorage__ConnectionString="$STORAGE_CONN"
upsert_secret sponsor-service-secrets MongoDb__ConnectionString="$MONGO_CONN"
upsert_secret smart-auth-service-secrets MongoDb__ConnectionString="$MONGO_CONN" \
  MongoDb__DatabaseName=CloudHealthOffice
upsert_secret provider-verification-service-secrets MongoDb__ConnectionString="$MONGO_CONN" \
  ProviderVerification__SamGovApiKey=not-configured
upsert_secret risk-adjustment-service-secret MongoDb__ConnectionString="$MONGO_CONN"

kubectl apply -f deploy/gcp/cho-service-tokens.demo.yaml >/dev/null
ok "cho-service-tokens (demo development issuers)"

# ── Kafka (Strimzi) ──────────────────────────────────────────────────────────
if [[ "$ENABLE_KAFKA" == "true" ]]; then
  log "Installing Kafka (Strimzi ${STRIMZI_VERSION}, 3 KRaft brokers)"
  helm upgrade --install strimzi oci://quay.io/strimzi-helm/strimzi-kafka-operator \
    --version "$STRIMZI_VERSION" --namespace "$KAFKA_NAMESPACE" --create-namespace --wait >/dev/null
  ok "strimzi operator"
  kubectl apply -f deploy/gcp/kafka/kafka-cluster.yaml >/dev/null
  kubectl wait kafka/"$KAFKA_CLUSTER" -n "$KAFKA_NAMESPACE" --for=condition=Ready --timeout=900s >/dev/null \
    && ok "kafka cluster ready ($KAFKA_BOOTSTRAP)" \
    || err "Kafka cluster not ready; check: kubectl get kafka,kafkanodepool,pods -n $KAFKA_NAMESPACE"
  kubectl apply -f infrastructure/kafka/topics.yaml >/dev/null
  ok "topics from infrastructure/kafka/topics.yaml"
else
  warn "Kafka skipped (ENABLE_KAFKA=false); services use in-process messaging"
fi

# ── MongoDB and Redis ────────────────────────────────────────────────────────
log "Deploying MongoDB and Redis"
# The manifest sets a 3.5 GB WiredTiger cache but requests 2Gi. Autopilot can
# hold a pod to its request, so request enough memory for the cache.
python3 - <<'PY' | kubectl apply -f - >/dev/null
import sys, yaml
docs = [d for d in yaml.safe_load_all(open("infrastructure/k8s/mongodb-deployment.yaml")) if d]
for d in docs:
    if d.get("kind") == "StatefulSet":
        for c in d["spec"]["template"]["spec"]["containers"]:
            c.setdefault("resources", {}).setdefault("requests", {})["memory"] = "5Gi"
yaml.safe_dump_all(docs, sys.stdout, sort_keys=False)
PY
kubectl apply -f infrastructure/k8s/redis-dataprotection.yaml >/dev/null
kubectl rollout status statefulset/mongodb -n "$NAMESPACE" --timeout=600s >/dev/null \
  && ok "mongodb" || err "MongoDB did not become ready"

mongo_ready=false
for _ in $(seq 1 60); do
  if kubectl exec -n "$NAMESPACE" mongodb-0 -- mongosh --username "$MONGO_USER" --password "$MONGO_PASS" \
      --authenticationDatabase admin --quiet --eval 'db.adminCommand({ ping: 1 }).ok' >/dev/null 2>&1; then
    mongo_ready=true; break
  fi
  sleep 3
done
[[ "$mongo_ready" == true ]] || err "MongoDB never accepted an authenticated connection"
ok "mongodb accepting connections"

log "Seeding the synthetic demo tenant"
kubectl exec -n "$NAMESPACE" mongodb-0 -- mongosh --username "$MONGO_USER" --password "$MONGO_PASS" \
  --authenticationDatabase admin --quiet --eval '
    db = db.getSiblingDB("CloudHealthOffice");
    db.tenants.updateOne({ azureTenantId: "demo" }, { $set: {
      azureTenantId: "demo", organizationName: "Demo Health Plan", tier: "enterprise",
      isDemo: true, subscriptionStatus: "Active", createdAt: new Date() } }, { upsert: true });
    db.members.updateOne({ memberId: "MBR001" }, { $set: {
      memberId: "MBR001", subscriberId: "SUB001", firstName: "Jane", lastName: "Doe",
      dateOfBirth: ISODate("1985-03-15"), gender: "female", tenantId: "demo",
      status: "Active", createdAt: new Date() } }, { upsert: true });
    db.prior_auth_rules.deleteMany({ _id: "" });
  ' >/dev/null && ok "demo tenant and member" || warn "demo seed failed; rerun this script to retry"

# ── Services ─────────────────────────────────────────────────────────────────
log "Rendering and applying service manifests"
render_args=(--registry "$GAR_REPOSITORY" --tag "$IMAGE_TAG"
  --claims-replicas "$CLAIMS_REPLICAS" --benefit-plan-replicas "$BENEFIT_PLAN_REPLICAS")
[[ -n "$KAFKA_VALUE" ]] && render_args+=(--kafka-bootstrap "$KAFKA_VALUE")
[[ "$AI_EXAMINER" == true ]] && render_args+=(--ai-examiner)
rendered="$(mktemp)"
python3 deploy/gcp/render_manifests.py "${render_args[@]}" > "$rendered"
kubectl apply -n "$NAMESPACE" -f "$rendered" >/dev/null
ok "applied $(grep -c '^kind: Deployment' "$rendered") deployments"
[[ "$AI_EXAMINER" == true ]] && ok "AI claims examiner enabled (advisory only)" \
  || warn "AI claims examiner off (set ANTHROPIC_API_KEY to enable)"

# ── Wait ─────────────────────────────────────────────────────────────────────
log "Waiting for rollouts in parallel (timeout $ROLLOUT_TIMEOUT)"
# Autopilot adds nodes as pods are scheduled, so the first rollout is slow.
# Waiting in parallel keeps the total close to the slowest service.
declare -A wait_pids=()
for dep in $(kubectl get deployments -n "$NAMESPACE" -o jsonpath='{range .items[?(@.spec.replicas>0)]}{.metadata.name}{"\n"}{end}'); do
  kubectl rollout status "deployment/$dep" -n "$NAMESPACE" --timeout="$ROLLOUT_TIMEOUT" >/dev/null 2>&1 &
  wait_pids[$dep]=$!
done
failed=()
for dep in "${!wait_pids[@]}"; do
  if wait "${wait_pids[$dep]}"; then
    ok "$dep"
  else
    warn "$dep not ready"
    failed+=("$dep")
  fi
done

kubectl get pods -n "$NAMESPACE" -o wide
if (( ${#failed[@]} > 0 )); then
  echo
  warn "Not ready: ${failed[*]}"
  echo "  Inspect with: kubectl describe deployment/<name> -n $NAMESPACE; kubectl logs deployment/<name> -n $NAMESPACE"
  exit 1
fi

echo
echo "Cloud Health Office demo deployed to $context."
echo "Reach it with: scripts/demo/gcp/port-forward.sh"
