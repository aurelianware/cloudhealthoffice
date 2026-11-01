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
#                            member-document-service file through it; correspondence-service
#                            stores rendered letters there)
#                            (terraform outputs documents_bucket and documents_service_account in
#                            infrastructure/gcp). Without them, uploaded documents are not stored.
#   MANAGED_FHIR_STORE  MANAGED_FHIR_STORE_SERVICE_ACCOUNT  [MANAGED_FHIR_STORE_TENANT]
#                            Publish finalized claims' ExplanationOfBenefit to a Cloud Healthcare API
#                            FHIR store, which streams to BigQuery (terraform outputs
#                            managed_fhir_store_id and managed_fhir_store_service_account with
#                            enable_managed_fhir_store = true; ADR 023). Optional; off without them.
#   BULK_EXPORT_BUCKET  FHIR_SERVICE_GOOGLE_ACCOUNT
#                            Provider Access Bulk Data $export files (terraform outputs bulk_export_bucket
#                            and fhir_service_google_account with enable_bulk_export = true). Optional;
#                            without them a Group $export answers 501.
#   CONFORMANCE_FHIR_HOST  CONFORMANCE_AUTH_HOST
#                            Public hostnames the Cloudflare Tunnel routes to fhir-service and
#                            smart-auth-service, so an external suite (hosted Inferno) can run SMART
#                            Backend Services and Bulk Data against the demo. smart-auth-service's
#                            issuer and fhir-service's base URL become these hosts. Optional; both or
#                            neither. Needs CLOUDFLARE_TUNNEL_TOKEN. See docs/deploy/GCP-CONFORMANCE-TESTING.md.
#   PUBLIC_PROVIDER_DIRECTORY
#                            JSON object of the plans whose public Plan-Net Provider Directory
#                            fhir-service publishes, keyed by directory id, each naming its Tenant and
#                            the plan's Name and address: {"demo": {"Tenant": "demo", "Name": "...",
#                            "AddressLine": "...", "City": "...", "State": "FL", "PostalCode": "..."}}.
#                            Optional; nothing is published without it. See
#                            docs/architecture/public-provider-directory.md.
#   TRUSTED_PROXY_NETWORKS   Comma-separated CIDRs smart-auth-service believes X-Forwarded-Proto from
#                            (where cloudflared runs). Default: the cluster's pod range, read with
#                            gcloud (GKE_CLUSTER, GKE_LOCATION), else the nodes' pod CIDRs.
#   CPCDS_DROP_BUCKET  CPCDS_DROP_SERVICE_ACCOUNT
#                            The CPCDS file drop the importer loads from (terraform outputs
#                            cpcds_drop_bucket and cpcds_drop_service_account). Optional; both or neither.
#
# Hosted mode (HOSTED=true): the hosted HIPAA environment that holds real member
# data (docs/deploy/GCP-HOSTED-HIPAA.md, "Deploy"). Only on a cluster built with
# infrastructure/gcp hosted = true, never the demo. It refuses Cloudflare, the
# demo tenant and synthetic data, and changes what this script installs:
#   - disks on an encrypted StorageClass (cho-pd-cmek, the hosted disks key)
#   - Kafka with a TLS-only listener and SCRAM-SHA-512 sign-in
#     (deploy/gcp/hosted/kafka-cluster.yaml)
#   - MongoDB and Redis over TLS from an internal certificate authority this
#     script keeps in the cluster, Redis with a password and an append-only file
#     (deploy/gcp/hosted/redis.yaml)
#   - a CA bundle every service trusts (system CAs, Kafka's cluster CA, the
#     internal CA), mounted over the image's own
#   - database passwords kept in Secret Manager (infrastructure/gcp/hosted-secrets.tf)
#   - the plan's tenant and its administrators instead of the demo seed
#   - staff sign-in through Google Cloud IAP, Production hosts, and public FHIR
#     and SMART hosts behind the Google load balancer (apply the load balancer
#     with deploy/gcp/hosted/apply-ingress.sh)
# Hosted mode needs:
#   TOKEN_SIGNING_KEY_VERSION  TOKEN_SERVICE_GOOGLE_ACCOUNT  KUBERNETES_SA_ISSUER   (as above)
#   IAP_AUDIENCE          the portal backend service, /projects/NUMBER/global/backendServices/ID
#   HOSTED_TENANT         the plan's CHO tenant id (lower case, not demo)
#   HOSTED_TENANT_NAME    the plan's name
#   HOSTED_ADMIN_EMAILS   comma-separated addresses made TenantAdmin of that tenant
#   FHIR_HOST  AUTH_HOST  the public FHIR and SMART host names
#   HOSTED_DISK_KMS_KEY   terraform output hosted_disk_kms_key
#   HOSTED_SECRETS_PROJECT  terraform output hosted_secrets_project
#   DOCUMENTS_BUCKET  DOCUMENTS_SERVICE_ACCOUNT   (as above)
# and optionally ANTHROPIC_BAA_CONFIRMED=true with ANTHROPIC_API_KEY: the AI
# examiner sends claim data to Claude, so it runs only under a BAA with Anthropic.

set -Eeuo pipefail
cd "$(git rev-parse --show-toplevel)"

NAMESPACE="cloudhealthoffice"
KAFKA_NAMESPACE="kafka"
STRIMZI_VERSION="0.45.2"
KAFKA_CLUSTER="cloudhealthoffice-kafka"
KAFKA_BOOTSTRAP="${KAFKA_CLUSTER}-kafka-bootstrap.${KAFKA_NAMESPACE}.svc:9092"
ENABLE_KAFKA="${ENABLE_KAFKA:-true}"
HOSTED="${HOSTED:-false}"
case "$HOSTED" in
  true|false) ;;
  *) echo "HOSTED must be true or false" >&2; exit 1 ;;
esac
if [[ "$HOSTED" == true ]]; then
  # Brokers take TLS and SCRAM only (deploy/gcp/hosted/kafka-cluster.yaml).
  KAFKA_BOOTSTRAP="${KAFKA_CLUSTER}-kafka-bootstrap.${KAFKA_NAMESPACE}.svc:9093"
  RESOURCE_PROFILE="${RESOURCE_PROFILE:-default}"
else
  RESOURCE_PROFILE="${RESOURCE_PROFILE:-small}"
fi
case "$RESOURCE_PROFILE" in
  small) default_replicas=1 ;;
  default) default_replicas=2 ;;
  *) echo "RESOURCE_PROFILE must be small or default" >&2; exit 1 ;;
esac
CLAIMS_REPLICAS="${CLAIMS_REPLICAS:-$default_replicas}"
BENEFIT_PLAN_REPLICAS="${BENEFIT_PLAN_REPLICAS:-$default_replicas}"
# Standard persistent disk: new billing accounts have only 250 GB of SSD quota.
# Hosted: balanced disks encrypted with the hosted disks key.
STORAGE_CLASS="cho-pd-standard"
[[ "$HOSTED" == true ]] && STORAGE_CLASS="cho-pd-cmek"
# Hosted: the services trust this bundle instead of their image's CA file.
CA_BUNDLE_CONFIGMAP="cho-ca-bundle"
# Google's load balancer reaches pods from these ranges (container-native NEGs);
# smart-auth-service believes X-Forwarded-Proto only from them.
GFE_PROXY_NETWORKS="35.191.0.0/16,130.211.0.0/22"
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
if [[ "$HOSTED" == true ]]; then
  SECURITY_SETTINGS=(TOKEN_SIGNING_KEY_VERSION TOKEN_SERVICE_GOOGLE_ACCOUNT KUBERNETES_SA_ISSUER IAP_AUDIENCE)
else
  SECURITY_SETTINGS=(TOKEN_SIGNING_KEY_VERSION TOKEN_SERVICE_GOOGLE_ACCOUNT KUBERNETES_SA_ISSUER CF_ACCESS_TEAM_DOMAIN CF_ACCESS_AUD)
fi
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
if [[ "$HOSTED" == true ]]; then
  # Real member data: every safeguard is required, and nothing demo-shaped is allowed.
  [[ "$SECURITY" == production ]] || err "HOSTED=true needs production security: set ${SECURITY_SETTINGS[*]}"
  for setting in HOSTED_TENANT HOSTED_TENANT_NAME HOSTED_ADMIN_EMAILS FHIR_HOST AUTH_HOST \
      HOSTED_DISK_KMS_KEY HOSTED_SECRETS_PROJECT DOCUMENTS_BUCKET DOCUMENTS_SERVICE_ACCOUNT; do
    [[ -n "${!setting:-}" ]] || err "HOSTED=true needs $setting (docs/deploy/GCP-HOSTED-HIPAA.md, \"Deploy\")"
  done
  for setting in CLOUDFLARE_TUNNEL_TOKEN CF_ACCESS_TEAM_DOMAIN CF_ACCESS_AUD CONFORMANCE_FHIR_HOST \
      CONFORMANCE_AUTH_HOST TRUSTED_PROXY_NETWORKS DEMO_ADMIN_EMAILS MANAGED_FHIR_STORE; do
    [[ -z "${!setting:-}" ]] || err "HOSTED=true refuses $setting: $(case "$setting" in
      CLOUDFLARE*|CF_*) echo "Cloudflare decrypts traffic at its edge and has no BAA with us" ;;
      CONFORMANCE*|TRUSTED*) echo "the public hosts are FHIR_HOST and AUTH_HOST behind the Google load balancer" ;;
      DEMO*) echo "the hosted administrators are HOSTED_ADMIN_EMAILS" ;;
      *) echo "the managed FHIR store copy is not covered by the hosted keys yet" ;;
    esac)"
  done
  [[ "$HOSTED_TENANT" =~ ^[a-z0-9][a-z0-9-]{1,62}$ ]] || err "HOSTED_TENANT must be a lower-case tenant id (letters, digits, hyphens)"
  [[ "$HOSTED_TENANT" != *demo* ]] || err "HOSTED_TENANT must not be a demo tenant"
  [[ "$ENABLE_KAFKA" == true ]] || err "HOSTED=true runs Kafka (claims, accumulators and appeals depend on it)"
  [[ "$RESOURCE_PROFILE" == default ]] || err "HOSTED=true uses RESOURCE_PROFILE=default (two replicas, full requests)"
  [[ "$HOSTED_DISK_KMS_KEY" =~ ^projects/[^/]+/locations/[^/]+/keyRings/[^/]+/cryptoKeys/[^/]+$ ]] \
    || err "HOSTED_DISK_KMS_KEY must be projects/P/locations/L/keyRings/R/cryptoKeys/K (terraform output hosted_disk_kms_key)"
  if [[ -n "${ANTHROPIC_API_KEY:-}" && "${ANTHROPIC_BAA_CONFIRMED:-false}" != true ]]; then
    err "HOSTED=true sends claim data to Claude only under a BAA with Anthropic: set ANTHROPIC_BAA_CONFIRMED=true once it is signed, or leave ANTHROPIC_API_KEY empty"
  fi
fi

for tool in kubectl python3 openssl; do
  command -v "$tool" >/dev/null || err "$tool is required"
done
CA_BASE_BUNDLE="${CA_BASE_BUNDLE:-/etc/ssl/certs/ca-certificates.crt}"
if [[ "$HOSTED" == true ]]; then
  grep -q "BEGIN CERTIFICATE" "$CA_BASE_BUNDLE" 2>/dev/null \
    || err "CA_BASE_BUNDLE ($CA_BASE_BUNDLE) is not a PEM bundle of the public CAs"
fi
if [[ "$ENABLE_KAFKA" == "true" ]]; then
  command -v helm >/dev/null || err "helm is required to install the Strimzi Kafka operator (or set ENABLE_KAFKA=false)"
fi
python3 -c "import yaml" 2>/dev/null || err "PyYAML is required (pip install pyyaml)"
context="$(kubectl config current-context)"
[[ "$context" == gke_* ]] || warn "kubectl context is '$context', not a GKE context — continuing anyway"
if [[ "$HOSTED" == true && "$context" == *demo* ]]; then
  err "kubectl context '$context' looks like the demo cluster; HOSTED=true never deploys there"
fi
kubectl cluster-info >/dev/null || err "cluster not reachable"
ok "context $context, images ${GAR_REPOSITORY}:${IMAGE_TAG}"
if [[ "$HOSTED" == true ]]; then
  ok "hosted: tenant $HOSTED_TENANT, Production hosts, Google Cloud IAP sign-in, Cloud KMS signing"
elif [[ "$SECURITY" == production ]]; then
  ok "security: production (Cloud KMS signing, Kubernetes service identity, Cloudflare Access sign-in)"
else
  warn "security: demo (public development key, built-in demo sign-in). Set ${SECURITY_SETTINGS[*]} for production security."
fi

kubectl create namespace "$NAMESPACE" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
ok "namespace $NAMESPACE"

if [[ "$HOSTED" == true ]]; then
  # Retain: deleting a claim (or the cluster) keeps the disk, so data is only
  # ever removed on purpose.
  kubectl apply -f - >/dev/null <<YAML
apiVersion: storage.k8s.io/v1
kind: StorageClass
metadata:
  name: $STORAGE_CLASS
  labels:
    app: cloudhealthoffice
provisioner: pd.csi.storage.gke.io
parameters:
  type: pd-balanced
  disk-encryption-kms-key: $HOSTED_DISK_KMS_KEY
volumeBindingMode: WaitForFirstConsumer
allowVolumeExpansion: true
reclaimPolicy: Retain
YAML
  ok "storage class $STORAGE_CLASS (pd-balanced, encrypted with ${HOSTED_DISK_KMS_KEY##*/})"
else
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
fi

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

# Hosted: Secret Manager holds each password (infrastructure/gcp/hosted-secrets.tf),
# so a rebuilt or restored cluster gets the same ones. A secret with no version
# yet takes the cluster's current value, or a new one. Any other error stops the
# deploy: a fresh password would lock the services out of their database.
hosted_password() {
  local secret="$1" k8s_secret="$2" k8s_key="$3" value versions
  if value="$(gcloud secrets versions access latest --secret "$secret" --project "$HOSTED_SECRETS_PROJECT" 2>/dev/null)" \
      && [[ -n "$value" ]]; then
    printf '%s' "$value"; return
  fi
  versions="$(gcloud secrets versions list "$secret" --project "$HOSTED_SECRETS_PROJECT" \
    --filter='state=ENABLED' --limit 1 --format='value(name)')" \
    || err "Cannot read Secret Manager secret $secret in $HOSTED_SECRETS_PROJECT (terraform apply with hosted = true creates it and grants the deployer access)"
  [[ -z "$versions" ]] || err "Secret Manager secret $secret has a version the deployer cannot read"
  value="$(existing_secret_value "$k8s_secret" "$k8s_key")"
  [[ -n "$value" ]] || value="$(random_secret)"
  printf '%s' "$value" | gcloud secrets versions add "$secret" --project "$HOSTED_SECRETS_PROJECT" --data-file=- >/dev/null \
    || err "Could not store $secret in Secret Manager"
  printf '%s' "$value"
}

if [[ "$HOSTED" == true ]]; then
  log "Creating hosted secrets (passwords from Secret Manager)"
  MONGO_PASS="$(hosted_password cho-mongodb-password mongodb-auth password)"
  POSTGRES_PASSWORD="$(hosted_password cho-postgres-password reference-data-service-secret POSTGRES_PASSWORD)"
  PRICING_ADMIN_SECRET="$(hosted_password cho-pricing-admin-secret pricing-api-secret AdminSecret)"
  REDIS_PASS="$(hosted_password cho-redis-password redis-auth password)"
  ok "passwords from Secret Manager ($HOSTED_SECRETS_PROJECT)"
else
  log "Creating demo secrets"
  MONGO_PASS="$(existing_secret_value mongodb-auth password)"
  [[ -n "$MONGO_PASS" ]] || MONGO_PASS="$(random_secret)"
  POSTGRES_PASSWORD="$(existing_secret_value reference-data-service-secret POSTGRES_PASSWORD)"
  [[ -n "$POSTGRES_PASSWORD" ]] || POSTGRES_PASSWORD="$(random_secret)"
  PRICING_ADMIN_SECRET="$(existing_secret_value pricing-api-secret AdminSecret)"
  [[ -n "$PRICING_ADMIN_SECRET" ]] || PRICING_ADMIN_SECRET="$(random_secret)"
fi
MONGO_HOST="mongodb.${NAMESPACE}.svc.cluster.local"
REDIS_HOST="redis-dataprotection.${NAMESPACE}.svc.cluster.local"
MONGO_CONN="mongodb://${MONGO_USER}:${MONGO_PASS}@${MONGO_HOST}:27017/?authSource=admin"
REDIS_CONN="${REDIS_HOST}:6379"
if [[ "$HOSTED" == true ]]; then
  # The drivers check these certificates against the CA bundle (below).
  MONGO_CONN="${MONGO_CONN}&tls=true"
  REDIS_CONN="${REDIS_CONN},ssl=true,sslHost=${REDIS_HOST},password=${REDIS_PASS},abortConnect=false"
fi
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
# Hosted: written once Kafka is up, with the cho-services user's password.
[[ "$HOSTED" == true ]] || upsert_secret kafka-secret bootstrapServers="$KAFKA_VALUE" saslUsername= saslPassword=
if [[ "$HOSTED" == true ]]; then
  upsert_secret redis-auth password="$REDIS_PASS"
fi
upsert_secret anthropic-secret apiKey="${ANTHROPIC_API_KEY:-not-configured}"
# Entra ID is not used on GKE (Cloudflare Access or IAP sign-in, Kubernetes
# service identity); placeholders keep pods starting.
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
upsert_secret correspondence-service-secret MongoDb__ConnectionString="$MONGO_CONN"
upsert_secret sponsor-service-secrets MongoDb__ConnectionString="$MONGO_CONN"
upsert_secret smart-auth-service-secrets MongoDb__ConnectionString="$MONGO_CONN" \
  MongoDb__DatabaseName=CloudHealthOffice
# Retain the installed key on manual runs that do not provide a replacement.
SAM_KEY_VALUE="${SAM_API_KEY:-$(existing_secret_value provider-verification-service-secrets ProviderVerification__SamGovApiKey)}"
case "${SAM_SCREENING_ENABLED:-false}" in
  true) [[ -n "$SAM_KEY_VALUE" && "$SAM_KEY_VALUE" != not-configured ]] || err "SAM screening requires SAM_API_KEY" ;;
  false) ;;
  *) err "SAM_SCREENING_ENABLED must be true or false" ;;
esac
# Feed the payload over stdin: the API key is never a command-line argument.
SAM_KEY_VALUE="$SAM_KEY_VALUE" SAM_MONGO_CONN="$MONGO_CONN" SAM_NAMESPACE="$NAMESPACE" python3 -c '
import base64,json,os
encode=lambda value:base64.b64encode(value.encode()).decode()
print(json.dumps({"apiVersion":"v1","kind":"Secret","metadata":{"name":"provider-verification-service-secrets","namespace":os.environ["SAM_NAMESPACE"]},"type":"Opaque","data":{"MongoDb__ConnectionString":encode(os.environ["SAM_MONGO_CONN"]),"ProviderVerification__SamGovApiKey":encode(os.environ["SAM_KEY_VALUE"])}}))
' | kubectl apply -f - >/dev/null
unset SAM_KEY_VALUE
ok "provider-verification-service-secrets"

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

# ── Internal certificate authority (hosted) ──────────────────────────────────
# MongoDB and Redis serve TLS with a certificate from a private CA kept in
# Secret cho-internal-ca (encrypted with the gke-secrets key, and in the GKE
# backups). The server certificate is reissued when it is within 30 days of
# expiry or was not signed by the current CA.
if [[ "$HOSTED" == true ]]; then
  log "Internal certificate authority"
  tls_dir="$(mktemp -d)"
  chmod 700 "$tls_dir"
  existing_secret_value cho-internal-ca ca.crt > "$tls_dir/ca.crt"
  existing_secret_value cho-internal-ca ca.key > "$tls_dir/ca.key"
  if ! openssl x509 -in "$tls_dir/ca.crt" -noout -checkend 31536000 >/dev/null 2>&1 \
      || ! openssl pkey -in "$tls_dir/ca.key" -noout >/dev/null 2>&1; then
    openssl req -x509 -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -sha256 -days 3650 \
      -subj "/O=Cloud Health Office/CN=CHO hosted internal CA" \
      -addext "basicConstraints=critical,CA:TRUE,pathlen:0" -addext "keyUsage=critical,keyCertSign,cRLSign" \
      -keyout "$tls_dir/ca.key" -out "$tls_dir/ca.crt" 2>/dev/null
    kubectl create secret generic cho-internal-ca -n "$NAMESPACE" \
      --from-file=ca.crt="$tls_dir/ca.crt" --from-file=ca.key="$tls_dir/ca.key" \
      --dry-run=client -o yaml | kubectl apply -f - >/dev/null
    ok "new internal CA (ten years)"
  else
    ok "internal CA (expires $(openssl x509 -in "$tls_dir/ca.crt" -noout -enddate | cut -d= -f2))"
  fi
  existing_secret_value cho-internal-tls tls.crt > "$tls_dir/tls.crt"
  existing_secret_value cho-internal-tls tls.key > "$tls_dir/tls.key"
  if ! openssl x509 -in "$tls_dir/tls.crt" -noout -checkend 2592000 >/dev/null 2>&1 \
      || ! openssl verify -CAfile "$tls_dir/ca.crt" "$tls_dir/tls.crt" >/dev/null 2>&1 \
      || ! openssl pkey -in "$tls_dir/tls.key" -noout >/dev/null 2>&1; then
    san="DNS:localhost,IP:127.0.0.1"
    for svc in mongodb redis-dataprotection; do
      san+=",DNS:${svc},DNS:${svc}.${NAMESPACE},DNS:${svc}.${NAMESPACE}.svc,DNS:${svc}.${NAMESPACE}.svc.cluster.local"
    done
    san+=",DNS:mongodb-0.mongodb.${NAMESPACE}.svc.cluster.local,DNS:redis-dataprotection-0.redis-dataprotection.${NAMESPACE}.svc.cluster.local"
    openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -sha256 \
      -subj "/O=Cloud Health Office/CN=${MONGO_HOST}" -keyout "$tls_dir/tls.key" -out "$tls_dir/tls.csr" 2>/dev/null
    printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature\nbasicConstraints=critical,CA:FALSE\n' "$san" > "$tls_dir/ext.cnf"
    openssl x509 -req -in "$tls_dir/tls.csr" -CA "$tls_dir/ca.crt" -CAkey "$tls_dir/ca.key" -CAcreateserial \
      -days 825 -sha256 -extfile "$tls_dir/ext.cnf" -out "$tls_dir/tls.crt" 2>/dev/null
    ok "new MongoDB and Redis certificate (825 days)"
  else
    ok "MongoDB and Redis certificate (expires $(openssl x509 -in "$tls_dir/tls.crt" -noout -enddate | cut -d= -f2))"
  fi
  # MongoDB reads certificate and key from one file.
  cat "$tls_dir/tls.crt" "$tls_dir/tls.key" > "$tls_dir/server.pem"
  kubectl create secret generic cho-internal-tls -n "$NAMESPACE" \
    --from-file=tls.crt="$tls_dir/tls.crt" --from-file=tls.key="$tls_dir/tls.key" \
    --from-file=ca.crt="$tls_dir/ca.crt" --from-file=server.pem="$tls_dir/server.pem" \
    --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  cp "$tls_dir/ca.crt" "$tls_dir/internal-ca.crt"
  rm -f "$tls_dir/ca.key" "$tls_dir/tls.key" "$tls_dir/server.pem" "$tls_dir/tls.csr"
  ok "cho-internal-tls"
fi

# ── Kafka (Strimzi) ──────────────────────────────────────────────────────────
if [[ "$ENABLE_KAFKA" == "true" ]]; then
  log "Installing Kafka (Strimzi ${STRIMZI_VERSION}, 3 KRaft brokers)"
  helm upgrade --install strimzi oci://quay.io/strimzi-helm/strimzi-kafka-operator \
    --version "$STRIMZI_VERSION" --namespace "$KAFKA_NAMESPACE" --create-namespace --wait >/dev/null
  ok "strimzi operator"
  if [[ "$HOSTED" == true ]]; then
    kubectl apply -f deploy/gcp/hosted/kafka-cluster.yaml >/dev/null
  else
    kubectl apply -f deploy/gcp/kafka/kafka-cluster.yaml >/dev/null
  fi
  kubectl wait kafka/"$KAFKA_CLUSTER" -n "$KAFKA_NAMESPACE" --for=condition=Ready --timeout=900s >/dev/null \
    && ok "kafka cluster ready ($KAFKA_BOOTSTRAP)" \
    || err "Kafka cluster not ready; check: kubectl get kafka,kafkanodepool,pods -n $KAFKA_NAMESPACE"
  kubectl apply -f infrastructure/kafka/topics.yaml >/dev/null
  ok "topics from infrastructure/kafka/topics.yaml"
  if [[ "$HOSTED" == true ]]; then
    kafka_password=""
    for _ in $(seq 1 60); do
      kafka_password="$(kubectl get secret cho-services -n "$KAFKA_NAMESPACE" -o jsonpath='{.data.password}' 2>/dev/null | base64 -d 2>/dev/null || true)"
      [[ -n "$kafka_password" ]] && break
      sleep 3
    done
    [[ -n "$kafka_password" ]] || err "Kafka user cho-services has no password yet; check: kubectl get kafkauser -n $KAFKA_NAMESPACE"
    upsert_secret kafka-secret bootstrapServers="$KAFKA_VALUE" saslUsername=cho-services saslPassword="$kafka_password"
    unset kafka_password
  fi
else
  warn "Kafka skipped (ENABLE_KAFKA=false); services use in-process messaging"
fi

# ── CA bundle (hosted) ───────────────────────────────────────────────────────
# The public CAs, every certificate of Kafka's cluster CA (both during a
# renewal) and the internal CA. render_manifests.py mounts it over each
# container's system bundle.
if [[ "$HOSTED" == true ]]; then
  kubectl get secret "${KAFKA_CLUSTER}-cluster-ca-cert" -n "$KAFKA_NAMESPACE" -o json \
    | python3 -c '
import base64, json, sys
data = json.load(sys.stdin)["data"]
print("".join(base64.b64decode(v).decode() for k, v in sorted(data.items()) if k.endswith(".crt")), end="")
' > "$tls_dir/kafka-ca.crt"
  grep -q "BEGIN CERTIFICATE" "$tls_dir/kafka-ca.crt" || err "Kafka cluster CA not found (secret ${KAFKA_CLUSTER}-cluster-ca-cert)"
  cat "$CA_BASE_BUNDLE" "$tls_dir/kafka-ca.crt" "$tls_dir/internal-ca.crt" > "$tls_dir/ca-certificates.crt"
  kubectl create configmap "$CA_BUNDLE_CONFIGMAP" -n "$NAMESPACE" \
    --from-file=ca-certificates.crt="$tls_dir/ca-certificates.crt" \
    --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  ok "$CA_BUNDLE_CONFIGMAP ($(grep -c 'BEGIN CERTIFICATE' "$tls_dir/ca-certificates.crt") certificates)"
  rm -rf "$tls_dir"
fi

# ── MongoDB and Redis ────────────────────────────────────────────────────────
log "Deploying MongoDB and Redis"
# The manifest sets a 3.5 GB WiredTiger cache but requests 2Gi. Request enough
# memory for the cache, put the data on standard disk, and with the small
# profile lower the CPU request so everything fits on one node.
# Hosted: TLS only (requireTLS) with the internal certificate, and a larger disk.
RESOURCE_PROFILE="$RESOURCE_PROFILE" STORAGE_CLASS="$STORAGE_CLASS" HOSTED="$HOSTED" python3 - <<'PY' | kubectl apply -f - >/dev/null
import os, sys, yaml
hosted = os.environ["HOSTED"] == "true"
docs = [d for d in yaml.safe_load_all(open("infrastructure/k8s/mongodb-deployment.yaml")) if d]
for d in docs:
    if d.get("kind") == "StatefulSet":
        pod = d["spec"]["template"]["spec"]
        for c in pod["containers"]:
            requests = c.setdefault("resources", {}).setdefault("requests", {})
            requests["memory"] = "5Gi"
            if os.environ["RESOURCE_PROFILE"] == "small":
                requests["cpu"] = "250m"
            if hosted:
                c["args"] = c.get("args", []) + [
                    "--tlsMode", "requireTLS",
                    "--tlsCertificateKeyFile", "/etc/cho-tls/server.pem",
                    "--tlsCAFile", "/etc/cho-tls/ca.crt",
                    "--tlsAllowConnectionsWithoutCertificates",
                    "--tlsDisabledProtocols", "TLS1_0,TLS1_1",
                ]
                c.setdefault("volumeMounts", []).append(
                    {"name": "tls", "mountPath": "/etc/cho-tls", "readOnly": True})
        if hosted:
            # mongod runs as uid 999; the group lets it read the key.
            pod.setdefault("securityContext", {})["fsGroup"] = 999
            pod.setdefault("volumes", []).append(
                {"name": "tls", "secret": {"secretName": "cho-internal-tls", "defaultMode": 0o440}})
        for t in d["spec"].get("volumeClaimTemplates", []):
            t["spec"]["storageClassName"] = os.environ["STORAGE_CLASS"]
            if hosted:
                t["spec"]["resources"]["requests"]["storage"] = "100Gi"
yaml.safe_dump_all(docs, sys.stdout, sort_keys=False)
PY
if [[ "$HOSTED" == true ]]; then
  kubectl apply -f deploy/gcp/hosted/redis.yaml >/dev/null
  kubectl rollout status statefulset/redis-dataprotection -n "$NAMESPACE" --timeout=300s >/dev/null \
    && ok "redis (TLS, password, append-only file)" || err "Redis did not become ready"
else
  kubectl apply -f infrastructure/k8s/redis-dataprotection.yaml >/dev/null
fi
MONGOSH=(mongosh --username "$MONGO_USER" --password "$MONGO_PASS" --authenticationDatabase admin --quiet)
[[ "$HOSTED" == true ]] && MONGOSH+=(--tls --tlsCAFile /etc/cho-tls/ca.crt --host localhost)
kubectl rollout status statefulset/mongodb -n "$NAMESPACE" --timeout=600s >/dev/null \
  && ok "mongodb" || err "MongoDB did not become ready"

mongo_ready=false
for _ in $(seq 1 60); do
  if kubectl exec -n "$NAMESPACE" mongodb-0 -- "${MONGOSH[@]}" \
      --eval 'db.adminCommand({ ping: 1 }).ok' >/dev/null 2>&1; then
    mongo_ready=true; break
  fi
  sleep 3
done
[[ "$mongo_ready" == true ]] || err "MongoDB never accepted an authenticated connection"
ok "mongodb accepting connections"

if [[ "$HOSTED" == true ]]; then
  # No synthetic tenant or member: the plan's own data arrives through the
  # CPCDS file drop and the configured feeds.
  SEED_TENANT="$HOSTED_TENANT" SEED_TENANT_NAME="$HOSTED_TENANT_NAME" SEED_IS_DEMO=false
  SEED_ADMIN_EMAILS="$HOSTED_ADMIN_EMAILS"
else
  SEED_TENANT=demo SEED_TENANT_NAME="Demo Health Plan" SEED_IS_DEMO=true
  SEED_ADMIN_EMAILS="${DEMO_ADMIN_EMAILS:-}"
  log "Seeding the synthetic demo tenant"
  kubectl exec -n "$NAMESPACE" mongodb-0 -- "${MONGOSH[@]}" --eval '
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
fi

if [[ "$SECURITY" == production ]]; then
  # tenant-service's identity read model: the tenant, and an Active TenantAdmin
  # for each address in DEMO_ADMIN_EMAILS (HOSTED_ADMIN_EMAILS when hosted).
  # Cloudflare Access or IAP decides who reaches the portal; these records
  # decide who gets a token in the tenant. Existing users keep their roles and
  # status (insert only).
  [[ -n "$SEED_ADMIN_EMAILS" ]] \
    || warn "DEMO_ADMIN_EMAILS is empty: no one has a user in the $SEED_TENANT tenant, so no one can sign in until a user is added"
  log "Seeding the $SEED_TENANT tenant's administrators"
  if [[ "$HOSTED" == true ]]; then
    # The hosted tenant also gets its entry in the tenant list the portal reads.
    kubectl exec -n "$NAMESPACE" mongodb-0 -- \
      env SEED_TENANT="$SEED_TENANT" SEED_TENANT_NAME="$SEED_TENANT_NAME" "${MONGOSH[@]}" --eval '
        db = db.getSiblingDB("CloudHealthOffice");
        const tenant = process.env.SEED_TENANT;
        db.tenants.updateOne({ azureTenantId: tenant }, { $set: {
          azureTenantId: tenant, organizationName: process.env.SEED_TENANT_NAME, tier: "enterprise",
          isDemo: false, subscriptionStatus: "Active" }, $setOnInsert: { createdAt: new Date() } }, { upsert: true });
      ' >/dev/null || err "Could not record tenant $SEED_TENANT"
  fi
  kubectl exec -n "$NAMESPACE" mongodb-0 -- \
    env SEED_TENANT="$SEED_TENANT" SEED_TENANT_NAME="$SEED_TENANT_NAME" SEED_IS_DEMO="$SEED_IS_DEMO" \
    SEED_ADMIN_EMAILS="$SEED_ADMIN_EMAILS" "${MONGOSH[@]}" --eval '
      db = db.getSiblingDB("CloudHealthOffice");
      const tenant = process.env.SEED_TENANT;
      db.Tenants.updateOne({ tenantId: tenant, azureTenantId: tenant }, { $set: {
        tenantId: tenant, azureTenantId: tenant, organizationName: process.env.SEED_TENANT_NAME, tier: "enterprise",
        isDemo: process.env.SEED_IS_DEMO === "true", subscriptionStatus: "Active", updatedAt: new Date() },
        $setOnInsert: { createdAt: new Date() } }, { upsert: true });
      const roles = ["TenantAdmin", "ClaimsSupervisor", "MemberServices", "ProviderRelations", "Finance"];
      for (const raw of (process.env.SEED_ADMIN_EMAILS || "").split(",")) {
        const email = raw.trim();
        if (!email) continue;
        const normalized = email.toLowerCase();
        db.TenantUsers.updateOne({ tenantId: tenant, emailNormalized: normalized }, { $setOnInsert: {
          _id: tenant + "-admin-" + require("crypto").createHash("sha256").update(normalized).digest("hex").slice(0, 32),
          tenantId: tenant, email: email,
          emailNormalized: normalized, azureAdObjectId: "", azureAdTenantId: "", displayName: email,
          firstName: "", lastName: "", roles: roles, department: "Administration", status: "Active",
          createdAt: new Date(), updatedAt: new Date(), createdBy: "deploy-gke.sh" } }, { upsert: true });
      }
    ' >/dev/null && ok "$SEED_TENANT tenant users (${SEED_ADMIN_EMAILS:-none})" \
    || { [[ "$HOSTED" == true ]] && err "Could not seed the $SEED_TENANT administrators; no one could sign in"; \
         warn "$SEED_TENANT tenant user seed failed; rerun this script to retry"; }
fi

# ── Services ─────────────────────────────────────────────────────────────────
log "Rendering and applying service manifests"
render_args=(--registry "$GAR_REPOSITORY" --tag "$IMAGE_TAG"
  --claims-replicas "$CLAIMS_REPLICAS" --benefit-plan-replicas "$BENEFIT_PLAN_REPLICAS"
  --storage-class "$STORAGE_CLASS" --resource-profile "$RESOURCE_PROFILE")
[[ -n "$KAFKA_VALUE" ]] && render_args+=(--kafka-bootstrap "$KAFKA_VALUE")
if [[ -n "${CPCDS_DROP_BUCKET:-}" && -n "${CPCDS_DROP_SERVICE_ACCOUNT:-}" ]]; then
  render_args+=(--cpcds-drop-bucket "$CPCDS_DROP_BUCKET" --cpcds-drop-service-account "$CPCDS_DROP_SERVICE_ACCOUNT")
elif [[ -n "${CPCDS_DROP_BUCKET:-}${CPCDS_DROP_SERVICE_ACCOUNT:-}" ]]; then
  err "CPCDS_DROP_BUCKET and CPCDS_DROP_SERVICE_ACCOUNT go together"
fi
[[ "$AI_EXAMINER" == true ]] && render_args+=(--ai-examiner)
[[ "${SAM_SCREENING_ENABLED:-false}" == true ]] && render_args+=(--sam-screening)
case "${LEIE_SCREENING_ENABLED:-false}" in
  true) render_args+=(--leie-screening) ;;
  false) ;;
  *) err "LEIE_SCREENING_ENABLED must be true or false" ;;
esac
if [[ "$HOSTED" == true ]]; then
  # Production hosts, IAP sign-in for the plan's tenant, and the public FHIR
  # and SMART hosts behind Google's load balancer, which delivers HTTPS to the
  # pods as HTTP from the GFE ranges.
  render_args+=(--security production --hosted --access-provider google-iap
    --token-signing-key-version "$TOKEN_SIGNING_KEY_VERSION"
    --token-service-google-account "$TOKEN_SERVICE_GOOGLE_ACCOUNT"
    --kubernetes-issuer "$KUBERNETES_SA_ISSUER"
    --token-public-key-file "$TOKEN_PUBLIC_KEY_FILE"
    --access-audience "$IAP_AUDIENCE"
    --access-tenant "$HOSTED_TENANT"
    --conformance-fhir-host "$FHIR_HOST" --conformance-auth-host "$AUTH_HOST"
    --conformance-tenant "$HOSTED_TENANT"
    --ca-bundle-configmap "$CA_BUNDLE_CONFIGMAP")
  IFS=',' read -ra networks <<<"$GFE_PROXY_NETWORKS"
  for network in "${networks[@]}"; do render_args+=(--trusted-proxy-network "$network"); done
  ok "public hosts: https://$FHIR_HOST/fhir/r4, issuer https://$AUTH_HOST (behind the Google load balancer)"
elif [[ "$SECURITY" == production ]]; then
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
if [[ -n "${APPEALS_SECRETS_PROJECT:-}" && -n "${APPEALS_SECRETS_SERVICE_ACCOUNT:-}" ]]; then
  render_args+=(--appeals-secrets-project "$APPEALS_SECRETS_PROJECT"
    --appeals-secrets-service-account "$APPEALS_SECRETS_SERVICE_ACCOUNT")
elif [[ -n "${APPEALS_SECRETS_PROJECT:-}${APPEALS_SECRETS_SERVICE_ACCOUNT:-}" ]]; then
  err "APPEALS_SECRETS_PROJECT and APPEALS_SECRETS_SERVICE_ACCOUNT must both be set"
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
if [[ -n "${BULK_EXPORT_BUCKET:-}" && -n "${FHIR_SERVICE_GOOGLE_ACCOUNT:-}" ]]; then
  render_args+=(--bulk-export-bucket "$BULK_EXPORT_BUCKET" --fhir-service-google-account "$FHIR_SERVICE_GOOGLE_ACCOUNT")
elif [[ -n "${BULK_EXPORT_BUCKET:-}${FHIR_SERVICE_GOOGLE_ACCOUNT:-}" ]]; then
  warn "BULK_EXPORT_BUCKET and FHIR_SERVICE_GOOGLE_ACCOUNT go together; bulk export is off"
fi
if [[ -n "${CONFORMANCE_FHIR_HOST:-}" && -n "${CONFORMANCE_AUTH_HOST:-}" ]]; then
  [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]] \
    || err "CONFORMANCE_FHIR_HOST / CONFORMANCE_AUTH_HOST are routed by the Cloudflare Tunnel; set CLOUDFLARE_TUNNEL_TOKEN"
  # cloudflared delivers the HTTPS requests it carries as plain HTTP, so
  # smart-auth-service learns the scheme from X-Forwarded-Proto, believed only
  # from the pod range cloudflared runs in.
  proxy_networks="${TRUSTED_PROXY_NETWORKS:-}"
  if [[ -z "$proxy_networks" && -n "${GKE_CLUSTER:-}" && -n "${GKE_LOCATION:-}" ]] && command -v gcloud >/dev/null; then
    proxy_networks="$(gcloud container clusters describe "$GKE_CLUSTER" --location "$GKE_LOCATION" \
      ${GCP_PROJECT_ID:+--project "$GCP_PROJECT_ID"} --format='value(clusterIpv4Cidr)' 2>/dev/null || true)"
  fi
  if [[ -z "$proxy_networks" ]]; then
    proxy_networks="$(kubectl get nodes -o jsonpath='{range .items[*]}{.spec.podCIDR}{","}{end}')"
    proxy_networks="${proxy_networks%,}"
    [[ -n "$proxy_networks" ]] && warn "Trusting the current nodes' pod CIDRs ($proxy_networks); a replaced node is not trusted until the next deploy. Set TRUSTED_PROXY_NETWORKS to the cluster pod range to avoid that."
  fi
  [[ -n "$proxy_networks" ]] || err "Could not find the cluster pod range; set TRUSTED_PROXY_NETWORKS"
  render_args+=(--conformance-fhir-host "$CONFORMANCE_FHIR_HOST" --conformance-auth-host "$CONFORMANCE_AUTH_HOST")
  IFS=',' read -ra networks <<<"$proxy_networks"
  for network in "${networks[@]}"; do
    [[ -n "${network// /}" ]] && render_args+=(--trusted-proxy-network "${network// /}")
  done
  ok "conformance hostnames: https://$CONFORMANCE_FHIR_HOST/fhir/r4, issuer https://$CONFORMANCE_AUTH_HOST (proxy networks $proxy_networks)"
  warn "Route these hostnames only behind Cloudflare Access; bypass just the paths in docs/deploy/GCP-CONFORMANCE-TESTING.md, and only while a suite runs"
elif [[ -n "${CONFORMANCE_FHIR_HOST:-}${CONFORMANCE_AUTH_HOST:-}" ]]; then
  err "CONFORMANCE_FHIR_HOST and CONFORMANCE_AUTH_HOST go together"
fi
if [[ -n "${PUBLIC_PROVIDER_DIRECTORY:-}" ]]; then
  render_args+=(--public-directory "$PUBLIC_PROVIDER_DIRECTORY")
  ok "public Provider Directory: $(python3 -c 'import json,sys; print(", ".join(sorted(json.loads(sys.argv[1]))))' "$PUBLIC_PROVIDER_DIRECTORY" 2>/dev/null || echo '(checked by render_manifests.py)') at /fhir/r4/directory/<id>/"
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
  if [[ "$HOSTED" == true ]]; then
    ok "no Cloudflare Tunnel (hosted traffic enters through the Google load balancer)"
  else
    warn "Cloudflare Tunnel off (set CLOUDFLARE_TUNNEL_TOKEN to enable); reach the demo with port-forward"
  fi
fi

# ── Wait ─────────────────────────────────────────────────────────────────────
log "Starting the private document scanner"
kubectl apply -n "$NAMESPACE" -f deploy/gcp/document-scanner.yaml >/dev/null
kubectl rollout status -n "$NAMESPACE" deployment/document-scanner --timeout=1200s

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
if [[ "$HOSTED" == true ]]; then
  echo "Cloud Health Office hosted environment deployed to $context (tenant $HOSTED_TENANT)."
  echo "Apply or refresh the load balancer with deploy/gcp/hosted/apply-ingress.sh, then sign in at the portal host through IAP."
  exit 0
fi
echo "Cloud Health Office demo deployed to $context (security: $SECURITY)."
if [[ "$SECURITY" == production ]]; then
  echo "Sign in at the Cloudflare Access hostname of the portal; port-forwarded requests are not signed in."
else
  echo "Reach it with: scripts/demo/gcp/port-forward.sh"
fi
