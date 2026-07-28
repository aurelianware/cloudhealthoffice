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
SERVICEBUS_ONLY="${SERVICEBUS_ONLY:-false}"
SERVICEBUS_RECONCILIATION_ENABLED="${SERVICEBUS_RECONCILIATION_ENABLED:-true}"
SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS="${SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS:-300}"
CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS="${CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS:-15}"
ADJUDICATION_MAX_CONCURRENT_CALLS="${ADJUDICATION_MAX_CONCURRENT_CALLS:-}"
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
SEED_PARALLELISM="${SEED_PARALLELISM:-$PARALLELISM}"
PROGRESS_EVERY="${PROGRESS_EVERY:-500}"
KIND_CLUSTER_NAME="${KIND_CLUSTER_NAME:-docker-desktop}"
SKIP_BUILD="${SKIP_BUILD:-false}"
JOB_TIMEOUT="${JOB_TIMEOUT:-30m}"
ORIGINAL_ADJUDICATION_MAX_CONCURRENT_CALLS=""

log() {
  printf '\033[1;34m==>\033[0m %s\n' "$*"
}

duration_seconds() {
  local duration="$1"
  if [[ "$duration" =~ ^([0-9]+)([smh])$ ]]; then
    case "${BASH_REMATCH[2]}" in
      s) echo "${BASH_REMATCH[1]}" ;;
      m) echo "$((BASH_REMATCH[1] * 60))" ;;
      h) echo "$((BASH_REMATCH[1] * 3600))" ;;
    esac
    return
  fi

  echo "Unsupported duration '${duration}'; use an integer followed by s, m, or h" >&2
  return 2
}

wait_for_job_terminal() {
  local job_name="$1"
  local timeout="$2"
  local timeout_seconds
  local deadline
  local conditions

  timeout_seconds="$(duration_seconds "$timeout")"
  deadline=$((SECONDS + timeout_seconds))

  while (( SECONDS < deadline )); do
    conditions="$(
      kubectl get job "$job_name" \
        -n "$NAMESPACE" \
        -o jsonpath='{range .status.conditions[*]}{.type}={.status}{"\n"}{end}'
    )"
    if [[ "$conditions" == *"Complete=True"* ]]; then
      return 0
    fi
    if [[ "$conditions" == *"Failed=True"* ]]; then
      return 1
    fi
    sleep 2
  done

  echo "Timed out after ${timeout} waiting for job/${job_name}" >&2
  return 124
}

# Invoked indirectly by the signal/exit trap installed below.
# shellcheck disable=SC2329
restore_claims_concurrency() {
  local exit_code=$?
  trap - EXIT INT TERM HUP

  if [[ -n "$ORIGINAL_ADJUDICATION_MAX_CONCURRENT_CALLS" \
    && "$ORIGINAL_ADJUDICATION_MAX_CONCURRENT_CALLS" != "$ADJUDICATION_MAX_CONCURRENT_CALLS" ]]; then
    log "Restoring claims-service Service Bus concurrency to ${ORIGINAL_ADJUDICATION_MAX_CONCURRENT_CALLS} per replica"
    if kubectl patch configmap claims-service-config \
      -n "$NAMESPACE" \
      --type merge \
      -p "{\"data\":{\"Messaging__AdjudicationMaxConcurrentCalls\":\"${ORIGINAL_ADJUDICATION_MAX_CONCURRENT_CALLS}\"}}" >/dev/null; then
      kubectl rollout restart deployment/claims-service -n "$NAMESPACE" >/dev/null
      kubectl rollout status deployment/claims-service -n "$NAMESPACE" --timeout=180s >/dev/null \
        || echo "WARNING: claims-service rollout did not complete after restoring concurrency" >&2
    else
      echo "WARNING: failed to restore claims-service concurrency to ${ORIGINAL_ADJUDICATION_MAX_CONCURRENT_CALLS}" >&2
    fi
  fi

  exit "$exit_code"
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
  if [[ -n "$ADJUDICATION_MAX_CONCURRENT_CALLS" ]]; then
    ORIGINAL_ADJUDICATION_MAX_CONCURRENT_CALLS="$(
      kubectl get configmap claims-service-config \
        -n "$NAMESPACE" \
        -o jsonpath='{.data.Messaging__AdjudicationMaxConcurrentCalls}'
    )"
    trap restore_claims_concurrency EXIT INT TERM HUP

    log "Setting claims-service Service Bus concurrency to ${ADJUDICATION_MAX_CONCURRENT_CALLS} per replica"
    kubectl patch configmap claims-service-config \
      -n "$NAMESPACE" \
      --type merge \
      -p "{\"data\":{\"Messaging__AdjudicationMaxConcurrentCalls\":\"${ADJUDICATION_MAX_CONCURRENT_CALLS}\"}}" >/dev/null
    kubectl rollout restart deployment/claims-service -n "$NAMESPACE" >/dev/null
  fi

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
            - --seed-parallelism
            - "${SEED_PARALLELISM}"
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
            $(if [[ "$SERVICEBUS_ONLY" == "true" ]]; then printf -- '- --servicebus-only\n'; fi)
            $(if [[ "$SERVICEBUS_RECONCILIATION_ENABLED" != "true" ]]; then printf -- '- --no-servicebus-reconciliation\n'; fi)
            - --servicebus-reconciliation-timeout
            - "${SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS}"
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

# macOS idle sleep pauses the Docker Desktop VM -- and every pod inside it.
# Keep the host awake while polling both successful and failed terminal states.
caffeinate_pid=""
if command -v caffeinate >/dev/null 2>&1; then
  caffeinate -dis -w "$$" &
  caffeinate_pid=$!
fi

job_status=0
wait_for_job_terminal "$JOB_NAME" "$JOB_TIMEOUT" || job_status=$?

if [[ -n "$caffeinate_pid" ]]; then
  kill "$caffeinate_pid" >/dev/null 2>&1 || true
  wait "$caffeinate_pid" 2>/dev/null || true
fi

kubectl logs -n "$NAMESPACE" "job/${JOB_NAME}"
exit "$job_status"
