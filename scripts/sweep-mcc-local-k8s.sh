#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   CLAIMS=1000 PARALLELISM_VALUES="8 10 11 12" REPEATS=2 ./scripts/sweep-mcc-local-k8s.sh
#
# For a quick smoke test with an image already loaded locally:
#   SKIP_BUILD=true CLAIMS=20 PARALLELISM_VALUES="2" REPEATS=1 ./scripts/sweep-mcc-local-k8s.sh
#
# For repeat benchmarks after synthetic providers have already been seeded:
#   SKIP_BUILD=true SEED_PROVIDERS=false CLAIMS=50000 PARALLELISM_VALUES="10" REPEATS=1 ./scripts/sweep-mcc-local-k8s.sh

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

NAMESPACE="${NAMESPACE:-cloudhealthoffice}"
IMAGE="${IMAGE:-cloudhealthoffice-mcc-platform-validator:local}"
CLAIMS="${CLAIMS:-1000}"
MAX_CLAIMS="${MAX_CLAIMS:-$CLAIMS}"
TENANT="${TENANT:-demo}"
SEED_MEMBERS="${SEED_MEMBERS:-true}"
SEED_PROVIDERS="${SEED_PROVIDERS:-true}"
SERVICEBUS_ONLY="${SERVICEBUS_ONLY:-false}"
SERVICEBUS_RECONCILIATION_ENABLED="${SERVICEBUS_RECONCILIATION_ENABLED:-true}"
SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS="${SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS:-300}"
CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS="${CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS:-15}"
PEND_OBSERVATION_ENABLED="${PEND_OBSERVATION_ENABLED:-true}"
PEND_OBSERVATION_TIMEOUT_SECONDS="${PEND_OBSERVATION_TIMEOUT_SECONDS:-45}"
PEND_OBSERVATION_INTERVAL_MS="${PEND_OBSERVATION_INTERVAL_MS:-1000}"
PARALLELISM_VALUES="${PARALLELISM_VALUES:-8 10 11 12}"
REPEATS="${REPEATS:-2}"
PROGRESS_EVERY="${PROGRESS_EVERY:-100}"
KIND_CLUSTER_NAME="${KIND_CLUSTER_NAME:-docker-desktop}"
SKIP_BUILD="${SKIP_BUILD:-false}"
TIMEOUT="${TIMEOUT:-30m}"
OUTPUT_DIR="${OUTPUT_DIR:-/tmp/mcc-sweep-$(date +%Y%m%d%H%M%S)}"
CLEAN_SWEEP_JOBS="${CLEAN_SWEEP_JOBS:-true}"

log() {
  printf '\033[1;34m==>\033[0m %s\n' "$*"
}

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required tool: $1" >&2
    exit 1
  fi
}

extract_summary_json() {
  awk '
    /__MCC_SUMMARY_JSON_BEGIN__/ { capture = 1; next }
    /__MCC_SUMMARY_JSON_END__/ { capture = 0 }
    capture { print }
  '
}

json_number() {
  local file="$1"
  local filter="$2"
  jq -r "$filter // 0 | if type == \"number\" then . else 0 end" "$file"
}

stage_p95() {
  local file="$1"
  local label="$2"
  jq -r --arg label "$label" '
    (.adjudicationStepTimings // [])
    | map(select(.label == $label))
    | first
    | .p95Milliseconds // 0
  ' "$file"
}

print_row() {
  local file="$1"
  local parallelism="$2"
  local repeat="$3"
  local status="$4"
  local throughput p95 p99 failures matches scenarios accum ncci provider rate

  throughput="$(json_number "$file" '.throughputClaimsPerSecond')"
  p95="$(json_number "$file" '.p95LatencyMilliseconds')"
  p99="$(json_number "$file" '.p99LatencyMilliseconds')"
  failures="$(json_number "$file" '.platformFailures')"
  matches="$(json_number "$file" '.workflowMatches')"
  scenarios="$(json_number "$file" '.workflowScenarios')"
  accum="$(stage_p95 "$file" 'Adjudicate.benefitCalculation.accumulatorRead')"
  ncci="$(stage_p95 "$file" 'Adjudicate.ncci')"
  provider="$(stage_p95 "$file" 'Adjudicate.providerIntegrity')"
  rate="$(stage_p95 "$file" 'Adjudicate.rateResolution')"

  printf '%-5s %-6s %-7s %10.2f %8.0f %8.0f %9.0f %8.0f %8.0f %8.0f %9s %9s\n' \
    "$parallelism" "$repeat" "$status" "$throughput" "$p95" "$p99" \
    "$accum" "$ncci" "$provider" "$rate" "$failures" "${matches}/${scenarios}"
}

run_case() {
  local parallelism="$1"
  local repeat="$2"
  local job_name="mcc-sweep-p${parallelism}-r${repeat}-$(date +%H%M%S)"
  local log_file="$OUTPUT_DIR/${job_name}.log"
  local json_file="$OUTPUT_DIR/${job_name}.json"
  local seed_member_arg=""
  local seed_provider_arg=""
  local servicebus_only_arg=""
  local servicebus_reconciliation_arg=""
  local pend_observation_arg=""
  local status="ok"

  if [[ "$SEED_MEMBERS" != "true" ]]; then
    seed_member_arg='                --no-seed-members \'
  fi
  if [[ "$SEED_PROVIDERS" != "true" ]]; then
    seed_provider_arg='                --no-seed-providers \'
  fi
  if [[ "$SERVICEBUS_ONLY" == "true" ]]; then
    servicebus_only_arg='                --servicebus-only \'
  fi
  if [[ "$SERVICEBUS_RECONCILIATION_ENABLED" != "true" ]]; then
    servicebus_reconciliation_arg='                --no-servicebus-reconciliation \'
  fi
  if [[ "$PEND_OBSERVATION_ENABLED" != "true" ]]; then
    pend_observation_arg='                --no-pend-observation \'
  fi

  log "Running MCC sweep case parallelism=${parallelism}, repeat=${repeat}"
  kubectl delete job -n "$NAMESPACE" "$job_name" --ignore-not-found >/dev/null

  kubectl apply -n "$NAMESPACE" -f - <<EOF >/dev/null
apiVersion: batch/v1
kind: Job
metadata:
  name: ${job_name}
  labels:
    app.kubernetes.io/name: mcc-platform-validator
    cho.cloudhealthoffice.com/sweep: "true"
spec:
  backoffLimit: 0
  template:
    spec:
      restartPolicy: Never
      containers:
        - name: mcc-platform-validator
          image: ${IMAGE}
          imagePullPolicy: IfNotPresent
          command:
            - /bin/sh
            - -c
          args:
            - |
              dotnet mcc-platform-validator.dll \
                --claims "${CLAIMS}" \
                --max-claims "${MAX_CLAIMS}" \
                --tenant "${TENANT}" \
                --parallelism "${parallelism}" \
                --claims-url http://claims-service \
                --benefit-url http://benefit-plan-service \
                --member-url http://member-service \
                --provider-url http://provider-service \
                --servicebus-reconciliation-timeout "${SERVICEBUS_RECONCILIATION_TIMEOUT_SECONDS}" \
                --pend-observation-timeout "${PEND_OBSERVATION_TIMEOUT_SECONDS}" \
                --pend-observation-interval-ms "${PEND_OBSERVATION_INTERVAL_MS}" \
                --progress-every "${PROGRESS_EVERY}" \
${seed_member_arg}
${seed_provider_arg}
${servicebus_only_arg}
${servicebus_reconciliation_arg}
${pend_observation_arg}
                --summary-json /tmp/mcc-summary.json
              status=\$?
              echo "__MCC_SUMMARY_JSON_BEGIN__"
              cat /tmp/mcc-summary.json 2>/dev/null || true
              printf '\\n__MCC_SUMMARY_JSON_END__\\n'
              exit "\$status"
EOF

  if ! kubectl wait -n "$NAMESPACE" --for=condition=complete "job/${job_name}" --timeout="$TIMEOUT" >/dev/null 2>&1; then
    status="failed"
    kubectl wait -n "$NAMESPACE" --for=condition=failed "job/${job_name}" --timeout=60s >/dev/null 2>&1 || true
  fi

  kubectl logs -n "$NAMESPACE" "job/${job_name}" > "$log_file"
  extract_summary_json < "$log_file" > "$json_file"

  if ! jq -e . "$json_file" >/dev/null 2>&1; then
    status="no-json"
    rm -f "$json_file"
    echo "Could not extract valid summary JSON for ${job_name}; see ${log_file}" >&2
    return 0
  fi

  print_row "$json_file" "$parallelism" "$repeat" "$status" | tee -a "$OUTPUT_DIR/results.tsv"
}

require_tool kubectl
require_tool jq

mkdir -p "$OUTPUT_DIR"
: > "$OUTPUT_DIR/results.tsv"

if [[ "$SKIP_BUILD" != "true" ]]; then
  require_tool docker
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

if [[ "$CLEAN_SWEEP_JOBS" == "true" ]]; then
  log "Cleaning previous MCC sweep jobs in namespace ${NAMESPACE}"
  kubectl delete job -n "$NAMESPACE" -l 'cho.cloudhealthoffice.com/sweep=true' --ignore-not-found >/dev/null
fi

if kubectl get deployment -n "$NAMESPACE" claims-service >/dev/null 2>&1; then
  log "Setting claims-service benefit-plan timeout to ${CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS}s"
  kubectl set env deployment/claims-service \
    -n "$NAMESPACE" \
    "Services__BenefitPlanServiceTimeoutSeconds=${CLAIMS_SERVICE_BENEFIT_TIMEOUT_SECONDS}" >/dev/null
  kubectl rollout status deployment/claims-service -n "$NAMESPACE" --timeout=120s >/dev/null
fi

log "Writing sweep artifacts to ${OUTPUT_DIR}"
printf '%-5s %-6s %-7s %10s %8s %8s %9s %8s %8s %8s %9s %9s\n' \
  "P" "Run" "Status" "Claims/s" "P95" "P99" "AccP95" "NCCI95" "Prov95" "Rate95" "Failures" "Workflow"

for parallelism in $PARALLELISM_VALUES; do
  if [[ ! "$parallelism" =~ ^[0-9]+$ ]]; then
    echo "Invalid parallelism value: $parallelism" >&2
    exit 1
  fi

  for repeat in $(seq 1 "$REPEATS"); do
    run_case "$parallelism" "$repeat"
  done
done

log "Averages by parallelism"
jq -s -r '
  sort_by(.run.parallelism)
  | group_by(.run.parallelism)
  | map({
      parallelism: .[0].run.parallelism,
      runs: length,
      throughput: (map(.throughputClaimsPerSecond) | add / length),
      p95: (map(.p95LatencyMilliseconds) | add / length),
      p99: (map(.p99LatencyMilliseconds) | add / length),
      accumulatorP95: (
        map((.adjudicationStepTimings // [])
          | map(select(.label == "Adjudicate.benefitCalculation.accumulatorRead"))
          | first
          | .p95Milliseconds // 0)
        | add / length),
      failures: (map(.platformFailures) | add),
      workflowMatches: (map(.workflowMatches) | add),
      workflowScenarios: (map(.workflowScenarios) | add)
    })
  | sort_by(.parallelism)
  | .[]
  | [
      .parallelism,
      .runs,
      .throughput,
      .p95,
      .p99,
      .accumulatorP95,
      .failures,
      .workflowMatches,
      .workflowScenarios
    ]
  | @tsv
' "$OUTPUT_DIR"/*.json \
  | awk 'BEGIN {
      printf "%-5s %-5s %10s %8s %8s %9s %9s %11s\n", "P", "Runs", "Claims/s", "P95", "P99", "AccP95", "Failures", "Workflow"
    }
    {
      printf "%-5s %-5s %10.2f %8.0f %8.0f %9.0f %9s %11s\n", $1, $2, $3, $4, $5, $6, $7, $8 "/" $9
    }'

log "Sweep complete: ${OUTPUT_DIR}"
