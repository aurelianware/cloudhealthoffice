#!/usr/bin/env bash
# run-mcc.sh — Run a demo-sized Million Claim Challenge on the GKE demo cluster.
#
# Wraps scripts/run-mcc-local-k8s.sh with the GKE settings: the validator image
# from Artifact Registry (built by the deploy-gcp-gke workflow), no local
# build, and a development bearer token for the demo tenant. Results appear in
# the portal's Mass Adjudication console and in the job log.
#
# Required: GAR_REPOSITORY, IMAGE_TAG (same values the deploy used)
# Optional: CLAIMS (default 2000), PARALLELISM (default 10)

set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"

[[ -n "${GAR_REPOSITORY:-}" ]] || { echo "GAR_REPOSITORY is required" >&2; exit 1; }
[[ -n "${IMAGE_TAG:-}" ]] || { echo "IMAGE_TAG is required" >&2; exit 1; }

CHO_BEARER_TOKEN="$(TENANT_ID="${TENANT:-demo}" python3 "$ROOT_DIR/scripts/demo/gcp/cho_demo.py" token)"
export CHO_BEARER_TOKEN

export IMAGE="${GAR_REPOSITORY}/cloudhealthoffice-mcc-platform-validator:${IMAGE_TAG}"
export SKIP_BUILD=true
export KIND_CLUSTER_NAME="${KIND_CLUSTER_NAME:-none}"
export CLAIMS="${CLAIMS:-2000}"
export PARALLELISM="${PARALLELISM:-10}"
export JOB_TIMEOUT="${JOB_TIMEOUT:-30m}"
# Autopilot may add a node before a restarted service is ready.
export ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-600s}"

exec "$ROOT_DIR/scripts/run-mcc-local-k8s.sh"
