#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

NAMESPACE="${NAMESPACE:-cloudhealthoffice}"
JOB_NAME="${JOB_NAME:-mcc-validator}"
IMAGE="${IMAGE:-cloudhealthoffice-mcc-platform-validator:local}"
CLAIMS="${CLAIMS:-5000}"
MAX_CLAIMS="${MAX_CLAIMS:-$CLAIMS}"
TENANT="${TENANT:-demo}"
SEED="${SEED:-42}"
CLAIMS_URL="${CLAIMS_URL:-http://claims-service}"
BENEFIT_URL="${BENEFIT_URL:-http://benefit-plan-service}"
MEMBER_URL="${MEMBER_URL:-http://member-service}"
COVERAGE_URL="${COVERAGE_URL:-http://coverage-service}"
PROVIDER_URL="${PROVIDER_URL:-http://provider-service}"
AUTHORIZATION_URL="${AUTHORIZATION_URL:-http://authorization-service}"
SEED_MEMBERS="${SEED_MEMBERS:-true}"
SEED_PROVIDERS="${SEED_PROVIDERS:-true}"
SEED_AUTHORIZATIONS="${SEED_AUTHORIZATIONS:-true}"
CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS="${CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS:-15}"
SERVICE_CATEGORY_ADMIN_WRITE_ENABLED="${SERVICE_CATEGORY_ADMIN_WRITE_ENABLED:-true}"
PEND_OBSERVATION_ENABLED="${PEND_OBSERVATION_ENABLED:-true}"
PEND_OBSERVATION_TIMEOUT_SECONDS="${PEND_OBSERVATION_TIMEOUT_SECONDS:-45}"
PEND_OBSERVATION_INTERVAL_MS="${PEND_OBSERVATION_INTERVAL_MS:-1000}"
# Off by default. Set to a path (e.g. /tmp/mcc-pend-diagnostics.json) to capture
# per-claim pend diagnostics; the aggregate table prints to the job logs regardless
# of whether the JSON artifact itself is copied out of the pod.
PEND_DIAGNOSTICS_PATH="${PEND_DIAGNOSTICS_PATH:-}"
PEND_DIAGNOSTICS_NCCI_SAMPLE="${PEND_DIAGNOSTICS_NCCI_SAMPLE:-200}"
PARALLELISM="${PARALLELISM:-10}"
PROGRESS_EVERY="${PROGRESS_EVERY:-500}"
KIND_CLUSTER_NAME="${KIND_CLUSTER_NAME:-docker-desktop}"
SKIP_BUILD="${SKIP_BUILD:-false}"
JOB_TIMEOUT="${JOB_TIMEOUT:-30m}"

log() {
  printf '\033[1;34m==>\033[0m %s\n' "$*"
}

if [[ "$SKIP_BUILD" != "true" ]]; then
  log "Building ${IMAGE}"
  docker build \
    -t "$IMAGE" \
    --build-arg REGISTRY=mcr.microsoft.com \
    -f "$ROOT_DIR/src/tools/mcc-platform-validator/Dockerfile" \
    "$ROOT_DIR"
fi

if command -v kind >/dev/null 2>&1 && kind get clusters | grep -qx "$KIND_CLUSTER_NAME"; then
  log "Loading ${IMAGE} into kind cluster ${KIND_CLUSTER_NAME}"
  kind load docker-image "$IMAGE" --name "$KIND_CLUSTER_NAME"
fi

if kubectl get deployment -n "$NAMESPACE" claims-service >/dev/null 2>&1; then
  log "Setting claims-service benefit-plan timeout to ${CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS}s"
  kubectl set env deployment/claims-service \
    -n "$NAMESPACE" \
    "Services__BenefitPlanServiceTimeoutSeconds=${CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS}" >/dev/null
  kubectl rollout status deployment/claims-service -n "$NAMESPACE" --timeout=120s >/dev/null
fi

if kubectl get deployment -n "$NAMESPACE" benefit-plan-service >/dev/null 2>&1; then
  log "Setting benefit-plan service-category mapping seed gate to ${SERVICE_CATEGORY_ADMIN_WRITE_ENABLED}"
  kubectl set env deployment/benefit-plan-service \
    -n "$NAMESPACE" \
    "ServiceCategoryMapping__AdminWriteEnabled=${SERVICE_CATEGORY_ADMIN_WRITE_ENABLED}" >/dev/null
  kubectl rollout status deployment/benefit-plan-service -n "$NAMESPACE" --timeout=120s >/dev/null
fi

log "Running ${CLAIMS} claims in namespace ${NAMESPACE} with wait timeout ${JOB_TIMEOUT}"
kubectl delete job -n "$NAMESPACE" "$JOB_NAME" --ignore-not-found >/dev/null
kubectl apply -n "$NAMESPACE" -f - <<EOF
apiVersion: batch/v1
kind: Job
metadata:
  name: ${JOB_NAME}
spec:
  backoffLimit: 0
  template:
    spec:
      restartPolicy: Never
      containers:
        - name: mcc-platform-validator
          image: ${IMAGE}
          imagePullPolicy: IfNotPresent
          args:
            - --claims
            - "${CLAIMS}"
            - --max-claims
            - "${MAX_CLAIMS}"
            - --tenant
            - "${TENANT}"
            - --seed
            - "${SEED}"
            - --parallelism
            - "${PARALLELISM}"
            - --claims-url
            - "${CLAIMS_URL}"
            - --benefit-url
            - "${BENEFIT_URL}"
            - --member-url
            - "${MEMBER_URL}"
            - --coverage-url
            - "${COVERAGE_URL}"
            - --provider-url
            - "${PROVIDER_URL}"
            - --authorization-url
            - "${AUTHORIZATION_URL}"
            $(if [[ "$SEED_MEMBERS" != "true" ]]; then printf -- '- --no-seed-members\n'; fi)
            $(if [[ "$SEED_PROVIDERS" != "true" ]]; then printf -- '- --no-seed-providers\n'; fi)
            $(if [[ "$SEED_AUTHORIZATIONS" != "true" ]]; then printf -- '- --no-seed-authorizations\n'; fi)
            $(if [[ "$PEND_OBSERVATION_ENABLED" != "true" ]]; then printf -- '- --no-pend-observation\n'; fi)
            - --pend-observation-timeout
            - "${PEND_OBSERVATION_TIMEOUT_SECONDS}"
            - --pend-observation-interval-ms
            - "${PEND_OBSERVATION_INTERVAL_MS}"
            $(if [[ -n "$PEND_DIAGNOSTICS_PATH" ]]; then printf -- '- --pend-diagnostics\n            - "%s"\n            - --pend-diagnostics-ncci-sample\n            - "%s"\n' "$PEND_DIAGNOSTICS_PATH" "$PEND_DIAGNOSTICS_NCCI_SAMPLE"; fi)
            - --progress-every
            - "${PROGRESS_EVERY}"
            - --summary-json
            - /tmp/mcc-summary.json
EOF

kubectl wait -n "$NAMESPACE" --for=condition=complete "job/${JOB_NAME}" --timeout="$JOB_TIMEOUT"
kubectl logs -n "$NAMESPACE" "job/${JOB_NAME}"
