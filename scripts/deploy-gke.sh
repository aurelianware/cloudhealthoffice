#!/usr/bin/env bash
# deploy-gke.sh — Deploy the Cloud Health Office demo to a GKE cluster
# (the single-node Standard cluster infrastructure/gcp creates by default, or Autopilot).
#
# Demo-grade. Synthetic data only. No public ingress: reach it with
# scripts/demo/gcp/port-forward.sh, or through a Cloudflare Tunnel protected by
# Cloudflare Access. See docs/deploy/GCP-DEMO.md.
#
# Two security modes (docs/deploy/GCP-DEMO.md, "Production security"):
#   demo        (default) development hosts with the public development key and
#               the portal's built-in demo sign-in
#   production  when every setting below is present: services are Staging hosts
#               that refuse development keys, token-service signs with Cloud KMS,
#               each service proves its Kubernetes identity, and the portal signs
#               people in through Cloudflare Access (no second sign-in)
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
#   ANTHROPIC_API_KEY        Enables the advisory AI claims examiner. After rollout the deploy
#                            makes one real call to Claude with the examiner's own settings and
#                            fails if it doesn't succeed (bad key, no credit, retired model).
#   REQUIRE_AI_EXAMINER=false  true fails the deploy when ANTHROPIC_API_KEY is empty instead of
#                            turning the examiner off (the workflow sets it unless you untick it)
#   CLOUDFLARE_TUNNEL_TOKEN  Runs a Cloudflare Tunnel connector (deploy/gcp/cloudflare) so the
#                            demo is reachable at a Cloudflare Access-protected hostname
#   ENABLE_KAFKA=true        Set to false to skip Kafka (services fall back to in-process messaging)
#   RESOURCE_PROFILE=small   small fits one 8-vCPU node (lower CPU requests, one
#                            replica each); default keeps the manifests' requests
#   CLAIMS_REPLICAS  BENEFIT_PLAN_REPLICAS   (1 with the small profile, else 2)
#   ROLLOUT_TIMEOUT=900s
#   Production security (all five, or none):
#   TOKEN_SIGNING_KEY_VERSION     terraform output token_signing_key_version
#   TOKEN_SERVICE_GOOGLE_ACCOUNT  terraform output token_service_google_account
#   KUBERNETES_SA_ISSUER          terraform output kubernetes_service_account_issuer
#   CF_ACCESS_TEAM_DOMAIN         Cloudflare Zero Trust team domain (yourteam.cloudflareaccess.com)
#   CF_ACCESS_AUD                 the Access application's AUD tag (the demo hostname's application)
#   DEMO_ADMIN_EMAILS             comma-separated addresses made TenantAdmin of the demo tenant,
#                                 so they can sign in through Access and add other users
#   DOCUMENTS_BUCKET  DOCUMENTS_SERVICE_ACCOUNT
#                            Google Cloud Storage for document-service (attachment- and
#                            member-document-service file through it)
#                            (terraform outputs documents_bucket and documents_service_account in
#                            infrastructure/gcp). Without them, uploaded documents are not stored.
#   MANAGED_FHIR_STORE  MANAGED_FHIR_STORE_SERVICE_ACCOUNT  [MANAGED_FHIR_STORE_TENANT]
#                            Publish finalized claims' ExplanationOfBenefit to a Cloud Healthcare API
#                            FHIR store, which streams to BigQuery (terraform outputs
#                            managed_fhir_store_id and managed_fhir_store_service_account with
#                            enable_managed_fhir_store = true; ADR 023). Optional; off without them.

set -Eeuo pipefail
cd "$(git rev-parse --show-toplevel)"

NAMESPACE="cloudhealthoffice"
KAFKA_NAMESPACE="kafka"
STRIMZI_VERSION="0.45.2"
KAFKA_CLUSTER="cloudhealthoffice-kafka"
KAFKA_BOOTSTRAP="${KAFKA_CLUSTER}-kafka-bootstrap.${KAFKA_NAMESPACE}.svc:9092"
ENABLE_KAFKA="${ENABLE_KAFKA:-true}"
RESOURCE_PROFILE="${RESOURCE_PROFILE:-small}"
case "$RESOURCE_PROFILE" in
  small) default_replicas=1 ;;
  default) default_replicas=2 ;;
  *) echo "RESOURCE_PROFILE must be small or default" >&2; exit 1 ;;
esac
CLAIMS_REPLICAS="${CLAIMS_REPLICAS:-$default_replicas}"
BENEFIT_PLAN_REPLICAS="${BENEFIT_PLAN_REPLICAS:-$default_replicas}"
# Standard persistent disk: new billing accounts have only 250 GB of SSD quota.
STORAGE_CLASS="cho-pd-standard"
ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-900s}"
MONGO_USER="admin"

log()  { echo -e "\n\033[1;36m▶ $*\033[0m"; }
ok()   { echo "  ✓ $*"; }
# In GitHub Actions, warnings and errors also become run annotations, so the
# reason a deploy failed shows on the run summary without opening the log.
gha() { [[ -n "${GITHUB_ACTIONS:-}" ]] && echo "::$1::${2//$'\n'/%0A}"; return 0; }
warn() { echo "  ⚠ $*"; gha warning "$*"; }
err()  { echo "  ✗ $*" >&2; gha error "$*"; exit 1; }
# Any other command that fails under set -e: name the line and command.
# shellcheck disable=SC2154  # rc is assigned inside the trap string
trap 'rc=$?; gha error "deploy-gke.sh failed at line $LINENO (exit $rc): $BASH_COMMAND"' ERR

# ── Preflight ────────────────────────────────────────────────────────────────
log "Preflight checks"
[[ -n "${GAR_REPOSITORY:-}" ]] || err "GAR_REPOSITORY is required (e.g. us-west1-docker.pkg.dev/PROJECT/cho)"
[[ -n "${IMAGE_TAG:-}" ]] || err "IMAGE_TAG is required"
if [[ "${REQUIRE_AI_EXAMINER:-false}" == "true" && -z "${ANTHROPIC_API_KEY:-}" ]]; then
  err "The AI claims examiner is required but ANTHROPIC_API_KEY is empty (set the repository secret, or untick ai_examiner to deploy without it)"
fi
SECURITY_SETTINGS=(TOKEN_SIGNING_KEY_VERSION TOKEN_SERVICE_GOOGLE_ACCOUNT KUBERNETES_SA_ISSUER CF_ACCESS_TEAM_DOMAIN CF_ACCESS_AUD)
security_set=(); security_missing=()
for setting in "${SECURITY_SETTINGS[@]}"; do
  if [[ -n "${!setting:-}" ]]; then security_set+=("$setting"); else security_missing+=("$setting"); fi
done
if (( ${#security_set[@]} == ${#SECURITY_SETTINGS[@]} )); then
  SECURITY=production
elif (( ${#security_set[@]} == 0 )); then
  SECURITY=demo
else
  err "Production security needs all of ${SECURITY_SETTINGS[*]}; missing: ${security_missing[*]}"
fi
if [[ "$SECURITY" == production ]]; then
  command -v gcloud >/dev/null || err "gcloud is required to read the token signing public key"
fi

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
if [[ "$SECURITY" == production ]]; then
  ok "security: production (Cloud KMS signing, Kubernetes service identity, Cloudflare Access sign-in)"
else
  warn "security: demo (public development key, built-in demo sign-in). Set ${SECURITY_SETTINGS[*]} for production security."
fi

kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
ok "namespace $NAMESPACE"

kubectl apply -f - >/dev/null <<YAML
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: $STORAGE_CLASS
  labels:
    app: cloudhealthoffice
provisioner: pd.csi.storage.gke.io
parameters:
  type: pd-standard
volumeBindingMode: WaitForFirstConsumer
allowVolumeExpansion: true
reclaimPolicy: Delete
YAML
ok "storage class $STORAGE_CLASS (pd-standard)"

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
upsert_secret database-secret connectionString="$MONGO_CONN"
upsert_secret redis-secret connectionString="$REDIS_CONN"
upsert_secret reference-data-service-secret POSTGRES_PASSWORD="$POSTGRES_PASSWORD"
upsert_secret azure-storage-secret connectionString="$STORAGE_CONN"
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
upsert_secret attachment-service-secret MongoDb__ConnectionString="$MONGO_CONN"
upsert_secret member-document-service-secret MongoDb__ConnectionString="$MONGO_CONN"
upsert_secret sponsor-service-secrets MongoDb__ConnectionString="$MONGO_CONN"
upsert_secret smart-auth-service-secrets MongoDb__ConnectionString="$MONGO_CONN" \
  MongoDb__DatabaseName=CloudHealthOffice
upsert_secret provider-verification-service-secrets MongoDb__ConnectionString="$MONGO_CONN" \
  ProviderVerification__SamGovApiKey=not-configured

if [[ "$SECURITY" == production ]]; then
  # Every service trusts exactly this key; render_manifests.py writes it into
  # the cho-service-tokens ConfigMap, replacing the demo development issuers.
  TOKEN_PUBLIC_KEY_FILE="$(mktemp)"
  kms_pattern='^projects/([^/]+)/locations/([^/]+)/keyRings/([^/]+)/cryptoKeys/([^/]+)/cryptoKeyVersions/([0-9]+)$'
  [[ "$TOKEN_SIGNING_KEY_VERSION" =~ $kms_pattern ]] \
    || err "TOKEN_SIGNING_KEY_VERSION must be projects/<p>/locations/<l>/keyRings/<r>/cryptoKeys/<k>/cryptoKeyVersions/<n> (check for stray spaces or newlines)"
  kms_project="${BASH_REMATCH[1]}" kms_location="${BASH_REMATCH[2]}" kms_ring="${BASH_REMATCH[3]}"
  kms_key="${BASH_REMATCH[4]}" kms_version="${BASH_REMATCH[5]}"
  gcloud kms keys versions get-public-key "$kms_version" --key "$kms_key" --keyring "$kms_ring" \
    --location "$kms_location" --project "$kms_project" --output-file "$TOKEN_PUBLIC_KEY_FILE" \
    || err "Could not read the public key of $TOKEN_SIGNING_KEY_VERSION (the deployer needs roles/cloudkms.publicKeyViewer; terraform apply with enable_token_signing_kms = true grants it)"
  ok "token signing public key ($kms_key version $kms_version)"
else
  kubectl apply -f deploy/gcp/cho-service-tokens.demo.yaml >/dev/null
  ok "cho-service-tokens (demo development issuers)"
fi

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
# The manifest sets a 3.5 GB WiredTiger cache but requests 2Gi. Request enough
# memory for the cache, put the data on standard disk, and with the small
# profile lower the CPU request so everything fits on one node.
RESOURCE_PROFILE="$RESOURCE_PROFILE" STORAGE_CLASS="$STORAGE_CLASS" python3 - <<'PY' | kubectl apply -f - >/dev/null
import os, sys, yaml
docs = [d for d in yaml.safe_load_all(open("infrastructure/k8s/mongodb-deployment.yaml")) if d]
for d in docs:
    if d.get("kind") == "StatefulSet":
        for c in d["spec"]["template"]["spec"]["containers"]:
            requests = c.setdefault("resources", {}).setdefault("requests", {})
            requests["memory"] = "5Gi"
            if os.environ["RESOURCE_PROFILE"] == "small":
                requests["cpu"] = "250m"
        for t in d["spec"].get("volumeClaimTemplates", []):
            t["spec"]["storageClassName"] = os.environ["STORAGE_CLASS"]
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

if [[ "$SECURITY" == production ]]; then
  # tenant-service's identity read model: the demo tenant, and an Active
  # TenantAdmin for each DEMO_ADMIN_EMAILS address. Cloudflare Access decides
  # who reaches the portal; these records decide who gets a token in the tenant.
  # Existing users keep their roles and status (insert only).
  [[ -n "${DEMO_ADMIN_EMAILS:-}" ]] \
    || warn "DEMO_ADMIN_EMAILS is empty: no one has a user in the demo tenant, so no one can sign in until a user is added"
  kubectl exec -n "$NAMESPACE" mongodb-0 -- \
    env DEMO_ADMIN_EMAILS="${DEMO_ADMIN_EMAILS:-}" mongosh --username "$MONGO_USER" --password "$MONGO_PASS" \
    --authenticationDatabase admin --quiet --eval '
      db = db.getSiblingDB("CloudHealthOffice");
      db.Tenants.updateOne({ tenantId: "demo", azureTenantId: "demo" }, { $set: {
        tenantId: "demo", azureTenantId: "demo", organizationName: "Demo Health Plan", tier: "enterprise",
        isDemo: true, subscriptionStatus: "Active", updatedAt: new Date() },
        $setOnInsert: { createdAt: new Date() } }, { upsert: true });
      const roles = ["TenantAdmin", "ClaimsSupervisor", "MemberServices", "ProviderRelations", "Finance"];
      for (const raw of (process.env.DEMO_ADMIN_EMAILS || "").split(",")) {
        const email = raw.trim();
        if (!email) continue;
        const normalized = email.toLowerCase();
        db.TenantUsers.updateOne({ tenantId: "demo", emailNormalized: normalized }, { $setOnInsert: {
          _id: "demo-admin-" + require("crypto").createHash("sha256").update(normalized).digest("hex").slice(0, 32),
          tenantId: "demo", email: email,
          emailNormalized: normalized, azureAdObjectId: "", azureAdTenantId: "", displayName: email,
          firstName: "", lastName: "", roles: roles, department: "Administration", status: "Active",
          createdAt: new Date(), updatedAt: new Date(), createdBy: "deploy-gke.sh" } }, { upsert: true });
      }
    ' >/dev/null && ok "demo tenant users (${DEMO_ADMIN_EMAILS:-none})" || warn "demo tenant user seed failed; rerun this script to retry"
fi

# ── Services ─────────────────────────────────────────────────────────────────
log "Rendering and applying service manifests"
render_args=(--registry "$GAR_REPOSITORY" --tag "$IMAGE_TAG"
  --claims-replicas "$CLAIMS_REPLICAS" --benefit-plan-replicas "$BENEFIT_PLAN_REPLICAS"
  --storage-class "$STORAGE_CLASS" --resource-profile "$RESOURCE_PROFILE")
[[ -n "$KAFKA_VALUE" ]] && render_args+=(--kafka-bootstrap "$KAFKA_VALUE")
[[ "$AI_EXAMINER" == true ]] && render_args+=(--ai-examiner)
if [[ "$SECURITY" == production ]]; then
  render_args+=(--security production
    --token-signing-key-version "$TOKEN_SIGNING_KEY_VERSION"
    --token-service-google-account "$TOKEN_SERVICE_GOOGLE_ACCOUNT"
    --kubernetes-issuer "$KUBERNETES_SA_ISSUER"
    --token-public-key-file "$TOKEN_PUBLIC_KEY_FILE"
    --access-team-domain "$CF_ACCESS_TEAM_DOMAIN"
    --access-audience "$CF_ACCESS_AUD")
  [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]] \
    || warn "CLOUDFLARE_TUNNEL_TOKEN is not set: with Cloudflare Access sign-in the portal is reachable only through the tunnel"
fi
if [[ -n "${DOCUMENTS_BUCKET:-}" && -n "${DOCUMENTS_SERVICE_ACCOUNT:-}" ]]; then
  render_args+=(--documents-bucket "$DOCUMENTS_BUCKET" --documents-service-account "$DOCUMENTS_SERVICE_ACCOUNT")
else
  warn "DOCUMENTS_BUCKET / DOCUMENTS_SERVICE_ACCOUNT not set: attachment and member document uploads will fail (no storage). Set them from the infrastructure/gcp outputs."
fi
if [[ -n "${MANAGED_FHIR_STORE:-}" && -n "${MANAGED_FHIR_STORE_SERVICE_ACCOUNT:-}" ]]; then
  render_args+=(--managed-fhir-store "$MANAGED_FHIR_STORE"
    --managed-fhir-store-service-account "$MANAGED_FHIR_STORE_SERVICE_ACCOUNT"
    --managed-fhir-store-tenant "${MANAGED_FHIR_STORE_TENANT:-demo}")
  [[ "$ENABLE_KAFKA" == true ]] \
    || warn "MANAGED_FHIR_STORE is set but Kafka is off: nothing will be published (fhir-service reads claims.finalized.v1)"
elif [[ -n "${MANAGED_FHIR_STORE:-}${MANAGED_FHIR_STORE_SERVICE_ACCOUNT:-}" ]]; then
  warn "MANAGED_FHIR_STORE and MANAGED_FHIR_STORE_SERVICE_ACCOUNT go together; managed FHIR store publishing is off"
fi
rendered="$(mktemp)"
python3 deploy/gcp/render_manifests.py "${render_args[@]}" > "$rendered"
apply_rendered() { apply_out="$(kubectl apply -n "$NAMESPACE" -f "$rendered" 2>&1)"; }
apply_failed() {
  # Name what the API server rejected, not just that apply failed.
  apply_errors="$(grep -viE ' (created|configured|unchanged)$' <<<"$apply_out" | head -10)"
  err "kubectl apply of the service manifests failed:"$'\n'"${apply_errors:-$apply_out}"
}
if ! apply_rendered; then
  # An object changed outside this script (kubectl set env / edit, e.g. while
  # tuning a benchmark) can make the three-way merge produce an invalid object,
  # such as an env var with both value and valueFrom. Replace those objects
  # with the rendered manifest outright, then apply again.
  mapfile -t invalid < <(grep -oE '^The [A-Za-z]+ "[^"]+" is invalid' <<<"$apply_out" | sed -E 's/^The ([A-Za-z]+) "([^"]+)".*/\1 \2/' | sort -u)
  (( ${#invalid[@]} > 0 )) || apply_failed
  for obj in "${invalid[@]}"; do
    read -r obj_kind obj_name <<<"$obj"
    warn "$obj_kind/$obj_name differs from its manifest (changed outside this deploy); replacing it"
    if ! replace_out="$(OBJ_KIND="$obj_kind" OBJ_NAME="$obj_name" python3 -c '
import os, sys, yaml
for doc in yaml.safe_load_all(open(sys.argv[1])):
    if doc and doc.get("kind") == os.environ["OBJ_KIND"] and doc["metadata"]["name"] == os.environ["OBJ_NAME"]:
        print(yaml.safe_dump(doc, sort_keys=False))
' "$rendered" | kubectl replace --save-config -n "$NAMESPACE" -f - 2>&1)"; then
      err "Replacing $obj_kind/$obj_name failed: $replace_out"
    fi
  done
  apply_rendered || apply_failed
fi
ok "applied $(grep -c '^kind: Deployment' "$rendered") deployments"
[[ "$AI_EXAMINER" == true ]] && ok "AI claims examiner enabled (advisory only)" \
  || warn "AI claims examiner off (set ANTHROPIC_API_KEY to enable)"

# ── Cloudflare Tunnel (optional) ─────────────────────────────────────────────
if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then
  log "Cloudflare Tunnel"
  upsert_secret cloudflare-tunnel-token token="$CLOUDFLARE_TUNNEL_TOKEN"
  kubectl apply -f deploy/gcp/cloudflare/cloudflared.yaml >/dev/null
  ok "cloudflared applied; protect every tunnel hostname with Cloudflare Access (docs/deploy/GCP-PUBLIC-HOSTING-PLAN.md)"
else
  kubectl delete deployment cloudflared -n "$NAMESPACE" --ignore-not-found >/dev/null
  warn "Cloudflare Tunnel off (set CLOUDFLARE_TUNNEL_TOKEN to enable); reach the demo with port-forward"
fi

# ── Wait ─────────────────────────────────────────────────────────────────────
log "Waiting for rollouts in parallel (timeout $ROLLOUT_TIMEOUT)"
# Image pulls (and on Autopilot, new nodes) make the first rollout slow.
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
  echo "  Inspect with: kubectl describe deployment/<name> -n $NAMESPACE; kubectl logs deployment/<name> -n $NAMESPACE"
  err "Not ready after $ROLLOUT_TIMEOUT: ${failed[*]}"
fi

# ── AI examiner live check ───────────────────────────────────────────────────
# "Enabled" only means a key was present. Prove it works: run the examiner image
# once as a Job with the deployment's own env (key, model, network path) and make
# one tiny call to Claude. A separate pod, so the check can't push the running
# examiner past its memory limit.
if [[ "$AI_EXAMINER" == true ]]; then
  log "Checking the AI claims examiner can reach Claude"
  check_job="claims-examiner-anthropic-check"
  kubectl delete job "$check_job" -n "$NAMESPACE" --ignore-not-found >/dev/null
  kubectl get deployment claims-examiner-service -n "$NAMESPACE" -o json | CHECK_JOB="$check_job" python3 -c '
import json, os, sys
dep = json.load(sys.stdin)
pod = dep["spec"]["template"]
spec = pod["spec"]
spec["restartPolicy"] = "Never"
for c in spec["containers"]:
    c["args"] = ["--check-anthropic"]
    for probe in ("livenessProbe", "readinessProbe", "startupProbe"):
        c.pop(probe, None)
    c.pop("ports", None)
labels = {k: v for k, v in (pod.get("metadata", {}).get("labels") or {}).items() if k != "app"}
labels["app"] = os.environ["CHECK_JOB"]
print(json.dumps({
    "apiVersion": "batch/v1", "kind": "Job",
    "metadata": {"name": os.environ["CHECK_JOB"], "namespace": dep["metadata"]["namespace"]},
    "spec": {"backoffLimit": 0, "activeDeadlineSeconds": 180, "ttlSecondsAfterFinished": 3600,
             "template": {"metadata": {"labels": labels}, "spec": spec}},
}))' | kubectl apply -f - >/dev/null
  check_result=""
  for _ in $(seq 1 90); do
    succeeded="$(kubectl get job "$check_job" -n "$NAMESPACE" -o jsonpath='{.status.succeeded}' 2>/dev/null || true)"
    failed_count="$(kubectl get job "$check_job" -n "$NAMESPACE" -o jsonpath='{.status.failed}' 2>/dev/null || true)"
    [[ "${succeeded:-0}" -ge 1 ]] && { check_result=ok; break; }
    [[ "${failed_count:-0}" -ge 1 ]] && { check_result=failed; break; }
    sleep 2
  done
  check_output="$(kubectl logs "job/$check_job" -n "$NAMESPACE" 2>/dev/null | grep -E '^Anthropic' | tail -1 || true)"
  case "$check_result" in
    ok) ok "${check_output:-Anthropic reachable}" ;;
    failed) err "AI claims examiner cannot reach Claude: ${check_output:-see kubectl logs job/$check_job -n $NAMESPACE}" ;;
    *) err "AI claims examiner check did not finish in time; see kubectl logs job/$check_job -n $NAMESPACE" ;;
  esac
fi

echo
echo "Cloud Health Office demo deployed to $context (security: $SECURITY)."
if [[ "$SECURITY" == production ]]; then
  echo "Sign in at the Cloudflare Access hostname of the portal; port-forwarded requests are not signed in."
else
  echo "Reach it with: scripts/demo/gcp/port-forward.sh"
fi
