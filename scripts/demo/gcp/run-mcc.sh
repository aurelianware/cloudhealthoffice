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

# The validator seeds platform prior-auth rules (POST
# /api/v1/prior-auth-rules/seed-platform), which requires platform:admin.
CHO_BEARER_TOKEN="$(TENANT_ID="${TENANT:-demo}" python3 "$ROOT_DIR/scripts/demo/gcp/cho_demo.py" token --platform-admin)"
export CHO_BEARER_TOKEN

# The deploy workflow tags images with the full commit SHA. Accept a short
# SHA (as the GitHub UI shows it) by expanding it from this checkout.
if [[ "$IMAGE_TAG" =~ ^[0-9a-f]{7,39}$ ]]; then
  if full_sha="$(git -C "$ROOT_DIR" rev-parse --verify --quiet "${IMAGE_TAG}^{commit}")"; then
    echo "==> Using full commit SHA ${full_sha} for image tag ${IMAGE_TAG}"
    IMAGE_TAG="$full_sha"
  else
    echo "IMAGE_TAG ${IMAGE_TAG} looks like a short commit SHA, but this checkout doesn't have it." >&2
    echo "Run git pull, or pass the full 40-character SHA (images are tagged with it)." >&2
    exit 1
  fi
fi

export IMAGE="${GAR_REPOSITORY}/cloudhealthoffice-mcc-platform-validator:${IMAGE_TAG}"

# Fail now rather than leave the job in ImagePullBackOff until JOB_TIMEOUT.
if command -v gcloud >/dev/null 2>&1; then
  if ! gcloud artifacts docker images describe "$IMAGE" >/dev/null 2>&1; then
    echo "Image not found in Artifact Registry: $IMAGE" >&2
    echo "Use the image tag of a successful 'Deploy demo to GKE' run (its full commit SHA)." >&2
    exit 1
  fi
fi
export SKIP_BUILD=true
export KIND_CLUSTER_NAME="${KIND_CLUSTER_NAME:-none}"
export CLAIMS="${CLAIMS:-2000}"
export PARALLELISM="${PARALLELISM:-10}"
export JOB_TIMEOUT="${JOB_TIMEOUT:-30m}"
# Restarted services (and on Autopilot, new nodes) can take a while to be ready.
export ROLLOUT_TIMEOUT="${ROLLOUT_TIMEOUT:-600s}"

exec "$ROOT_DIR/scripts/run-mcc-local-k8s.sh"
