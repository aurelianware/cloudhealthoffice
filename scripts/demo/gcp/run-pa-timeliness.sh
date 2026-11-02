#!/usr/bin/env bash
# run-pa-timeliness.sh — Run the PA Timeliness Challenge on the GKE demo cluster.
#
# The CMS-0057-F counterpart to run-mcc.sh. Runs the pa-timeliness-challenge
# image (built by the deploy-gcp-gke workflow) as a Kubernetes Job inside the
# cluster: it imports a synthetic history cohort into authorization-service,
# submits the live cohort through fhir-service PAS $submit, decides it as the
# reviewer, and scores the CMS-0057-F metrics, UM timeliness report and annual
# draft against the answer key. Synthetic data only.
#
# Required: GAR_REPOSITORY, IMAGE_TAG (same values the deploy used)
# Optional: TENANT (default pa-timeliness: a tenant of its own keeps the score
#           clean, since the metrics count every request in the tenant),
#           COUNT (default 10000), SEED (default 57), LIVE_FRACTION (0.05),
#           PARALLELISM (8), MODE (all | history-only | live-only),
#           JOB_TIMEOUT (default 45m), OUT_DIR (default ./pa-timeliness-results)
#
# Results: the job log and run-summary.json/.md under OUT_DIR/<timestamp>/.
# Exit status: 0 every check passed, 1 a check failed, other values a run failure.

set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

[[ -n "${GAR_REPOSITORY:-}" ]] || { echo "GAR_REPOSITORY is required" >&2; exit 2; }
[[ -n "${IMAGE_TAG:-}" ]] || { echo "IMAGE_TAG is required" >&2; exit 2; }

NAMESPACE="${NAMESPACE:-cloudhealthoffice}"
JOB_NAME="${JOB_NAME:-pa-timeliness-challenge}"
TENANT="${TENANT:-pa-timeliness}"
COUNT="${COUNT:-10000}"
SEED="${SEED:-57}"
LIVE_FRACTION="${LIVE_FRACTION:-0.05}"
PARALLELISM="${PARALLELISM:-8}"
MODE="${MODE:-all}"
JOB_TIMEOUT="${JOB_TIMEOUT:-45m}"
AUTHORIZATION_URL="${AUTHORIZATION_URL:-http://authorization-service}"
FHIR_URL="${FHIR_URL:-http://fhir-service}"
OUT_DIR="${OUT_DIR:-./pa-timeliness-results}"

case "$MODE" in
  all) mode_arg="" ;;
  history-only) mode_arg="--history-only" ;;
  live-only) mode_arg="--live-only" ;;
  *) echo "MODE must be all, history-only or live-only" >&2; exit 2 ;;
esac

case "$JOB_TIMEOUT" in
  *m) timeout_seconds=$(( ${JOB_TIMEOUT%m} * 60 )) ;;
  *h) timeout_seconds=$(( ${JOB_TIMEOUT%h} * 3600 )) ;;
  *s) timeout_seconds=${JOB_TIMEOUT%s} ;;
  *) echo "JOB_TIMEOUT must look like 45m, 2h or 900s" >&2; exit 2 ;;
esac

# The deploy workflow tags images with the full commit SHA; expand a short one.
if [[ "$IMAGE_TAG" =~ ^[0-9a-f]{7,39}$ ]]; then
  if full_sha="$(git -C "$ROOT_DIR" rev-parse --verify --quiet "${IMAGE_TAG}^{commit}")"; then
    IMAGE_TAG="$full_sha"
  else
    echo "IMAGE_TAG ${IMAGE_TAG} looks like a short commit SHA, but this checkout doesn't have it." >&2
    echo "Run git pull, or pass the full 40-character SHA (images are tagged with it)." >&2
    exit 2
  fi
fi
IMAGE="${GAR_REPOSITORY}/cloudhealthoffice-pa-timeliness-challenge:${IMAGE_TAG}"

if command -v gcloud >/dev/null 2>&1; then
  if ! gcloud artifacts docker images describe "$IMAGE" >/dev/null 2>&1; then
    echo "Image not found in Artifact Registry: $IMAGE" >&2
    echo "Use the image tag of a 'Deploy demo to GKE' run that built pa-timeliness-challenge (its full commit SHA)." >&2
    exit 2
  fi
fi

# A development user token for the run's tenant, kept in a Secret so it never
# appears in the Job spec. Its tenant must match --tenant.
CHO_BEARER_TOKEN="$(TENANT_ID="$TENANT" python3 "$ROOT_DIR/scripts/demo/gcp/cho_demo.py" token)"
kubectl create secret generic pa-timeliness-token -n "$NAMESPACE" \
  --from-literal=token="$CHO_BEARER_TOKEN" --dry-run=client -o yaml | kubectl apply -f - >/dev/null

echo "==> PA Timeliness Challenge: ${COUNT} requests, seed ${SEED}, tenant ${TENANT}, mode ${MODE}"
kubectl delete job -n "$NAMESPACE" "$JOB_NAME" --ignore-not-found >/dev/null
kubectl apply -n "$NAMESPACE" -f - <<EOF
apiVersion: batch/v1
kind: Job
metadata:
  name: ${JOB_NAME}
  labels:
    app.kubernetes.io/name: pa-timeliness-challenge
spec:
  backoffLimit: 0
  ttlSecondsAfterFinished: 86400
  template:
    metadata:
      labels:
        app.kubernetes.io/name: pa-timeliness-challenge
    spec:
      restartPolicy: Never
      containers:
        - name: pa-timeliness-challenge
          image: ${IMAGE}
          imagePullPolicy: IfNotPresent
          resources:
            requests: { cpu: 250m, memory: 256Mi }
            limits: { memory: 1Gi }
          env:
            - name: CHO_BEARER_TOKEN
              valueFrom:
                secretKeyRef:
                  name: pa-timeliness-token
                  key: token
          args:
            - run
            - --out
            - /tmp/pa-timeliness
            - --auth-url
            - "${AUTHORIZATION_URL}"
            - --fhir-url
            - "${FHIR_URL}"
            - --tenant
            - "${TENANT}"
            - --count
            - "${COUNT}"
            - --seed
            - "${SEED}"
            - --live-fraction
            - "${LIVE_FRACTION}"
            - --parallelism
            - "${PARALLELISM}"
            $(if [[ -n "$mode_arg" ]]; then printf -- '- %s\n' "$mode_arg"; fi)
EOF

deadline=$((SECONDS + timeout_seconds))
job_status=124
while (( SECONDS < deadline )); do
  conditions="$(kubectl get job "$JOB_NAME" -n "$NAMESPACE" \
    -o jsonpath='{range .status.conditions[*]}{.type}={.status}{"\n"}{end}' 2>/dev/null || true)"
  if [[ "$conditions" == *"Complete=True"* ]]; then job_status=0; break; fi
  if [[ "$conditions" == *"Failed=True"* ]]; then job_status=1; break; fi
  sleep 5
done

run_dir="${OUT_DIR}/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$run_dir"
kubectl logs -n "$NAMESPACE" "job/${JOB_NAME}" > "${run_dir}/job.log" 2>&1 || true

# The tool prints run-summary.json between markers; keep it and its Markdown.
awk '/=== PA_TIMELINESS_SUMMARY_JSON_BEGIN ===/{on=1; next} /=== PA_TIMELINESS_SUMMARY_JSON_END ===/{on=0} on' \
  "${run_dir}/job.log" > "${run_dir}/run-summary.json"
if [[ -s "${run_dir}/run-summary.json" ]]; then
  awk '/^# PA Timeliness Challenge run/{on=1} /^Summary: /{on=0} on' "${run_dir}/job.log" > "${run_dir}/run-summary.md"
  cat "${run_dir}/run-summary.md"
else
  rm -f "${run_dir}/run-summary.json"
  tail -n 60 "${run_dir}/job.log"
fi
echo "==> Results in ${run_dir}"

kubectl delete secret pa-timeliness-token -n "$NAMESPACE" --ignore-not-found >/dev/null

if (( job_status == 124 )); then
  echo "Timed out after ${JOB_TIMEOUT} waiting for job/${JOB_NAME}; it may still be running (kubectl logs -f job/${JOB_NAME} -n ${NAMESPACE})." >&2
fi
exit "$job_status"
