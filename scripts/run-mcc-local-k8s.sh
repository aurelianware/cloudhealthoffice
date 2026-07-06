#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

NAMESPACE="${NAMESPACE:-cloudhealthoffice}"
JOB_NAME="${JOB_NAME:-mcc-validator}"
IMAGE="${IMAGE:-cloudhealthoffice-mcc-platform-validator:local}"
CLAIMS="${CLAIMS:-5000}"
MAX_CLAIMS="${MAX_CLAIMS:-$CLAIMS}"
TENANT="${TENANT:-demo}"
SEED_PROVIDERS="${SEED_PROVIDERS:-true}"
PARALLELISM="${PARALLELISM:-10}"
PROGRESS_EVERY="${PROGRESS_EVERY:-500}"
KIND_CLUSTER_NAME="${KIND_CLUSTER_NAME:-docker-desktop}"
SKIP_BUILD="${SKIP_BUILD:-false}"

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

log "Running ${CLAIMS} claims in namespace ${NAMESPACE}"
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
            - --parallelism
            - "${PARALLELISM}"
            - --claims-url
            - http://claims-service
            - --benefit-url
            - http://benefit-plan-service
            - --provider-url
            - http://provider-service
            $(if [[ "$SEED_PROVIDERS" != "true" ]]; then printf -- '- --no-seed-providers\n'; fi)
            - --progress-every
            - "${PROGRESS_EVERY}"
            - --summary-json
            - /tmp/mcc-summary.json
EOF

kubectl wait -n "$NAMESPACE" --for=condition=complete "job/${JOB_NAME}" --timeout=30m
kubectl logs -n "$NAMESPACE" "job/${JOB_NAME}"
